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
}

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
