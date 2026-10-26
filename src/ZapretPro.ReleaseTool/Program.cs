using System.Security.Cryptography;
using System.Text;
using ZapretPro.Core.Update;

// Подпись выпусков FiolKid. Закрытый ключ хранится зашифрованным (DPAPI, только эта учётная запись Windows)
// вне репозитория: %LOCALAPPDATA%\FiolKid-release\signing-key.bin. Потеряешь ключ — новые выпуски
// старые копии программы не примут, их придётся один раз обновить вручную. Сделай резервную копию (export).
//
//   keygen                      создать ключ (один раз) и показать открытую часть для Updater.PublicKey
//   pubkey                      показать открытую часть
//   sign <zip> <версия> [файл]  подписать: рядом с архивом появятся FiolKid-update.json и .sig
//   export <файл.pem>           резервная копия закрытого ключа (хранить офлайн, никому не давать)

var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiolKid-release", "signing-key.bin");
var entropy = "FiolKid.ReleaseSigning.v1"u8.ToArray();
Console.OutputEncoding = Encoding.UTF8;

ECDsa LoadKey()
{
    if (!File.Exists(keyPath))
        throw new InvalidOperationException($"Ключа нет ({keyPath}). Сначала: keygen");
    var key = ECDsa.Create();
    key.ImportPkcs8PrivateKey(ProtectedData.Unprotect(File.ReadAllBytes(keyPath), entropy, DataProtectionScope.CurrentUser), out _);
    return key;
}

string PublicOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

try
{
    switch (args.FirstOrDefault())
    {
        case "keygen":
            if (File.Exists(keyPath))
            {
                Console.WriteLine("Ключ уже есть, новый не создаю. Открытая часть:");
                Console.WriteLine(PublicOf(LoadKey()));
                return 0;
            }
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
                File.WriteAllBytes(keyPath, ProtectedData.Protect(key.ExportPkcs8PrivateKey(), entropy, DataProtectionScope.CurrentUser));
                Console.WriteLine($"Ключ создан: {keyPath}");
                Console.WriteLine("Открытая часть (вставить в Updater.PublicKey):");
                Console.WriteLine(PublicOf(key));
            }
            return 0;

        case "pubkey":
            using (var key = LoadKey())
                Console.WriteLine(PublicOf(key));
            return 0;

        case "sign" when args.Length >= 3:
        {
            var zip = Path.GetFullPath(args[1]);
            var version = Version.Parse(args[2]).ToString(3);
            var notes = args.Length >= 4 ? File.ReadAllText(args[3]).Trim() : null;
            var manifest = new UpdateManifest(version, Path.GetFileName(zip), UpdateManifest.Sha256Of(zip), new FileInfo(zip).Length,
                notes, DateTimeOffset.UtcNow);
            var bytes = manifest.ToBytes();
            using var key = LoadKey();
            var signature = UpdateManifest.Sign(bytes, key);
            if (!UpdateManifest.Verify(bytes, signature, PublicOf(key)))
                throw new InvalidOperationException("Подпись не проверилась — что-то не так с ключом");
            if (PublicOf(key) != Updater.PublicKey)
                throw new InvalidOperationException("Этот ключ не тот, что встроен в программу (Updater.PublicKey) — выпуск никто не примет");
            var dir = Path.GetDirectoryName(zip)!;
            File.WriteAllBytes(Path.Combine(dir, UpdateManifest.ManifestName), bytes);
            File.WriteAllText(Path.Combine(dir, UpdateManifest.SignatureName), signature);
            Console.WriteLine($"Подписано: версия {version}, {manifest.Size / 1048576.0:0.0} МБ, sha256 {manifest.Sha256}");
            return 0;
        }

        case "export" when args.Length >= 2:
            using (var key = LoadKey())
                File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem());
            Console.WriteLine($"Закрытый ключ сохранён в {args[1]}. Храни его офлайн и никому не показывай.");
            return 0;

        default:
            Console.WriteLine("Команды: keygen | pubkey | sign <zip> <версия> [заметки.txt] | export <файл.pem>");
            return 2;
    }
}
catch (Exception e) when (e is InvalidOperationException or CryptographicException or IOException or FormatException or ArgumentException)
{
    Console.Error.WriteLine($"Ошибка: {e.Message}");
    return 1;
}
