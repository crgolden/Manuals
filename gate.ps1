param([string]$Goal, [string[]]$Steps)

$ErrorActionPreference = 'Continue'
$gateCommon = Join-Path $PSScriptRoot '..\Tools\Gates\GateCommon.ps1'
if (-not (Test-Path -LiteralPath $gateCommon)) {
    Write-Host "GATE: FAILED (the Tools repository must be cloned beside this one: $gateCommon)"
    exit 1
}
. $gateCommon
$gateOutput = Join-Path ([IO.Path]::GetTempPath()) "crgolden-gates\$(Split-Path -Leaf $PSScriptRoot)"
New-Item -ItemType Directory -Force -Path $gateOutput | Out-Null
Register-GateSteps @('Begin Sonar analysis', 'Build with dotnet', 'Restore local tools', 'jb inspectcode',
    'Run unit tests with coverage', 'Local Redis (WSL) for the integration tier', 'Run integration tests with coverage', 'End Sonar analysis',
    'Fail on open Sonar issues')
Register-StepInputs @{
    'Begin Sonar analysis'                       = @('*')
    'Build with dotnet'                          = @('*')
    'Restore local tools'                        = @('dotnet-tools.json')
    'jb inspectcode'                             = @('*')
    'Run unit tests with coverage'               = @('*')
    'Local Redis (WSL) for the integration tier' = @('*')
    'Run integration tests with coverage'        = @('*')
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
Initialize-GateState 'Manuals' $repo
Assert-RequestedSteps $Steps
Invoke-CatalogSteps

$sonarCarried = Test-StepCarried $sonarIssues
if ($sonarCarried) {
    $null = Test-StepCarried $beginSonar
    $null = Test-StepCarried $build
    $null = Test-StepCarried $endSonar
}
else {
    $sonarStartedAt = [DateTimeOffset]::UtcNow
    $env:JAVA_HOME = "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\jre"
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

if (-not (Test-StepCarried $unitStep)) {
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

        if ($redisAvailable) {
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
