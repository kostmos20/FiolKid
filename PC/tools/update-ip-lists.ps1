# Обновляет снимки официальных списков подсетей для запасного режима VPN (data\vpn).
# Результат коммитится. Позже это же будет делать Updater в самой программе (этап 5).

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'data\vpn'
$utf8 = New-Object Text.UTF8Encoding $false
New-Item -ItemType Directory -Force $out | Out-Null

# Подсети Telegram: официальный список плюс список itdoginfo — core.telegram.org иногда отдаёт урезанную версию
$tgSources = 'https://core.telegram.org/resources/cidr.txt',
             'https://raw.githubusercontent.com/itdoginfo/allow-domains/main/Subnets/IPv4/telegram.lst'
$tg = $tgSources | ForEach-Object { (Invoke-WebRequest $_ -UseBasicParsing).Content -split "`n" } |
    ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+/\d+$' } | Sort-Object -Unique
if ($tg.Count -lt 3) { throw 'Список подсетей Telegram подозрительно короткий' }
$header = "# Подсети Telegram (IPv4), источники: $($tgSources -join ', '), $(Get-Date -Format yyyy-MM-dd)"
[IO.File]::WriteAllLines((Join-Path $out 'telegram-cidr.txt'), [string[]](@($header) + $tg), $utf8)

# Google: все его сервисы, включая видеосерверы YouTube (googlevideo.com)
$goog = Invoke-RestMethod 'https://www.gstatic.com/ipranges/goog.json'
$v4 = @($goog.prefixes | Where-Object { $_.ipv4Prefix } | ForEach-Object { $_.ipv4Prefix })
if ($v4.Count -lt 20) { throw 'Список подсетей Google подозрительно короткий' }
$header = "# Подсети Google (IPv4), источник https://www.gstatic.com/ipranges/goog.json, $($goog.creationTime)"
[IO.File]::WriteAllLines((Join-Path $out 'google-cidr.txt'), [string[]](@($header) + $v4), $utf8)

# Заблокированные в России сайты — через VPN. Список itdoginfo/allow-domains (Russia inside), обновляется еженедельно.
# Программа сама скачивает свежую версию раз в сутки, это лишь встроенный снимок на случай, если скачать не выйдет.
$base = 'https://raw.githubusercontent.com/itdoginfo/allow-domains/main'
$blocked = (Invoke-WebRequest "$base/Russia/inside-raw.lst" -UseBasicParsing).Content -split "`n" |
    ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }
if ($blocked.Count -lt 500) { throw 'Список заблокированных доменов подозрительно короткий' }
$header = "# Заблокированные в России сайты, источник $base/Russia/inside-raw.lst, $(Get-Date -Format yyyy-MM-dd)"
[IO.File]::WriteAllLines((Join-Path $out 'blocked-domains.txt'), [string[]](@($header) + $blocked), $utf8)

# Подсети Meta (Instagram, Facebook) и X: у них сотни поддоменов, по одним доменам не поймать
$nets = foreach ($name in 'meta', 'twitter') {
    (Invoke-WebRequest "$base/Subnets/IPv4/$name.lst" -UseBasicParsing).Content -split "`n" |
        ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+/\d+$' }
}
if ($nets.Count -lt 10) { throw 'Список подсетей Meta/X подозрительно короткий' }
$header = "# Подсети Meta и X, источник $base/Subnets/IPv4/{meta,twitter}.lst, $(Get-Date -Format yyyy-MM-dd)"
[IO.File]::WriteAllLines((Join-Path $out 'blocked-cidr.txt'), [string[]](@($header) + $nets), $utf8)

Write-Host "Telegram: $($tg.Count), Google: $($v4.Count), заблокированных доменов: $($blocked.Count), подсетей Meta/X: $($nets.Count) -> $out"
