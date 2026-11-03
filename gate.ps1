param([string]$Goal, [string]$Probe, [string]$Only)

$ErrorActionPreference = 'Continue'
$gateCommon = $env:GATE_LIBRARY
if (-not $gateCommon -or -not (Test-Path -LiteralPath $gateCommon -PathType Leaf)) {
    Write-Host 'GATE: FAILED (GATE_LIBRARY names no gate library script; README.md, section Gate, says how to plug one in or run the steps without one)'
    exit 1
}
. $gateCommon
$gateOutput = if (Get-Command Get-GateOutputDirectory -ErrorAction SilentlyContinue) {
    Get-GateOutputDirectory (Split-Path -Leaf $PSScriptRoot)
} else {
    (New-Item -ItemType Directory -Force -Path (Join-Path ([IO.Path]::GetTempPath()) "claude\crgolden-gates\$(Split-Path -Leaf $PSScriptRoot)")).FullName
}
Register-GateSteps @('Begin Sonar analysis', 'Build with dotnet', 'Restore local tools', 'jb inspectcode',
    'Run unit tests with coverage', 'Local Redis (WSL) for the integration tier', 'Run integration tests with coverage', 'End Sonar analysis',
    'Fail on open Sonar issues')
Register-StepInputs @{
    'Begin Sonar analysis'                       = @('*')
    'Build with dotnet'                          = @('*')
    'Restore local tools'                        = @('dotnet-tools.json')
    'jb inspectcode'                             = @('*')
    'Run unit tests with coverage'               = @('*', '!Manuals.Tests.Integration/*')
    'Local Redis (WSL) for the integration tier' = @('*', '!Manuals.Tests.Unit/*')
    'Run integration tests with coverage'        = @('*', '!Manuals.Tests.Unit/*')
    'End Sonar analysis'                         = @('*')
    'Fail on open Sonar issues'                  = @('*')
}
$repo = $PSScriptRoot
$sarif = (Join-Path $gateOutput 'manuals-inspect.sarif')
$unitTrx = Join-Path $repo 'Manuals.Tests.Unit\bin\Release\net10.0\TestResults\unit-tests.trx'
$integrationTrx = Join-Path $repo 'Manuals.Tests.Integration\bin\Release\net10.0\TestResults\integration-tests.trx'
$sonarBranch = Get-SonarBranchName
$beginSonar = "Begin Sonar analysis (branch $sonarBranch)"
$build = 'Build with dotnet (Release, RestoreLockedMode)'
$endSonar = 'End Sonar analysis (quality gate waited)'
$sonarIssues = 'Fail on open Sonar issues'
$unitStep = 'Run unit tests with coverage (Category=Unit)'
$integrationStep = 'Run integration tests with coverage (Category=Integration)'
$redisStep = 'Local Redis (WSL) for the integration tier'
$env:TZ = 'UTC'
if ($env:TZ -ne 'UTC') { Write-Host 'GATE: FAILED (TZ pin)'; exit 1 }
Set-Location $repo
$probeSteps = @('Run unit tests with coverage', 'Run integration tests with coverage')
if ($Only -and -not $Probe) { Write-Host 'PROBE: FAILED (-Only names the one test a probe runs, so it needs -Probe)'; exit 1 }
if ($Probe -and ($Probe.Trim() -cnotin $probeSteps -or [string]::IsNullOrWhiteSpace($Only) -or $Only.Contains(','))) {
    Write-Host "PROBE: FAILED (a probe runs exactly one test: -Probe '<$($probeSteps -join ' | ')>' -Only '<fully qualified test method>')"
    exit 1
}
$gateState = @{ Probe = $Probe }
if ((Get-Command Initialize-GateState).Parameters.ContainsKey('Only')) { $gateState['Only'] = $Only }
Initialize-GateState 'Manuals' $repo @gateState
Resolve-OwedSteps
Invoke-CatalogSteps

