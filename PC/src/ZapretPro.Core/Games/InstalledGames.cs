using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZapretPro.Core.Games;

/// <summary>Установленная игра, которую нашёл автопоиск. Path — папка игры.</summary>
public sealed record FoundGame(string Name, string Path, string Store);

/// <summary>
/// Ищет установленные игры, чтобы не выбирать .exe руками: библиотеки Steam (на всех дисках), Epic Games,
/// GOG, Roblox, Xbox и игры Riot, Blizzard, Ubisoft, EA и других — по списку установленных программ Windows.
/// Ничего не меняет, только читает. Любая ошибка в одном источнике не мешает остальным.
/// </summary>
public static class InstalledGames
{
    // Служебное Steam и лаунчеры — не игры
    private static readonly string[] NotGameNames =
    [
        "Steamworks", "Proton", "Steam Linux Runtime", "SteamVR", "Steam Audio", "Wallpaper Engine",
        "Battle.net", "Riot Client", "Ubisoft Connect", "EA app", "Origin", "Lesta Game Center",
        "Wargaming.net Game Center", "Epic Games Launcher",
    ];

    // Издатели, чьи игры ставятся мимо Steam и Epic
    private static readonly string[] Publishers =
    [
        "Riot Games", "Blizzard Entertainment", "Ubisoft", "Electronic Arts", "Wargaming", "Lesta",
        "miHoYo", "HoYoverse", "Cognosphere", "Gaijin", "Mojang",
    ];

    public static IReadOnlyList<FoundGame> Find()
    {
        var found = new List<FoundGame>();
        void Add(string store, Func<IEnumerable<FoundGame>> source)
        {
            try
            {
                found.AddRange(source());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                          or System.Security.SecurityException or ArgumentException)
            {
                // Источник не читается — остальные всё равно покажем
            }
        }

        Add("Steam", () => SteamRoots().SelectMany(FindSteam));
        Add("Epic Games", () => FindEpic(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests")));
        Add("GOG", FindGog);
        Add("Roblox", FindRoblox);
        Add("Xbox", FindXbox);
        Add("Windows", FindUninstall);
        return Clean(found);
    }

    /// <summary>Без служебного, без несуществующих папок и без повторов; по алфавиту.</summary>
    internal static IReadOnlyList<FoundGame> Clean(IEnumerable<FoundGame> games) => games
        .Where(g => g.Name.Length > 0 && g.Path.Length > 3)
        .Where(g => !NotGameNames.Any(n => g.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
        .Select(g => g with { Path = g.Path.TrimEnd('\\', '/').Replace('/', '\\') })
        .Where(g => Directory.Exists(g.Path))
        .GroupBy(g => g.Path, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    // ---------- Steam ----------

    private static IEnumerable<string> SteamRoots()
    {
        var roots = new[]
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
        };
        return roots.OfType<string>().Select(r => r.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Все библиотеки Steam (libraryfolders.vdf) и игры в них (appmanifest_*.acf).</summary>
    internal static IEnumerable<FoundGame> FindSteam(string steamRoot)
    {
        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(VdfValues(File.ReadAllText(vdf), "path"));

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
                continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var name = VdfValues(text, "name").FirstOrDefault();
                var dir = VdfValues(text, "installdir").FirstOrDefault();
                if (name is not null && dir is not null)
                    yield return new FoundGame(name, Path.Combine(apps, "common", dir), "Steam");
            }
        }
    }

    /// <summary>Значения ключа из текстового формата Valve: "ключ"  "значение".</summary>
    internal static IEnumerable<string> VdfValues(string text, string key) =>
        Regex.Matches(text, $"\"{Regex.Escape(key)}\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    // ---------- Epic ----------

    internal static IEnumerable<FoundGame> FindEpic(string manifestsDir)
    {
        if (!Directory.Exists(manifestsDir))
            yield break;
        foreach (var file in Directory.EnumerateFiles(manifestsDir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                continue;
            // Движок Unreal и плагины — не игры
            if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array
                && !categories.EnumerateArray().Any(c => c.GetString() == "games"))
                continue;
            if (Text("DisplayName") is { } name && Text("InstallLocation") is { } path)
                yield return new FoundGame(name, path, "Epic Games");
        }
    }

    // ---------- GOG, Roblox, Xbox, программы Windows ----------

    private static IEnumerable<FoundGame> FindGog()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null)
            yield break;
        foreach (var id in games.GetSubKeyNames())
        {
            using var game = games.OpenSubKey(id);
            if (game?.GetValue("gameName") is string name && game.GetValue("path") is string path)
                yield return new FoundGame(name, path, "GOG");
        }
    }

    /// <summary>Roblox обновляется в новую папку Versions\version-…, поэтому берём всю папку Roblox.</summary>
    private static IEnumerable<FoundGame> FindRoblox()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox"),
        };
        foreach (var dir in candidates)
        {
            var versions = Path.Combine(dir, "Versions");
            if (Directory.Exists(versions)
                && Directory.EnumerateFiles(versions, "RobloxPlayerBeta.exe", SearchOption.AllDirectories).Any())
                yield return new FoundGame("Roblox", dir, "Roblox");
        }
    }

    private static IEnumerable<FoundGame> FindXbox()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var dir = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(dir))
                continue;
            foreach (var game in Directory.EnumerateDirectories(dir))
                yield return new FoundGame(Path.GetFileName(game), game, "Xbox");
        }
    }

    private static IEnumerable<FoundGame> FindUninstall()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var hives = new[]
        {
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
            RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
        };
        foreach (var hive in hives)
        {
            using (hive)
            using (var list = hive.OpenSubKey(key))
            {
                if (list is null)
                    continue;
                foreach (var id in list.GetSubKeyNames())
                {
                    using var app = list.OpenSubKey(id);
                    if (app?.GetValue("Publisher") is string publisher
                        && Publishers.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase))
                        && app.GetValue("DisplayName") is string name
                        && app.GetValue("InstallLocation") is string path && path.Length > 3)
                        yield return new FoundGame(name, path.Trim('"'), publisher.Trim());
                }
            }
        }
    }
}
