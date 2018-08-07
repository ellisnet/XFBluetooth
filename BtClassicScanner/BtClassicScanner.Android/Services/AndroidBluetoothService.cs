using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using BtClassicScanner.Models;
using BtClassicScanner.Services;
using CodeBrix.Prism.Android.Services;
using CodeBrix.Prism.Helpers;
using Java.IO;
using Java.Util;
using Plugin.Permissions;
using Plugin.Permissions.Abstractions;
using Debug = System.Diagnostics.Debug;
using Timer = System.Timers.Timer;

//Created based on documentation available here:
// https://developer.android.com/guide/topics/connectivity/bluetooth.html
// https://developer.xamarin.com/api/member/Android.Bluetooth.BluetoothAdapter.StartDiscovery/
// https://developer.android.com/reference/android/bluetooth/BluetoothDevice#createRfcommSocketToServiceRecord%28java.util.UUID%29
// https://acaliaro.wordpress.com/2017/02/07/connect-a-barcode-reader-to-a-xamarin-forms-app-via-bluetooth/

namespace BtClassicScanner.Droid.Services
{
    public class AndroidBluetoothService : IBluetoothService, IObservable<IBluetoothDevice>
    {
        private static Context _context;
        private static BluetoothAdapter _adapter;
        private static bool _isDiscovering;
        private static bool _isDiscoveryRunning;
        private static bool _isDiscoveryStopping;
        private static bool _isDiscoveryCanceling;
        private static readonly object _discoveryLocker = new object();
        private static readonly object _deviceDiscoveredLocker = new object();
        //private static readonly UUID _expectedUuid = UUID.FromString("00001812-0000-1000-8000-00805f9b34fb");  //Read this UUID from the Teemi TMSL-55 2D barcode scanner
        private static readonly UUID _serialPortUuid = UUID.FromString("00001101-0000-1000-8000-00805f9b34fb"); //Standard serial port uuid
        private static string _serviceName => _context.PackageName;

        private readonly object _discoverySubscriberLocker = new object();
        private readonly List<ObserverSubscription<IBluetoothDevice>> _discoverySubscriptions 
            = new List<ObserverSubscription<IBluetoothDevice>>();

        private SimpleBroadcastReceiver _discoveryReceiver;
        private SimpleBroadcastReceiver _discoveryStartedReceiver;
        private SimpleBroadcastReceiver _discoveryFinishedReceiver;
        private SimpleBroadcastReceiver _uuidReceiver;
        private SimpleBroadcastReceiver _deviceConnectedReceiver;
        private bool _isDiscoveryReceiverRegistered;
        private bool _deviceConnectBroadcastReceived;
        private Timer _discoveryTimeoutTimer;
        private TaskCompletionSource<bool> _connectDeviceTcs;
        private readonly SemaphoreSlim _pairedDeviceLocker = new SemaphoreSlim(1, 1);
        private readonly List<UUID> _serviceUuids = new List<UUID>();
        private readonly object _serviceUuidLocker = new object();
        private Timer _uuidSearchTimer;
        private Timer _uuidSearchTimeoutTimer;
        private UUID _foundUuid;
        private Action<byte[]> _incomingBytesAction;
        private CancellationTokenSource _readBytesCancellation;
        private readonly object _readBytesLocker = new object();
        private BluetoothDeviceInfo _connectedDevice;
        private InputStreamReader _connectedDeviceInputStream;

        #region Private methods

        private void RemoveDiscoverySubscriber(Guid subscriptionId)
        {
            lock (_discoverySubscriberLocker)
            {
                ObserverSubscription<IBluetoothDevice>[] subscribersToRemove =
                    _discoverySubscriptions.Where(w => w.SubscriptionId == subscriptionId).ToArray();
                foreach (ObserverSubscription<IBluetoothDevice> subscription in subscribersToRemove)
                {
                    _discoverySubscriptions.Remove(subscription);
                }
            }
        }

        private void CheckAdapter()
        {
            _adapter = _adapter ?? BluetoothAdapter.DefaultAdapter;

            if (_adapter == null)
            {
                throw new InvalidOperationException("A Bluetooth adapter could not be found on this device.");
            }
        }

