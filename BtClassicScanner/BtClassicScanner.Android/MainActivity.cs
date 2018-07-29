using Android.App;
using Android.Content.PM;
using Android.OS;
using BtClassicScanner.Droid.Services;
using BtClassicScanner.Services;
using CodeBrix.Prism.Ioc;
using Prism;
using Prism.Ioc;

namespace BtClassicScanner.Droid
{
    [Activity(Label = "BtClassicScanner", 
        Icon = "@mipmap/icon", 
        Theme = "@style/MainTheme", 
        MainLauncher = true, 
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation)]
    public class MainActivity : global::Xamarin.Forms.Platform.Android.FormsAppCompatActivity
    {
        protected override void OnCreate(Bundle bundle)
        {
            TabLayoutResource = Resource.Layout.Tabbar;
            ToolbarResource = Resource.Layout.Toolbar;

            base.OnCreate(bundle);

            global::Xamarin.Forms.Forms.Init(this, bundle);
            CodeBrix.Prism.Platform.Init(this, bundle);
            LoadApplication(new App(new AndroidInitializer()));
        }
    }

    public class AndroidInitializer : IPlatformInitializer
    {
        public void RegisterTypes(IContainerRegistry container)
        {
            // Register CodeBrix pages and services
            CodeBrix.Prism.Platform.RegisterTypes(container);

            // Register any platform specific services
            container.RegisterDisposable<IBluetoothService, AndroidBluetoothService>();
        }
    }
}

