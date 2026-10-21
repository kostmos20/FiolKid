# Скачивает сторонние файлы из официальных релизов и сверяет SHA-256.
# Результат — папка third_party (в git не попадает). build.ps1 вызывает этот скрипт сам.
#
# Чтобы обновить версию: поменять Url и Sha256 ниже (хэш есть на странице релиза GitHub),
# удалить third_party и запустить tools\import-strategies.ps1.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path $PSScriptRoot -Parent
$third = Join-Path $root 'third_party'

$deps = @(
    @{
        # Стратегии, списки и winws.exe + WinDivert (сборка bol-van/zapret)
        Name   = 'zapret'
        Source = 'Flowseal/zapret-discord-youtube 1.10.4'
        Url    = 'https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.4/zapret-discord-youtube-1.10.4.zip'
        Sha256 = '291af673bd0238506f9548e0e94ca0abf244000f4c9a06960544fb01c5f97b34'
        Unzip  = $true
    },
    @{
        Name   = 'tg-ws-proxy'
        Source = 'Flowseal/tg-ws-proxy v1.11.2'
        Url    = 'https://github.com/Flowseal/tg-ws-proxy/releases/download/v1.11.2/TgWsProxy_windows.exe'
        Sha256 = '54acaa989858dd0f90eca3d18a93125fac3d8f702fc25cff8900d6098f725853'
        Unzip  = $false
    },
    @{
        # Официальный клиент AmneziaWG: amneziawg.exe (служба туннеля), awg.exe, wintun.dll
        Name   = 'amneziawg'
        Source = 'amnezia-vpn/amneziawg-windows-client 3.1.0'
        Url    = 'https://github.com/amnezia-vpn/amneziawg-windows-client/releases/download/3.1.0/amneziawg-amd64-3.1.0.msi'
        Sha256 = 'a1b48ea8699cd347832a3691d832004574ef8ad65bcf887611ac8acb99b7de8b'
        Unzip  = $false
        Msi    = $true
    }
)

New-Item -ItemType Directory -Force $third | Out-Null

foreach ($dep in $deps) {
    $target = Join-Path $third $dep.Name
    $stamp = Join-Path $target '.sha256'
    if ((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $dep.Sha256)) {
        continue
    }

    Write-Host "Скачиваю $($dep.Name)..."
    $file = Join-Path $third ([IO.Path]::GetFileName($dep.Url))
    Invoke-WebRequest -Uri $dep.Url -OutFile $file -UseBasicParsing

    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $dep.Sha256) {
        Remove-Item $file
        throw "Контрольная сумма $($dep.Name) не совпала: ожидалось $($dep.Sha256), получено $actual"
    }

    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force $target | Out-Null
    if ($dep.Unzip) {
        Expand-Archive $file -DestinationPath $target
        Remove-Item $file
        # Архив обычно содержит одну папку с версией в имени — поднимаем её содержимое на уровень выше
        $inner = @(Get-ChildItem $target)
        if ($inner.Count -eq 1 -and $inner[0].PSIsContainer) {
            Get-ChildItem $inner[0].FullName -Force | Move-Item -Destination $target
            Remove-Item $inner[0].FullName
        }
    } elseif ($dep.Msi) {
        # Административная установка только распаковывает файлы MSI, ничего не ставя в систему
        $extract = Join-Path $third "$($dep.Name)-msi"
        $p = Start-Process msiexec.exe -ArgumentList "/a `"$file`" /qn TARGETDIR=`"$extract`"" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "Не удалось распаковать $($dep.Name): msiexec вернул $($p.ExitCode)" }
        Get-ChildItem (Join-Path $extract 'AmneziaWG') -File | Move-Item -Destination $target
        Remove-Item -Recurse -Force $extract
        Remove-Item $file
    } else {
        Move-Item $file $target
    }
    Set-Content (Join-Path $target '.source') $dep.Source -Encoding ascii
    Set-Content $stamp $dep.Sha256 -Encoding ascii
}
