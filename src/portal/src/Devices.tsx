import { useEffect, useState } from 'react'
import type { DeviceDetail, DeviceListItem, DevicePage, RegionSummary } from './api/generated/types.gen'
import { loadDevice, searchFleetDevices } from './api/fleet'
import type { DeviceStatusChange } from './useOperatorStatus'

type Props = {
  regions: RegionSummary[]
  changes: ReadonlyMap<string, DeviceStatusChange>
  resyncVersion: number
}

const format = (value: number) => new Intl.NumberFormat().format(value)
const time = (value: string | null) => value ? new Date(value).toLocaleString() : 'Never reported'
const metric = (value: number | string | null, suffix: string) => value === null ? '—' : `${Math.round(Number(value))}${suffix}`

function latest(device: DeviceListItem, changes: ReadonlyMap<string, DeviceStatusChange>) {
  const change = changes.get(device.id.toLowerCase())
  return change ? { ...device, online: change.online, health: change.health, lastSeenAt: change.lastSeenAt } : device
}

function Status({ device }: { device: DeviceListItem }) {
  return <div className="status-pair"><span className={`status-pill ${device.online ? 'online' : 'offline'}`}>{device.online ? 'Online' : 'Offline'}</span><span className={`health-label ${device.health ?? 'unknown'}`}>{device.health ?? 'No report'}</span></div>
}

