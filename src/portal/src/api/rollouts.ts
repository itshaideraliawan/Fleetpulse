import { createRollout, getRecentRollouts, getRolloutProgress } from './generated/sdk.gen'
import type { CreateRolloutRequest } from './generated/types.gen'

export async function startRollout(body: CreateRolloutRequest, signal: AbortSignal) {
  const { data, error, response } = await createRollout({ body, signal })
  if (!data) {
    const validation = error && 'errors' in error ? Object.values(error.errors ?? {}).flat().join(' ') : ''
    throw new Error(validation || error?.detail || error?.title || `Rollout request failed (HTTP ${response?.status ?? 'unknown'}). Check recent rollouts before trying again.`)
  }
  return data
}

export async function loadRecentRollouts(signal: AbortSignal) {
  const { data, response } = await getRecentRollouts({ signal })
  if (!data) throw new Error(`Rollout history failed (HTTP ${response?.status ?? 'unknown'}).`)
  return data
}

export async function loadRolloutProgress(id: string, signal: AbortSignal) {
  const { data, response } = await getRolloutProgress({ path: { id }, signal })
  if (!data) throw new Error(`Rollout progress failed (HTTP ${response?.status ?? 'unknown'}).`)
  return data
}
