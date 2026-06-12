import { Link, createFileRoute } from '@tanstack/react-router'
import { useState } from 'react'
import type { ConfigPushLogResponse } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'

export const Route = createFileRoute('/nodes/$nodeId')({
  component: NodePushLogPage,
})

const PAGE_SIZE = 20

function NodePushLogPage() {
  const { nodeId } = Route.useParams()
  const [page, setPage] = useState(1)
  const log = useApiData<ConfigPushLogResponse>(
    `/v1/edge-nodes/${nodeId}/push-log?page=${page}&pageSize=${PAGE_SIZE}`,
  )

  if (log.loading) {
    return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">Yükleniyor…</main>
  }
  if (log.error) {
    return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">{log.error}</main>
  }
  if (!log.data) return null

  const { items, totalCount } = log.data
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE))

  return (
    <main className="mx-auto max-w-5xl space-y-6 px-4 py-8">
      <div className="flex items-baseline justify-between">
        <div>
          <Link to="/nodes" className="text-sm text-gray-500 hover:underline">
            ← Edge node'lar
          </Link>
          <h1 className="text-2xl font-semibold">Config push geçmişi</h1>
          <p className="font-mono text-xs text-gray-500">{nodeId}</p>
        </div>
        <span className="text-sm text-gray-500">{totalCount} kayıt</span>
      </div>

      {items.length === 0 ? (
        <p className="text-sm text-gray-500">Bu node'a henüz config push yapılmamış.</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-200 text-left text-gray-500 dark:border-gray-800">
              <th className="py-2">Zaman</th>
              <th>Sonuç</th>
              <th>Hata</th>
            </tr>
          </thead>
          <tbody>
            {items.map((entry, i) => (
              <tr key={`${entry.pushedAtUtc}-${i}`} className="border-b border-gray-100 dark:border-gray-900">
                <td className="py-2">{new Date(entry.pushedAtUtc).toLocaleString()}</td>
                <td>
                  <span
                    className={`rounded-full px-2 py-0.5 text-xs ${
                      entry.succeeded
                        ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900 dark:text-emerald-200'
                        : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200'
                    }`}
                  >
                    {entry.succeeded ? 'Başarılı' : 'Başarısız'}
                  </span>
                </td>
                <td className="max-w-md truncate text-gray-500" title={entry.error ?? undefined}>
                  {entry.error ?? '—'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {totalPages > 1 && (
        <div className="flex items-center gap-3 text-sm">
          <button
            type="button"
            disabled={page <= 1}
            onClick={() => setPage((p) => p - 1)}
            className="rounded-md border border-gray-200 px-3 py-1 disabled:opacity-40 dark:border-gray-800"
          >
            ← Önceki
          </button>
          <span className="text-gray-500">
            Sayfa {page} / {totalPages}
          </span>
          <button
            type="button"
            disabled={page >= totalPages}
            onClick={() => setPage((p) => p + 1)}
            className="rounded-md border border-gray-200 px-3 py-1 disabled:opacity-40 dark:border-gray-800"
          >
            Sonraki →
          </button>
        </div>
      )}
    </main>
  )
}