        private async Task<IList<UUID>> GetDeviceServiceUuids(BluetoothDeviceInfo deviceInfo)
        {
            if (deviceInfo == null) { throw new ArgumentNullException(nameof(deviceInfo)); }
            var searchTcs = new TaskCompletionSource<bool>();
            _serviceUuids.Clear();

            _uuidSearchTimer = new Timer(6000) {AutoReset = false};
            _uuidSearchTimer.Elapsed += (sender, args) => { searchTcs?.TrySetResult(true); };

            _uuidSearchTimeoutTimer = new Timer(20000) { AutoReset = false }; //Timeout after 10 seconds
            _uuidSearchTimeoutTimer.Elapsed += (sender, args) => { searchTcs?.TrySetResult(false); };

            _uuidReceiver = new SimpleBroadcastReceiver((context, intent) =>
            {
                lock (_serviceUuidLocker)
                {
                    bool parcelUuidFound = false;
                    IParcelable[] parcelUuids = intent.GetParcelableArrayExtra(BluetoothDevice.ExtraUuid);
                    if (parcelUuids != null)
                    {
                        foreach (IParcelable parcelUuid in parcelUuids)
                        {
                            parcelUuidFound = true;
                            var uuid = UUID.FromString(parcelUuid.ToString());
                            if (!_serviceUuids.Any(a => a.Equals(uuid)))
                            {
                                _serviceUuids.Add(uuid);
                            }
                        }
                    }

                    if (parcelUuidFound)
                    {
                        _uuidSearchTimer.Stop(); //does nothing if the timer is not already started
                        _uuidSearchTimer.Start(); //Start the timer to stop searching, if another UUID isn't found first
                    }
                }
            });

            _deviceConnectBroadcastReceived = false;
            _deviceConnectedReceiver = new SimpleBroadcastReceiver((context, intent) =>
                {
                    _deviceConnectBroadcastReceived = true;
                    _uuidSearchTimer.Stop();
                    _uuidSearchTimer.Start();
                });

            _context.RegisterReceiver(_uuidReceiver, new IntentFilter(BluetoothDevice.ActionUuid));
            _context.RegisterReceiver(_deviceConnectedReceiver, new IntentFilter(BluetoothDevice.ActionAclConnected));
            _adapter.CancelDiscovery();

            deviceInfo.NativeDevice.FetchUuidsWithSdp();
            _uuidSearchTimeoutTimer.Start();

            await searchTcs.Task;

            _uuidSearchTimer.Stop();
            _uuidSearchTimeoutTimer.Stop();
            _context.UnregisterReceiver(_uuidReceiver);
            _context.UnregisterReceiver(_deviceConnectedReceiver);

            return _serviceUuids.ToArray();
        }

        //TODO: Do I need the server socket at all?
        private async Task<bool> ConnectDeviceToSocket(BluetoothDeviceInfo deviceInfo) //, BluetoothSocket serverSocket)
        {
            if (deviceInfo == null) { throw new ArgumentNullException(nameof(deviceInfo)); }
            //if (serverSocket == null) { throw new ArgumentNullException(nameof(serverSocket)); }
            bool result = false;

            BluetoothSocket deviceSocket;
            var tcs = new TaskCompletionSource<BluetoothSocket>();

            var socketTimeout = new Timer(30000) {AutoReset = false};
            socketTimeout.Elapsed += (sender, args) => { tcs?.TrySetResult(null);};

            new Task(async () =>
            {
                try
                {
                    //_foundUuid = null;
                    //ParcelUuid[] guids = deviceInfo.NativeDevice.GetUuids();
                    //if (guids == null || guids.Length == 0)
                    //{
                    //    IList<UUID> uuids = await GetDeviceServiceUuids(deviceInfo);  //Note that this may or may not return any UUIDs - kind of hit or miss; so also checking to see if a connect happened
                    //    if (uuids.Any(a => a.Equals(_serialPortUuid)) || _deviceConnectBroadcastReceived)
                    //    {
                    //        guids = deviceInfo.NativeDevice.GetUuids();
                    //        _foundUuid = guids.FirstOrDefault(f => f.Uuid.Equals(_serialPortUuid))?.Uuid;
                    //    }
                    //}
                    //else
                    //{
                    //    _foundUuid = guids.FirstOrDefault(f => f.Uuid.Equals(_serialPortUuid))?.Uuid;
                    //}

                    //if (_foundUuid == null)
                    //{
                    //    throw new InvalidOperationException($"The discovered device does not appear to support the specified service UUID: {_serialPortUuid}");
                    //}

                    //if ((!uuids.Contains(_scannerUuid)) && (!_deviceConnectBroadcastReceived))
                    //{
                    //    throw new InvalidOperationException($"The discovered device does not appear to support the specified service UUID: {_scannerUuid}");
                    //}
                    //tcs?.TrySetResult(deviceInfo.NativeDevice.CreateRfcommSocketToServiceRecord(_foundUuid));
                    //tcs?.TrySetResult(deviceInfo.NativeDevice.CreateInsecureRfcommSocketToServiceRecord(_foundUuid));
                    //tcs?.TrySetResult(deviceInfo.NativeDevice.CreateRfcommSocketToServiceRecord(_expectedUuid));
                    tcs?.TrySetResult(deviceInfo.NativeDevice.CreateRfcommSocketToServiceRecord(_serialPortUuid));
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.ToString());
                    Debugger.Break();
                    throw;
                }
            }).Start();

