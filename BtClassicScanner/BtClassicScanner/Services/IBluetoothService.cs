using System;
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
    }
}
