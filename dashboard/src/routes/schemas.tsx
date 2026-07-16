import { Link, createFileRoute, useNavigate } from '@tanstack/react-router'
import { FileJson, Plus, Upload } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import type {
  SchemaCompatibilityResponse,
  SchemaSubject,
  SchemaUsageResponse,
  SchemaVersion,
} from '#/lib/api'
import {
  ApiError,
  UnauthorizedError,
  apiGet,
  apiSend,
  hasSession,
  subjectLatestVersion,
  subjectName,
  versionId,
} from '#/lib/api'
import { PageHeader, PageState } from '@/components/PageState'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

export const Route = createFileRoute('/schemas')({
  component: SchemasPage,
})

/** Distinguishes "registry disabled" (503) from a transient/empty result. */
function isDisabled(err: unknown): boolean {
  return err instanceof ApiError && err.status === 503
}

function SchemasPage() {
  const navigate = useNavigate()
  const [subjects, setSubjects] = useState<Array<SchemaSubject> | null>(null)
  const [disabled, setDisabled] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [selected, setSelected] = useState<string | null>(null)
  const [bump, setBump] = useState(0)

  const reload = useCallback(() => setBump((n) => n + 1), [])

  useEffect(() => {
    if (!hasSession()) {
      navigate({ to: '/login' })
      return
    }
    let cancelled = false
    setLoading(true)
    apiGet<Array<SchemaSubject>>('/v1/schemas')
      .then((result) => {
        if (cancelled) return
        setDisabled(false)
        setSubjects(Array.isArray(result) ? result : [])
        setError(null)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (err instanceof UnauthorizedError) {
          navigate({ to: '/login' })
          return
        }
        if (isDisabled(err)) {
          setDisabled(true)
          setSubjects([])
          setError(null)
          return
        }
        // Vaultify unreachable degrades to an empty list, not an error.
        setSubjects([])
        setError(err instanceof Error ? err.message : 'İstek başarısız.')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [navigate, bump])

  if (loading) return <PageState>Yükleniyor…</PageState>

  return (
    <div className="space-y-6">
      <PageHeader
        title="Şemalar"
        description="Vaultify şema kayıt defteri. body_schema kuralları JSON gövdeyi bu şemalara karşı doğrular; her kural yalnızca {subject, version} referansını taşır."
        action={!disabled ? <UploadSchemaDialog onUploaded={reload} /> : undefined}
      />

      {disabled && (
        <Card className="text-muted-foreground items-center gap-2 py-12 text-center text-sm">
          <FileJson className="text-muted-foreground/60 size-8" />
          <div className="font-medium">Şema kayıt defteri yapılandırılmamış</div>
          <p className="max-w-md">
            Vaultify (<code className="font-mono">Vaultify:BaseUrl</code>) ayarlanmadığı için şema
            özelliği kapalı. Yapılandırıldığında subject'ler burada listelenir.
          </p>
        </Card>
      )}

      {!disabled && error && <p className="text-destructive text-sm">{error}</p>}

      {!disabled && (
        <div className="grid gap-6 lg:grid-cols-[280px_1fr]">
          <SubjectList
            subjects={subjects ?? []}
            selected={selected}
            onSelect={setSelected}
          />
          {selected ? (
            <SubjectDetail subject={selected} />
          ) : (
            <Card className="text-muted-foreground items-center py-12 text-center text-sm">
              {(subjects?.length ?? 0) === 0
                ? 'Henüz şema yok. Sağ üstten yeni bir şema yükleyin.'
                : 'Ayrıntıları görmek için bir subject seçin.'}
            </Card>
          )}
        </div>
      )}
    </div>
  )
}

function SubjectList({
  subjects,
  selected,
  onSelect,
}: {
  subjects: Array<SchemaSubject>
  selected: string | null
  onSelect: (subject: string) => void
}) {
  if (subjects.length === 0) {
    return (
      <Card className="text-muted-foreground items-center py-8 text-center text-xs">
        Subject yok.
      </Card>
    )
  }
  return (
    <Card className="overflow-hidden py-0">
      <ul className="divide-border divide-y">
        {subjects.map((s) => {
          const name = subjectName(s)
          const latest = subjectLatestVersion(s)
          const active = name === selected
          return (
            <li key={name}>
              <button
                onClick={() => onSelect(name)}
                className={`hover:bg-muted/50 flex w-full flex-col items-start gap-1 px-3 py-2.5 text-left transition-colors ${
                  active ? 'bg-primary/10' : ''
                }`}
              >
                <span
                  className={`font-mono text-sm ${active ? 'text-primary font-semibold' : ''}`}
                >
                  {name}
                </span>
                <span className="text-muted-foreground flex items-center gap-2 text-xs">
                  {latest && <span className="tabular-nums">v{latest}</span>}
                  {typeof s.type === 'string' && (
                    <Badge variant="secondary" className="text-[10px]">
                      {s.type}
                    </Badge>
                  )}
                </span>
              </button>
            </li>
          )
        })}
      </ul>
    </Card>
  )
}

function SubjectDetail({ subject }: { subject: string }) {
  const navigate = useNavigate()
  const [versions, setVersions] = useState<Array<SchemaVersion> | null>(null)
  const [selectedVersion, setSelectedVersion] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setVersions(null)
    setSelectedVersion(null)
    setError(null)
    apiGet<Array<SchemaVersion>>(`/v1/schemas/${encodeURIComponent(subject)}/versions`)
      .then((result) => {
        if (cancelled) return
        const list = Array.isArray(result) ? result : []
        setVersions(list)
        // Default to the newest version (last in the list).
        const last = list.length > 0 ? versionId(list[list.length - 1]) : null
        setSelectedVersion(last)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (err instanceof UnauthorizedError) {
          navigate({ to: '/login' })
          return
        }
        setError(err instanceof Error ? err.message : 'Sürümler yüklenemedi.')
      })
    return () => {
      cancelled = true
    }
  }, [subject, navigate])

  return (
    <div className="space-y-6">
      <div>
        <h2 className="font-mono text-lg font-semibold">{subject}</h2>
        {error && <p className="text-destructive mt-1 text-sm">{error}</p>}
      </div>

      <div className="grid gap-6 md:grid-cols-[180px_1fr]">
        <VersionTimeline
          versions={versions}
          selected={selectedVersion}
          onSelect={setSelectedVersion}
        />
        {selectedVersion ? (
          <VersionContent subject={subject} version={selectedVersion} />
        ) : (
          <Card className="text-muted-foreground items-center py-10 text-center text-xs">
            {versions === null ? 'Yükleniyor…' : 'Bu subject için sürüm yok.'}
          </Card>
        )}
      </div>

      <SchemaUsage subject={subject} />
    </div>
  )
}

function VersionTimeline({
  versions,
  selected,
  onSelect,
}: {
  versions: Array<SchemaVersion> | null
  selected: string | null
  onSelect: (version: string) => void
}) {
  if (versions === null) {
    return <div className="text-muted-foreground text-xs">Yükleniyor…</div>
  }
  if (versions.length === 0) {
    return <div className="text-muted-foreground text-xs">Sürüm yok.</div>
  }
  return (
    <div className="space-y-1">
      <div className="text-muted-foreground mb-1 text-xs font-medium">Sürümler</div>
      {[...versions].reverse().map((v) => {
        const id = versionId(v)
        const active = id === selected
        return (
          <button
            key={id}
            onClick={() => onSelect(id)}
            className={`flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-sm transition-colors ${
              active ? 'bg-primary/10 text-primary font-semibold' : 'hover:bg-muted/50'
            }`}
          >
            <span className="tabular-nums">v{id}</span>
          </button>
        )
      })}
    </div>
  )
}

function VersionContent({ subject, version }: { subject: string; version: string }) {
  const navigate = useNavigate()
  const [content, setContent] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setContent(null)
    setError(null)
    apiGet<unknown>(
      `/v1/schemas/${encodeURIComponent(subject)}/versions/${encodeURIComponent(version)}`,
    )
      .then((result) => {
        if (cancelled) return
        setContent(
          typeof result === 'string' ? result : JSON.stringify(result, null, 2),
        )
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (err instanceof UnauthorizedError) {
          navigate({ to: '/login' })
          return
        }
        setError(err instanceof Error ? err.message : 'İçerik yüklenemedi.')
      })
    return () => {
      cancelled = true
    }
  }, [subject, version, navigate])

  if (error) return <p className="text-destructive text-sm">{error}</p>
  if (content === null) {
    return <div className="text-muted-foreground text-xs">Yükleniyor…</div>
  }
  return (
    <pre className="bg-muted max-h-[420px] overflow-auto rounded-md p-3 font-mono text-xs whitespace-pre-wrap">
      {content}
    </pre>
  )
}

