using System.Text.Json.Nodes;

namespace ZapretPro.Core.Key;

/// <summary>
/// Результат разбора ключа vpn://. Содержит секреты (в <see cref="Container"/>),
/// поэтому <see cref="ToString"/> намеренно выводит только имя контейнера.
/// </summary>
public sealed class ParsedKey
{
    internal ParsedKey(string containerName, string? hostName, JsonObject container, IReadOnlyList<string> allContainers)
    {
        ContainerName = containerName;
        Protocol = VpnProtocolInfo.FromContainerName(containerName);
        HostName = hostName;
        Container = container;
        AllContainers = allContainers;
    }

    public VpnProtocol Protocol { get; }

    /// <summary>Имя контейнера из ключа, например «amnezia-awg».</summary>
    public string ContainerName { get; }

    /// <summary>Адрес сервера. Не секрет, но в отчёт не попадает.</summary>
    public string? HostName { get; }

    /// <summary>Настройки выбранного контейнера, включая ключи шифрования. Наружу не выводить.</summary>
    public JsonObject Container { get; }

    /// <summary>Имена всех контейнеров в ключе.</summary>
    public IReadOnlyList<string> AllContainers { get; }

    public bool IsSupported => VpnProtocolInfo.IsSupported(Protocol);

    /// <summary>Понятная ошибка для пользователя, если протокол не поддерживается; иначе null.</summary>
    public string? UnsupportedReason => IsSupported
        ? null
        : Protocol == VpnProtocol.Unknown
            ? $"Ключ использует незнакомый способ подключения ({ContainerName}). Попроси ключ для AmneziaWG или XRay."
            : $"Ключ использует {VpnProtocolInfo.DisplayName(Protocol)}, программа его пока не поддерживает. Попроси ключ для AmneziaWG или XRay.";

    public override string ToString() => ContainerName;
}
