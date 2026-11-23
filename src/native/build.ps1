# Compila la parte nativa a 64 e a 32 bit: DLL di aggancio, iniettore e giochi di prova.
# Richiede i Build Tools C++ di Visual Studio. Uso: .\build.ps1 [-OutRoot <cartella>]
# Senza -OutRoot i file vanno in bin\ e obj\ accanto a questo script.
param([string]$OutRoot = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $OutRoot) { $OutRoot = $here }

$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
$vswhere = Join-Path $installer 'vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Build Tools di Visual Studio non trovati.' }
$env:PATH = "$installer;$env:PATH"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Componente C++ (VC Tools) non installato.' }
$vcvarsall = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'

$flags = '/nologo /O2 /MT /EHsc /std:c++17 /W3 /DUNICODE /D_UNICODE'
$mh = "$here\third_party\minhook\src"   # MinHook 1.3.4 (licenza BSD a 2 clausole)
$mhSources = "`"$mh\hook.c`" `"$mh\buffer.c`" `"$mh\trampoline.c`" `"$mh\hde\hde32.c`" `"$mh\hde\hde64.c`""
$hookSources = "`"$here\hook.cpp`" `"$here\hook_dxgi.cpp`" `"$here\hook_d3d12.cpp`" `"$here\hook_d3d9.cpp`" `"$here\hook_gl.cpp`" `"$here\hook_vk.cpp`" `"$here\hook_graph.cpp`""
$flags += " /I`"$here\third_party\Vulkan-Headers\include`""   # intestazioni ufficiali Khronos (Apache 2.0 / MIT)

foreach ($arch in @(@{ Name = 'x64'; Bits = '64' }, @{ Name = 'x86'; Bits = '32' })) {
    $out = Join-Path $OutRoot "bin\$($arch.Name)"
    $obj = Join-Path $OutRoot "obj\$($arch.Name)"
    New-Item -ItemType Directory -Force $out, $obj | Out-Null
    $b = $arch.Bits

    # Le librerie grafiche sono a caricamento ritardato (/DELAYLOAD): la DLL non deve portare
    # dentro il gioco API che il gioco non usa.
    # MinHook va compilata in una cartella a parte: ha anche lei un file che si chiama "hook".
    $mhObj = Join-Path $obj 'minhook'
    New-Item -ItemType Directory -Force $mhObj | Out-Null
    $steps = @(
        "cl /nologo /O2 /MT /W3 /c $mhSources /Fo`"$mhObj\\`"",
        "cl $flags /LD $hookSources `"$mhObj\*.obj`" /Fo`"$obj\\`" /Fe`"$out\FrameGripHook$b.dll`" /link d3d11.lib d3d12.lib dxgi.lib d3d9.lib d3dcompiler.lib opengl32.lib gdi32.lib user32.lib delayimp.lib /DELAYLOAD:d3d11.dll /DELAYLOAD:d3d12.dll /DELAYLOAD:d3d9.dll /DELAYLOAD:opengl32.dll /DELAYLOAD:D3DCOMPILER_47.dll",
        "cl $flags `"$here\inject.cpp`" /Fo`"$obj\\`" /Fe`"$out\ffinject$b.exe`""
    )
    if ($arch.Name -eq 'x64') {
        $steps += "cl $flags `"$here\testgame.cpp`" /Fo`"$obj\\`" /Fe`"$out\ff_testgame.exe`" /link /SUBSYSTEM:WINDOWS d3d11.lib dxgi.lib user32.lib"
        $steps += "cl $flags `"$here\testgamevk.cpp`" /Fo`"$obj\\`" /Fe`"$out\ff_testgamevk.exe`" /link /SUBSYSTEM:WINDOWS user32.lib"
        $steps += "cl $flags `"$here\testgame12.cpp`" /Fo`"$obj\\`" /Fe`"$out\ff_testgame12.exe`" /link /SUBSYSTEM:WINDOWS d3d12.lib dxgi.lib user32.lib"
    } else {
        $steps += "cl $flags `"$here\testgame9.cpp`" /Fo`"$obj\\`" /Fe`"$out\ff_testgame9.exe`" /link /SUBSYSTEM:WINDOWS d3d9.lib user32.lib"
        $steps += "cl $flags `"$here\testgamegl.cpp`" /Fo`"$obj\\`" /Fe`"$out\ff_testgamegl.exe`" /link /SUBSYSTEM:WINDOWS opengl32.lib gdi32.lib user32.lib"
    }

    $cmd = "`"$vcvarsall`" $($arch.Name) >nul && " + ($steps -join ' && ')
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "Compilazione nativa $($arch.Name) fallita (codice $LASTEXITCODE)." }
}

Get-ChildItem (Join-Path $OutRoot 'bin') -Include *.dll, *.exe -Recurse | ForEach-Object { '{0,-8} {1}' -f $_.Directory.Name, $_.Name }
