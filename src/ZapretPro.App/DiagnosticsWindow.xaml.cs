using System.Text;
using System.Windows;
using ZapretPro.Core.Health;

namespace ZapretPro.App;

/// <summary>Подробности для внука: всё состояние как есть и журнал.</summary>
public partial class DiagnosticsWindow : Window
{
    private readonly AppEngine _engine;

    public DiagnosticsWindow(AppEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        foreach (var line in _engine.Log)
            LogList.Items.Add(line);
        ScrollToEnd();

        Action refresh = () => Dispatcher.BeginInvoke(Refresh);
        Action<string> added = line => Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(line);
            ScrollToEnd();
        });
        _engine.StatusChanged += refresh;
        _engine.LogAdded += added;
        Closed += (_, _) =>
        {
            _engine.StatusChanged -= refresh;
            _engine.LogAdded -= added;
        };
        Refresh();
    }

    private void Refresh()
    {
        var s = _engine.Status;
        var sb = new StringBuilder();
        sb.AppendLine($"Включено: {(_engine.IsOn ? "да" : "нет")}    состояние zapret: {s.State}    стратегия: {s.StrategyId ?? "—"} ({s.StrategyNumber} из {s.StrategyCount})");
        sb.AppendLine($"VPN: {s.Vpn.State}, маршрутов {s.Vpn.RouteCount}, запасной канал: {(s.Vpn.Fallback.Count == 0 ? "—" : string.Join(", ", s.Vpn.Fallback))}");
        foreach (var service in Enum.GetValues<Service>())
        {
            var problem = s.LastReport?.Problems.GetValueOrDefault(service);
            var via = s.ViaVpn.Contains(service) ? " [через VPN]" : "";
            sb.AppendLine($"{service,-9} {_engine.Look(service)}{via}{(problem is null ? "" : $" — {problem}")}");
        }
        if (s.LastReport is { } r)
        {
            sb.AppendLine($"Интернет: {(r.InternetOk ? "есть" : "нет")}    нейросети видят страну: {r.AiCountry ?? "—"}");
            sb.AppendLine($"К прокси Telegram подключены: {(r.TelegramClients is null ? "?" : r.TelegramClients.Count == 0 ? "никто" : string.Join(", ", r.TelegramClients))}");
        }
        if (s.LastError is not null)
            sb.AppendLine($"Ошибка: {s.LastError}");
        if (s.Vpn.Error is not null)
            sb.AppendLine($"VPN: {s.Vpn.Error}");
        DetailsText.Text = sb.ToString().TrimEnd();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        await _engine.CheckNowAsync();
        CheckButton.IsEnabled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_engine.BuildReport());
            MessageBox.Show(this, "Журнал скопирован — вставь его в сообщение (Ctrl+V).", "Готово");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось скопировать");
        }
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
