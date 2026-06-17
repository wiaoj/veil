import { Link, createFileRoute } from '@tanstack/react-router'
import { Plus } from 'lucide-react'
import type { ListZonesResponse } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'
import { PageHeader, PageState } from '@/components/PageState'
import { StatusBadge } from '@/components/StatusBadge'
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

export const Route = createFileRoute('/zones')({
  component: ZonesPage,
})

function ZonesPage() {
  const zones = useApiData<ListZonesResponse>('/v1/zones?pageSize=100')

  if (zones.loading) return <PageState>Yükleniyor…</PageState>
  if (zones.error) return <PageState>{zones.error}</PageState>

  const items = zones.data?.items ?? []

  return (
    <div className="space-y-6">
      <PageHeader
        title="Zone'lar"
        description={`${zones.data?.totalCount ?? 0} kayıt`}
        action={
          <Button asChild>
            <Link to="/zones/new">
              <Plus className="size-4" /> Yeni zone
            </Link>
          </Button>
        }
      />

      {items.length === 0 ? (
        <Card className="text-muted-foreground items-center py-12 text-center text-sm">
          Henüz zone yok.
        </Card>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Hostname</TableHead>
                <TableHead>Durum</TableHead>
                <TableHead>Kural</TableHead>
                <TableHead className="text-right">Id</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((zone) => (
                <TableRow key={zone.id}>
                  <TableCell className="font-medium">
                    <Link
                      to="/zones/$zoneId"
                      params={{ zoneId: zone.id }}
                      className="hover:underline"
                    >
                      {zone.hostname}
                    </Link>
                  </TableCell>
                  <TableCell>
                    <StatusBadge status={zone.status} />
                  </TableCell>
                  <TableCell className="tabular-nums">{zone.ruleCount}</TableCell>
                  <TableCell className="text-muted-foreground text-right font-mono text-xs">
                    {zone.id}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      )}
    </div>
  )
}
