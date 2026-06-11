import { useNavigate } from '@tanstack/react-router'
import { useEffect, useState } from 'react'
import { UnauthorizedError, apiGet, hasSession } from '#/lib/api'

/**
 * Client-side authenticated data loading: redirects to /login when there is
 * no session or the refresh chain fails. (TanStack Query replaces this once
 * the scaffold grows — kept dependency-free for now.)
 */
export function useApiData<T>(path: string): {
  data: T | null
  error: string | null
  loading: boolean
} {
  const navigate = useNavigate()
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    if (!hasSession()) {
      navigate({ to: '/login' })
      return
    }
    let cancelled = false
    setLoading(true)
    apiGet<T>(path)
      .then((result) => {
        if (!cancelled) setData(result)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (err instanceof UnauthorizedError) {
          navigate({ to: '/login' })
          return
        }
        setError(err instanceof Error ? err.message : 'İstek başarısız.')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [path, navigate])

  return { data, error, loading }
}
