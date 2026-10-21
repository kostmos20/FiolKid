using System.Diagnostics;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Zapret;

/// <summary>То, что нужно <see cref="Health.Watchdog"/> от zapret. Отдельно — чтобы проверять перебор стратегий без WinDivert.</summary>
public interface IZapretRunner
{
    IReadOnlyList<Strategy> Strategies { get; }
    bool IsRunning { get; }
    string? LastError { get; }
    Task<bool> StartAsync(Strategy strategy, CancellationToken ct);
    void Stop();

    /// <summary>
    /// winws упал на открытии драйвера WinDivert. Тогда дело не в стратегии, и перебирать остальные
    /// бессмысленно: они упадут так же.
    /// </summary>
    bool DriverBroken { get; }
}

/// <summary>
/// Запускает и останавливает winws.exe (zapret + драйвер WinDivert) с выбранной стратегией.
/// Ожидаемая раскладка папки: bin\ (winws.exe, WinDivert, *.bin), lists\, strategies.json.
/// </summary>
public sealed class ZapretManager : IZapretRunner
{
    private const string WinwsName = "winws";

    // Если winws не принял аргументы или не смог загрузить драйвер, он завершается почти сразу.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(1.5);

    private readonly string _binDir;
    private readonly string _listsDir;
    private readonly string _strategiesPath;
    private readonly OutputTail _output = new();
    private IReadOnlyList<Strategy>? _strategies;
    private Process? _process;

    public ZapretManager() : this(AppPaths.ZapretDir)
    {
    }

    public ZapretManager(string zapretDir)
    {
        _binDir = Path.Combine(zapretDir, "bin");
        _listsDir = Path.Combine(zapretDir, "lists");
        _strategiesPath = Path.Combine(zapretDir, "strategies.json");
    }

    public IReadOnlyList<Strategy> Strategies => _strategies ??= StrategyFile.Load(_strategiesPath).Strategies;

    public bool IsRunning => _process is { HasExited: false };

    public string? LastError { get; private set; }

    public bool DriverBroken { get; private set; }

    /// <summary>Последние строки вывода winws — для отчёта.</summary>
    public IReadOnlyList<string> RecentOutput => _output.Lines;

    public async Task<bool> StartAsync(Strategy strategy, CancellationToken ct)
    {
        Stop();
        KillStrayWinws();
        LastError = null;
        DriverBroken = false;

        var winws = Path.Combine(_binDir, "winws.exe");
        if (!File.Exists(winws))
        {
            // Чаще всего его удалил или заблокировал антивирус
            LastError = $"Не найден {winws}. Возможно, его удалил антивирус.";
            return false;
        }

        EnsureUserLists();
        _output.Clear();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(winws, strategy.BuildArguments(_binDir, _listsDir))
            {
                WorkingDirectory = _binDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastError = $"Не удалось запустить winws: {e.Message}";
            process.Dispose();
            return false;
        }

        _output.Attach(process);
        _process = process;

        await Task.Delay(StartupGrace, ct);
        if (process.HasExited)
        {
            var lines = _output.Lines;
            var tail = string.Join(" | ", lines.TakeLast(3));
            var exitCode = process.ExitCode;
            _process = null;
            process.Dispose();

            // Ошибки драйвера winws пишет как «windivert: error …»
            if (lines.Any(l => l.Contains("windivert:", StringComparison.OrdinalIgnoreCase)))
            {
                DriverBroken = true;
                var others = OtherBypassTools();
                LastError = "Не открывается драйвер zapret (WinDivert). Перезагрузи компьютер — обычно это помогает. " +
                            (others.Count > 0
                                ? $"Ещё на компьютере есть другой обход блокировок ({string.Join(", ", others)}) — его нужно выключить или удалить. "
                                : "") +
                            $"Подробности: {tail}";
                return false;
            }

            LastError = $"winws завершился с кодом {exitCode} на стратегии «{strategy.Id}»: {tail}";
            return false;
        }

        return true;
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        ChildProcesses.KillTree(process);
        process?.Dispose();
    }

    /// <summary>
    /// Другие программы обхода блокировок, установленные службой Windows: они держат свой драйвер WinDivert
    /// и мешают нашему. Наш winws мы и так закрываем перед запуском, а службы перезапускают свой сами.
    /// </summary>
    internal static IReadOnlyList<string> OtherBypassTools()
    {
        string[] markers = ["zapret", "goodbyedpi", "winws", "byedpi", "spoofdpi", "windivert14"];
        try
        {
            return System.ServiceProcess.ServiceController.GetServices()
                .Where(s => markers.Any(m => s.ServiceName.Contains(m, StringComparison.OrdinalIgnoreCase)
                                             || s.DisplayName.Contains(m, StringComparison.OrdinalIgnoreCase)))
                .Select(s => $"служба «{s.DisplayName}»")
                .ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Два winws одновременно мешают друг другу, поэтому перед запуском закрываем любые другие,
    /// в том числе от zapret-discord-youtube, если его запускали вручную.
    /// </summary>
    private static void KillStrayWinws()
    {
        foreach (var process in Process.GetProcessesByName(WinwsName))
        {
            using (process)
                ChildProcesses.KillTree(process);
        }
    }

    /// <summary>Стратегии Flowseal ссылаются на пользовательские списки; без файлов winws не стартует.</summary>
    private void EnsureUserLists()
    {
        // Те же заглушки, что создаёт service.bat Flowseal
        WriteIfMissing("list-general-user.txt", "domain.example.abc");
        WriteIfMissing("list-exclude-user.txt", "domain.example.abc");
        WriteIfMissing("ipset-exclude-user.txt", "203.0.113.113/32");

        void WriteIfMissing(string name, string content)
        {
            var path = Path.Combine(_listsDir, name);
            if (!File.Exists(path))
                File.WriteAllText(path, content + Environment.NewLine);
        }
    }
}
