#requires -Version 7.0
param(
    [Parameter(Mandatory)] [string] $Probe,
    [Parameter(Mandatory)] [string] $Manifest,
    [switch] $IncludeForcedExit
)

$ErrorActionPreference = 'Stop'
$probePath = (Resolve-Path -LiteralPath $Probe).Path
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path

function Read-ProbeLine($Process) {
    $read = $Process.StandardOutput.ReadLineAsync()
    if (!$read.Wait(15000)) {
        throw "No response from probe PID $($Process.Id)."
    }
    $read.Result
}

function Invoke-Step($Process, [string] $Command) {
    $Process.StandardInput.WriteLine($Command)
    $Process.StandardInput.WriteLine('status')
    while ($null -ne ($line = Read-ProbeLine $Process)) {
        Write-Host $line
        if ($line.StartsWith('STATUS ')) { return $line -eq 'STATUS active=True' }
    }
    throw 'The session ended before returning its status.'
}

function Get-Visibility([string] $Phase, [string] $Family) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = & $probePath query $Family
    $exitCode = $LASTEXITCODE
    Write-Host "$Phase ($($watch.ElapsedMilliseconds) ms for query process)"
    $result | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "The query failed with exit code $exitCode." }
    $result
}

function Start-ProbeSession([string] $Path) {
    $start = [Diagnostics.ProcessStartInfo]::new($probePath)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.ArgumentList.Add('session')
    $start.ArgumentList.Add($Path)
    [Diagnostics.Process]::Start($start)
}

function Stop-ProbeSession($Process) {
    try {
        if (!$Process.HasExited) {
            $Process.StandardInput.WriteLine('quit')
            $Process.StandardInput.Close()
        }
        while ($null -ne ($line = Read-ProbeLine $Process)) { Write-Host $line }
        if (!$Process.WaitForExit(15000) -or $Process.ExitCode -ne 0) {
            throw 'The session did not exit cleanly.'
        }
    }
    finally {
        $Process.Dispose()
    }
}

if ($IncludeForcedExit) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ForcedExitFixtureCleanup
{
    [DllImport("gdi32.dll", EntryPoint = "RemoveFontResourceExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool Remove(string path, uint flags, IntPtr reserved);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true)]
    private static extern IntPtr Send(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out UIntPtr result);

    public static bool Notify()
    {
        UIntPtr ignored;
        return Send(new IntPtr(0xffff), 0x001D, IntPtr.Zero, IntPtr.Zero, 2, 500, out ignored) != IntPtr.Zero;
    }
}
'@
}

foreach ($fixture in (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)) {
    $path = Join-Path (Split-Path -Parent $manifestPath) $fixture.file
    $hash = (Get-FileHash -LiteralPath $path).Hash
    Write-Host "`n$($fixture.kind) / $($fixture.family) / SHA256=$hash"
    $baseline = Get-Visibility 'Baseline' $fixture.family
    if ('GDI=False' -notin $baseline -or 'DirectWrite=False' -notin $baseline) {
        throw "The test name is already present. Generate a new fixture."
    }

    $session = Start-ProbeSession $path
    try {
        Write-Host (Read-ProbeLine $session)
        Write-Host (Read-ProbeLine $session)
        $null = Invoke-Step $session 'on'
        $null = Get-Visibility 'After on' $fixture.family
        $null = Invoke-Step $session 'on'
        $null = Invoke-Step $session 'off'
        $null = Get-Visibility 'After off' $fixture.family
        $null = Invoke-Step $session 'on'
        $null = Get-Visibility 'After reactivation' $fixture.family
    }
    finally {
        Stop-ProbeSession $session
    }
    $afterExit = Get-Visibility 'After exit' $fixture.family
    if ($IncludeForcedExit -and 'GDI=False' -in $afterExit -and 'DirectWrite=False' -in $afterExit) {
        $session = Start-ProbeSession $path
        $killed = $false
        try {
            if (Invoke-Step $session 'on') {
                $session.Kill()
                $killed = $true
                if (!$session.WaitForExit(15000)) { throw 'Forced termination timed out.' }
                $null = Get-Visibility 'After forced exit' $fixture.family

                $restarted = Start-ProbeSession $path
                try {
                    $null = Invoke-Step $restarted 'status'
                    $null = Get-Visibility 'After same-session restart' $fixture.family
                }
                finally {
                    Stop-ProbeSession $restarted
                }
            }
        }
        finally {
            if ($killed) {
                $session.Dispose()
                # Unique fixture absent at baseline and one confirmed registration in the killed process.
                $removed = [ForcedExitFixtureCleanup]::Remove($path, 0, [IntPtr]::Zero)
                Write-Host "Forced exit cleanup removed=$removed"
                if ($removed) {
                    Write-Host "WM_FONTCHANGE success=$([ForcedExitFixtureCleanup]::Notify())"
                }
                $clean = Get-Visibility 'After forced exit cleanup' $fixture.family
                if ('GDI=False' -notin $clean -or 'DirectWrite=False' -notin $clean) {
                    throw "The fixture is still visible after cleanup: $path"
                }
            }
            else {
                Stop-ProbeSession $session
            }
        }
    }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $hash) {
        throw "The source file changed: $path"
    }
}
