import { describe, expect, it } from 'vitest'
import { displayScore, improvements, percent, thresholdProblem } from './format'
import { bundleFormData, dtFormData, pathProblem, prepareDtBundle, prepareSourceBundle, singleFormData } from './upload'

const md = (path: string, text = '# Heading', type = 'text/markdown') => {
  const file = new File([text], path.split('/').pop()!, { type })
  Object.defineProperty(file, 'webkitRelativePath', { value: path })
  return file
}

describe('DT bundle preparation', () => {
  it('keeps relative paths, sorts them and skips non-Markdown files', () => {
    const bundle = prepareDtBundle([md('dt/method-02-research/notes.md'), md('dt/method-01-scope/map.md'),
      md('dt/images/photo.png', 'x', 'image/png'), md('dt/.DS_Store.md')])
    expect(bundle.errors).toEqual([])
    expect(bundle.files.map(f => f.path)).toEqual(['dt/method-01-scope/map.md', 'dt/method-02-research/notes.md'])
    expect(bundle.skipped).toEqual(['dt/images/photo.png', 'dt/.DS_Store.md'])
  })
  it('falls back to the file name for multi-select without a folder', () => {
    expect(prepareDtBundle([new File(['# A'], 'coaching-state.md')]).files[0].path).toBe('coaching-state.md')
  })
  it('rejects duplicates, empty files and too many files', () => {
    expect(prepareDtBundle([md('x/A.md'), md('x/a.md')]).errors[0]).toContain('duplicate')
    expect(prepareDtBundle([md('x/empty.md', '')]).errors[0]).toContain('empty')
    expect(prepareDtBundle(Array.from({ length: 301 }, (_, i) => md(`x/${i}.md`))).errors[0]).toContain('at most 300')
    expect(prepareDtBundle([md('x/photo.png', 'x')]).errors[0]).toContain('No Markdown')
  })
  it('mirrors server path rules', () => {
    expect(pathProblem('method-01-scope/map.md')).toBeNull()
    for (const bad of ['../up.md', '/root.md', 'C:/drive.md', 'a//b.md', 'a/./b.md', ' a/b.md', 'notes.txt', `${'a'.repeat(258)}.md`])
      expect(pathProblem(bad), bad).not.toBeNull()
  })
  it('sends one paths value per file in order', () => {
    const body = dtFormData(prepareDtBundle([md('dt/b.md'), md('dt/a.md')]).files)
    expect(body.get('role')).toBe('DT')
    expect(body.getAll('paths')).toEqual(['dt/a.md', 'dt/b.md'])
    expect((body.getAll('file') as File[]).map(f => f.name)).toEqual(['a.md', 'b.md'])
    const single = singleFormData('BRD', new File(['%PDF-'], 'brd.pdf'))
    expect(single.getAll('file')).toHaveLength(1); expect(single.has('paths')).toBe(false)
  })
})

describe('Source evidence preparation', () => {
  it('accepts a folder of PDF, DOCX, Markdown and text files and skips everything else', () => {
    const bundle = prepareSourceBundle([md('research/interviews/a.txt', 'notes', 'text/plain'), md('research/survey.PDF', '%PDF-', 'application/pdf'),
      md('research/report.docx', 'PK', 'application/zip'), md('research/summary.md'), md('research/photo.jpg', 'x', 'image/jpeg'),
      md('research/.hidden.txt', 'x')])
    expect(bundle.errors).toEqual([])
    expect(bundle.files.map(f => f.path)).toEqual(['research/interviews/a.txt', 'research/report.docx', 'research/summary.md', 'research/survey.PDF'])
    expect(bundle.skipped).toEqual(['research/photo.jpg', 'research/.hidden.txt'])
  })
  it('uses file names for multi-select and rejects duplicates, empty and oversized selections', () => {
    expect(prepareSourceBundle([new File(['a'], 'one.txt'), new File(['%PDF-'], 'two.pdf')]).files.map(f => f.path)).toEqual(['one.txt', 'two.pdf'])
    expect(prepareSourceBundle([md('x/A.txt', 'a'), md('x/a.txt', 'b')]).errors[0]).toContain('duplicate')
    expect(prepareSourceBundle([md('x/empty.txt', '')]).errors[0]).toContain('empty')
    expect(prepareSourceBundle(Array.from({ length: 301 }, (_, i) => md(`x/${i}.txt`, 'x'))).errors[0]).toContain('at most 300')
    expect(prepareSourceBundle([md('x/photo.png', 'x')]).errors[0]).toContain('No PDF, DOCX, Markdown or text')
  })
  it('sends role Source with one paths value per file', () => {
    const body = bundleFormData('Source', prepareSourceBundle([md('r/b.pdf', '%PDF-'), md('r/a.txt', 'a')]).files)
    expect(body.get('role')).toBe('Source')
    expect(body.getAll('paths')).toEqual(['r/a.txt', 'r/b.pdf'])
    expect((body.getAll('file') as File[]).map(f => f.name)).toEqual(['a.txt', 'b.pdf'])
    expect(pathProblem('r/a.txt', false)).toBeNull()
  })
})

describe('Result formatting', () => {
  it('rounds to one decimal unless that would contradict the server decision', () => {
    expect(displayScore(74.96, 70, 'Pass')).toBe('75.0')
    expect(displayScore(69.96, 70, 'Fail')).toBe('69.960')
    expect(displayScore(70, 70, 'Pass')).toBe('70.0')
  })
  it('formats weights as percentages', () => expect([0.4, 0.3].map(percent)).toEqual(['40%', '30%']))
  it('orders improvements fail-first and ignores checks without guidance', () => {
    const base = { layer: 'judge' as const, score: 0, weight: 1, detail: '', evidence: [] }
    const list = improvements([{ ...base, id: 'w', title: 'W', status: 'warn', guidance: 'g' },
      { ...base, id: 'f', title: 'F', status: 'fail', guidance: 'g' }, { ...base, id: 'n', title: 'N', status: 'fail', guidance: ' ' },
      { ...base, id: 'p', title: 'P', status: 'pass', guidance: 'g' }])
    expect(list.map(c => c.id)).toEqual(['f', 'w'])
  })
  it('validates thresholds on the 0–100 scale', () => {
    expect(thresholdProblem('0')).toBeNull(); expect(thresholdProblem('100')).toBeNull(); expect(thresholdProblem('72.5')).toBeNull()
    expect(thresholdProblem('')).not.toBeNull(); expect(thresholdProblem('-1')).not.toBeNull()
    expect(thresholdProblem('100.1')).not.toBeNull(); expect(thresholdProblem('abc')).not.toBeNull()
  })
})
