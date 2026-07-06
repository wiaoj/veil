import { Link, createFileRoute, useNavigate } from '@tanstack/react-router'
import { ArrowLeft, ChevronDown, ChevronUp, Pencil, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import type { Rule, ZoneDetail } from '#/lib/api'
import { apiSend } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'
import { PageState } from '@/components/PageState'
import { StatusBadge } from '@/components/StatusBadge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Input, Select } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

export const Route = createFileRoute('/zones_/$zoneId')({
  component: ZoneDetailPage,
})

function ZoneDetailPage() {
  const { zoneId } = Route.useParams()
  const navigate = useNavigate()
  // bump forces a refetch after mutations
  const [bump, setBump] = useState(0)
  const zone = useApiData<ZoneDetail>(`/v1/zones/${zoneId}?_=${bump}`)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function deleteZone(hostname: string) {
    if (!window.confirm(`"${hostname}" zone'u ve tüm kuralları kalıcı olarak silinsin mi?`)) return
    setBusy(true)
    setError(null)
    try {
      await apiSend(`/v1/zones/${zoneId}`, 'DELETE')
      navigate({ to: '/zones' })
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Silme başarısız.')
      setBusy(false)
    }
  }

  async function mutate(action: () => Promise<unknown>) {
    setBusy(true)
    setError(null)
    try {
      await action()
      setBump((n) => n + 1)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'İşlem başarısız.')
    } finally {
      setBusy(false)
    }
  }

  if (zone.loading) return <PageState>Yükleniyor…</PageState>
  if (zone.error) return <PageState>{zone.error}</PageState>
  if (!zone.data) return null

  const z = zone.data
  const paused = z.status === 'Paused'

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <Link
            to="/zones"
            className="text-muted-foreground hover:text-foreground mb-2 inline-flex items-center gap-1 text-sm no-underline"
          >
            <ArrowLeft className="size-4" /> Zone'lar
          </Link>
          <div className="flex items-center gap-3">
            <h1 className="text-2xl font-semibold tracking-tight">{z.hostname}</h1>
            <StatusBadge status={z.status} />
          </div>
          <p className="text-muted-foreground mt-1 font-mono text-xs">{z.id}</p>
        </div>
        <div className="flex items-center gap-2">
          {(z.status === 'Provisioning' || z.status === 'Error') && (
            <Button
              disabled={busy}
              onClick={() => mutate(() => apiSend(`/v1/zones/${z.id}/activate`, 'POST'))}
            >
              Aktive et
            </Button>
          )}
          <Button
            variant="outline"
            disabled={busy}
            onClick={() => mutate(() => apiSend(`/v1/zones/${z.id}/${paused ? 'resume' : 'pause'}`, 'POST'))}
          >
            {paused ? 'Devam ettir' : 'Duraklat'}
          </Button>
          <Button variant="destructive" disabled={busy} onClick={() => deleteZone(z.hostname)}>
            <Trash2 className="size-4" /> Sil
          </Button>
        </div>
      </div>

      {error && <p className="text-destructive text-sm">{error}</p>}

      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <CardHeader className="flex-row items-center justify-between">
            <CardTitle className="text-sm">Upstream</CardTitle>
            <EditUpstreamDialog
              zone={z}
              busy={busy}
              onSave={(body) => mutate(() => apiSend(`/v1/zones/${z.id}/upstream`, 'PUT', body))}
            />
          </CardHeader>
          <CardContent className="space-y-2 text-sm">
            {z.upstream.targets.map((t) => (
              <p key={t.url} className="font-mono text-xs">
                {t.url} <span className="text-muted-foreground">(w={t.weight})</span>
              </p>
            ))}
            <p className="text-muted-foreground">
              {z.upstream.strategy} · bağlantı {z.upstream.connectTimeoutMs}ms · yanıt{' '}
              {z.upstream.responseTimeoutMs}ms
            </p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex-row items-center justify-between">
            <CardTitle className="text-sm">Challenge</CardTitle>
            <EditChallengeDialog
              zone={z}
              busy={busy}
              onSave={(body) => mutate(() => apiSend(`/v1/zones/${z.id}/challenge`, 'PUT', body))}
            />
          </CardHeader>
          <CardContent className="text-muted-foreground text-sm">
            {z.challenge.enabled
              ? `Aktif · zorluk ${z.challenge.difficulty} · token ${z.challenge.expirationSeconds}sn${z.challenge.requireCaptcha ? ' · CAPTCHA fallback' : ''}`
              : 'Devre dışı'}
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex-row items-center justify-between">
            <CardTitle className="text-sm">Önbellek</CardTitle>
            <Button
              size="sm"
              variant="outline"
              disabled={busy}
              onClick={() =>
                mutate(() =>
                  apiSend(`/v1/zones/${z.id}/cache`, 'PUT', { enabled: !z.cacheEnabled }),
                )
              }
            >
              {z.cacheEnabled ? 'Devre dışı bırak' : 'Etkinleştir'}
            </Button>
          </CardHeader>
          <CardContent className="text-muted-foreground text-sm">
            {z.cacheEnabled
              ? 'Aktif · yalnızca açıkça önbelleklenebilir GET yanıtları (RFC 7234)'
              : 'Devre dışı'}
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex-row items-center justify-between">
            <CardTitle className="text-sm">Managed WAF</CardTitle>
            <EditManagedRulesDialog
              zone={z}
              busy={busy}
              onSave={(body) => mutate(() => apiSend(`/v1/zones/${z.id}/managed-rules`, 'PUT', body))}
            />
          </CardHeader>
          <CardContent className="text-muted-foreground text-sm">
            {z.managedRules.sqlInjection || z.managedRules.xss || z.managedRules.pathTraversal
              ? `${[
                  z.managedRules.sqlInjection && 'SQLi',
                  z.managedRules.xss && 'XSS',
                  z.managedRules.pathTraversal && 'Path',
                ]
                  .filter(Boolean)
                  .join(', ')} → ${z.managedRules.action}${z.managedRules.inspectBody ? ' · gövde taranıyor' : ''}`
              : 'Devre dışı'}
          </CardContent>
        </Card>
      </div>

      <div className="space-y-2">
        <h2 className="text-sm font-semibold">Kurallar ({z.rules.length})</h2>
        {z.rules.length === 0 ? (
          <Card className="text-muted-foreground items-center py-10 text-center text-sm">
            Kural yok — tüm trafik upstream'e geçer.
          </Card>
        ) : (
          <Card className="overflow-hidden py-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Öncelik</TableHead>
                  <TableHead>Ad</TableHead>
                  <TableHead>Aksiyon</TableHead>
                  <TableHead>Koşullar</TableHead>
                  <TableHead>Durum</TableHead>
                  <TableHead className="text-right">İşlem</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(() => {
                  const ordered = z.rules.slice().sort((a, b) => a.priority - b.priority)
                  const reorder = (from: number, to: number) => {
                    const ids = ordered.map((r) => r.id)
                    const [moved] = ids.splice(from, 1)
                    ids.splice(to, 0, moved)
                    return mutate(() => apiSend(`/v1/zones/${z.id}/rules/order`, 'PUT', { ruleIds: ids }))
                  }
                  return ordered.map((rule, index) => (
                    <RuleRow
                      key={rule.id}
                      rule={rule}
                      busy={busy}
                      canMoveUp={index > 0}
                      canMoveDown={index < ordered.length - 1}
                      onMoveUp={() => reorder(index, index - 1)}
                      onMoveDown={() => reorder(index, index + 1)}
                      onToggle={() =>
                        mutate(() =>
                          apiSend(`/v1/zones/${z.id}/rules/${rule.id}`, 'PATCH', {
                            isEnabled: !rule.isEnabled,
                          }),
                        )
                      }
                      onSetPriority={(priority) =>
                        mutate(() =>
                          apiSend(`/v1/zones/${z.id}/rules/${rule.id}`, 'PATCH', { priority }),
                        )
                      }
                      onDelete={() => mutate(() => apiSend(`/v1/zones/${z.id}/rules/${rule.id}`, 'DELETE'))}
                    />
                  ))
                })()}
              </TableBody>
            </Table>
          </Card>
        )}
      </div>

      <AddRuleForm
        busy={busy}
        nextPriority={(z.rules.length === 0 ? 0 : Math.max(...z.rules.map((r) => r.priority))) + 10}
        onAdd={(body) => mutate(() => apiSend(`/v1/zones/${z.id}/rules`, 'POST', body))}
      />
    </div>
  )
}

