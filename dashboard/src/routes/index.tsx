import { createFileRoute } from '@tanstack/react-router'
import type {
  AnalyticsSummary,
  ChallengeStatsResponse,
  TopIpsResponse,
  VerdictBreakdownResponse,
} from '#/lib/api'
import { useApiData } from '#/lib/useApiData'

export const Route = createFileRoute('/')({
  component: Overview,
})

function Overview() {
  const summary = useApiData<AnalyticsSummary>('/v1/analytics/summary?hours=24')
  const topIps = useApiData<TopIpsResponse>('/v1/analytics/top-ips?hours=24&limit=5')
  const verdicts = useApiData<VerdictBreakdownResponse>('/v1/analytics/verdicts?hours=24')
  const challenges = useApiData<ChallengeStatsResponse>('/v1/analytics/challenges?hours=24')

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

      <div className="grid gap-8 md:grid-cols-2">
        <section>
          <h2 className="mb-2 text-sm font-medium text-gray-500">Verdict dağılımı</h2>
          <VerdictBreakdown data={verdicts.data} />
        </section>

        <section>
          <h2 className="mb-2 text-sm font-medium text-gray-500">Challenge istatistikleri</h2>
          <ChallengeStats data={challenges.data} />
        </section>
      </div>

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

const VERDICT_LABEL: Record<string, { label: string; bar: string }> = {
  allow: { label: 'İzin verildi', bar: 'bg-emerald-500' },
  block: { label: 'Engellendi', bar: 'bg-red-500' },
  challenge: { label: 'Challenge', bar: 'bg-amber-500' },
  challenge_pass: { label: 'Challenge geçti', bar: 'bg-sky-500' },
  rate_limit: { label: 'Rate limit', bar: 'bg-purple-500' },
}

function VerdictBreakdown({ data }: { data: VerdictBreakdownResponse | null }) {
  const items = data?.items ?? []
  if (items.length === 0) {
    return <p className="text-sm text-gray-500">Bu pencerede veri yok.</p>
  }
  const max = Math.max(1, ...items.map((v) => v.total))
  return (
    <div className="space-y-2 rounded-lg border border-gray-200 p-4 dark:border-gray-800">
      {items.map((v) => {
        const meta = VERDICT_LABEL[v.verdict] ?? { label: v.verdict, bar: 'bg-gray-400' }
        return (
          <div key={v.verdict} className="flex items-center gap-3 text-sm">
            <span className="w-32 shrink-0 text-gray-600 dark:text-gray-300">{meta.label}</span>
            <div className="h-3 flex-1 overflow-hidden rounded bg-gray-100 dark:bg-gray-900">
              <div className={`h-full rounded ${meta.bar}`} style={{ width: `${(v.total / max) * 100}%` }} />
            </div>
            <span className="w-16 shrink-0 text-right tabular-nums text-gray-500">{v.total.toLocaleString()}</span>
          </div>
        )
      })}
    </div>
  )
}

function ChallengeStats({ data }: { data: ChallengeStatsResponse | null }) {
  if (!data) {
    return <p className="text-sm text-gray-500">Bu pencerede veri yok.</p>
  }
  const ratePct = Math.round(data.passRate * 1000) / 10
  return (
    <div className="rounded-lg border border-gray-200 p-4 dark:border-gray-800">
      <div className="grid grid-cols-3 gap-4 text-center">
        <div>
          <p className="text-sm text-gray-500">Sunulan</p>
          <p className="text-2xl font-semibold text-amber-600">{data.issued.toLocaleString()}</p>
        </div>
        <div>
          <p className="text-sm text-gray-500">Geçen</p>
          <p className="text-2xl font-semibold text-emerald-600">{data.passed.toLocaleString()}</p>
        </div>
        <div>
          <p className="text-sm text-gray-500">Geçiş oranı</p>
          <p className="text-2xl font-semibold">{ratePct}%</p>
        </div>
      </div>
      <div className="mt-4 h-2 overflow-hidden rounded bg-gray-100 dark:bg-gray-900">
        <div className="h-full rounded bg-emerald-500" style={{ width: `${Math.min(100, ratePct)}%` }} />
      </div>
      <p className="mt-1 text-xs text-gray-400">Geçiş oranı = geçen / (sunulan + geçen)</p>
    </div>
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
