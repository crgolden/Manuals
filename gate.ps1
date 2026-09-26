param([string]$Goal)

$ErrorActionPreference = 'Continue'
$gateCommon = Join-Path $PSScriptRoot '..\Tools\Gates\GateCommon.ps1'
if (-not (Test-Path -LiteralPath $gateCommon)) {
    Write-Host "GATE: FAILED (the Tools repository must be cloned beside this one: $gateCommon)"
    exit 1
}
. $gateCommon
$gateOutput = Join-Path ([IO.Path]::GetTempPath()) "crgolden-gates\$(Split-Path -Leaf $PSScriptRoot)"
New-Item -ItemType Directory -Force -Path $gateOutput | Out-Null
Register-GateSteps @('Local Redis (WSL) for the integration tier', 'Begin Sonar analysis', 'Build with dotnet', 'Restore local tools', 'jb inspectcode',
    'Run unit tests with coverage', 'Run integration tests with coverage', 'End Sonar analysis')
$repo = $PSScriptRoot
$sarif = (Join-Path $gateOutput 'manuals-inspect.sarif')
$unitTrx = Join-Path $repo 'Manuals.Tests.Unit\bin\Release\net10.0\TestResults\unit-tests.trx'
$integrationTrx = Join-Path $repo 'Manuals.Tests.Integration\bin\Release\net10.0\TestResults\integration-tests.trx'
$sonarBranch = "branch-local-$($env:COMPUTERNAME.ToLowerInvariant())"
$beginSonar = "Begin Sonar analysis (branch $sonarBranch)"
$build = 'Build with dotnet (Release, RestoreLockedMode)'
$endSonar = 'End Sonar analysis (quality gate waited)'
$unitStep = 'Run unit tests with coverage (Category=Unit)'
$integrationStep = 'Run integration tests with coverage (Category=Integration)'
$env:TZ = 'UTC'
if ($env:TZ -ne 'UTC') { Write-Host 'GATE: FAILED (TZ pin)'; exit 1 }
Set-Location $repo
Initialize-GateState 'Manuals' $repo
Invoke-CatalogSteps

$redisAvailable = [bool](Get-CimInstance Win32_Processor | Select-Object -First 1).VirtualizationFirmwareEnabled
if ($redisAvailable) {
    Write-Row 'Local Redis (WSL) for the integration tier' 'PASS' 'virtualization available in this boot'
}
else {
    Write-Row 'Local Redis (WSL) for the integration tier' 'SKIPPED' 'this boot has no virtualization, so WSL Redis cannot run; the integration tier is skipped'
}

$sonarCarried = Test-StepCarried $endSonar
if ($sonarCarried) {
    $null = Test-StepCarried $beginSonar
    $null = Test-StepCarried $build
}
else {
    $env:JAVA_HOME = "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\jre"
    $global:LASTEXITCODE = $null
    dotnet-sonarscanner begin /k:"crgolden_Manuals" /o:"crgolden" /d:sonar.token="$env:SONAR_TOKEN" /d:sonar.host.url="https://sonarcloud.io" /d:sonar.cs.opencover.reportsPaths="coverage.opencover.xml" /d:sonar.cs.vscoveragexml.reportsPaths="coverage-integration.xml" /d:sonar.exclusions="**/bin/**,**/obj/**" /d:sonar.coverage.exclusions="**/Program.cs" /d:sonar.qualitygate.wait=true /d:sonar.scanner.skipJreProvisioning=true /d:sonar.branch.name="$sonarBranch"
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
    dotnet jb inspectcode "$repo\Manuals.slnx" --no-build -e=WARNING --output="$sarif"
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
    Test-Trx $unitStep $unitTrx $global:LASTEXITCODE 1
}

if (-not $redisAvailable) {
    Write-Row $integrationStep 'SKIPPED' 'needs WSL Redis, which this boot cannot run'
}
elseif (-not (Test-StepCarried $integrationStep)) {
    if (Test-Path $integrationTrx) { Remove-Item $integrationTrx -Force }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:RedisDatabase = '1'
    $global:LASTEXITCODE = $null
    dotnet-coverage collect `
        "dotnet test --project Manuals.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration --stop-on-fail on --report-xunit-trx --report-xunit-trx-filename integration-tests.trx --results-directory=Manuals.Tests.Integration/bin/Release/net10.0/TestResults" `
        -f xml -o "coverage-integration.xml" -s "coverage.settings.xml"
    Test-Trx $integrationStep $integrationTrx $global:LASTEXITCODE 1
}

if (-not $sonarCarried) {
    $global:LASTEXITCODE = $null
    dotnet-sonarscanner end /d:sonar.token="$env:SONAR_TOKEN"
    $null = Test-Exit $endSonar
}

Write-Row 'Upload test results / dotnet publish / Upload artifact / deploy' 'NOT RUN' 'delivery steps, not checks'
Complete-Gate
