using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Dns;

/// <summary>
/// Наш DNS на 127.0.0.1 и ::1. Вопросы о сайтах, которые идут через VPN (и любых их поддоменах),
/// задаёт DNS Cloudflare через туннель — провайдер не может подменить ответ, — и, прежде чем ответить
/// браузеру, добавляет полученные адреса в маршруты VPN. На IPv6-вопросы о них отвечает «адресов нет»:
/// туннель только IPv4, и браузер иначе ушёл бы по IPv6 мимо него. Остальные вопросы уходят
/// прежнему DNS (провайдера или роутера) как есть — российские сайты резолвятся как обычно.
/// Любая ошибка — пересылаем прежнему DNS: интернет не должен ломаться из-за нашего DNS.
/// </summary>
public sealed class SmartDns : IAsyncDisposable
{
    public static readonly IPEndPoint[] SecureUpstreams =
        [new(IPAddress.Parse("1.1.1.1"), 53), new(IPAddress.Parse("1.0.0.1"), 53)];

    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(2.5);

    private readonly int _port;
    private readonly IReadOnlyList<IPEndPoint> _normalUpstreams;
    private readonly IReadOnlyList<IPEndPoint> _secureUpstreams;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Socket> _udp = [];
    private readonly List<TcpListener> _tcp = [];
    private readonly List<Task> _loops = [];

    /// <summary>Через какой набор VPN идёт домен (null — мимо VPN). Меняется на лету, когда включается запасной режим.</summary>
    public Func<string, RouteSet?> Classify { get; set; } = _ => null;

    /// <summary>Узнали адреса сайта, идущего через VPN: добавить маршруты до того, как браузер к ним пойдёт.</summary>
    public Func<IReadOnlyList<IPAddress>, RouteSet, Task> OnResolved { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>Работает ли туннель: если нет, вопросы о сайтах из списков тоже идут прежнему DNS.</summary>
    public Func<bool> TunnelUp { get; set; } = () => false;

    public event Action<string>? Log;

    public SmartDns(IReadOnlyList<IPEndPoint> normalUpstreams, int port = 53, IReadOnlyList<IPEndPoint>? secureUpstreams = null)
    {
        _port = port;
        // Сам себе пересылать нельзя — получилась бы петля
        _normalUpstreams = normalUpstreams.Where(e => !(IPAddress.IsLoopback(e.Address) && e.Port == port)).ToList();
        _secureUpstreams = secureUpstreams ?? SecureUpstreams;
    }

    /// <summary>Занимает порт на 127.0.0.1 и ::1. Бросает SocketException, если порт занят другой программой.</summary>
    public void Start()
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                udp.Bind(new IPEndPoint(address, _port));
            }
            catch (SocketException) when (address.Equals(IPAddress.IPv6Loopback))
            {
                udp.Dispose(); // IPv6 выключен в системе — хватит и IPv4
                continue;
            }
            _udp.Add(udp);
            _loops.Add(UdpLoopAsync(udp));

