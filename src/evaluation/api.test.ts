// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, request } from './api'

vi.mock('./auth', () => ({ auth: { token: vi.fn(async () => 'test-only-token') } }))
afterEach(() => vi.unstubAllGlobals())
describe('Evaluation API boundary', () => {
  it('sends a token only to the same-origin API and preserves the idempotency key', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ id: 'run' }), { status: 202 }))
    vi.stubGlobal('fetch', fetchMock)
    await api.startScan('team', 'submission', ['target'], ['source'], 'stable-key')
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('/api/teams/team/submissions/submission/scans')
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer test-only-token')
    expect(new Headers(init.headers).get('Idempotency-Key')).toBe('stable-key')
    expect(init.credentials).toBe('omit')
    expect(JSON.parse(String(init.body))).toEqual({ targetVersionIds: ['target'], evidenceVersionIds: ['source'] })
  })
  it('does not override the multipart content boundary', async () => {
    const fetchMock = vi.fn(async () => new Response('{}', { status: 202 }))
    vi.stubGlobal('fetch', fetchMock)
    const body = new FormData()
    body.set('role', 'Source')
    await api.upload('team', 'submission', body)
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(new Headers(init.headers).has('Content-Type')).toBe(false)
  })
  it('surfaces blocked configuration rather than fabricating a result', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ error: 'Judge integration unavailable.' }), { status: 409 })))
    await expect(api.startScan('team', 'submission', ['t'], ['e'], 'key')).rejects.toThrow('Judge integration unavailable.')
  })
  it('allows scans without Source evidence', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ id: 'run' }), { status: 202 }))
    vi.stubGlobal('fetch', fetchMock)
    await api.startScan('team', 'submission', ['brd'], [], 'key')
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(JSON.parse(String(init.body))).toEqual({ targetVersionIds: ['brd'], evidenceVersionIds: [] })
  })
  it('handles denied access even when the API has an empty response', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 404 })))
    await expect(request('/teams/private')).rejects.toBeInstanceOf(ApiError)
    await expect(request('/teams/private')).rejects.toThrow('Resource unavailable')
  })
})
