param([Parameter(Mandatory = $true)][string]$RunDirectory,
      [Parameter(Mandatory = $true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$data = Get-Content (Join-Path $RunDirectory 'results.json') -Raw | ConvertFrom-Json
$environment = Get-Content (Join-Path $RunDirectory 'environment.json') -Raw | ConvertFrom-Json
function Distribution($values) {
    if ($null -eq $values -or @($values).Count -eq 0) { return @{ count = 0 } }
    [double[]]$sorted = @($values)
    [Array]::Sort($sorted)
    if ($sorted.Length -eq 0) { return @{ count = 0 } }
    $negative = 0
    foreach ($value in $sorted) { if ($value -lt 0) { $negative++ } }
    return @{ count = $sorted.Length; p50 = $sorted[[Math]::Ceiling($sorted.Length * .50) - 1];
        p95 = $sorted[[Math]::Ceiling($sorted.Length * .95) - 1]; p99 = $sorted[[Math]::Ceiling($sorted.Length * .99) - 1];
        maximum = $sorted[-1]; negativeSamples = $negative }
}
$modes = foreach ($mode in $data.modes) {
    $ackWithin10 = 0
    foreach ($value in $mode.commandAcknowledgementsMilliseconds) {
        if ($value -ge 0 -and $value -le 10000) { $ackWithin10++ }
    }
    $freshness = Distribution $mode.freshnessMilliseconds
    $waves = @($mode.rollout.waves)
    $installed = ($waves | Measure-Object installed -Sum).Sum
    $restored = ($waves | Measure-Object rolledBack -Sum).Sum
    $failedWave = @($waves | Where-Object { $_.gateCheckedAtUtc -and $_.gateFailures * 100 -ge $_.gateTotal * 2 } | Select-Object -First 1)
    @{ admission = $mode.admission; devices = $mode.devices; requestedHoldSeconds = $mode.requestedHoldSeconds;
        observedHoldSeconds = $mode.holdElapsedSeconds; startupSeconds = $mode.startupSeconds;
        minimumSimulatorSessions = $mode.minimumSessions;
        minimumGatewaySessions = ($mode.samples | Measure-Object gatewaySessions -Minimum).Minimum;
        unexpectedSessionClosures = $mode.unexpectedSessionClosures; holdConnectionFailures = $mode.holdConnectionFailures;
        heartbeatDelta = $mode.heartbeatDelta; telemetryDelta = $mode.telemetryDelta;
        steadyFreshnessMilliseconds = $freshness;
        stormFreshnessMilliseconds = Distribution $mode.stormFreshnessMilliseconds;
        ackMilliseconds = Distribution $mode.commandAcknowledgementsMilliseconds;
        acknowledgementBasis = if ($mode.acknowledgementBasis) { $mode.acknowledgementBasis } else { 'Legacy persisted receipt (may infer from completed result)' };
        persistedReceiptMilliseconds = Distribution $mode.persistedReceiptMilliseconds;
        ackWithin10Seconds = $ackWithin10; missingAcknowledgements = $mode.missingAcknowledgements;
        commandPostSeconds = $mode.commandPostSeconds; command = $mode.command;
        recoverySeconds = $mode.recoverySeconds; recoveredSessions = $mode.recoveredSessions;
        injectionRequestedSeconds = $mode.injectionRequestedSeconds;
        rolloutSeconds = $mode.rolloutSeconds; rolloutStatus = $mode.rollout.summary.status;
        failingWave = if ($failedWave.Count -gt 0) { $failedWave[0].number } else { $null };
        installed = $installed; restored = $restored; haltReason = $mode.rollout.haltReason;
        simulatorFirmwareInstalled = $mode.finalSimulator.firmwareInstalled;
        simulatorFirmwareRolledBack = $mode.finalSimulator.firmwareRolledBack;
        peakProbeWorkingSetBytes = ($mode.samples | Measure-Object probeWorkingSetBytes -Maximum).Maximum;
        holdSampleCount = @($mode.samples).Count; error = $mode.error;
        targets = @{ stability = ($mode.holdElapsedSeconds -ge 900 -and $mode.minimumSessions -eq 5000 -and $mode.unexpectedSessionClosures -eq 0 -and $mode.holdConnectionFailures -eq 0 -and ($mode.samples | Measure-Object gatewaySessions -Minimum).Minimum -eq 5000);
            operatorFeedWithin2Seconds = ($freshness.count -gt 0 -and $freshness.maximum -le 2000 -and $freshness.negativeSamples -eq 0);
            command99PercentWithin10Seconds = ($ackWithin10 -ge 4950);
            reconnectWithin60Seconds = ($mode.recoveredSessions -eq 5000 -and $mode.recoverySeconds -le 60);
            wave2AndCompleteRollback = ($failedWave.Count -gt 0 -and $failedWave[0].number -eq 2 -and $mode.rollout.summary.status -eq 6 -and $installed -eq $restored) } }
}
$publicEnvironment = $environment | Select-Object cpu, hostMemoryBytes, os, docker, dockerMemoryBytes,
    baseCommit, devices, holdSeconds, permitsPerSecond, burstCapacity, sqlMemoryLimitMb, isolation, images, imageIds
$summary = @{ measuredAtUtc = $data.measuredAtUtc; runtime = $data.runtime;
    processorCount = $data.processorCount; environment = $publicEnvironment; modes = @($modes);
    rawResultsSha256 = (Get-FileHash (Join-Path $RunDirectory 'results.json') -Algorithm SHA256).Hash;
    definitions = 'tools/ScaleProbe/README.md; nearest-rank quantiles; feed delivery is not browser paint' }
$summary | ConvertTo-Json -Depth 15 | Set-Content -Encoding UTF8 $OutputPath
