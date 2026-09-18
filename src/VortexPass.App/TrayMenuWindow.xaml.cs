using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace VortexPass.App;

public partial class TrayMenuWindow : Window
{
    private readonly Action _open;
    private readonly Func<Task> _toggleProtection;
    private readonly Func<Task> _toggleWarp;
    private readonly Func<Task> _exit;

    public TrayMenuWindow(Action open, Func<Task> toggleProtection, Func<Task> toggleWarp, Func<Task> exit)
    {
        _open = open;
        _toggleProtection = toggleProtection;
        _toggleWarp = toggleWarp;
        _exit = exit;
        InitializeComponent();
    }

    public void UpdateState(bool protectionEnabled, bool transitionInProgress, bool needsCheck = false, bool hasError = false, bool warpConnected = false, bool warpPending = false)
    {
        var healthy = protectionEnabled && !needsCheck && !hasError;
        var accent = hasError ? "#FF6B6B" : needsCheck ? "#FFB340" : healthy ? "#00D68F" : "#FF706A86";
        StatusDot.Fill = Brush(accent);
        StatusPanel.Background = Brush(healthy ? "#1522A878" : "#151F1630");
        StatusPanel.BorderBrush = Brush(healthy ? "#4652DEAC" : "#353C3A57");
        StatusText.Foreground = Brush(healthy ? "#64F3D2" : accent);
        StatusText.Text = transitionInProgress ? "Запуск защиты…" : hasError ? "Ошибка защиты" : needsCheck ? "Нужна проверка маршрута" : protectionEnabled ? "Защита активна" : "Защита выключена";
        StatusHint.Text = transitionInProgress ? "Starting" : hasError ? "Error" : needsCheck ? "Check" : protectionEnabled ? "Live" : "Idle";
        StatusHint.Foreground = Brush(accent);
        ProtectionButton.IsEnabled = !transitionInProgress || protectionEnabled;
        ProtectionGlyph.Foreground = Brush(protectionEnabled ? "#00D68F" : "#FFC5BFFF");
        ProtectionText.Text = transitionInProgress && !protectionEnabled ? "Подождите…" : protectionEnabled ? "Остановить защиту" : "Запустить защиту";
        WarpButton.IsEnabled = !warpPending;
        WarpGlyph.Foreground = Brush(warpConnected ? "#00D68F" : "#FF62AFFF");
        WarpText.Text = warpPending ? "WARP: подождите…" : warpConnected ? "Выключить WARP" : "Включить WARP";
    }

    private static System.Windows.Media.SolidColorBrush Brush(string value) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));

    public void ShowAt(System.Windows.Point cursor, Rect workArea)
    {
        var menuWidth = Width;
        var menuHeight = Height;
        Left = Math.Clamp(cursor.X - menuWidth + 20, workArea.Left + 4, workArea.Right - menuWidth - 4);
        Top = Math.Clamp(cursor.Y - menuHeight + 12, workArea.Top + 4, workArea.Bottom - menuHeight - 4);
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
        Activate();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _open();
    }

    private async void ProtectionButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await _toggleProtection();
    }

    private async void WarpButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await _toggleWarp();
    }

    private async void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await _exit();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => Hide();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }
}
