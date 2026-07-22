/**
 * @vitest-environment jsdom
 */
import { render, screen, waitFor, cleanup } from '@testing-library/react'
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { SchemaConditionInput } from './SchemaConditionInput'
import { apiGet } from '#/lib/api'

vi.mock('#/lib/api', () => ({
  apiGet: vi.fn(),
  subjectName: (s: any) => s.subject ?? s.name,
  versionId: (v: any) => v.version,
}))

describe('SchemaConditionInput', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('renders loading state initially and then subjects', async () => {
    vi.mocked(apiGet).mockResolvedValue([{ subject: 'test-subject' }])

    render(<SchemaConditionInput subject="" version="" onChange={vi.fn()} />)

    // Should show loading
    expect(screen.getByTestId('loading-subjects')).toBeTruthy()

    // Wait for the mock to resolve
    await waitFor(() => {
      expect(screen.getByTestId('subject-select')).toBeTruthy()
    })

    const options = screen.getAllByRole('option')
    expect(options[1].textContent).toBe('test-subject')
  })

  it('fetches versions when a subject is selected', async () => {
    vi.mocked(apiGet).mockImplementation(async (path: string) => {
      if (path === '/v1/schemas') return [{ subject: 'test-subject' }]
      return [{ version: '1.0.0' }]
    })

    render(<SchemaConditionInput subject="test-subject" version="" onChange={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByTestId('subject-select')).toBeTruthy()
      expect(screen.getByTestId('version-select')).toBeTruthy()
    })

    // Both selects should have options
    const selects = screen.getAllByRole('combobox')
    expect(selects).toHaveLength(2)
  })

  it('falls back to manual inputs on api error', async () => {
    vi.mocked(apiGet).mockRejectedValue(new Error('Network error'))

    render(<SchemaConditionInput subject="my-sub" version="1.0" onChange={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByTestId('fallback-subject-input')).toBeTruthy()
      expect(screen.getByTestId('fallback-version-input')).toBeTruthy()
    })
  })
})
