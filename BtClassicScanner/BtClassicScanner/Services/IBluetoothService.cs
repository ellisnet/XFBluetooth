using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BtClassicScanner.Models;

namespace BtClassicScanner.Services
{
    public interface IBluetoothService : IDisposable
    {
        bool IsDiscovering { get; }
        IObservable<IBluetoothDevice> GetDiscoveryObservable();
        Task<bool> StartDeviceDiscovery(int? timeoutSeconds = null);
        Task<bool> StopDeviceDiscovery();
        Task<bool> PairWithDevice(IBluetoothDevice device);
        Task<IList<IBluetoothDevice>> GetPairedDevices();
    }
}
