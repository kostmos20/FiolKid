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

# gh — установленный в системе или распакованный рядом с проектом (F:\Program\FiolKid-tools\gh)
$ghCommand = Get-Command gh -ErrorAction SilentlyContinue
$gh = if ($ghCommand) { $ghCommand.Source } else { Join-Path (Split-Path $root -Parent) 'FiolKid-tools\gh\bin\gh.exe' }
if (-not (Test-Path $gh)) { throw "Не нашёл GitHub CLI (gh): $gh" }
function gh { & $gh @args }

# Текст страницы выпуска: что нового и подсказка, что из файлов скачивать
function Get-ReleaseBody([string]$Text) {
    @"
$Text

### Как установить
Скачай **FiolKid.zip** (ниже, в разделе Assets), распакуй целиком в любую папку и запусти ``FiolKid.exe``.
Уже стоит FiolKid — ничего качать не нужно: программа сама предложит «Обновить».

Остальные файлы людям не нужны: ``FiolKid-update.json`` и ``.sig`` — подписанное описание выпуска для автообновления,
Source code — исходный код (GitHub добавляет его сам).

Прямая ссылка на последнюю версию: https://github.com/kostmos20/FiolKid/releases/latest/download/FiolKid.zip
"@
}
$csproj = Join-Path $root 'src\ZapretPro.App\ZapretPro.App.csproj'
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Не нашёл <Version> в ZapretPro.App.csproj' }
$tag = "v$version"

if (git -C $root status --porcelain) { throw 'Есть несохранённые изменения — сначала git commit и git push' }
# Windows PowerShell считает сообщение gh об ошибке («release not found») исключением — ловим и смотрим на код выхода
$released = $false
try { gh release view $tag *> $null; $released = ($LASTEXITCODE -eq 0) } catch { $released = $false }
if ($released) { throw "Выпуск $tag уже есть — подними версию в ZapretPro.App.csproj" }

Write-Host "Собираю FiolKid $version…" -ForegroundColor Cyan
# В отдельную папку release: dist и FiolKid.zip автора не трогаем — на них он проверяет обновление
$release = Join-Path $root 'release'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -ZipPath 'release\FiolKid.zip' -NoDist
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }

Write-Host 'Подписываю…' -ForegroundColor Cyan
$notesFile = Join-Path $env:TEMP "fiolkid-notes-$version.txt"
[IO.File]::WriteAllText($notesFile, $Notes, (New-Object Text.UTF8Encoding $false))
dotnet run --project (Join-Path $root 'src\ZapretPro.ReleaseTool') -c Release -- sign (Join-Path $release 'FiolKid.zip') $version $notesFile
if ($LASTEXITCODE -ne 0) { throw 'Подпись не удалась' }

Write-Host "Выкладываю $tag на GitHub…" -ForegroundColor Cyan
$assets = 'FiolKid.zip', 'FiolKid-update.json', 'FiolKid-update.json.sig' | ForEach-Object { Join-Path $release $_ }
$extra = @()
if ($Draft) { $extra += '--draft' }
gh release create $tag @assets --title "FiolKid $version" --notes (Get-ReleaseBody $Notes) @extra
if ($LASTEXITCODE -ne 0) { throw 'GitHub не принял выпуск' }
Write-Host "Готово: FiolKid $version выложена. Программы увидят её в течение 6 часов (или сразу — «Проверить обновления»)." -ForegroundColor Green
