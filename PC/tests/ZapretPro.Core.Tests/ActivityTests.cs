using ZapretPro.Core.Health;

namespace ZapretPro.Core.Tests;

public sealed class ActivityTests
{
    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe", true)]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", true)]
    [InlineData(@"C:\Riot Games\VALORANT\live\VALORANT.exe", true)]
    [InlineData(@"C:\Users\Ваня\AppData\Local\Roblox\Versions\version-abc\RobloxPlayerBeta.exe", true)]
    // Лаунчеры и служебное — не игра, иначе перебор не случался бы никогда
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", false)]
    [InlineData(@"C:\Riot Games\Riot Client\RiotClientServices.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Battle.net.exe", false)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    public void Games_are_recognised_by_folder(string path, bool game) =>
        Assert.Equal(game, ZapretPro.Core.Games.GameMonitor.IsGamePath(path));
}
