import { applyDemoControl, getDemoStatus } from './generated/sdk.gen'
import type { DemoRequest } from './generated/types.gen'

export async function loadDemoStatus(signal: AbortSignal) {
  const { data, response } = await getDemoStatus({ signal })
  if (!data) throw new Error(`Demo status failed (HTTP ${response?.status ?? 'unknown'}).`)
  return data
}

export async function sendDemoControl(body: DemoRequest, signal: AbortSignal) {
  const { data, error, response } = await applyDemoControl({ body, signal })
  if (!data) {
    const validation = error && 'errors' in error ? Object.values(error.errors ?? {}).flat().join(' ') : ''
    throw new Error(validation || error?.detail || error?.title || `Demo request failed (HTTP ${response?.status ?? 'unknown'}).`)
  }
  return data
}
