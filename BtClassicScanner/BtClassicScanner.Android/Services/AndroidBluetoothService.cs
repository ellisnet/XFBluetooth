using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static readonly UUID _scannerUuid = UUID.FromString("00001812-0000-1000-8000-00805f9b34fb");  //Read this UUID from the Teemi TMSL-55 2D barcode scanner
        private static string _serviceName => _context.PackageName;

        private readonly object _discoverySubscriberLocker = new object();
        private readonly List<ObserverSubscription<IBluetoothDevice>> _discoverySubscriptions 
            = new List<ObserverSubscription<IBluetoothDevice>>();

        private SimpleBroadcastReceiver _discoveryReceiver;
        private SimpleBroadcastReceiver _discoveryStartedReceiver;
        private SimpleBroadcastReceiver _discoveryFinishedReceiver;
        private SimpleBroadcastReceiver _uuidReceiver;
        private bool _isDiscoveryReceiverRegistered;
        private Timer _discoveryTimeoutTimer;
        private TaskCompletionSource<bool> _pairedDeviceTcs;
        private readonly SemaphoreSlim _pairedDeviceLocker = new SemaphoreSlim(1, 1);
        private readonly List<UUID> _serviceUuids = new List<UUID>();
        private readonly object _serviceUuidLocker = new object();
        private Timer _uuidSearchTimer;
        private Timer _uuidSearchTimeoutTimer;

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

            _uuidSearchTimer = new Timer(2000) {AutoReset = false};
            _uuidSearchTimer.Elapsed += (sender, args) => { searchTcs?.TrySetResult(true); };

            _uuidSearchTimeoutTimer = new Timer(10000) { AutoReset = false }; //Timeout after 10 seconds
            _uuidSearchTimeoutTimer.Elapsed += (sender, args) => { searchTcs?.TrySetResult(false); };

            _uuidReceiver = new SimpleBroadcastReceiver((context, intent) =>
            {
                lock (_serviceUuidLocker)
                {
                    IParcelable[] parcelUuids = intent.GetParcelableArrayExtra(BluetoothDevice.ExtraUuid);
                    if (parcelUuids != null)
                    {
                        foreach (IParcelable parcelUuid in parcelUuids)
                        {
                            var uuid = UUID.FromString(parcelUuid.ToString());
                            if (!_serviceUuids.Any(a => a.Equals(uuid)))
                            {
                                _serviceUuids.Add(uuid);
                            }
                        }
                    }

                    _uuidSearchTimer.Stop(); //does nothing if the timer is not already started
                    _uuidSearchTimer.Start(); //Start the timer to stop searching, if another UUID isn't found first
                }
            });

            _context.RegisterReceiver(_uuidReceiver, new IntentFilter(BluetoothDevice.ActionUuid));
            _adapter.CancelDiscovery();

            deviceInfo.NativeDevice.FetchUuidsWithSdp();
            _uuidSearchTimeoutTimer.Start();

            await searchTcs.Task;

            _uuidSearchTimer.Stop();
            _uuidSearchTimeoutTimer.Stop();
            _context.UnregisterReceiver(_uuidReceiver);

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

            var socketTimeout = new Timer(10000) {AutoReset = false};
            socketTimeout.Elapsed += (sender, args) => { tcs?.TrySetResult(null);};

            new Task(async () =>
            {
                try
                {
                    IList<UUID> uuids = await GetDeviceServiceUuids(deviceInfo);
                    if (!uuids.Contains(_scannerUuid))
                    {
                        throw new InvalidOperationException($"The discovered device does not appear to support the specified service UUID: {_scannerUuid}");
                    }
                    tcs.SetResult(deviceInfo.NativeDevice.CreateRfcommSocketToServiceRecord(_scannerUuid));
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

                //var connectTcs = new TaskCompletionSource<bool>();

                try
                {

                        //var test = deviceInfo.NativeDevice.FetchUuidsWithSdp();  //This might work to get the UUIDS/service id.  Need to figure out how to receive the inbound intent - BroadcastReceiver?
                    ////var uuids = deviceInfo.NativeDevice.GetUuids(); //Not working, and probably won't work

                    //await connectTcs.Task;

                    await deviceSocket.ConnectAsync();  //Currently failing - wrong service id?
                    if (deviceSocket.IsConnected)
                    {
                        deviceInfo.IsConnected = true;
                        deviceInfo.IsPaired = true; //TODO: Confirm that it is paired at this point
                        deviceInfo.ConnectedSocket = deviceSocket;
                        result = true;
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.ToString());
                    Debugger.Break();

                    try
                    {
                        deviceSocket.Close();
                    }
                    catch (Exception)
                    {
                        //Nothing to do here - couldn't close the socket
                    }

                    result = false;
                }
            }




            //    await Task.Run(() =>
            //    {
            //        try
            //        {
            //            deviceSocket.Connect();
            //            deviceInfo.IsConnected = true;
            //            deviceInfo.IsPaired = true; //TODO: Confirm that it is paired at this point
            //            deviceInfo.ConnectedSocket = deviceSocket;
            //            connectTcs.SetResult(true);
            //        }
            //        catch (Exception e)
            //        {
            //            try
            //            {
            //                deviceSocket.Close();
            //            }
            //            catch (Exception)
            //            {
            //                //Nothing to do here - couldn't close the socket
            //            }
            //            Debug.WriteLine(e.ToString());
            //            Debugger.Break();
            //            connectTcs.SetResult(false);
            //        }
            //    });
            //    result = await connectTcs.Task;
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

        public async Task<bool> PairWithDevice(IBluetoothDevice device)
        {
            var deviceInfo = device as BluetoothDeviceInfo;
            if (deviceInfo == null) { throw new ArgumentNullException(nameof(device));}

            bool result = deviceInfo.IsPaired;

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
                    Debug.WriteLine($"Problem while pairing device:\n{e}");
                    throw;
                }
                finally
                {
                    _pairedDeviceLocker.Release();
                }
            }
            else
            {
                Debug.WriteLine($"Device '{deviceInfo.DeviceName}' is already paired.");
            }

            return result;
        }

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

            if (_pairedDeviceTcs != null)
            {
                if (!(_pairedDeviceTcs.Task.IsCanceled || _pairedDeviceTcs.Task.IsCompleted))
                {
                    _pairedDeviceTcs.TrySetCanceled();
                }
                _pairedDeviceTcs = null;
            }

            _discoveryTimeoutTimer?.Stop();
            _discoveryTimeoutTimer?.Dispose();

            _adapter = null;
            _context = null;
        }

        #endregion
    }
}
