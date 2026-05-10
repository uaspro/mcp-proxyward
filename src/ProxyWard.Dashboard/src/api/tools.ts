import { getJson, postJson } from './client'

export type ToolInventoryResponse = {
  servers: ToolInventoryServer[]
}

export type ToolInventoryServer = {
  serverId: string
  latestVersion: number | null
  driftStatus: string
  tools: ToolInventoryTool[]
}

export type ToolInventoryTool = {
  name: string
  latestVersion: number
  driftStatus: string
  title: string | null
  description: string | null
  nameHash: string | null
  titleHash: string | null
  descriptionHash: string | null
  inputSchemaHash: string | null
  outputSchemaHash: string | null
}

export type ToolDiscoveryRequest = {
  serverId: string
  upstream?: string | null
}

export type ToolDiscoveryResponse = {
  serverId: string
  upstream: string
  latestVersion: number
  snapshotHash: string
  wasNewVersion: boolean
  tools: ToolInventoryTool[]
}

export function getToolInventory(signal?: AbortSignal) {
  return getJson<ToolInventoryResponse>('/api/tools', signal)
}

export function discoverTools(request: ToolDiscoveryRequest, signal?: AbortSignal) {
  return postJson<ToolDiscoveryResponse>('/api/tools/discover', request, signal)
}
