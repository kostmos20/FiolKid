using System.Runtime.InteropServices;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Games;

/// <summary>Игра из списка «через VPN», которая сейчас запущена: её процессы и их exe.</summary>
public sealed record RunningGame(GameEntry Game, IReadOnlySet<int> Pids, IReadOnlyList<string> Exes);

/// <summary>Что сейчас идёт на компьютере.</summary>
public sealed record GameScan(string? Name, bool FullScreenOnly, IReadOnlyList<RunningGame> VpnGames)
{
    public static readonly GameScan None = new(null, false, []);

    public bool Playing => Name is not null;

    /// <summary>Для журнала и сторожа: «игра (dota2)», «полноэкранное приложение».</summary>
    public string? Busy => Name is null ? null : FullScreenOnly ? Name : $"игра ({Name})";

    /// <summary>Ключ для сравнения: изменилось ли что-то, о чём стоит сообщить.</summary>
    internal string Key => $"{Name}|{FullScreenOnly}|" +
                           string.Join(";", VpnGames.Select(g => g.Game.Path + ":" + string.Join(",", g.Pids.Order())));
}

/// <summary>
/// Раз в 2 секунды смотрит, какие процессы запущены. Игрой считается любой процесс из папок игр Steam, Epic,
/// Battle.net, Riot и других, игра из списка «через VPN» и любое полноэкранное приложение. Запускать игру через
/// FiolKid не нужно: человек запускает её как обычно, а программа замечает сама.
/// </summary>
public sealed class GameMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    // Игры лежат здесь; лаунчеры (Steam, Epic, Riot Client) — в своих папках и игрой не считаются
    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\",
        @"\Epic Games\",
        @"\Riot Games\",
        @"\Battle.net\",
        @"\Ubisoft Game Launcher\games\",
        @"\EA Games\",
        @"\GOG Galaxy\Games\",
        @"\XboxGames\",
        @"\Roblox\Versions\",
    ];

    private static readonly string[] NotGames =
    [
        @"\Epic Games\Launcher\",
        @"\Riot Games\Riot Client\",
        @"\steamapps\common\Steamworks Shared\",
        @"\Battle.net\Battle.net",
        @"\Roblox\Versions\RobloxStudio",
    ];

    private readonly Func<IReadOnlyList<GameEntry>> _games;
    private readonly Func<IReadOnlyList<ProcessPaths.Entry>> _processes;
    private readonly Func<bool> _fullScreen;
    private volatile IReadOnlyList<string> _foundFolders = [];

    /// <summary>Папки игр, которые нашёл автопоиск: игры не из стандартных папок (GOG, другой диск) — тоже игры.</summary>
    public void SetFoundFolders(IReadOnlyList<string> folders) => _foundFolders = folders;
    private readonly object _lock = new();
    private Timer? _timer;

    public GameMonitor(Func<IReadOnlyList<GameEntry>> games, Func<IReadOnlyList<ProcessPaths.Entry>>? processes = null,
        Func<bool>? fullScreen = null)
    {
        _games = games;
        _processes = processes ?? ProcessPaths.Snapshot;
        _fullScreen = fullScreen ?? FullScreen;
    }

    public GameScan Current { get; private set; } = GameScan.None;

    /// <summary>Что-то изменилось: (было, стало). Вызывается из фонового потока.</summary>
    public event Action<GameScan, GameScan>? Changed;

    public void Start() => _timer ??= new Timer(_ => Scan(), null, TimeSpan.Zero, Interval);

    public void Dispose() => _timer?.Dispose();

    /// <summary>Посмотреть сейчас. Список игр поменяли — тоже сюда, чтобы не ждать 2 секунды.</summary>
    public GameScan Scan()
    {
        GameScan before, after;
        lock (_lock)
        {
            before = Current;
            try
            {
                after = Evaluate(_processes(), _games(), _fullScreen, _foundFolders);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return before;
            }
            Current = after;
        }
        if (before.Key != after.Key)
            Changed?.Invoke(before, after);
        return after;
    }

    internal static GameScan Evaluate(IReadOnlyList<ProcessPaths.Entry> processes, IReadOnlyList<GameEntry> games, Func<bool> fullScreen,
        IReadOnlyList<string>? found = null)
    {
        found ??= [];
        var vpn = new List<RunningGame>();
        foreach (var game in games)
        {
            var mine = processes.Where(p => game.Matches(p.Path)).ToList();
            if (mine.Count > 0)
                vpn.Add(new RunningGame(game, mine.Select(p => p.Pid).ToHashSet(),
                    mine.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        // Имя для человека: игра из списка — по её имени, остальные — по exe
        var name = vpn.FirstOrDefault()?.Game.Name
                   ?? processes.Where(p => IsGamePath(p.Path) || found.Any(f => p.Path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)))
                       .Select(p => Path.GetFileNameWithoutExtension(p.Path)).FirstOrDefault();
        if (name is not null)
            return new GameScan(name, false, vpn);
        return fullScreen() ? new GameScan("полноэкранное приложение", true, vpn) : new GameScan(null, false, vpn);
    }

    public static bool IsGamePath(string path) =>
        GameFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))
        && !NotGames.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static bool FullScreen()
    {
        try
        {
            // 2 — занят (полноэкранное приложение), 3 — полноэкранная игра Direct3D, 4 — режим презентации
            return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
