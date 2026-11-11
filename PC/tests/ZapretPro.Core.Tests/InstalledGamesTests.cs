using System.Net;
using ZapretPro.Core.Games;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Tests;

public sealed class InstalledGamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Steam_games_are_found_in_every_library()
    {
        var steam = Dir("Steam");
        var library = Dir("D", "SteamLibrary");
        File.WriteAllText(Path.Combine(Dir("Steam", "steamapps"), "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Replace(@"\", @"\\")}}" }
                "1" { "path" "{{library.Replace(@"\", @"\\")}}" }
            }
            """);
        Dir("Steam", "steamapps", "common", "dota 2 beta");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_570.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"570\"\n\t\"name\"\t\t\"Dota 2\"\n\t\"installdir\"\t\t\"dota 2 beta\"\n}");
        Dir("D", "SteamLibrary", "steamapps", "common", "VRChat");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_438100.acf"),
            "\"AppState\"\n{\n\t\"name\"\t\t\"VRChat\"\n\t\"installdir\"\t\t\"VRChat\"\n}");
        Dir("D", "SteamLibrary", "steamapps", "common", "Steamworks Shared");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_228980.acf"),
            "\"AppState\"\n{\n\t\"name\"\t\t\"Steamworks Common Redistributables\"\n\t\"installdir\"\t\t\"Steamworks Shared\"\n}");
        // Удалённая игра: манифест есть, папки нет
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_1.acf"),
            "\"AppState\"\n{\n\t\"name\"\t\t\"Gone\"\n\t\"installdir\"\t\t\"Gone\"\n}");

        var found = InstalledGames.Clean(InstalledGames.FindSteam(steam));

        Assert.Equal(["Dota 2", "VRChat"], found.Select(g => g.Name));
        Assert.Equal(Path.Combine(library, @"steamapps\common\VRChat"), found[1].Path);
    }

    [Fact]
    public void Epic_games_are_found_but_not_the_engine_or_unfinished_installs()
    {
        var manifests = Dir("Manifests");
        var fortnite = Dir("Epic Games", "Fortnite");
        var engine = Dir("Epic Games", "UE_5.3");
        File.WriteAllText(Path.Combine(manifests, "a.item"),
            $$"""{"DisplayName":"Fortnite","InstallLocation":"{{fortnite.Replace(@"\", @"\\")}}","AppCategories":["public","games"]}""");
        File.WriteAllText(Path.Combine(manifests, "b.item"),
            $$"""{"DisplayName":"Unreal Engine","InstallLocation":"{{engine.Replace(@"\", @"\\")}}","AppCategories":["engines"]}""");
        File.WriteAllText(Path.Combine(manifests, "c.item"),
            $$"""{"DisplayName":"Half","InstallLocation":"{{fortnite.Replace(@"\", @"\\")}}","bIsIncompleteInstall":true}""");

        var found = InstalledGames.Clean(InstalledGames.FindEpic(manifests));

        Assert.Equal(["Fortnite"], found.Select(g => g.Name));
    }

    [Fact]
    public void Presets_match_games_by_name_or_folder()
    {
        var presets = GamePresets.Parse("""
            # комментарий
            [roblox]
            roblox.com
            128.116.0.0/17
            [vrchat]
            vrchat.cloud
            мусор с пробелами
            """);

        Assert.Equal(["roblox", "vrchat"], presets.All.Select(p => p.Id));
        Assert.Equal([IPNetwork.Parse("128.116.0.0/17")], presets.All[0].Networks);
        Assert.Equal(["vrchat.cloud"], presets.All[1].Domains);
        Assert.Equal("vrchat", presets.For("VRChat", @"D:\SteamLibrary\steamapps\common\VRChat")?.Id);
        Assert.Equal("roblox", presets.For("Roblox", @"C:\Users\a\AppData\Local\Roblox")?.Id);
        Assert.Null(presets.For("Dota 2", @"C:\Steam\steamapps\common\dota 2 beta"));
    }

    [Fact]
    public void Bundled_presets_file_has_roblox_and_vrchat()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\data\vpn"));
        var presets = GamePresets.Load(dir, null);

        Assert.Contains(presets.All, p => p.Id == "roblox" && p.Networks.Count > 0);
        Assert.Contains(presets.All, p => p.Id == "vrchat" && p.Domains.Contains("vrchat.cloud"));
    }

    [Fact]
    public void Found_game_folders_count_as_games_for_game_mode()
    {
        var gog = @"E:\GOG Games\Witcher 3";
        var processes = new[] { new ProcessPaths.Entry(5, gog + @"\bin\witcher3.exe") };

        Assert.False(GameMonitor.Evaluate(processes, [], () => false).Playing);
        Assert.Equal("witcher3", GameMonitor.Evaluate(processes, [], () => false, [gog]).Name);
    }
}
