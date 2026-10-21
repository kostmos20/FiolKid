using System.Windows;
using System.Windows.Media;
using ZapretPro.Core.Key;
using ZapretPro.Core.Vpn;

namespace ZapretPro.App;

/// <summary>Настройки: ключ VPN, состояние VPN, версии. Простым языком, крупно.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppEngine _engine;

    public SettingsWindow(AppEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        Action refresh = () => Dispatcher.BeginInvoke(Refresh);
        _engine.StatusChanged += refresh;
        Closed += (_, _) => _engine.StatusChanged -= refresh;
        Refresh();
    }

    private void Refresh()
    {
        var (keyOk, keyText) = _engine.KeyState();
        KeyText.Text = keyText;
        KeyDot.Fill = Brush(keyOk ? "GreenBrush" : _engine.Keys.HasKey ? "RedBrush" : "AmberBrush");
        PasteKeyButton.Content = _engine.Keys.HasKey ? "Заменить ключ" : "Вставить ключ";
        DeleteKeyButton.Visibility = _engine.Keys.HasKey ? Visibility.Visible : Visibility.Collapsed;

        var vpn = _engine.Status.Vpn;
        var (text, color) = !_engine.IsOn
            ? ("Выключено — включи программу на главном экране", "GrayBrush")
            : vpn.State switch
            {
                VpnState.Connected => ("Подключено", "GreenBrush"),
                VpnState.Connecting => ("Подключаюсь…", "AmberBrush"),
                VpnState.NoKey => ("Нет ключа", "AmberBrush"),
                VpnState.BadKey => ("Ключ не подходит", "RedBrush"),
                VpnState.ServerNotResponding => ("Сервер не отвечает", "RedBrush"),
                VpnState.Failed => ("Не подключается", "RedBrush"),
                _ => ("Подключаюсь…", "AmberBrush"),
            };
        VpnText.Text = text;
        VpnDot.Fill = Brush(color);
        var hint = _engine.IsOn && vpn.State is not (VpnState.Connected or VpnState.NoKey) ? vpn.Error : null;
        VpnHint.Text = hint;
        VpnHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        ReconnectButton.IsEnabled = _engine.IsOn;

        // Привязка WPF видит только свойства, а у кортежа — поля
        VersionsList.ItemsSource = _engine.ComponentVersions().Select(v => new { Item1 = v.Name, Item2 = v.Version }).ToList();
    }

    private void PasteKey_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "Сначала скопируй ключ (он начинается с vpn://), потом нажми эту кнопку.",
                "Ключ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _engine.SaveKey(text);
            Clipboard.Clear();
        }
        catch (KeyParseException ex)
        {
            MessageBox.Show(this, ex.Message, "Ключ не подходит", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Удалить ключ? Нейросети и заблокированные сайты перестанут открываться, пока не вставишь новый.",
                "Удалить ключ", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _engine.DeleteKey();
        Refresh();
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        ReconnectButton.IsEnabled = false;
        VpnText.Text = "Проверяю…";
        await _engine.CheckNowAsync();
        Refresh();
    }

    private void Details_Click(object sender, RoutedEventArgs e) =>
        new DiagnosticsWindow(_engine) { Owner = this }.Show();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private Brush Brush(string key) => (Brush)FindResource(key);
}
