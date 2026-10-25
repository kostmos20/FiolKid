using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZapretPro.Core.Health;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Vpn;

/// <summary>Результат замера одного сервера подписки. Без адреса и ключей — можно показывать и хранить.</summary>
public sealed record ServerScore(string Key, string Name, string Protocol, double? PingMs, double? Mbps, string? Country, string? Problem)
{
    /// <summary>Сервер ответил.</summary>
    public bool Works => PingMs is not null;

    /// <summary>Нейросети с него работать не будут: выходит в интернет из России и т. п.</summary>
    public bool BadCountry => Country is not null && !HealthChecker.IsAiCountryOk(Country);

    /// <summary>
    /// Чем больше, тем лучше: скорость скачивания с поправкой на задержку. Сервер без замера скорости
    /// (до него очередь не дошла) идёт после всех замеренных — по задержке.
    /// </summary>
    public double Score => !Works ? 0
        : Mbps is { } mbps ? 1000 + mbps / (1 + PingMs!.Value / 300)
        : 1000 / (1 + PingMs!.Value);
}

/// <summary>
/// Замер серверов подписки до подключения. Один временный sing-box без адаптера и прав администратора:
/// на каждый сервер — свой локальный прокси (127.0.0.1:порт), через который мы и меряем. Сначала у всех
/// сразу задержку и страну выхода, потом по очереди скорость скачивания у лучших по задержке
/// (одновременно мерить скорость нельзя — серверы делили бы одну линию).
/// </summary>
public sealed class ServerBenchmark
{
    public const int MaxServers = 40;
    private const int SpeedTestCount = 5;
    private const int SpeedBytes = 4_000_000;
    private static readonly TimeSpan SpeedLimit = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

    private readonly string _exe;
    private readonly string _confPath;
    private readonly bool _protect;

    public ServerBenchmark() : this(Path.Combine(AppPaths.InstallDir, "sing-box"), ProtectedFolder.Root)
    {
    }

    /// <param name="protect">Конфиг с паролями серверов — в папку только для администраторов (false — для тестов).</param>
    public ServerBenchmark(string singBoxDir, string confDir, bool protect = true)
    {
        _exe = Path.Combine(singBoxDir, "sing-box.exe");
        _confPath = Path.Combine(confDir, "sing-box-benchmark.json");
        _protect = protect;
    }

    /// <summary>Адреса для проверки — можно подменить в тестах.</summary>
    public string PingUrl { get; init; } = "https://www.gstatic.com/generate_204";
    public string TraceUrl { get; init; } = "https://www.cloudflare.com/cdn-cgi/trace";
    public string SpeedUrl { get; init; } = $"https://speed.cloudflare.com/__down?bytes={SpeedBytes}";

    public event Action<string>? Log;

