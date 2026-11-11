using System.Diagnostics;
using System.Text.Json.Nodes;
using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Tests;

/// <summary>Закрепление сервера на время игры — на настоящем sing-box (без TUN, чтобы не нужны были права администратора).</summary>
public sealed class ServerPinTests
{
    private static string? FindSingBox()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "third_party", "sing-box", "sing-box.exe");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    [Fact]
    public async Task Pin_keeps_one_server_and_unpin_returns_to_auto()
    {
        var singBox = FindSingBox();
        if (singBox is null)
            return; // tools\fetch-deps.ps1 ещё не запускали

        // Как в SingBoxConfig: переключатель proxy → auto (urltest) или конкретный сервер; «серверы» — прямой выход
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "selector", ["tag"] = "proxy", ["outbounds"] = new JsonArray("auto", "1. A", "2. B"), ["default"] = "auto" },
                new JsonObject { ["type"] = "urltest", ["tag"] = "auto", ["outbounds"] = new JsonArray("1. A", "2. B") },
                new JsonObject { ["type"] = "direct", ["tag"] = "1. A" },
                new JsonObject { ["type"] = "direct", ["tag"] = "2. B" }),
            ["route"] = new JsonObject { ["final"] = "proxy" },
        };
        var (conf, port, secret) = SingBoxApi.WithApi(config.ToJsonString());
        var dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "pin.json");
        await File.WriteAllTextAsync(path, conf);

        using var process = Process.Start(new ProcessStartInfo(singBox, $"run -c \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            var api = new SingBoxApi(() => (port, secret));
            string? pinned = null;
            for (var i = 0; i < 50 && pinned is null; i++)
            {
                await Task.Delay(100);
                pinned = await api.PinAsync(default);
            }

            Assert.NotNull(pinned);
            Assert.Contains(pinned, new[] { "1. A", "2. B" });
            Assert.Equal(pinned, await Now(port, secret));

            await api.UnpinAsync(default);
            Assert.Equal("auto", await Now(port, secret));

            // Чужой пароль — API не слушается
            Assert.Null(await new SingBoxApi(() => (port, "wrong")).PinAsync(default));
        }
        finally
        {
            process.Kill();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<string?> Now(int port, string secret)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        return JsonNode.Parse(await http.GetStringAsync("proxies/proxy"))?["now"]?.GetValue<string>();
    }
}
