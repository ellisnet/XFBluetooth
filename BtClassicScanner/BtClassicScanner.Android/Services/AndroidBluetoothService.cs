using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
    public class AndroidBluetoothService : IBluetoothService, IObservable<IBluetoothDevice>, IObservable<IncomingBytes>
    {
        //This idea to automatically approve the pairing request did not work, because the necessary permissions are not available to third-party apps - see notes below.

        //public static Action<Intent> PairingRequestAction { get; set; } = (intent) =>
        //{
        //    if (intent != null)
        //    {
        //        var pairingDevice = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
        //        int pin = intent.GetIntExtra(BluetoothDevice.ExtraPairingKey, 0);
        //        byte[] pinBytes = Encoding.UTF8.GetBytes(pin.ToString());
        //        pairingDevice.SetPin(pinBytes);
        
        //        //The following line fails on newer versions of Android (8 or greater?) - and says that the app needs "android.permission.BLUETOOTH_PRIVILEGED" - but this
        //        // permission cannot be successfully added to the app manifest; because, according to the Google docs, "This is not available to third party applications.":
        //        // https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_PRIVILEGED
        //        pairingDevice.SetPairingConfirmation(true);
        //    }
        //};

        #region Private static fields

        private static Context _context;
        private static BluetoothAdapter _adapter;
        private static bool _isDiscovering;
        private static bool _isDiscoveryRunning;
        private static bool _isDiscoveryStopping;
        private static bool _isDiscoveryCanceling;
        private static readonly object _discoveryLocker = new object();
        private static readonly object _deviceDiscoveredLocker = new object();
        private static readonly UUID _serialPortUuid = UUID.FromString("00001101-0000-1000-8000-00805f9b34fb"); //Standard serial port uuid

        #endregion

        #region Private instance fields

        private readonly List<ObserverSubscription<IBluetoothDevice>> _discoverySubscriptions 
            = new List<ObserverSubscription<IBluetoothDevice>>();

        private readonly List<ObserverSubscription<IncomingBytes>> _incomingSubscriptions
            = new List<ObserverSubscription<IncomingBytes>>();

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
        private CancellationTokenSource _readBytesCancellation;
        private BluetoothDeviceInfo _connectedDevice;
        private InputStreamReader _connectedDeviceInputStream;

        #endregion

        #region Private methods

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

        private async Task<bool> ConnectDeviceToSocket(BluetoothDeviceInfo deviceInfo)
        {
            if (deviceInfo == null) { throw new ArgumentNullException(nameof(deviceInfo)); }
            bool result = false;

            var tcs = new TaskCompletionSource<BluetoothSocket>();

            var socketTimeout = new Timer(30000) {AutoReset = false};
            socketTimeout.Elapsed += (sender, args) => { tcs?.TrySetResult(null);};

            new Task(() =>
            {
                try
                {
                    tcs?.TrySetResult(deviceInfo.NativeDevice.CreateRfcommSocketToServiceRecord(_serialPortUuid));
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.ToString());
                    Debugger.Break();
                    tcs?.TrySetResult(null);
                }
            }).Start();

            socketTimeout.Start();

            var deviceSocket = await tcs.Task;

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
                            deviceSocket.Connect(); //This line causes the user to be prompted to pair the device, if it wasn't already paired.

                            //The following line will not be hit until the user taps "PAIR" (if they needed to pair the device) - so, if we
                            //  successfully get past Connect() - the user *did* choose to pair the device; or it was already paired.
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
                        catch (IOException ioEx)
                        {
                            throw new Exception($"Unable to connect to the Bluetooth device, possibly because the user did not tap the 'PAIR' option - {ioEx.Message}", ioEx);
                        }
                        catch (Exception e)
                        {
                            Debug.WriteLine(e.ToString());
                            Debugger.Break();
                            throw;
                        }

                        if (connectedSocket != null)
                        {
                            deviceInfo.IsConnected = true;
                            deviceInfo.IsPaired = true; //TODO: Confirm that it is paired at this point
                            deviceInfo.ConnectedSocket = connectedSocket;
                            _connectedDevice = deviceInfo;

                            _readBytesCancellation = new CancellationTokenSource();
                            _connectedDeviceInputStream = new InputStreamReader(connectedSocket.InputStream);

                            //Fire off a background task to continuously read bytes
                            new Task(async () =>
                            {
                                while (!_readBytesCancellation.IsCancellationRequested)
                                {
                                    try
                                    {
                                        var readBuffer = new char[1024];
                                        int numBytes = await _connectedDeviceInputStream.ReadAsync(readBuffer);
                                        if (numBytes > 0)
                                        {
                                            _incomingSubscriptions.NotifyAllNext(new IncomingBytes(readBuffer.Take(numBytes).Select(s => (byte)s).ToArray()));
                                        }
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

                                        _incomingSubscriptions.NotifyAllError(e);
                                    }
                                }
                            }).Start();

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
            }

            return result;
        }

        private async Task<bool> ConnectPairWithDevice(IBluetoothDevice device)
        {
            var deviceInfo = device as BluetoothDeviceInfo;
            if (deviceInfo == null) { throw new ArgumentNullException(nameof(device)); }

            bool result = deviceInfo.IsPaired && deviceInfo.IsConnected;

            if (!result)
            {
                await _pairedDeviceLocker.WaitAsync();

                try
                {
                    CheckAdapter();

                    //SimpleBroadcastReceiver pairReceiver = null;

                    //This idea to automatically approve the pairing request did not work, because the necessary permissions 
                    // are not available to third-party apps - see notes at the top of the class.
                    
                    //if (waitForPairing && PairingRequestAction != null)
                    //{
                    //    pairReceiver = new SimpleBroadcastReceiver((context, intent) 
                    //        => PairingRequestAction.Invoke(intent));
                    //    pairReceiver.Register(_context, new IntentFilter(BluetoothDevice.ActionPairingRequest));
                    //}

                    result = await ConnectDeviceToSocket(deviceInfo);

                    //pairReceiver?.Unregister(_context);
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

            return result;
        }

        #endregion

        #region Implement IBluetoothService

        // ReSharper disable once InconsistentlySynchronizedField
        public bool IsDiscovering => _isDiscovering && _isDiscoveryRunning;

        public IObservable<IBluetoothDevice> GetDiscoveryObservable() => this;

        public IObservable<IncomingBytes> GetIncomingObservable() => this;

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
                            _discoverySubscriptions.NotifyAllNext(new BluetoothDeviceInfo(device));
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

                _discoverySubscriptions.NotifyAllCompleted();

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

        public Task<bool> PairWithDevice(IBluetoothDevice device)
            => ConnectPairWithDevice(device);

        public Task<bool> ConnectWithPairedDevice(IBluetoothDevice device) 
            => ConnectPairWithDevice(device);

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
            var subscription = new ObserverSubscription<IBluetoothDevice>(subscriber, id => _discoverySubscriptions.RemoveSubscription(id));
            _discoverySubscriptions.AddSubscription(subscription);
            return subscription;
        }

        #endregion

        #region Implement IObservable<IncomingBytes>

        public IDisposable Subscribe(IObserver<IncomingBytes> subscriber)
        {
            if (subscriber == null) { throw new ArgumentNullException(nameof(subscriber)); }
            var subscription = new ObserverSubscription<IncomingBytes>(subscriber, id => _incomingSubscriptions.RemoveSubscription(id));
            _incomingSubscriptions.AddSubscription(subscription);
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

            foreach (ObserverSubscription<IBluetoothDevice> subscription in _discoverySubscriptions.GetActiveSubscriptions())
            {
                subscription.NotifyCompleted();
            }
            _discoverySubscriptions.ClearSubscriptions();

            foreach (ObserverSubscription<IncomingBytes> subscription in _incomingSubscriptions.GetActiveSubscriptions())
            {
                subscription.NotifyCompleted();
            }
            _incomingSubscriptions.ClearSubscriptions();

            _adapter = null;
            _context = null;
        }

        #endregion
    }
}
