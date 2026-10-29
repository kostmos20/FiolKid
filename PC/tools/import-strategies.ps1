# Превращает стратегии Flowseal (general*.bat) в data\strategies.json.
# Запускать после обновления версии zapret в tools\fetch-deps.ps1. Результат коммитится.
#
# В аргументах winws пути заменяются на {bin} и {lists}, а игровой фильтр выключается
# так же, как его выключает service.bat Flowseal (порт-заглушка 12).

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root 'third_party\zapret'
$out = Join-Path $root 'data\strategies.json'

& (Join-Path $PSScriptRoot 'fetch-deps.ps1')

function Get-Order([string]$name) {
    # Порядок перебора: основная, ALT по номерам, FAKE TLS AUTO, SIMPLE FAKE, экспериментальная — последней
    $group = switch -Regex ($name) {
        '^general$'        { 0 }
        '^ALT'             { 1 }
        '^FAKE TLS AUTO'   { 2 }
        '^SIMPLE FAKE'     { 3 }
        default            { 9 }
    }
    $num = if ($name -match '(\d+)$') { [int]$Matches[1] } elseif ($name -match 'ALT$') { 1 } else { 0 }
    return $group * 1000 + $num
}

$strategies = foreach ($bat in Get-ChildItem $src -Filter 'general*.bat') {
    $text = Get-Content $bat.FullName -Raw -Encoding UTF8
    # Склеиваем строки-продолжения (^ в конце строки) и берём всё после "%BIN%winws.exe"
    $joined = $text -replace '\^\r?\n', ' '
    if ($joined -notmatch '"%BIN%winws\.exe"\s+(.+)') { throw "Не нашёл вызов winws в $($bat.Name)" }
    $args = $Matches[1].Trim()

    # «^» в cmd экранирует следующий символ: winws получает «!», а не «^!»
    $args = $args -replace '\^(.)', '$1'
    $args = $args.Replace('%BIN%', '{bin}').Replace('%LISTS%', '{lists}')
    $args = $args -replace '%GameFilter(TCP|UDP)?%', '12'
    if ($args -match '%[^%\s]+%') { throw "В $($bat.Name) осталась незнакомая переменная $($Matches[0])" }

    $name = if ($bat.BaseName -match '\((.+)\)') { $Matches[1] } else { 'general' }
    [pscustomobject]@{ id = $name; args = $args; order = (Get-Order $name) }
}

$source = (Get-Content (Join-Path $src '.source') -Raw).Trim()
$doc = [ordered]@{
    source     = $source
    strategies = @($strategies | Sort-Object order | ForEach-Object { [ordered]@{ id = $_.id; args = $_.args } })
}

New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
[IO.File]::WriteAllText($out, ($doc | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
Write-Host "Записано стратегий: $($doc.strategies.Count) -> $out"
$doc.strategies | ForEach-Object { '  ' + $_.id }
