param([string]$Goal, [string]$Only)

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
$unitTrx = Join-Path $repo 'Manuals.Tests.Unit\bin\Release\net11.0\TestResults\unit-tests.trx'
$integrationTrx = Join-Path $repo 'Manuals.Tests.Integration\bin\Release\net11.0\TestResults\integration-tests.trx'
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
$probeProjects = @('Manuals.Tests.Unit', 'Manuals.Tests.Integration')
if ($Only) {
    $probeFile, $probeTest = $Only -split '::', 2
    $probeFile = "$probeFile".Trim().Replace('\', '/')
    $probeTest = "$probeTest".Trim()
    $probeProject = $probeFile.Split('/')[0]
    if (-not $probeTest -or $probeTest.Contains(',') -or $probeProject -notin $probeProjects -or
            -not (Test-Path -LiteralPath (Join-Path $repo $probeFile) -PathType Leaf)) {
        Write-Host "PROBE: FAILED (a probe runs exactly one test, named by the file it lives in: -Only '<a test file under $($probeProjects -join ' | ')>::<test method>')"
        exit 1
    }
    $probeClass = [IO.Path]::GetFileNameWithoutExtension($probeFile)
}
function Open-IntegrationRedis {
    $fromEnvironment = -not [string]::IsNullOrWhiteSpace($env:RedisHost)
    if ($fromEnvironment -and [string]::IsNullOrWhiteSpace($env:RedisPort)) {
        return [pscustomobject]@{ Session = $null; Available = $false; FromEnvironment = $true; Endpoint = $null; Missing = 'RedisHost is set in the environment without RedisPort' }
    }
    $session = $null
    if (-not $fromEnvironment) {
        $sessionStart = [Diagnostics.ProcessStartInfo]::new('wsl.exe', '--exec sleep infinity')
        $sessionStart.UseShellExecute = $true
        $sessionStart.WindowStyle = 'Hidden'
        $session = [Diagnostics.Process]::Start($sessionStart)
    }
    $settings = Get-Content -Raw (Join-Path $repo 'Manuals\appsettings.Development.json') | ConvertFrom-Json
    $redisHost = if ($fromEnvironment) { $env:RedisHost } else { $settings.RedisHost }
    $redisPort = if ($fromEnvironment) { $env:RedisPort } else { $settings.RedisPort }
    $available = $false
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while (-not $available -and -not ($session -and $session.HasExited) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        $client = [Net.Sockets.TcpClient]::new()
        try { $available = $client.ConnectAsync($redisHost, [int]$redisPort).Wait(1000) }
        catch [AggregateException] { Start-Sleep -Seconds 1 }
        finally { $client.Dispose() }
    }
    return [pscustomobject]@{ Session = $session; Available = $available; FromEnvironment = $fromEnvironment; Endpoint = "${redisHost}:$redisPort"; Missing = $null }
}

function Close-IntegrationRedis($redis) {
    if ($redis.Session) {
        if (-not $redis.Session.HasExited) { $redis.Session.Kill($true) }
        $redis.Session.Dispose()
    }
}

if ($Only) {
    $probeIntegration = $probeProject -eq $probeProjects[1]
    $redis = $null
    if ($probeIntegration) {
        $redis = Open-IntegrationRedis
        if (-not $redis.Available) {
            Close-IntegrationRedis $redis
            Write-Host "PROBE: FAILED ($(if ($redis.Missing) { $redis.Missing } else { "nothing accepted a connection on $($redis.Endpoint) within 60 seconds" }))"
            exit 1
        }
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:RedisDatabase ??= '1'
    }
    try {
        $probeResults = Join-Path $gateOutput 'probe-results'
        $probeTrx = Join-Path $probeResults 'probe.trx'
        if (Test-Path -LiteralPath $probeTrx) { Remove-Item -LiteralPath $probeTrx -Force }
        Write-Host "PROBE: running one test, '$probeTest' of $probeClass in $probeProject; no gate record or verdict is written"
        $global:LASTEXITCODE = $null
        dotnet test --project $probeProject --no-build --configuration Release -- --filter-query "/*/*/$probeClass/$probeTest" --report-xunit-trx --report-xunit-trx-filename probe.trx --results-directory=$probeResults
        $probeExit = $global:LASTEXITCODE
    }
    finally {
        if ($redis) { Close-IntegrationRedis $redis }
    }
    if (-not (Test-Path -LiteralPath $probeTrx)) { Write-Host "PROBE: FAILED (no TRX, exit $probeExit)"; exit 1 }
    [xml]$probeDoc = Get-Content -LiteralPath $probeTrx -Raw
    $counters = $probeDoc.TestRun.ResultSummary.Counters
    if ($probeExit -ne 0 -or [int]$counters.executed -ne 1 -or [int]$counters.failed -ne 0) {
        Write-Host "PROBE: FAILED (a probe runs one test, and '$Only' executed $($counters.executed), failed $($counters.failed), exit $probeExit)"
        exit 1
    }
    Write-Host "PROBE: PASSED for the one test named, against the last build's binaries; no verdict was saved"
    exit 0
}
exit (Invoke-Gate {
Initialize-GateState 'Manuals' $repo
Resolve-OwedSteps
Invoke-CatalogSteps

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
    Assert-StepExit $beginSonar

    $global:LASTEXITCODE = $null
    dotnet build --no-incremental --configuration Release /p:RestoreLockedMode=true -warnaserror
    Assert-StepExit $build
}

$global:LASTEXITCODE = $null
dotnet tool restore
Assert-StepExit 'Restore local tools (dotnet tool restore)'

if (-not (Test-StepCarried 'jb inspectcode')) {
    if (Test-Path $sarif) { Remove-Item $sarif -Force }
    dotnet jb inspectcode "$repo\Manuals.slnx" --no-build -e=WARNING --caches-home="$(New-InspectCodeCaches $gateOutput)" --output="$sarif"
    Test-Sarif $sarif
}

if (-not (Test-StepCarried $unitStep)) {
    if (Test-Path $unitTrx) { Remove-Item $unitTrx -Force }
    $global:LASTEXITCODE = $null
    dotnet coverlet Manuals.Tests.Unit\bin\Release\net11.0 `
        --target "dotnet" `
        --targetargs "test --project Manuals.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit --stop-on-fail on --report-xunit-trx --report-xunit-trx-filename unit-tests.trx --results-directory=Manuals.Tests.Unit/bin/Release/net11.0/TestResults" `
        --format opencover --output "coverage.opencover.xml" `
        --skipautoprops --exclude-by-attribute GeneratedCodeAttribute --exclude-by-file "**/obj/**" `
        --exclude-by-file "**/Program.cs" --does-not-return-attribute DoesNotReturnAttribute --include "[Manuals]*"
    Test-Trx $unitStep $unitTrx $global:LASTEXITCODE -floor 1
}

if (Test-StepCarried $integrationStep) {
    Write-Row $redisStep 'NOT RUN' 'the integration tier carried its verdict, so no Redis is needed'
}
else {
    $redis = Open-IntegrationRedis
    if ($redis.Missing) { Stop-Gate $redisStep $redis.Missing }
    $redisFromEnvironment = $redis.FromEnvironment
    $wslSession = $redis.Session
    $redisEndpoint = $redis.Endpoint
    $redisAvailable = $redis.Available
    try {
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

        if ($redisAvailable) {
            if (Test-Path $integrationTrx) { Remove-Item $integrationTrx -Force }
            $env:ASPNETCORE_ENVIRONMENT = 'Development'
            $env:RedisDatabase ??= '1'
            $global:LASTEXITCODE = $null
            dotnet-coverage collect `
                "dotnet test --project Manuals.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration --stop-on-fail on --report-xunit-trx --report-xunit-trx-filename integration-tests.trx --results-directory=Manuals.Tests.Integration/bin/Release/net11.0/TestResults" `
                -f xml -o "coverage-integration.xml" -s "coverage.settings.xml"
            Test-Trx $integrationStep $integrationTrx $global:LASTEXITCODE -floor 1
        }
    }
    finally {
        Close-IntegrationRedis $redis
    }
}

if (-not $sonarCarried) {
    $global:LASTEXITCODE = $null
    dotnet-sonarscanner end
    Assert-StepExit $endSonar
    Test-SonarIssues $sonarIssues 'crgolden_Manuals' $sonarBranch $sonarStartedAt
}

Write-Row 'Upload test results / dotnet publish / Upload artifact / deploy' 'NOT RUN' 'delivery steps, not checks'
Complete-Gate
})
