using System;
using BtClassicScanner.Models;

namespace BtClassicScanner.Services
{
    public interface IBluetoothService : IDisposable
    {
        IObservable<IBluetoothDevice> GetDiscoveryObservable();
        void StartDeviceDiscovery(int? timeoutSeconds = null);
        void StopDeviceDiscovery();
    }
}
