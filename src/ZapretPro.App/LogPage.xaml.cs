using System.Text;
using System.Windows;
using System.Windows.Controls;
using ZapretPro.Core.Health;

namespace ZapretPro.App;

/// <summary>Вкладка «Журнал»: подробное состояние, журнал и «Скопировать отчёт».</summary>
public partial class LogPage : UserControl
{
    private AppEngine _engine = null!;

    public LogPage()
    {
        InitializeComponent();
    }

    /// <summary>Сообщение для всплывающей подсказки главного окна.</summary>
    public event Action<string>? Toast;

    public void Init(AppEngine engine)
    {
        _engine = engine;
        foreach (var line in _engine.Log)
            LogList.Items.Add(line);
        ScrollToEnd();

        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        _engine.LogAdded += line => Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(line);
            while (LogList.Items.Count > 500)
                LogList.Items.RemoveAt(0);
            ScrollToEnd();
        });
        Refresh();
    }

    public void Refresh()
    {
        var s = _engine.Status;
        var sb = new StringBuilder();
        sb.AppendLine($"Включено: {(_engine.IsOn ? "да" : "нет")}   zapret: {s.State}   способ: {s.StrategyId ?? "—"} ({s.StrategyNumber} из {s.StrategyCount})");
        sb.AppendLine($"VPN: {s.Vpn.State}, маршрутов {s.Vpn.RouteCount}{(s.Vpn.Fallback.Count == 0 ? "" : $", запасной канал: {string.Join(", ", s.Vpn.Fallback)}")}");
        foreach (var service in Enum.GetValues<Service>())
        {
            var problem = s.LastReport?.Problems.GetValueOrDefault(service);
            var via = s.ViaVpn.Contains(service) ? " [VPN]" : "";
            sb.AppendLine($"{service,-9} {_engine.Look(service)}{via}{(problem is null ? "" : $" — {problem}")}");
        }
        if (s.LastReport is { } r)
            sb.AppendLine($"Интернет: {(r.InternetOk ? "есть" : "нет")}   нейросети видят страну: {r.AiCountry ?? "—"}   " +
                          $"к прокси Telegram: {(r.TelegramClients is null ? "?" : r.TelegramClients.Count == 0 ? "никто" : string.Join(", ", r.TelegramClients))}");
        if (s.LastError is not null)
            sb.AppendLine($"Ошибка: {s.LastError}");
        if (s.Vpn.Error is not null)
            sb.AppendLine($"VPN: {s.Vpn.Error}");
        DetailsText.Text = sb.ToString().TrimEnd();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        button.Content = "Собираю отчёт…";
        try
        {
            var report = await _engine.BuildFullReportAsync();
            Clipboard.SetText(report);
            Toast?.Invoke("Отчёт скопирован — вставь его в сообщение (Ctrl+V)");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Toast?.Invoke("Не получилось скопировать — попробуй ещё раз");
        }
        finally
        {
            button.Content = "Скопировать отчёт";
            button.IsEnabled = true;
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        await _engine.CheckNowAsync();
        CheckButton.IsEnabled = true;
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
