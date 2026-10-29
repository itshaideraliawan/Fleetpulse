param(
    [string]$BaseUrl = 'http://127.0.0.1:5173',
    [string]$OutputPath = '.dev-notes/demo-runs/D6-04/service-rehearsals.json'
)
$ErrorActionPreference = 'Stop'
$runs = [Collections.Generic.List[object]]::new()

function Read-Json([string]$path) {
    Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + $path) -TimeoutSec 90
}
function Write-Json([string]$path, $body) {
    Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + $path) -Method Post `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 8) -TimeoutSec 90
}
function Ready($value) {
    !$value.resetPending -and $value.appliedRevision -eq $value.desired.revision -and
        $value.gateways.Count -gt 0 -and @($value.gateways | Where-Object {
            $_.revision -ne $value.desired.revision -or $_.configuredDevices -ne $_.connectedDevices
        }).Count -eq 0
}
function Wait-State([string]$path, [scriptblock]$predicate, [int]$timeoutSeconds = 240) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = Read-Json $path
        if (& $predicate $value) { return $value }
        Start-Sleep -Seconds 1
    } while ($watch.Elapsed.TotalSeconds -lt $timeoutSeconds)
    throw "Timed out waiting for $path. Last response: $($value | ConvertTo-Json -Depth 6 -Compress)"
}
function Control([int]$action, [bool]$inject = $false) {
    $state = Read-Json '/api/demo'
    Write-Json '/api/demo' @{ requestId = [Guid]::NewGuid(); expectedRevision = $state.desired.revision
        action = $action; admissionEnabled = $true
        badReleaseVersion = if ($inject) { '2.4.0' } else { $null }
        badReleaseModel = if ($inject) { 'Kiosk-B' } else { $null } }
}
function Save-Results {
    $directory = Split-Path ([IO.Path]::GetFullPath($OutputPath)) -Parent
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    @{ completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); baseUrl = $BaseUrl
        visualVerified = $false; scope = 'Service flow through the portal proxy; not browser interaction'
        runs = $runs.ToArray() } | ConvertTo-Json -Depth 14 | Set-Content -Encoding UTF8 $OutputPath
}

try {
    foreach ($iteration in 1..2) {
        $run = [ordered]@{ iteration = $iteration; startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O') }
        $runs.Add($run)
        Write-Host "Run $iteration`: wait for 5000 configured sessions, then reset"
        $null = Wait-State '/api/demo' { param($value) (Ready $value) -and $value.connectedDevices -eq 5000 }
        $null = Control 2
        $baseline = Wait-State '/api/demo' { param($value)
            (Ready $value) -and $value.connectedDevices -eq 5000 -and
            $value.desired.badReleaseVersion -eq $null -and $value.desired.admissionEnabled }
        $run.resetGeneration = $baseline.desired.resetGeneration
        foreach ($historyPath in @('/api/commands', '/api/rollouts')) {
            $history = Read-Json $historyPath
            if ($null -ne $history -and @($history).Count -ne 0) { throw "Reset retained history at $historyPath" }
        }
        $summary = Wait-State '/api/fleet/summary' { param($value) $value.onlineDevices -eq 5000 }
        $run.overview = $summary
        $devices = Read-Json '/api/devices?page=1&pageSize=25'
        $detail = Read-Json ("/api/devices/" + $devices.items[0].id)
        if (!$detail.device.online -or $detail.device.firmwareVersion -ne '1.0.0') {
            throw 'Device detail did not show the online baseline firmware.'
        }
        $run.deviceDetail = $detail

        Write-Host "Run $iteration`: diagnostics for one region"
        $region = $summary.regions[0]
        $command = Write-Json '/api/commands' @{ kind = 0; deviceId = $null
            regionId = $region.regionId; model = $null; fleet = $false; expiresInSeconds = 300 }
        $progress = Wait-State ("/api/commands/" + $command.commandId) { param($value)
            $value.summary.pending -eq 0 -and $value.summary.acknowledged -eq 0 }
        if ($progress.summary.succeeded -ne $region.totalDevices -or
            $progress.summary.failed -ne 0 -or $progress.summary.expired -ne 0) {
            throw 'Regional diagnostics did not succeed for every target.'
        }
        $run.diagnostics = $progress.summary

        Write-Host "Run $iteration`: inject Kiosk-B failure; roll out first 100 devices"
        $null = Control 0 $true
        $null = Wait-State '/api/demo' { param($value) (Ready $value) -and $value.desired.badReleaseVersion -eq '2.4.0' }
        $rollout = Write-Json '/api/rollouts' @{ firmwareVersion = '2.4.0'; regionId = $null; model = $null; maxDevices = 100 }
        $release = Wait-State ("/api/rollouts/" + $rollout.id) { param($value) $value.summary.status -in @(3,6) }
        $installed = ($release.waves | Measure-Object installed -Sum).Sum
        $restored = ($release.waves | Measure-Object rolledBack -Sum).Sum
        if ($release.summary.status -ne 6 -or $release.summary.currentWaveNumber -ne 2 -or
            $release.waves[0].gateFailures -ne 0 -or $installed -ne 7 -or $restored -ne 7 -or
            @($release.transitions | Where-Object { $_.status -eq 4 -and $_.waveNumber -eq 2 }).Count -ne 1) {
            throw 'Expected a passing canary, wave-2 halt, and seven confirmed restorations.'
        }
        $run.rollout = $release

        Write-Host "Run $iteration`: drop all sessions and sample actual recovery"
        $samples = [Collections.Generic.List[object]]::new()
        $recoveryWatch = [Diagnostics.Stopwatch]::StartNew()
        $drop = Control 1 $true
        $seenDip = $false
        do {
            $state = Read-Json '/api/demo'
            $samples.Add(@{ seconds = $recoveryWatch.Elapsed.TotalSeconds; connected = $state.connectedDevices })
            if ($state.connectedDevices -lt 5000) { $seenDip = $true }
            if ($seenDip -and $state.connectedDevices -eq 5000 -and (Ready $state) -and
                $state.desired.dropGeneration -eq $drop.desired.dropGeneration) { break }
            if ($recoveryWatch.Elapsed.TotalSeconds -gt 240) { throw 'Fleet did not recover after drop.' }
            Start-Sleep -Seconds 1
        } while ($true)
        $run.recoverySeconds = $recoveryWatch.Elapsed.TotalSeconds
        $run.recoverySamples = $samples.ToArray()
        $run.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Save-Results
        Write-Host "Run $iteration completed; recovery $([Math]::Round($run.recoverySeconds,2))s"
    }
} catch {
    if ($runs.Count -gt 0) { $runs[$runs.Count - 1].error = $_.Exception.Message }
    Save-Results
    throw
}
Write-Host "Two service rehearsals passed. Browser walkthrough verification remains separate. Evidence: $OutputPath"
