using System.Net;
using System.Net.Sockets;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Dns;

/// <summary>Наш DNS вместо системного — пока работает VPN.</summary>
public interface IDnsHijack
{
    bool Active { get; }
    string? LastError { get; }

    Task<bool> StartAsync(Func<string, RouteSet?> classify, Func<IReadOnlyList<IPAddress>, RouteSet, Task> onResolved, Func<bool> tunnelUp);

    /// <summary>Сменить, какие домены идут через VPN (включился или выключился запасной режим).</summary>
    void UpdateClassifier(Func<string, RouteSet?> classify);

    /// <summary>Последние сайты, отправленные в VPN, — для отчёта.</summary>
    IReadOnlyList<string> Recent { get; }

    /// <summary>Запросы через туннель часто теряются — VPN работает с перебоями.</summary>
    bool Unstable { get; }

    Task StopAsync();

    /// <summary>
    /// Свой DNS выключен, а DNS Windows вернуть до конца не вышло — попробовать ещё раз.
    /// Зовётся по таймеру и при смене сети.
    /// </summary>
    Task RetryRestoreAsync();
}

/// <summary>
/// Запускает <see cref="SmartDns"/> и переключает на него Windows. Порядок важен: сначала занять порт,
/// потом переключать — чтобы ни секунды не остаться без DNS. Выключение — в обратном порядке.
/// </summary>
public sealed class DnsHijack : IDnsHijack
{
    private readonly SystemDns _system;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SmartDns? _dns;

    public DnsHijack(string ownAdapter) : this(new SystemDns(BackupPath, ownAdapter))
    {
    }

    public DnsHijack(SystemDns system)
    {
        _system = system;
        _system.Log += m => Log?.Invoke(m);
    }

    public static string BackupPath => Path.Combine(AppPaths.DataDir, "dns-backup.json");

    public event Action<string>? Log;

    /// <summary>
    /// Наш DNS работает и Windows переключена на него. Файла с прежними DNS нет, а DNS считается включённым —
    /// значит, сторож увидел, что наш DNS не отвечает, и вернул DNS Windows сам.
    /// </summary>
    public bool Active => _dns is not null && _system.HasBackup;
    public string? LastError { get; private set; }

    /// <summary>
    /// Программа упала, не вернув DNS, — вернуть при следующем запуске, первым делом. Заодно чинит DNS,
    /// застрявший на адресе программы без файла с прежними настройками (так бывало до 0.9.12).
    /// </summary>
    public static async Task RecoverAsync(string ownAdapter, Action<string>? log = null)
    {
        var system = new SystemDns(BackupPath, ownAdapter);
        if (log is not null)
            system.Log += log;
        if (system.HasBackup)
            await system.RestoreAsync();
        else
            await system.ResetStrayAsync(() => SystemDns.LoopbackAnswers());
    }

    public async Task<bool> StartAsync(Func<string, RouteSet?> classify, Func<IReadOnlyList<IPAddress>, RouteSet, Task> onResolved,
        Func<bool> tunnelUp)
    {
        await _gate.WaitAsync();
        try
        {
            return await StartCoreAsync(classify, onResolved, tunnelUp);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartCoreAsync(Func<string, RouteSet?> classify, Func<IReadOnlyList<IPAddress>, RouteSet, Task> onResolved,
        Func<bool> tunnelUp)
    {
        if (Active)
            return true;
        if (_dns is not null)
        {
            // Сторож вернул DNS Windows: наш перестал отвечать. Старый выкидываем и запускаем заново
            Log?.Invoke("Свой DNS перестал отвечать, и DNS Windows был возвращён как был — запускаю свой DNS заново");
            var stale = _dns;
            _dns = null;
            await stale.DisposeAsync();
        }
        LastError = null;

        // Если остались настройки от прошлого раза — вернуть, чтобы запомнить настоящие, а не наши
        if (_system.HasBackup)
            await _system.RestoreAsync();
        var adapters = _system.Capture();
        var upstreams = adapters.SelectMany(a => a.EffectiveServers).Select(IPAddress.Parse)
            .Where(a => !IPAddress.IsLoopback(a)).Distinct().Select(a => new IPEndPoint(a, 53)).ToList();
        if (upstreams.Count == 0)
            upstreams = [new(IPAddress.Parse("77.88.8.8"), 53), new(IPAddress.Parse("8.8.8.8"), 53)];

        var dns = new SmartDns(upstreams) { Classify = classify, OnResolved = onResolved, TunnelUp = tunnelUp };
        dns.Log += m => Log?.Invoke(m);
        try
        {
            dns.Start();
        }
        catch (SocketException e)
        {
            await dns.DisposeAsync();
            LastError = $"Порт DNS (53) занят другой программой ({e.SocketErrorCode}) — сайты из списков будут открываться не полностью";
            return false;
        }

        try
        {
            await _system.ActivateAsync(adapters);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await dns.DisposeAsync();
            await _system.RestoreAsync();
            LastError = $"Не удалось переключить DNS Windows: {e.Message}";
            return false;
        }

        _dns = dns;
        return true;
    }

    public void UpdateClassifier(Func<string, RouteSet?> classify)
    {
        if (_dns is null)
            return;
        _dns.Classify = classify;
        // Windows помнит прежние ответы: без сброса, например, Discord после включения запасного режима
        // ещё минуты ходил бы на старые адреса мимо VPN
        SystemDns.FlushCache();
    }

    public IReadOnlyList<string> Recent => _dns?.Recent ?? [];

    public bool Unstable => _dns?.Unstable ?? false;

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var dns = _dns;
            _dns = null;
            // Сначала вернуть DNS Windows, потом освободить порт. Не вернулся до конца — файл с прежними
            // DNS остался, RetryRestoreAsync попробует ещё
            try
            {
                await _system.RestoreAsync();
            }
            finally
            {
                if (dns is not null)
                    await dns.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RetryRestoreAsync()
    {
        if (!await _gate.WaitAsync(0))
            return;
        try
        {
            if (_dns is null && _system.HasBackup)
                await _system.RestoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
