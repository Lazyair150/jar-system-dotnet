# 依赖漏洞 + 秘密扫描门（SDL / ASVS F-02、I-03 的执行物）
#
# 目标：把"依赖里有没有已知高危漏洞""仓库里有没有硬编码的秘密"变成**可阻断**的检查。
#
# 第一部分：依赖漏洞（dotnet list package --vulnerable，NuGet 官方数据源）
#   - 有 high/critical => SEC-SCAN-FAILED，exit 1（verify.bat 据此置 VERDICT=FAIL）
#   - 干净             => SEC-OK (projects=N, vulnerable=0)
#   - 拿不到数据源     => SEC-SCAN-SKIPPED，exit 0（离线不假报成功，也不假阻断）
#
# 第二部分：秘密扫描（正则在源码/配置/文档里找"看起来是当前可用的凭证"）
#   - 命中 => SEC-SECRETS-FAILED，exit 1
#   - 干净 => SEC-SECRETS-OK (scanned=N files)
#   这不是真正的熵检测（那需要 gitleaks / trufflehog），但能挡住"手滑把 key 提交进来"这类真实事故，
#   而且不依赖任何外部工具 —— 本仓库要求离线可验证。这个缺口如实记在 docs/security/ 里。
#
# 为什么允许"跳过"依赖扫描：本仓库是本地运行的世界，离线开发是常态；
# 把"没有网络"当成"有漏洞"会让门禁失去意义，把它当成"安全"则是撒谎 ——
# 所以两者都不做，只如实写一行 SEC-SCAN-SKIPPED，让人看得见这次没验到。
#
# 注意（踩过的坑，两次）：
#   1) 本文件必须**带 UTF-8 BOM**。Windows PowerShell 5.1 对无 BOM 的 UTF-8 按 ANSI 解码，
#      注释里的中文被拆成非法字节序列，脚本静默中断（只打一行就退出、退出码 0）。
#   2) 不要用 $ErrorActionPreference='Stop' + `& 原生命令 2>&1`：dotnet 的 stderr 横幅会变成
#      终止错误，同样静默中断。保持 Continue，stderr 丢掉，只看 $LASTEXITCODE 与 stdout 的 JSON。

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repo 'JarSystem.slnx'

# ---------- 第一部分：依赖漏洞 ----------
if (Test-Path $solution) {
    $raw = & dotnet list $solution package --vulnerable --include-transitive --format json 2>$null
    $exit = $LASTEXITCODE
    $text = ($raw | Out-String)

    if ($exit -ne 0 -or $text.TrimStart() -notmatch '^\{') {
        Write-Output "SEC-SCAN-SKIPPED (vulnerability source unavailable, exit=$exit)"
    }
    else {
        $report = $null
        try { $report = $text | ConvertFrom-Json } catch { $report = $null }

        if ($null -eq $report) {
            Write-Output "SEC-SCAN-SKIPPED (scan output is not JSON)"
        }
        else {
            $blocking = @()
            $projects = 0
            foreach ($project in $report.projects) {
                $projects++
                foreach ($framework in $project.frameworks) {
                    foreach ($group in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                        if ($null -eq $group) { continue }
                        if ($null -eq $group.vulnerabilities) { continue }
                        foreach ($v in $group.vulnerabilities) {
                            $severity = "$($v.severity)"
                            if ($severity -eq 'High' -or $severity -eq 'Critical') {
                                $blocking += "$($project.path) $($group.id) $($group.resolvedVersion) $severity"
                            }
                        }
                    }
                }
            }

            if ($blocking.Count -gt 0) {
                Write-Output "SEC-SCAN-FAILED (high/critical vulnerable packages: $($blocking.Count))"
                $blocking | ForEach-Object { Write-Output "  $_" }
                exit 1
            }
            # --vulnerable 只列出"有漏洞的包"，干净时列表为空：这里报的是"扫了几个项目、几项命中"，
            # 而不是"依赖总数"，免得 0 被读成"没有依赖"。
            Write-Output "SEC-OK (projects=$projects, vulnerable=0)"
        }
    }
}
else {
    Write-Output "SEC-SCAN-SKIPPED (solution not found: $solution)"
}

# ---------- 第二部分：秘密扫描 ----------
# 只扫源码/配置/文档；bin/obj/node_modules/.git 不算（构建产物与第三方代码不是作者写的东西）。
# 模式刻意保守：宁可漏，也不要因为"看起来像"而误报 —— 误报会让人开始忽略这条门。
$secretPatterns = @(
    'BEGIN [A-Z ]*PRIVATE KEY',
    'AKIA[0-9A-Z]{16}',
    'ASIA[0-9A-Z]{16}',
    'sk-[A-Za-z0-9_\-]{24,}',
    'ghp_[A-Za-z0-9]{24,}',
    'github_pat_[A-Za-z0-9_]{24,}',
    'xox[baprs]-[A-Za-z0-9\-]{12,}',
    'AIza[0-9A-Za-z_\-]{35}'
)

$files = @()
foreach ($ext in @('.cs', '.js', '.ts', '.json', '.md', '.txt', '.bat', '.ps1', '.csproj', '.slnx', '.yml', '.yaml')) {
    $files += @(Get-ChildItem -LiteralPath $repo -Recurse -File -Filter "*$ext" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules|\.git)[\\/]' })
}

$secretHits = @()
foreach ($file in $files) {
    $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
    if ($null -eq $content) { continue }
    foreach ($pattern in $secretPatterns) {
        if ($content -match $pattern) {
            $relative = $file.FullName.Substring($repo.Length).TrimStart('\', '/')
            $secretHits += "$relative matches /$pattern/"
        }
    }
}

if ($secretHits.Count -gt 0) {
    Write-Output "SEC-SECRETS-FAILED (possible committed credentials: $($secretHits.Count))"
    $secretHits | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output "SEC-SECRETS-OK (scanned=$($files.Count) files, patterns=$($secretPatterns.Count))"
exit 0
