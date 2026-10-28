import { useEffect, useRef, useState } from 'react'
import { loadDemoStatus, sendDemoControl } from './api/demo'
import type { DemoStatus } from './api/generated/types.gen'

type Point = { seconds: number; connected: number }
type Recovery = { generation: number; admission: boolean; mixed?: boolean; target: number; points: Point[] }
const number = (value: number | string) => Number(value).toLocaleString()

export function RecoveryChart({ runs }: { runs: Recovery[] }) {
  const maxCount = Math.max(1, ...runs.flatMap(run => [run.target, ...run.points.map(point => point.connected)]))
  const maxTime = Math.max(30, ...runs.flatMap(run => run.points.map(point => point.seconds)))
  const x = (seconds: number) => 55 + seconds / maxTime * 630
  const y = (count: number) => 205 - count / maxCount * 170
  return <div className="recovery-chart">
    <svg viewBox="0 0 720 250" role="img" aria-label="Actual Gateway connections over seconds since each drop request">
      <desc>Each line samples actual Gateway sessions once per second. Green means admission on; orange means admission off. The latest two drops in this tab are retained. Very fast disconnects can fall between samples.</desc>
      {[0, 0.5, 1].map(fraction => <g key={fraction}><line x1="55" x2="685" y1={y(maxCount * fraction)} y2={y(maxCount * fraction)} className="chart-grid" /><text x="45" y={y(maxCount * fraction) + 4} textAnchor="end">{Math.round(maxCount * fraction)}</text></g>)}
      <text x="55" y="225">0s</text><text x="685" y="225" textAnchor="end">{Math.ceil(maxTime)}s after drop request</text>
      {runs.map((run, index) => <g key={run.generation}><polyline fill="none" stroke={run.admission ? '#258b55' : '#c17a28'} strokeWidth={index === runs.length - 1 ? 3 : 2} strokeDasharray={index === runs.length - 1 ? undefined : '6 4'} points={run.points.map(point => `${x(point.seconds)},${y(point.connected)}`).join(' ')} />{run.points.at(-1) && <circle cx={x(run.points.at(-1)!.seconds)} cy={y(run.points.at(-1)!.connected)} r="4" fill={run.admission ? '#258b55' : '#c17a28'} />}</g>)}
    </svg>
    <ul className="recovery-legend">{runs.map((run, index) => <li key={run.generation}><span style={{ background: run.admission ? '#258b55' : '#c17a28' }} />{index === runs.length - 1 ? 'Latest' : 'Previous'} drop · {run.mixed ? 'Admission changed during run' : `Admission ${run.admission ? 'on' : 'off'}`} · {number(run.points.at(-1)?.connected ?? 0)} / {number(run.target)} baseline sessions</li>)}</ul>
  </div>
}

