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

/**
 * Non-2xx (non-401) API failure. Carries the status code and the parsed error
 * body so callers can distinguish cases (e.g. 503 = schema registry disabled)
 * and surface the server's own message (e.g. Vaultify's rejection reason).
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly body: unknown,
    path: string,
  ) {
    super(`API ${status}: ${path}`)
  }
}

async function readError(response: Response, path: string): Promise<ApiError> {
  let body: unknown = null
  try {
    const text = await response.text()
    body = text.length > 0 ? JSON.parse(text) : null
  } catch {
    body = null
  }
  return new ApiError(response.status, body, path)
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
    if (!response.ok) throw await readError(response, path)
    return (await response.json()) as T
  }
  throw new UnauthorizedError()
}

/** Authenticated mutation with the same refresh-and-retry semantics. */
export async function apiSend<T = unknown>(
  path: string,
  method: 'POST' | 'PUT' | 'PATCH' | 'DELETE',
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
    if (!response.ok) throw await readError(response, path)
    if (response.status === 204) return null
    const text = await response.text()
    return text.length > 0 ? (JSON.parse(text) as T) : null
  }
  throw new UnauthorizedError()
}

/**
 * Opens an authenticated Server-Sent Events stream and invokes `onEvent` for
 * each `data:` payload (parsed as JSON). Uses fetch + a stream reader (not
 * EventSource) so the Bearer header and refresh-retry apply. Resolves when
 * the stream ends or `signal` aborts; throws UnauthorizedError on 401.
 */
export async function apiStream<T>(
  path: string,
  onEvent: (data: T) => void,
  signal: AbortSignal,
): Promise<void> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const access = window.localStorage.getItem(ACCESS_KEY)
    const response = await fetch(path, {
      headers: access ? { Authorization: `Bearer ${access}` } : {},
      signal,
    })
    if (response.status === 401) {
      if (attempt === 0 && (await tryRefresh())) continue
      clearSession()
      throw new UnauthorizedError()
    }
    if (!response.ok || response.body === null) throw new Error(`API ${response.status}: ${path}`)

    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    while (true) {
      const { done, value } = await reader.read()
      if (done) return
      buffer += decoder.decode(value, { stream: true })
      // SSE frames are separated by a blank line.
      let sep: number
      while ((sep = buffer.indexOf('\n\n')) !== -1) {
        const frame = buffer.slice(0, sep)
        buffer = buffer.slice(sep + 2)
        for (const line of frame.split('\n')) {
          if (line.startsWith('data:')) {
            try {
              onEvent(JSON.parse(line.slice(5).trim()) as T)
            } catch {
              // Ignore malformed frames (e.g. the initial ": connected").
            }
          }
        }
      }
    }
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
    riskThreshold: number
    cookieDomain: string
  }
  rules: Array<Rule>
  cacheEnabled: boolean
  shadow: boolean
  managedRules: {
    sqlInjection: boolean
    xss: boolean
    pathTraversal: boolean
    inspectBody: boolean
    action: string
    blockOversizedBody: boolean
  }
  widget: WidgetConfig
}

/** Embeddable bot-verification widget config (secret never returned). */
export interface WidgetConfig {
  enabled: boolean
  siteKey: string
  theme: string
  hasSecret: boolean
}

/** Returned once when widget keys are (re)generated — the secret is shown here only. */
export interface WidgetRotateResult {
  siteKey: string
  secret: string
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

export interface LiveLogEvent {
  tsMs: number
  zone: string
  method: string
  path: string
  status: number
  verdict: string
  clientIp: string
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

export interface ApiKeySummary {
  id: string
  name: string
  scopes: Array<string>
  isActive: boolean
  createdAt: string
  revokedAt: string | null
  lastUsedAt: string | null
}

export interface ListApiKeysResponse {
  items: Array<ApiKeySummary>
}

export interface CreateApiKeyResponse {
  id: string
  name: string
  scopes: Array<string>
  key: string
  createdAtUtc: string
}

// ── AI traffic analysis (Phase 11) ───────────────────────────────────

export interface TrafficCount {
  value: string
  count: number
}

export interface SuggestedRule {
  conditionType: string
  value: string
  action: string
}

export interface AnalystVerdict {
  classification: string
  confidence: number
  summary: string
  suggestedRule: SuggestedRule | null
}

export interface TrafficIncident {
  id: string
  detectedAtUtc: string
  zone: string
  anomalyScore: number
  signals: Array<string>
  ratePerSecond: number
  baselineRatePerSecond: number
  blockedRatio: number
  distinctIps: number
  topIps: Array<TrafficCount>
  topPaths: Array<TrafficCount>
  topAsns: Array<TrafficCount>
  classification: string
  suggestedRule: SuggestedRule | null
  verdict: AnalystVerdict | null
  action: string
}

export interface ApplyAiRuleResult {
  applied: boolean
  action: string
  reason: string | null
}

/**
 * Manually applies an AI-suggested rule to a zone (one-click apply). `shadow`
 * stages it observe-only (Log action); otherwise the real action is enforced.
 * Returns the outcome (applied + resolved action, or a reason it was skipped).
 */
export async function applyAiRule(
  zone: string,
  rule: SuggestedRule,
  shadow: boolean,
): Promise<ApplyAiRuleResult> {
  const result = await apiSend<ApplyAiRuleResult>('/v1/intelligence/incidents/apply', 'POST', {
    zone,
    rule,
    shadow,
  })
  if (result === null) throw new Error('Boş yanıt.')
  return result
}

// ── Schema registry (Vaultify-backed) ────────────────────────────────

// Subject/version list items are proxied straight from Vaultify (its HATEOAS
// envelope unwrapped to `data`), so exact field names aren't guaranteed — the
// schemas route normalizes them defensively. Kept as open records for that.
export type SchemaSubject = {
  subject?: string
  name?: string
  latestVersion?: string
  version?: string
  type?: string
  [key: string]: unknown
}

export type SchemaVersion = {
  version?: string
  createdAt?: string
  createdAtUtc?: string
  type?: string
  [key: string]: unknown
}

/** Compatibility pre-check outcome. `compatible` is null when the registry
 * could not be reached (the upload isn't blocked on it). */
export interface SchemaCompatibilityResponse {
  compatible: boolean | null
  detail: string | null
}

/** A rule that references a schema subject, with a link back to its zone. */
export interface SchemaUsageItem {
  zoneId: string
  hostname: string
  ruleId: string
  ruleName: string
  versions: string
}

export interface SchemaUsageResponse {
  subject: string
  items: Array<SchemaUsageItem>
}

/** Normalizes a proxied subject entry to the fields the UI needs. */
export function subjectName(s: SchemaSubject): string {
  return s.subject ?? s.name ?? ''
}

export function subjectLatestVersion(s: SchemaSubject): string | null {
  return s.latestVersion ?? s.version ?? null
}

/** Normalizes a proxied version entry's identifier. */
export function versionId(v: SchemaVersion): string {
  return v.version ?? ''
}
