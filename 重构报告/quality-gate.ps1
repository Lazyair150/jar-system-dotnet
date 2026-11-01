param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $failures.Add($message)
    Write-Output "QUALITY-FAIL: $message"
}

Push-Location $repoRoot
try {
    $allFiles = & git -c core.quotepath=false ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) {
        Add-Failure 'git file inventory failed'
        $allFiles = @()
    }

    $codeExtensions = @('.cs', '.js', '.ts', '.csproj', '.props', '.targets', '.yml', '.yaml', '.bat', '.ps1')
    $codeFiles = @($allFiles | Where-Object {
        $codeExtensions -contains [IO.Path]::GetExtension($_)
    } | ForEach-Object {
        Join-Path $repoRoot ($_ -replace '/', '\')
    } | Where-Object {
        (Test-Path -LiteralPath $_) -and
        ([IO.Path]::GetFileName($_) -ne 'quality-gate.ps1')
    })

    $forbidden = @(
        @{ Name = 'NotImplementedException'; Pattern = 'NotImplementedException' },
        @{ Name = 'pragma warning disable'; Pattern = '#pragma\s+warning\s+disable' },
        @{ Name = 'SuppressMessage'; Pattern = 'SuppressMessage' },
        @{ Name = 'skipped test'; Pattern = '\[(Fact|Theory)\s*\([^\)]*Skip' },
        @{ Name = 'test Skip assignment'; Pattern = 'Skip\s*=' },
        @{ Name = 'placeholder marker'; Pattern = '\b(TODO|FIXME|HACK)\b' },
        @{ Name = 'empty catch'; Pattern = 'catch\s*(?:\([^)]*\))?\s*\{\s*\}' },
        @{ Name = 'private key'; Pattern = '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----' },
        @{ Name = 'AWS access key'; Pattern = 'AKIA[0-9A-Z]{16}' }
    )

    foreach ($file in $codeFiles) {
        $text = [IO.File]::ReadAllText($file)
        foreach ($rule in $forbidden) {
            if ([Text.RegularExpressions.Regex]::IsMatch($text, $rule.Pattern)) {
                Add-Failure ("{0} in {1}" -f $rule.Name, $file.Substring($repoRoot.Length + 1))
            }
        }
    }

    $coreProject = Join-Path $repoRoot 'src\JarSystem.Core\JarSystem.Core.csproj'
    if (Select-String -LiteralPath $coreProject -Pattern '<PackageReference' -Quiet) {
        Add-Failure 'core project has a PackageReference'
    }

    $coreSources = @($allFiles | Where-Object {
        $_ -like 'src/JarSystem.Core/*.cs' -or $_ -like 'src/JarSystem.Core/*/*.cs' -or
        $_ -like 'src/JarSystem.Core/*/*/*.cs' -or $_ -like 'src/JarSystem.Core/*/*/*/*.cs'
    } | ForEach-Object { Join-Path $repoRoot ($_ -replace '/', '\') })
    foreach ($file in $coreSources) {
        $text = [IO.File]::ReadAllText($file)
        if ($text -match 'using\s+Microsoft\.(AspNetCore|Extensions)\.') {
            Add-Failure ("host framework import in core: {0}" -f $file.Substring($repoRoot.Length + 1))
        }
    }

    $solution = [IO.File]::ReadAllText((Join-Path $repoRoot 'JarSystem.slnx'))
    if ($solution -notmatch 'test\\JarSystem\.Tests\\JarSystem\.Tests\.csproj') {
        Add-Failure 'xUnit project is missing from JarSystem.slnx'
    }

    $requiredPaths = @(
        (Join-Path $PSScriptRoot '_contract\state_keys.baseline.txt'),
        (Join-Path $PSScriptRoot '_contract\state.schema.json'),
        (Join-Path $repoRoot 'src\JarSystem.Web\wwwroot\js\contract.d.ts'),
        (Join-Path $repoRoot 'config\world.json'),
        (Join-Path $repoRoot 'config\scenario.sample.json')
    )
    foreach ($requiredPath in $requiredPaths) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            Add-Failure "required verification asset missing: $requiredPath"
        }
    }

    if (-not (Test-Path -LiteralPath $LogPath)) {
        Add-Failure "verification log missing: $LogPath"
    }
    else {
        $log = [IO.File]::ReadAllText($LogPath)

        $legacy = [regex]::Match($log, '=== 0 bugs / 2000 random \+ 0 bugs / 3000 capacity \+ (\d+) deterministic ===')
        if (-not $legacy.Success) {
            Add-Failure 'legacy determinism summary missing'
        }
        elseif ([int]$legacy.Groups[1].Value -lt 32) {
            Add-Failure ("deterministic case count fell below 32: {0}" -f $legacy.Groups[1].Value)
        }

        $xunit = [regex]::Match($log, 'JarSystem\.Tests\s+Total:\s*(\d+), Errors: 0, Failed: 0, Skipped: 0, Not Run: 0')
        if (-not $xunit.Success) {
            Add-Failure 'xUnit focused summary missing or not clean'
        }
        elseif ([int]$xunit.Groups[1].Value -lt 22) {
            Add-Failure ("xUnit test count fell below 22: {0}" -f $xunit.Groups[1].Value)
        }

        foreach ($marker in @(
            'CONFIG-WORLD-MATCH',
            'PROTOCOL-OK',
            'SOLVER-OK',
            'MODDIFF-OK',
            'REPLAY-OK',
            'SCENARIO-OK'
        )) {
            if ($log -notmatch [regex]::Escape($marker)) {
                Add-Failure "evidence marker missing: $marker"
            }
        }

        if ($log -match 'SNAPSHOT-MISMATCH|SNAPSHOT-NO-BASELINE|DTS-DEGRADED|STEP-FAILED|JS-SYNTAX-ERROR') {
            Add-Failure 'verification log contains a failure marker'
        }
        if ($log -match '(?m)\bwarning (CS|MSB|NETSDK)\d+') {
            Add-Failure 'verification log contains a compiler or MSBuild warning'
        }

        if ((Get-Command node -ErrorAction SilentlyContinue) -and ($log -notmatch '(?m)^JS-OK\s*$')) {
            Add-Failure 'Node is installed but JS-OK is absent'
        }
    }

    $buildLogPath = Join-Path $PSScriptRoot '_build_output.txt'
    if (-not (Test-Path -LiteralPath $buildLogPath)) {
        Add-Failure 'build log missing'
    }
    elseif ([IO.File]::ReadAllText($buildLogPath) -match '(?m)\bwarning (CS|MSB|NETSDK)\d+') {
        Add-Failure 'build log contains a compiler or MSBuild warning'
    }
}
finally {
    Pop-Location
}

if ($failures.Count -gt 0) {
    Write-Output ("QUALITY-GATE-FAILED ({0})" -f $failures.Count)
    exit 1
}

Write-Output 'QUALITY-OK'
exit 0