function Invoke-ProbeTest([string]$step, [string]$project) {
    $probeResults = Join-Path $gateOutput 'probe-results'
    $probeTrx = Join-Path $probeResults 'probe.trx'
    if (Test-Path -LiteralPath $probeTrx) { Remove-Item -LiteralPath $probeTrx -Force }
    $global:LASTEXITCODE = $null
    dotnet test --project $project --no-build --configuration Release -- --filter-method $Only --report-xunit-trx --report-xunit-trx-filename probe.trx --results-directory=$probeResults
    $probeExit = $global:LASTEXITCODE
    if (-not (Test-Path -LiteralPath $probeTrx)) { Stop-Gate $step "no TRX (exit $probeExit)" }
    [xml]$probeDoc = Get-Content -LiteralPath $probeTrx -Raw
    $counters = $probeDoc.TestRun.ResultSummary.Counters
    if ($probeExit -ne 0 -or [int]$counters.executed -ne 1 -or [int]$counters.failed -ne 0) {
        Stop-Gate $step "a probe runs one test, and '$Only' executed $($counters.executed), failed $($counters.failed) (exit $probeExit)"
    }
    Write-Row $step 'PASS' "one test, '$Only', passed (the last build's binaries; a probe builds nothing)"
}

$sonarCarried = Test-StepCarried $sonarIssues
if ($sonarCarried) {
    $null = Test-StepCarried $beginSonar
    $null = Test-StepCarried $build
    $null = Test-StepCarried $endSonar
}
else {
    $sonarStartedAt = [DateTimeOffset]::UtcNow
    $env:JAVA_HOME = Get-SonarScannerJavaHome $beginSonar
    $global:LASTEXITCODE = $null
    dotnet-sonarscanner begin /k:"crgolden_Manuals" /o:"crgolden" /d:sonar.host.url="https://sonarcloud.io" /d:sonar.cs.opencover.reportsPaths="coverage.opencover.xml" /d:sonar.cs.vscoveragexml.reportsPaths="coverage-integration.xml" /d:sonar.exclusions="**/bin/**,**/obj/**" /d:sonar.coverage.exclusions="**/Program.cs,**/gate.ps1" /d:sonar.qualitygate.wait=true /d:sonar.scanner.skipJreProvisioning=true /d:sonar.branch.name="$sonarBranch"
    $null = Test-Exit $beginSonar

    $global:LASTEXITCODE = $null
    dotnet build --no-incremental --configuration Release /p:RestoreLockedMode=true -warnaserror
    $null = Test-Exit $build
}

$global:LASTEXITCODE = $null
dotnet tool restore
$null = Test-Exit 'Restore local tools (dotnet tool restore)'

if (-not (Test-StepCarried 'jb inspectcode')) {
    if (Test-Path $sarif) { Remove-Item $sarif -Force }
    dotnet jb inspectcode "$repo\Manuals.slnx" --no-build -e=WARNING --caches-home="$(New-InspectCodeCaches $gateOutput)" --output="$sarif"
    Test-Sarif $sarif
}

