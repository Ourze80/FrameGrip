# Prova aggancio e limitatore sui giochi di prova a 32 bit (DirectX 9 e OpenGL).
# Uso: .\test-x86.ps1
$n = $PSScriptRoot
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class FFTickX { [DllImport("kernel32.dll")] public static extern ulong GetTickCount64(); }
'@
$log = "$env:LOCALAPPDATA\FrameGrip\hook.log"
foreach ($game in 'ff_testgame9', 'ff_testgamegl') {
    Remove-Item $log -ErrorAction SilentlyContinue
    $g = Start-Process "$n\bin\x86\$game.exe" -ArgumentList 20 -PassThru
    Start-Sleep 2
    & "$n\bin\x86\ffinject32.exe" $g.Id "$n\bin\x86\FrameGripHook32.dll" | Out-Null
    Start-Sleep 5   # tempo perché un eventuale aggancio cancellato venga rimesso
    "=== $game ==="
    try {
        $m = [IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting("Local\FrameGripHook_$($g.Id)")
        $v = $m.CreateViewAccessor()
        $a = $v.ReadUInt64(32); Start-Sleep 2
        "libero:    api=$($v.ReadUInt32(4))  fps=$(($v.ReadUInt64(32) - $a) / 2)"
        $v.Write(16, [double]45); $v.Write(8, [uint32]1)
        1..4 | ForEach-Object { $v.Write(24, [FFTickX]::GetTickCount64()); Start-Sleep -Milliseconds 250 }
        $a = $v.ReadUInt64(32); $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 2000) { $v.Write(24, [FFTickX]::GetTickCount64()); Start-Sleep -Milliseconds 100 }
        "limite 45: fps=$([math]::Round(($v.ReadUInt64(32) - $a) / $sw.Elapsed.TotalSeconds, 1))"
        $v.Dispose(); $m.Dispose()
    } catch { "errore: $($_.Exception.Message)" }
    $g.Refresh()
    if (-not $g.HasExited) { Stop-Process -Id $g.Id -Force } else { "il gioco e' uscito: codice $($g.ExitCode)" }
    Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch 'device di prova' } | ForEach-Object { '   ' + $_.Substring(9) }
}
