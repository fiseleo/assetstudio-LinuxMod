using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AssetStudio.Avalonia.Views;

namespace AssetStudio.Avalonia
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = new MainWindow();
                desktop.MainWindow = mainWindow;
                if (desktop.Args?.Length > 0)
                {
                    mainWindow.Opened += (_, _) => mainWindow.LoadPaths(desktop.Args);
                }
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
