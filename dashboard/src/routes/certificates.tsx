import { createFileRoute } from '@tanstack/react-router'
import { useState } from 'react'
import type { ListCertificatesResponse } from '#/lib/api'
import { apiSend } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'
import { PageHeader, PageState } from '@/components/PageState'
import { StatusBadge } from '@/components/StatusBadge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

export const Route = createFileRoute('/certificates')({
  component: CertificatesPage,
})

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

  async function revokeCertificate(id: string, hostname: string) {
    if (!window.confirm(`"${hostname}" sertifikası iptal edilsin mi?`)) return
    try {
      await apiSend(`/v1/certificates/${id}/revoke`, 'POST')
      setBump((n) => n + 1)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'İptal başarısız.')
    }
  }

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

  if (certs.loading) return <PageState>Yükleniyor…</PageState>
  if (certs.error) return <PageState>{certs.error}</PageState>

  const items = certs.data?.items ?? []

  return (
    <div className="space-y-6">
      <PageHeader title="Sertifikalar" description={`${certs.data?.totalCount ?? 0} kayıt`} />

      <Card>
        <CardContent className="space-y-3">
          <form onSubmit={requestCertificate} className="flex flex-wrap items-center gap-2">
            <Input
              type="text"
              value={hostname}
              onChange={(e) => setHostname(e.target.value)}
              placeholder="örn. app.example.com"
              className="w-72"
            />
            <Button type="submit" disabled={busy || hostname.trim().length === 0}>
              Sertifika iste
            </Button>
            {error && <span className="text-destructive text-sm">{error}</span>}
          </form>
          <p className="text-muted-foreground text-xs">
            ACME worker bekleyen istekleri otomatik işler; sertifika kesilince edge node'lara
            push'lanır.
          </p>
        </CardContent>
      </Card>

      {items.length === 0 ? (
        <Card className="text-muted-foreground items-center py-12 text-center text-sm">
          Henüz sertifika yok.
        </Card>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Hostname</TableHead>
                <TableHead>Durum</TableHead>
                <TableHead>Talep</TableHead>
                <TableHead>Bitiş</TableHead>
                <TableHead>Id</TableHead>
                <TableHead className="text-right">İşlem</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((cert) => (
                <TableRow key={cert.id}>
                  <TableCell className="font-medium">{cert.hostname}</TableCell>
                  <TableCell>
                    <StatusBadge status={cert.status} />
                  </TableCell>
                  <TableCell className="text-muted-foreground">
                    {new Date(cert.requestedAtUtc).toLocaleString()}
                  </TableCell>
                  <TableCell>
                    {cert.expiresAtUtc === null ? (
                      <span className="text-muted-foreground">—</span>
                    ) : (
                      <span
                        className={
                          daysLeft(cert.expiresAtUtc) <= 30
                            ? 'text-amber-600 dark:text-amber-400'
                            : 'text-muted-foreground'
                        }
                      >
                        {new Date(cert.expiresAtUtc).toLocaleDateString()} (
                        {daysLeft(cert.expiresAtUtc)} gün)
                      </span>
                    )}
                  </TableCell>
                  <TableCell className="text-muted-foreground font-mono text-xs">
                    {cert.id}
                  </TableCell>
                  <TableCell className="text-right">
                    {cert.status === 'Active' && (
                      <button
                        onClick={() => revokeCertificate(cert.id, cert.hostname)}
                        className="text-destructive text-xs hover:underline"
                      >
                        iptal et
                      </button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      )}
    </div>
  )
}
