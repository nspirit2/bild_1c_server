# Run only as NT AUTHORITY\LOCAL SERVICE via a one-shot scheduled task.
# Handoff file must already be restricted to Administrators/SYSTEM/LOCAL SERVICE.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$base = 'E:\BuildSystem\SyntheticStage'
$handoff = Join-Path $base 'Data\credential.handoff'
$result = Join-Path $base 'Data\import-result.txt'
try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-19') { throw 'Wrong import identity' }
    if (Test-Path (Join-Path $base 'Data\agent-token.dpapi')) { throw 'Existing credential; refusing overwrite' }
    $env:Agent__DataDirectory = Join-Path $base 'Data'
    Set-Location (Join-Path $base 'Agent')
    # Token never appears in arguments/stdout. Pipe directly to the existing import mode.
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = 'C:\Program Files\dotnet\dotnet.exe'
    $start.Arguments = '"E:\BuildSystem\SyntheticStage\Agent\BuildSystem.Agent.dll" import-token'
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.WriteLine([IO.File]::ReadAllText($handoff).Trim())
    $process.StandardInput.Close()
    $null = $process.StandardOutput.ReadToEnd()
    $null = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw 'Import rejected' }
    $process.Dispose()
    if (-not (Test-Path (Join-Path $base 'Data\agent-token.dpapi'))) { throw 'Protected credential missing' }
    Remove-Item -LiteralPath $handoff
    [IO.File]::WriteAllText($result, 'OK: LOCAL SERVICE DPAPI import; plaintext handoff deleted.')
    exit 0
} catch {
    # No exception details: an unexpected exception must not echo credential-bearing state.
    [IO.File]::WriteAllText($result, 'FAILED: inspect ACL, identity and existing DPAPI state; no credential printed.')
    exit 1
}
# end
