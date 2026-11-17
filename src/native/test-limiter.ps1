# Prova automatica del limitatore: avvia il gioco di prova, inietta la DLL
# e misura la regolarità dei frame a vari limiti. Uso: .\test-limiter.ps1
$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot 'bin\x64'

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class FFTick { [DllImport("kernel32.dll")] public static extern ulong GetTickCount64(); }
'@

$game = Start-Process "$bin\ff_testgame.exe" -ArgumentList 40 -PassThru
Start-Sleep -Seconds 2
& "$bin\ffinject64.exe" $game.Id "$bin\FrameGripHook64.dll"
"Iniezione: codice $LASTEXITCODE"
Start-Sleep -Milliseconds 800

$map = [IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting("Local\FrameGripHook_$($game.Id)")
$view = $map.CreateViewAccessor()
$freq = $view.ReadInt64(40)

function Pump([double]$seconds, [bool]$heartbeat = $true) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        if ($heartbeat) { $view.Write(24, [FFTick]::GetTickCount64()) }
        Start-Sleep -Milliseconds 100
    }
}

function Measure-Frames([string]$label, [double]$fps, [bool]$heartbeat = $true) {
    $view.Write(16, [double]$fps)
    $view.Write(8, [uint32]$(if ($fps -gt 0) { 1 } else { 0 }))
    if ($heartbeat) { Pump 0.7 } else { Start-Sleep -Seconds 4 }   # assestamento (o attesa che il battito scada)
    $start = $view.ReadUInt64(32)
    if ($heartbeat) { Pump 2.0 } else { Start-Sleep -Seconds 2 }
    $stop = $view.ReadUInt64(32)
    $n = [Math]::Min([long]($stop - $start), 1900)
    if ($n -lt 3) { "{0,-28} nessun frame" -f $label; return }
    $ft = New-Object 'System.Collections.Generic.List[double]'
    $prev = 0
    for ($i = $stop - $n; $i -lt $stop; $i++) {
        $q = $view.ReadInt64(48 + ($i % 2048) * 8)
        if ($prev -ne 0) { $ft.Add(($q - $prev) * 1000.0 / $freq) }
        $prev = $q
    }
    $mean = ($ft | Measure-Object -Average).Average
    $sd = [Math]::Sqrt((($ft | ForEach-Object { ($_ - $mean) * ($_ - $mean) }) | Measure-Object -Average).Average)
    $sorted = $ft | Sort-Object
    "{0,-28} fps {1,8:0.0}   medio {2,7:0.000} ms   dev.std {3,6:0.000} ms   min {4,7:0.000}   max {5,7:0.000}   campioni {6}" -f `
        $label, (1000.0 / $mean), $mean, $sd, $sorted[0], $sorted[-1], $ft.Count
}

# Nota: se sul PC gira un altro limitatore il "libero" si ferma al suo tetto,
# e i limiti più alti di quel tetto non sono verificabili.
Measure-Frames 'Libero' 0
Measure-Frames 'Limite 30' 30
Measure-Frames 'Limite 60' 60
Measure-Frames 'Limite 90' 90
Measure-Frames 'Limite 72.5' 72.5
Measure-Frames 'Limite 60, app chiusa' 60 $false
Measure-Frames 'Libero di nuovo' 0

$view.Dispose(); $map.Dispose()
if (-not $game.HasExited) { Stop-Process -Id $game.Id -Force }
