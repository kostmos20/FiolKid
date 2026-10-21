# Обновляет снимки официальных списков подсетей для запасного режима VPN (data\vpn).
# Результат коммитится. Позже это же будет делать Updater в самой программе (этап 5).

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'data\vpn'
$utf8 = New-Object Text.UTF8Encoding $false
New-Item -ItemType Directory -Force $out | Out-Null

# Telegram публикует свои подсети сам
$tg = (Invoke-WebRequest 'https://core.telegram.org/resources/cidr.txt' -UseBasicParsing).Content -split "`n" |
    ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+/\d+$' }
if ($tg.Count -lt 3) { throw 'Список подсетей Telegram подозрительно короткий' }
$header = "# Подсети Telegram (IPv4), источник https://core.telegram.org/resources/cidr.txt, $(Get-Date -Format yyyy-MM-dd)"
[IO.File]::WriteAllLines((Join-Path $out 'telegram-cidr.txt'), [string[]](@($header) + $tg), $utf8)

# Google: все его сервисы, включая видеосерверы YouTube (googlevideo.com)
$goog = Invoke-RestMethod 'https://www.gstatic.com/ipranges/goog.json'
$v4 = @($goog.prefixes | Where-Object { $_.ipv4Prefix } | ForEach-Object { $_.ipv4Prefix })
if ($v4.Count -lt 20) { throw 'Список подсетей Google подозрительно короткий' }
$header = "# Подсети Google (IPv4), источник https://www.gstatic.com/ipranges/goog.json, $($goog.creationTime)"
[IO.File]::WriteAllLines((Join-Path $out 'google-cidr.txt'), [string[]](@($header) + $v4), $utf8)

Write-Host "Telegram: $($tg.Count), Google: $($v4.Count) -> $out"
