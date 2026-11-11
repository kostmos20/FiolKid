using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Dns;

/// <summary>
/// Записки сторожа DNS для журнала программы: сторож — отдельный процесс и писать в журнал сам не может.
/// Программа забирает их по таймеру.
/// </summary>
public static class DnsRecoveryNote
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "dns-guard-note.txt");

    public static void Add(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(FilePath, $"{DateTime.Now:dd.MM HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Забрать накопленное (и удалить) или null, если записок нет.</summary>
    public static string? Take()
    {
        if (!File.Exists(FilePath))
            return null;
        try
        {
            var text = File.ReadAllText(FilePath).Trim();
            File.Delete(FilePath);
            return text.Length == 0 ? null : $"Сторож DNS: {text}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
