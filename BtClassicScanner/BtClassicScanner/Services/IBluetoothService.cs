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
        Task<bool> PairWithDevice(IBluetoothDevice device, Action<byte[]> incomingAction);
        Task<bool> ConnectWithPairedDevice(IBluetoothDevice device, Action<byte[]> incomingAction);
        Task<IList<IBluetoothDevice>> GetPairedDevices();
        Task DisconnectDevice();
    }
}
