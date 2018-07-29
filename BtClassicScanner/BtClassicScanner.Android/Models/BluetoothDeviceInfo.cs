using System;
using Android.Bluetooth;

namespace BtClassicScanner.Models
{
    public class BluetoothDeviceInfo : IBluetoothDevice
    {
        private BluetoothDevice _device;

        public bool IsPaired { get; set; }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected && ConnectedSocket != null;
            set => _isConnected = value;
        }

        public BluetoothSocket ConnectedSocket { get; set; }
        public string DeviceName => _device.Name;
        public string HardwareAddress => _device.Address;

        public BluetoothDevice NativeDevice => _device;

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