export function DemoControl() {
  const [status, setStatus] = useState<DemoStatus | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [admission, setAdmission] = useState(true)
  const [inject, setInject] = useState(false)
  const [version, setVersion] = useState('2.4.0')
  const [model, setModel] = useState('Kiosk-B')
  const [confirmReset, setConfirmReset] = useState(false)
  const [runs, setRuns] = useState<Recovery[]>([])
  const initialized = useRef(false)
  const resetGeneration = useRef<number | null>(null)
  const submitController = useRef<AbortController | null>(null)

  useEffect(() => () => submitController.current?.abort(), [])
  useEffect(() => {
    const controller = new AbortController()
    let inFlight = false
    async function refresh() {
      if (inFlight) return
      inFlight = true
      try {
        const next = await loadDemoStatus(controller.signal)
        if (controller.signal.aborted) return
        setStatus(next); setStatusError(null)
        if (!initialized.current || resetGeneration.current !== Number(next.desired.resetGeneration)) {
          setAdmission(next.desired.admissionEnabled); setInject(next.desired.badReleaseVersion != null)
          setVersion(next.desired.badReleaseVersion ?? '2.4.0'); setModel(next.desired.badReleaseModel ?? 'Kiosk-B')
          initialized.current = true; setRuns([])
        }
        resetGeneration.current = Number(next.desired.resetGeneration)
        if (next.dropAtUtc) {
          const seconds = Math.max(0, (Date.parse(next.sampledAtUtc) - Date.parse(next.dropAtUtc)) / 1000)
          if (seconds <= 180) setRuns(previous => {
            const generation = Number(next.desired.dropGeneration)
            const last = previous.at(-1)
            const point = { seconds, connected: Number(next.connectedDevices) }
            if (last?.generation === generation) {
              const recovering = (last.points.at(-1)?.connected ?? 0) < last.target
              return [...previous.slice(0, -1), { ...last, mixed: last.mixed || (recovering && last.admission !== next.desired.admissionEnabled), points: [...last.points, point].slice(-181) }]
            }
            return [...previous.slice(-1), { generation, admission: next.desired.admissionEnabled, target: Number(next.recoveryTarget), points: [point] }]
          })
        }
      } catch (cause) {
        if (!controller.signal.aborted) setStatusError(cause instanceof Error ? cause.message : 'Demo status failed.')
      } finally { inFlight = false }
    }
    void refresh()
    const timer = setInterval(() => void refresh(), 1000)
    return () => { controller.abort(); clearInterval(timer) }
  }, [])

  const ready = status != null && !status.resetPending && Number(status.appliedRevision) === Number(status.desired.revision) && status.gateways.length > 0 && status.gateways.every(gateway => Number(gateway.revision) === Number(status.desired.revision) && Number(gateway.configuredDevices) === Number(gateway.connectedDevices))
  const disabled = busy || !ready || statusError != null

  async function act(action: number) {
    if (!status || disabled) return
    const controller = new AbortController()
    submitController.current = controller
    setBusy(true); setActionError(null); setNotice(null)
    try {
      const next = await sendDemoControl({ requestId: crypto.randomUUID(), expectedRevision: status.desired.revision,
        action, admissionEnabled: admission, badReleaseVersion: action === 0 ? inject ? version.trim() : null : status.desired.badReleaseVersion,
        badReleaseModel: action === 0 ? inject ? model : null : status.desired.badReleaseModel }, controller.signal)
      if (!controller.signal.aborted) {
        setStatus(next); setConfirmReset(false)
        setNotice(action === 0 ? 'Settings saved. Waiting for Gateways and devices to apply them.' : action === 1 ? 'Drop requested. Recovery samples show actual Gateway sessions.' : 'Reset requested. Waiting for devices to restore baseline firmware.')
      }
    } catch (cause) {
      if (!controller.signal.aborted) setActionError(`${cause instanceof Error ? cause.message : 'Demo request failed.'} Check the current settings before trying again; requests are not automatically retried.`)
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }

  return <div className="dashboard-stack">
    <section className="data-panel" aria-labelledby="demo-controls-heading">
      <div className="panel-heading"><div><span className="section-kicker">LOCAL SCENARIOS</span><h2 id="demo-controls-heading">Demo controls</h2><p>Change simulator fault behavior and compare recovery with admission on or off.</p></div><span className={`status-pill ${ready && !statusError ? 'online' : 'offline'}`}>{ready && !statusError ? 'Applied' : 'Waiting for synchronization'}</span></div>
      {statusError && <p className="inline-error" role="alert">{statusError} Shown values may be stale. Retrying automatically.</p>}
      {!status ? <p className="no-data" role="status">Loading demo settings…</p> : <>
        <div className="demo-summary"><div><strong>{number(status.connectedDevices)}</strong><span>Actual Gateway sessions</span></div><div><strong>{number(status.gateways.length)}</strong><span>Reporting Gateways</span></div><div><strong>{status.desired.admissionEnabled ? 'On' : 'Off'}</strong><span>Saved admission setting</span></div></div>
        <form className="command-form demo-settings" onSubmit={event => { event.preventDefault(); void act(0) }}>
          <label className="demo-toggle"><input type="checkbox" checked={admission} disabled={busy} onChange={event => setAdmission(event.target.checked)} />Admission control</label>
          <label className="demo-toggle"><input type="checkbox" checked={inject} disabled={busy} onChange={event => setInject(event.target.checked)} />Inject bad release</label>
          {inject && <><label>Release version<input required maxLength={40} value={version} disabled={busy} onChange={event => setVersion(event.target.value)} /></label><label>Hardware model<select value={model} disabled={busy} onChange={event => setModel(event.target.value)}>{['Kiosk-A', 'Kiosk-B', 'Kiosk-C'].map(name => <option key={name}>{name}</option>)}</select></label></>}
          <button className="primary-button" disabled={disabled || (inject && !version.trim())} type="submit">{busy ? 'Applying…' : 'Apply settings'}</button>
        </form>
        <p className="refresh-note">Injection affects future installs matching both version and model; it does not launch a rollout. Wait for Applied, then launch that version on the Rollouts screen. Rollback remains independent of fault injection.</p>
        <div className="demo-actions"><div><h3>Drop all connections</h3><p>Close device sessions on every reporting Gateway. Simulators reconnect with jitter. The chart samples actual sessions, independently of the 45-second telemetry offline window.</p><button className="primary-button" disabled={disabled || Number(status.connectedDevices) === 0} onClick={() => void act(1)}>Drop connections and chart recovery</button></div><div><h3>Reset fleet</h3><p>Requires idle commands and rollouts. Restore firmware 1.0.0, clear command/rollout history and hot telemetry, disable injection, and enable admission. Devices reconnect and acknowledge the baseline.</p><label className="demo-toggle"><input type="checkbox" checked={confirmReset} disabled={busy} onChange={event => setConfirmReset(event.target.checked)} />I understand this clears command and rollout history</label><button className="danger-button" disabled={disabled || !confirmReset} onClick={() => void act(2)}>Reset fleet to baseline</button></div></div>
        {status.resetPending && <p className="refresh-note" role="status">Reset is applying. New commands and rollouts are blocked until active Gateway sessions acknowledge it.</p>}
        {status.gateways.length === 0 && <p className="no-data">Waiting for a Gateway heartbeat. Start Aspire and the simulator to use these controls.</p>}
        {notice && <p className="refresh-note" role="status">{notice}</p>}
        {actionError && <p className="inline-error" role="alert">{actionError}</p>}
      </>}
    </section>
    <section className="data-panel" aria-labelledby="recovery-heading"><div className="panel-heading"><div><span className="section-kicker">RECONNECT RECOVERY</span><h2 id="recovery-heading">Connections after a drop</h2><p>One sample per second, up to three minutes per drop. Latest two runs in this screen; dashed line is the previous run.</p></div></div>
      {runs.length === 0 ? <p className="no-data">Drop connections to start a measured recovery curve. Apply the other admission setting and drop again to compare.</p> : <RecoveryChart runs={runs} />}
      <p className="refresh-note">Baseline is the session count before the drop request. Counts use five-second Gateway leases; stopped Gateways age out. A fast disconnect/reconnect may occur between samples. Curves are local to this screen and clear on reset or navigation.</p>
    </section>
  </div>
}