function SchemaUsage({ subject }: { subject: string }) {
  const navigate = useNavigate()
  const [usage, setUsage] = useState<SchemaUsageResponse | null>(null)

  useEffect(() => {
    let cancelled = false
    setUsage(null)
    apiGet<SchemaUsageResponse>(`/v1/schemas/${encodeURIComponent(subject)}/usage`)
      .then((result) => {
        if (!cancelled) setUsage(result)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (err instanceof UnauthorizedError) navigate({ to: '/login' })
        // Usage is best-effort; a failure just hides the panel.
      })
    return () => {
      cancelled = true
    }
  }, [subject, navigate])

  const items = usage?.items ?? []

  return (
    <Card>
      <CardContent className="space-y-3">
        <div className="text-sm font-medium">Bu şemayı kullanan kurallar</div>
        {usage === null ? (
          <p className="text-muted-foreground text-xs">Yükleniyor…</p>
        ) : items.length === 0 ? (
          <p className="text-muted-foreground text-xs">
            Hiçbir kural bu şemayı referans vermiyor.
          </p>
        ) : (
          <ul className="divide-border divide-y text-sm">
            {items.map((item) => (
              <li
                key={`${item.zoneId}:${item.ruleId}`}
                className="flex flex-wrap items-center justify-between gap-2 py-2"
              >
                <div className="flex flex-col">
                  <Link
                    to="/zones/$zoneId"
                    params={{ zoneId: item.zoneId }}
                    className="text-primary font-mono hover:underline"
                  >
                    {item.hostname}
                  </Link>
                  <span className="text-muted-foreground text-xs">{item.ruleName}</span>
                </div>
                <Badge variant="outline" className="font-mono text-[11px]">
                  v{item.versions}
                </Badge>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}

function UploadSchemaDialog({ onUploaded }: { onUploaded: () => void }) {
  const [open, setOpen] = useState(false)
  const [subject, setSubject] = useState('')
  const [version, setVersion] = useState('')
  const [content, setContent] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [warning, setWarning] = useState<string | null>(null)
  const [confirmIncompatible, setConfirmIncompatible] = useState(false)

  function reset() {
    setSubject('')
    setVersion('')
    setContent('')
    setError(null)
    setWarning(null)
    setConfirmIncompatible(false)
    setBusy(false)
  }

  function parseContent(): unknown | undefined {
    try {
      return JSON.parse(content)
    } catch {
      setError('İçerik geçerli JSON değil.')
      return undefined
    }
  }

  async function upload(parsed: unknown) {
    await apiSend('/v1/schemas', 'POST', {
      subject: subject.trim(),
      version: version.trim(),
      content: parsed,
    })
    onUploaded()
    setOpen(false)
    reset()
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setWarning(null)
    const parsed = parseContent()
    if (parsed === undefined) return

    setBusy(true)
    try {
      // Pre-check compatibility against the subject's existing versions. A null
      // `compatible` means the registry couldn't check (unreachable) — don't block.
      const check = await apiSend<SchemaCompatibilityResponse>(
        `/v1/schemas/${encodeURIComponent(subject.trim())}/compatibility`,
        'POST',
        { content: parsed },
      )
      if (check && check.compatible === false) {
        setWarning(
          check.detail
            ? `Uyumsuz: ${check.detail}`
            : 'Aday şema mevcut sürümlerle uyumsuz görünüyor.',
        )
        setConfirmIncompatible(true)
        setBusy(false)
        return
      }
      await upload(parsed)
    } catch (err) {
      setError(uploadErrorMessage(err))
      setBusy(false)
    }
  }

  async function forceUpload() {
    const parsed = parseContent()
    if (parsed === undefined) return
    setBusy(true)
    setError(null)
    try {
      await upload(parsed)
    } catch (err) {
      setError(uploadErrorMessage(err))
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        setOpen(o)
        if (!o) reset()
      }}
    >
      <DialogTrigger asChild>
        <Button>
          <Plus className="size-4" /> Şema yükle
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Şema yükle</DialogTitle>
          <DialogDescription>
            Yüklemeden önce Vaultify uyumluluğu denetlenir; şema geçerliyse kayıt defterine eklenir.
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} className="space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="schema-subject">Subject</Label>
              <Input
                id="schema-subject"
                required
                className="font-mono"
                placeholder="orders.create"
                value={subject}
                onChange={(e) => {
                  setSubject(e.target.value)
                  setConfirmIncompatible(false)
                  setWarning(null)
                }}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="schema-version">Sürüm</Label>
              <Input
                id="schema-version"
                required
                className="font-mono"
                placeholder="1.0.0"
                value={version}
                onChange={(e) => setVersion(e.target.value)}
              />
            </div>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="schema-content">İçerik (JSON Schema)</Label>
            <textarea
              id="schema-content"
              required
              spellCheck={false}
              className="border-input bg-background focus-visible:ring-ring min-h-[220px] w-full rounded-md border px-3 py-2 font-mono text-xs focus-visible:ring-1 focus-visible:outline-none"
              placeholder={'{\n  "type": "object",\n  "required": ["id"],\n  "properties": { "id": { "type": "string" } }\n}'}
              value={content}
              onChange={(e) => {
                setContent(e.target.value)
                setConfirmIncompatible(false)
                setWarning(null)
              }}
            />
          </div>

          {warning && <p className="text-amber-600 text-sm dark:text-amber-400">{warning}</p>}
          {error && <p className="text-destructive text-sm">{error}</p>}

          <div className="flex items-center justify-end gap-2">
            {confirmIncompatible ? (
              <Button
                type="button"
                variant="destructive"
                disabled={busy}
                onClick={() => void forceUpload()}
              >
                <Upload className="size-4" />
                {busy ? 'Yükleniyor…' : 'Yine de yükle'}
              </Button>
            ) : (
              <Button
                type="submit"
                disabled={
                  busy ||
                  subject.trim().length === 0 ||
                  version.trim().length === 0 ||
                  content.trim().length === 0
                }
              >
                {busy ? 'Denetleniyor…' : 'Denetle ve yükle'}
              </Button>
            )}
          </div>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function uploadErrorMessage(err: unknown): string {
  if (err instanceof ApiError && err.body && typeof err.body === 'object') {
    const body = err.body as { error?: string; detail?: string }
    return body.error ?? body.detail ?? `Yükleme başarısız (${err.status}).`
  }
  return err instanceof Error ? err.message : 'Yükleme başarısız.'
}
