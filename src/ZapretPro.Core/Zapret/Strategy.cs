using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretPro.Core.Zapret;

/// <summary>
/// Стратегия обхода — набор аргументов winws. В <see cref="Args"/> пути заданы
/// шаблонами {bin} и {lists}, чтобы файл не зависел от папки установки.
/// </summary>
public sealed record Strategy(string Id, string Args)
{
    public string BuildArguments(string binDir, string listsDir) => Args
        .Replace("{bin}", WithTrailingSlash(binDir))
        .Replace("{lists}", WithTrailingSlash(listsDir));

    private static string WithTrailingSlash(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
}

/// <summary>strategies.json: генерируется tools\import-strategies.ps1 из стратегий Flowseal, обновляется без пересборки.</summary>
public sealed class StrategyFile
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("strategies")]
    public List<Strategy> Strategies { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static StrategyFile Load(string path)
    {
        StrategyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<StrategyFile>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            throw new InvalidDataException($"Не удалось прочитать список способов обхода {path}: {e.Message}", e);
        }

        var strategies = file?.Strategies.Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Args)).ToList();
        if (strategies is null || strategies.Count == 0)
            throw new InvalidDataException($"В {path} нет ни одной стратегии.");

        var duplicate = strategies.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"В {path} стратегия «{duplicate.Key}» встречается дважды.");

        return new StrategyFile { Source = file!.Source, Strategies = strategies };
    }
}
