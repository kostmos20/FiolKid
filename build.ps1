# Собирает готовые к запуску exe в папку dist рядом с этим файлом.
# Запуск: правой кнопкой по build.ps1 -> «Выполнить с помощью PowerShell»
# или из терминала: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$work = Join-Path $root 'obj\publish'

& (Join-Path $root 'tools\fetch-deps.ps1')

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $dist | Out-Null

$apps = @(
    @{ Project = 'src\ZapretPro.App';     Exe = 'OdnaKnopka.exe';         Name = 'ОднаКнопка.exe' },
    @{ Project = 'src\ZapretPro.KeyTool'; Exe = 'OdnaKnopka.KeyTool.exe'; Name = 'ПроверитьКлюч.exe' }
)

foreach ($app in $apps) {
    $out = Join-Path $work $app.Exe
    dotnet publish (Join-Path $root $app.Project) -c Release -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Не собрался $($app.Project)" }
    Copy-Item (Join-Path $out $app.Exe) (Join-Path $dist $app.Name)
}

# zapret: winws + WinDivert + файлы-приманки, списки доменов и наши стратегии
$third = Join-Path $root 'third_party'
$zapret = Join-Path $dist 'zapret'
New-Item -ItemType Directory -Force (Join-Path $zapret 'lists') | Out-Null
Copy-Item -Recurse (Join-Path $third 'zapret\bin') $zapret
foreach ($list in 'ipset-all.txt', 'ipset-exclude.txt', 'list-exclude.txt', 'list-general.txt', 'list-google.txt') {
    Copy-Item (Join-Path $third "zapret\lists\$list") (Join-Path $zapret 'lists')
}
Copy-Item (Join-Path $root 'data\strategies.json') $zapret

# Прокси для Telegram
$tg = Join-Path $dist 'tg-ws-proxy'
New-Item -ItemType Directory -Force $tg | Out-Null
Copy-Item (Join-Path $third 'tg-ws-proxy\TgWsProxy_windows.exe') $tg

Write-Host ''
Write-Host "Готово: $dist" -ForegroundColor Green
Get-ChildItem $dist -File | ForEach-Object { '  {0}  ({1:N0} МБ)' -f $_.Name, ($_.Length / 1MB) }
Get-ChildItem $dist -Directory | ForEach-Object { '  {0}\' -f $_.Name }
