using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ZapretPro.Core.Update;

namespace ZapretPro.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public UpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    [Fact]
    public void Embedded_public_key_is_a_valid_p256_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Updater.PublicKey), out _);
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void Manifest_signature_rejects_any_change_and_foreign_keys()
    {
        var manifest = new UpdateManifest("0.9.1", "FiolKid.zip", "ab", 10, "заметки", DateTimeOffset.UnixEpoch).ToBytes();
        var signature = UpdateManifest.Sign(manifest, _key);

        Assert.True(UpdateManifest.Verify(manifest, signature, PublicKey));

        var tampered = (byte[])manifest.Clone();
        tampered[^3] ^= 1;
        Assert.False(UpdateManifest.Verify(tampered, signature, PublicKey));

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateManifest.Verify(manifest, UpdateManifest.Sign(manifest, stranger), PublicKey));
        Assert.False(UpdateManifest.Verify(manifest, "не base64", PublicKey));
    }

    [Fact]
    public async Task Newer_signed_release_is_found_downloaded_and_unpacked()
    {
        var release = MakeRelease("0.9.1");
        var updater = new Updater(new HttpClient(release), PublicKey, Path.Combine(_dir, "work"), "https://api.test/latest");

        var update = await updater.CheckAsync(new Version(0, 9, 0), default);

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 9, 1), update.Version);
        Assert.Equal("Быстрее и красивее", update.Notes);
        Assert.Null(await updater.CheckAsync(new Version(0, 9, 1), default)); // та же версия — не предлагаем
        Assert.Null(await updater.CheckAsync(new Version(1, 0, 0), default)); // и старее — тоже

        var app = await updater.DownloadAsync(update, null, default);
        Assert.True(File.Exists(Path.Combine(app, "FiolKid.exe")));
        Assert.Equal("новая версия", File.ReadAllText(Path.Combine(app, "FiolKid.exe")));
    }

    [Fact]
    public async Task Release_signed_by_someone_else_is_refused()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = MakeRelease("0.9.1", signWith: stranger);
        var updater = new Updater(new HttpClient(release), PublicKey, Path.Combine(_dir, "work"), "https://api.test/latest");

        var error = await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(new Version(0, 9, 0), default));
        Assert.Contains("подпись", error.Message);
    }

    [Fact]
    public async Task Archive_replaced_after_signing_is_refused()
    {
        var release = MakeRelease("0.9.1");
        var updater = new Updater(new HttpClient(release), PublicKey, Path.Combine(_dir, "work"), "https://api.test/latest");
        var update = (await updater.CheckAsync(new Version(0, 9, 0), default))!;

        // Кто-то подменил архив, но размер подогнал
        var zip = release.Files["FiolKid.zip"];
        release.Files["FiolKid.zip"] = Enumerable.Repeat((byte)0x42, zip.Length).ToArray();

        var error = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(update, null, default));
        Assert.Contains("не совпадает", error.Message);
    }

    [Fact]
    public async Task No_releases_yet_means_no_update()
    {
        var updater = new Updater(new HttpClient(new FakeGitHub()), PublicKey, _dir, "https://api.test/latest");
        Assert.Null(await updater.CheckAsync(new Version(0, 9, 0), default));
    }

    [Fact]
    public void Applier_replaces_changed_files_keeps_user_lists_and_backs_up()
    {
        var source = Path.Combine(_dir, "new");
        var install = Path.Combine(_dir, "install");
        var backup = Path.Combine(_dir, "backup");
        Write(source, "FiolKid.exe", "v2");
        Write(source, @"zapret\lists\list-general-user.txt", "из новой версии");
        Write(source, @"zapret\bin\WinDivert64.sys", "драйвер");
        Write(source, @"vpn\new-list.txt", "новый файл");
        Write(source, @"tg-ws-proxy\TgWsProxy_data\config.json", "чужое");
        Write(install, "FiolKid.exe", "v1");
        Write(install, @"zapret\lists\list-general-user.txt", "мои сайты");
        Write(install, @"zapret\bin\WinDivert64.sys", "драйвер");

        UpdateApplier.Apply(source, install, backup, _ => { });

        Assert.Equal("v2", Read(install, "FiolKid.exe"));
        Assert.Equal("мои сайты", Read(install, @"zapret\lists\list-general-user.txt"));
        Assert.Equal("новый файл", Read(install, @"vpn\new-list.txt"));
        Assert.Equal("v1", Read(backup, "FiolKid.exe"));
        Assert.False(File.Exists(Path.Combine(backup, @"zapret\bin\WinDivert64.sys"))); // не менялся — не трогали
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));

    /// <summary>Выпуск на «GitHub»: архив с папкой FiolKid, подписанное описание и ответ API.</summary>
    private FakeGitHub MakeRelease(string version, ECDsa? signWith = null)
    {
        var zipPath = Path.Combine(_dir, $"release-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("FiolKid/FiolKid.exe").Open());
            writer.Write("новая версия");
        }
        var zip = File.ReadAllBytes(zipPath);
        var manifest = new UpdateManifest(version, "FiolKid.zip", Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(), zip.Length,
            "Быстрее и красивее", DateTimeOffset.UtcNow).ToBytes();
        var github = new FakeGitHub();
        github.Files["FiolKid.zip"] = zip;
        github.Files[UpdateManifest.ManifestName] = manifest;
        github.Files[UpdateManifest.SignatureName] = Encoding.ASCII.GetBytes(UpdateManifest.Sign(manifest, signWith ?? _key));
        github.Latest = $$"""
            {"tag_name":"v{{version}}","assets":[
              {"name":"FiolKid.zip","browser_download_url":"https://dl.test/FiolKid.zip"},
              {"name":"{{UpdateManifest.ManifestName}}","browser_download_url":"https://dl.test/{{UpdateManifest.ManifestName}}"},
              {"name":"{{UpdateManifest.SignatureName}}","browser_download_url":"https://dl.test/{{UpdateManifest.SignatureName}}"}]}
            """;
        return github;
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string? Latest { get; set; }
        public Dictionary<string, byte[]> Files { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.test")
                return Task.FromResult(Latest is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Latest, Encoding.UTF8, "application/json") });
            var name = Path.GetFileName(uri.AbsolutePath);
            return Task.FromResult(Files.TryGetValue(name, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
