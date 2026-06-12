import { Link, createFileRoute } from '@tanstack/react-router'
import type { ListEdgeNodesResponse } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'

export const Route = createFileRoute('/nodes')({
  component: NodesPage,
})

const STATUS_TONE: Record<string, string> = {
  Active: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900 dark:text-emerald-200',
  Registered: 'bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-200',
  Disabled: 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300',
}

function NodesPage() {
  const nodes = useApiData<ListEdgeNodesResponse>('/v1/edge-nodes?pageSize=100')

  if (nodes.loading) {
    return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">Yükleniyor…</main>
  }
  if (nodes.error) {
    return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">{nodes.error}</main>
  }

  const items = nodes.data?.items ?? []

  return (
    <main className="mx-auto max-w-5xl space-y-6 px-4 py-8">
      <div className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Edge node'lar</h1>
        <span className="text-sm text-gray-500">{nodes.data?.totalCount ?? 0} kayıt</span>
      </div>

      {items.length === 0 ? (
        <p className="text-sm text-gray-500">Henüz kayıtlı edge node yok.</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-200 text-left text-gray-500 dark:border-gray-800">
              <th className="py-2">Ad</th>
              <th>Adres</th>
              <th>Durum</th>
              <th>Son push</th>
              <th>Son görülme</th>
            </tr>
          </thead>
          <tbody>
            {items.map((node) => (
              <tr key={node.id} className="border-b border-gray-100 dark:border-gray-900">
                <td className="py-2 font-medium">
                  <Link to="/nodes/$nodeId" params={{ nodeId: node.id }} className="hover:underline">
                    {node.name}
                  </Link>
                </td>
                <td className="font-mono text-xs text-gray-500">{node.address}</td>
                <td>
                  <span
                    className={`rounded-full px-2 py-0.5 text-xs ${STATUS_TONE[node.status] ?? 'bg-gray-100 text-gray-600'}`}
                  >
                    {node.status}
                  </span>
                </td>
                <td>
                  {node.lastPushAtUtc === null ? (
                    <span className="text-gray-400">—</span>
                  ) : (
                    <span className={node.lastPushSucceeded ? 'text-emerald-600' : 'text-red-600'}>
                      {node.lastPushSucceeded ? '✓' : '✗'} {new Date(node.lastPushAtUtc).toLocaleString()}
                    </span>
                  )}
                </td>
                <td className="text-gray-500">
                  {node.lastSeenAtUtc ? new Date(node.lastSeenAtUtc).toLocaleString() : '—'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  )
}
