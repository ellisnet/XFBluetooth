using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Acr.UserDialogs;
using BtClassicScanner.Models;
using BtClassicScanner.Services;
using CodeBrix.Prism.Helpers;
using CodeBrix.Prism.ViewModels;
using Plugin.Permissions;
using Plugin.Permissions.Abstractions;
using Prism.Commands;
using Prism.Navigation;

namespace BtClassicScanner.ViewModels
{
    public class MainPageViewModel : ViewModelBase
    {
        //private static readonly string DeviceToLookFor = "SL_"; //TMSL-55 - BLE scanner
        private static readonly string DeviceToLookFor = "CT1018"; //TMCT-10 Pro - Bluetooth classic scanner

        public static readonly byte LineFeed = 0x0a;
        public static readonly byte CarriageReturn = 0x0d;

        private readonly List<byte> _incomingBytes = new List<byte>();
        private readonly object _incomingByteLocker = new object();

        private IBluetoothService _bluetoothService;
        private SimpleObserver<IBluetoothDevice> _discoveryObserver;
        private IPermissions _permissions = CrossPermissions.Current;

        #region Bindable properties

        private bool _isFindingDevices;
        public bool IsFindingDevices
        {
            get => _isFindingDevices;
            set => SetProperty(ref _isFindingDevices, value);
        }

        private bool _isScannerPaired;
        public bool IsScannerPaired
        {
            get => _isScannerPaired;
            set => SetProperty(ref _isScannerPaired, value);
        }

        #endregion

        #region Commands and their implementations

        #region FindDevicesCommand 

        private DelegateCommand _findDevicesCommand;
        public DelegateCommand FindDevicesCommand =>
            LazyCommand(ref _findDevicesCommand, DoFindDevices, () => (!IsFindingDevices) && (!IsScannerPaired))
                .ObservesProperty(() => IsFindingDevices)
                .ObservesProperty(() => IsScannerPaired);

        public async void DoFindDevices()
        {
            if (IsFindingDevices) { return;}

            IsFindingDevices = true;

            try
            {
                _discoveryObserver = new SimpleObserver<IBluetoothDevice>(DeviceDiscovered,
                    () => IsFindingDevices = false,
                    async (exception) =>
                    {
                        await ShowErrorAsync(exception);
                        IsFindingDevices = false;
                    });
                _discoveryObserver.GetSubscription(_bluetoothService.GetDiscoveryObservable());

                //Check for permission on location service - needed for Bluetooth device discovery
                PermissionStatus status = await _permissions.CheckPermissionStatusAsync(Permission.Location);
                if (status != PermissionStatus.Granted)
                {
                    //Show the message whether rationale is needed or not.
                    //if (await _permissions.ShouldShowRequestPermissionRationaleAsync(Permission.Location))
                    //{
                        await ShowInfoAsync(
                            "The app needs access to the Location service to scan for Bluetooth devices.",
                            "Location permission needed");
                    //}

                    Dictionary<Permission, PermissionStatus> permissionResult = await _permissions.RequestPermissionsAsync(Permission.Location);
                    if (permissionResult.ContainsKey(Permission.Location))
                    {
                        status = permissionResult[Permission.Location];
                    }
                }

                if (status == PermissionStatus.Granted)
                {
                    await _bluetoothService.StartDeviceDiscovery(30);
                }
                else if (status != PermissionStatus.Unknown)
                {
                    await ShowErrorAsync("Unable to scan for Bluetooth devices without the required permissions.");
                }
            }
            catch (Exception e)
            {
                await ShowErrorAsync(e);
                IsFindingDevices = false;
            }            
        }

        #endregion

        #endregion

        public async void DeviceDiscovered(IBluetoothDevice device)
        {
            if (device != null)
            {
                Debug.WriteLine($"Device detected -\nName: {device.DeviceName}\nAddress: {device.HardwareAddress}");
                if (!String.IsNullOrWhiteSpace(device.DeviceName) && device.DeviceName.Contains(DeviceToLookFor))
                {
                    DialogService.Toast($"Scanner found! Name: {device.DeviceName} - Address: {device.HardwareAddress}");
                    await _bluetoothService.StopDeviceDiscovery();
                    IsScannerPaired = await _bluetoothService.PairWithDevice(device, ProcessIncomingBytes);
                }
            }
        }

        private void ProcessIncomingBytes(byte[] incoming)
        {
            if (incoming != null && incoming.Length > 0)
            {
                lock (_incomingByteLocker)
                {
                    bool endOfCode = false;
                    foreach (byte current in incoming)
                    {
                        endOfCode = current == LineFeed || current == CarriageReturn;
                        if (endOfCode)
                        {
                            break;
                        }
                        else
                        {
                            _incomingBytes.Add(current);
                        }
                    }

                    if (endOfCode)
                    {
                        string barcode = null;
                        if (_incomingBytes.Count > 0)
                        {
                            barcode = Encoding.ASCII.GetString(_incomingBytes.ToArray());
                        }
                        _incomingBytes.Clear();
                        if (barcode != null)
                        {
                            //This needs to be fire-and-forget
                            new Task(async () =>
                            {
                                string message = barcode;
                                await DialogService.AlertAsync(message, "Barcode read!");
                            }).Start();
                        }
                    }
                }
            }
        }

        private async Task<bool> CheckConnectPairedDevice()
        {
            bool result = false;

            IList<IBluetoothDevice> paired = await _bluetoothService.GetPairedDevices();
            if (paired.Any(a => a.DeviceName.Contains(DeviceToLookFor)))
            {
                IBluetoothDevice device = paired.First(f => f.DeviceName.Contains(DeviceToLookFor));
                result = await _bluetoothService.ConnectWithPairedDevice(device, ProcessIncomingBytes);
                IsScannerPaired = result;
                if (result)
                {
                    DialogService.Toast($"Scanner found! Name: {device.DeviceName} - Address: {device.HardwareAddress}");
                }
            }

            return result;
        }

        public MainPageViewModel(
            INavigationService navigationService,
            IUserDialogs dialogService,
            IBluetoothService bluetoothService)
            : base(navigationService, dialogService)
        {
            _bluetoothService = bluetoothService ?? throw new ArgumentNullException(nameof(bluetoothService));

            //Starting a fire-and-forget task to try connecting to a previously paired device
            new Task(async () =>
            {
                await Task.Delay(500);
                await CheckConnectPairedDevice();
            }).Start();
        }

        public override void Destroy()
        {
            _discoveryObserver?.Dispose();
            _discoveryObserver = null;
            _bluetoothService.Dispose();
            _bluetoothService = null;
            base.Destroy();
        }
    }
}
