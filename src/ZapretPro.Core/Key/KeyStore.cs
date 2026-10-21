using System.Security.Cryptography;
using System.Text;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Key;

/// <summary>
/// Хранит ключ vpn:// на диске, зашифрованным через DPAPI под текущим пользователем Windows.
/// В открытом виде ключ нигде не пишется.
/// </summary>
public sealed class KeyStore
{
    private static readonly byte[] Entropy = "OdnaKnopka.KeyStore.v1"u8.ToArray();

    private readonly string _path;

    public KeyStore() : this(DefaultPath)
    {
    }

    public KeyStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDir, "key.bin");

    public bool HasKey => File.Exists(_path);

    /// <summary>Проверяет ключ и сохраняет его вместо прежнего. Бросает <see cref="KeyParseException"/>, если ключ негодный.</summary>
    public ParsedKey Save(string key)
    {
        var parsed = KeyParser.Parse(key);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(KeyParser.Normalize(key)), Entropy, DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, _path, overwrite: true);
        return parsed;
    }

    /// <summary>Сохранённый ключ или null, если его нет либо он зашифрован под другим пользователем.</summary>
    public string? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
