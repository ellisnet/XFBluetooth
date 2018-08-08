using System;
using Android.Bluetooth;
using BtClassicScanner.Services;

namespace BtClassicScanner.Models
{
    public class BluetoothDeviceInfo : IBluetoothDevice
    {
        private BluetoothDevice _device;

        public bool IsPaired { get; set; }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected && ConnectedSocket != null && ConnectedSocket.IsConnected;
            set => _isConnected = value;
        }

        public bool IsDisposed { get; private set; }

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
            IsDisposed = true;
            if (ConnectedSocket != null && ConnectedSocket.IsConnected)
            {
                ConnectedSocket.Close();
            }
            ConnectedSocket?.Dispose();
            ConnectedSocket = null;
            _device?.Dispose();
            _device = null;
        }
    }
}
