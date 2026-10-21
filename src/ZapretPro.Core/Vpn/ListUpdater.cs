using System.Text;

namespace ZapretPro.Core.Vpn;

/// <summary>
/// Раз в сутки скачивает свежий список заблокированных в России сайтов (itdoginfo/allow-domains,
/// обновляется еженедельно). Не скачалось или пришло что-то странное — остаётся прежний список.
/// </summary>
public sealed class ListUpdater
{
    private const string Base = "https://raw.githubusercontent.com/itdoginfo/allow-domains/main/";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    // Файл → откуда собрать и сколько строк минимум должно быть, чтобы поверить, что это не обрезок
    private static readonly (string File, string[] Urls, int MinLines)[] Sources =
    [
        ("blocked-domains.txt", [Base + "Russia/inside-raw.lst"], 500),
        ("blocked-cidr.txt", [Base + "Subnets/IPv4/meta.lst", Base + "Subnets/IPv4/twitter.lst"], 10),
    ];

    private readonly string _dir;
    private readonly Func<string, CancellationToken, Task<string>> _download;
    private readonly TimeProvider _time;

    public ListUpdater(string dir, Func<string, CancellationToken, Task<string>>? download = null, TimeProvider? time = null)
    {
        _dir = dir;
        _download = download ?? DownloadAsync;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Возвращает true, если хотя бы один список обновился.</summary>
    public async Task<bool> RefreshIfStaleAsync(CancellationToken ct)
    {
        var changed = false;
        foreach (var (file, urls, minLines) in Sources)
        {
            var path = Path.Combine(_dir, file);
            if (File.Exists(path) && _time.GetUtcNow() - File.GetLastWriteTimeUtc(path) < MaxAge)
                continue;

            List<string> lines;
            try
            {
                var parts = await Task.WhenAll(urls.Select(u => _download(u, ct)));
                lines = parts
                    .SelectMany(p => p.Split('\n'))
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .Distinct()
                    .ToList();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                continue;
            }

            if (lines.Count < minLines || lines.Any(l => l.Length > 253 || l.Any(char.IsWhiteSpace)))
                continue;

            Directory.CreateDirectory(_dir);
            var text = new StringBuilder()
                .AppendLine($"# Скачано {_time.GetUtcNow():yyyy-MM-dd HH:mm} UTC из {string.Join(", ", urls)}")
                .AppendJoin(Environment.NewLine, lines)
                .AppendLine()
                .ToString();

            var old = File.Exists(path) ? File.ReadAllText(path) : null;
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
            changed |= old is null || StripHeader(old) != StripHeader(text);
        }
        return changed;
    }

    private static string StripHeader(string text) => string.Join('\n', text.Split('\n').Where(l => !l.StartsWith('#')));

    private static async Task<string> DownloadAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return await http.GetStringAsync(url, ct);
    }
}
