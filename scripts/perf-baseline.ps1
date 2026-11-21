<#
.SYNOPSIS
    LitSSH 桌面端启动性能基准：冷/热启动耗时、空闲内存，可作为发布前回归门禁。

.DESCRIPTION
    以 LITSSH_PERF=1 启动 App，等待主窗口完成首帧后从
    %APPDATA%\LitSSH\topology-perf.log 读取 app.startup.begin -> mainwindow.shown 的耗时，
    并记录 working set / private memory。默认跑 3 次取中位数。

    运行前请关闭已打开的 LitSSHmcp.App（单实例会直接退出，导致测不到启动日志）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\perf-baseline.ps1

.EXAMPLE
    # 发布门禁：中位启动超过 1500ms 或空闲内存超过 350MB 时失败
    powershell -ExecutionPolicy Bypass -File scripts\perf-baseline.ps1 -Runs 5 -StartupBudgetMs 1500 -MemoryBudgetMB 350

.EXAMPLE
    # 指定自包含发布产物
    powershell -ExecutionPolicy Bypass -File scripts\perf-baseline.ps1 -Exe release\v1.9.0\App\LitSSHmcp.App.exe
#>
param(
    [string]$Exe,
    [int]$Runs = 3,
    [int]$SettleSeconds = 8,
    [int]$StartupBudgetMs = 0,
    [int]$MemoryBudgetMB = 0,
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"

function Resolve-AppExe([string]$path) {
    if ($path) {
        $resolved = (Resolve-Path $path).Path
        if (-not (Test-Path $resolved)) { throw "找不到 App: $path" }
        return $resolved
    }

    $root = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $root "src\LitSSHmcp.App\bin\Release\net8.0-windows10.0.19041.0\LitSSHmcp.App.exe"),
        (Join-Path $root "publish-app\LitSSHmcp.App.exe")
    )
    $releaseRoot = Join-Path $root "release"
    if (Test-Path $releaseRoot) {
        $candidates += Get-ChildItem $releaseRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object { Join-Path $_.FullName "App\LitSSHmcp.App.exe" }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }
    throw "未找到 LitSSHmcp.App.exe；请先 dotnet build -c Release 或 dotnet publish，并用 -Exe 指定路径。"
}

function Read-PerfLines([string]$logPath, [int]$skip) {
    if (-not (Test-Path $logPath)) { return @() }
    return @(Get-Content $logPath -ErrorAction SilentlyContinue | Select-Object -Skip $skip)
}

function Get-Stamp([string]$line) {
    if (-not $line) { return $null }
    $token = ($line -split '\s+')[0]
    return [datetime]::ParseExact($token, 'HH:mm:ss.fff', $null)
}

function Get-Median([double[]]$values) {
    if ($values.Count -eq 0) { return 0 }
    $sorted = $values | Sort-Object
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

if ($Runs -lt 1) { throw "-Runs 必须 >= 1" }

$exePath = Resolve-AppExe $Exe
$appDir = Split-Path -Parent $exePath
$exeName = [System.IO.Path]::GetFileNameWithoutExtension($exePath)
$logPath = Join-Path $env:APPDATA "LitSSH\topology-perf.log"

$running = Get-Process -Name $exeName -ErrorAction SilentlyContinue
if ($running) {
    throw "检测到 $exeName 正在运行（PID: $($running.Id -join ',')）。请先退出 App，单实例模式下新进程会立即退出。"
}

Write-Host "App     : $exePath"
Write-Host "PerfLog : $logPath"
Write-Host "Runs    : $Runs (settle ${SettleSeconds}s)`n"

$startupValues = New-Object System.Collections.Generic.List[double]
$memoryValues = New-Object System.Collections.Generic.List[double]
$rows = @()

for ($i = 1; $i -le $Runs; $i++) {
    $before = 0
    if (Test-Path $logPath) { $before = (Get-Content $logPath).Count }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.WorkingDirectory = $appDir
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["LITSSH_PERF"] = "1"

    $proc = [System.Diagnostics.Process]::Start($psi)
    try {
        Start-Sleep -Seconds $SettleSeconds

        $live = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if (-not $live) { throw "第 $i 次启动后进程已退出（可能被单实例/配置/应用锁拦截）。" }

        $wsMB = [math]::Round($live.WorkingSet64 / 1MB, 1)
        $privMB = [math]::Round($live.PrivateMemorySize64 / 1MB, 1)

        $lines = Read-PerfLines $logPath $before
        $beginLine = ($lines | Select-String 'app\.startup\.begin' | Select-Object -Last 1).Line
        $shownLine = ($lines | Select-String 'mainwindow\.shown' | Select-Object -Last 1).Line
        $ctorLine = ($lines | Select-String 'mainwindow\.ctor: ' | Select-Object -Last 1).Line
        $createLine = ($lines | Select-String 'mainwindow\.create: ' | Select-Object -Last 1).Line

        if (-not $beginLine -or -not $shownLine) {
            throw "第 $i 次未在 perf 日志中找到启动记录；请确认 LITSSH_PERF=1 且 App 正常完成启动。"
        }

        $startupMs = ((Get-Stamp $shownLine) - (Get-Stamp $beginLine)).TotalMilliseconds
        if ($startupMs -lt 0) { $startupMs += 86400000 }   # 跨午夜
        $startupValues.Add($startupMs)
        $memoryValues.Add($wsMB)

        $rows += [pscustomobject]@{
            Run      = $i
            Startup  = [math]::Round($startupMs)
            Ctor     = if ($ctorLine) { [math]::Round((($ctorLine -split ': ')[1] -replace ' ms.*$', '')) } else { $null }
            Create   = if ($createLine) { [math]::Round((($createLine -split ': ')[1] -replace ' ms.*$', '')) } else { $null }
            WSMb     = $wsMB
            PrivMb   = $privMB
            Threads  = $live.Threads.Count
        }

        Write-Host ("run {0}: startup={1:N0}ms  WS={2}MB  private={3}MB  threads={4}" -f $i, $startupMs, $wsMB, $privMB, $live.Threads.Count)
    }
    finally {
        if ($KeepOpen -and $i -eq $Runs) {
            Write-Host "已保留最后一次进程（PID $($proc.Id)），请手动退出。"
        }
        else {
            try { if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null } } catch { }
            # 给单实例互斥/托盘图标/子进程一点释放时间，避免影响下一轮。
            Start-Sleep -Milliseconds 800
        }
    }
}

$medianStartup = Get-Median ([double[]]$startupValues.ToArray())
$medianMemory = Get-Median ([double[]]$memoryValues.ToArray())

Write-Host ""
$rows | Format-Table -AutoSize

Write-Host ("median startup : {0:N0} ms" -f $medianStartup)
Write-Host ("median WS      : {0} MB" -f $medianMemory)

$failed = $false
if ($StartupBudgetMs -gt 0 -and $medianStartup -gt $StartupBudgetMs) {
    Write-Host ("FAIL: 启动中位数 {0:N0}ms 超过预算 {1}ms" -f $medianStartup, $StartupBudgetMs) -ForegroundColor Red
    $failed = $true
}
if ($MemoryBudgetMB -gt 0 -and $medianMemory -gt $MemoryBudgetMB) {
    Write-Host ("FAIL: 空闲内存中位数 {0}MB 超过预算 {1}MB" -f $medianMemory, $MemoryBudgetMB) -ForegroundColor Red
    $failed = $true
}

if ($failed) { exit 1 }
Write-Host "OK" -ForegroundColor Green
exit 0
