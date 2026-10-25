using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    /// <summary>Строка журнала с цветом: ошибки — красные, предупреждения — янтарные, успехи — зелёные.</summary>
    public sealed record LogLine(string Text, Brush Brush);

    // Слова, по которым видно, что строка про беду или про успех. Грубо, но глазу этого хватает
    private static readonly string[] Bad = ["ОШИБКА", "Ошибка", "не запустился", "Не удалось", "не удалось", "завис", "Не прошло", "не отвечает", "не помогла"];
    private static readonly string[] Warn = ["перебоями", "не ответил", "запасной", "Перезагрузи", "Проверка не прошла", "не скачалась", "часто не отвечает"];
    private static readonly string[] Good = ["Все проверки прошли", "открылось 2 из 2", "Самый быстрый", "Включено", "сохранён", "сохранена"];

    public void Init(AppEngine engine)
    {
        _engine = engine;
        foreach (var line in _engine.Log)
            LogList.Items.Add(Colorize(line));
        ScrollToEnd();

        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        _engine.LogAdded += line => Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(Colorize(line));
            while (LogList.Items.Count > 500)
                LogList.Items.RemoveAt(0);
            ScrollToEnd();
        });
        Refresh();
    }

    private LogLine Colorize(string line) => new(line, (Brush)FindResource(
        Bad.Any(line.Contains) ? "RedBrush"
        : Warn.Any(line.Contains) ? "AmberBrush"
        : Good.Any(line.Contains) ? "GreenBrush"
        : "MutedBrush"));

    public void Refresh()
    {
        var s = _engine.Status;
        var sb = new StringBuilder();
        sb.AppendLine($"Включено: {(_engine.IsOn ? "да" : "нет")}   zapret: {s.State}   альт: {s.StrategyId ?? "—"} ({s.StrategyNumber} из {s.StrategyCount})");
        sb.AppendLine($"VPN: {s.Vpn.State}{(s.Vpn.Profile is null ? "" : $" ({s.Vpn.Profile})")}, маршрутов {s.Vpn.RouteCount}, свой DNS: {(s.Vpn.SmartDns ? "да" : "нет")}" +
                      $"{(s.Vpn.Fallback.Count == 0 ? "" : $", запасной канал: {string.Join(", ", s.Vpn.Fallback)}")}");
        foreach (var service in Enum.GetValues<Service>())
        {
            var problem = s.LastReport?.Problems.GetValueOrDefault(service);
            var via = s.ViaVpn.Contains(service) ? " [VPN]" : "";
            sb.AppendLine($"{service,-9} {_engine.Look(service)}{via}{(problem is null ? "" : $" — {problem}")}");
        }
        if (s.LastReport is { } r)
            sb.AppendLine($"Интернет: {(r.InternetOk ? "есть" : "нет")}   ChatGPT видит: {r.AiCountry ?? "—"} ({r.AiExitIp ?? "—"})   " +
                          $"к прокси Telegram: {(r.TelegramClients is null ? "?" : r.TelegramClients.Count == 0 ? (r.TelegramAppRunning ? "никто" : "Telegram закрыт") : string.Join(", ", r.TelegramClients))}");
        if (s.LastError is not null)
            sb.AppendLine($"Ошибка: {s.LastError}");
        if (s.Vpn.Error is not null)
            sb.AppendLine($"VPN: {s.Vpn.Error}");
        DetailsText.Text = sb.ToString().TrimEnd();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyButton.IsEnabled = false;
        CopyButton.Content = "Собираю отчёт…";
        try
        {
            var report = await _engine.BuildFullReportAsync();
            Clipboard.SetText(report);
            Toast?.Invoke("Отчёт скопирован — вставь его в сообщение (Ctrl+V)");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Toast?.Invoke("Не получилось скопировать — нажми «Сохранить файлом»");
        }
        finally
        {
            CopyButton.Content = "Скопировать отчёт";
            CopyButton.IsEnabled = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var path = await _engine.SaveReportAsync();
            Toast?.Invoke($"Отчёт на рабочем столе: {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Toast?.Invoke($"Не удалось сохранить: {ex.Message}");
        }
        finally
        {
            SaveButton.IsEnabled = true;
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
