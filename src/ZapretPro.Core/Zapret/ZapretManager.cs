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

    /// <summary>Последние строки вывода winws — для отчёта.</summary>
    public IReadOnlyList<string> RecentOutput => _output.Lines;

    public async Task<bool> StartAsync(Strategy strategy, CancellationToken ct)
    {
        Stop();
        KillStrayWinws();
        LastError = null;

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
            var tail = string.Join(" | ", _output.Lines.TakeLast(3));
            LastError = $"winws завершился с кодом {process.ExitCode} на стратегии «{strategy.Id}»: {tail}";
            _process = null;
            process.Dispose();
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
