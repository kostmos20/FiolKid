namespace ZapretPro.Core.Infrastructure;

/// <summary>
/// Журнал в файлах по дням (logs\fiolkid-2026-10-25.log), хранятся 7 последних. В окне видно только
/// последние 500 строк и только с запуска программы, а файл переживает перезапуск и аварию —
/// по нему можно восстановить, что было вчера. Ключей и ссылок в журнал программа не пишет.
/// </summary>
public sealed class FileLog
{
    private const int KeepDays = 7;

    private readonly string _dir;
    private readonly object _lock = new();
    private DateTime _cleanedFor;

    public FileLog() : this(Path.Combine(AppPaths.DataDir, "logs"))
    {
    }

    public FileLog(string dir)
    {
        _dir = dir;
    }

    public string Directory => _dir;

    public string TodayPath => Path.Combine(_dir, $"fiolkid-{DateTime.Now:yyyy-MM-dd}.log");

    public void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                File.AppendAllText(TodayPath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
                if (_cleanedFor != DateTime.Today)
                {
                    _cleanedFor = DateTime.Today;
                    Cleanup();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Журнал в файле — подспорье; не записался — программа работает дальше
            }
        }
    }

    private void Cleanup()
    {
        foreach (var old in System.IO.Directory.GetFiles(_dir, "fiolkid-*.log")
                     .OrderByDescending(f => f, StringComparer.Ordinal)
                     .Skip(KeepDays))
            File.Delete(old);
    }
}
