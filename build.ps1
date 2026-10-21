# Собирает готовую к запуску программу в папку dist и архив FiolKid.zip для отправки.
# Запуск: правой кнопкой по build.ps1 -> «Выполнить с помощью PowerShell»
# или из терминала: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$work = Join-Path $root 'obj\publish'
# Собираем во временную папку, а в dist потом переносим только изменившееся:
# драйвер WinDivert может оставаться загруженным из dist, и удалить его файл нельзя
$stage = Join-Path $work 'dist'
$third = Join-Path $root 'third_party'

& (Join-Path $root 'tools\fetch-deps.ps1')

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

$apps = @(
    @{ Project = 'src\ZapretPro.App';     Exe = 'FiolKid.exe';            Name = 'FiolKid.exe' },
    @{ Project = 'src\ZapretPro.KeyTool'; Exe = 'FiolKid.KeyTool.exe';    Name = 'ПроверитьКлюч.exe' }
)

foreach ($app in $apps) {
    $out = Join-Path $work $app.Exe
    dotnet publish (Join-Path $root $app.Project) -c Release -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Не собрался $($app.Project)" }
    Copy-Item (Join-Path $out $app.Exe) (Join-Path $stage $app.Name)
}

# zapret: winws + WinDivert + файлы-приманки, списки доменов и наши стратегии
$zapret = Join-Path $stage 'zapret'
New-Item -ItemType Directory -Force (Join-Path $zapret 'lists') | Out-Null
Copy-Item -Recurse (Join-Path $third 'zapret\bin') $zapret
foreach ($list in 'ipset-all.txt', 'ipset-exclude.txt', 'list-exclude.txt', 'list-general.txt', 'list-google.txt') {
    Copy-Item (Join-Path $third "zapret\lists\$list") (Join-Path $zapret 'lists')
}
Copy-Item (Join-Path $root 'data\strategies.json') $zapret

# Прокси для Telegram
$tg = Join-Path $stage 'tg-ws-proxy'
New-Item -ItemType Directory -Force $tg | Out-Null
Copy-Item (Join-Path $third 'tg-ws-proxy\TgWsProxy_windows.exe') $tg

# VPN: официальный клиент AmneziaWG и списки доменов/подсетей для раздельной маршрутизации
$awg = Join-Path $stage 'amneziawg'
New-Item -ItemType Directory -Force $awg | Out-Null
Copy-Item (Join-Path $third 'amneziawg\*') $awg -Exclude '.sha256', '.source'
Copy-Item -Recurse (Join-Path $root 'data\vpn') $stage

# Версии компонентов — их показывает раздел «Обновления» в настройках
$versions = @(
    "Прокси Telegram = $((Get-Content (Join-Path $third 'tg-ws-proxy\.source') -Raw).Trim())",
    "VPN (AmneziaWG) = $((Get-Content (Join-Path $third 'amneziawg\.source') -Raw).Trim())"
)
[IO.File]::WriteAllLines((Join-Path $stage 'versions.txt'), [string[]]$versions, (New-Object Text.UTF8Encoding $false))

# Архив для отправки тестировщику: всё, кроме ПроверитьКлюч.exe, плюс инструкция
$package = Join-Path $work 'package'
$packageApp = Join-Path $package 'FiolKid'
if (Test-Path $package) { Remove-Item -Recurse -Force $package }
New-Item -ItemType Directory -Force $package | Out-Null
Copy-Item -Recurse $stage $packageApp
Remove-Item (Join-Path $packageApp 'ПроверитьКлюч.exe')
Copy-Item (Join-Path $root 'docs\КАК-ЗАПУСТИТЬ.txt') $packageApp
$zip = Join-Path $root 'FiolKid.zip'
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# UTF-8 в именах файлов, иначе русские названия в архиве превратятся в кракозябры
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, 'Optimal', $false, [Text.Encoding]::UTF8)

# Переносим в dist только изменившееся; данные tg-ws-proxy, созданные при запуске, не трогаем
robocopy $stage $dist /MIR /XD TgWsProxy_data /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    Write-Warning 'Часть файлов в dist занята — закрой FiolKid.exe и запусти сборку ещё раз. Архив при этом собран полностью.'
}

# Компилятор остаётся висеть в памяти после сборки — выключаем
dotnet build-server shutdown | Out-Null

Write-Host ''
Write-Host "Готово: $dist" -ForegroundColor Green
Get-ChildItem $dist -File | ForEach-Object { '  {0}  ({1:N0} МБ)' -f $_.Name, ($_.Length / 1MB) }
Get-ChildItem $dist -Directory | ForEach-Object { '  {0}\' -f $_.Name }
Write-Host ("Архив для отправки: {0} ({1:N0} МБ)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
