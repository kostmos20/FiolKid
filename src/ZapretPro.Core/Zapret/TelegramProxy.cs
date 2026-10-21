using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Zapret;

public interface ITelegramProxy
{
    bool IsRunning { get; }
    string? LastError { get; }
    Task<bool> StartAsync(CancellationToken ct);
    void Stop();
}

/// <summary>
/// Локальный MTProto-прокси для Telegram: официальный TgWsProxy_windows.exe от Flowseal
/// (https://github.com/Flowseal/tg-ws-proxy) в портативном режиме.
/// Конфиг и отметки «первый запуск уже был» кладём заранее, чтобы не всплывали его окна
/// и он не проверял обновления.
/// </summary>
public sealed class TelegramProxy : ITelegramProxy
{
    public const string Host = "127.0.0.1";
    public const int Port = 1443;

    private const string ExeName = "TgWsProxy_windows.exe";

    // Распаковка PyInstaller-exe при старте занимает несколько секунд
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    private readonly string _dir;
    private readonly string _secret;
    private Process? _process;

    public TelegramProxy(string secret) : this(AppPaths.TelegramProxyDir, secret)
    {
    }

    public TelegramProxy(string dir, string secret)
    {
        if (secret.Length != 32 || !secret.All(Uri.IsHexDigit))
            throw new ArgumentException("Секрет должен быть 32 hex-символа.", nameof(secret));
        _dir = dir;
        _secret = secret.ToLowerInvariant();
    }

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Ссылка, по которой Telegram Desktop добавляет прокси. Префикс dd — как у самого tg-ws-proxy.</summary>
    public string ProxyLink => $"tg://proxy?server={Host}&port={Port}&secret=dd{_secret}";

    public bool IsRunning => _process is { HasExited: false };

    public string? LastError { get; private set; }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        Stop();
        LastError = null;

        var exe = Path.Combine(_dir, ExeName);
        if (!File.Exists(exe))
        {
            LastError = $"Не найден {exe}. Возможно, его удалил антивирус.";
            return false;
        }

        KillOurStrayCopies(exe);
        if (await IsPortOpenAsync(ct))
        {
            LastError = $"Порт {Port} уже занят другой программой — скорее всего, отдельно запущенным TgWsProxy. Закрой её.";
            return false;
        }

        WriteConfig();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                WorkingDirectory = _dir,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastError = $"Не удалось запустить прокси Telegram: {e.Message}";
            process.Dispose();
            return false;
        }
        _process = process;

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                LastError = $"Прокси Telegram завершился сразу после запуска (код {process.ExitCode}).";
                _process = null;
                process.Dispose();
                return false;
            }
            if (await IsPortOpenAsync(ct))
                return true;
            await Task.Delay(500, ct);
        }

        LastError = $"Прокси Telegram не открыл порт {Port} за {StartupTimeout.TotalSeconds:0} с.";
        Stop();
        return false;
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        ChildProcesses.KillTree(process);
        process?.Dispose();
    }

    /// <summary>
    /// Отдаёт ссылку Telegram Desktop: он спросит «Подключить?», дальше ходит через прокси сам.
    /// Через explorer.exe — чтобы Telegram, если он не запущен, не стартовал с правами администратора.
    /// </summary>
    public void OpenInTelegram() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ProxyLink}\"") { UseShellExecute = false })?.Dispose();

    public static async Task<bool> IsPortOpenAsync(CancellationToken ct, int port = Port)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(Host, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Портативный режим tg-ws-proxy включается папкой TgWsProxy_data рядом с exe.</summary>
    internal void WriteConfig()
    {
        var data = Path.Combine(_dir, "TgWsProxy_data");
        Directory.CreateDirectory(data);

        var config = new JsonObject
        {
            ["host"] = Host,
            ["port"] = Port,
            ["secret"] = _secret,
            ["dc_ip"] = new JsonArray("2:149.154.167.220", "4:149.154.167.220"),
            ["verbose"] = false,
            ["buf_kb"] = 256,
            ["pool_size"] = 4,
            ["log_max_mb"] = 1,
            ["check_updates"] = false,
            ["cfproxy"] = true,
            ["h2"] = true,
            ["cfproxy_user_domain_enabled"] = false,
            ["cfproxy_user_domain"] = new JsonArray(),
            ["cfproxy_worker_enabled"] = false,
            ["cfproxy_worker_domain"] = new JsonArray(),
            ["force_test_dc"] = false,
            ["no_secure"] = false,
            ["appearance"] = "auto",
            ["language"] = "ru",
            ["autostart"] = false,
        };
        File.WriteAllText(Path.Combine(data, "config.json"), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Без этих отметок tg-ws-proxy при старте показывает окно-инструкцию и предупреждение про IPv6
        File.WriteAllText(Path.Combine(data, ".first_run_done_mtproto"), "");
        File.WriteAllText(Path.Combine(data, ".ipv6_warned"), "");
    }

    /// <summary>Копии из нашей папки, оставшиеся от прошлого запуска. Чужие экземпляры не трогаем.</summary>
    private static void KillOurStrayCopies(string exe)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                if (string.Equals(ChildProcesses.PathOf(process), exe, StringComparison.OrdinalIgnoreCase))
                    ChildProcesses.KillTree(process);
            }
        }
    }
}
