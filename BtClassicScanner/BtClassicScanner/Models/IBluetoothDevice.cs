using System;

namespace BtClassicScanner.Models
{
    public interface IBluetoothDevice : IDisposable
    {
        bool IsPaired { get; }
        string DeviceName { get; }
        string HardwareAddress { get; }
    }
}
