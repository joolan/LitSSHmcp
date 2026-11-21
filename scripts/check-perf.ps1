# LitSSH 发布性能门禁
# 默认发布 App 到 artifacts/perf-gate-app，再运行 perf-baseline.ps1；
# 启动中位数或空闲内存超过预算时脚本返回非 0。
#
# 示例：
#   powershell -ExecutionPolicy Bypass -File scripts\check-perf.ps1
#   powershell ... -File scripts\check-perf.ps1 -Exe release\v1.9.0\App\LitSSHmcp.App.exe
#   powershell ... -File scripts\check-perf.ps1 -NoPublish -StartupBudgetMs 1600 -MemoryBudgetMB 400

param(
    [string]$Exe,
    [switch]$NoPublish,
    [int]$Runs = 3,
    [int]$SettleSeconds = 6,
    [int]$StartupBudgetMs = 1600,
    [int]$MemoryBudgetMB = 400,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\perf-gate-app"

function Resolve-ReleaseExe {
    $candidates = @(
        (Join-Path $root "src\LitSSHmcp.App\bin\Release\net8.0-windows10.0.19041.0\LitSSHmcp.App.exe"),
        (Join-Path $root "publish-app\LitSSHmcp.App.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw "未找到 Release App；请先构建或去掉 -NoPublish 让脚本自动发布。"
}

if (-not $Exe) {
    if ($NoPublish) {
        $Exe = Resolve-ReleaseExe
    }
    else {
        if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
        Write-Host "publishing App -> $publishDir"
        dotnet publish (Join-Path $root "src\LitSSHmcp.App\LitSSHmcp.App.csproj") `
            -c Release -r win-x64 --self-contained true -o $publishDir --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "publish failed" }
        $Exe = Join-Path $publishDir "LitSSHmcp.App.exe"
    }
}

if (-not (Test-Path $Exe)) { throw "找不到 App: $Exe" }

Write-Host "perf gate: exe=$Exe runs=$Runs startup<=${StartupBudgetMs}ms memory<=${MemoryBudgetMB}MB"

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "perf-baseline.ps1") `
    -Exe $Exe -Runs $Runs -SettleSeconds $SettleSeconds `
    -StartupBudgetMs $StartupBudgetMs -MemoryBudgetMB $MemoryBudgetMB

$exit = $LASTEXITCODE
if (-not $KeepArtifacts -and -not $NoPublish -and (Test-Path $publishDir)) {
    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
}

if ($exit -ne 0) {
    Write-Host "PERF GATE FAILED" -ForegroundColor Red
    exit $exit
}
Write-Host "PERF GATE PASSED" -ForegroundColor Green
