// Thin API client: JWT storage, automatic refresh-and-retry on 401, typed
// helpers for the endpoints the dashboard consumes. Tokens live in
// localStorage — everything here is client-side only (guarded for SSR).

const ACCESS_KEY = 'veil.accessToken'
const REFRESH_KEY = 'veil.refreshToken'

const isBrowser = typeof window !== 'undefined'

export function hasSession(): boolean {
  return isBrowser && window.localStorage.getItem(ACCESS_KEY) !== null
}

export function clearSession(): void {
  if (!isBrowser) return
  window.localStorage.removeItem(ACCESS_KEY)
  window.localStorage.removeItem(REFRESH_KEY)
}

function storeTokens(tokens: TokenPair): void {
  window.localStorage.setItem(ACCESS_KEY, tokens.accessToken)
  window.localStorage.setItem(REFRESH_KEY, tokens.refreshToken)
}

interface TokenPair {
  accessToken: string
  expiresInSeconds: number
  refreshToken: string
  tokenType: string
}

export async function login(email: string, password: string): Promise<boolean> {
  const response = await fetch('/v1/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })
  if (!response.ok) return false
  storeTokens((await response.json()) as TokenPair)
  return true
}

async function tryRefresh(): Promise<boolean> {
  const refreshToken = window.localStorage.getItem(REFRESH_KEY)
  if (!refreshToken) return false
  const response = await fetch('/v1/auth/refresh', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken }),
  })
  if (!response.ok) {
    clearSession()
    return false
  }
  storeTokens((await response.json()) as TokenPair)
  return true
}

export class UnauthorizedError extends Error {
  constructor() {
    super('Oturum geçersiz — yeniden giriş yapın.')
  }
}

/** Authenticated GET with a single refresh-and-retry on 401. */
export async function apiGet<T>(path: string): Promise<T> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const access = window.localStorage.getItem(ACCESS_KEY)
    const response = await fetch(path, {
      headers: access ? { Authorization: `Bearer ${access}` } : {},
    })
    if (response.status === 401) {
      if (attempt === 0 && (await tryRefresh())) continue
      clearSession()
      throw new UnauthorizedError()
    }
    if (!response.ok) throw new Error(`API ${response.status}: ${path}`)
    return (await response.json()) as T
  }
  throw new UnauthorizedError()
}

/** Authenticated mutation with the same refresh-and-retry semantics. */
export async function apiSend<T = unknown>(
  path: string,
  method: 'POST' | 'PUT' | 'DELETE',
  body?: unknown,
): Promise<T | null> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const access = window.localStorage.getItem(ACCESS_KEY)
    const response = await fetch(path, {
      method,
      headers: {
        ...(access ? { Authorization: `Bearer ${access}` } : {}),
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })
    if (response.status === 401) {
      if (attempt === 0 && (await tryRefresh())) continue
      clearSession()
      throw new UnauthorizedError()
    }
    if (!response.ok) throw new Error(`API ${response.status}: ${path}`)
    if (response.status === 204) return null
    const text = await response.text()
    return text.length > 0 ? (JSON.parse(text) as T) : null
  }
  throw new UnauthorizedError()
}

// ── Response shapes (mirror the control plane contracts) ─────────────

export interface ZoneSummary {
  id: string
  hostname: string
  status: string
  ruleCount: number
}

export interface ListZonesResponse {
  items: Array<ZoneSummary>
  page: number
  pageSize: number
  totalCount: number
}

export interface RuleCondition {
  type: string
  value?: string | null
  name?: string | null
  asn?: number | null
  mode?: string | null
}

export interface Rule {
  id: string
  name: string
  priority: number
  action: string
  isEnabled: boolean
  conditions: Array<RuleCondition>
  rateLimit: { requests: number; windowSecs: number } | null
}

export interface ZoneDetail {
  id: string
  hostname: string
  status: string
  upstream: {
    targets: Array<{ url: string; weight: number }>
    strategy: string
    connectTimeoutMs: number
    responseTimeoutMs: number
    passHostHeader: boolean
  }
  challenge: {
    enabled: boolean
    difficulty: number
    expirationSeconds: number
    requireCaptcha: boolean
  }
  rules: Array<Rule>
}

export interface VolumePoint {
  bucket: string
  total: number
  blocked: number
  challenged: number
  rateLimited: number
}

export interface AnalyticsSummary {
  windowHours: number
  total: number
  allowed: number
  blocked: number
  challenged: number
  challengePassed: number
  rateLimited: number
  uniqueIps: number
  avgDurationMs: number
  series: Array<VolumePoint>
}

export interface TopIpEntry {
  clientIp: string
  total: number
  blocked: number
  challenged: number
  rateLimited: number
  lastSeenUtc: string
}

export interface TopIpsResponse {
  windowHours: number
  items: Array<TopIpEntry>
}

export interface VerdictCount {
  verdict: string
  total: number
}

export interface VerdictBreakdownResponse {
  windowHours: number
  items: Array<VerdictCount>
}

export interface ChallengeStatsResponse {
  windowHours: number
  issued: number
  passed: number
  passRate: number
}

export interface CertificateSummary {
  id: string
  hostname: string
  status: string
  requestedAtUtc: string
  expiresAtUtc: string | null
}

export interface ListCertificatesResponse {
  items: Array<CertificateSummary>
  page: number
  pageSize: number
  totalCount: number
}

export interface CertificateDetail {
  id: string
  hostname: string
  status: string
  requestedAtUtc: string
  issuedAtUtc: string | null
  expiresAtUtc: string | null
  lastError: string | null
}

export interface EdgeNodeSummary {
  id: string
  name: string
  address: string
  status: string
  registeredAtUtc: string
  lastSeenAtUtc: string | null
  lastPushSucceeded: boolean | null
  lastPushAtUtc: string | null
}

export interface ListEdgeNodesResponse {
  items: Array<EdgeNodeSummary>
  page: number
  pageSize: number
  totalCount: number
}

export interface ConfigPushLogEntry {
  succeeded: boolean
  error: string | null
  pushedAtUtc: string
}

export interface ConfigPushLogResponse {
  nodeId: string
  items: Array<ConfigPushLogEntry>
  page: number
  pageSize: number
  totalCount: number
}
