import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useEffect, useRef, useState } from 'react'
import type { LiveLogEvent } from '#/lib/api'
import { UnauthorizedError, apiStream, hasSession } from '#/lib/api'

export const Route = createFileRoute('/live')({
  component: LivePage,
})

const MAX_ROWS = 100

const VERDICT_TONE: Record<string, string> = {
  allow: 'text-emerald-600',
  block: 'text-red-600',
  challenge: 'text-amber-600',
  challenge_pass: 'text-sky-600',
  rate_limited: 'text-purple-600',
  no_zone: 'text-gray-400',
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
    <main className="mx-auto max-w-5xl space-y-4 px-4 py-8">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">Canlı trafik</h1>
        <div className="flex items-center gap-3 text-sm">
          <span className="flex items-center gap-1.5 text-gray-500">
            <span
              className={`h-2 w-2 rounded-full ${connected ? 'bg-emerald-500' : 'bg-gray-400'}`}
            />
            {connected ? 'bağlı' : 'yeniden bağlanıyor…'}
          </span>
          <button
            onClick={() => setPaused((p) => !p)}
            className="rounded-md border border-gray-300 px-3 py-1 hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-900"
          >
            {paused ? 'Devam' : 'Duraklat'}
          </button>
        </div>
      </div>

      <p className="text-xs text-gray-400">
        Bağlandıktan sonra gelen istekler akar (son {MAX_ROWS} kayıt). Duraklatma akışı kesmez,
        sadece tabloyu dondurur.
      </p>

      {rows.length === 0 ? (
        <p className="text-sm text-gray-500">İstek bekleniyor…</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-200 text-left text-gray-500 dark:border-gray-800">
              <th className="py-2">Zaman</th>
              <th>Zone</th>
              <th>Metot</th>
              <th>Yol</th>
              <th>Durum</th>
              <th>Verdict</th>
              <th>IP</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={`${r.tsMs}-${i}`} className="border-b border-gray-100 dark:border-gray-900">
                <td className="py-1.5 tabular-nums text-gray-500">
                  {new Date(r.tsMs).toLocaleTimeString()}
                </td>
                <td>{r.zone}</td>
                <td className="font-mono text-xs">{r.method}</td>
                <td className="max-w-xs truncate font-mono text-xs" title={r.path}>
                  {r.path}
                </td>
                <td className="tabular-nums">{r.status}</td>
                <td className={VERDICT_TONE[r.verdict] ?? ''}>{r.verdict}</td>
                <td className="font-mono text-xs text-gray-500">{r.clientIp}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  )
}
