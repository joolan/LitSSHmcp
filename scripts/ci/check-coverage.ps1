[CmdletBinding()]
param(
    [string]$ResultsDirectory = "artifacts/coverage",
    [double]$OverallMinimumLineRate = 40.0
)

$ErrorActionPreference = "Stop"

$assemblyThresholds = [ordered]@{
    "LitSSHmcp.Core"      = 55.0
    "LitSSHmcp.App"       = 24.5
    "LitSSHmcp.McpServer" = 35.0
    "LitSSHmcp.Agent"     = 73.0
    "LitSSHmcp.Web"       = 81.0
}

if (-not (Test-Path -LiteralPath $ResultsDirectory)) {
    throw "Coverage results directory not found: $ResultsDirectory"
}

$coverageFiles = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter "coverage.cobertura.xml")
if ($coverageFiles.Count -eq 0) {
    throw "No coverage.cobertura.xml found under $ResultsDirectory"
}

# 同一 Core 程序集会被多个测试项目收集到；不同 runner 的 filename 可能是绝对路径、
# 带程序集前缀或相对路径，必须归一化后再按行合并，否则分母被重复放大。
function Normalize-CoverageSource([string]$assembly, [string]$source) {
    $s = $source -replace '/', '\'
    $needle = '\' + $assembly + '\'
    $i = $s.LastIndexOf($needle, [StringComparison]::OrdinalIgnoreCase)
    if ($i -ge 0) { return $s.Substring($i + $needle.Length) }
    if ($s.StartsWith($assembly + '\', [StringComparison]::OrdinalIgnoreCase)) {
        return $s.Substring($assembly.Length + 1)
    }
    return $s
}

$lineHits = @{}
$unparsed = @()

foreach ($file in $coverageFiles) {
    try {
        [xml]$doc = Get-Content -LiteralPath $file.FullName -Encoding UTF8
    }
    catch {
        $unparsed += $file.FullName
        continue
    }

    foreach ($package in $doc.coverage.packages.package) {
        $assembly = [string]$package.name
        foreach ($class in $package.classes.class) {
            $source = Normalize-CoverageSource $assembly ([string]$class.filename)
            foreach ($line in $class.lines.line) {
                $key = "{0}|{1}|{2}" -f $assembly, $source, $line.number
                $hits = [int]$line.hits
                if (-not $lineHits.ContainsKey($key) -or $lineHits[$key] -lt $hits) {
                    $lineHits[$key] = $hits
                }
            }
        }
    }
}

if ($unparsed.Count -gt 0) {
    throw "Unable to parse coverage files: $($unparsed -join ', ')"
}

$stats = @{}
foreach ($key in $lineHits.Keys) {
    $assembly = $key.Split('|')[0]
    if (-not $stats.ContainsKey($assembly)) {
        $stats[$assembly] = [pscustomobject]@{ Covered = 0; Total = 0 }
    }
    $stats[$assembly].Total++
    if ($lineHits[$key] -gt 0) {
        $stats[$assembly].Covered++
    }
}

$totalCovered = 0
$totalLines = 0
$failed = @()

"Coverage file(s): {0}" -f $coverageFiles.Count
""
"{0,-24} {1,10} {2,10} {3,10} {4,8}  {5}" -f "Assembly", "Covered", "Total", "LineRate", "Minimum", "Result"

foreach ($assembly in $assemblyThresholds.Keys) {
    if (-not $stats.ContainsKey($assembly)) {
        $failed += "$assembly (no coverage data)"
        "{0,-24} {1,10} {2,10} {3,10} {4,8}  {5}" -f $assembly, "-", "-", "-", $assemblyThresholds[$assembly], "FAIL"
        continue
    }

    $entry = $stats[$assembly]
    $rate = if ($entry.Total -eq 0) { 0.0 } else { ($entry.Covered / $entry.Total) * 100.0 }
    $totalCovered += $entry.Covered
    $totalLines += $entry.Total

    $minimum = [double]$assemblyThresholds[$assembly]
    $result = if ($rate -ge $minimum) { "PASS" } else { "FAIL" }
    if ($result -eq "FAIL") {
        $failed += ("{0} {1:N1}% < {2:N1}%" -f $assembly, $rate, $minimum)
    }
    "{0,-24} {1,10} {2,10} {3,9:N1}% {4,7:N1}%  {5}" -f $assembly, $entry.Covered, $entry.Total, $rate, $minimum, $result
}

$overallRate = if ($totalLines -eq 0) { 0.0 } else { ($totalCovered / $totalLines) * 100.0 }
""
$overallResult = if ($overallRate -ge $OverallMinimumLineRate) { "PASS" } else { "FAIL" }
"{0,-24} {1,10} {2,10} {3,9:N1}% {4,7:N1}%  {5}" -f "Overall", $totalCovered, $totalLines, $overallRate, $OverallMinimumLineRate, $overallResult
if ($overallResult -eq "FAIL") {
    $failed += ("Overall {0:N1}% < {1:N1}%" -f $overallRate, $OverallMinimumLineRate)
}

if ($failed.Count -gt 0) {
    ""
    Write-Error ("Coverage gate failed: " + ($failed -join "; "))
    exit 1
}

""
"Coverage gate passed."
exit 0
