using System.Net;
using System.Net.Sockets;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ZapretPro.Core.Games;

/// <summary>Куда подключаются процессы: TCP-соединения и UDP-отправки, сразу как случились.</summary>
public interface IGameTraffic : IDisposable
{
    /// <summary>Процесс <c>pid</c> открыл соединение (TCP) или отправил пакет (UDP) на адрес.</summary>
    event Action<int, IPAddress, bool>? Seen;

    /// <summary>Null — слежка запущена, иначе причина, почему нет.</summary>
    string? Start();

    void Stop();
}

/// <summary>
/// Слежка через ETW (события ядра «сеть TCP/IP», библиотека Microsoft TraceEvent). Для UDP другого способа
/// узнать адрес назначения нет: в таблице GetExtendedUdpTable его не бывает. Сессия ETW живёт в системе
/// отдельно от программы и переживает её падение — поэтому при запуске закрываем оставшуюся с нашим именем.
/// Работает только пока запущена игра из списка: событий много (на каждый пакет).
/// </summary>
public sealed class EtwGameTraffic : IGameTraffic
{
    public const string SessionName = "FiolKid-Games";

    private readonly object _lock = new();
    private TraceEventSession? _session;
    private Task? _pump;

    public event Action<int, IPAddress, bool>? Seen;

    /// <summary>Закрыть сессию, оставшуюся от упавшей программы: иначе новая с тем же именем не создастся.</summary>
    public static void StopLeftover()
    {
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
            {
                using var old = new TraceEventSession(SessionName, TraceEventSessionOptions.Attach);
                old.Stop(noThrow: true);
            }
        }
        catch (Exception)
        {
            // Нет прав или ETW недоступен — новая сессия скажет причину сама
        }
    }

    public string? Start()
    {
        lock (_lock)
        {
            if (_session is not null)
                return null;
            StopLeftover();
            try
            {
                var session = new TraceEventSession(SessionName) { StopOnDispose = true };
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                var kernel = session.Source.Kernel;
                kernel.TcpIpConnect += e => Raise(e.ProcessID, e.daddr, true);
                kernel.TcpIpReconnect += e => Raise(e.ProcessID, e.daddr, true);
                kernel.UdpIpSend += e => Raise(e.ProcessID, e.daddr, false);
                _session = session;
                _pump = Task.Run(() =>
                {
                    try
                    {
                        session.Source.Process();
                    }
                    catch (Exception)
                    {
                        // Слежка остановилась — игра дальше идёт по запомненным адресам
                    }
                });
                return null;
            }
            catch (Exception e)
            {
                // Любая беда ETW (нет прав, антивирус, нет нужной библиотеки) не должна ронять программу
                _session?.Dispose();
                _session = null;
                return e.Message;
            }
        }
    }

    private void Raise(int pid, IPAddress? address, bool tcp)
    {
        if (address is not { AddressFamily: AddressFamily.InterNetwork } || pid <= 0)
            return;
        try
        {
            Seen?.Invoke(pid, address, tcp);
        }
        catch (Exception)
        {
            // Ошибка в обработке одного соединения не должна останавливать слежку
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            var session = _session;
            _session = null;
            if (session is null)
                return;
            try
            {
                session.Stop(noThrow: true);
                session.Dispose();
            }
            catch (Exception)
            {
            }
            _pump?.Wait(TimeSpan.FromSeconds(3));
            _pump = null;
        }
    }

    public void Dispose() => Stop();
}
