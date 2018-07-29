using System.Diagnostics;
using Acr.UserDialogs;
using CodeBrix.Prism.ViewModels;
using Prism.Navigation;

namespace BtClassicScanner.ViewModels
{
    public class MainPageViewModel : ViewModelBase
    {
        public MainPageViewModel(
            INavigationService navigationService,
            IUserDialogs dialogService)
            : base(navigationService, dialogService)
        { }
    }
}