            var tcp = new TcpListener(address, _port);
            tcp.Start();
            _tcp.Add(tcp);
            _loops.Add(TcpLoopAsync(tcp));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var socket in _udp)
            socket.Dispose();
        foreach (var listener in _tcp)
            listener.Stop();
        try
        {
            await Task.WhenAll(_loops);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    /// <summary>Ответ на один запрос. Отдельно от сокетов — чтобы проверять тестами.</summary>
    internal async Task<byte[]> HandleAsync(byte[] query, CancellationToken ct)
    {
        if (!DnsMessage.TryReadQuestion(query, out var name, out var type, out _))
            return await ForwardAsync(query, _normalUpstreams, ct) ?? DnsMessage.ServerFailure(query);

        var set = TunnelUp() ? Classify(name) : null;
        if (set is null)
            return await ForwardAsync(query, _normalUpstreams, ct) ?? DnsMessage.ServerFailure(query);

        // Только IPv4 через туннель: IPv6 и HTTPS-записи (в них подсказки адресов IPv6) — пусто
        if (type is DnsMessage.TypeAaaa or DnsMessage.TypeHttps)
            return DnsMessage.EmptyAnswer(query);

        var answer = await ForwardAsync(query, _secureUpstreams, ct);
        var viaVpn = answer is not null;
        if (answer is null)
        {
            // Туннель ответил через раз — адрес берём у обычного DNS, но сайт всё равно ведём в VPN:
            // иначе, например, ChatGPT увидел бы российский адрес
            NoteSecureFailure(name);
            answer = await ForwardAsync(query, _normalUpstreams, ct);
            if (answer is null)
                return DnsMessage.ServerFailure(query);
        }

        var addresses = DnsMessage.ReadARecords(answer);
        Remember($"{name} → {(addresses.Count == 0 ? "нет адресов" : string.Join(",", addresses))} ({set})" +
                 (viaVpn ? "" : " [адрес от DNS провайдера]"));
        if (addresses.Count > 0)
        {
            try
            {
                await OnResolved(addresses, set.Value);
            }
            catch (Exception e)
            {
                Log?.Invoke($"Не удалось добавить маршрут для {name}: {e.Message}");
            }
        }
        return answer;
    }

    // Сколько раз за последние минуты DNS через VPN не ответил: много — значит, туннель работает с перебоями
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LogQuietPeriod = TimeSpan.FromMinutes(10);
    private const int UnstableAfter = 5;
    private readonly Queue<DateTime> _secureFailures = new();
    private DateTime _lastFailureLog = DateTime.MinValue;
    private DateTime _lastUnstableLog = DateTime.MinValue;

    /// <summary>DNS через VPN часто не отвечает — туннель работает с перебоями.</summary>
    public bool Unstable
    {
        get
        {
            lock (_secureFailures)
            {
                Prune(DateTime.UtcNow);
                return _secureFailures.Count >= UnstableAfter;
            }
        }
    }

    /// <summary>
    /// Не пишем в журнал каждый промах (их бывают десятки в минуту): первый — и одно предупреждение,
    /// если промахов стало много.
    /// </summary>
    private void NoteSecureFailure(string name)
    {
        var now = DateTime.UtcNow;
        string? message = null;
        lock (_secureFailures)
        {
            _secureFailures.Enqueue(now);
            Prune(now);
            if (_secureFailures.Count >= UnstableAfter && now - _lastUnstableLog >= LogQuietPeriod)
            {
                _lastUnstableLog = now;
                message = "VPN-сервер отвечает с перебоями: запросы через туннель часто теряются. Чаще всего так бывает, " +
                          "когда этот же ключ включён ещё на одном устройстве — на каждое устройство нужен свой ключ.";
            }
            else if (now - _lastFailureLog >= LogQuietPeriod)
            {
                _lastFailureLog = now;
                message = $"DNS через VPN не ответил ({name}) — адрес взят у обычного DNS, сайт всё равно идёт через VPN";
            }
        }
        if (message is not null)
            Log?.Invoke(message);
    }

    private void Prune(DateTime now)
    {
        while (_secureFailures.Count > 0 && now - _secureFailures.Peek() > FailureWindow)
            _secureFailures.Dequeue();
    }

    private readonly Queue<string> _recent = new();

    /// <summary>Последние вопросы о сайтах через VPN — для отчёта: видно, что куда пошло.</summary>
    public IReadOnlyList<string> Recent
    {
        get
        {
            lock (_recent)
                return _recent.ToArray();
        }
    }

    private void Remember(string line)
    {
        lock (_recent)
        {
            _recent.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
            while (_recent.Count > 20)
                _recent.Dequeue();
        }
    }

    /// <summary>Спрашивает вышестоящие DNS по очереди; null — не ответил никто.</summary>
    private async Task<byte[]?> ForwardAsync(byte[] query, IReadOnlyList<IPEndPoint> upstreams, CancellationToken ct)
    {
        foreach (var upstream in upstreams)
        {
            try
            {
                var answer = await AskUdpAsync(query, upstream, ct);
                if (answer is not null && DnsMessage.IsTruncated(answer))
                    answer = await AskTcpAsync(query, upstream, ct) ?? answer;
                if (answer is not null)
                    return answer;
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Следующий сервер
            }
        }
        return null;
    }

    private static async Task<byte[]?> AskUdpAsync(byte[] query, IPEndPoint upstream, CancellationToken ct)
    {
        using var socket = new Socket(upstream.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(UpstreamTimeout);
        await socket.SendToAsync(query, SocketFlags.None, upstream, timeout.Token);
        var buffer = new byte[4096];
        while (true)
        {
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, upstream, timeout.Token);
            if (received.ReceivedBytes >= 12 && DnsMessage.Id(buffer) == DnsMessage.Id(query))
                return buffer[..received.ReceivedBytes];
        }
    }

    private static async Task<byte[]?> AskTcpAsync(byte[] query, IPEndPoint upstream, CancellationToken ct)
    {
        using var client = new TcpClient(upstream.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(UpstreamTimeout);
        await client.ConnectAsync(upstream, timeout.Token);
        var stream = client.GetStream();
        await WriteFramedAsync(stream, query, timeout.Token);
        return await ReadFramedAsync(stream, timeout.Token);
    }

    private async Task UdpLoopAsync(Socket socket)
    {
        var buffer = new byte[4096];
        var any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
            }
            catch (SocketException)
            {
                continue; // ICMP «порт недоступен» от прошлого ответа — не повод останавливаться
            }
            var query = buffer[..received.ReceivedBytes];
            var client = received.RemoteEndPoint;
            _ = Task.Run(async () =>
            {
                try
                {
                    var answer = await HandleAsync(query, _stop.Token);
                    await socket.SendToAsync(answer, SocketFlags.None, client, _stop.Token);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException
                                          or ArgumentException)
                {
                }
            });
        }
    }

    private async Task TcpLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(_stop.Token);
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var query = await ReadFramedAsync(stream, _stop.Token);
                        if (query is not null)
                            await WriteFramedAsync(stream, await HandleAsync(query, _stop.Token), _stop.Token);
                    }
                    catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ArgumentException)
                    {
                    }
                }
            });
        }
    }

    /// <summary>DNS по TCP: 2 байта длины, затем сообщение.</summary>
    private static async Task<byte[]?> ReadFramedAsync(Stream stream, CancellationToken ct)
    {
        var length = new byte[2];
        await stream.ReadExactlyAsync(length, ct);
        var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await stream.ReadExactlyAsync(message, ct);
        return message.Length >= 12 ? message : null;
    }

    private static async Task WriteFramedAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var framed = new byte[2 + message.Length];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)message.Length);
        message.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct);
    }
}
