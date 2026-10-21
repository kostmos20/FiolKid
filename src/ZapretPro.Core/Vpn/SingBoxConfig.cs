using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZapretPro.Core.Vpn;

/// <summary>
/// Конфиг sing-box для туннеля по подписке. Как и у AmneziaWG, адаптер «FiolKid» поднимается без
/// своих маршрутов (auto_route = false): что пускать в туннель, решает программа (TunnelRoutes и наш DNS).
/// Всё, что попало в адаптер, уходит через лучший из серверов подписки (urltest сам меряет задержку
/// и переключается, если сервер перестал отвечать).
/// </summary>
public static class SingBoxConfig
{
    // Сколько серверов держать в автовыборе: больше — дольше и шумнее проверки
    public const int MaxServers = 20;

    public static string Build(IReadOnlyList<ProxyServer> servers, string adapter)
    {
        if (servers.Count == 0)
            throw new ArgumentException("Нет ни одного сервера", nameof(servers));

        var outbounds = new JsonArray();
        var tags = new JsonArray();
        for (var i = 0; i < servers.Count; i++)
        {
            var outbound = servers[i].Outbound.DeepClone().AsObject();
            outbound["tag"] = $"s{i}";
            outbounds.Add(outbound);
            tags.Add($"s{i}");
        }
        outbounds.Insert(0, new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = "auto",
            ["outbounds"] = tags,
            ["url"] = "https://www.gstatic.com/generate_204",
            ["interval"] = "3m",
            // Переходить на другой сервер, только если он быстрее на заметную величину
            ["tolerance"] = 80,
        });
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            // Имена серверов подписки резолвит система (наш DNS отдаст их прежнему DNS — они не в списках)
            ["dns"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "local" }) },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = adapter,
                ["address"] = new JsonArray("172.19.0.1/30"),
                ["mtu"] = 1400,
                ["auto_route"] = false,
                ["strict_route"] = false,
                ["stack"] = "gvisor",
            }),
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["final"] = "auto",
                // Соединения к серверам подписки — мимо туннеля, через обычный интернет (иначе петля)
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local",
            },
        };
        // На основе стандартных настроек: значения, созданные из строк в списках, без них не записываются
        return config.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true });
    }

    /// <summary>
    /// Какие серверы взять в автовыбор: сначала VLESS Reality (лучше всего обходит блокировки), потом
    /// Hysteria2, потом остальные. Внутри группы — случайно, чтобы не все пользователи сидели на первых.
    /// </summary>
    public static IReadOnlyList<ProxyServer> Select(IReadOnlyList<ProxyServer> servers, int max = MaxServers, Random? random = null)
    {
        random ??= Random.Shared;
        return servers
            .GroupBy(s => s.Preference)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(_ => random.Next()))
            .Take(max)
            .ToList();
    }
}
