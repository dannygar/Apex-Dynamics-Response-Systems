import type { CheckOutcome, Decision, Layer } from './contracts'

export const LAYERS: Layer[] = ['structure', 'reference', 'judge']
export const LAYER_LABELS: Record<Layer, string> = { structure: 'Structure', reference: 'Reference', judge: 'AI judge' }
export const STATUS_LABELS: Record<CheckOutcome['status'], string> = { pass: 'Pass', warn: 'Warning', fail: 'Fail', skip: 'Skipped' }

export const percent = (weight: number) => `${Math.round(weight * 1000) / 10}%`

// One decimal for display; more precision only if rounding would visually contradict the server decision.
export function displayScore(score100: number, threshold: number, decision: Decision): string {
  const rounded = score100.toFixed(1)
  return (Number(rounded) >= threshold) === (decision === 'Pass') ? rounded : score100.toFixed(3)
}

// Failing checks first, then warnings; only checks that carry guidance.
export function improvements(checks: CheckOutcome[]): CheckOutcome[] {
  const rank = { fail: 0, warn: 1, pass: 2, skip: 3 }
  return checks.filter(c => (c.status === 'fail' || c.status === 'warn') && c.guidance.trim())
    .sort((a, b) => rank[a.status] - rank[b.status] || LAYERS.indexOf(a.layer) - LAYERS.indexOf(b.layer))
}

// Client-side hint only; the API validates and versions every policy.
export function thresholdProblem(value: string): string | null {
  if (!value.trim()) return 'Enter a threshold.'
  const number = Number(value)
  if (!Number.isFinite(number)) return 'Enter a number.'
  if (number < 0 || number > 100) return 'Use a value from 0 to 100.'
  return null
}