            socketTimeout.Start();

            deviceSocket = await tcs.Task;

            socketTimeout.Stop();

            if (deviceSocket != null)
            {
                //Cancel discovery on adapter - just in case
                _adapter.CancelDiscovery();

                _connectDeviceTcs = new TaskCompletionSource<bool>();

                new Task(() =>
                {
                    try
                    {
                        BluetoothSocket connectedSocket = null;
                        try
                        {
                            deviceSocket.Connect();
                            connectedSocket = deviceSocket;
                        }
                        //catch (Java.IO.IOException)
                        //{
                        //    try
                        //    {
                        //        //Attempting to use fallback socket as described here:
                        //        // https://stackoverflow.com/questions/18657427/ioexception-read-failed-socket-might-closed-bluetooth-on-android-4-3
                        //        BluetoothDevice device = deviceInfo.NativeDevice;
                        //        var javaClass = Java.Lang.Class.ForName("android.bluetooth.BluetoothDevice");
                        //        var javaMethod = javaClass.GetMethod("createRfcommSocket", new[] { Java.Lang.Integer.Type });
                        //        BluetoothSocket fallbackSocket = (BluetoothSocket)javaMethod.Invoke(device, new Java.Lang.Object[] { 2 }); //{ 1 }
                        //        fallbackSocket.Connect();
                        //        connectedSocket = fallbackSocket;
                        //    }
                        //    catch (Exception)
                        //    {
                        //        throw;
                        //    }
                        //}
                        catch (Exception)
                        {
                            throw;
                        }

                        if (connectedSocket != null)
                        {
                            deviceInfo.IsConnected = true;
                            deviceInfo.IsPaired = true; //TODO: Confirm that it is paired at this point
                            deviceInfo.ConnectedSocket = connectedSocket;
                            _connectedDevice = deviceInfo;

                            if (_incomingBytesAction != null)
                            {
                                _readBytesCancellation = new CancellationTokenSource();
                                _connectedDeviceInputStream = new InputStreamReader(connectedSocket.InputStream);

                                //Fire off a background task to continuously read bytes
                                new Task(async () =>
                                {
                                    while (!_readBytesCancellation.IsCancellationRequested)
                                    {
                                        try
                                        {
                                            //lock (_readBytesLocker)
                                            //{
                                            var readBuffer = new char[1024];
                                            int numBytes = await _connectedDeviceInputStream.ReadAsync(readBuffer);
                                            if (numBytes > 0)
                                            {
                                                _incomingBytesAction?.Invoke(readBuffer.Take(numBytes).Select(s => (byte)s).ToArray());
                                            }
                                            //}
                                        }
                                        catch (Exception e)
                                        {
                                            Debug.WriteLine(e.ToString());
                                            Debugger.Break();
                                            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                                            if (_readBytesCancellation?.Token != null &&
                                                _readBytesCancellation.Token.CanBeCanceled)
                                            {
                                                _readBytesCancellation.Cancel();
                                            }

                                            //TODO: Probably should have an ErrorAction that gets invoked here
                                        }
                                    }
                                }).Start();
                            }

                            _connectDeviceTcs?.TrySetResult(true);
                        }
                        else
                        {
                            _connectDeviceTcs?.TrySetResult(false);
                        }
                    }
                    catch (Exception e)
                    {
                        try
                        {
                            deviceSocket.Close();
                        }
                        catch (Exception)
                        {
                            //Nothing to do here - couldn't close the socket
                        }
                        Debug.WriteLine(e.ToString());
                        Debugger.Break();
                        _connectDeviceTcs?.TrySetResult(false);
                    }
                }).Start();

                result = await _connectDeviceTcs.Task;

                //try
                //{

                //        //var test = deviceInfo.NativeDevice.FetchUuidsWithSdp();  //This might work to get the UUIDS/service id.  Need to figure out how to receive the inbound intent - BroadcastReceiver?
                //    ////var uuids = deviceInfo.NativeDevice.GetUuids(); //Not working, and probably won't work

                //    //await connectTcs.Task;

                //    await Task.Delay(3000);
                //    bool isConnected = deviceSocket.IsConnected;

                //    await deviceSocket.ConnectAsync();  //Currently failing - wrong service id?
                //    if (deviceSocket.IsConnected)
                //    {
                //        deviceInfo.IsConnected = true;
                //        deviceInfo.IsPaired = true; //TODO: Confirm that it is paired at this point
                //        deviceInfo.ConnectedSocket = deviceSocket;
                //        result = true;
                //    }
                //}
                //catch (Exception e)
                //{
                //    Debug.WriteLine(e.ToString());
                //    Debugger.Break();

                //    try
                //    {
                //        deviceSocket.Close();
                //    }
                //    catch (Exception)
                //    {
                //        //Nothing to do here - couldn't close the socket
                //    }

                //    result = false;
                //}
            }





