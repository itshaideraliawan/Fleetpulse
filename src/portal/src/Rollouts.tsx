import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { loadRecentRollouts, loadRolloutProgress, startRollout } from './api/rollouts'
import type { RegionSummary, RolloutProgress, RolloutSummary } from './api/generated/types.gen'

const states = ['Draft', 'Wave running', 'Gate check', 'Completed', 'Halted', 'Rolling back', 'Rolled back']
const format = (value: number | string) => Number(value).toLocaleString()
const time = (value: string) => new Date(value).toLocaleString()

export function Rollouts({ regions }: { regions: RegionSummary[] }) {
  const [firmware, setFirmware] = useState('2.4.0')
  const [regionId, setRegionId] = useState('')
  const [model, setModel] = useState('')
  const [limit, setLimit] = useState(100)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [historyError, setHistoryError] = useState<string | null>(null)
  const [progressError, setProgressError] = useState<string | null>(null)
  const [recent, setRecent] = useState<RolloutSummary[]>([])
  const [selectedId, setSelectedId] = useState('')
  const [progress, setProgress] = useState<RolloutProgress | null>(null)
  const [version, setVersion] = useState(0)
  const submitController = useRef<AbortController | null>(null)

  useEffect(() => () => submitController.current?.abort(), [])

  useEffect(() => {
    const controller = new AbortController()
    let inFlight = false
    async function refresh() {
      if (inFlight) return
      inFlight = true
      try {
        const result = await loadRecentRollouts(controller.signal)
        if (!controller.signal.aborted) { setRecent(result); setHistoryError(null) }
      } catch (cause) {
        if (!controller.signal.aborted) setHistoryError(cause instanceof Error ? cause.message : 'Rollout history failed.')
      } finally { inFlight = false }
    }
    void refresh()
    const timer = setInterval(() => void refresh(), 5000)
    return () => { controller.abort(); clearInterval(timer) }
  }, [version])

  useEffect(() => {
    if (!selectedId) return
    const controller = new AbortController()
    let inFlight = false
    async function refresh() {
      if (inFlight) return
      inFlight = true
      try {
        const result = await loadRolloutProgress(selectedId, controller.signal)
        if (!controller.signal.aborted) { setProgress(result); setProgressError(null) }
      } catch (cause) {
        if (!controller.signal.aborted) setProgressError(cause instanceof Error ? cause.message : 'Rollout progress failed.')
      } finally { inFlight = false }
    }
    void refresh()
    const timer = setInterval(() => void refresh(), 1000)
    return () => { controller.abort(); clearInterval(timer) }
  }, [selectedId])

  function select(id: string) { setSelectedId(id); setProgress(null); setProgressError(null) }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (busy) return
    const controller = new AbortController()
    submitController.current = controller
    setBusy(true); setError(null)
    try {
      const result = await startRollout({ firmwareVersion: firmware.trim(), regionId: regionId ? Number(regionId) : null,
        model: model || null, maxDevices: limit }, controller.signal)
      if (!controller.signal.aborted) { select(result.id); setVersion(value => value + 1) }
    } catch (cause) {
      if (!controller.signal.aborted) {
        setError(`${cause instanceof Error ? cause.message : 'Rollout request failed.'} Check recent rollouts before trying again.`)
        setVersion(value => value + 1)
      }
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }

  const summary = progress?.summary
  const installed = progress?.waves.reduce((sum, wave) => sum + Number(wave.installed), 0) ?? 0
  const restored = progress?.waves.reduce((sum, wave) => sum + Number(wave.rolledBack), 0) ?? 0
  const rollbackFailed = progress?.waves.reduce((sum, wave) => sum + Number(wave.rollbackFailed), 0) ?? 0
  const unresolved = progress?.waves.reduce((sum, wave) => sum + Number(wave.unresolvedInstalls), 0) ?? 0
  const rollingBack = summary && [4, 5, 6].includes(summary.status)

  return <div className="dashboard-stack">
    <section className="data-panel" aria-labelledby="launch-rollout-heading">
      <div className="panel-heading"><div><span className="section-kicker">SAFE CHANGE</span><h2 id="launch-rollout-heading">Launch a staged release</h2><p>Expand through 1%, 10%, 50%, and 100% of the selected cohort. Every wave must have less than 2% failures.</p></div></div>
      <form className="command-form" onSubmit={event => void submit(event)}>
        <label>Firmware version<input required maxLength={40} value={firmware} disabled={busy} onChange={event => setFirmware(event.target.value)} /></label>
        <label>Region<select value={regionId} disabled={busy} onChange={event => setRegionId(event.target.value)}><option value="">All regions</option>{regions.map(region => <option key={String(region.regionId)} value={String(region.regionId)}>{region.regionName}</option>)}</select></label>
        <label>Model<select value={model} disabled={busy} onChange={event => setModel(event.target.value)}><option value="">All models</option>{['Kiosk-A', 'Kiosk-B', 'Kiosk-C'].map(name => <option key={name}>{name}</option>)}</select></label>
        <label>Maximum devices<input type="number" required min={1} max={5000} value={limit} disabled={busy} onChange={event => setLimit(Number(event.target.value))} /></label>
        <button type="submit" className="primary-button" disabled={busy || !firmware.trim()}>{busy ? 'Starting…' : 'Start rollout'}</button>
      </form>
      <p className="refresh-note">Targets the first {format(limit)} matching devices by serial number, including offline devices. Leave region and model unrestricted to target across the fleet. The rollout starts immediately; failed gates trigger automatic rollback.</p>
      {error && <p className="inline-error" role="alert">{error}</p>}
    </section>

    <section className="data-panel" aria-labelledby="rollout-progress-heading">
      <div className="panel-heading"><div><span className="section-kicker">RELEASE ACTIVITY</span><h2 id="rollout-progress-heading">Waves and health gates</h2><p>Selected rollout refreshes every second. History refreshes every five seconds.</p></div></div>
      <label className="command-history">Recent rollouts (latest 20)<select value={selectedId} onChange={event => select(event.target.value)}><option value="">Select a rollout</option>{selectedId && !recent.some(item => item.id === selectedId) && <option value={selectedId}>Selected rollout · {selectedId.slice(0, 8)}</option>}{recent.map(item => <option key={item.id} value={item.id}>{item.firmwareVersion} · {states[item.status]} · {format(item.total)} devices · {time(item.createdAtUtc)} · {item.id.slice(0, 8)}</option>)}</select></label>
      {historyError && <p className="inline-error" role="alert">{historyError} Retrying automatically.</p>}
      {progressError && <p className="inline-error" role="alert">{progressError} Shown results may be stale. Retrying automatically.</p>}
      {!selectedId ? <p className="no-data">Launch a release or select a recent rollout to see wave results.</p> : !summary || !progress ? <p className="no-data" role="status">Loading rollout…</p> : <>
        <div className="rollout-heading"><h3>Release {summary.firmwareVersion}</h3><span className={`status-pill rollout-state-${summary.status}`}>{states[summary.status] ?? 'Unknown'}</span><span>{format(summary.total)} devices · Current wave {summary.currentWaveNumber ?? '—'}</span></div>
        <p className="rollout-reason">{progress.stateReason}</p>
        {progress.haltReason && <div className="inline-error"><strong>Halt reason</strong><p>{progress.haltReason}</p></div>}
        <div className="table-scroll"><table className="device-table rollout-waves"><thead><tr><th>Wave / cumulative reach</th><th>Devices</th><th>Installed</th><th>Failed / expired</th><th>Health gate</th></tr></thead><tbody>{progress.waves.map(wave => {
          const total = Number(wave.total)
          const checked = wave.gateCheckedAtUtc != null
          const passed = checked && Number(wave.gateTotal) > 0 && Number(wave.gateFailures) * 100 < Number(wave.gateTotal) * 2
          return <tr key={String(wave.number)} className={summary.currentWaveNumber === wave.number ? 'current-wave' : undefined}>
            <td>Wave {wave.number} · {wave.cumulativePercentage}%{summary.currentWaveNumber === wave.number && <small>Current wave</small>}</td><td>{format(total)}</td><td>{format(wave.installed)} / {format(total)}</td><td>{format(wave.failed)}</td>
            <td>{total === 0 ? 'Skipped (empty wave)' : checked ? <><span className={`status-pill ${passed ? 'online' : 'health-label unhealthy'}`}>{passed ? 'Passed' : 'Failed'}</span><small>{format(wave.gateFailures ?? 0)} / {format(wave.gateTotal ?? 0)} failed ({(Number(wave.gateFailures) / Number(wave.gateTotal) * 100).toFixed(2)}%)</small><small>{time(wave.gateCheckedAtUtc!)}</small></> : progress.haltReason ? 'Not evaluated' : 'Awaiting results'}</td>
          </tr>
        })}</tbody></table></div>
        {rollingBack && <section className="rollout-rollback" aria-labelledby="rollback-heading"><h3 id="rollback-heading">Automatic rollback</h3><div className="command-progress"><strong>{format(restored)} / {format(installed)} updated devices restored</strong><span>{summary.status === 6 ? 'Rollback complete' : 'Restoring original firmware'}</span><progress value={restored} max={Math.max(1, installed)} aria-label="Devices restored to original firmware" /></div><p>{format(rollbackFailed)} failed or expired rollback commands · {format(unresolved)} install results not yet confirmed.</p><p>Late successful installs are also rolled back. Unknown installs or unsuccessful rollbacks keep the rollout open.</p></section>}
        <h3 className="rollout-history-title">State transitions</h3>
        {progress.transitions.length === 0 ? <p className="no-data">The worker is waiting to start this rollout.</p> : <ol className="rollout-transitions">{progress.transitions.map(item => <li key={String(item.sequence)}><strong>{states[item.status]}{item.waveNumber != null ? ` · Wave ${item.waveNumber}` : ''}</strong><time dateTime={item.occurredAtUtc}>{time(item.occurredAtUtc)}</time><p>{item.reason}</p></li>)}</ol>}
      </>}
    </section>
  </div>
}
