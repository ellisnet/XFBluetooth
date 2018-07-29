using System;
using Android.Bluetooth;

namespace BtClassicScanner.Models
{
    public class BluetoothDeviceInfo : IBluetoothDevice
    {
        private BluetoothDevice _device;

        public bool IsPaired { get; set; }
        public string DeviceName => _device.Name;
        public string HardwareAddress => _device.Address;

        public BluetoothDeviceInfo(BluetoothDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public void Dispose()
        {
            //TODO: Figure out what is needed to dispose the native device
        }
    }
}
