import { createFileRoute } from '@tanstack/react-router'
import type {
  AnalyticsSummary,
  ChallengeStatsResponse,
  TopIpsResponse,
  VerdictBreakdownResponse,
} from '#/lib/api'
import { useApiData } from '#/lib/useApiData'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

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
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Genel bakış</h1>
        <p className="text-muted-foreground mt-1 text-sm">Son {s.windowHours} saat</p>
      </div>

      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <Stat label="İstek" value={s.total} />
        <Stat label="Engellenen" value={s.blocked} tone="text-destructive" />
        <Stat label="Challenge" value={s.challenged} tone="text-amber-600 dark:text-amber-400" />
        <Stat label="Tekil IP" value={s.uniqueIps} />
      </div>

      <Card>
        <CardHeader>
          <CardTitle>İstek hacmi</CardTitle>
        </CardHeader>
        <CardContent>
          {s.series.length === 0 ? (
            <p className="text-muted-foreground text-sm">Bu pencerede trafik yok.</p>
          ) : (
            <div className="flex h-32 items-end gap-1">
              {s.series.map((p) => (
                <div
                  key={p.bucket}
                  title={`${new Date(p.bucket).toLocaleString()} — ${p.total} istek, ${p.blocked} engel`}
                  className="bg-primary/70 hover:bg-primary flex-1 rounded-t transition-colors"
                  style={{ height: `${Math.max(4, (p.total / maxBucket) * 100)}%` }}
                />
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Verdict dağılımı</CardTitle>
          </CardHeader>
          <CardContent>
            <VerdictBreakdown data={verdicts.data} />
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Challenge istatistikleri</CardTitle>
          </CardHeader>
          <CardContent>
            <ChallengeStats data={challenges.data} />
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>En aktif IP'ler</CardTitle>
        </CardHeader>
        <CardContent>
          {(topIps.data?.items ?? []).length === 0 ? (
            <p className="text-muted-foreground text-sm">Bu pencerede veri yok.</p>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-muted-foreground border-b text-left">
                  <th className="pb-2 font-medium">IP</th>
                  <th className="pb-2 font-medium">İstek</th>
                  <th className="pb-2 font-medium">Engel</th>
                  <th className="pb-2 font-medium">Son görülme</th>
                </tr>
              </thead>
              <tbody>
                {(topIps.data?.items ?? []).map((ip) => (
                  <tr key={ip.clientIp} className="border-b last:border-0">
                    <td className="py-2.5 font-mono">{ip.clientIp}</td>
                    <td className="py-2.5 tabular-nums">{ip.total.toLocaleString()}</td>
                    <td className="py-2.5 tabular-nums">{ip.blocked.toLocaleString()}</td>
                    <td className="text-muted-foreground py-2.5">
                      {new Date(ip.lastSeenUtc).toLocaleString()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

const VERDICT_LABEL: Record<string, { label: string; bar: string }> = {
  allow: { label: 'İzin verildi', bar: 'bg-emerald-500' },
  block: { label: 'Engellendi', bar: 'bg-destructive' },
  challenge: { label: 'Challenge', bar: 'bg-amber-500' },
  challenge_pass: { label: 'Challenge geçti', bar: 'bg-sky-500' },
  rate_limit: { label: 'Rate limit', bar: 'bg-violet-500' },
}

function VerdictBreakdown({ data }: { data: VerdictBreakdownResponse | null }) {
  const items = data?.items ?? []
  if (items.length === 0) {
    return <p className="text-muted-foreground text-sm">Bu pencerede veri yok.</p>
  }
  const max = Math.max(1, ...items.map((v) => v.total))
  return (
    <div className="space-y-3">
      {items.map((v) => {
        const meta = VERDICT_LABEL[v.verdict] ?? { label: v.verdict, bar: 'bg-muted-foreground' }
        return (
          <div key={v.verdict} className="flex items-center gap-3 text-sm">
            <span className="text-muted-foreground w-32 shrink-0">{meta.label}</span>
            <div className="bg-muted h-2.5 flex-1 overflow-hidden rounded-full">
              <div
                className={`h-full rounded-full ${meta.bar}`}
                style={{ width: `${(v.total / max) * 100}%` }}
              />
            </div>
            <span className="w-16 shrink-0 text-right tabular-nums">{v.total.toLocaleString()}</span>
          </div>
        )
      })}
    </div>
  )
}

function ChallengeStats({ data }: { data: ChallengeStatsResponse | null }) {
  if (!data) {
    return <p className="text-muted-foreground text-sm">Bu pencerede veri yok.</p>
  }
  const ratePct = Math.round(data.passRate * 1000) / 10
  return (
    <div>
      <div className="grid grid-cols-3 gap-4 text-center">
        <div>
          <p className="text-muted-foreground text-sm">Sunulan</p>
          <p className="mt-1 text-2xl font-semibold tabular-nums text-amber-600 dark:text-amber-400">
            {data.issued.toLocaleString()}
          </p>
        </div>
        <div>
          <p className="text-muted-foreground text-sm">Geçen</p>
          <p className="mt-1 text-2xl font-semibold tabular-nums text-emerald-600 dark:text-emerald-400">
            {data.passed.toLocaleString()}
          </p>
        </div>
        <div>
          <p className="text-muted-foreground text-sm">Geçiş oranı</p>
          <p className="mt-1 text-2xl font-semibold tabular-nums">{ratePct}%</p>
        </div>
      </div>
      <div className="bg-muted mt-4 h-2 overflow-hidden rounded-full">
        <div className="h-full rounded-full bg-emerald-500" style={{ width: `${Math.min(100, ratePct)}%` }} />
      </div>
      <p className="text-muted-foreground mt-2 text-xs">Geçiş oranı = geçen / (sunulan + geçen)</p>
    </div>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: string }) {
  return (
    <Card className="gap-0 py-0">
      <CardContent className="px-5 py-4">
        <p className="text-muted-foreground text-sm">{label}</p>
        <p className={`mt-1 text-3xl font-semibold tabular-nums ${tone ?? ''}`}>
          {value.toLocaleString()}
        </p>
      </CardContent>
    </Card>
  )
}

function Centered({ children }: { children: React.ReactNode }) {
  return (
    <main className="text-muted-foreground flex min-h-[50vh] items-center justify-center">
      {children}
    </main>
  )
}
