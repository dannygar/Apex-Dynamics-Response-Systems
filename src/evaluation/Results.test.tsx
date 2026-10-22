// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { Results } from './Results'
import type { ArtifactOutcome, CheckOutcome, Scan } from './contracts'

afterEach(cleanup)
// Test-only scan payloads; never bundled into the application or used by the API.
const check = (layer: CheckOutcome['layer'], status: CheckOutcome['status'], title: string, guidance = ''): CheckOutcome =>
  ({ id: `${layer}.${title}`, layer, title, status, score: status === 'pass' ? 1 : 0.4, weight: 1, detail: `${title} detail`,
    evidence: ['✔ quoted text'], guidance })
const dt: ArtifactOutcome = {
  role: 'DT', versionIds: ['dt-a', 'dt-b'], decision: 'Pass', score100: 74.96, threshold: 70,
  layers: [{ layer: 'structure', weight: 0.4, score: 0.9 }, { layer: 'reference', weight: 0.3, score: null }, { layer: 'judge', weight: 0.3, score: 0.5 }],
  checks: [check('structure', 'pass', 'Method folders'), check('judge', 'warn', 'Insight depth', 'Aim for: evidence-backed insights'),
    check('structure', 'fail', 'Coaching state', 'Add coaching-state.md'), check('judge', 'skip', 'Quotes verified')],
  summary: 'Solid structure, shallow insights.', injectionDetected: false, truncated: false,
  usage: { promptTokens: 1200, completionTokens: 300, calls: 1 }, invocationId: 'inv-1', judgeStatus: 'warn', errors: [],
}
const scan: Scan = {
  id: 'test-run', submissionId: 'test-submission', status: 'Completed', outcome: 'Pass', error: null, createdAt: '2026-10-20T12:00:00Z',
  inputs: [
    { versionId: 'dt-a', role: 'DT', hash: 'h1', textLength: 80, bundleId: 'bundle', relativePath: 'method-01-scope/map.md' },
    { versionId: 'dt-b', role: 'DT', hash: 'h2', textLength: 80, bundleId: 'bundle', relativePath: 'coaching-state.md' },
    { versionId: 'src', role: 'Source', hash: 'h3', textLength: 80, bundleId: 'src', relativePath: 'interview.txt' },
  ],
  policy: { version: 2, dt: 70, brd: 75, prd: 80 },
  judge: { version: 'agent-eval/test', evaluator: 'agent-eval test', sidecar: '0.1.0', reference: { id: 'case', sha: 'abc' },
    rubricsSha: 'r', profilesSha: 'p', judge: { deployment: 'fixture', apiVersion: 'v1', samples: 1 }, scale: { minimum: 0, maximum: 100 },
    layerWeights: { structure: 0.4, reference: 0.3, judge: 0.3 }, kinds: ['dt', 'brd', 'prd'] },
  result: { outcome: 'Pass', artifacts: [dt], errors: [] },
  invocationIds: ['inv-1'],
}

describe('Scan results', () => {
  it('shows server decision, score/100, threshold and Not scanned for unselected types', () => {
    render(<Results scan={scan} documents={[]} />)
    expect(screen.getAllByText('Not scanned')).toHaveLength(2)
    const result = screen.getByRole('region', { name: 'DT result' })
    expect(within(result).getByText(/Decision:/).textContent).toContain('Pass')
    expect(within(result).getByText('75.0 / 100')).toBeTruthy()
    expect(within(result).getByText(/Threshold: 70 \/ 100/)).toBeTruthy()
  })
  it('renders the layer breakdown with snapshot weights and text labels', () => {
    render(<Results scan={scan} documents={[]} />)
    const table = screen.getByRole('table', { name: 'Layer breakdown' })
    const rows = within(table).getAllByRole('row').map(r => r.textContent)
    expect(rows).toEqual(['LayerWeightScore', 'Structure40%90.0 / 100', 'Reference30%Not scored', 'AI judge30%50.0 / 100'])
  })
  it('lists improvements from failing then warning checks and groups checks by layer', () => {
    render(<Results scan={scan} documents={[]} />)
    const items = within(screen.getByRole('heading', { name: 'What to improve' }).nextElementSibling as HTMLElement).getAllByRole('listitem')
    expect(items.map(i => i.textContent)).toEqual(['Fail: Coaching state — Add coaching-state.md',
      'Warning: Insight depth — Aim for: evidence-backed insights'])
    expect(screen.getByText('Structure checks (2)')).toBeTruthy()
    expect(screen.getByText('AI judge checks (2)')).toBeTruthy()
    expect(screen.getByText(/Skipped · not scored/)).toBeTruthy()
    expect(screen.getByText(/Source evidence snapshotted/).textContent).toContain('interview.txt')
  })
  it('shows technical failure with partial results and no fabricated score', () => {
    const failed: ArtifactOutcome = { ...dt, role: 'BRD', versionIds: ['brd'], decision: 'Inconclusive', score100: null, threshold: 75,
      layers: [], checks: [], summary: '', usage: null, errors: ['AI judge failed (502). No score produced.'] }
    render(<Results scan={{ ...scan, status: 'TechnicalFailure', outcome: 'Inconclusive', error: 'AI judge failed (502). No score produced.',
      inputs: [...scan.inputs, { versionId: 'brd', role: 'BRD', hash: 'h', textLength: 1, bundleId: 'brd', relativePath: 'brd.pdf' }],
      result: { outcome: 'Inconclusive', artifacts: [dt, failed], errors: [] } }} documents={[]} />)
    const brd = screen.getByRole('region', { name: 'BRD result' })
    expect(within(brd).getByText(/Score:/).textContent).toContain('Unavailable')
    expect(within(brd).getByText(/Decision:/).textContent).toContain('Inconclusive')
    expect(within(brd).getByRole('alert').textContent).toContain('502')
    expect(within(screen.getByRole('region', { name: 'DT result' })).getByText(/Decision:/).textContent).toContain('Pass')
  })
  it('shows pending progress while the judge runs', () => {
    render(<Results scan={{ ...scan, status: 'Evaluating', outcome: 'Inconclusive', result: { outcome: 'Pending', errors: [],
      artifacts: [{ ...dt, decision: 'Pending', score100: null, layers: [], checks: [] }] } }} documents={[]} />)
    expect(screen.getByText(/Evaluation in progress/)).toBeTruthy()
    expect(within(screen.getByRole('region', { name: 'DT result' })).getByText('Pending')).toBeTruthy()
  })
  it('renders judge text as untrusted text and flags injection and truncation', () => {
    render(<Results scan={{ ...scan, result: { outcome: 'Pass', errors: [], artifacts: [{ ...dt, injectionDetected: true, truncated: true,
      summary: '<script>unsafe()</script>', checks: [{ ...check('judge', 'fail', '<img src=x onerror=alert(1)>', '<b>bold</b>') }] }] } }} documents={[]} />)
    expect(screen.getByText('<script>unsafe()</script>')).toBeTruthy()
    expect(document.querySelector('script')).toBeNull()
    expect(document.querySelector('img')).toBeNull()
    expect(document.querySelector('b')).toBeNull()
    expect(screen.getByText(/instruction-like text was detected/)).toBeTruthy()
    expect(screen.getByText(/truncated for the AI judge/)).toBeTruthy()
  })
})
