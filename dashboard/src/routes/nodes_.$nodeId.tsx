import { Link, createFileRoute } from '@tanstack/react-router'
import { ArrowLeft } from 'lucide-react'
import { useState } from 'react'
import type { ConfigPushLogResponse } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'
import { PageState } from '@/components/PageState'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

export const Route = createFileRoute('/nodes_/$nodeId')({
  component: NodePushLogPage,
})

const PAGE_SIZE = 20

function NodePushLogPage() {
  const { nodeId } = Route.useParams()
  const [page, setPage] = useState(1)
  const log = useApiData<ConfigPushLogResponse>(
    `/v1/edge-nodes/${nodeId}/push-log?page=${page}&pageSize=${PAGE_SIZE}`,
  )

  if (log.loading) return <PageState>Yükleniyor…</PageState>
  if (log.error) return <PageState>{log.error}</PageState>
  if (!log.data) return null

  const { items, totalCount } = log.data
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE))

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <Link
            to="/nodes"
            className="text-muted-foreground hover:text-foreground mb-2 inline-flex items-center gap-1 text-sm no-underline"
          >
            <ArrowLeft className="size-4" /> Edge node'lar
          </Link>
          <h1 className="text-2xl font-semibold tracking-tight">Config push geçmişi</h1>
          <p className="text-muted-foreground mt-1 font-mono text-xs">{nodeId}</p>
        </div>
        <span className="text-muted-foreground text-sm">{totalCount} kayıt</span>
      </div>

      {items.length === 0 ? (
        <Card className="text-muted-foreground items-center py-12 text-center text-sm">
          Bu node'a henüz config push yapılmamış.
        </Card>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Zaman</TableHead>
                <TableHead>Sonuç</TableHead>
                <TableHead>Hata</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((entry, i) => (
                <TableRow key={`${entry.pushedAtUtc}-${i}`}>
                  <TableCell>{new Date(entry.pushedAtUtc).toLocaleString()}</TableCell>
                  <TableCell>
                    <Badge variant={entry.succeeded ? 'success' : 'destructive'}>
                      {entry.succeeded ? 'Başarılı' : 'Başarısız'}
                    </Badge>
                  </TableCell>
                  <TableCell
                    className="text-muted-foreground max-w-md truncate"
                    title={entry.error ?? undefined}
                  >
                    {entry.error ?? '—'}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      )}

      {totalPages > 1 && (
        <div className="flex items-center gap-3 text-sm">
          <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
            ← Önceki
          </Button>
          <span className="text-muted-foreground">
            Sayfa {page} / {totalPages}
          </span>
          <Button
            variant="outline"
            size="sm"
            disabled={page >= totalPages}
            onClick={() => setPage((p) => p + 1)}
          >
            Sonraki →
          </Button>
        </div>
      )}
    </div>
  )
}
