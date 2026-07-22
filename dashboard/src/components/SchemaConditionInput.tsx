import { useEffect, useState } from 'react'
import type { SchemaSubject, SchemaVersion } from '#/lib/api'
import { apiGet, subjectName, versionId } from '#/lib/api'
import { Input, Select } from '@/components/ui/input'

interface SchemaConditionInputProps {
  subject: string
  version: string
  onChange: (update: { subject?: string; version?: string }) => void
}

export function SchemaConditionInput({ subject, version, onChange }: SchemaConditionInputProps) {
  const [subjects, setSubjects] = useState<Array<SchemaSubject> | null>(null)
  const [versions, setVersions] = useState<Array<SchemaVersion> | null>(null)
  const [loadingSubjects, setLoadingSubjects] = useState(true)
  const [loadingVersions, setLoadingVersions] = useState(false)
  const [fallback, setFallback] = useState(false)

  // Fetch subjects on mount
  useEffect(() => {
    let cancelled = false
    setLoadingSubjects(true)
    apiGet<Array<SchemaSubject>>('/v1/schemas')
      .then((result) => {
        if (!cancelled) {
          setSubjects(Array.isArray(result) ? result : [])
          setFallback(false)
        }
      })
      .catch(() => {
        if (!cancelled) {
          // If registry is down or errors, fallback to manual text inputs
          setFallback(true)
        }
      })
      .finally(() => {
        if (!cancelled) setLoadingSubjects(false)
      })
    return () => {
      cancelled = true
    }
  }, [])

  // Fetch versions when subject changes
  useEffect(() => {
    if (!subject || fallback) {
      setVersions(null)
      return
    }
    let cancelled = false
    setLoadingVersions(true)
    apiGet<Array<SchemaVersion>>(`/v1/schemas/${encodeURIComponent(subject)}/versions`)
      .then((result) => {
        if (!cancelled) {
          setVersions(Array.isArray(result) ? result : [])
        }
      })
      .catch(() => {
        if (!cancelled) setFallback(true)
      })
      .finally(() => {
        if (!cancelled) setLoadingVersions(false)
      })
    return () => {
      cancelled = true
    }
  }, [subject, fallback])

  if (fallback) {
    return (
      <>
        <Input
          required
          placeholder="Şema subject"
          value={subject}
          onChange={(e) => onChange({ subject: e.target.value })}
          className="w-40 font-mono"
          data-testid="fallback-subject-input"
        />
        <Input
          required
          placeholder="Versiyon (ör. 1.0.0)"
          value={version}
          onChange={(e) => onChange({ version: e.target.value })}
          className="w-32 font-mono"
          data-testid="fallback-version-input"
        />
      </>
    )
  }

  return (
    <>
      {loadingSubjects ? (
        <div className="text-muted-foreground w-40 px-3 py-2 text-xs" data-testid="loading-subjects">Şemalar yükleniyor...</div>
      ) : (
        <Select
          required
          value={subject}
          onChange={(e) => onChange({ subject: e.target.value, version: '' })}
          className="w-40 font-mono"
          data-testid="subject-select"
        >
          <option value="" disabled>
            Şema seçin...
          </option>
          {subjects?.map((s) => {
            const name = subjectName(s)
            return (
              <option key={name} value={name}>
                {name}
              </option>
            )
          })}
        </Select>
      )}

      {subject && !loadingSubjects && (
        loadingVersions ? (
          <div className="text-muted-foreground w-32 px-3 py-2 text-xs" data-testid="loading-versions">Yükleniyor...</div>
        ) : (
          <Select
            required
            value={version}
            onChange={(e) => onChange({ version: e.target.value })}
            className="w-32 font-mono"
            data-testid="version-select"
          >
            <option value="" disabled>
              Versiyon...
            </option>
            {versions?.map((v) => {
              const id = versionId(v)
              return (
                <option key={id} value={id}>
                  v{id}
                </option>
              )
            })}
          </Select>
        )
      )}
    </>
  )
}
