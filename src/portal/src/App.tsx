import { useCallback, useEffect, useState } from 'react'
import type { FleetSummary } from './api/generated/types.gen'
import { loadFleetSummary } from './api/fleet'
import { Devices } from './Devices'
import { Overview } from './Overview'
import { useOperatorStatus } from './useOperatorStatus'

const screens = [
  { id: 'overview', label: 'Fleet overview', icon: '◫', description: 'A live view of kiosk health and activity across every region.' },
  { id: 'devices', label: 'Devices', icon: '▦', description: 'Find a kiosk and inspect its connection, health, and firmware.' },
  { id: 'commands', label: 'Commands', icon: '⌁', description: 'Send an operation to a device, a group, or the full fleet.' },
  { id: 'rollouts', label: 'Rollouts', icon: '↗', description: 'Stage software releases and watch each health gate.' },
  { id: 'demo', label: 'Demo control', icon: '⚙', description: 'Control the fleet scenarios used in the local demo.' },
] as const

type ScreenId = (typeof screens)[number]['id']

function App() {
  const [activeScreen, setActiveScreen] = useState<ScreenId>('overview')
  const [summary, setSummary] = useState<FleetSummary | null>(null)
  const [summaryError, setSummaryError] = useState<string | null>(null)
  const [loadingSummary, setLoadingSummary] = useState(true)
  const [resyncVersion, setResyncVersion] = useState(0)
  const resync = useCallback(() => setResyncVersion(value => value + 1), [])
  const { batch, changes, messageRate, connectionState } = useOperatorStatus(resync)
  const screen = screens.find(item => item.id === activeScreen) ?? screens[0]

  useEffect(() => {
    const controller = new AbortController()
    let inFlight = false
    async function refresh() {
      if (inFlight) return
      inFlight = true
      try {
        const next = await loadFleetSummary(controller.signal)
        if (!controller.signal.aborted) { setSummary(next); setSummaryError(null); setLoadingSummary(false) }
      } catch (error) {
        if (!controller.signal.aborted) { setSummaryError(error instanceof Error ? error.message : 'Fleet summary failed.'); setLoadingSummary(false) }
      } finally { inFlight = false }
    }
    void refresh()
    const timer = setInterval(() => void refresh(), 5000)
    return () => { controller.abort(); clearInterval(timer) }
  }, [resyncVersion])

  return <div className="app-shell">
    <aside className="sidebar">
      <a className="brand" href="#overview" onClick={() => setActiveScreen('overview')} aria-label="FleetPulse home"><span className="brand-mark" aria-hidden="true"><i /><i /><i /><i /></span><span>fleet<span className="brand-light">pulse</span><small>CONTROL CENTER</small></span></a>
      <div className="nav-label">WORKSPACE</div>
      <nav aria-label="Main navigation">{screens.map(item => <button className={`nav-item${activeScreen === item.id ? ' active' : ''}`} key={item.id} onClick={() => setActiveScreen(item.id)} aria-current={activeScreen === item.id ? 'page' : undefined}><span className="nav-icon" aria-hidden="true">{item.icon}</span>{item.label}{item.id === 'overview' && <span className="nav-dot" />}</button>)}</nav>
      <div className="sidebar-bottom"><div className="environment-card"><span className="environment-indicator" /><span><strong>Local environment</strong><small>Fleet API + operator hub</small></span></div><div className="profile"><span className="avatar">FP</span><span><strong>Fleet operator</strong><small>Local workspace</small></span></div></div>
    </aside>
    <main className="main-panel">
      <header className="topbar"><div className="breadcrumbs"><span>Workspace</span><span className="crumb-slash">/</span><strong>{screen.label}</strong></div><div className="topbar-actions"><span className={`connection ${connectionState}`}><span />{connectionState === 'live' ? 'Live updates' : connectionState === 'connecting' ? 'Connecting' : connectionState === 'reconnecting' ? 'Reconnecting' : 'Live updates offline'}</span></div></header>
      <section className="content" aria-labelledby="page-title">
        <div className="page-heading"><div><div className="eyebrow">FLEET OPERATIONS <span>·</span> WORKSPACE</div><h1 id="page-title">{screen.label}</h1><p>{screen.description}</p></div>{(activeScreen === 'overview' || activeScreen === 'devices') && <span className="live-badge"><span />LIVE FLEET DATA</span>}</div>
        {activeScreen === 'overview' ? <Overview summary={summary} batch={connectionState === 'live' ? batch : null} messageRate={connectionState === 'live' ? messageRate : 0} loading={loadingSummary} error={summaryError} onDevices={() => setActiveScreen('devices')} onRetry={resync} /> : activeScreen === 'devices' ? <Devices regions={summary?.regions ?? []} changes={changes} resyncVersion={resyncVersion} /> : <div className="empty-panel"><div className="empty-art" aria-hidden="true"><div className="orbit orbit-one" /><div className="orbit orbit-two" /><div className="device-card"><span className="device-screen"><span /><span /><span /></span><span className="device-base" /></div><span className="spark spark-one">✦</span><span className="spark spark-two">✳</span><span className="spark spark-three">·</span></div><div className="empty-copy"><span className="coming-soon">YOUR WORKSPACE</span><h2>{screen.label} starts here</h2><p>This area will become available in a later FleetPulse task.</p></div></div>}
        <footer className="page-footer"><span><span className="footer-mark">◈</span> FleetPulse <span className="footer-separator">/</span> Built for reliable operations</span><span>LOCAL WORKSPACE <span className="footer-dot" /></span></footer>
      </section>
    </main>
  </div>
}

export default App
