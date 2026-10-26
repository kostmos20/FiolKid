using System.Diagnostics;
using System.Security.Cryptography;

namespace ZapretPro.Core.Update;

/// <summary>
/// Замена файлов программы новой версией. Работает уже новая версия, запущенная из временной папки
/// с ключом --apply-update: старая к этому времени выключила zapret, VPN и прокси и закрылась, поэтому
/// её файлы свободны. Копируются только изменившиеся файлы (драйвер WinDivert, если он не менялся,
/// не трогаем — он может быть занят до перезагрузки). Свои списки человека (*-user.txt) не перезаписываем.
/// Заменённые файлы сначала сохраняются в резервную копию; если что-то не скопировалось — откатываемся.
/// </summary>
public static class UpdateApplier
{
    public const string BackupFolder = "backup";

    // Создаётся при работе программы и хранит её данные — не наше дело его перезаписывать
    private static readonly string[] SkipDirectories = ["TgWsProxy_data"];

    public static void Apply(string sourceDir, string installDir, string backupDir, Action<string> log)
    {
        if (Directory.Exists(backupDir))
            Directory.Delete(backupDir, recursive: true);
        Directory.CreateDirectory(backupDir);

        var replaced = new List<string>();
        var created = new List<string>();
        try
        {
            foreach (var source in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, source);
                if (SkipDirectories.Any(d => relative.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var target = Path.Combine(installDir, relative);
                if (File.Exists(target))
                {
                    if (Path.GetFileName(target).EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase) || SameContent(source, target))
                        continue;
                    var backup = Path.Combine(backupDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: true);
                    replaced.Add(relative);
                }
                else
                    created.Add(relative);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyWithRetry(source, target);
            }
            log($"Обновление: заменено {replaced.Count}, добавлено {created.Count} файлов");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Обновление не удалось ({e.Message}) — возвращаю прежние файлы");
            foreach (var relative in replaced)
                TryCopy(Path.Combine(backupDir, relative), Path.Combine(installDir, relative));
            foreach (var relative in created)
                TryDelete(Path.Combine(installDir, relative));
            throw;
        }
    }

    /// <summary>
    /// Дождаться, пока закроются старая программа и её сторож DNS: пока они работают, FiolKid.exe занят.
    /// </summary>
    public static void WaitForExit(int pid, string installExe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit((int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds));
        }
        catch (ArgumentException)
        {
            // Уже закрылась
        }

        while (DateTime.UtcNow < deadline)
        {
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(installExe)).Any(p =>
            {
                using (p)
                    return string.Equals(Infrastructure.ChildProcesses.PathOf(p), installExe, StringComparison.OrdinalIgnoreCase);
            });
            if (!running)
                return;
            Thread.Sleep(300);
        }
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    private static void CopyWithRetry(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                // Файл ещё держит закрывающаяся программа или антивирус — подождём
                Thread.Sleep(500);
            }
        }
    }

    private static void TryCopy(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
