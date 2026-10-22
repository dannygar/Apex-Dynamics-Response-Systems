// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { DocumentVersion, Readiness } from './contracts'
import EvaluationWorkspace from './EvaluationWorkspace'

// Test-only API doubles; the application has no mock client.
const mocks = vi.hoisted(() => ({
  api: {
    teams: vi.fn(), readiness: vi.fn(), projects: vi.fn(), submissions: vi.fn(), submission: vi.fn(), policy: vi.fn(),
    startScan: vi.fn(), scan: vi.fn(), upload: vi.fn(), members: vi.fn(), me: vi.fn(),
  },
}))
vi.mock('./auth', () => ({
  identityConfigured: true,
  auth: { account: async () => 'person@example.test', signOut: vi.fn(), signIn: vi.fn() },
}))
vi.mock('./api', () => ({ api: mocks.api }))

const contract = { version: 'agent-eval/test judge/fixture', evaluator: 'agent-eval test', sidecar: '0.1.0', reference: { id: 'case-ref', sha: 'abc' },
  rubricsSha: 'r', profilesSha: 'p', judge: { deployment: 'fixture-deployment', apiVersion: 'v1', samples: 1 }, scale: { minimum: 0, maximum: 100 },
  layerWeights: { structure: 0.4, reference: 0.3, judge: 0.3 }, kinds: ['dt', 'brd', 'prd'] }
const readiness = (ready: boolean): Readiness => ({ ready, blockers: ready ? [] : ['Judge worker has not registered recently.'],
  judge: ready ? contract : null, lastHeartbeat: ready ? '2026-10-20T12:00:00Z' : null, scale: { minimum: 0, maximum: 100 },
  scanner: 'scanner note', imports: 'imports note', pdf: 'pdf note' })
const doc = (id: string, role: DocumentVersion['role'], path: string, bundleId = id, status = 'Ready'): DocumentVersion =>
  ({ id, role, fileName: path.split('/').pop()!, relativePath: path, bundleId, hash: 'h', size: 10, status, error: null, textLength: 5,
    createdAt: '2026-10-20T10:00:00Z' })

afterEach(cleanup)
beforeEach(() => {
  Object.values(mocks.api).forEach(fn => fn.mockReset())
  mocks.api.teams.mockResolvedValue([{ id: 'team', name: 'Fixture team', role: 'Member' }])
  mocks.api.me.mockResolvedValue({ subject: 'subject', tenantId: 'tenant', name: 'Person', isPlatformAdmin: false, platformAdminTeamId: null,
    memberships: [{ teamId: 'team', teamName: 'Fixture team', role: 'Member', isPlatformAdmin: false }], pendingInvitationCount: 0 })
  mocks.api.projects.mockResolvedValue([{ id: 'project', name: 'Fixture project' }])
  mocks.api.submissions.mockResolvedValue([{ id: 'submission', name: 'Fixture submission', description: '' }])
  mocks.api.policy.mockResolvedValue({ version: 1, thresholds: { version: 1, dt: 70, brd: 80, prd: 90 } })
  mocks.api.submission.mockResolvedValue({ submission: { id: 'submission', name: 'Fixture submission', description: '' }, scans: [], documents: [
    doc('dt-1', 'DT', 'dt/method-01-scope/map.md', 'bundle'), doc('dt-2', 'DT', 'dt/coaching-state.md', 'bundle'),
    doc('brd-1', 'BRD', 'brd.md'), doc('prd-1', 'PRD', 'prd.pdf', 'prd-1', 'Quarantined'), doc('src-1', 'Source', 'interview.txt')] })
  mocks.api.startScan.mockResolvedValue({ id: 'run' })
  mocks.api.scan.mockResolvedValue(null)
})

async function openTeam() {
  render(<EvaluationWorkspace />)
  const team = await screen.findByRole('combobox', { name: 'Authorized team' })
  await screen.findByRole('option', { name: /Fixture team/ })
  fireEvent.change(team, { target: { value: 'team' } })
}

