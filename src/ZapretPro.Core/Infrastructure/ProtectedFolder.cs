using System.Security.AccessControl;
using System.Security.Principal;

namespace ZapretPro.Core.Infrastructure;

/// <summary>Папка для конфигов с ключами VPN: доступ только у SYSTEM и администраторов, наследование прав отключено.</summary>
public static class ProtectedFolder
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FiolKid", "vpn");

    public static void Write(string dir, string path, string content)
    {
        var folder = Directory.CreateDirectory(dir);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        folder.SetAccessControl(security);
        File.WriteAllText(path, content);
    }
}
