using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Android.Bluetooth;
using Android.Content;
using BtClassicScanner.Models;
using BtClassicScanner.Services;
using CodeBrix.Prism.Android.Services;
using CodeBrix.Prism.Helpers;
using Debug = System.Diagnostics.Debug;

//Created based on documentation available here:
// https://developer.android.com/guide/topics/connectivity/bluetooth.html
// https://developer.xamarin.com/api/member/Android.Bluetooth.BluetoothAdapter.StartDiscovery/

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
        private readonly object _discoverySubscriberLocker = new object();
        private readonly List<ObserverSubscription<IBluetoothDevice>> _discoverySubscriptions 
            = new List<ObserverSubscription<IBluetoothDevice>>();

        private SimpleBroadcastReceiver _discoveryReceiver;
        private SimpleBroadcastReceiver _discoveryStartedReceiver;
        private SimpleBroadcastReceiver _discoveryFinishedReceiver;
        private bool _isDiscoveryReceiverRegistered;
        private Timer _discoveryTimeoutTimer;

        #region Implement IBluetoothService

        public IObservable<IBluetoothDevice> GetDiscoveryObservable() => this;

        // ReSharper disable once InconsistentlySynchronizedField
        public bool IsDiscovering => _isDiscovering && _isDiscoveryRunning;

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

        public IDisposable Subscribe(IObserver<IBluetoothDevice> subscriber)
        {
            if (subscriber == null) { throw new ArgumentNullException(nameof(subscriber));}
            var subscription = new ObserverSubscription<IBluetoothDevice>(subscriber, RemoveDiscoverySubscriber);
            lock (_discoverySubscriberLocker)
            {
                _discoverySubscriptions.Add(subscription);
            }

            return subscription;
        }

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
                _adapter = _adapter ?? BluetoothAdapter.DefaultAdapter;

                if (_adapter == null)
                {
                    throw new InvalidOperationException("A Bluetooth adapter could not be found on this device.");
                }

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
                                foreach (ObserverSubscription<IBluetoothDevice> subscription in _discoverySubscriptions)
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

                _adapter = _adapter ?? BluetoothAdapter.DefaultAdapter;

                if (_adapter == null)
                {
                    throw new InvalidOperationException("A Bluetooth adapter could not be found on this device.");
                }

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

        #endregion

        public AndroidBluetoothService(IContextService contextService)
        {
            _context = _context ?? contextService?.Context ?? throw new ArgumentNullException(nameof(contextService));
        }

        #region Implement IDisposable

        public void Dispose()
        {
            _adapter = null;
            _context = null;
        }

        #endregion

    }
}