describe('Evaluation workspace flow', () => {
  it('shows registered judge details when ready', async () => {
    mocks.api.readiness.mockResolvedValue(readiness(true))
    await openTeam()
    const banner = await screen.findByRole('region', { name: 'Judge readiness' })
    await within(banner).findByText(/Ready:/)
    expect(within(banner).getByText('fixture-deployment · API v1')).toBeTruthy()
    expect(within(banner).getByText(/case-ref/)).toBeTruthy()
    expect(within(banner).getByText(/Structure 40%/)).toBeTruthy()
  })
  it('shows blockers and keeps scanning unavailable when the judge is not registered', async () => {
    mocks.api.readiness.mockResolvedValue(readiness(false))
    await openTeam()
    expect(await screen.findByText('Judge worker has not registered recently.')).toBeTruthy()
    fireEvent.change(await screen.findByRole('combobox', { name: 'Project' }), { target: { value: 'project' } })
    fireEvent.click(await screen.findByRole('button', { name: 'Fixture submission' }))
    expect((await screen.findByRole('button', { name: 'Review selection' }) as HTMLButtonElement).disabled).toBe(true)
    expect(screen.getByText(/The AI judge is not registered/)).toBeTruthy()
  })
  it('selects one DT bundle and one BRD, sends Source only as evidence', async () => {
    mocks.api.readiness.mockResolvedValue(readiness(true))
    await openTeam()
    fireEvent.change(await screen.findByRole('combobox', { name: 'Project' }), { target: { value: 'project' } })
    fireEvent.click(await screen.findByRole('button', { name: 'Fixture submission' }))
    fireEvent.click(await screen.findByRole('radio', { name: /DT bundle of 2 files/ }))
    fireEvent.click(screen.getByRole('radio', { name: 'brd.md' }))
    expect((screen.getByRole('radio', { name: 'prd.pdf' }) as HTMLInputElement).disabled).toBe(true)
    fireEvent.click(screen.getByRole('checkbox', { name: /^interview\.txt/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Review selection' }))
    expect(screen.getByText('DT bundle: 2 Markdown file(s)')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Run scan' }))
    await waitFor(() => expect(mocks.api.startScan).toHaveBeenCalled())
    const [, , targets, evidence] = mocks.api.startScan.mock.calls[0]
    expect(targets).toEqual(['dt-2', 'dt-1', 'brd-1'])
    expect(evidence).toEqual(['src-1'])
  })
  it('selects evidence from a source folder upload as a whole or file by file', async () => {
    mocks.api.readiness.mockResolvedValue(readiness(true))
    mocks.api.submission.mockResolvedValue({ submission: { id: 'submission', name: 'Fixture submission', description: '' }, scans: [], documents: [
      doc('brd-1', 'BRD', 'brd.md'), doc('src-1', 'Source', 'interview.txt'),
      doc('src-a', 'Source', 'research/a.pdf', 'research'), doc('src-b', 'Source', 'research/notes/b.txt', 'research'),
      doc('src-c', 'Source', 'research/c.docx', 'research', 'Quarantined')] })
    await openTeam()
    fireEvent.change(await screen.findByRole('combobox', { name: 'Project' }), { target: { value: 'project' } })
    fireEvent.click(await screen.findByRole('button', { name: 'Fixture submission' }))
    const group = await screen.findByRole('checkbox', { name: /^All ready files in the source upload/ }) as HTMLInputElement
    expect(screen.getByText(/Source upload of 3 files/)).toBeTruthy()
    expect((screen.getByRole('checkbox', { name: /^research\/c\.docx/ }) as HTMLInputElement).disabled).toBe(true)
    fireEvent.click(group)
    expect(group.checked).toBe(true)
    expect(screen.getByText('2 of 3 ready evidence files selected.')).toBeTruthy()
    fireEvent.click(screen.getByRole('checkbox', { name: /^research\/notes\/b\.txt/ }))
    expect(group.checked).toBe(false); expect(group.indeterminate).toBe(true)
    fireEvent.click(screen.getByRole('radio', { name: 'brd.md' }))
    fireEvent.click(screen.getByRole('button', { name: 'Review selection' }))
    expect(screen.getByText(/Source evidence \(not sent to the judge\): 1 file/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Run scan' }))
    await waitFor(() => expect(mocks.api.startScan).toHaveBeenCalled())
    expect(mocks.api.startScan.mock.calls[0][3]).toEqual(['src-a'])
  })
  it('selects and clears all ready evidence', async () => {
    mocks.api.readiness.mockResolvedValue(readiness(true))
    mocks.api.submission.mockResolvedValue({ submission: { id: 'submission', name: 'Fixture submission', description: '' }, scans: [], documents: [
      doc('brd-1', 'BRD', 'brd.md'), doc('src-1', 'Source', 'interview.txt'),
      doc('src-a', 'Source', 'research/a.pdf', 'research'), doc('src-b', 'Source', 'research/b.txt', 'research')] })
    await openTeam()
    fireEvent.change(await screen.findByRole('combobox', { name: 'Project' }), { target: { value: 'project' } })
    fireEvent.click(await screen.findByRole('button', { name: 'Fixture submission' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Select all evidence' }))
    expect(screen.getByText('3 of 3 ready evidence files selected.')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Clear evidence' }))
    expect(screen.getByText('0 of 3 ready evidence files selected.')).toBeTruthy()
  })
  it('uploads a source folder with relative paths', async () => {
    mocks.api.readiness.mockResolvedValue(readiness(true))
    mocks.api.upload.mockResolvedValue({ versions: [{}, {}] })
    await openTeam()
    fireEvent.change(await screen.findByRole('combobox', { name: 'Project' }), { target: { value: 'project' } })
    fireEvent.click(await screen.findByRole('button', { name: 'Fixture submission' }))
    fireEvent.change(await screen.findByRole('combobox', { name: 'Document type' }), { target: { value: 'Source' } })
    const file = (path: string, text: string) => {
      const f = new File([text], path.split('/').pop()!)
      Object.defineProperty(f, 'webkitRelativePath', { value: path })
      return f
    }
    fireEvent.change(screen.getByLabelText('Choose a source folder'), { target: { files: [file('research/a.pdf', '%PDF-'), file('research/x.png', 'x'),
      file('research/notes/b.txt', 'notes')] } })
    expect(await screen.findByText('2 file(s) selected; 1 unsupported or hidden file(s) skipped.')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Upload to quarantine' }))
    await waitFor(() => expect(mocks.api.upload).toHaveBeenCalled())
    const body = mocks.api.upload.mock.calls[0][2] as FormData
    expect(body.get('role')).toBe('Source')
    expect(body.getAll('paths')).toEqual(['research/a.pdf', 'research/notes/b.txt'])
  })
})
