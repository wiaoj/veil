import { Link, createFileRoute } from '@tanstack/react-router'
import { Check, Copy, MoreHorizontal, Plus, Power, PowerOff, Trash2, X } from 'lucide-react'
import { useState } from 'react'
import type { EdgeNodeSummary, ListEdgeNodesResponse } from '#/lib/api'
import { apiSend } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'
import { PageHeader, PageState } from '@/components/PageState'
import { StatusBadge } from '@/components/StatusBadge'
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
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
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

export const Route = createFileRoute('/nodes')({
  component: NodesPage,
})

function NodesPage() {
  const [bump, setBump] = useState(0)
  const nodes = useApiData<ListEdgeNodesResponse>(`/v1/edge-nodes?pageSize=100&_=${bump}`)

  if (nodes.loading) return <PageState>Yükleniyor…</PageState>
  if (nodes.error) return <PageState>{nodes.error}</PageState>

  const items = nodes.data?.items ?? []

  return (
    <div className="space-y-6">
      <PageHeader
        title="Edge node'lar"
        description={`${nodes.data?.totalCount ?? 0} kayıt`}
        action={<RegisterNodeDialog onRegistered={() => setBump((n) => n + 1)} />}
      />

      {items.length === 0 ? (
        <Card className="text-muted-foreground items-center py-12 text-center text-sm">
          Henüz kayıtlı edge node yok.
        </Card>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Ad</TableHead>
                <TableHead>Adres</TableHead>
                <TableHead>Durum</TableHead>
                <TableHead>Son push</TableHead>
                <TableHead>Son görülme</TableHead>
                <TableHead className="text-right">İşlem</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((node) => (
                <NodeRow key={node.id} node={node} onChanged={() => setBump((n) => n + 1)} />
              ))}
            </TableBody>
          </Table>
        </Card>
      )}
    </div>
  )
}

function NodeRow({ node, onChanged }: { node: EdgeNodeSummary; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)
  const isDisabled = node.status === 'Disabled'

  async function run(fn: () => Promise<unknown>) {
    setBusy(true)
    try {
      await fn()
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  function remove() {
    if (!window.confirm(`"${node.name}" node kaydı silinsin mi? Yeniden kaydedilmesi gerekir.`)) return
    void run(() => apiSend(`/v1/edge-nodes/${node.id}`, 'DELETE'))
  }

  return (
    <TableRow>
      <TableCell className="font-medium">
        <Link to="/nodes/$nodeId" params={{ nodeId: node.id }} className="hover:underline">
          {node.name}
        </Link>
      </TableCell>
      <TableCell className="text-muted-foreground font-mono text-xs">{node.address}</TableCell>
      <TableCell>
        <StatusBadge status={node.status} />
      </TableCell>
      <TableCell>
        {node.lastPushAtUtc === null ? (
          <span className="text-muted-foreground">—</span>
        ) : (
          <span
            className={`inline-flex items-center gap-1 ${
              node.lastPushSucceeded ? 'text-emerald-600 dark:text-emerald-400' : 'text-destructive'
            }`}
          >
            {node.lastPushSucceeded ? <Check className="size-3.5" /> : <X className="size-3.5" />}
            {new Date(node.lastPushAtUtc).toLocaleString()}
          </span>
        )}
      </TableCell>
      <TableCell className="text-muted-foreground">
        {node.lastSeenAtUtc ? new Date(node.lastSeenAtUtc).toLocaleString() : '—'}
      </TableCell>
      <TableCell className="text-right">
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" disabled={busy} aria-label="İşlemler">
              <MoreHorizontal className="size-4" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            {isDisabled ? (
              <DropdownMenuItem
                onClick={() => run(() => apiSend(`/v1/edge-nodes/${node.id}/enable`, 'POST'))}
              >
                <Power className="size-4" /> Etkinleştir
              </DropdownMenuItem>
            ) : (
              <DropdownMenuItem
                onClick={() => run(() => apiSend(`/v1/edge-nodes/${node.id}/disable`, 'POST'))}
              >
                <PowerOff className="size-4" /> Devre dışı bırak
              </DropdownMenuItem>
            )}
            <DropdownMenuSeparator />
            <DropdownMenuItem onClick={remove} className="text-destructive">
              <Trash2 className="size-4" /> Sil
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </TableCell>
    </TableRow>
  )
}

interface RegisterResult {
  id: string
  name: string
  address: string
  status: string
  token: string
}

function RegisterNodeDialog({ onRegistered }: { onRegistered: () => void }) {
  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [address, setAddress] = useState('http://127.0.0.1:8080')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<RegisterResult | null>(null)

  function reset() {
    setName('')
    setAddress('http://127.0.0.1:8080')
    setError(null)
    setResult(null)
    setBusy(false)
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const res = await apiSend<RegisterResult>('/v1/edge-nodes', 'POST', {
        name: name.trim(),
        address: address.trim(),
      })
      // Refresh the list only when the dialog closes — refetching now would
      // flip the page into its loading state and unmount this token reveal.
      setResult(res)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Kayıt başarısız.')
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
          if (result) onRegistered()
          reset()
        }
      }}
    >
      <DialogTrigger asChild>
        <Button>
          <Plus className="size-4" /> Node kaydet
        </Button>
      </DialogTrigger>
      <DialogContent>
        {result ? (
          <RegisteredView result={result} onClose={() => setOpen(false)} />
        ) : (
          <>
            <DialogHeader>
              <DialogTitle>Edge node kaydet</DialogTitle>
              <DialogDescription>
                Kayıt anında bir kimlik ve tek seferlik token üretilir. Token yalnızca bir kez
                gösterilir.
              </DialogDescription>
            </DialogHeader>
            <form onSubmit={submit} className="space-y-4">
              <div className="space-y-1.5">
                <Label htmlFor="node-name">Ad</Label>
                <Input
                  id="node-name"
                  required
                  placeholder="fra-1"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="node-address">Adres (config push hedefi)</Label>
                <Input
                  id="node-address"
                  required
                  className="font-mono"
                  placeholder="http://127.0.0.1:8080"
                  value={address}
                  onChange={(e) => setAddress(e.target.value)}
                />
              </div>
              {error && <p className="text-destructive text-sm">{error}</p>}
              <DialogFooter>
                <Button type="submit" disabled={busy || name.trim().length === 0}>
                  {busy ? 'Kaydediliyor…' : 'Kaydet'}
                </Button>
              </DialogFooter>
            </form>
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}

function RegisteredView({ result, onClose }: { result: RegisterResult; onClose: () => void }) {
  const envSnippet = `VEIL_NODE_ID=${result.id}\nVEIL_NODE_TOKEN=${result.token}`
  const [copied, setCopied] = useState(false)

  function copy() {
    void navigator.clipboard.writeText(envSnippet).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <>
      <DialogHeader>
        <DialogTitle>Node kaydedildi: {result.name}</DialogTitle>
        <DialogDescription>
          Aşağıdaki değerleri edge'in <code className="font-mono">.env</code> dosyasına ekle. Token
          bir daha gösterilmeyecek.
        </DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <div className="bg-muted relative rounded-md p-3 font-mono text-xs break-all">
          <pre className="whitespace-pre-wrap">{envSnippet}</pre>
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
        <p className="text-muted-foreground text-xs">
          Durum: <StatusBadge status={result.status} /> · Adres{' '}
          <span className="font-mono">{result.address}</span>
        </p>
      </div>
      <DialogFooter>
        <Button onClick={onClose}>Tamam</Button>
      </DialogFooter>
    </>
  )
}
