using System.Diagnostics;
using System.Security;
using System.Text;

namespace ZapretPro.Core.Infrastructure;

/// <summary>
/// Запуск вместе с Windows — задачей Планировщика «FiolKid» при входе пользователя, с наивысшими правами.
/// Через автозагрузку (реестр Run) программа с правами администратора каждый раз спрашивала бы
/// «Разрешить?», а из Планировщика — нет. Запускается с --tray: сразу в трей, без окна.
/// </summary>
public static class Autostart
{
    private const string TaskName = "FiolKid";

    public static bool IsEnabled() => Run("/Query /TN " + TaskName).Code == 0;

    public static string? Enable(string exePath)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        // Своё описание задачи: по умолчанию Планировщик не запустил бы её на ноутбуке от батареи
        // и через трое суток работы остановил бы программу
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>FiolKid: запуск при входе в Windows</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exePath)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"fiolkid-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var (code, output) = Run($"/Create /TN {TaskName} /XML \"{file}\" /F");
            return code == 0 ? null : output.Trim();
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static string? Disable()
    {
        var (code, output) = Run($"/Delete /TN {TaskName} /F");
        return code == 0 ? null : output.Trim();
    }

    private static (int Code, string Output) Run(string args)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return (process.ExitCode, output);
    }
}
