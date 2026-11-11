using System.Net;

namespace ZapretPro.Core.Games;

/// <summary>Готовый набор для игры: её домены и подсети серверов — через VPN с первого запуска.</summary>
public sealed record GamePreset(string Id, IReadOnlyList<string> Domains, IReadOnlyList<IPNetwork> Networks);

/// <summary>
/// Наборы для популярных игр, которым нужен VPN (Roblox, VRChat). Файл game-presets.txt: секции [id],
/// в них домены и подсети. Лежит в программе и обновляется вместе с остальными списками — если у игры
/// сменятся адреса, достаточно поправить файл в репозитории, без нового выпуска.
/// </summary>
public sealed class GamePresets
{
    public const string FileName = "game-presets.txt";

    private readonly IReadOnlyList<GamePreset> _presets;

    public GamePresets(IReadOnlyList<GamePreset> presets)
    {
        _presets = presets;
    }

    public static readonly GamePresets Empty = new([]);

    public IReadOnlyList<GamePreset> All => _presets;

    /// <summary>Свежая скачанная версия, если есть, иначе — та, что пришла с программой.</summary>
    public static GamePresets Load(string bundledDir, string? updatedDir)
    {
        foreach (var dir in new[] { updatedDir, bundledDir })
        {
            var path = dir is null ? null : Path.Combine(dir, FileName);
            if (path is null || !File.Exists(path))
                continue;
            try
            {
                var parsed = Parse(File.ReadAllText(path));
                if (parsed.All.Count > 0)
                    return parsed;
            }
            catch (IOException)
            {
            }
        }
        return Empty;
    }

    public static GamePresets Parse(string text)
    {
        var presets = new List<GamePreset>();
        string? id = null;
        var domains = new List<string>();
        var networks = new List<IPNetwork>();
        void Flush()
        {
            if (id is not null && domains.Count + networks.Count > 0)
                presets.Add(new GamePreset(id, domains.ToList(), networks.ToList()));
            domains.Clear();
            networks.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                id = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }
            if (id is null)
                continue;
            if (IPNetwork.TryParse(line, out var net) && net.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                networks.Add(net);
            else if (IPAddress.TryParse(line, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                networks.Add(new IPNetwork(ip, 32));
            else if (line.Contains('.') && !line.Any(char.IsWhiteSpace))
                domains.Add(line.TrimStart('.').ToLowerInvariant());
        }
        Flush();
        return new GamePresets(presets);
    }

    /// <summary>Набор для этой игры: по имени или пути («VRChat», «…\Roblox»), без учёта пробелов и регистра.</summary>
    public GamePreset? For(string name, string path)
    {
        var haystack = (name + "|" + path).Replace(" ", "").ToLowerInvariant();
        return _presets.FirstOrDefault(p => haystack.Contains(p.Id.Replace(" ", "")));
    }
}
