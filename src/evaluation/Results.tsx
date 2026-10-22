import type { ArtifactOutcome, Decision, DocumentVersion, Layer, Scan, ScoredRole } from './contracts'
import { displayScore, improvements, LAYER_LABELS, LAYERS, percent, STATUS_LABELS } from './format'

// All judge-provided strings are untrusted: they are rendered only as React text, never as HTML.
const ACTIVE = ['Queued', 'Preparing', 'Evaluating']

export function Results({ scan, documents }: { scan: Scan; documents: DocumentVersion[] }) {
  const active = ACTIVE.includes(scan.status)
  const outcome = active ? 'Pending' : scan.result?.outcome === 'Pending' ? 'Inconclusive' : scan.result?.outcome ?? scan.outcome ?? 'Inconclusive'
  const thresholds: Record<ScoredRole, number> = { DT: scan.policy.dt, BRD: scan.policy.brd, PRD: scan.policy.prd }
  return <article className="eval-card" aria-label={`Scan ${scan.id}`}>
    <h3>Scan {new Date(scan.createdAt).toLocaleString()}</h3>
    <p>Execution: <strong>{scan.status}</strong> · Acceptance: <Badge decision={outcome} /></p>
    {active && <p role="status" aria-live="polite">Evaluation in progress. AI judge calls can take several minutes per artifact.</p>}
    {scan.error && <p role="alert">{scan.error}</p>}
    <p>Policy v{scan.policy.version} · thresholds DT {scan.policy.dt} / BRD {scan.policy.brd} / PRD {scan.policy.prd} (0–100 scale)</p>
    <details><summary>Judge snapshot</summary>
      <p className="eval-mono">{scan.judge.version}</p>
      <p>{scan.judge.evaluator} · deployment {scan.judge.judge.deployment} ({scan.judge.judge.apiVersion}) ·
        {' '}{scan.judge.judge.samples} sample{scan.judge.judge.samples === 1 ? '' : 's'} · case reference {scan.judge.reference.id}</p>
      <p>Invocation IDs: {scan.invocationIds.length ? scan.invocationIds.join(', ') : 'none'}</p>
    </details>
    {(['DT', 'BRD', 'PRD'] as const).map(role => {
      const inputs = scan.inputs.filter(i => i.role === role)
      const artifact = scan.result?.artifacts.find(a => a.role === role)
      return <section key={role} aria-label={`${role} result`}>
        <h4>{role}</h4>
        {!inputs.length ? <p>Not scanned</p> :
          <ArtifactResult artifact={artifact} role={role} threshold={artifact?.threshold ?? thresholds[role]}
            files={inputs.map(i => i.relativePath || documents.find(d => d.id === i.versionId)?.fileName || i.versionId)}
            weights={scan.judge.layerWeights} pending={active} />}
      </section>
    })}
    {(scan.inputs.some(i => i.role === 'Source')) && <p>Source evidence snapshotted (context only, not sent to the judge):{' '}
      {scan.inputs.filter(i => i.role === 'Source').map(i => i.relativePath).join(', ')}</p>}
  </article>
}

function Badge({ decision }: { decision: string }) {
  return <strong className={`eval-badge eval-${decision.toLowerCase()}`}>{decision}</strong>
}

function ArtifactResult({ artifact, role, threshold, files, weights, pending }: {
  artifact: ArtifactOutcome | undefined; role: ScoredRole; threshold: number; files: string[]
  weights: Record<Layer, number>; pending: boolean
}) {
  const decision: Decision = artifact?.decision ?? (pending ? 'Pending' : 'Inconclusive')
  const score = artifact?.score100
  const todo = artifact ? improvements(artifact.checks) : []
  return <div className="eval-card eval-artifact">
    <p>Decision: <Badge decision={decision} /> · Score:{' '}
      <strong>{score == null ? 'Unavailable' : `${displayScore(score, threshold, decision)} / 100`}</strong>
      {' '}· Threshold: {threshold} / 100</p>
    {role === 'DT' ? <details><summary>{files.length} Markdown file{files.length === 1 ? '' : 's'} in bundle</summary>
      <ul className="eval-mono">{files.map(f => <li key={f}>{f}</li>)}</ul></details> : <p className="eval-mono">{files.join(', ')}</p>}
    {artifact?.errors.map(e => <p key={e} role="alert">{e}</p>)}
    {artifact?.injectionDetected && <p className="eval-warning" role="alert">
      Warning: instruction-like text was detected in this artifact. The judge was told to ignore it; review the document content.</p>}
    {artifact?.truncated && <p className="eval-warning">Note: the artifact was truncated for the AI judge; later content was not judged.</p>}
    {artifact && artifact.layers.length > 0 && <table className="eval-table">
      <caption>Layer breakdown</caption>
      <thead><tr><th scope="col">Layer</th><th scope="col">Weight</th><th scope="col">Score</th></tr></thead>
      <tbody>{LAYERS.map(layer => {
        const value = artifact.layers.find(l => l.layer === layer)
        return <tr key={layer}><th scope="row">{LAYER_LABELS[layer]}</th><td>{percent(value?.weight ?? weights[layer])}</td>
          <td>{value?.score == null ? 'Not scored' : `${(value.score * 100).toFixed(1)} / 100`}</td></tr>
      })}</tbody>
    </table>}
    {artifact?.summary && <><h5>Judge summary</h5><p>{artifact.summary}</p></>}
    {todo.length > 0 && <><h5>What to improve</h5>
      <ol>{todo.map((c, i) => <li key={`${c.id}-${i}`}><strong>{STATUS_LABELS[c.status]}: {c.title}</strong> — {c.guidance}</li>)}</ol></>}
    {artifact && artifact.checks.length > 0 && LAYERS.map(layer => {
      const checks = artifact.checks.filter(c => c.layer === layer)
      if (!checks.length) return null
      return <details key={layer}><summary>{LAYER_LABELS[layer]} checks ({checks.length})</summary>
        <ul className="eval-checks">{checks.map((c, i) => <li key={`${c.id}-${i}`}>
          <p><strong>{c.title}</strong> · {STATUS_LABELS[c.status]} · {c.status === 'skip' ? 'not scored' : `${(c.score * 100).toFixed(0)} / 100`}</p>
          {c.detail && <p>{c.detail}</p>}
          {c.evidence.length > 0 && <ul aria-label="Evidence">{c.evidence.map((e, j) => <li key={j}>{e}</li>)}</ul>}
          {c.guidance && <p>Guidance: {c.guidance}</p>}
        </li>)}</ul>
      </details>
    })}
    {artifact?.usage && <p className="eval-muted">Judge usage: {artifact.usage.calls} call(s), {artifact.usage.promptTokens.toLocaleString()} prompt
      and {artifact.usage.completionTokens.toLocaleString()} completion tokens.</p>}
  </div>
}
