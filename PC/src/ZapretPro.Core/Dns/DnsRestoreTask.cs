using System.Diagnostics;
using System.Text;

namespace ZapretPro.Core.Dns;

/// <summary>Страховка: вернуть DNS Windows, даже если FiolKid больше не запустят.</summary>
public interface IDnsRestoreTask
{
    Task InstallAsync(string backupPath);
    Task RemoveAsync();
}

/// <summary>
/// Задача Планировщика «при включении компьютера» от имени SYSTEM — живёт, пока DNS переключён на программу.
/// Компьютер выключили, батарея села, синий экран — сторож (--dns-guard) умирает вместе с программой, и
/// без неё DNS вернул бы только следующий запуск FiolKid. А если автозапуск выключен или папку программы
/// удалили (сборка переносная), интернета не было бы совсем. Задача не зависит от FiolKid.exe: это
/// PowerShell-скрипт прямо в задаче, который читает файл с прежними DNS, возвращает их и удаляет себя.
/// </summary>
public sealed class DnsRestoreTask : IDnsRestoreTask
{
    public const string TaskName = "FiolKid - возврат DNS";

    public async Task InstallAsync(string backupPath)
    {
        var xml = Path.Combine(Path.GetTempPath(), $"fiolkid-dns-task-{Environment.ProcessId}.xml");
        // Планировщик ждёт XML в UTF-16, как в объявлении
        await File.WriteAllTextAsync(xml, BuildXml(backupPath), Encoding.Unicode);
        try
        {
            var (code, output) = await SchtasksAsync("/Create", "/TN", TaskName, "/XML", xml, "/F");
            if (code != 0)
                throw new InvalidOperationException(output);
        }
        finally
        {
            File.Delete(xml);
        }
    }

    public async Task RemoveAsync() => await SchtasksAsync("/Delete", "/TN", TaskName, "/F");

    internal static string BuildXml(string backupPath)
    {
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(backupPath)));
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>FiolKid</Author>
                <Description>Возвращает DNS Windows, если FiolKid не успел вернуть его сам (выключили компьютер). Удаляется сама, когда DNS вернулся.</Description>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT20S</Delay>
                </BootTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>powershell.exe</Command>
                  <Arguments>-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {command}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// Скрипт задачи. Делает то же, что <see cref="SystemDns.RestoreAsync"/>: netsh по нынешнему имени адаптера
    /// (найденному по ID), проверка по реестру, не встало — запись в реестр напрямую. Всё вернулось — удаляет
    /// файл и задачу; нет — остаётся до следующего включения.
    /// </summary>
    internal static string BuildScript(string backupPath) => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $file = '{{backupPath.Replace("'", "''")}}'
        $task = '{{TaskName}}'
        # Программа запущена — DNS вернёт она сама
        if (Get-Process -Name FiolKid -ErrorAction SilentlyContinue) { exit }
        if (-not (Test-Path -LiteralPath $file)) { schtasks.exe /Delete /TN $task /F 2>$null | Out-Null; exit }

        function Norm($v) { (@("$v" -split '[, ]') | Where-Object { $_ }) -join ',' }

        function Restore-Family($family, $reg, $id, $name, $servers) {
            $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$reg\Parameters\Interfaces\$id"
            if (-not (Test-Path -LiteralPath $key)) { return $true }
            $want = (@($servers) | Where-Object { $_ }) -join ','
            if ($name) {
                if ($want) {
                    $list = $want -split ','
                    netsh.exe interface $family set dnsservers "name=$name" source=static "address=$($list[0])" register=primary validate=no | Out-Null
                    for ($i = 1; $i -lt $list.Count; $i++) {
                        netsh.exe interface $family add dnsservers "name=$name" "address=$($list[$i])" "index=$($i + 1)" validate=no | Out-Null
                    }
                } else {
                    netsh.exe interface $family set dnsservers "name=$name" source=dhcp | Out-Null
                }
            }
            $now = Norm (Get-ItemProperty -LiteralPath $key -Name NameServer).NameServer
            if ($now -ne $want) {
                Set-ItemProperty -LiteralPath $key -Name NameServer -Value $want -Type String
                $now = Norm (Get-ItemProperty -LiteralPath $key -Name NameServer).NameServer
            }
            return ($now -eq $want)
        }

        # Без @(...): в PowerShell 5 ConvertFrom-Json отдаёт массив одним объектом, foreach его и так переберёт
        try { $adapters = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $adapters = $null }
        if ($null -eq $adapters) { exit }
        $ok = $true
        foreach ($a in $adapters) {
            $name = (Get-NetAdapter -IncludeHidden | Where-Object { "$($_.InterfaceGuid)" -eq "$($a.Id)" } | Select-Object -First 1).Name
            if (-not $name) { $name = $a.Name }
            if (-not (Restore-Family 'ipv4' 'Tcpip' $a.Id $name $a.StaticV4)) { $ok = $false }
            if (-not (Restore-Family 'ipv6' 'Tcpip6' $a.Id $name $a.StaticV6)) { $ok = $false }
        }
        ipconfig.exe /flushdns | Out-Null
        if ($ok) {
            Remove-Item -LiteralPath $file -Force
            schtasks.exe /Delete /TN $task /F 2>$null | Out-Null
        }
        """;

    private static async Task<(int Code, string Output)> SchtasksAsync(params string[] args)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("schtasks не запустился");
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.Trim());
    }
}
