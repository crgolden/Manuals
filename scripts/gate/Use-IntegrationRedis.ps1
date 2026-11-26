[string[]]$Command = @($args)
if (-not $Command.Count) {
    Write-Host 'Use-IntegrationRedis.ps1 was given no command to run'
    exit 1
}
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fromEnvironment = -not [string]::IsNullOrWhiteSpace($env:RedisHost)
if ($fromEnvironment -and [string]::IsNullOrWhiteSpace($env:RedisPort)) {
    Write-Host 'RedisHost is set in the environment without RedisPort'
    exit 1
}
$session = $null
if (-not $fromEnvironment) {
    $sessionStart = [Diagnostics.ProcessStartInfo]::new('wsl.exe', '--exec sleep infinity')
    $sessionStart.UseShellExecute = $true
    $sessionStart.WindowStyle = 'Hidden'
    $session = [Diagnostics.Process]::Start($sessionStart)
}
try {
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
    if (-not $available) {
        if ($session -and $session.HasExited) { Write-Host "WSL could not be started (wsl.exe exit $($session.ExitCode)), and the integration tier needs its Redis" }
        elseif ($fromEnvironment) { Write-Host "nothing accepted a connection on ${redisHost}:$redisPort (RedisHost from the environment) within 60 seconds" }
        else { Write-Host "WSL is running but nothing accepted a connection on ${redisHost}:$redisPort within 60 seconds" }
        exit 1
    }
    Set-Location -LiteralPath $repo
    $global:LASTEXITCODE = $null
    & $Command[0] @($Command | Select-Object -Skip 1)
    $commandExit = $global:LASTEXITCODE
}
finally {
    if ($session) {
        if (-not $session.HasExited) { $session.Kill($true) }
        $session.Dispose()
    }
}
if ($null -eq $commandExit) { exit 1 }
exit $commandExit
