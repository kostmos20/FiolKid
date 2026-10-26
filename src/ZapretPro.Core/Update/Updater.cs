using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Update;

/// <summary>Найденное обновление: уже проверенное по подписи.</summary>
public sealed record AvailableUpdate(Version Version, string? Notes, Uri ZipUrl, string Sha256, long Size);

/// <summary>
/// Обновления с GitHub (раздел Releases репозитория программы). Порядок такой:
/// 1) описание последнего выпуска и его подпись — проверяем подпись встроенным открытым ключом;
/// 2) если версия новее — качаем архив и сверяем размер и SHA-256 с подписанным описанием;
/// 3) распаковываем во временную папку; заменяет файлы уже новая версия (<see cref="UpdateApplier"/>),
///    после того как старая выключит zapret, VPN и прокси и закроется.
/// </summary>
public sealed class Updater
{
    public const string Repository = "kostmos20/FiolKid";

    /// <summary>Открытая часть ключа подписи выпусков (SubjectPublicKeyInfo, base64). Закрытая — только у автора.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEbeLheIlA8zz6l3L6vxpb+NBIjH/temfwrXE9QQG70o2JgUWOSAxbz/+2jLfZePaOSVO6ay12Jpre4wjOedzisw==";

    private readonly HttpClient _http;
    private readonly string _publicKey;
    private readonly string _workDir;
    private readonly string _latestUrl;

    public Updater() : this(CreateClient(), PublicKey, Path.Combine(AppPaths.DataDir, "update"))
    {
    }

    public Updater(HttpClient http, string publicKey, string workDir, string? latestUrl = null)
    {
        _http = http;
        _publicKey = publicKey;
        _workDir = workDir;
        _latestUrl = latestUrl ?? $"https://api.github.com/repos/{Repository}/releases/latest";
    }

    /// <summary>Версия программы (FiolKid.exe), а не этой библиотеки: у неё своего номера нет.</summary>
    public static Version Current =>
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly).GetName().Version is { } v
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
            : new Version(0, 0, 0);

    /// <summary>Есть ли версия новее <paramref name="current"/>. Null — нет (или выпусков ещё нет).</summary>
    /// <exception cref="UpdateException">Что-то не так: нет сети, подпись не сходится и т. п.</exception>
    public async Task<AvailableUpdate?> CheckAsync(Version current, CancellationToken ct)
    {
        Release? release;
        try
        {
            using var response = await _http.GetAsync(_latestUrl, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // ни одного выпуска
            response.EnsureSuccessStatusCode();
            release = await response.Content.ReadFromJsonAsync<Release>(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            throw new UpdateException($"GitHub не ответил: {e.Message}");
        }

        var assets = release?.Assets ?? [];
        var manifestUrl = assets.FirstOrDefault(a => a.Name == UpdateManifest.ManifestName)?.Url;
        var signatureUrl = assets.FirstOrDefault(a => a.Name == UpdateManifest.SignatureName)?.Url;
        if (manifestUrl is null || signatureUrl is null)
            throw new UpdateException("в выпуске нет подписанного описания — пропускаю");

        byte[] manifestBytes;
        string signature;
        try
        {
            manifestBytes = await _http.GetByteArrayAsync(manifestUrl, ct);
            signature = await _http.GetStringAsync(signatureUrl, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException($"описание выпуска не скачалось: {e.Message}");
        }

        if (!UpdateManifest.Verify(manifestBytes, signature, _publicKey))
            throw new UpdateException("подпись выпуска не сходится — это не официальное обновление, ставить не буду");
        var manifest = UpdateManifest.Parse(manifestBytes)
                       ?? throw new UpdateException("описание выпуска не читается");
        if (!Version.TryParse(manifest.Version, out var version))
            throw new UpdateException($"непонятная версия «{manifest.Version}»");
        if (version <= current)
            return null;

        var zipUrl = assets.FirstOrDefault(a => a.Name == manifest.File)?.Url
                     ?? throw new UpdateException($"в выпуске нет архива {manifest.File}");
        return new AvailableUpdate(version, manifest.Notes, zipUrl, manifest.Sha256, manifest.Size);
    }

    /// <summary>
    /// Скачивает архив, сверяет с подписанным описанием и распаковывает. Возвращает папку новой версии
    /// (в ней FiolKid.exe).
    /// </summary>
    public async Task<string> DownloadAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_workDir);
        CleanOld();
        var zip = Path.Combine(_workDir, $"FiolKid-{update.Version}.zip");
        try
        {
            using var response = await _http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await using (var file = File.Create(zip))
            {
                var buffer = new byte[256 * 1024];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    if (total > update.Size)
                        throw new UpdateException("архив больше, чем указано в описании");
                    progress?.Report(update.Size > 0 ? (double)total / update.Size : 0);
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException($"архив не скачался: {e.Message}");
        }

        if (new FileInfo(zip).Length != update.Size || UpdateManifest.Sha256Of(zip) != update.Sha256.ToLowerInvariant())
        {
            File.Delete(zip);
            throw new UpdateException("скачанный архив не совпадает с подписанным описанием — повреждён или подменён");
        }

        var target = Path.Combine(_workDir, update.Version.ToString());
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        ZipFile.ExtractToDirectory(zip, target);
        File.Delete(zip);

        // В архиве всё лежит в папке FiolKid
        var app = Directory.GetFiles(target, "FiolKid.exe", SearchOption.AllDirectories)
            .OrderBy(p => p.Length).FirstOrDefault()
            ?? throw new UpdateException("в архиве нет FiolKid.exe");
        return Path.GetDirectoryName(app)!;
    }

    /// <summary>Распакованные прошлые обновления больше не нужны (резервную копию не трогаем).</summary>
    private void CleanOld()
    {
        foreach (var dir in Directory.GetDirectories(_workDir).Where(d => Path.GetFileName(d) != UpdateApplier.BackupFolder))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub API без User-Agent отвечает ошибкой
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FiolKid/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, [property: JsonPropertyName("assets")] List<Asset>? Assets);

    private sealed record Asset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] Uri Url);
}

public sealed class UpdateException(string message) : Exception(message);
