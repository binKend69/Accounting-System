using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HisabTak.Application;
using HisabTak.WPF.ViewModels;
using HisabTak.WPF.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var window = new MainWindow(new MainWindowViewModel(new ApplicationIdentity("HisabTak", "smoke")),
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<MainWindow>>());
        window.Show(); window.UpdateLayout();
        if (window.FlowDirection != FlowDirection.RightToLeft) throw new InvalidOperationException("RTL layout missing.");
        if (((FrameworkElement)window.FindName("WorkspacePanel")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Workspace must require authentication.");
        Directory.CreateDirectory("artifacts/wpf-smoke");
        foreach (var width in new[] { 1220, 980 })
        {
            window.Width = width; window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create($"artifacts/wpf-smoke/login-{width}.png"); encoder.Save(stream);
        }
        var login = (FrameworkElement)window.FindName("LoginPanel");
        var button = FindButton(login);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!((TextBlock)window.FindName("StatusText")).Text.Contains("لم يضبط اتصال"))
            throw new InvalidOperationException("Missing-connection login must fail visibly and safely.");
        window.Close(); app.Shutdown();
        Console.WriteLine("WPF smoke passed: XAML construction, RTL, authenticated workspace gate, two window sizes, safe missing-connection login.");
    }
    private static Button FindButton(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) return button;
            try { return FindButton(child); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Login button not found.");
    }
}
