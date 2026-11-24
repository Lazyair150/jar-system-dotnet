param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $LogPath)) {
    Write-Error "Verification log not found: $LogPath"
    exit 2
}

$log = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $LogPath).Path)

Write-Output ''
Write-Output '---------------- KEY LINES ----------------'
$keyPatterns = @(
    'error CS', 'warning CS', 'warning MSB', 'warning NETSDK',
    ' error ', 'FAIL ', 'bugs /', 'exit=', 'schema ', 'loose=',
    'CONFIG-WORLD', 'world ', 'contract dumped', 'PROTOCOL-',
    'protocol ', 'script ascii', 'types dumped', 'types dts',
    'SOLVER-', 'mismatch detail:', 'MODDIFF-', 'REPLAY-', 'SNAPSHOT-', 'STEP-FAILED',
    'JS-', 'DOC-', 'SCENARIO-', 'QUALITY-', 'SEC-',
    'JarSystem.Tests', 'Total:'
)
Select-String -LiteralPath $LogPath -Pattern $keyPatterns |
    ForEach-Object { Write-Output $_.Line }
Write-Output '-------------------------------------------'
Write-Output "full log : $LogPath"
Write-Output ''

$failureMarkers = @(
    'error CS',
    'FAIL ',
    'CONFIG-WORLD-MISMATCH',
    'schema dump FAILED',
    'PROTOCOL-MISMATCH',
    'SOURCE-NOT-FOUND',
    'SCRIPT-NON-ASCII',
    'DTS-DEGRADED',
    'SOLVER-MISMATCH',
    'MODDIFF-MISMATCH',
    'REPLAY-MISMATCH',
    'SNAPSHOT-MISMATCH',
    'SNAPSHOT-NO-BASELINE',
    'STEP-FAILED',
    'JS-SYNTAX-ERROR',
    'SCENARIO-GATE-FAILED',
    'QUALITY-GATE-FAILED',
    'SEC-SCAN-FAILED'
)

$verdict = 'OK'
foreach ($marker in $failureMarkers) {
    if ($log.Contains($marker)) {
        $verdict = 'FAIL'
        break
    }
}
if (-not $log.Contains('QUALITY-OK')) {
    $verdict = 'FAIL'
}

Write-Output "VERDICT=$verdict"
Add-Content -LiteralPath $LogPath -Value "VERDICT=$verdict" -Encoding UTF8
if ($verdict -eq 'FAIL') {
    exit 1
}
exit 0