            //}

            return result;
        }

        #endregion

        #region Implement IBluetoothService

        // ReSharper disable once InconsistentlySynchronizedField
        public bool IsDiscovering => _isDiscovering && _isDiscoveryRunning;

        public IObservable<IBluetoothDevice> GetDiscoveryObservable() => this;
 
        public async Task<bool> StartDeviceDiscovery(int? timeoutSeconds = null)
        {
            bool startDiscovery = false;
            lock (_discoveryLocker)
            {
                startDiscovery = (!_isDiscovering) && (!_isDiscoveryStopping);
                if (startDiscovery) { _isDiscovering = true; }
            }

            if (startDiscovery)
            {
                PermissionStatus locationPermission = await CrossPermissions.Current.CheckPermissionStatusAsync(Permission.Location);
                if (locationPermission != PermissionStatus.Granted)
                {
                    throw new InvalidOperationException("Cannot scan for devices without the necessary permissions.");
                }

                CheckAdapter();

                _discoveryReceiver = new SimpleBroadcastReceiver((context, intent) =>
                {
                    lock (_deviceDiscoveredLocker)
                    {
                        if (intent.Action.Equals(BluetoothDevice.ActionFound))
                        {
                            var device = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
                            var deviceInfo = new BluetoothDeviceInfo(device);
                            lock (_discoverySubscriberLocker)
                            {
                                ObserverSubscription<IBluetoothDevice>[] subscriptionsToNotify = _discoverySubscriptions.ToArray();
                                foreach (ObserverSubscription<IBluetoothDevice> subscription in subscriptionsToNotify)
                                {
                                    subscription.NotifyNext(deviceInfo);
                                }
                            }
                        }
                    }
                });

                _discoveryStartedReceiver = new SimpleBroadcastReceiver((context, intent) =>
                {
                    Debug.WriteLine("Bluetooth discovery has started.");
                    _isDiscoveryRunning = true;
                    _isDiscoveryCanceling = false;
                });

                _discoveryFinishedReceiver = new SimpleBroadcastReceiver(async (context, intent) =>
                {
                    Debug.WriteLine("Bluetooth discovery has finished (or was canceled).");
                    if (_isDiscoveryRunning && (!_isDiscoveryCanceling))
                    {
                        _isDiscoveryCanceling = true;
                        await StopDeviceDiscovery();
                        _isDiscoveryCanceling = false;
                    }                    
                    _isDiscoveryRunning = false;
                });

                _context.RegisterReceiver(_discoveryReceiver, new IntentFilter(BluetoothDevice.ActionFound));
                _context.RegisterReceiver(_discoveryStartedReceiver, new IntentFilter(BluetoothAdapter.ActionDiscoveryStarted));
                _context.RegisterReceiver(_discoveryFinishedReceiver, new IntentFilter(BluetoothAdapter.ActionDiscoveryFinished));
                _isDiscoveryReceiverRegistered = true;

                //Per Xamarin documentation, you always want to call "CancelDiscovery" right before starting discovery
                _adapter.CancelDiscovery();
                await Task.Delay(200);

                _discoveryTimeoutTimer?.Dispose();
                _discoveryTimeoutTimer = null;

                if (timeoutSeconds.GetValueOrDefault(0) > 0)
                {
                    // ReSharper disable once PossibleInvalidOperationException
                    _discoveryTimeoutTimer = new Timer(timeoutSeconds.Value * 1000) { AutoReset = false };
                    _discoveryTimeoutTimer.Elapsed += async (sender, args) =>
                    {
                        Debug.WriteLine("Bluetooth discovery timeout exceeded.");
                        await StopDeviceDiscovery();
                    };
                    _discoveryTimeoutTimer.Start();
                }

                lock (_discoveryLocker)
                {
                    _isDiscovering = _adapter.StartDiscovery();
                }

                if (!_isDiscovering)
                {
                    Debugger.Break(); //Why not discovering?
                }

                if (_isDiscovering)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(200);
                        lock (_discoveryLocker)
                        {
                            _isDiscovering = _adapter.IsDiscovering;
                        }
                        if (_isDiscovering) { break;}
                    }
                }
            }

            // ReSharper disable InconsistentlySynchronizedField
            return _isDiscovering && (!_isDiscoveryStopping);
            // ReSharper restore InconsistentlySynchronizedField
        }

        public async Task<bool> StopDeviceDiscovery()
        {
            bool stopDiscovery = false;
            bool result = false;
            lock (_discoveryLocker)
            {
                stopDiscovery = _isDiscovering && (!_isDiscoveryStopping);
                if (stopDiscovery) { _isDiscoveryStopping = true;}
            }

            if (stopDiscovery)
            {
                _discoveryTimeoutTimer?.Stop();

                CheckAdapter();

                lock (_discoverySubscriberLocker)
                {
                    ObserverSubscription<IBluetoothDevice>[] subscriptionsToNotify = _discoverySubscriptions.ToArray();
                    foreach (ObserverSubscription<IBluetoothDevice> subscription in subscriptionsToNotify)
                    {
                        subscription.NotifyCompleted();
                    }
                }

                if (_adapter.IsEnabled && _adapter.IsDiscovering)
                {
                    _adapter.CancelDiscovery();
                }

                if (_discoveryReceiver != null && _isDiscoveryReceiverRegistered)
                {
                    _context.UnregisterReceiver(_discoveryReceiver);
                    _context.UnregisterReceiver(_discoveryStartedReceiver);
                    _context.UnregisterReceiver(_discoveryFinishedReceiver);
                    _isDiscoveryReceiverRegistered = false;
                    _discoveryReceiver = null;
                    _discoveryStartedReceiver = null;
                    _discoveryFinishedReceiver = null;
                }

                result = _adapter == null;
                if (!result)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(200);
                        result = !_adapter.IsDiscovering;
                        if (result)
                        {
                            lock (_discoveryLocker)
                            {
                                _isDiscoveryStopping = false;
                                _isDiscovering = false;
                            }
                            break;
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(200);
                    result = !_adapter.IsDiscovering;
                    if (result) { break; }
                }
            }

            return result;
        }

        public async Task<bool> PairWithDevice(IBluetoothDevice device, Action<byte[]> incomingAction)
        {
            var deviceInfo = device as BluetoothDeviceInfo;
            if (deviceInfo == null) { throw new ArgumentNullException(nameof(device));}

            _incomingBytesAction = incomingAction;

            bool result = deviceInfo.IsPaired && deviceInfo.IsConnected;

            if (!result)
            {
                await _pairedDeviceLocker.WaitAsync();

                try
                {
                    CheckAdapter();

                    result = await ConnectDeviceToSocket(deviceInfo);

                    //BluetoothServerSocket tempSocket;
                    //BluetoothSocket socket;
                    //var tcs = new TaskCompletionSource<BluetoothServerSocket>();

                    ////TODO: Need to kick off a timer to time out the task

                    //await Task.Run(() =>
                    //{
                    //    try
                    //    {
                    //        tcs.SetResult(_adapter.ListenUsingRfcommWithServiceRecord(_serviceName, _serviceUuid));
                    //    }
                    //    catch (Exception e)
                    //    {
                    //        Debug.WriteLine(e.ToString());
                    //        Debugger.Break();
                    //        throw;
                    //    }
                    //});

                    //tempSocket = await tcs.Task;

                    //try
                    //{
                    //    socket = await tempSocket.AcceptAsync(2000); //Can't find documentation about what the timeout is - assuming milliseconds?
                    //    result = await ConnectDeviceToSocket(deviceInfo, socket);
                    //    tempSocket.Close();
                    //}
                    //catch (Exception e)
                    //{
                    //    Debug.WriteLine(e.ToString());
                    //    Debugger.Break();
                    //    throw;
                    //}                    
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Problem while pairing or connecting device:\n{e}");
                    throw;
                }
                finally
                {
                    _pairedDeviceLocker.Release();
                }
            }
            //else
            //{
            //    Debug.WriteLine($"Device '{deviceInfo.DeviceName}' is already paired.");
            //}

            return result;
        }

        //At this time, connecting to a previously paired device is the same as pairing with it for the first time
        public Task<bool> ConnectWithPairedDevice(IBluetoothDevice device, Action<byte[]> incomingAction) 
            => PairWithDevice(device, incomingAction);

        public async Task<IList<IBluetoothDevice>> GetPairedDevices()
        {
            var result = new List<IBluetoothDevice>();

            await _pairedDeviceLocker.WaitAsync();

            try
            {
                CheckAdapter();

                BluetoothDevice[] pairedDevices = _adapter.BondedDevices?.ToArray() ?? new BluetoothDevice[] { };
                foreach (BluetoothDevice device in pairedDevices)
                {
                    result.Add(new BluetoothDeviceInfo(device) { IsPaired = true });
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Problem while getting paired devices:\n{e}");
                throw;
            }
            finally
            {
                _pairedDeviceLocker.Release();
            }

            return result;
        }

        public async Task DisconnectDevice()
        {
            await _pairedDeviceLocker.WaitAsync();
            try
            {
                CheckAdapter();

                if (_connectedDevice != null && _connectedDevice.IsConnected)
                {
                    if (_readBytesCancellation?.Token != null && _readBytesCancellation.Token.CanBeCanceled)
                    {
                        _readBytesCancellation.Cancel();
                    }

                    _connectedDeviceInputStream?.Close();
                    _connectedDeviceInputStream?.Dispose();
                    _connectedDeviceInputStream = null;
                }
                _connectedDevice?.Dispose();
                _connectedDevice = null;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Problem while disconnecting from the active device:\n{e}");
                throw;
            }
            finally
            {
                _pairedDeviceLocker.Release();
            }
        }

        #endregion

        #region Implement IObservable<IBluetoothDevice>

        public IDisposable Subscribe(IObserver<IBluetoothDevice> subscriber)
        {
            if (subscriber == null) { throw new ArgumentNullException(nameof(subscriber)); }
            var subscription = new ObserverSubscription<IBluetoothDevice>(subscriber, RemoveDiscoverySubscriber);
            lock (_discoverySubscriberLocker)
            {
                _discoverySubscriptions.Add(subscription);
            }

            return subscription;
        }

        #endregion

        public AndroidBluetoothService(IContextService contextService)
        {
            _context = _context ?? contextService?.Context ?? throw new ArgumentNullException(nameof(contextService));
        }

        #region Implement IDisposable

        public void Dispose()
        {
            if (_discoveryReceiver != null)
            {
                if (_isDiscoveryReceiverRegistered)
                {
                    _context.UnregisterReceiver(_discoveryReceiver);
                    _context.UnregisterReceiver(_discoveryStartedReceiver);
                    _context.UnregisterReceiver(_discoveryFinishedReceiver);
                    _isDiscoveryReceiverRegistered = false;
                }

                _discoveryReceiver = null;
                _discoveryStartedReceiver = null;
                _discoveryFinishedReceiver = null;
            }

            if (_connectDeviceTcs != null)
            {
                if (!(_connectDeviceTcs.Task.IsCanceled || _connectDeviceTcs.Task.IsCompleted))
                {
                    _connectDeviceTcs.TrySetCanceled();
                }
                _connectDeviceTcs = null;
            }

            _discoveryTimeoutTimer?.Stop();
            _discoveryTimeoutTimer?.Dispose();

            _adapter = null;
            _context = null;
        }

        #endregion
    }
}
