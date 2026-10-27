import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { loadCommandProgress, loadRecentCommands, sendCommand } from './api/commands'
import { searchFleetDevices } from './api/fleet'
import type { CommandProgress, CommandSummary, DeviceListItem, RegionSummary } from './api/generated/types.gen'

const statuses = ['Pending', 'Acknowledged', 'Succeeded', 'Failed', 'Expired']
const format = (value: number | string) => Number(value).toLocaleString()
const time = (value: string) => new Date(value).toLocaleString()

export function Commands({ regions }: { regions: RegionSummary[] }) {
  const [target, setTarget] = useState('device')
  const [search, setSearch] = useState('')
  const [devices, setDevices] = useState<DeviceListItem[]>([])
  const [deviceId, setDeviceId] = useState('')
  const [regionId, setRegionId] = useState('')
  const [model, setModel] = useState('')
  const [expiry, setExpiry] = useState(300)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [searchError, setSearchError] = useState<string | null>(null)
  const [historyError, setHistoryError] = useState<string | null>(null)
  const [progressError, setProgressError] = useState<string | null>(null)
  const [recent, setRecent] = useState<CommandSummary[]>([])
  const [selectedId, setSelectedId] = useState('')
  const [progress, setProgress] = useState<CommandProgress | null>(null)
  const [page, setPage] = useState(1)
  const [status, setStatus] = useState('')
  const [version, setVersion] = useState(0)
  const submitController = useRef<AbortController | null>(null)

  useEffect(() => () => submitController.current?.abort(), [])

  useEffect(() => {
    if (target !== 'device') return
    const controller = new AbortController()
    const timer = setTimeout(() => {
      void searchFleetDevices({ query: search.trim(), regionId: '', status: '', health: '', page: 1 }, controller.signal)
        .then(result => { if (!controller.signal.aborted) { setDevices(result.items); setSearchError(null) } })
        .catch((cause: unknown) => { if (!controller.signal.aborted) setSearchError(cause instanceof Error ? cause.message : 'Device search failed.') })
    }, 300)
    return () => { clearTimeout(timer); controller.abort() }
  }, [search, target])

  useEffect(() => {
    const controller = new AbortController()
    let inFlight = false
    async function refresh() {
      if (inFlight) return
      inFlight = true
      try {
        const commands = await loadRecentCommands(controller.signal)
        if (!controller.signal.aborted) { setRecent(commands); setHistoryError(null) }
      } catch (cause) {
        if (!controller.signal.aborted) setHistoryError(cause instanceof Error ? cause.message : 'Command history failed.')
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
        const next = await loadCommandProgress(selectedId, page, status, controller.signal)
        if (!controller.signal.aborted) {
          const lastPage = Math.max(1, Math.ceil(Number(next.total) / 25))
          if (page > lastPage) { setPage(lastPage); return }
          setProgress(next); setProgressError(null)
        }
      } catch (cause) {
        if (!controller.signal.aborted) setProgressError(cause instanceof Error ? cause.message : 'Command progress failed.')
      } finally { inFlight = false }
    }
    void refresh()
    const timer = setInterval(() => void refresh(), 1000)
    return () => { controller.abort(); clearInterval(timer) }
  }, [selectedId, page, status])

  function select(id: string) {
    setSelectedId(id); setPage(1); setStatus(''); setProgress(null); setProgressError(null)
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (busy) return
    const controller = new AbortController()
    submitController.current = controller
    setBusy(true); setError(null)
    try {
      const created = await sendCommand({ kind: 0, deviceId: target === 'device' ? deviceId : null,
        regionId: target === 'group' && regionId ? Number(regionId) : null,
        model: target === 'group' && model.trim() ? model.trim() : null,
        fleet: target === 'fleet', expiresInSeconds: expiry }, controller.signal)
      if (!controller.signal.aborted) { select(created.commandId); setVersion(value => value + 1) }
    } catch (cause) {
      if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Command request failed. Check recent commands before sending again.')
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }

  const summary = progress?.summary
  const completed = summary ? Number(summary.succeeded) + Number(summary.failed) + Number(summary.expired) : 0
  const total = Number(summary?.total ?? 0)
  const pageCount = Math.max(1, Math.ceil(Number(progress?.total ?? 0) / 25))
  const valid = target === 'fleet' || (target === 'device' ? Boolean(deviceId) : Boolean(regionId || model.trim()))

  return <div className="dashboard-stack">
    <section className="data-panel" aria-labelledby="send-command-heading">
      <div className="panel-heading"><div><span className="section-kicker">REMOTE OPERATIONS</span><h2 id="send-command-heading">Run diagnostics</h2><p>Check device health and collect a result from every target.</p></div></div>
      <form className="command-form" onSubmit={event => void submit(event)}>
        <label>Target<select value={target} disabled={busy} onChange={event => setTarget(event.target.value)}><option value="device">One device</option><option value="group">Region / model group</option><option value="fleet">Entire fleet</option></select></label>
        {target === 'device' && <><label>Find device<input value={search} maxLength={100} placeholder="Search serial or model" onChange={event => { setSearch(event.target.value); setDeviceId('') }} /></label><label>Device<select required value={deviceId} onChange={event => setDeviceId(event.target.value)}><option value="">Select a device</option>{devices.map(device => <option key={device.id} value={device.id}>{device.serialNumber} · {device.model} · {device.regionName}</option>)}</select></label></>}
        {target === 'group' && <><label>Region<select value={regionId} onChange={event => setRegionId(event.target.value)}><option value="">All regions</option>{regions.map(region => <option key={String(region.regionId)} value={String(region.regionId)}>{region.regionName}</option>)}</select></label><label>Model<input value={model} maxLength={40} placeholder="All models" onChange={event => setModel(event.target.value)} /></label></>}
        <label>Expires after (seconds)<input type="number" required min={10} max={86400} value={expiry} onChange={event => setExpiry(Number(event.target.value))} /></label>
        <button className="primary-button" disabled={!valid || busy} type="submit">{busy ? 'Sending…' : 'Run diagnostics'}</button>
      </form>
      <p className="refresh-note">{target === 'fleet' ? 'This command targets every device in the fleet.' : target === 'group' ? 'Choose a region, a model, or both. Both filters must match.' : 'Search results show up to 25 devices. Refine your search to find a kiosk.'} Offline devices receive pending commands when they reconnect before expiry.</p>
      {searchError && target === 'device' && <p className="inline-error" role="alert">{searchError}</p>}
      {error && <p className="inline-error" role="alert">{error}</p>}
    </section>

    <section className="data-panel" aria-labelledby="command-progress-heading">
      <div className="panel-heading"><div><span className="section-kicker">COMMAND ACTIVITY</span><h2 id="command-progress-heading">Progress and results</h2><p>Results refresh every second. Recent commands refresh every five seconds.</p></div></div>
      <label className="command-history">Recent commands (latest 20)<select value={selectedId} onChange={event => select(event.target.value)}><option value="">Select a command</option>{selectedId && !recent.some(command => command.id === selectedId) && <option value={selectedId}>Selected command · {selectedId.slice(0, 8)}</option>}{recent.map(command => <option value={command.id} key={command.id}>{time(command.createdAtUtc)} · {format(command.total)} targets · {command.id.slice(0, 8)}</option>)}</select></label>
      {historyError && <p className="inline-error" role="alert">{historyError} Retrying automatically.</p>}
      {progressError && <p className="inline-error" role="alert">{progressError} Shown results may be stale. Retrying automatically.</p>}
      {!selectedId ? <p className="no-data">Run diagnostics or select a recent command to see its progress.</p> : !summary ? <p className="no-data" role="status">Loading command results…</p> : <>
        <div className="command-progress"><strong>{format(completed)} / {format(total)} finished</strong><span>{completed === total ? 'Complete' : 'In progress'} · Expires {time(summary.expiresAtUtc)}</span><progress value={completed} max={Math.max(1, total)} aria-label="Finished command targets" /></div>
        <div className="command-counts">{[summary.pending, summary.acknowledged, summary.succeeded, summary.failed, summary.expired].map((count, index) => <div key={statuses[index]}><strong>{format(count)}</strong><span>{statuses[index]}</span></div>)}</div>
        <div className="filter-row"><label><span className="sr-only">Result status</span><select value={status} onChange={event => { setStatus(event.target.value); setPage(1); setProgress(null) }}><option value="">All results</option>{statuses.map((name, index) => <option value={index} key={name}>{name}</option>)}</select></label></div>
        <div className="table-scroll"><table className="device-table command-results"><thead><tr><th>Device</th><th>Status</th><th>Retries</th><th>Result</th></tr></thead><tbody>{progress.items.map(item => <tr key={item.id}><td>{item.serialNumber}<small>{item.model} · {item.regionName}</small></td><td><span className={`status-pill command-status-${item.status}`}>{statuses[item.status] ?? 'Unknown'}</span></td><td>{format(item.retryCount)}</td><td>{item.detail ?? (item.status === 2 ? 'Completed successfully' : item.status === 3 ? 'Command failed' : item.status === 4 ? 'Expired before completion' : 'Awaiting result')}<small>{item.completedAtUtc ? time(item.completedAtUtc) : item.acknowledgedAtUtc ? `Acknowledged ${time(item.acknowledgedAtUtc)}` : ''}</small></td></tr>)}</tbody></table></div>
        {progress.items.length === 0 && <p className="no-data">No targets match this status.</p>}
        <div className="pagination"><span>{format(progress.total)} matching targets</span><div><button disabled={page <= 1} onClick={() => setPage(value => value - 1)}>Previous</button><span>Page {page} of {pageCount}</span><button disabled={page >= pageCount} onClick={() => setPage(value => value + 1)}>Next</button></div></div>
      </>}
    </section>
  </div>
}
