import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useEffect, useRef, useState } from 'react'
import type { LiveLogEvent } from '#/lib/api'
import { UnauthorizedError, apiStream, hasSession } from '#/lib/api'
import { PageHeader } from '@/components/PageState'
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

export const Route = createFileRoute('/live')({
  component: LivePage,
})

const MAX_ROWS = 100

const VERDICT_TONE: Record<string, string> = {
  allow: 'text-emerald-600 dark:text-emerald-400',
  block: 'text-destructive',
  challenge: 'text-amber-600 dark:text-amber-400',
  challenge_pass: 'text-sky-600 dark:text-sky-400',
  rate_limited: 'text-violet-600 dark:text-violet-400',
  no_zone: 'text-muted-foreground',
}

function LivePage() {
  const navigate = useNavigate()
  const [rows, setRows] = useState<Array<LiveLogEvent>>([])
  const [connected, setConnected] = useState(false)
  const [paused, setPaused] = useState(false)
  const pausedRef = useRef(false)
  pausedRef.current = paused

  useEffect(() => {
    if (!hasSession()) {
      navigate({ to: '/login' })
      return
    }
    const controller = new AbortController()
    let stopped = false

    async function run() {
      // Reconnect loop: SSE ends (idle proxy, redeploy) → reopen.
      while (!stopped) {
        setConnected(true)
        try {
          await apiStream<LiveLogEvent>(
            '/v1/analytics/stream',
            (event) => {
              if (pausedRef.current) return
              setRows((prev) => [event, ...prev].slice(0, MAX_ROWS))
            },
            controller.signal,
          )
        } catch (err) {
          if (err instanceof UnauthorizedError) {
            navigate({ to: '/login' })
            return
          }
        }
        setConnected(false)
        if (stopped) return
        await new Promise((r) => setTimeout(r, 2000))
      }
    }
    void run()

    return () => {
      stopped = true
      controller.abort()
    }
  }, [navigate])

  return (
    <div className="space-y-4">
      <PageHeader
        title="Canlı trafik"
        description={`Bağlandıktan sonra gelen istekler akar (son ${MAX_ROWS} kayıt). Duraklatma akışı kesmez, sadece tabloyu dondurur.`}
        action={
          <div className="flex items-center gap-3 text-sm">
            <span className="text-muted-foreground flex items-center gap-1.5">
              <span
                className={`h-2 w-2 rounded-full ${connected ? 'bg-emerald-500' : 'bg-muted-foreground/50'}`}
              />
              {connected ? 'bağlı' : 'yeniden bağlanıyor…'}
            </span>
            <Button variant="outline" size="sm" onClick={() => setPaused((p) => !p)}>
              {paused ? 'Devam' : 'Duraklat'}
            </Button>
          </div>
        }
      />

      {rows.length === 0 ? (
        <Card className="text-muted-foreground items-center py-12 text-center text-sm">
          İstek bekleniyor…
        </Card>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Zaman</TableHead>
                <TableHead>Zone</TableHead>
                <TableHead>Metot</TableHead>
                <TableHead>Yol</TableHead>
                <TableHead>Durum</TableHead>
                <TableHead>Verdict</TableHead>
                <TableHead>IP</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((r, i) => (
                <TableRow key={`${r.tsMs}-${i}`}>
                  <TableCell className="text-muted-foreground tabular-nums">
                    {new Date(r.tsMs).toLocaleTimeString()}
                  </TableCell>
                  <TableCell>{r.zone}</TableCell>
                  <TableCell className="font-mono text-xs">{r.method}</TableCell>
                  <TableCell className="max-w-xs truncate font-mono text-xs" title={r.path}>
                    {r.path}
                  </TableCell>
                  <TableCell className="tabular-nums">{r.status}</TableCell>
                  <TableCell className={`font-medium ${VERDICT_TONE[r.verdict] ?? ''}`}>
                    {r.verdict}
                  </TableCell>
                  <TableCell className="text-muted-foreground font-mono text-xs">
                    {r.clientIp}
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
