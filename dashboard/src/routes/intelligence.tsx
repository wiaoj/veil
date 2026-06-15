import { createFileRoute } from '@tanstack/react-router'
import type { TrafficIncident } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'

export const Route = createFileRoute('/intelligence')({
  component: IntelligencePage,
})

const ACTION_TONE: Record<string, string> = {
  Enforced: 'text-red-600',
  Shadowed: 'text-amber-600',
  Suggested: 'text-sky-600',
  None: 'text-gray-400',
}

function scoreTone(score: number): string {
  if (score >= 80) return 'text-red-600'
  if (score >= 60) return 'text-amber-600'
  return 'text-gray-500'
}

function IncidentCard({ incident }: { incident: TrafficIncident }) {
  const rule = incident.verdict?.suggestedRule ?? incident.suggestedRule
  return (
    <article className="space-y-3 rounded-xl border border-[var(--line)] bg-[var(--chip-bg)] p-4">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div className="flex items-baseline gap-3">
          <span className="font-mono text-sm font-semibold">{incident.zone}</span>
          <span className="rounded-full border border-[var(--chip-line)] px-2 py-0.5 text-xs">
            {incident.verdict?.classification ?? incident.classification}
          </span>
        </div>
        <div className="flex items-center gap-3 text-xs">
          <span className={`font-semibold ${scoreTone(incident.anomalyScore)}`}>
            skor {incident.anomalyScore}
          </span>
          <span className={ACTION_TONE[incident.action] ?? ''}>{incident.action}</span>
          <span className="text-gray-400">
            {new Date(incident.detectedAtUtc).toLocaleTimeString()}
          </span>
        </div>
      </div>

      <div className="flex flex-wrap gap-1.5">
        {incident.signals.map((s) => (
          <span
            key={s}
            className="rounded-md bg-[var(--link-bg-hover)] px-2 py-0.5 font-mono text-[11px] text-[var(--sea-ink-soft)]"
          >
            {s}
          </span>
        ))}
      </div>

      {incident.verdict?.summary && (
        <p className="text-sm text-[var(--sea-ink-soft)]">{incident.verdict.summary}</p>
      )}

      <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs text-gray-500 sm:grid-cols-4">
        <span>
          hız <b className="tabular-nums">{incident.ratePerSecond.toFixed(0)}/s</b> (~
          {incident.baselineRatePerSecond.toFixed(0)}/s)
        </span>
        <span>
          blok oranı <b className="tabular-nums">{(incident.blockedRatio * 100).toFixed(0)}%</b>
        </span>
        <span>
          farklı IP <b className="tabular-nums">{incident.distinctIps}</b>
        </span>
        <span className="truncate" title={incident.topIps.map((t) => t.value).join(', ')}>
          en çok IP <b className="font-mono">{incident.topIps[0]?.value ?? '—'}</b>
        </span>
      </div>

      {rule && (
        <div className="rounded-md border border-dashed border-[var(--chip-line)] px-3 py-2 text-xs">
          <span className="text-gray-500">önerilen kural: </span>
          <span className="font-mono">
            {rule.conditionType} = {rule.value} → {rule.action}
          </span>
        </div>
      )}
    </article>
  )
}

function IntelligencePage() {
  const { data, error, loading } = useApiData<Array<TrafficIncident>>(
    '/v1/intelligence/incidents?limit=50',
  )

  return (
    <main className="mx-auto max-w-4xl space-y-4 px-4 py-8">
      <div>
        <h1 className="text-2xl font-semibold">Yapay zeka analizi</h1>
        <p className="mt-1 text-sm text-gray-500">
          ML.NET tabanlı canlı anomali tespiti. Her olay bellekte saptanır; yüksek güvenli
          öneriler enforce, diğerleri shadow modunda denenir.
        </p>
      </div>

      {loading && <p className="text-sm text-gray-500">Yükleniyor…</p>}
      {error && <p className="text-sm text-red-600">{error}</p>}
      {!loading && !error && (!data || data.length === 0) && (
        <p className="text-sm text-gray-500">
          Henüz anomali yok. (Intelligence katmanı kapalıysa bu liste boş kalır.)
        </p>
      )}

      <div className="space-y-3">
        {data?.map((incident) => (
          <IncidentCard key={incident.id} incident={incident} />
        ))}
      </div>
    </main>
  )
}
