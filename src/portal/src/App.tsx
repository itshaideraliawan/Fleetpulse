import { useState } from 'react'

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
  const screen = screens.find((item) => item.id === activeScreen) ?? screens[0]

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a className="brand" href="#overview" onClick={() => setActiveScreen('overview')} aria-label="FleetPulse home">
          <span className="brand-mark" aria-hidden="true"><i /><i /><i /><i /></span>
          <span>fleet<span className="brand-light">pulse</span><small>CONTROL CENTER</small></span>
        </a>

        <div className="nav-label">WORKSPACE</div>
        <nav aria-label="Main navigation">
          {screens.map((item) => (
            <button
              className={`nav-item${activeScreen === item.id ? ' active' : ''}`}
              key={item.id}
              onClick={() => setActiveScreen(item.id)}
              aria-current={activeScreen === item.id ? 'page' : undefined}
            >
              <span className="nav-icon" aria-hidden="true">{item.icon}</span>
              {item.label}
              {item.id === 'overview' && <span className="nav-dot" />}
            </button>
          ))}
        </nav>

        <div className="sidebar-bottom">
          <div className="environment-card">
            <span className="environment-indicator" />
            <span><strong>Local environment</strong><small>Ready for your fleet</small></span>
            <span className="more">···</span>
          </div>
          <div className="profile"><span className="avatar">FP</span><span><strong>Fleet operator</strong><small>Workspace admin</small></span><span className="more">⌄</span></div>
        </div>
      </aside>

      <main className="main-panel">
        <header className="topbar">
          <div className="breadcrumbs"><span>Workspace</span><span className="crumb-slash">/</span><strong>{screen.label}</strong></div>
          <div className="topbar-actions"><span className="connection"><span />API connection pending</span><button className="help-button" aria-label="Help">?</button></div>
        </header>

        <section className="content" aria-labelledby="page-title">
          <div className="page-heading">
            <div><div className="eyebrow">FLEET OPERATIONS <span>·</span> WORKSPACE</div><h1 id="page-title">{screen.label}</h1><p>{screen.description}</p></div>
            <button className="date-button" disabled><span aria-hidden="true">◷</span> Last 24 hours <span className="chevron">⌄</span></button>
          </div>

          <div className="empty-panel">
            <div className="empty-art" aria-hidden="true">
              <div className="orbit orbit-one" /><div className="orbit orbit-two" />
              <div className="device-card"><span className="device-screen"><span /><span /><span /></span><span className="device-base" /></div>
              <span className="spark spark-one">✦</span><span className="spark spark-two">✳</span><span className="spark spark-three">·</span>
            </div>
            <div className="empty-copy"><span className="coming-soon">YOUR WORKSPACE</span><h2>{activeScreen === 'overview' ? 'Your fleet starts here' : `${screen.label} starts here`}</h2><p>Connect the Fleet API to bring your kiosk operations into view. Live data and controls will appear here as they become available.</p><span className="setup-note"><span className="note-icon">i</span> This screen is ready for the next stage of setup.</span></div>
          </div>

          <footer className="page-footer"><span><span className="footer-mark">◈</span> FleetPulse <span className="footer-separator">/</span> Built for reliable operations</span><span>LOCAL WORKSPACE <span className="footer-dot" /></span></footer>
        </section>
      </main>
    </div>
  )
}

export default App
