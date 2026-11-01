# Runs a build step, shows its output, and when it fails reports the important lines as GitHub annotations
# (annotations are readable through the GitHub API even where raw logs are not).
param([Parameter(Mandatory = $true)][string]$Title, [Parameter(Mandatory = $true)][string]$Command)

$log = Join-Path $env:RUNNER_TEMP ("step-" + [guid]::NewGuid().ToString("N") + ".log")
$global:LASTEXITCODE = 0
Invoke-Expression "$Command 2>&1" | Tee-Object -FilePath $log | Out-Host
$code = $LASTEXITCODE
if ($code -eq 0) { exit 0 }

function Escape([string]$s) { return $s.Replace('%', '%25').Replace("`r", '').Replace("`n", '%0A') }

$lines = Get-Content $log
$errors = $lines | Where-Object { $_ -match '(error [A-Z]+[0-9]+|: error |FAIL |Exception|Unhandled|error:)' } |
    Select-Object -Unique | Select-Object -First 40
if ($errors) {
    $chunks = [System.Collections.Generic.List[string]]::new()
    $batch = @()
    foreach ($e in $errors) {
        $batch += ($e.Trim() -replace '\s+\[[^\]]+\.csproj[^\]]*\]$', '')
        if ($batch.Count -eq 8) { $chunks.Add(($batch -join "`n")); $batch = @() }
    }
    if ($batch.Count) { $chunks.Add(($batch -join "`n")) }
    foreach ($c in ($chunks | Select-Object -First 5)) { Write-Host "::error title=$Title::$(Escape $c)" }
}
$tail = ($lines | Select-Object -Last 25) -join "`n"
Write-Host "::error title=$Title (last lines)::$(Escape $tail)"
exit $code
