using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace VortexPass.App;

public partial class TrayMenuWindow : Window
{
    private readonly Action _open;
    private readonly Func<Task> _toggleProtection;
    private readonly Func<Task> _exit;

    public TrayMenuWindow(Action open, Func<Task> toggleProtection, Func<Task> exit)
    {
        _open = open;
        _toggleProtection = toggleProtection;
        _exit = exit;
        InitializeComponent();
    }

    public void UpdateState(bool protectionEnabled, bool transitionInProgress)
    {
        StatusDot.Fill = Brush(protectionEnabled ? "#00D68F" : "#FF706A86");
        StatusPanel.Background = Brush(protectionEnabled ? "#1522A878" : "#151F1630");
        StatusPanel.BorderBrush = Brush(protectionEnabled ? "#4652DEAC" : "#353C3A57");
        StatusText.Foreground = Brush(protectionEnabled ? "#64F3D2" : "#FF9B97B3");
        StatusText.Text = transitionInProgress ? "Применяем изменения…" : protectionEnabled ? "Защита активна" : "Защита выключена";
        StatusHint.Text = transitionInProgress ? "Working" : protectionEnabled ? "Live" : "Idle";
        StatusHint.Foreground = Brush(protectionEnabled ? "#00D68F" : "#FF626078");
        ProtectionButton.IsEnabled = !transitionInProgress;
        ProtectionGlyph.Foreground = Brush(protectionEnabled ? "#00D68F" : "#FFC5BFFF");
        ProtectionText.Text = transitionInProgress ? "Подождите…" : protectionEnabled ? "Остановить защиту" : "Запустить защиту";
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
