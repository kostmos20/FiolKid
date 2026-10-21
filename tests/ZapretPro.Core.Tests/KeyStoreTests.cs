using System.Text;
using ZapretPro.Core.Key;

namespace ZapretPro.Core.Tests;

public sealed class KeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));
    private string KeyPath => Path.Combine(_dir, "key.bin");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Saves_encrypted_and_loads_back()
    {
        var store = new KeyStore(KeyPath);
        var key = SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg"));

        Assert.False(store.HasKey);
        Assert.Null(store.Load());

        var parsed = store.Save(key);

        Assert.Equal(VpnProtocol.AmneziaWg, parsed.Protocol);
        Assert.True(store.HasKey);
        Assert.Equal(key, store.Load());

        var onDisk = File.ReadAllBytes(KeyPath);
        Assert.DoesNotContain(key[6..40], Encoding.UTF8.GetString(onDisk));
        Assert.DoesNotContain(key[6..40], Encoding.Latin1.GetString(onDisk));
    }

    [Fact]
    public void Stores_normalized_key()
    {
        var store = new KeyStore(KeyPath);
        var key = SyntheticKey.Build(SyntheticKey.Config("amnezia-xray", "amnezia-xray"));

        store.Save("  " + key[..30] + "\r\n" + key[30..] + "\n");

        Assert.Equal(key, store.Load());
    }

    [Fact]
    public void Replaces_previous_key()
    {
        var store = new KeyStore(KeyPath);
        store.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));
        var newKey = SyntheticKey.Build(SyntheticKey.Config("amnezia-xray", "amnezia-xray"));

        store.Save(newKey);

        Assert.Equal(newKey, store.Load());
        Assert.False(File.Exists(KeyPath + ".tmp"));
    }

    [Fact]
    public void Does_not_overwrite_good_key_with_invalid_one()
    {
        var store = new KeyStore(KeyPath);
        var key = SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg"));
        store.Save(key);

        Assert.Throws<KeyParseException>(() => store.Save("vpn://мусор"));

        Assert.Equal(key, store.Load());
    }

    [Fact]
    public void Corrupted_file_loads_as_no_key()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(KeyPath, [1, 2, 3, 4]);

        Assert.Null(new KeyStore(KeyPath).Load());
    }

    [Fact]
    public void Delete_removes_key()
    {
        var store = new KeyStore(KeyPath);
        store.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));

        store.Delete();

        Assert.False(store.HasKey);
        Assert.Null(store.Load());
    }
}
