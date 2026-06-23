import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useEffect, useState } from 'react'
import type { TrafficIncident } from '#/lib/api'
import { UnauthorizedError, apiGet, applyAiRule, hasSession } from '#/lib/api'
import { PageHeader, PageState } from '@/components/PageState'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'

export const Route = createFileRoute('/intelligence')({
  component: IntelligencePage,
})

const REFRESH_MS = 10_000

const ACTION_TONE: Record<string, string> = {
  Enforced: 'text-destructive',
  Shadowed: 'text-amber-600 dark:text-amber-400',
  Suggested: 'text-sky-600 dark:text-sky-400',
  None: 'text-muted-foreground',
}

function scoreTone(score: number): string {
  if (score >= 80) return 'text-destructive'
  if (score >= 60) return 'text-amber-600 dark:text-amber-400'
  return 'text-muted-foreground'
}

function IncidentCard({ incident }: { incident: TrafficIncident }) {
  const rule = incident.verdict?.suggestedRule ?? incident.suggestedRule
  const [applying, setApplying] = useState<false | 'enforce' | 'shadow'>(false)
  const [outcome, setOutcome] = useState<string | null>(null)

  async function apply(shadow: boolean) {
    if (!rule) return
    setApplying(shadow ? 'shadow' : 'enforce')
    setOutcome(null)
    try {
      const res = await applyAiRule(incident.zone, rule, shadow)
      setOutcome(
        res.applied
          ? `Uygulandı → ${res.action}`
          : `Uygulanamadı: ${res.reason ?? 'bilinmeyen sebep'}`,
      )
    } catch (err) {
      setOutcome(err instanceof Error ? err.message : 'İstek başarısız.')
    } finally {
      setApplying(false)
    }
  }

  return (
    <Card>
      <CardContent className="space-y-3">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <div className="flex items-baseline gap-3">
            <span className="font-mono text-sm font-semibold">{incident.zone}</span>
            <Badge variant="outline">
              {incident.verdict?.classification ?? incident.classification}
            </Badge>
          </div>
          <div className="flex items-center gap-3 text-xs">
            <span className={`font-semibold ${scoreTone(incident.anomalyScore)}`}>
              skor {incident.anomalyScore}
            </span>
            <span className={`font-medium ${ACTION_TONE[incident.action] ?? ''}`}>
              {incident.action}
            </span>
            <span className="text-muted-foreground">
              {new Date(incident.detectedAtUtc).toLocaleTimeString()}
            </span>
          </div>
        </div>

        <div className="flex flex-wrap gap-1.5">
          {incident.signals.map((s) => (
            <span
              key={s}
              className="bg-muted text-muted-foreground rounded-md px-2 py-0.5 font-mono text-[11px]"
            >
              {s}
            </span>
          ))}
        </div>

        {incident.verdict?.summary && (
          <p className="text-muted-foreground text-sm">{incident.verdict.summary}</p>
        )}

        <div className="text-muted-foreground grid grid-cols-2 gap-x-6 gap-y-1 text-xs sm:grid-cols-4">
          <span>
            hız{' '}
            <b className="text-foreground tabular-nums">{incident.ratePerSecond.toFixed(0)}/s</b> (~
            {incident.baselineRatePerSecond.toFixed(0)}/s)
          </span>
          <span>
            blok oranı{' '}
            <b className="text-foreground tabular-nums">
              {(incident.blockedRatio * 100).toFixed(0)}%
            </b>
          </span>
          <span>
            farklı IP <b className="text-foreground tabular-nums">{incident.distinctIps}</b>
          </span>
          <span className="truncate" title={incident.topIps.map((t) => t.value).join(', ')}>
            en çok IP <b className="text-foreground font-mono">{incident.topIps[0]?.value ?? '—'}</b>
          </span>
        </div>

        {rule && (
          <div className="border-border space-y-2 rounded-md border border-dashed px-3 py-2 text-xs">
            <div className="text-muted-foreground">
              önerilen kural:{' '}
              <span className="text-foreground font-mono">
                {rule.conditionType} = {rule.value} → {rule.action}
              </span>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <Button
                size="sm"
                variant="destructive"
                disabled={applying !== false}
                onClick={() => void apply(false)}
              >
                {applying === 'enforce' ? 'Uygulanıyor…' : 'Uygula'}
              </Button>
              <Button
                size="sm"
                variant="outline"
                disabled={applying !== false}
                onClick={() => void apply(true)}
              >
                {applying === 'shadow' ? 'Ekleniyor…' : 'Shadow'}
              </Button>
              {outcome && <span className="text-muted-foreground">{outcome}</span>}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function IntelligencePage() {
  const navigate = useNavigate()
  const [data, setData] = useState<Array<TrafficIncident> | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    if (!hasSession()) {
      navigate({ to: '/login' })
      return
    }
    let cancelled = false

    async function poll() {
      try {
        const result = await apiGet<Array<TrafficIncident>>('/v1/intelligence/incidents?limit=50')
        if (cancelled) return
        setData(result)
        setError(null)
      } catch (err) {
        if (cancelled) return
        if (err instanceof UnauthorizedError) {
          navigate({ to: '/login' })
          return
        }
        setError(err instanceof Error ? err.message : 'İstek başarısız.')
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void poll()
    const timer = setInterval(poll, REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [navigate])

  if (loading) return <PageState>Yükleniyor…</PageState>

  return (
    <div className="mx-auto max-w-4xl space-y-4">
      <PageHeader
        title="Yapay zeka analizi"
        description={`ML.NET tabanlı canlı anomali tespiti (${REFRESH_MS / 1000}s'de bir yenilenir). Yüksek güvenli öneriler otomatik enforce, diğerleri shadow olur; bir öneriyi elle de uygulayabilirsiniz.`}
      />

      {error && <p className="text-destructive text-sm">{error}</p>}
      {!error && (!data || data.length === 0) && (
        <Card className="text-muted-foreground items-center py-12 text-center text-sm">
          Henüz anomali yok. (Intelligence katmanı kapalıysa bu liste boş kalır.)
        </Card>
      )}

      <div className="space-y-3">
        {data?.map((incident) => (
          <IncidentCard key={incident.id} incident={incident} />
        ))}
      </div>
    </div>
  )
}
