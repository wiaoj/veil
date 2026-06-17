import { Link, createFileRoute, useNavigate } from '@tanstack/react-router'
import { ArrowLeft } from 'lucide-react'
import { useState } from 'react'
import { apiSend } from '#/lib/api'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'

export const Route = createFileRoute('/zones_/new')({
  component: NewZonePage,
})

function NewZonePage() {
  const navigate = useNavigate()
  const [hostname, setHostname] = useState('')
  const [upstreamUrl, setUpstreamUrl] = useState('http://')
  const [challengeEnabled, setChallengeEnabled] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const created = await apiSend<{ id: string }>('/v1/zones', 'POST', {
        hostname: hostname.trim(),
        upstream: { targets: [{ url: upstreamUrl.trim(), weight: 1 }] },
        challenge: challengeEnabled ? { enabled: true } : { enabled: false },
      })
      navigate({ to: '/zones/$zoneId', params: { zoneId: created!.id } })
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Zone oluşturulamadı.')
      setBusy(false)
    }
  }

  return (
    <div className="mx-auto max-w-xl space-y-6">
      <div>
        <Link
          to="/zones"
          className="text-muted-foreground hover:text-foreground mb-2 inline-flex items-center gap-1 text-sm no-underline"
        >
          <ArrowLeft className="size-4" /> Zone'lar
        </Link>
        <h1 className="text-2xl font-semibold tracking-tight">Yeni zone</h1>
      </div>

      <Card>
        <CardContent>
          <form onSubmit={handleSubmit} className="space-y-4">
            <div className="space-y-1.5">
              <label htmlFor="hostname" className="text-sm font-medium">
                Hostname
              </label>
              <Input
                id="hostname"
                required
                placeholder="app.example.com"
                value={hostname}
                onChange={(e) => setHostname(e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <label htmlFor="upstream" className="text-sm font-medium">
                Upstream URL
              </label>
              <Input
                id="upstream"
                required
                placeholder="http://10.0.0.5:3000"
                value={upstreamUrl}
                onChange={(e) => setUpstreamUrl(e.target.value)}
                className="font-mono"
              />
            </div>
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                className="accent-primary size-4"
                checked={challengeEnabled}
                onChange={(e) => setChallengeEnabled(e.target.checked)}
              />
              PoW challenge aktif (varsayılan ayarlarla)
            </label>
            {error && <p className="text-destructive text-sm">{error}</p>}
            <Button type="submit" disabled={busy}>
              {busy ? 'Oluşturuluyor…' : 'Oluştur'}
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
