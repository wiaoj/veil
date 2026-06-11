import { Link, createFileRoute } from '@tanstack/react-router'
import type { ListZonesResponse } from '#/lib/api'
import { useApiData } from '#/lib/useApiData'

export const Route = createFileRoute('/zones')({
  component: ZonesPage,
})

const STATUS_TONE: Record<string, string> = {
  Active: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900 dark:text-emerald-200',
  Provisioning: 'bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-200',
  Paused: 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300',
  Error: 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200',
}

function ZonesPage() {
  const zones = useApiData<ListZonesResponse>('/v1/zones?pageSize=100')

  if (zones.loading) {
    return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">Yükleniyor…</main>
  }
  if (zones.error) {
    return <main className="flex min-h-[50vh] items-center justify-center text-gray-500">{zones.error}</main>
  }

  const items = zones.data?.items ?? []

  return (
    <main className="mx-auto max-w-5xl space-y-6 px-4 py-8">
      <div className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Zone'lar</h1>
        <div className="flex items-center gap-4">
          <span className="text-sm text-gray-500">{zones.data?.totalCount ?? 0} kayıt</span>
          <Link
            to="/zones/new"
            className="rounded-md bg-gray-900 px-3 py-1.5 text-sm text-white hover:bg-gray-700 dark:bg-gray-100 dark:text-gray-900"
          >
            Yeni zone
          </Link>
        </div>
      </div>

      {items.length === 0 ? (
        <p className="text-sm text-gray-500">Henüz zone yok.</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-200 text-left text-gray-500 dark:border-gray-800">
              <th className="py-2">Hostname</th>
              <th>Durum</th>
              <th>Kural</th>
              <th className="text-right">Id</th>
            </tr>
          </thead>
          <tbody>
            {items.map((zone) => (
              <tr key={zone.id} className="border-b border-gray-100 dark:border-gray-900">
                <td className="py-2 font-medium">
                  <Link to="/zones/$zoneId" params={{ zoneId: zone.id }} className="hover:underline">
                    {zone.hostname}
                  </Link>
                </td>
                <td>
                  <span
                    className={`rounded-full px-2 py-0.5 text-xs ${STATUS_TONE[zone.status] ?? 'bg-gray-100 text-gray-600'}`}
                  >
                    {zone.status}
                  </span>
                </td>
                <td>{zone.ruleCount}</td>
                <td className="text-right font-mono text-xs text-gray-500">{zone.id}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  )
}
