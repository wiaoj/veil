import { createFileRoute } from '@tanstack/react-router'
import { useState } from 'react'
import type { Rule, ZoneDetail } from '#/lib/api'
import { apiSend } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'

export const Route = createFileRoute('/zones/$zoneId')({
  component: ZoneDetailPage,
})

function ZoneDetailPage() {
  const { zoneId } = Route.useParams()
  // bump forces a refetch after mutations
  const [bump, setBump] = useState(0)
  const zone = useApiData<ZoneDetail>(`/v1/zones/${zoneId}?_=${bump}`)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

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

  if (zone.loading) return <Centered>Yükleniyor…</Centered>
  if (zone.error) return <Centered>{zone.error}</Centered>
  if (!zone.data) return null

  const z = zone.data
  const paused = z.status === 'Paused'

  return (
    <main className="mx-auto max-w-5xl space-y-8 px-4 py-8">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold">{z.hostname}</h1>
          <p className="font-mono text-xs text-gray-500">{z.id} · {z.status}</p>
        </div>
        <button
          disabled={busy}
          onClick={() => mutate(() => apiSend(`/v1/zones/${z.id}/${paused ? 'resume' : 'pause'}`, 'POST'))}
          className="rounded-md border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:hover:bg-gray-900"
        >
          {paused ? 'Devam ettir' : 'Duraklat'}
        </button>
      </div>

      {error && <p className="text-sm text-red-600">{error}</p>}

      <div className="grid gap-4 md:grid-cols-2">
        <section className="rounded-lg border border-gray-200 p-4 text-sm dark:border-gray-800">
          <h2 className="mb-2 font-medium">Upstream</h2>
          {z.upstream.targets.map((t) => (
            <p key={t.url} className="font-mono text-xs">{t.url} (w={t.weight})</p>
          ))}
          <p className="mt-2 text-gray-500">
            {z.upstream.strategy} · bağlantı {z.upstream.connectTimeoutMs}ms · yanıt {z.upstream.responseTimeoutMs}ms
          </p>
        </section>
        <section className="rounded-lg border border-gray-200 p-4 text-sm dark:border-gray-800">
          <h2 className="mb-2 font-medium">Challenge</h2>
          <p className="text-gray-500">
            {z.challenge.enabled
              ? `Aktif · zorluk ${z.challenge.difficulty} · token ${z.challenge.expirationSeconds}sn${z.challenge.requireCaptcha ? ' · CAPTCHA fallback' : ''}`
              : 'Devre dışı'}
          </p>
        </section>
      </div>

      <section>
        <h2 className="mb-2 text-sm font-medium text-gray-500">Kurallar ({z.rules.length})</h2>
        {z.rules.length === 0 ? (
          <p className="text-sm text-gray-500">Kural yok — tüm trafik upstream'e geçer.</p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-200 text-left text-gray-500 dark:border-gray-800">
                <th className="py-2">Öncelik</th>
                <th>Ad</th>
                <th>Aksiyon</th>
                <th>Koşullar</th>
                <th>Durum</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {z.rules
                .slice()
                .sort((a, b) => a.priority - b.priority)
                .map((rule) => (
                  <RuleRow
                    key={rule.id}
                    rule={rule}
                    busy={busy}
                    onToggle={() =>
                      mutate(() =>
                        apiSend(`/v1/zones/${z.id}/rules/${rule.id}`, 'PUT', {
                          priority: rule.priority,
                          isEnabled: !rule.isEnabled,
                        }),
                      )
                    }
                    onDelete={() => mutate(() => apiSend(`/v1/zones/${z.id}/rules/${rule.id}`, 'DELETE'))}
                  />
                ))}
            </tbody>
          </table>
        )}
      </section>
    </main>
  )
}

function RuleRow({
  rule,
  busy,
  onToggle,
  onDelete,
}: {
  rule: Rule
  busy: boolean
  onToggle: () => void
  onDelete: () => void
}) {
  return (
    <tr className="border-b border-gray-100 dark:border-gray-900">
      <td className="py-2">{rule.priority}</td>
      <td className="font-medium">{rule.name}</td>
      <td>
        {rule.action}
        {rule.rateLimit ? ` (${rule.rateLimit.requests}/${rule.rateLimit.windowSecs}sn)` : ''}
      </td>
      <td className="font-mono text-xs text-gray-500">
        {rule.conditions.map((c) => `${c.type}${c.name ? `:${c.name}` : ''}=${c.value ?? c.asn ?? ''}`).join(' AND ')}
      </td>
      <td>{rule.isEnabled ? 'Aktif' : 'Pasif'}</td>
      <td className="space-x-2 text-right">
        <button disabled={busy} onClick={onToggle} className="text-xs underline disabled:opacity-50">
          {rule.isEnabled ? 'kapat' : 'aç'}
        </button>
        <button disabled={busy} onClick={onDelete} className="text-xs text-red-600 underline disabled:opacity-50">
          sil
        </button>
      </td>
    </tr>
  )
}

function Centered({ children }: { children: React.ReactNode }) {
  return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">{children}</main>
}
