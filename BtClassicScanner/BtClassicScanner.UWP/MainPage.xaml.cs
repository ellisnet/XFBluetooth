using Prism;
using Prism.Ioc;

namespace BtClassicScanner.UWP
{
    public sealed partial class MainPage
    {
        public MainPage()
        {
            this.InitializeComponent();
            CodeBrix.Prism.Platform.Init();
            LoadApplication(new BtClassicScanner.App(new UwpInitializer()));
        }
    }

    public class UwpInitializer : IPlatformInitializer
    {
        public void RegisterTypes(IContainerRegistry container)
        {
            // Register CodeBrix pages and services
            CodeBrix.Prism.Platform.RegisterTypes(container);

            // Register any platform specific services
        }
    }
}
