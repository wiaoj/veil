import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { apiSend } from '#/lib/api'

export const Route = createFileRoute('/zones/new')({
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
    <main className="mx-auto max-w-xl space-y-6 px-4 py-8">
      <h1 className="text-2xl font-semibold">Yeni zone</h1>
      <form onSubmit={handleSubmit} className="space-y-4">
        <label className="block text-sm">
          Hostname
          <input
            required
            placeholder="app.example.com"
            value={hostname}
            onChange={(e) => setHostname(e.target.value)}
            className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 dark:border-gray-700 dark:bg-gray-900"
          />
        </label>
        <label className="block text-sm">
          Upstream URL
          <input
            required
            placeholder="http://10.0.0.5:3000"
            value={upstreamUrl}
            onChange={(e) => setUpstreamUrl(e.target.value)}
            className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 font-mono dark:border-gray-700 dark:bg-gray-900"
          />
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={challengeEnabled}
            onChange={(e) => setChallengeEnabled(e.target.checked)}
          />
          PoW challenge aktif (varsayılan ayarlarla)
        </label>
        {error && <p className="text-sm text-red-600">{error}</p>}
        <button
          type="submit"
          disabled={busy}
          className="rounded-md bg-gray-900 px-4 py-2 text-sm text-white hover:bg-gray-700 disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {busy ? 'Oluşturuluyor…' : 'Oluştur'}
        </button>
      </form>
    </main>
  )
}
