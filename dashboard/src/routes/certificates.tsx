import { createFileRoute } from '@tanstack/react-router'
import { useState } from 'react'
import type { ListCertificatesResponse } from '#/lib/api'
import { apiSend } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'

export const Route = createFileRoute('/certificates')({
  component: CertificatesPage,
})

const STATUS_TONE: Record<string, string> = {
  Active: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900 dark:text-emerald-200',
  Pending: 'bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-200',
  Failed: 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200',
  Expired: 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300',
  Revoked: 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300',
}

/** Days until expiry; negative when already past. */
function daysLeft(expiresAtUtc: string): number {
  return Math.floor((new Date(expiresAtUtc).getTime() - Date.now()) / 86_400_000)
}

function CertificatesPage() {
  // bump forces a refetch after a new request is submitted
  const [bump, setBump] = useState(0)
  const certs = useApiData<ListCertificatesResponse>(`/v1/certificates?pageSize=100&_=${bump}`)
  const [hostname, setHostname] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function requestCertificate(e: React.FormEvent) {
    e.preventDefault()
    if (hostname.trim().length === 0) return
    setBusy(true)
    setError(null)
    try {
      await apiSend('/v1/certificates', 'POST', { hostname: hostname.trim() })
      setHostname('')
      setBump((n) => n + 1)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'İstek başarısız.')
    } finally {
      setBusy(false)
    }
  }

  if (certs.loading) {
    return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">Yükleniyor…</main>
  }
  if (certs.error) {
    return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">{certs.error}</main>
  }

  const items = certs.data?.items ?? []

  return (
    <main className="mx-auto max-w-5xl space-y-6 px-4 py-8">
      <div className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Sertifikalar</h1>
        <span className="text-sm text-gray-500">{certs.data?.totalCount ?? 0} kayıt</span>
      </div>

      <form onSubmit={requestCertificate} className="flex flex-wrap items-center gap-2">
        <input
          type="text"
          value={hostname}
          onChange={(e) => setHostname(e.target.value)}
          placeholder="örn. app.example.com"
          className="w-72 rounded-md border border-gray-200 px-3 py-1.5 text-sm dark:border-gray-800 dark:bg-gray-900"
        />
        <button
          type="submit"
          disabled={busy || hostname.trim().length === 0}
          className="rounded-md bg-gray-900 px-3 py-1.5 text-sm text-white hover:bg-gray-700 disabled:opacity-40 dark:bg-gray-100 dark:text-gray-900"
        >
          Sertifika iste
        </button>
        {error && <span className="text-sm text-red-600">{error}</span>}
      </form>
      <p className="text-xs text-gray-400">
        ACME worker bekleyen istekleri otomatik işler; sertifika kesilince edge node'lara push'lanır.
      </p>

      {items.length === 0 ? (
        <p className="text-sm text-gray-500">Henüz sertifika yok.</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-200 text-left text-gray-500 dark:border-gray-800">
              <th className="py-2">Hostname</th>
              <th>Durum</th>
              <th>Talep</th>
              <th>Bitiş</th>
              <th className="text-right">Id</th>
            </tr>
          </thead>
          <tbody>
            {items.map((cert) => (
              <tr key={cert.id} className="border-b border-gray-100 dark:border-gray-900">
                <td className="py-2 font-medium">{cert.hostname}</td>
                <td>
                  <span
                    className={`rounded-full px-2 py-0.5 text-xs ${STATUS_TONE[cert.status] ?? 'bg-gray-100 text-gray-600'}`}
                  >
                    {cert.status}
                  </span>
                </td>
                <td className="text-gray-500">{new Date(cert.requestedAtUtc).toLocaleString()}</td>
                <td>
                  {cert.expiresAtUtc === null ? (
                    <span className="text-gray-400">—</span>
                  ) : (
                    <span className={daysLeft(cert.expiresAtUtc) <= 30 ? 'text-amber-600' : 'text-gray-500'}>
                      {new Date(cert.expiresAtUtc).toLocaleDateString()} ({daysLeft(cert.expiresAtUtc)} gün)
                    </span>
                  )}
                </td>
                <td className="text-right font-mono text-xs text-gray-500">{cert.id}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  )
}
