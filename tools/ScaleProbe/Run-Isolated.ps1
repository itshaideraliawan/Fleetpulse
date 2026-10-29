param(
    [ValidateSet(5000)][int]$Devices = 5000,
    [ValidateRange(1, 3600)][int]$HoldSeconds = 900,
    [ValidateRange(1, 100000)][int]$PermitsPerSecond = 200,
    [ValidateRange(1, 100000)][int]$BurstCapacity = 200,
    [string]$OutputDirectory = '.dev-notes/scale-runs'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runPath = [IO.Path]::GetFullPath((Join-Path $repository (Join-Path $OutputDirectory (Get-Date -Format 'yyyyMMdd-HHmmss'))))
New-Item -ItemType Directory -Path $runPath -Force | Out-Null
$prefix = 'fleetpulse-scale-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$ownedContainers = @()
$ownedProcesses = @()
$secret = 'Scale9!' + [Guid]::NewGuid().ToString('N')
$previousSqlPassword = $env:MSSQL_SA_PASSWORD
$previousRabbitPassword = $env:RABBITMQ_DEFAULT_PASS

function Free-Port {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}
function Mapped-Port([string]$container, [string]$port) {
    $mapping = & docker port $container $port
    if ($LASTEXITCODE -ne 0) { throw 'Could not read container port.' }
    return [int]($mapping.Split(':')[-1])
}
function Start-ServiceProcess([string]$name, [string]$dll, [hashtable]$settings, [string[]]$extraArguments = @()) {
    $keys = @('ConnectionStrings__fleetdb', 'ConnectionStrings__redis', 'ConnectionStrings__rabbitmq',
        'ASPNETCORE_URLS', 'ASPNETCORE_ENVIRONMENT', 'Logging__LogLevel__Default',
        'AdmissionControl__PermitsPerSecond', 'AdmissionControl__BurstCapacity', 'OTEL_EXPORTER_OTLP_ENDPOINT')
    $previous = @{}
    foreach ($key in $keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $null, 'Process')
    }
    foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process') }
    try {
        $arguments = @('"' + (Join-Path $repository $dll) + '"') + $extraArguments
        return Start-Process dotnet -ArgumentList $arguments -WorkingDirectory $repository -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $runPath "$name.log") -RedirectStandardError (Join-Path $runPath "$name.err.log")
    }
    finally {
        foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
    }
}
function Wait-Healthy([string]$url, $process) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Service exited; inspect logs in $runPath" }
        try { if ((Invoke-WebRequest -UseBasicParsing -Uri "$url/health" -TimeoutSec 3).StatusCode -eq 200) { return } }
        catch { }
        Start-Sleep -Seconds 2
    }
    throw "Service health timeout; inspect logs in $runPath"
}
function Wait-Containers {
    # Native readiness failures are expected until each container finishes booting.
    $ErrorActionPreference = 'Continue'
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        & docker exec $sqlName /bin/bash -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -l 2 -Q "SELECT 1"' *> $null
        $sqlReady = $LASTEXITCODE -eq 0
        & docker exec $redisName redis-cli ping *> $null
        $redisReady = $LASTEXITCODE -eq 0
        & docker exec $rabbitName rabbitmq-diagnostics -q ping *> $null
        if ($sqlReady -and $redisReady -and $LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 3
    }
    throw 'Disposable database or broker readiness timed out.'
}
try {
    Write-Output "Run directory: $runPath"
    $env:MSSQL_SA_PASSWORD = $secret
    $sqlName = "$prefix-sql"
    & docker run -d --rm --name $sqlName --env ACCEPT_EULA=Y --env MSSQL_SA_PASSWORD --env MSSQL_MEMORY_LIMIT_MB=2048 -p '127.0.0.1::1433' mcr.microsoft.com/mssql/server:2022-latest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SQL container start failed.' }
    $ownedContainers += $sqlName
    $redisName = "$prefix-redis"
    & docker run -d --rm --name $redisName -p '127.0.0.1::6379' redis:8.6 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Redis container start failed.' }
    $ownedContainers += $redisName
    $env:RABBITMQ_DEFAULT_PASS = $secret
    $rabbitName = "$prefix-rabbit"
    & docker run -d --rm --name $rabbitName --env RABBITMQ_DEFAULT_USER=scale --env RABBITMQ_DEFAULT_PASS -p '127.0.0.1::5672' rabbitmq:4.3 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'RabbitMQ container start failed.' }
    $ownedContainers += $rabbitName
    $sqlPort = Mapped-Port $sqlName '1433/tcp'
    $redisPort = Mapped-Port $redisName '6379/tcp'
    $rabbitPort = Mapped-Port $rabbitName '5672/tcp'
    $redisConnection = "127.0.0.1:$redisPort"
    $rabbitConnection = "amqp://scale:$([Uri]::EscapeDataString($secret))@127.0.0.1:$rabbitPort"
    $sqlConnection = "Server=127.0.0.1,$sqlPort;Database=fleetdb;User ID=sa;Password=$secret;TrustServerCertificate=true"
    Wait-Containers
    $apiPort = Free-Port
    $gatewayPort = Free-Port
    $apiUrl = "http://127.0.0.1:$apiPort"
    $gatewayUrl = "http://127.0.0.1:$gatewayPort"
    $common = @{ ConnectionStrings__redis = $redisConnection; ConnectionStrings__rabbitmq = $rabbitConnection;
        ASPNETCORE_ENVIRONMENT = 'Production'; Logging__LogLevel__Default = 'Warning' }
    $apiSettings = $common.Clone()
    $apiSettings['ConnectionStrings__fleetdb'] = $sqlConnection
    $apiSettings['ASPNETCORE_URLS'] = $apiUrl
    $apiProcess = Start-ServiceProcess 'fleet-api' 'src/FleetApi/bin/Release/net10.0/FleetApi.dll' $apiSettings
    $ownedProcesses += $apiProcess
    Wait-Healthy $apiUrl $apiProcess
    $gatewaySettings = $common.Clone()
    $gatewaySettings['ASPNETCORE_URLS'] = $gatewayUrl
    $gatewaySettings['AdmissionControl__PermitsPerSecond'] = [string]$PermitsPerSecond
    $gatewaySettings['AdmissionControl__BurstCapacity'] = [string]$BurstCapacity
    $gatewayProcess = Start-ServiceProcess 'gateway' 'src/Gateway/bin/Release/net10.0/Gateway.dll' $gatewaySettings
    $ownedProcesses += $gatewayProcess
    Wait-Healthy $gatewayUrl $gatewayProcess
    $metadata = @{ cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name);
        hostMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory;
        os = (Get-CimInstance Win32_OperatingSystem).Caption; docker = (& docker version --format '{{.Server.Version}}');
        dockerMemoryBytes = (& docker info --format '{{.MemTotal}}'); baseCommit = (& git -C $repository rev-parse HEAD);
        devices = $Devices; holdSeconds = $HoldSeconds; permitsPerSecond = $PermitsPerSecond; burstCapacity = $BurstCapacity;
        sqlMemoryLimitMb = 2048;
        apiProcessId = $apiProcess.Id; gatewayProcessId = $gatewayProcess.Id;
        api = $apiUrl; gateway = $gatewayUrl; isolation = 'Disposable SQL, Redis, RabbitMQ; Release services; no Aspire dashboard/OTLP/React browser';
        images = @('mcr.microsoft.com/mssql/server:2022-latest', 'redis:8.6', 'rabbitmq:4.3');
        imageIds = @(& docker image inspect --format '{{.Id}}' mcr.microsoft.com/mssql/server:2022-latest redis:8.6 rabbitmq:4.3) }
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $runPath 'environment.json')
    $probeArguments = @('--api', $apiUrl, '--gateway', $gatewayUrl, '--devices', [string]$Devices,
        '--hold-seconds', [string]$HoldSeconds, '--output', ('"' + (Join-Path $runPath 'results.json') + '"'))
    $probeProcess = Start-ServiceProcess 'probe' 'tools/ScaleProbe/bin/Release/net10.0/ScaleProbe.dll' @{} $probeArguments
    # Retain the native handle so Windows PowerShell can read ExitCode after exit.
    $null = $probeProcess.Handle
    $ownedProcesses += $probeProcess
    Write-Output "Probe started. API=$apiUrl Gateway=$gatewayUrl"
    while (-not $probeProcess.HasExited) {
        $processSamples = foreach ($process in @($apiProcess, $gatewayProcess, $probeProcess)) {
            if (-not $process.HasExited) {
                $process.Refresh()
                @{ atUtc = [DateTimeOffset]::UtcNow.ToString('O'); id = $process.Id; workingSetBytes = $process.WorkingSet64; cpuSeconds = $process.TotalProcessorTime.TotalSeconds }
            }
        }
        $processSamples | ConvertTo-Json -Compress | Add-Content (Join-Path $runPath 'processes.jsonl')
        Start-Sleep -Seconds 5
    }
    $probeProcess.WaitForExit()
    if ($probeProcess.ExitCode -ne 0) { throw "Probe failed; inspect $runPath" }
    Write-Output "Measurements complete: $(Join-Path $runPath 'results.json')"
}
finally {
    foreach ($process in $ownedProcesses) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
    foreach ($container in $ownedContainers) { & docker rm -f $container 2>$null | Out-Null }
    [Environment]::SetEnvironmentVariable('MSSQL_SA_PASSWORD', $previousSqlPassword, 'Process')
    [Environment]::SetEnvironmentVariable('RABBITMQ_DEFAULT_PASS', $previousRabbitPassword, 'Process')
}