if ($Only -and -not (Test-StepCarried $unitStep)) { Invoke-ProbeTest $unitStep 'Manuals.Tests.Unit' }
elseif (-not (Test-StepCarried $unitStep)) {
    if (Test-Path $unitTrx) { Remove-Item $unitTrx -Force }
    $global:LASTEXITCODE = $null
    dotnet coverlet Manuals.Tests.Unit\bin\Release\net10.0 `
        --target "dotnet" `
        --targetargs "test --project Manuals.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit --stop-on-fail on --report-xunit-trx --report-xunit-trx-filename unit-tests.trx --results-directory=Manuals.Tests.Unit/bin/Release/net10.0/TestResults" `
        --format opencover --output "coverage.opencover.xml" `
        --skipautoprops --exclude-by-attribute GeneratedCodeAttribute --exclude-by-file "**/obj/**" `
        --exclude-by-file "**/Program.cs" --does-not-return-attribute DoesNotReturnAttribute --include "[Manuals]*"
    Test-Trx $unitStep $unitTrx $global:LASTEXITCODE -floor 1
}

if (Test-StepCarried $integrationStep) {
    Write-Row $redisStep 'NOT RUN' 'the integration tier carried its verdict, so no Redis is needed'
}
else {
    $redisFromEnvironment = -not [string]::IsNullOrWhiteSpace($env:RedisHost)
    if ($redisFromEnvironment -and [string]::IsNullOrWhiteSpace($env:RedisPort)) { Stop-Gate $redisStep 'RedisHost is set in the environment without RedisPort' }
    $wslSession = $null
    if (-not $redisFromEnvironment) {
        $wslSessionStart = [Diagnostics.ProcessStartInfo]::new('wsl.exe', '--exec sleep infinity')
        $wslSessionStart.UseShellExecute = $true
        $wslSessionStart.WindowStyle = 'Hidden'
        $wslSession = [Diagnostics.Process]::Start($wslSessionStart)
    }
    try {
        $redisSettings = Get-Content -Raw (Join-Path $repo 'Manuals\appsettings.Development.json') | ConvertFrom-Json
        $redisHost = if ($redisFromEnvironment) { $env:RedisHost } else { $redisSettings.RedisHost }
        $redisPort = if ($redisFromEnvironment) { $env:RedisPort } else { $redisSettings.RedisPort }
        $redisEndpoint = "${redisHost}:$redisPort"
        $redisAvailable = $false
        $redisDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
        while (-not $redisAvailable -and -not ($wslSession -and $wslSession.HasExited) -and [DateTimeOffset]::UtcNow -lt $redisDeadline) {
            $redisProbe = [Net.Sockets.TcpClient]::new()
            try {
                $redisAvailable = $redisProbe.ConnectAsync($redisHost, [int]$redisPort).Wait(1000)
            }
            catch [AggregateException] {
                Start-Sleep -Seconds 1
            }
            finally {
                $redisProbe.Dispose()
            }
        }

        if ($redisAvailable -and $redisFromEnvironment) {
            Write-Row $redisStep 'PASS' "RedisHost taken from the environment, and $redisEndpoint accepts connections"
        }
        elseif ($redisAvailable) {
            Write-Row $redisStep 'PASS' "a WSL session is open and $redisEndpoint accepts connections"
        }
        elseif ($wslSession -and $wslSession.HasExited) {
            Write-Row $redisStep 'SKIPPED' "WSL could not be started (wsl.exe exit $($wslSession.ExitCode))"
            Write-Row $integrationStep 'SKIPPED' 'needs WSL Redis, and WSL could not be started'
        }
        elseif ($redisFromEnvironment) {
            Stop-Gate $redisStep "nothing accepted a connection on $redisEndpoint (RedisHost from the environment) within 60 seconds"
        }
        else {
            Stop-Gate $redisStep "WSL is running but nothing accepted a connection on $redisEndpoint within 60 seconds"
        }

        if ($redisAvailable -and $Only) {
            $env:ASPNETCORE_ENVIRONMENT = 'Development'
            $env:RedisDatabase ??= '1'
            Invoke-ProbeTest $integrationStep 'Manuals.Tests.Integration'
        }
        elseif ($redisAvailable) {
            if (Test-Path $integrationTrx) { Remove-Item $integrationTrx -Force }
            $env:ASPNETCORE_ENVIRONMENT = 'Development'
            $env:RedisDatabase ??= '1'
            $global:LASTEXITCODE = $null
            dotnet-coverage collect `
                "dotnet test --project Manuals.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration --stop-on-fail on --report-xunit-trx --report-xunit-trx-filename integration-tests.trx --results-directory=Manuals.Tests.Integration/bin/Release/net10.0/TestResults" `
                -f xml -o "coverage-integration.xml" -s "coverage.settings.xml"
            Test-Trx $integrationStep $integrationTrx $global:LASTEXITCODE -floor 1
        }
    }
    finally {
        if ($wslSession) {
            if (-not $wslSession.HasExited) { $wslSession.Kill($true) }
            $wslSession.Dispose()
        }
    }
}

if (-not $sonarCarried) {
    $global:LASTEXITCODE = $null
    dotnet-sonarscanner end
    $null = Test-Exit $endSonar
    Test-SonarIssues $sonarIssues 'crgolden_Manuals' $sonarBranch $sonarStartedAt
}

Write-Row 'Upload test results / dotnet publish / Upload artifact / deploy' 'NOT RUN' 'delivery steps, not checks'
Complete-Gate
