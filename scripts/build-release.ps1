# LitSSH MCP 发布脚本
# 生成「App(含内置 MCP 服务器)」「独立 MCP 服务器」「CLI」「Web 助手」四类产物。
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version v1.4.1
#   powershell ... -File scripts\build-release.ps1 -Version v1.4.1 -FrameworkDependent
param(
    [string]$Version = "dev",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $root "release\$Version"
$selfContained = (-not $FrameworkDependent).ToString().ToLower()

# 把 tag（如 v1.2.0）转换为可嵌入程序集的版本号（发布产物「设置 → 关于」会显示）
$versionNumber = $Version -replace '^[vV]', ''
$versionArg = @()
if ($versionNumber -match '^\d+\.\d+') { $versionArg = @("-p:Version=$versionNumber") }

function Publish-Project($proj, $out) {
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Write-Host "publish $proj -> $out"
    dotnet publish $proj -c Release -r $Runtime --self-contained $selfContained -o $out --nologo -v q @versionArg
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

# 1) MCP 服务器（独立产物）
$mcpOut = Join-Path $releaseDir "McpServer"
Publish-Project (Join-Path $root "src\LitSSHmcp.McpServer\LitSSHmcp.McpServer.csproj") $mcpOut

# 2) App（内置 MCP 服务器到 App 的 mcp/ 子目录，AI 助手据此自动探测）
$appOut = Join-Path $releaseDir "App"
Publish-Project (Join-Path $root "src\LitSSHmcp.App\LitSSHmcp.App.csproj") $appOut
$appMcp = Join-Path $appOut "mcp"
if (Test-Path $appMcp) { Remove-Item -Recurse -Force $appMcp }
Copy-Item -Recurse $mcpOut $appMcp

# 3) CLI 终端工具（独立产物）
$cliOut = Join-Path $releaseDir "Cli"
Publish-Project (Join-Path $root "src\LitSSHmcp.Cli\LitSSHmcp.Cli.csproj") $cliOut

# 4) Web 助手（浏览器端，独立进程）
$webOut = Join-Path $releaseDir "Web"
Publish-Project (Join-Path $root "src\LitSSHmcp.Web\LitSSHmcp.Web.csproj") $webOut

Write-Host "done. 产物位于: $releaseDir"