const STRATEGIES = ['RoundRobin', 'LeastConnections', 'IpHash'] as const

function EditUpstreamDialog({
  zone,
  busy,
  onSave,
}: {
  zone: ZoneDetail
  busy: boolean
  onSave: (body: unknown) => void
}) {
  const [open, setOpen] = useState(false)
  const first = zone.upstream.targets[0]
  const [url, setUrl] = useState(first?.url ?? 'http://')
  const [strategy, setStrategy] = useState(zone.upstream.strategy)
  const [connectMs, setConnectMs] = useState(zone.upstream.connectTimeoutMs)
  const [responseMs, setResponseMs] = useState(zone.upstream.responseTimeoutMs)
  const [passHost, setPassHost] = useState(zone.upstream.passHostHeader)

  function submit(e: React.FormEvent) {
    e.preventDefault()
    onSave({
      targets: [{ url: url.trim(), weight: first?.weight ?? 1 }],
      strategy,
      connectTimeoutMs: connectMs,
      responseTimeoutMs: responseMs,
      passHostHeader: passHost,
    })
    setOpen(false)
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="sm">
          <Pencil className="size-3.5" /> Düzenle
        </Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Upstream düzenle</DialogTitle>
          <DialogDescription>
            Değişiklik kaydedilince edge node'lara push'lanır. (Tek hedef düzenlenir.)
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} className="space-y-4">
          <div className="space-y-1.5">
            <Label htmlFor="up-url">Hedef URL</Label>
            <Input
              id="up-url"
              required
              className="font-mono"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="up-strategy">Strateji</Label>
            <Select
              id="up-strategy"
              value={strategy}
              onChange={(e) => setStrategy(e.target.value)}
            >
              {STRATEGIES.map((s) => (
                <option key={s}>{s}</option>
              ))}
            </Select>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="up-connect">Bağlantı timeout (ms)</Label>
              <Input
                id="up-connect"
                type="number"
                min={100}
                value={connectMs}
                onChange={(e) => setConnectMs(Number(e.target.value))}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="up-response">Yanıt timeout (ms)</Label>
              <Input
                id="up-response"
                type="number"
                min={100}
                value={responseMs}
                onChange={(e) => setResponseMs(Number(e.target.value))}
              />
            </div>
          </div>
          <Label className="cursor-pointer">
            <input
              type="checkbox"
              className="accent-primary size-4"
              checked={passHost}
              onChange={(e) => setPassHost(e.target.checked)}
            />
            Orijinal Host header'ı upstream'e geçir
          </Label>
          <DialogFooter>
            <Button type="submit" disabled={busy}>
              Kaydet
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function EditChallengeDialog({
  zone,
  busy,
  onSave,
}: {
  zone: ZoneDetail
  busy: boolean
  onSave: (body: unknown) => void
}) {
  const [open, setOpen] = useState(false)
  const [enabled, setEnabled] = useState(zone.challenge.enabled)
  const [difficulty, setDifficulty] = useState(zone.challenge.difficulty)
  const [expiration, setExpiration] = useState(zone.challenge.expirationSeconds)
  const [requireCaptcha, setRequireCaptcha] = useState(zone.challenge.requireCaptcha)

  function submit(e: React.FormEvent) {
    e.preventDefault()
    onSave({
      enabled,
      difficulty,
      expirationSeconds: expiration,
      requireCaptcha,
    })
    setOpen(false)
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="sm">
          <Pencil className="size-3.5" /> Düzenle
        </Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Challenge düzenle</DialogTitle>
          <DialogDescription>Proof-of-Work challenge ayarları.</DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} className="space-y-4">
          <Label className="cursor-pointer">
            <input
              type="checkbox"
              className="accent-primary size-4"
              checked={enabled}
              onChange={(e) => setEnabled(e.target.checked)}
            />
            Challenge aktif
          </Label>
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="ch-diff">Zorluk (8-32)</Label>
              <Input
                id="ch-diff"
                type="number"
                min={8}
                max={32}
                value={difficulty}
                onChange={(e) => setDifficulty(Number(e.target.value))}
                disabled={!enabled}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ch-exp">Token TTL (sn)</Label>
              <Input
                id="ch-exp"
                type="number"
                min={1}
                value={expiration}
                onChange={(e) => setExpiration(Number(e.target.value))}
                disabled={!enabled}
              />
            </div>
          </div>
          <Label className="cursor-pointer">
            <input
              type="checkbox"
              className="accent-primary size-4"
              checked={requireCaptcha}
              onChange={(e) => setRequireCaptcha(e.target.checked)}
              disabled={!enabled}
            />
            CAPTCHA fallback
          </Label>
          <DialogFooter>
            <Button type="submit" disabled={busy}>
              Kaydet
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function EditManagedRulesDialog({
  zone,
  busy,
  onSave,
}: {
  zone: ZoneDetail
  busy: boolean
  onSave: (body: unknown) => void
}) {
  const m = zone.managedRules
  const [open, setOpen] = useState(false)
  const [sqli, setSqli] = useState(m.sqlInjection)
  const [xss, setXss] = useState(m.xss)
  const [path, setPath] = useState(m.pathTraversal)
  const [inspectBody, setInspectBody] = useState(m.inspectBody)
  const [action, setAction] = useState(m.action)

  function submit(e: React.FormEvent) {
    e.preventDefault()
    onSave({
      sqlInjection: sqli,
      xss,
      pathTraversal: path,
      inspectBody,
      action,
    })
    setOpen(false)
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="sm">
          <Pencil className="size-3.5" /> Düzenle
        </Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Managed WAF düzenle</DialogTitle>
          <DialogDescription>
            OWASP-CRS tarzı yerleşik imza aileleri. İstek satırı, sorgu, header ve (opsiyonel)
            gövde taranır.
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} className="space-y-3">
          <Label className="cursor-pointer">
            <input
              type="checkbox"
              className="accent-primary size-4"
              checked={sqli}
              onChange={(e) => setSqli(e.target.checked)}
            />
            SQL Injection
          </Label>
          <Label className="cursor-pointer">
            <input
              type="checkbox"
              className="accent-primary size-4"
              checked={xss}
              onChange={(e) => setXss(e.target.checked)}
            />
            XSS
          </Label>
          <Label className="cursor-pointer">
            <input
              type="checkbox"
              className="accent-primary size-4"
              checked={path}
              onChange={(e) => setPath(e.target.checked)}
            />
            Path Traversal
          </Label>
          <Label className="cursor-pointer">
            <input
              type="checkbox"
              className="accent-primary size-4"
              checked={inspectBody}
              onChange={(e) => setInspectBody(e.target.checked)}
            />
            İstek gövdesini de tara (256 KiB'a kadar)
          </Label>
          <div className="space-y-1.5">
            <Label htmlFor="mr-action">Eşleşme aksiyonu</Label>
            <select
              id="mr-action"
              className="border-input bg-background h-9 w-full rounded-md border px-3 text-sm"
              value={action}
              onChange={(e) => setAction(e.target.value)}
            >
              <option value="block">block</option>
              <option value="challenge">challenge</option>
            </select>
          </div>
          <DialogFooter>
            <Button type="submit" disabled={busy}>
              Kaydet
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

const CONDITION_TYPES = [
  'ip_match',
  'ip_range',
  'country',
  'asn',
  'path_match',
  'path_regex',
  'header',
  'user_agent',
  'ja3',
  'ja4',
] as const

interface ConditionDraft {
  type: string
  value: string
  headerName: string
}

function newCondition(): ConditionDraft {
  return { type: 'path_match', value: '', headerName: '' }
}

function AddRuleForm({
  busy,
  nextPriority,
  onAdd,
}: {
  busy: boolean
  nextPriority: number
  onAdd: (body: unknown) => void
}) {
  const [name, setName] = useState('')
  const [action, setAction] = useState('Block')
  const [conditions, setConditions] = useState<Array<ConditionDraft>>([newCondition()])
  const [requests, setRequests] = useState(60)
  const [windowSecs, setWindowSecs] = useState(60)

  function patch(index: number, change: Partial<ConditionDraft>) {
    setConditions((cs) => cs.map((c, i) => (i === index ? { ...c, ...change } : c)))
  }

  function submit(event: React.FormEvent) {
    event.preventDefault()
    const mapped = conditions.map((c) =>
      c.type === 'asn'
        ? { type: c.type, asn: Number(c.value) }
        : c.type === 'header'
          ? { type: c.type, name: c.headerName, value: c.value }
          : { type: c.type, value: c.value },
    )
    onAdd({
      name,
      priority: nextPriority,
      action,
      conditions: mapped,
      rateLimit: action === 'RateLimit' ? { requests, windowSecs } : null,
    })
    setName('')
    setConditions([newCondition()])
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Kural ekle</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="space-y-4">
          <div className="flex flex-wrap items-end gap-2">
            <Input
              required
              placeholder="Kural adı"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="w-48"
            />
            <Select value={action} onChange={(e) => setAction(e.target.value)} className="w-36">
              <option>Allow</option>
              <option>Block</option>
              <option>Challenge</option>
              <option>RateLimit</option>
            </Select>
            {action === 'RateLimit' && (
              <>
                <Input
                  type="number"
                  min={1}
                  value={requests}
                  onChange={(e) => setRequests(Number(e.target.value))}
                  className="w-20"
                  title="İstek"
                />
                <span className="text-muted-foreground text-sm">istek /</span>
                <Input
                  type="number"
                  min={1}
                  value={windowSecs}
                  onChange={(e) => setWindowSecs(Number(e.target.value))}
                  className="w-20"
                  title="Saniye"
                />
                <span className="text-muted-foreground text-sm">sn</span>
              </>
            )}
          </div>

          <div className="space-y-2">
            <p className="text-muted-foreground text-xs">Koşullar (hepsi sağlanmalı — AND)</p>
            {conditions.map((condition, index) => (
              <div key={index} className="flex flex-wrap items-center gap-2">
                {index > 0 && (
                  <span className="text-muted-foreground text-xs font-medium">VE</span>
                )}
                <Select
                  value={condition.type}
                  onChange={(e) => patch(index, { type: e.target.value })}
                  className="w-40"
                >
                  {CONDITION_TYPES.map((t) => (
                    <option key={t}>{t}</option>
                  ))}
                </Select>
                {condition.type === 'header' && (
                  <Input
                    required
                    placeholder="Header adı"
                    value={condition.headerName}
                    onChange={(e) => patch(index, { headerName: e.target.value })}
                    className="w-40"
                  />
                )}
                <Input
                  required
                  placeholder={condition.type === 'asn' ? 'ASN (ör. 64500)' : 'Değer'}
                  value={condition.value}
                  onChange={(e) => patch(index, { value: e.target.value })}
                  className="w-48 font-mono"
                />
                {conditions.length > 1 && (
                  <button
                    type="button"
                    onClick={() => setConditions((cs) => cs.filter((_, i) => i !== index))}
                    className="text-destructive text-xs hover:underline"
                    title="Koşulu kaldır"
                  >
                    kaldır
                  </button>
                )}
              </div>
            ))}
            <button
              type="button"
              onClick={() => setConditions((cs) => [...cs, newCondition()])}
              className="text-muted-foreground hover:text-foreground text-xs"
            >
              + koşul ekle
            </button>
          </div>

          <Button type="submit" disabled={busy}>
            Ekle
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}

function RuleRow({
  rule,
  busy,
  canMoveUp,
  canMoveDown,
  onMoveUp,
  onMoveDown,
  onToggle,
  onSetPriority,
  onDelete,
}: {
  rule: Rule
  busy: boolean
  canMoveUp: boolean
  canMoveDown: boolean
  onMoveUp: () => void
  onMoveDown: () => void
  onToggle: () => void
  onSetPriority: (priority: number) => void
  onDelete: () => void
}) {
  const [priority, setPriority] = useState(String(rule.priority))
  // Resync when the rule reloads (after a commit or reorder); never clobbers
  // mid-edit since rule.priority only changes once the refetch lands.
  useEffect(() => {
    setPriority(String(rule.priority))
  }, [rule.priority])

  function commitPriority() {
    const next = Number(priority)
    if (!Number.isFinite(next) || next === rule.priority) {
      setPriority(String(rule.priority))
      return
    }
    onSetPriority(next)
  }

  return (
    <TableRow>
      <TableCell>
        <div className="flex items-center gap-1.5">
          <input
            type="number"
            data-rule-priority={rule.id}
            value={priority}
            disabled={busy}
            onChange={(e) => setPriority(e.target.value)}
            onBlur={commitPriority}
            onKeyDown={(e) => {
              if (e.key === 'Enter') e.currentTarget.blur()
            }}
            title="Önceliği değiştir (küçük = önce değerlendirilir)"
            className="border-input bg-background focus-visible:border-ring focus-visible:ring-ring/40 h-7 w-14 rounded-md border px-2 text-sm tabular-nums outline-none focus-visible:ring-[3px] disabled:opacity-50"
          />
          <div className="flex flex-col">
            <button
              disabled={busy || !canMoveUp}
              onClick={onMoveUp}
              className="text-muted-foreground hover:text-foreground disabled:opacity-30"
              title="Yukarı taşı"
            >
              <ChevronUp className="size-3.5" />
            </button>
            <button
              disabled={busy || !canMoveDown}
              onClick={onMoveDown}
              className="text-muted-foreground hover:text-foreground disabled:opacity-30"
              title="Aşağı taşı"
            >
              <ChevronDown className="size-3.5" />
            </button>
          </div>
        </div>
      </TableCell>
      <TableCell className="font-medium">{rule.name}</TableCell>
      <TableCell>
        {rule.action}
        {rule.rateLimit ? (
          <span className="text-muted-foreground">
            {' '}
            ({rule.rateLimit.requests}/{rule.rateLimit.windowSecs}sn)
          </span>
        ) : (
          ''
        )}
      </TableCell>
      <TableCell className="text-muted-foreground font-mono text-xs">
        {rule.conditions
          .map((c) => `${c.type}${c.name ? `:${c.name}` : ''}=${c.value ?? c.asn ?? ''}`)
          .join(' AND ')}
      </TableCell>
      <TableCell>
        {rule.isEnabled ? (
          <span className="text-emerald-600 dark:text-emerald-400">Aktif</span>
        ) : (
          <span className="text-muted-foreground">Pasif</span>
        )}
      </TableCell>
      <TableCell className="text-right">
        <div className="flex items-center justify-end gap-2">
          <button
            disabled={busy}
            onClick={onToggle}
            className="text-muted-foreground hover:text-foreground text-xs disabled:opacity-50"
          >
            {rule.isEnabled ? 'kapat' : 'aç'}
          </button>
          <button
            disabled={busy}
            onClick={onDelete}
            className="text-destructive inline-flex items-center text-xs disabled:opacity-50"
            title="Sil"
          >
            <Trash2 className="size-3.5" />
          </button>
        </div>
      </TableCell>
    </TableRow>
  )
}
