using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZapretPro.Core.Update;

/// <summary>
/// Описание выпуска, которое лежит рядом с архивом на GitHub: версия, имя и SHA-256 архива, что нового.
/// Подписано ключом автора (ECDSA P-256); открытая часть ключа встроена в программу. Поэтому
/// подменить обновление нельзя даже с доступом к репозиторию: без закрытого ключа подпись не сойдётся,
/// а без верной подписи программа архив не возьмёт.
/// </summary>
public sealed record UpdateManifest(string Version, string File, string Sha256, long Size, string? Notes, DateTimeOffset Published)
{
    public const string ManifestName = "FiolKid-update.json";
    public const string SignatureName = "FiolKid-update.json.sig";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json));

    public static UpdateManifest? Parse(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Подпись описания (base64) закрытым ключом автора.</summary>
    public static string Sign(byte[] manifest, ECDsa key) =>
        Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256));

    /// <summary>Описание подписано именно этим открытым ключом и не изменено ни на байт.</summary>
    public static bool Verify(byte[] manifest, string signature, string publicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(manifest, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static string Sha256Of(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
