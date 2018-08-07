using System;

namespace BtClassicScanner.Models
{
    public interface IBluetoothDevice : IDisposable
    {
        bool IsPaired { get; }
        bool IsConnected { get; }
        bool IsDisposed { get; }
        string DeviceName { get; }
        string HardwareAddress { get; }
    }
}
