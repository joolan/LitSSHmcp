[CmdletBinding()]
param(
    [ValidateSet("catalog", "authorization", "paths", "commands", "secrets", "web", "all")]
    [string]$Domain = "all",
    [string]$Configuration = "Release",
    [string]$Solution = "LitSSHmcp.slnx"
)

$ErrorActionPreference = "Stop"

$domains = @(
    @{
        Name    = "catalog"
        Filter  = "FullyQualifiedName~ToolCatalogConsistencyTests|FullyQualifiedName~UsageGuideFilterTests|FullyQualifiedName~ToolGroupsTests|FullyQualifiedName~McpProtocolIntegrationTests"
        Minimum = 13
    },
    @{
        Name    = "authorization"
        Filter  = "FullyQualifiedName~McpAuthorizationFilterTests|FullyQualifiedName~McpWriteGateFilterTests|FullyQualifiedName~ToolTargetResolverTests|FullyQualifiedName~CallerDataScopeTests|FullyQualifiedName~CallerScopeTests|FullyQualifiedName~ApprovalScopeTests|FullyQualifiedName~LocalApprovalScopeTests"
        Minimum = 90
    },
    @{
        Name    = "paths"
        Filter  = "FullyQualifiedName~PathGuardTests|FullyQualifiedName~RemotePathGuardTests|FullyQualifiedName~WebJobsRootTests|FullyQualifiedName~WebFileEndpointTests|FullyQualifiedName~FileTransferToolsSessionLandingTests|FullyQualifiedName~RemoteFileBrowserTerminalCommandTests"
        Minimum = 58
    },
    @{
        Name    = "commands"
        Filter  = "FullyQualifiedName~LocalCommandPolicyTests|FullyQualifiedName~PosixCommandLineTests|FullyQualifiedName~ScriptRiskAnalyzerTests|FullyQualifiedName~CommandFilterServiceTests|FullyQualifiedName~RedisCommandParserTests|FullyQualifiedName~DockerCommandDefaultsTests|FullyQualifiedName~LocalShellSessionToolTests|FullyQualifiedName~LocalTerminalToolTests"
        Minimum = 126
    },
    @{
        Name    = "secrets"
        Filter  = "FullyQualifiedName~SecretRedactorTests|FullyQualifiedName~ResultMaskerTests|FullyQualifiedName~SqlRedactorTests|FullyQualifiedName~DpapiSecretProtectorTests|FullyQualifiedName~SessionLoggerTests|FullyQualifiedName~AuditChainTests"
        Minimum = 30
    },
    @{
        Name    = "web"
        Filter  = "FullyQualifiedName~WebSocketGuardTests|FullyQualifiedName~WebSocketTests|FullyQualifiedName~RateLimitTests|FullyQualifiedName~SecurityHeadersTests|FullyQualifiedName~ImageAttachmentValidatorTests|FullyQualifiedName~WebPolicyTests|FullyQualifiedName~WebConnectorSubsetTests|FullyQualifiedName~WebForwardedHeadersTests|FullyQualifiedName~MarkdownSanitizerTests"
        Minimum = 31
    }
)

$selected = if ($Domain -eq "all") { $domains } else { @($domains | Where-Object { $_.Name -eq $Domain }) }
$root = Join-Path (Get-Location) ("artifacts/risk-gates/" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $root -Force | Out-Null

$failures = @()
$summary = @()

foreach ($entry in $selected) {
    $name = $entry.Name
    $filter = $entry.Filter
    $minimum = [int]$entry.Minimum
    $resultsDir = Join-Path $root $name
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

    Write-Host ("=== risk gate: {0} (min {1} tests) ===" -f $name, $minimum)

    $testArgs = @(
        "test", $Solution,
        "-c", $Configuration,
        "--no-build",
        "--filter", $filter,
        "--logger", "trx",
        "--results-directory", $resultsDir
    )

    & dotnet @testArgs
    $exitCode = $LASTEXITCODE

    $trxFiles = @(Get-ChildItem -LiteralPath $resultsDir -Filter *.trx)
    $total = 0
    $passed = 0
    $failed = 0
    $skipped = 0

    foreach ($trxFile in $trxFiles) {
        [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Encoding UTF8
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($null -ne $counters) {
            $total += [int]$counters.total
            $passed += [int]$counters.passed
            $failed += [int]$counters.failed
            $skipped += [int]$counters.skipped
        }
    }

    if ($trxFiles.Count -eq 0) {
        $failures += "$name (no trx produced)"
    }

    $status = "PASS"
    if ($exitCode -ne 0) {
        $status = "FAIL"
        $failures += "$name (dotnet test exit $exitCode)"
    }
    if ($failed -gt 0) {
        $status = "FAIL"
        $failures += "$name ($failed failed)"
    }
    if ($total -lt $minimum) {
        $status = "FAIL"
        $failures += ("{0} (only {1} tests, expected >= {2})" -f $name, $total, $minimum)
    }

    $summary += [pscustomobject]@{
        Domain  = $name
        Total   = $total
        Passed  = $passed
        Failed  = $failed
        Skipped = $skipped
        Minimum = $minimum
        Status  = $status
    }
    Write-Host ""
}

""
"{0,-16} {1,8} {2,8} {3,8} {4,8} {5,8}  {6}" -f "Domain", "Total", "Passed", "Failed", "Skipped", "Minimum", "Status"
foreach ($row in $summary) {
    "{0,-16} {1,8} {2,8} {3,8} {4,8} {5,8}  {6}" -f $row.Domain, $row.Total, $row.Passed, $row.Failed, $row.Skipped, $row.Minimum, $row.Status
}

""
"TRX results: $root"

if ($failures.Count -gt 0) {
    Write-Error ("Risk gates failed: " + ($failures -join "; "))
    exit 1
}

"Risk gates passed."
exit 0
