using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Content;
using BtClassicScanner.Models;
using BtClassicScanner.Services;
using CodeBrix.Prism.Android.Services;
using CodeBrix.Prism.Helpers;

//Created based on documentation available here:
// https://developer.android.com/guide/topics/connectivity/bluetooth.html

namespace BtClassicScanner.Droid.Services
{
    public class AndroidBluetoothService : IBluetoothService, IObservable<IBluetoothDevice>
    {
        private static Context _context;
        private static BluetoothAdapter _adapter;
        private static bool _isDiscovering;
        private static bool _isDiscoveryStopping;
        private static readonly object _discoveryLocker = new object();
        private static readonly object _deviceDiscoveredLocker = new object();
        private readonly object _discoverySubscriberLocker = new object();
        private readonly List<ObserverSubscription<IBluetoothDevice>> _discoverySubscriptions 
            = new List<ObserverSubscription<IBluetoothDevice>>();

        private SimpleBroadcastReceiver _discoveryReceiver;
        private bool _isDiscoveryReceiverRegistered;

        #region Implement IBluetoothService

        public IObservable<IBluetoothDevice> GetDiscoveryObservable() => this;

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

        public void StartDeviceDiscovery(int? timeoutSeconds = null)
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

                _context.RegisterReceiver(_discoveryReceiver, new IntentFilter(BluetoothDevice.ActionFound));
                _isDiscoveryReceiverRegistered = true;
                //TODO: Need to implement timer task to timeout discovery here
                _adapter.StartDiscovery();
            }
        }

        public void StopDeviceDiscovery()
        {
            bool stopDiscovery = false;
            lock (_discoveryLocker)
            {
                stopDiscovery = _isDiscovering && (!_isDiscoveryStopping);
                if (stopDiscovery) { _isDiscoveryStopping = true;}
            }

            if (stopDiscovery)
            {
                _adapter = _adapter ?? BluetoothAdapter.DefaultAdapter;

                if (_adapter == null)
                {
                    throw new InvalidOperationException("A Bluetooth adapter could not be found on this device.");
                }

                if (_adapter.IsEnabled && _adapter.IsDiscovering)
                {
                    _adapter.CancelDiscovery();
                }

                if (_discoveryReceiver != null && _isDiscoveryReceiverRegistered)
                {
                    _context.UnregisterReceiver(_discoveryReceiver);
                    _isDiscoveryReceiverRegistered = false;
                    _discoveryReceiver = null;
                }
            }
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