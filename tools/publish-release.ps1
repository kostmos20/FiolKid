# Выпуск новой версии FiolKid: сборка, подпись и выкладка на GitHub (раздел Releases).
# Программы у людей раз в 6 часов спрашивают GitHub, видят новую версию и предлагают «Обновить».
#
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Notes "Что нового — одной-двумя фразами"
#
# Перед запуском: поднять <Version> в src\ZapretPro.App\ZapretPro.App.csproj, сохранить изменения в git
# и отправить их (git push). Нужны: gh (GitHub CLI, вход как владелец репозитория) и ключ подписи
# (src\ZapretPro.ReleaseTool: keygen — один раз на этом компьютере).
param(
    [Parameter(Mandatory)][string]$Notes,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root 'src\ZapretPro.App\ZapretPro.App.csproj'
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Не нашёл <Version> в ZapretPro.App.csproj' }
$tag = "v$version"

if (git -C $root status --porcelain) { throw 'Есть несохранённые изменения — сначала git commit и git push' }
if (gh release view $tag 2>$null) { throw "Выпуск $tag уже есть — подними версию в ZapretPro.App.csproj" }

Write-Host "Собираю FiolKid $version…" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }

Write-Host 'Подписываю…' -ForegroundColor Cyan
$notesFile = Join-Path $env:TEMP "fiolkid-notes-$version.txt"
[IO.File]::WriteAllText($notesFile, $Notes, (New-Object Text.UTF8Encoding $false))
dotnet run --project (Join-Path $root 'src\ZapretPro.ReleaseTool') -c Release -- sign (Join-Path $root 'FiolKid.zip') $version $notesFile
if ($LASTEXITCODE -ne 0) { throw 'Подпись не удалась' }

Write-Host "Выкладываю $tag на GitHub…" -ForegroundColor Cyan
$assets = 'FiolKid.zip', 'FiolKid-update.json', 'FiolKid-update.json.sig' | ForEach-Object { Join-Path $root $_ }
$extra = @()
if ($Draft) { $extra += '--draft' }
gh release create $tag @assets --title "FiolKid $version" --notes $Notes @extra
if ($LASTEXITCODE -ne 0) { throw 'GitHub не принял выпуск' }
Write-Host "Готово: FiolKid $version выложена. Программы увидят её в течение 6 часов (или сразу — «Проверить обновления»)." -ForegroundColor Green
