using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static readonly string DeviceToLookFor = "SL_";

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

        #endregion

        #region Commands and their implementations

        #region FindDevicesCommand 

        private DelegateCommand _findDevicesCommand;
        public DelegateCommand FindDevicesCommand =>
            LazyCommand(ref _findDevicesCommand, DoFindDevices, () => !IsFindingDevices)
                .ObservesProperty(() => IsFindingDevices);

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
                    bool isPaired = await _bluetoothService.PairWithDevice(device);
                }
            }
        }

        public MainPageViewModel(
            INavigationService navigationService,
            IUserDialogs dialogService,
            IBluetoothService bluetoothService)
            : base(navigationService, dialogService)
        {
            _bluetoothService = bluetoothService ?? throw new ArgumentNullException(nameof(bluetoothService));
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
