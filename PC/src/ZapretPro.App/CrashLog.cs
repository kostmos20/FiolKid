using System.IO;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.App;

/// <summary>Последний сбой программы — в файл рядом с настройками, чтобы он попал в отчёт после перезапуска.</summary>
public static class CrashLog
{
    private static readonly object Lock = new();
    private static string FilePath => Path.Combine(AppPaths.DataDir, "crash.txt");

    public static void Write(string where, Exception e)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var text = $"{DateTime.Now:dd.MM HH:mm:ss}, версия {typeof(CrashLog).Assembly.GetName().Version}, {where}:{Environment.NewLine}{e}";
                File.WriteAllText(FilePath, text.Length > 6000 ? text[..6000] : text);
            }
        }
        catch (Exception)
        {
            // Не смогли записать — не ронять из-за этого ещё раз
        }
    }

    /// <summary>Сбой за последние 3 дня или null.</summary>
    public static string? Recent()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || DateTime.Now - info.LastWriteTime > TimeSpan.FromDays(3))
                return null;
            return File.ReadAllText(FilePath).TrimEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
