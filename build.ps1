# Собирает готовые к запуску exe в папку dist рядом с этим файлом.
# Запуск: правой кнопкой по build.ps1 -> «Выполнить с помощью PowerShell»
# или из терминала: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$work = Join-Path $root 'obj\publish'

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

Write-Host ''
Write-Host "Готово: $dist" -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { '  {0}  ({1:N0} МБ)' -f $_.Name, ($_.Length / 1MB) }