export function Devices({ regions, changes, resyncVersion }: Props) {
  const [searchText, setSearchText] = useState('')
  const [query, setQuery] = useState('')
  const [regionId, setRegionId] = useState('')
  const [status, setStatus] = useState('')
  const [health, setHealth] = useState('')
  const [page, setPage] = useState(1)
  const [result, setResult] = useState<DevicePage | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<DeviceDetail | null>(null)
  const [listError, setListError] = useState<string | null>(null)
  const [detailError, setDetailError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [retryVersion, setRetryVersion] = useState(0)

  useEffect(() => {
    const timer = setTimeout(() => { setPage(1); setQuery(searchText.trim()) }, 300)
    return () => clearTimeout(timer)
  }, [searchText])

  useEffect(() => {
    const controller = new AbortController()
    let inFlight = false
    async function refresh() {
      if (inFlight) return
      inFlight = true
      try {
        const next = await searchFleetDevices({ query, regionId, status, health, page }, controller.signal)
        if (!controller.signal.aborted) { setResult(next); setListError(null); setLoading(false) }
      } catch (error) {
        if (!controller.signal.aborted) { setListError(error instanceof Error ? error.message : 'Device search failed.'); setLoading(false) }
      } finally { inFlight = false }
    }
    void refresh()
    const timer = setInterval(() => void refresh(), 5000)
    return () => { controller.abort(); clearInterval(timer) }
  }, [query, regionId, status, health, page, resyncVersion, retryVersion])

  useEffect(() => {
    if (!selectedId) return
    const controller = new AbortController()
    async function refresh() {
      try {
        const next = await loadDevice(selectedId!, controller.signal)
        if (!controller.signal.aborted) { setDetail(next); setDetailError(null) }
      } catch (error) {
        if (!controller.signal.aborted) setDetailError(error instanceof Error ? error.message : 'Device detail failed.')
      }
    }
    void refresh()
    const timer = setInterval(() => void refresh(), 5000)
    return () => { controller.abort(); clearInterval(timer) }
  }, [selectedId, resyncVersion, retryVersion])

  const total = Number(result?.total ?? 0)
  const pageSize = Number(result?.pageSize ?? 25)
  const pageCount = Math.max(1, Math.ceil(total / pageSize))
  const selectedChange = selectedId ? changes.get(selectedId.toLowerCase()) : undefined
  const shownDetail = detail && detail.device.id === selectedId ? {
    ...detail,
    device: latest(detail.device, changes),
    cpuPercent: selectedChange ? selectedChange.cpuPercent : detail.cpuPercent,
    memoryPercent: selectedChange ? selectedChange.memoryPercent : detail.memoryPercent,
    temperatureCelsius: selectedChange ? selectedChange.temperatureCelsius : detail.temperatureCelsius,
  } : null

  return <div className="dashboard-stack">
    <section className="data-panel devices-panel" aria-labelledby="device-list-heading">
      <div className="panel-heading"><div><span className="section-kicker">DEVICE INVENTORY</span><h2 id="device-list-heading">All devices</h2><p>Search by serial or model, then open a device for its latest state.</p></div><strong className="result-count">{format(total)} devices</strong></div>
      <div className="filter-row">
        <label className="search-field"><span className="sr-only">Search devices</span><span aria-hidden="true">⌕</span><input value={searchText} onChange={event => setSearchText(event.target.value)} maxLength={100} placeholder="Search serial or model" /></label>
        <label><span className="sr-only">Region</span><select value={regionId} onChange={event => { setRegionId(event.target.value); setPage(1) }}><option value="">All regions</option>{regions.map(region => <option key={String(region.regionId)} value={String(region.regionId)}>{region.regionName}</option>)}</select></label>
        <label><span className="sr-only">Connection</span><select value={status} onChange={event => { setStatus(event.target.value); setPage(1) }}><option value="">Any connection</option><option value="online">Online</option><option value="offline">Offline</option></select></label>
        <label><span className="sr-only">Health</span><select value={health} onChange={event => { setHealth(event.target.value); setPage(1) }}><option value="">Any health</option><option value="healthy">Healthy</option><option value="degraded">Degraded</option><option value="unhealthy">Unhealthy</option></select></label>
      </div>
      {listError && <div className="inline-error" role="alert">{listError} <button onClick={() => setRetryVersion(value => value + 1)}>Retry</button></div>}
      {loading && !result ? <p className="no-data" role="status">Loading devices…</p> : result?.items.length === 0 ? <p className="no-data">No devices match these filters.</p> : <div className="table-scroll"><table className="device-table"><thead><tr><th>Device</th><th>Location</th><th>Connection / health</th><th>Last seen</th></tr></thead><tbody>{result?.items.map(item => {
        const device = latest(item, changes)
        return <tr key={device.id}><td><button className="device-link" onClick={() => { setSelectedId(device.id); setDetail(null); setDetailError(null) }}>{device.serialNumber}</button><small>{device.model}</small></td><td>{device.siteName}<small>{device.regionName}</small></td><td><Status device={device} /></td><td>{time(device.lastSeenAt)}</td></tr>
      })}</tbody></table></div>}
      <div className="pagination"><span>{total === 0 ? '0' : format((page - 1) * pageSize + 1)}–{format(Math.min(page * pageSize, total))} of {format(total)}</span><div><button disabled={page <= 1} onClick={() => setPage(value => value - 1)}>Previous</button><span>Page {page} of {pageCount}</span><button disabled={page >= pageCount} onClick={() => setPage(value => value + 1)}>Next</button></div></div>
    </section>

    {selectedId && <div className="detail-backdrop" onClick={() => setSelectedId(null)}><aside className="device-detail" role="dialog" aria-modal="true" aria-label="Device details" onClick={event => event.stopPropagation()}>
      <button className="detail-close" onClick={() => setSelectedId(null)} aria-label="Close device details">×</button>
      {detailError && <div className="inline-error" role="alert">{detailError} <button onClick={() => setRetryVersion(value => value + 1)}>Retry</button></div>}
      {!shownDetail ? <p className="no-data" role="status">Loading device details…</p> : <>
        <span className="section-kicker">DEVICE DETAIL</span><h2>{shownDetail.device.serialNumber}</h2><p className="detail-subtitle">{shownDetail.device.model} · {shownDetail.device.siteName}, {shownDetail.device.regionName}</p>
        <div className="detail-status"><Status device={shownDetail.device} /></div>
        <dl className="detail-list"><div><dt>Firmware</dt><dd>{shownDetail.device.firmwareVersion}</dd></div><div><dt>Last seen</dt><dd>{time(shownDetail.device.lastSeenAt)}</dd></div><div><dt>CPU</dt><dd>{metric(shownDetail.cpuPercent, '%')}</dd></div><div><dt>Memory</dt><dd>{metric(shownDetail.memoryPercent, '%')}</dd></div><div><dt>Temperature</dt><dd>{metric(shownDetail.temperatureCelsius, '°C')}</dd></div><div><dt>Device ID</dt><dd className="device-id">{shownDetail.device.id}</dd></div></dl>
        <p className="detail-note">Connection shows whether the kiosk is online. Health is its latest reported condition, which can remain visible after it goes offline.</p>
      </>}
    </aside></div>}
  </div>
}