    public async Task<IReadOnlyList<ServerScore>> RunAsync(IReadOnlyList<ProxyServer> servers, CancellationToken ct)
    {
        if (!File.Exists(_exe))
            throw new InvalidOperationException($"Не найден {_exe}");
        var list = servers.Take(MaxServers).ToList();
        var ports = FreePorts(list.Count);
        var conf = BuildConfig(list, ports);
        if (_protect)
            ProtectedFolder.Write(Path.GetDirectoryName(_confPath)!, _confPath, conf);
        else
            File.WriteAllText(_confPath, conf);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_exe, $"run -c \"{_confPath}\" --disable-color")
            {
                WorkingDirectory = Path.GetDirectoryName(_exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        var output = new OutputTail();
        try
        {
            process.Start();
            output.Attach(process);
            await WaitForPortsAsync(process, ports, output, ct);

            Log?.Invoke($"Замеряю серверы подписки: {list.Count} шт.");
            using var gate = new SemaphoreSlim(8);
            var first = await Task.WhenAll(list.Select(async (server, i) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return await PingAsync(server, ports[i], ct);
                }
                finally
                {
                    gate.Release();
                }
            }));

            // Скорость — у лучших по задержке, по очереди; из «плохих» стран — в последнюю очередь
            var scores = first.ToList();
            var candidates = scores.Select((s, i) => (Score: s, Index: i)).Where(x => x.Score.Works)
                .OrderBy(x => x.Score.BadCountry).ThenBy(x => x.Score.PingMs).Take(SpeedTestCount).ToList();
            foreach (var (score, index) in candidates)
            {
                var mbps = await SpeedAsync(ports[index], ct);
                scores[index] = score with { Mbps = mbps };
            }
            return scores.OrderByDescending(s => s.Score).ToList();
        }
        finally
        {
            ChildProcesses.KillTree(process);
            try
            {
                File.Delete(_confPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Лучшие серверы для подключения: рабочие, не из «плохих» стран, по убыванию оценки.</summary>
    public static IReadOnlyList<string> Best(IEnumerable<ServerScore> scores, int count = 3)
    {
        var working = scores.Where(s => s.Works).OrderByDescending(s => s.Score).ToList();
        var good = working.Where(s => !s.BadCountry).ToList();
        return (good.Count > 0 ? good : working).Take(count).Select(s => s.Key).ToList();
    }

    internal static string BuildConfig(IReadOnlyList<ProxyServer> servers, IReadOnlyList<int> ports)
    {
        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();
        for (var i = 0; i < servers.Count; i++)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = $"in{i}",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = ports[i],
            });
            var outbound = servers[i].Outbound.DeepClone().AsObject();
            outbound["tag"] = $"s{i}";
            outbounds.Add(outbound);
            rules.Add(new JsonObject { ["inbound"] = new JsonArray($"in{i}"), ["action"] = "route", ["outbound"] = $"s{i}" });
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "local" }) },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["final"] = "direct",
                // Мимо туннеля FiolKid, если он сейчас поднят: меряем сами серверы, а не туннель
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local",
            },
        };
        return config.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true });
    }

    private async Task<ServerScore> PingAsync(ProxyServer server, int port, CancellationToken ct)
    {
        using var http = Client(port, RequestTimeout);
        double? best = null;
        string? problem = null;
        // Первый запрос дольше (рукопожатие с сервером), поэтому три раза и лучшее время
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await http.GetAsync(PingUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                // Не дозвонившись до сервера, sing-box сам отвечает ошибкой 5xx — это не ответ сайта
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"ответ {(int)response.StatusCode}");
                var ms = sw.Elapsed.TotalMilliseconds;
                if (best is null || ms < best)
                    best = ms;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                problem = e is TaskCanceledException ? "не ответил" : "не подключается";
                if (best is null && attempt == 1)
                    break; // два провала подряд — не тратим время
            }
        }

        string? country = null;
        if (best is not null)
        {
            try
            {
                var trace = await http.GetStringAsync(TraceUrl, ct);
                country = HealthChecker.ParseTraceCountry(trace);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
            }
        }
        return new ServerScore(server.Key, server.Name, server.Protocol, best is null ? null : Math.Round(best.Value), null, country,
            best is null ? problem : null);
    }

    private async Task<double?> SpeedAsync(int port, CancellationToken ct)
    {
        using var http = Client(port, SpeedLimit + TimeSpan.FromSeconds(4));
        try
        {
            using var response = await http.GetAsync(SpeedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(SpeedLimit);
            var buffer = new byte[64 * 1024];
            long total = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer, limit.Token)) > 0)
                    total += read;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Не скачалось за отведённое время — считаем по тому, что успело
            }
            var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.05);
            return total == 0 ? null : Math.Round(total * 8 / seconds / 1_000_000, 1);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static HttpClient Client(int port, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = timeout,
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return http;
    }

    private static async Task WaitForPortsAsync(Process process, IReadOnlyList<int> ports, OutputTail output, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"sing-box для замера завершился: {string.Join(" | ", output.Lines.TakeLast(3))}");
            if (await PortOpenAsync(ports[^1], ct))
                return;
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException("sing-box для замера не запустился за 10 с");
    }

    private static async Task<bool> PortOpenAsync(int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static List<int> FreePorts(int count)
    {
        var listeners = Enumerable.Range(0, count).Select(_ =>
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return l;
        }).ToList();
        var ports = listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToList();
        foreach (var l in listeners)
            l.Stop();
        return ports;
    }
}
