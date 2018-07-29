using System;
using System.Diagnostics;
using Acr.UserDialogs;
using BtClassicScanner.Models;
using BtClassicScanner.Services;
using CodeBrix.Prism.Helpers;
using CodeBrix.Prism.ViewModels;
using Prism.Commands;
using Prism.Navigation;

namespace BtClassicScanner.ViewModels
{
    public class MainPageViewModel : ViewModelBase
    {
        private IBluetoothService _bluetoothService;
        private SimpleObserver<IBluetoothDevice> _discoveryObserver;

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

        public void DoFindDevices()
        {
            if (IsFindingDevices) { return;}

            IsFindingDevices = true;

            _discoveryObserver = new SimpleObserver<IBluetoothDevice>(DeviceDiscovered, 
                () => IsFindingDevices = false, 
                async (exception) =>
                {
                    await ShowErrorAsync(exception);
                    IsFindingDevices = false;
                });
            _discoveryObserver.GetSubscription(_bluetoothService.GetDiscoveryObservable());
            _bluetoothService.StartDeviceDiscovery(20);
        }

        #endregion

        #endregion

        public void DeviceDiscovered(IBluetoothDevice device)
        {
            Debug.WriteLine($"Device found -\nName: {device.DeviceName}\nAddress: {device.HardwareAddress}");
            DialogService.Toast($"Device found - Name: {device.DeviceName} - Address: {device.HardwareAddress}");
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
