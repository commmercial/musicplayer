using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;

namespace Vybe.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
        }
        catch (Exception ex)
        {
            // Keep startup failures visible. Without a fallback window, a XAML or
            // initialization error makes the app appear to do nothing when launched.
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.txt");
                File.WriteAllText(logPath, ex.ToString());
            }
            catch { }

            _window = new Window
            {
                Title = "<3 could not start",
                Content = new ScrollViewer
                {
                    Padding = new Thickness(24),
                    Content = new TextBlock
                    {
                        Text = "<3 hit an error while opening:\n\n" + ex,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            };
        }

        _window.Activate();
    }
}
