using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZapretPro.Core.Key;
using ZapretPro.Core.Vpn;

namespace ZapretPro.App;

/// <summary>Вкладка «Настройки»: ключ VPN, состояние VPN, версии. Простым языком, крупно.</summary>
public partial class SettingsPage : UserControl
{
    private AppEngine _engine = null!;
    private bool _confirmDelete;
    private bool _confirmDeleteSubscription;

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>Сообщение для всплывающей подсказки главного окна.</summary>
    public event Action<string>? Toast;

    public void Init(AppEngine engine)
    {
        _engine = engine;
        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        var (keyOk, keyText) = _engine.KeyState();
        KeyText.Text = keyText;
        KeyDot.Fill = Brush(keyOk ? "GreenBrush" : _engine.Keys.HasKey ? "RedBrush" : "GrayBrush");
        DeleteKeyButton.Visibility = _engine.Keys.HasKey ? Visibility.Visible : Visibility.Collapsed;
        if (!_confirmDelete)
            DeleteKeyButton.Content = "Удалить";

        var (subOk, subText) = _engine.SubscriptionState();
        SubscriptionText.Text = subText;
        SubscriptionDot.Fill = Brush(subOk ? "GreenBrush" : _engine.Subscriptions.HasSubscription ? "RedBrush" : "GrayBrush");
        DeleteSubscriptionButton.Visibility = _engine.Subscriptions.HasSubscription ? Visibility.Visible : Visibility.Collapsed;
        if (!_confirmDeleteSubscription)
            DeleteSubscriptionButton.Content = "Удалить";

        var vpn = _engine.Status.Vpn;
        var (text, color) = !_engine.IsOn
            ? ("Выключено — включи программу на главной", "GrayBrush")
            : vpn.State switch
            {
                VpnState.Connected => ($"Подключено: {vpn.Profile}", "GreenBrush"),
                VpnState.Connecting => ($"Подключаюсь: {vpn.Profile}…", "AmberBrush"),
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

    private async void PasteKey_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            Toast?.Invoke("Сначала скопируй ключ (vpn://…) или ссылку на подписку (https://…), потом нажми эту кнопку");
            return;
        }
        PasteKeyButton.IsEnabled = false;
        PasteKeyButton.Content = "Проверяю…";
        try
        {
            Toast?.Invoke(await _engine.SaveKeyAsync(text));
            Clipboard.Clear();
        }
        catch (KeyParseException ex)
        {
            Toast?.Invoke(ex.Message);
        }
        finally
        {
            PasteKeyButton.IsEnabled = true;
            PasteKeyButton.Content = "Вставить ключ или ссылку";
        }
        Refresh();
    }

    /// <summary>Удаление — в два нажатия, чтобы не стереть подписку случайно.</summary>
    private void DeleteSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirmDeleteSubscription)
        {
            _confirmDeleteSubscription = true;
            DeleteSubscriptionButton.Content = "Точно удалить?";
            return;
        }
        _confirmDeleteSubscription = false;
        _engine.DeleteSubscription();
        Toast?.Invoke("Подписка удалена");
        Refresh();
    }

    /// <summary>Удаление — в два нажатия, чтобы не стереть ключ случайно.</summary>
    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            DeleteKeyButton.Content = "Точно удалить?";
            return;
        }
        _confirmDelete = false;
        _engine.DeleteKey();
        Toast?.Invoke("Ключ удалён");
        Refresh();
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        ReconnectButton.IsEnabled = false;
        VpnText.Text = "Проверяю…";
        await _engine.CheckNowAsync();
        Refresh();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
}
