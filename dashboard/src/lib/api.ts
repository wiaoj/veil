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
