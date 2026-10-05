# LitSSH MCP 发布脚本
# 生成「App(含内置 MCP 服务器)」与「独立 MCP 服务器」两类产物。
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version v1.2.0
#   powershell ... -File scripts\build-release.ps1 -Version v1.2.0 -FrameworkDependent
param(
    [string]$Version = "dev",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $root "release\$Version"
$selfContained = (-not $FrameworkDependent).ToString().ToLower()

function Publish-Project($proj, $out) {
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Write-Host "publish $proj -> $out"
    dotnet publish $proj -c Release -r $Runtime --self-contained $selfContained -o $out --nologo -v q
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

Write-Host "done. 产物位于: $releaseDir"
