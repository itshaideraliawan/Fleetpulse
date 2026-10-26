import { useEffect, useState } from 'react'
import { HubConnectionBuilder } from '@microsoft/signalr'

export type DeviceStatusChange = {
  deviceId: string
  online: boolean
  health: string | null
  lastSeenAt: string | null
  cpuPercent: number | null
  memoryPercent: number | null
  temperatureCelsius: number | null
}

export type OperatorStatusBatch = {
  sentAtUtc: string
  onlineDevices: number
  healthyDevices: number
  degradedDevices: number
  unhealthyDevices: number
  messagesSinceLastPush: number
  messagesPerSecond: number
  changes: DeviceStatusChange[]
}

export type ConnectionState = 'connecting' | 'live' | 'reconnecting' | 'offline'

export function useOperatorStatus(onResync: () => void) {
  const [batch, setBatch] = useState<OperatorStatusBatch | null>(null)
  const [changes, setChanges] = useState<ReadonlyMap<string, DeviceStatusChange>>(new Map<string, DeviceStatusChange>())
  const [messageRate, setMessageRate] = useState(0)
  const [connectionState, setConnectionState] = useState<ConnectionState>('connecting')

  useEffect(() => {
    let disposed = false
    let retryTimer: ReturnType<typeof setTimeout> | undefined
    let rateTimer: ReturnType<typeof setTimeout> | undefined
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/operator')
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => 5000 })
      .build()

    connection.on('statusBatch', (nextBatch: OperatorStatusBatch) => {
      if (disposed) return
      setBatch(nextBatch)
      setMessageRate(nextBatch.messagesPerSecond)
      clearTimeout(rateTimer)
      rateTimer = setTimeout(() => setMessageRate(0), 2500)
      setChanges(previous => {
        const updated = new Map(previous)
        for (const change of nextBatch.changes) updated.set(change.deviceId.toLowerCase(), change)
        return updated
      })
    })
    connection.onreconnecting(() => {
      if (!disposed) { setConnectionState('reconnecting'); setBatch(null); setMessageRate(0) }
    })
    connection.onreconnected(() => {
      if (!disposed) {
        setBatch(null)
        setChanges(new Map<string, DeviceStatusChange>())
        setConnectionState('live')
        onResync()
      }
    })
    connection.onclose(() => {
      if (!disposed) {
        setConnectionState('offline')
        setBatch(null)
        setMessageRate(0)
        retryTimer = setTimeout(() => void start(), 5000)
      }
    })

    async function start() {
      try {
        await connection.start()
        if (!disposed) {
          setConnectionState('live')
          onResync()
        }
      } catch {
        if (!disposed) {
          setConnectionState('offline')
          retryTimer = setTimeout(() => void start(), 5000)
        }
      }
    }

    void start()
    return () => {
      disposed = true
      clearTimeout(retryTimer)
      clearTimeout(rateTimer)
      void connection.stop()
    }
  }, [onResync])

  return { batch, changes, messageRate, connectionState }
}
