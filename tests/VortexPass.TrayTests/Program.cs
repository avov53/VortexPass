using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VortexPass.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ConnectionHandlerTests.Run().GetAwaiter().GetResult();
        var opened = false;
        var toggled = false;
        var window = new TrayMenuWindow(() => opened = true, () => { toggled = true; return Task.CompletedTask; },
            () => Task.CompletedTask, () => Task.CompletedTask);
        var output = Path.GetFullPath("artifacts/native-ui-qa"); Directory.CreateDirectory(output);
        foreach (var light in new[] { false, true })
        {
            window.SetAppearance(light, true);
            window.UpdateState(true, false, false, false, true, false);
            if (((TextBlock)window.FindName("StatusText")).Text != "Защита активна") throw new Exception("Tray status mismatch");
            if (((TextBlock)window.FindName("ProtectionText")).Text != "Остановить защиту") throw new Exception("Tray action mismatch");
            var view = (FrameworkElement)window.Content;
            view.Measure(new Size(310, 300)); view.Arrange(new Rect(0, 0, 310, 300)); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(310, 300, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, light ? "tray-light.png" : "tray-dark.png")); png.Save(file);
        }
        window.UpdateState(false, true, warpPending: true);
        if (((Button)window.FindName("ProtectionButton")).IsEnabled || ((Button)window.FindName("WarpButton")).IsEnabled)
            throw new Exception("Tray enables commands during transition");
        window.UpdateState(false, false);
        ((Button)window.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)window.FindName("ProtectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!opened || !toggled) throw new Exception("Tray callbacks lost");
        window.Close();
        Console.WriteLine("PASS native WPF tray: themes, status, disabled transitions, callbacks; snapshots rendered without network changes");
    }
}
