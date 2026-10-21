using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Vpn;

/// <summary>Последний удачно скачанный ответ подписки — чтобы VPN заработал и без доступа к сайту подписки.</summary>
public sealed record CachedSubscription(string Body, Dictionary<string, string> Headers, DateTimeOffset FetchedAt);

/// <summary>
/// Ссылка-подписка (или сами ссылки vless:// и т. п.) и её последний ответ. Как и ключ, хранится
/// зашифрованным DPAPI: по ссылке любой может пользоваться чужим VPN, а в ответе — пароли серверов.
/// </summary>
public sealed class SubscriptionStore
{
    private static readonly byte[] Entropy = "FiolKid.Subscription.v1"u8.ToArray();

    private readonly string _sourcePath;
    private readonly string _cachePath;

    public SubscriptionStore() : this(AppPaths.DataDir)
    {
    }

    public SubscriptionStore(string dir)
    {
        _sourcePath = Path.Combine(dir, "subscription.bin");
        _cachePath = Path.Combine(dir, "subscription-cache.bin");
    }

    public bool HasSubscription => File.Exists(_sourcePath);

    public string? LoadSource() => Read(_sourcePath);

    public void SaveSource(string source)
    {
        Write(_sourcePath, source.Trim());
        // Новая подписка — старый ответ к ней не относится
        if (File.Exists(_cachePath))
            File.Delete(_cachePath);
    }

    public CachedSubscription? LoadCache()
    {
        var json = Read(_cachePath);
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<CachedSubscription>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveCache(CachedSubscription cache) => Write(_cachePath, JsonSerializer.Serialize(cache));

    public void Delete()
    {
        foreach (var path in new[] { _sourcePath, _cachePath })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Скачивает подписку. Отдельно от хранения — чтобы в тестах подменять сеть.</summary>
public sealed class SubscriptionClient
{
    // Сервисы подписок отдают стандартный формат (base64 со ссылками) клиентам вида v2rayN
    private const string UserAgent = "v2rayN/7.10.0";

    private readonly Func<string, CancellationToken, Task<(string Body, Dictionary<string, string> Headers)>> _fetch;

    public SubscriptionClient(Func<string, CancellationToken, Task<(string Body, Dictionary<string, string> Headers)>>? fetch = null)
    {
        _fetch = fetch ?? FetchAsync;
    }

    /// <summary>Похоже ли вставленное на подписку или на ссылки серверов.</summary>
    public static bool LooksLikeSubscription(string text)
    {
        var t = text.Trim();
        return t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || IsLinkList(t);
    }

    private static bool IsLinkList(string text) =>
        new[] { "vless://", "hysteria2://", "hy2://", "trojan://", "ss://" }.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Источник — ссылка на подписку (скачиваем) или сами ссылки серверов (разбираем как есть).</summary>
    public async Task<CachedSubscription> LoadAsync(string source, TimeProvider time, CancellationToken ct)
    {
        if (IsLinkList(source) && !source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return new CachedSubscription(source, new Dictionary<string, string>(), time.GetUtcNow());
        var (body, headers) = await _fetch(source.Trim(), ct);
        return new CachedSubscription(body, headers, time.GetUtcNow());
    }

    private static async Task<(string, Dictionary<string, string>)> FetchAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat<KeyValuePair<string, IEnumerable<string>>>(response.Content.Headers))
            headers[name.ToLowerInvariant()] = string.Join(",", values);
        return (await response.Content.ReadAsStringAsync(ct), headers);
    }
}
