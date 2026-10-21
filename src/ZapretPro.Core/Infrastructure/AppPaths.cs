namespace ZapretPro.Core.Infrastructure;

/// <summary>Где лежат файлы программы.</summary>
public static class AppPaths
{
    /// <summary>Папка с exe: рядом лежат zapret\ и tg-ws-proxy\.</summary>
    public static string InstallDir => AppContext.BaseDirectory;

    public static string ZapretDir => Path.Combine(InstallDir, "zapret");

    public static string TelegramProxyDir => Path.Combine(InstallDir, "tg-ws-proxy");

    /// <summary>Данные пользователя: ключ, настройки. Переживают переустановку.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OdnaKnopka");
}
