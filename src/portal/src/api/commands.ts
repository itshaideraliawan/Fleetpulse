import { createCommand, getCommandProgress, getRecentCommands } from './generated/sdk.gen'
import type { CreateCommandRequest } from './generated/types.gen'

export async function sendCommand(body: CreateCommandRequest, signal: AbortSignal) {
  const { data, error, response } = await createCommand({ body, signal })
  if (!data) {
    const validation = error && 'errors' in error ? Object.values(error.errors ?? {}).flat().join(' ') : ''
    throw new Error(validation || error?.title || `Command request failed (HTTP ${response?.status ?? 'unknown'}). Check recent commands before sending again.`)
  }
  return data
}

export async function loadRecentCommands(signal: AbortSignal) {
  const { data, response } = await getRecentCommands({ signal })
  if (!data) throw new Error(`Command history failed (HTTP ${response?.status ?? 'unknown'}).`)
  return data
}

export async function loadCommandProgress(id: string, page: number, status: string, signal: AbortSignal) {
  const { data, response } = await getCommandProgress({
    path: { id }, query: { page, pageSize: 25, status: status === '' ? undefined : Number(status) }, signal,
  })
  if (!data) throw new Error(`Command progress failed (HTTP ${response?.status ?? 'unknown'}).`)
  return data
}
