using Prism;
using Prism.DryIoc;
using Prism.Ioc;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

[assembly: XamlCompilation (XamlCompilationOptions.Compile)]

namespace BtClassicScanner
{
	public partial class App : PrismApplication
	{
        //Default constructor needed for Xamarin Forms XAML Previewer - 
        // should NOT be used at runtime
	    public App() : this(null) { }

        //This constructor must be used at runtime
	    public App(IPlatformInitializer initializer) : base(initializer) { }

	    protected override async void OnInitialized()
	    {
	        InitializeComponent();
	        await NavigationService.NavigateAsync($"{nameof(NavigationPage)}/{nameof(Views.MainPage)}");
        }

	    protected override void RegisterTypes(IContainerRegistry containerRegistry)
	    {
	        //Register views here for navigation via Prism NavigationService -
	        // no need to register NavigationPage - already registered by CodeBrix
	        containerRegistry.RegisterForNavigation<Views.MainPage>();
	    }

        protected override void OnStart ()
		{
			// Handle when your app starts
		}

		protected override void OnSleep ()
		{
			// Handle when your app sleeps
		}

		protected override void OnResume ()
		{
			// Handle when your app resumes
		}
	}
}
