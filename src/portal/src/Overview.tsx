import type { FleetSummary } from './api/generated/types.gen'
import type { OperatorStatusBatch } from './useOperatorStatus'

type Props = {
  summary: FleetSummary | null
  batch: OperatorStatusBatch | null
  messageRate: number
  loading: boolean
  error: string | null
  onDevices: () => void
  onRetry: () => void
}

const format = (value: number) => new Intl.NumberFormat().format(value)

export function Overview({ summary, batch, messageRate, loading, error, onDevices, onRetry }: Props) {
  if (!summary) {
    return <div className="data-panel state-panel" role={error ? 'alert' : 'status'}>
      <h2>{error ? 'Fleet overview unavailable' : 'Loading fleet overview'}</h2>
      <p>{error ?? 'Connecting to the Fleet API for the latest fleet snapshot.'}</p>
      {error && <button className="primary-button" onClick={onRetry}>Try again</button>}
    </div>
  }

  const total = Number(summary.totalDevices)
  const online = batch?.onlineDevices ?? Number(summary.onlineDevices)
  const healthy = batch?.healthyDevices ?? Number(summary.healthyDevices)
  const degraded = batch?.degradedDevices ?? Number(summary.degradedDevices)
  const unhealthy = batch?.unhealthyDevices ?? Number(summary.unhealthyDevices)
  const percent = total === 0 ? 0 : Math.round(online / total * 100)

  return <div className="dashboard-stack">
    {error && <div className="inline-error" role="alert">The latest snapshot could not load. Showing the last known values. <button onClick={onRetry}>Retry</button></div>}
    <div className="metric-grid">
      <section className="metric-card metric-primary" aria-label="Online devices">
        <span className="metric-label">ONLINE DEVICES</span>
        <strong>{format(online)} <small>/ {format(total)}</small></strong>
        <div className="meter" role="progressbar" aria-valuenow={online} aria-valuemin={0} aria-valuemax={total || 1} aria-label="Online devices"><span style={{ width: `${percent}%` }} /></div>
        <p>{percent}% of the fleet connected <span>·</span> {format(Math.max(0, total - online))} offline</p>
      </section>
      <section className="metric-card"><span className="metric-label">HEALTHY</span><strong>{format(healthy)}</strong><p>Devices reporting normal health</p></section>
      <section className="metric-card"><span className="metric-label">NEEDS ATTENTION</span><strong>{format(degraded + unhealthy)}</strong><p>{format(degraded)} degraded <span>·</span> {format(unhealthy)} unhealthy</p></section>
      <section className="metric-card"><span className="metric-label">LIVE MESSAGE RATE</span><strong>{format(Math.round(messageRate))}<small> / sec</small></strong><p>Telemetry and heartbeats received</p></section>
    </div>

    <section className="data-panel region-panel" aria-labelledby="region-heading">
      <div className="panel-heading"><div><span className="section-kicker">FLEET HEALTH</span><h2 id="region-heading">Health by region</h2><p>Connection and reported health are tracked separately.</p></div><button className="text-button" onClick={onDevices}>View devices <span aria-hidden="true">→</span></button></div>
      <div className="region-legend"><span><i className="healthy-dot" />Healthy</span><span><i className="degraded-dot" />Degraded</span><span><i className="unhealthy-dot" />Unhealthy</span><span><i className="offline-dot" />Offline</span></div>
      {summary.regions.length === 0 ? <p className="no-data">No regions have been registered yet.</p> :
        <div className="region-list">{summary.regions.map((region) => {
          const count = Number(region.totalDevices)
          const segments = [
            { key: 'healthy', value: Number(region.healthyDevices) },
            { key: 'degraded', value: Number(region.degradedDevices) },
            { key: 'unhealthy', value: Number(region.unhealthyDevices) },
            { key: 'offline', value: Number(region.offlineDevices) },
          ]
          return <div className="region-row" key={String(region.regionId)}>
            <div className="region-name"><strong>{region.regionName}</strong><small>{format(Number(region.onlineDevices))} / {format(count)} online</small></div>
            <div className="region-bar" aria-label={`${region.regionName}: ${segments.map(item => `${item.value} ${item.key}`).join(', ')}`} role="img">
              {segments.map(item => <span key={item.key} className={`bar-${item.key}`} style={{ width: `${count ? item.value / count * 100 : 0}%` }} />)}
            </div>
            <span className="region-total">{format(count)} devices</span>
          </div>
        })}</div>}
    </section>
    {loading && <span className="refresh-note" role="status">Refreshing fleet snapshot…</span>}
  </div>
}
