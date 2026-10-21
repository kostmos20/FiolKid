using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Zapret;

namespace ZapretPro.Core.Tests;

public sealed class ZapretFilesTests : IDisposable
{
    private static readonly string StrategiesPath = Path.Combine(AppContext.BaseDirectory, "data", "strategies.json");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Bundled_strategies_are_valid()
    {
        var file = StrategyFile.Load(StrategiesPath);

        Assert.StartsWith("Flowseal/zapret-discord-youtube", file.Source);
        Assert.True(file.Strategies.Count >= 10);
        Assert.Equal("general", file.Strategies[0].Id);
        Assert.All(file.Strategies, s =>
        {
            Assert.DoesNotMatch("%[^%\\s]+%", s.Args);
            Assert.Contains("{lists}list-general.txt", s.Args);
            Assert.Contains("{lists}list-exclude.txt", s.Args);
        });
    }

    [Fact]
    public void Bundled_strategies_only_reference_files_that_exist_in_the_release()
    {
        var release = FindUp("third_party", "zapret");
        if (release is null)
            return; // tools\fetch-deps.ps1 ещё не запускали

        foreach (var strategy in StrategyFile.Load(StrategiesPath).Strategies)
        {
            var args = strategy.BuildArguments(Path.Combine(release, "bin"), Path.Combine(release, "lists"));
            foreach (Match m in Regex.Matches(args, "\"([^\"]+)\""))
            {
                var path = m.Groups[1].Value;
                if (path.EndsWith("-user.txt"))
                    continue; // создаёт ZapretManager при запуске
                Assert.True(File.Exists(path), $"{strategy.Id}: нет файла {path}");
            }
        }
    }

    [Fact]
    public void BuildArguments_substitutes_folders_with_trailing_slash()
    {
        var strategy = new Strategy("x", "--a=\"{bin}f.bin\" --b=\"{lists}l.txt\"");

        Assert.Equal(@"--a=""C:\Program Files\O\bin\f.bin"" --b=""C:\Program Files\O\lists\l.txt""",
            strategy.BuildArguments(@"C:\Program Files\O\bin", @"C:\Program Files\O\lists\"));
    }

    [Fact]
    public void StrategyFile_rejects_empty_and_duplicate_lists()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "s.json");

        File.WriteAllText(path, """{"strategies": []}""");
        Assert.Throws<InvalidDataException>(() => StrategyFile.Load(path));

        File.WriteAllText(path, """{"strategies": [{"id":"a","args":"x"},{"id":"a","args":"y"}]}""");
        Assert.Throws<InvalidDataException>(() => StrategyFile.Load(path));

        File.WriteAllText(path, "не json");
        Assert.Throws<InvalidDataException>(() => StrategyFile.Load(path));
    }

    [Fact]
    public async Task ZapretManager_reports_missing_winws()
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(_dir).FullName, "strategies.json"),
            """{"strategies": [{"id":"a","args":"x"}]}""");
        var zapret = new ZapretManager(_dir);

        Assert.False(await zapret.StartAsync(zapret.Strategies[0], default));
        Assert.Contains("winws.exe", zapret.LastError);
        Assert.False(zapret.IsRunning);
    }

    [Fact]
    public void Telegram_link_uses_dd_secret_on_localhost()
    {
        var secret = TelegramProxy.NewSecret();
        var proxy = new TelegramProxy(_dir, secret);

        Assert.Matches("^[0-9a-f]{32}$", secret);
        Assert.Equal($"tg://proxy?server=127.0.0.1&port=1443&secret=dd{secret}", proxy.ProxyLink);
        Assert.NotEqual(secret, TelegramProxy.NewSecret());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Telegram_rejects_bad_secret(string secret)
    {
        Assert.Throws<ArgumentException>(() => new TelegramProxy(_dir, secret));
    }

    [Fact]
    public void Telegram_config_enables_portable_mode_without_popups_and_update_checks()
    {
        var secret = TelegramProxy.NewSecret();
        new TelegramProxy(_dir, secret).WriteConfig();

        var data = Path.Combine(_dir, "TgWsProxy_data");
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "config.json")))!;
        Assert.Equal(secret, (string?)config["secret"]);
        Assert.Equal(1443, (int?)config["port"]);
        Assert.Equal("127.0.0.1", (string?)config["host"]);
        Assert.False((bool?)config["check_updates"]);
        Assert.False((bool?)config["autostart"]);
        Assert.True(File.Exists(Path.Combine(data, ".first_run_done_mtproto")));
        Assert.True(File.Exists(Path.Combine(data, ".ipv6_warned")));
    }

    [Fact]
    public async Task Telegram_port_check_sees_listening_socket()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(await TelegramProxy.IsPortOpenAsync(default, port));
        listener.Stop();
        Assert.False(await TelegramProxy.IsPortOpenAsync(default, port));
    }

    [Fact]
    public void Settings_survive_reload_and_corruption()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);

        store.Update(s => s.LastStrategyId = "ALT3");
        Assert.Equal("ALT3", new SettingsStore(path).Load().LastStrategyId);

        File.WriteAllText(path, "{битый");
        Assert.Null(store.Load().LastStrategyId);
        store.Update(s => s.TelegramSecret = "x");
        Assert.Equal("x", store.Load().TelegramSecret);
    }

    private static string? FindUp(params string[] relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. relative]);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
