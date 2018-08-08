using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BtClassicScanner.Services
{
    public class IncomingBytes
    {
        public byte[] Bytes { get; }

        public IncomingBytes(byte[] bytes)
        {
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length == 0) { throw new ArgumentException("The array cannot be empty.", nameof(bytes));}
        }
    }

    public interface IBluetoothDevice : IDisposable
    {
        bool IsPaired { get; }
        bool IsConnected { get; }
        bool IsDisposed { get; }
        string DeviceName { get; }
        string HardwareAddress { get; }
    }

    public interface IBluetoothService : IDisposable
    {
        bool IsDiscovering { get; }
        IObservable<IBluetoothDevice> GetDiscoveryObservable();
        IObservable<IncomingBytes> GetIncomingObservable();
        Task<bool> StartDeviceDiscovery(int? timeoutSeconds = null);
        Task<bool> StopDeviceDiscovery();
        Task<bool> PairWithDevice(IBluetoothDevice device);
        Task<bool> ConnectWithPairedDevice(IBluetoothDevice device);
        Task<IList<IBluetoothDevice>> GetPairedDevices();
        Task DisconnectDevice();
    }
}
