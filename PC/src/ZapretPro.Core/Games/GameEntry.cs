namespace ZapretPro.Core.Games;

/// <summary>
/// Игра из списка «Игры через VPN»: exe или целая папка (тогда через VPN идёт всё, что из неё запущено, —
/// лаунчер и вспомогательные процессы). Плюс то, что программа узнала: куда игра ходила.
/// </summary>
public sealed class GameEntry
{
    /// <summary>Полный путь к exe или к папке игры.</summary>
    public string Path { get; set; } = "";

    /// <summary>Как показывать: имя exe без расширения или имя папки.</summary>
    public string Name { get; set; } = "";

    /// <summary>Адреса (IPv4), куда игра подключалась, — со следующего запуска сразу через VPN.</summary>
    public List<string> LearnedAddresses { get; set; } = [];

    /// <summary>Домены этих адресов (из ответов нашего DNS): адреса у игровых серверов меняются, имена — реже.</summary>
    public List<string> LearnedDomains { get; set; } = [];

    /// <summary>Сколько адресов и доменов помнить: больше — уже не «серверы игры», а весь интернет.</summary>
    public const int MaxLearned = 500;

    public static GameEntry FromPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        var isFolder = Directory.Exists(full);
        return new GameEntry
        {
            Path = full,
            Name = isFolder ? System.IO.Path.GetFileName(full) : System.IO.Path.GetFileNameWithoutExtension(full),
        };
    }

    /// <summary>Этот exe — эта игра: тот же файл или лежит внутри папки игры.</summary>
    public bool Matches(string exePath)
    {
        if (exePath.Equals(Path, StringComparison.OrdinalIgnoreCase))
            return true;
        return exePath.StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase)
               && !Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
