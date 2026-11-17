# Crea l'installer classico di FrameGrip: dist\FrameGrip-Setup-<versione>.exe
# 1) pubblica l'app completa di .NET (chi la installa non deve scaricare altro);
# 2) compila FrameGrip.iss con Inno Setup 6 (gratuito, https://jrsoftware.org).
# Prima va compilata la parte nativa (src\native\build.ps1). Uso: .\build.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Resolve-Path (Join-Path $here '..\..')
$project = Join-Path $root 'src\FrameForge\FrameForge.csproj'
$work = Join-Path $here 'obj'
$publish = Join-Path $work 'publish'
$dist = Join-Path $root 'dist'

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 non trovato: installalo da https://jrsoftware.org/isdl.php' }

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '1.0.0' }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work, $dist | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Pubblicazione dell''app fallita.' }
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

$icon = Join-Path $root 'src\FrameForge\Assets\app.ico'
& $iscc /Qp "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutDir=$dist" "/DIconFile=$icon" (Join-Path $here 'FrameGrip.iss')
if ($LASTEXITCODE -ne 0) { throw "Compilazione dell'installer fallita (codice $LASTEXITCODE)." }

$file = Get-Item (Join-Path $dist "FrameGrip-Setup-$version.exe")
'{0}  ({1:N1} MB)' -f $file.FullName, ($file.Length / 1MB)
