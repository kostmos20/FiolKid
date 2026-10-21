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

    Task StopAsync();
}

/// <summary>
/// Запускает <see cref="SmartDns"/> и переключает на него Windows. Порядок важен: сначала занять порт,
/// потом переключать — чтобы ни секунды не остаться без DNS. Выключение — в обратном порядке.
/// </summary>
public sealed class DnsHijack : IDnsHijack
{
    private readonly SystemDns _system;
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

    public bool Active => _dns is not null;
    public string? LastError { get; private set; }

    /// <summary>Программа упала, не вернув DNS, — вернуть при следующем запуске, первым делом.</summary>
    public static async Task RecoverAsync(string ownAdapter)
    {
        var system = new SystemDns(BackupPath, ownAdapter);
        if (system.HasBackup)
            await system.RestoreAsync();
    }

    public async Task<bool> StartAsync(Func<string, RouteSet?> classify, Func<IReadOnlyList<IPAddress>, RouteSet, Task> onResolved,
        Func<bool> tunnelUp)
    {
        if (_dns is not null)
            return true;
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
        if (_dns is not null)
            _dns.Classify = classify;
    }

    public async Task StopAsync()
    {
        var dns = _dns;
        _dns = null;
        // Сначала вернуть DNS Windows, потом освободить порт
        await _system.RestoreAsync();
        if (dns is not null)
            await dns.DisposeAsync();
    }
}
