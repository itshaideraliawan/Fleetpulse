import { client } from './generated/client.gen'
import { getDevice, getFleetSummary, searchDevices } from './generated/sdk.gen'
import type { DeviceDetail, DevicePage, FleetSummary } from './generated/types.gen'

client.setConfig({ baseUrl: window.location.origin })

export async function loadFleetSummary(signal: AbortSignal): Promise<FleetSummary> {
  const { data, response } = await getFleetSummary({ signal })
  if (!data) throw new Error(`Fleet API summary request failed (HTTP ${response?.status ?? 'unknown'}).`)
  return data
}

export async function searchFleetDevices(
  filters: { query: string; regionId: string; status: string; health: string; page: number },
  signal: AbortSignal,
): Promise<DevicePage> {
  const { data, response } = await searchDevices({
    query: {
      q: filters.query || undefined,
      regionId: filters.regionId || undefined,
      status: filters.status || undefined,
      health: filters.health || undefined,
      page: filters.page,
      pageSize: 25,
    },
    signal,
  })
  if (!data) throw new Error(`Fleet API device search failed (HTTP ${response?.status ?? 'unknown'}).`)
  return data
}

export async function loadDevice(id: string, signal: AbortSignal): Promise<DeviceDetail> {
  const { data, response } = await getDevice({ path: { id }, signal })
  if (!data) throw new Error(`Fleet API device detail failed (HTTP ${response?.status ?? 'unknown'}).`)
  return data
}
