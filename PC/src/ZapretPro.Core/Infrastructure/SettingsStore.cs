using System.Text.Json;

namespace ZapretPro.Core.Infrastructure;

public sealed class AppSettings
{
    /// <summary>Последняя стратегия zapret, при которой всё открывалось.</summary>
    public string? LastStrategyId { get; set; }

    /// <summary>
    /// Секрет локального прокси Telegram. Создаётся один раз: Telegram Desktop
    /// запоминает ссылку с ним, и смена секрета отключила бы Telegram.
    /// </summary>
    public string? TelegramSecret { get; set; }

    /// <summary>Была ли программа включена, когда её закрыли: при следующем запуске включится сама.</summary>
    public bool Enabled { get; set; }

    /// <summary>Какой способ VPN оказался быстрее, если вставлено несколько (имя профиля).</summary>
    public string? PreferredVpn { get; set; }

    /// <summary>Как выбирать способ обхода zapret (альт): см. <see cref="StrategyModes"/>. Null — «самый быстрый».</summary>
    public string? StrategyMode { get; set; }

    /// <summary>Режим с учётом умолчания: человек просил, чтобы программа сама брала самый быстрый альт.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveStrategyMode => StrategyMode ?? StrategyModes.Fastest;

    /// <summary>Альт, выбранный вручную (режим «manual»).</summary>
    public string? ManualStrategyId { get; set; }

    /// <summary>Последний замер скорости альтов: когда и что показал каждый.</summary>
    public DateTimeOffset? StrategyBenchmarkAt { get; set; }
    public List<StrategyBenchmarkEntry>? StrategyBenchmark { get; set; }

    /// <summary>Подписка: выбирать сервер замером скорости (true, по умолчанию) или брать выбранный вручную.</summary>
    public bool? ServerAutoSelect { get; set; }

    /// <summary>Сервер подписки, выбранный вручную, — по отпечатку <c>ProxyServer.Key</c> (без адреса и пароля).</summary>
    public string? ManualServerKey { get; set; }

    /// <summary>Новогоднее оформление: снег, гирлянда, таймер. Null — включено.</summary>
    public bool? NewYearTheme { get; set; }

    /// <summary>Последний замер серверов подписки (без адресов и паролей).</summary>
    public DateTimeOffset? ServerBenchmarkAt { get; set; }
    public List<Vpn.ServerScore>? ServerBenchmark { get; set; }
}

public static class StrategyModes
{
    /// <summary>Перебор по порядку, оставляем первый, при котором всё открылось (как было всегда).</summary>
    public const string FirstWorking = "auto";
    /// <summary>Замеряем все альты и берём самый быстрый из рабочих.</summary>
    public const string Fastest = "fastest";
    /// <summary>Только выбранный человеком альт, без перебора.</summary>
    public const string Manual = "manual";
}

public sealed record StrategyBenchmarkEntry(string Id, int Ok, int Total, double Milliseconds);

/// <summary>Настройки в JSON-файле. Битый или отсутствующий файл — просто настройки по умолчанию.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public SettingsStore() : this(Path.Combine(AppPaths.DataDir, "settings.json"))
    {
    }

    public SettingsStore(string path)
    {
        _path = path;
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                return new AppSettings();
            }
        }
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_lock)
        {
            var settings = Load();
            change(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
