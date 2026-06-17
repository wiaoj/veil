import { createFileRoute } from '@tanstack/react-router'
import { Check, Copy, Plus } from 'lucide-react'
import { useState } from 'react'
import type { ApiKeySummary, CreateApiKeyResponse, ListApiKeysResponse } from '#/lib/api'
import { apiSend } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'
import { PageHeader, PageState } from '@/components/PageState'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

export const Route = createFileRoute('/api-keys')({
  component: ApiKeysPage,
})

function ApiKeysPage() {
  const [bump, setBump] = useState(0)
  const keys = useApiData<ListApiKeysResponse>(`/v1/api-keys?_=${bump}`)
  const refresh = () => setBump((n) => n + 1)

  if (keys.loading) return <PageState>Yükleniyor…</PageState>
  if (keys.error) return <PageState>{keys.error}</PageState>

  const items = keys.data?.items ?? []

  return (
    <div className="space-y-6">
      <PageHeader
        title="API anahtarları"
        description="Yönetim API'sine makine erişimi"
        action={<CreateApiKeyDialog onCreated={refresh} />}
      />

      {items.length === 0 ? (
        <Card className="text-muted-foreground items-center py-12 text-center text-sm">
          Henüz API anahtarı yok.
        </Card>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Ad</TableHead>
                <TableHead>Scope'lar</TableHead>
                <TableHead>Durum</TableHead>
                <TableHead>Oluşturma</TableHead>
                <TableHead>Son kullanım</TableHead>
                <TableHead className="text-right">İşlem</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((key) => (
                <ApiKeyRow key={key.id} apiKey={key} onChanged={refresh} />
              ))}
            </TableBody>
          </Table>
        </Card>
      )}
    </div>
  )
}

function ApiKeyRow({ apiKey, onChanged }: { apiKey: ApiKeySummary; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)

  async function revoke() {
    if (!window.confirm(`"${apiKey.name}" anahtarı kalıcı olarak iptal edilsin mi?`)) return
    setBusy(true)
    try {
      await apiSend(`/v1/api-keys/${apiKey.id}`, 'DELETE')
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  return (
    <TableRow>
      <TableCell className="font-medium">{apiKey.name}</TableCell>
      <TableCell className="text-muted-foreground">
        {apiKey.scopes.length === 0 ? (
          <span className="text-muted-foreground">—</span>
        ) : (
          <div className="flex flex-wrap gap-1">
            {apiKey.scopes.map((s) => (
              <Badge key={s} variant="secondary" className="font-mono text-[11px]">
                {s}
              </Badge>
            ))}
          </div>
        )}
      </TableCell>
      <TableCell>
        <Badge variant={apiKey.isActive ? 'success' : 'secondary'}>
          {apiKey.isActive ? 'Aktif' : 'İptal'}
        </Badge>
      </TableCell>
      <TableCell className="text-muted-foreground">
        {new Date(apiKey.createdAt).toLocaleDateString()}
      </TableCell>
      <TableCell className="text-muted-foreground">
        {apiKey.lastUsedAt ? new Date(apiKey.lastUsedAt).toLocaleString() : '—'}
      </TableCell>
      <TableCell className="text-right">
        {apiKey.isActive && (
          <button
            disabled={busy}
            onClick={revoke}
            className="text-destructive text-xs hover:underline disabled:opacity-50"
          >
            iptal et
          </button>
        )}
      </TableCell>
    </TableRow>
  )
}

function CreateApiKeyDialog({ onCreated }: { onCreated: () => void }) {
  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [scopes, setScopes] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<CreateApiKeyResponse | null>(null)

  function reset() {
    setName('')
    setScopes('')
    setError(null)
    setResult(null)
    setBusy(false)
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const parsedScopes = scopes
        .split(',')
        .map((s) => s.trim())
        .filter((s) => s.length > 0)
      const res = await apiSend<CreateApiKeyResponse>('/v1/api-keys', 'POST', {
        name: name.trim(),
        scopes: parsedScopes,
      })
      // Refresh the list only when the dialog closes — refetching now would
      // flip the page into its loading state and unmount this key reveal.
      setResult(res)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Oluşturma başarısız.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        setOpen(o)
        if (!o) {
          if (result) onCreated()
          reset()
        }
      }}
    >
      <DialogTrigger asChild>
        <Button>
          <Plus className="size-4" /> Anahtar oluştur
        </Button>
      </DialogTrigger>
      <DialogContent>
        {result ? (
          <CreatedKeyView apiKey={result} onClose={() => setOpen(false)} />
        ) : (
          <>
            <DialogHeader>
              <DialogTitle>API anahtarı oluştur</DialogTitle>
              <DialogDescription>
                Anahtar yalnızca bir kez gösterilir; yalnızca SHA-256 özeti saklanır.
              </DialogDescription>
            </DialogHeader>
            <form onSubmit={submit} className="space-y-4">
              <div className="space-y-1.5">
                <Label htmlFor="key-name">Ad</Label>
                <Input
                  id="key-name"
                  required
                  placeholder="ci-deploy"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="key-scopes">Scope'lar (virgülle, opsiyonel)</Label>
                <Input
                  id="key-scopes"
                  className="font-mono"
                  placeholder="zones:read, zones:write"
                  value={scopes}
                  onChange={(e) => setScopes(e.target.value)}
                />
              </div>
              {error && <p className="text-destructive text-sm">{error}</p>}
              <DialogFooter>
                <Button type="submit" disabled={busy || name.trim().length === 0}>
                  {busy ? 'Oluşturuluyor…' : 'Oluştur'}
                </Button>
              </DialogFooter>
            </form>
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}

function CreatedKeyView({
  apiKey,
  onClose,
}: {
  apiKey: CreateApiKeyResponse
  onClose: () => void
}) {
  const [copied, setCopied] = useState(false)
  function copy() {
    void navigator.clipboard.writeText(apiKey.key).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }
  return (
    <>
      <DialogHeader>
        <DialogTitle>Anahtar oluşturuldu: {apiKey.name}</DialogTitle>
        <DialogDescription>
          Bu anahtarı şimdi kopyala — bir daha gösterilmeyecek.
        </DialogDescription>
      </DialogHeader>
      <div className="bg-muted relative rounded-md p-3 font-mono text-xs break-all">
        <pre className="whitespace-pre-wrap">{apiKey.key}</pre>
        <Button
          type="button"
          variant="ghost"
          size="icon"
          className="absolute top-1.5 right-1.5"
          onClick={copy}
          aria-label="Kopyala"
        >
          {copied ? <Check className="size-4" /> : <Copy className="size-4" />}
        </Button>
      </div>
      <DialogFooter>
        <Button onClick={onClose}>Tamam</Button>
      </DialogFooter>
    </>
  )
}
