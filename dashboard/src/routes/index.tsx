import { createFileRoute } from '@tanstack/react-router'
import type { AnalyticsSummary, TopIpsResponse } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'

export const Route = createFileRoute('/')({
  component: Overview,
})

function Overview() {
  const summary = useApiData<AnalyticsSummary>('/v1/analytics/summary?hours=24')
  const topIps = useApiData<TopIpsResponse>('/v1/analytics/top-ips?hours=24&limit=5')

  if (summary.loading) return <Centered>Yükleniyor…</Centered>
  if (summary.error) return <Centered>{summary.error}</Centered>
  if (!summary.data) return null

  const s = summary.data
  const maxBucket = Math.max(1, ...s.series.map((p) => p.total))

  return (
    <main className="mx-auto max-w-5xl space-y-8 px-4 py-8">
      <h1 className="text-2xl font-semibold">Genel bakış (son {s.windowHours} saat)</h1>

      <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Stat label="İstek" value={s.total} />
        <Stat label="Engellenen" value={s.blocked} tone="text-red-600" />
        <Stat label="Challenge" value={s.challenged} tone="text-amber-600" />
        <Stat label="Tekil IP" value={s.uniqueIps} />
      </div>

      <section>
        <h2 className="mb-2 text-sm font-medium text-gray-500">İstek hacmi</h2>
        {s.series.length === 0 ? (
          <p className="text-sm text-gray-500">Bu pencerede trafik yok.</p>
        ) : (
          <div className="flex h-32 items-end gap-1 rounded-lg border border-gray-200 p-3 dark:border-gray-800">
            {s.series.map((p) => (
              <div
                key={p.bucket}
                title={`${new Date(p.bucket).toLocaleString()} — ${p.total} istek, ${p.blocked} engel`}
                className="flex-1 rounded-t bg-gray-700 dark:bg-gray-300"
                style={{ height: `${Math.max(4, (p.total / maxBucket) * 100)}%` }}
              />
            ))}
          </div>
        )}
      </section>

      <section>
        <h2 className="mb-2 text-sm font-medium text-gray-500">En aktif IP'ler</h2>
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-200 text-left text-gray-500 dark:border-gray-800">
              <th className="py-2">IP</th>
              <th>İstek</th>
              <th>Engel</th>
              <th>Son görülme</th>
            </tr>
          </thead>
          <tbody>
            {(topIps.data?.items ?? []).map((ip) => (
              <tr key={ip.clientIp} className="border-b border-gray-100 dark:border-gray-900">
                <td className="py-2 font-mono">{ip.clientIp}</td>
                <td>{ip.total}</td>
                <td>{ip.blocked}</td>
                <td>{new Date(ip.lastSeenUtc).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </main>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: string }) {
  return (
    <div className="rounded-lg border border-gray-200 p-4 dark:border-gray-800">
      <p className="text-sm text-gray-500">{label}</p>
      <p className={`text-2xl font-semibold ${tone ?? ''}`}>{value.toLocaleString()}</p>
    </div>
  )
}

function Centered({ children }: { children: React.ReactNode }) {
  return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">{children}</main>
}
