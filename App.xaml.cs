using Microsoft.UI.Xaml;

namespace RobotControllerApp
{
    /// <summary>
    /// Singleton application entry point managing the primary window lifecycle.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the application instance and associated XAML components.
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Transitions the application into the running state by instantiating and activating the dashboard window.
        /// </summary>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
