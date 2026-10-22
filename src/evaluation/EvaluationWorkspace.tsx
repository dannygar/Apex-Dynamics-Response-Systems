import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import { auth, identityConfigured } from './auth'
import { canWrite, captureInvitationHint, clearInvitationHint, READ_ONLY_LABEL, roleLabel } from './access'
import { Administration } from './Administration'
import type { DocumentVersion, Me, Policy, Project, Readiness, Scan, Submission, SubmissionDetail, Team } from './contracts'
import { percent } from './format'
import { GetStarted } from './Join'
import { bundleFormData, markdownRecommended, prepareDtBundle, prepareSourceBundle, singleFormData, type PreparedBundle } from './upload'
import { Results } from './Results'
import { Settings } from './Settings'
import './evaluation.css'

const message = (error: unknown) => error instanceof Error ? error.message : 'The operation failed.'
type View = 'workspace' | 'admin' | 'join'

export default function EvaluationWorkspace() {
  const [account, setAccount] = useState<string | null>(null)
  const [me, setMe] = useState<Me | null>(null)
  const [teams, setTeams] = useState<Team[]>([])
  const [teamId, setTeamId] = useState('')
  const [view, setView] = useState<View>('workspace')
  const [hint] = useState(() => identityConfigured ? captureInvitationHint() : null)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const report = useCallback((value: unknown) => setError(message(value)), [])
  const loadAccess = useCallback(async () => {
    const [identity, authorized] = await Promise.all([api.me(), api.teams()])
    setMe(identity); setTeams(authorized)
    setTeamId(current => authorized.some(t => t.id === current) ? current : '')
    return identity
  }, [])
  useEffect(() => {
    if (!identityConfigured) return
    let active = true
    auth.account().then(async name => {
      if (!active) return
      setAccount(name)
      if (name) {
        const identity = await loadAccess()
        // The invitation link only routes to the Join screen; acceptance is authorized by the API.
        if (active && hint && identity.pendingInvitationCount > 0) setView('join')
        else if (hint) clearInvitationHint()
      }
    }).catch(report)
    return () => { active = false }
  }, [report, loadAccess, hint])
  const team = teams.find(t => t.id === teamId)
  const member = Boolean(me && (me.memberships.length > 0 || me.isPlatformAdmin))
  // Signed-in users without a team get the self-service Get started screen; members open it on demand.
  const joining = Boolean(me && (view === 'join' || !member))
  return <main className="evaluation">
    <header className="eval-heading">
      <div><p className="eval-eyebrow">Apex Dynamics · Document assurance</p><h1>Evaluation workspace</h1>
        <p>Manual scans · immutable evidence · server-owned acceptance</p></div>
      {account && <div>
        {me?.isPlatformAdmin && !joining && <nav aria-label="Workspace sections" className="eval-tabs">
          <button aria-pressed={view === 'workspace'} onClick={() => setView('workspace')}>Teams</button>
          <button aria-pressed={view === 'admin'} onClick={() => setView('admin')}>Administration</button>
        </nav>}
        <span>{account}</span><button onClick={() => { void auth.signOut().catch(report) }}>Sign out</button>
      </div>}
    </header>
    {error && <div className="eval-error" role="alert">{error}<button onClick={() => setError('')}>Dismiss error</button></div>}
    {!identityConfigured ? <section className="eval-card">
      <h2>Sign-in configuration required</h2>
      <p>This workspace is connected to the real Evaluation API; there are no demonstration scores or sample submissions.</p>
      <p>Configure <code>VITE_ENTRA_TENANT_ID</code>, <code>VITE_ENTRA_CLIENT_ID</code> and <code>VITE_ENTRA_API_SCOPE</code>,
        register this app's redirect URI and start the API. See the Evaluation setup instructions in README.</p>
      <p>Live judging uses the agent-eval judge sidecar registered by the Evaluation worker. Without a recent registration,
        scans stay blocked and no score is shown.</p>
    </section> : !account ? <section className="eval-card">
      <h2>Use your organization identity</h2><p>Access is limited to teams you belong to. After signing in you can join a team
        or create one. If you received an invitation, sign in with the invited account.</p>
      <button disabled={busy} onClick={async () => {
        setBusy(true); setError('')
        try { await auth.signIn() } catch (value) { report(value); setBusy(false) }
      }}>{busy ? 'Opening sign-in…' : 'Sign in with Microsoft Entra'}</button>
    </section> : !me ? <p role="status">Checking your team access…</p> : joining ?
      <GetStarted hint={hint} hasInvitations={me.pendingInvitationCount > 0}
        onCancel={member ? () => { clearInvitationHint(); setView('workspace') } : undefined} onJoined={async joined => {
          clearInvitationHint()
          await loadAccess()
          setTeamId(joined.teamId); setView('workspace'); setError('')
          setNotice(joined.created
            ? `You created ${joined.teamName} and are its Administrator. Add projects, or set thresholds and invite people under Team settings.`
            : `You joined ${joined.teamName} as ${joined.role}.`)
        }} /> : view === 'admin' && me.isPlatformAdmin ? <Administration me={me} onTeamsChanged={loadAccess} /> : <>
      <p role="status" aria-live="polite">{notice}</p>
      {me.pendingInvitationCount > 0 && <p className="eval-warning">You have {me.pendingInvitationCount} pending
        invitation{me.pendingInvitationCount === 1 ? '' : 's'}. <button onClick={() => setView('join')}>Join another team</button></p>}
      <label>Authorized team<select value={teamId} onChange={event => { setTeamId(event.target.value); setError(''); setNotice('') }}>
        <option value="">Select a team</option>{teams.map(t => <option key={t.id} value={t.id}>{t.name} · {roleLabel(t.role)}</option>)}
      </select></label>
      <button onClick={() => { setNotice(''); setView('join') }}>Join or create a team</button>
      {me.isPlatformAdmin && <p className="eval-hint">As a platform administrator you can open every team. Teams marked
        “{READ_ONLY_LABEL}” are read-only until you join them. Once you join, you can create projects and submissions,
        upload documents, run scans, and set thresholds and members under Team settings. Teams you create make you their Administrator.</p>}
      {!teams.length && <p role="status">No teams yet. Use “Join or create a team”.</p>}
      {team && <TeamWorkspace key={`${team.id}:${team.role}`} team={team} report={report}
        onJoin={me.isPlatformAdmin && !team.isPlatformAdmin && team.role === 'PlatformAdmin' ? async () => {
          const joined = await api.joinTeam(team.id)
          await loadAccess(); setError('')
          setNotice(`You joined ${joined.teamName} as ${joined.role}. You can now create projects and submissions, run scans, and set thresholds and members under Team settings.`)
        } : undefined}
        onLeave={!team.isPlatformAdmin && team.role !== 'PlatformAdmin' ? async () => {
          await api.leaveTeam(team.id)
          await loadAccess(); setError('')
          setNotice(me.isPlatformAdmin ? `You left ${team.name}. It is read-only for you again.` : `You left ${team.name}.`)
        } : undefined}
        leaveConsequence={me.isPlatformAdmin ? 'it becomes read-only for you' : 'you lose access until you join it again'} />}
    </>}
  </main>
}

function TeamWorkspace({ team, report, onJoin, onLeave, leaveConsequence }: {
  team: Team; report: (error: unknown) => void; onJoin?: () => Promise<void>; onLeave?: () => Promise<void>; leaveConsequence: string
}) {
  const [readiness, setReadiness] = useState<Readiness | null>(null)
  const [projects, setProjects] = useState<Project[]>([])
  const [projectId, setProjectId] = useState('')
  const [settings, setSettings] = useState(false)
  const [busy, setBusy] = useState(false)
  const [confirmLeave, setConfirmLeave] = useState(false)
  const run = async (action: () => Promise<void>) => {
    setBusy(true)
    try { await action() } catch (error) { report(error) } finally { setBusy(false); setConfirmLeave(false) }
  }
  useEffect(() => {
    let active = true
    api.projects(team.id).then(p => { if (active) setProjects(p) }).catch(report)
    const load = () => { api.readiness(team.id).then(r => { if (active) setReadiness(r) }).catch(report) }
    load()
    // Judge registration is a worker heartbeat; refresh so the banner reflects its current freshness.
    const timer = setInterval(load, 60000)
    return () => { active = false; clearInterval(timer) }
  }, [team.id, report])
  return <>
    <div className="eval-toolbar"><strong>{team.name} / {roleLabel(team.role)}</strong>
      <button aria-pressed={!settings} onClick={() => setSettings(false)}>Submissions</button>
      <button aria-pressed={settings} onClick={() => setSettings(true)}>Team settings</button>
      {onLeave && !confirmLeave &&
        <button disabled={busy} onClick={() => setConfirmLeave(true)}>Leave team</button>}
    </div>
    {onLeave && confirmLeave && <div className="eval-warning" role="alertdialog" aria-labelledby="leave-heading">
      <p id="leave-heading"><strong>Leave {team.name}?</strong> Your projects, submissions and scans stay with the team;
        {` ${leaveConsequence}`}. The team's last Administrator can't leave.</p>
      <button disabled={busy} onClick={() => run(onLeave)}>{busy ? 'Leaving…' : 'Leave team'}</button>
      <button disabled={busy} onClick={() => setConfirmLeave(false)}>Stay in team</button>
    </div>}
    {team.role === 'PlatformAdmin' && <div className="eval-warning" role="note"><p><strong>{READ_ONLY_LABEL}:</strong> you can review this team's
      projects, documents, policy and results, but changes require a membership in the team.</p>
      {onJoin && <button disabled={busy} onClick={() => run(onJoin)}>{busy ? 'Joining…' : 'Join this team as Member'}</button>}
    </div>}
    <ReadinessBanner readiness={readiness} />
    {settings ? <Settings team={team} readiness={readiness} report={report} /> : <>
      <label>Project<select value={projectId} onChange={event => setProjectId(event.target.value)}>
        <option value="">Select a project</option>{projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
      </select></label>
      {canWrite(team) && <details className="eval-card"><summary>Create a project</summary>
        <form onSubmit={async event => {
          event.preventDefault(); const form = event.currentTarget
          const name = String(new FormData(form).get('name')); setBusy(true)
          try { const project = await api.createProject(team.id, name); setProjects(await api.projects(team.id)); setProjectId(project.id); form.reset() }
          catch (error) { report(error) } finally { setBusy(false) }
        }}><label>Project name<input name="name" required maxLength={200} /></label><button disabled={busy}>Create project</button></form>
      </details>}
      {projectId && <ProjectWorkspace key={projectId} team={team} projectId={projectId} readiness={readiness} report={report} />}
    </>}
  </>
}

function ReadinessBanner({ readiness }: { readiness: Readiness | null }) {
  const judge = readiness?.judge
  return <section className={`eval-card eval-readiness${readiness?.ready ? ' eval-ready' : ''}`} aria-label="Judge readiness">
    <h2>Judge readiness</h2>
    <div role="status" aria-live="polite">
      {!readiness ? <p>Checking API prerequisites…</p> : readiness.ready ?
        <p><strong>Ready:</strong> the AI judge is registered by the Evaluation worker.</p> :
        <p><strong>Blocked:</strong> live scanning is unavailable. No scores will be fabricated.</p>}
    </div>
    {readiness && !readiness.ready && <ul>{readiness.blockers.map(b => <li key={b}>{b}</li>)}</ul>}
    {judge && <dl className="eval-facts">
      <dt>Judge version</dt><dd className="eval-mono">{judge.version}</dd>
      <dt>Evaluator</dt><dd>{judge.evaluator} · sidecar {judge.sidecar}</dd>
      <dt>Model deployment</dt><dd>{judge.judge.deployment} · API {judge.judge.apiVersion}</dd>
      <dt>Judge samples</dt><dd>{judge.judge.samples}</dd>
      <dt>Case reference</dt><dd>{judge.reference.id} · {judge.reference.sha}</dd>
      <dt>Layer weights</dt><dd>Structure {percent(judge.layerWeights.structure)} · Reference {percent(judge.layerWeights.reference)} ·
        AI judge {percent(judge.layerWeights.judge)}</dd>
      <dt>Last heartbeat</dt><dd>{readiness?.lastHeartbeat ? new Date(readiness.lastHeartbeat).toLocaleString() : 'Never'}</dd>
    </dl>}
    {readiness && <details><summary>Ingestion notes</summary><p>{readiness.scanner}</p><p>{readiness.pdf}</p><p>{readiness.imports}</p></details>}
  </section>
}

function ProjectWorkspace({ team, projectId, readiness, report }: {
  team: Team; projectId: string; readiness: Readiness | null; report: (error: unknown) => void
}) {
  const [submissions, setSubmissions] = useState<Submission[]>([])
  const [submissionId, setSubmissionId] = useState('')
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    let active = true
    api.submissions(team.id, projectId).then(s => { if (active) setSubmissions(s) }).catch(report)
    return () => { active = false }
  }, [team.id, projectId, report])
  return <div className="eval-columns">
    <aside className="eval-card">
      <h2>Submissions</h2>
      {!submissions.length && <p>No submissions yet.</p>}
      <ul className="eval-list">{submissions.map(s => <li key={s.id}><button aria-pressed={s.id === submissionId} onClick={() => setSubmissionId(s.id)}>
        {s.name}</button></li>)}</ul>
      {canWrite(team) && <form onSubmit={async event => {
        event.preventDefault(); const form = event.currentTarget; const data = new FormData(form); setBusy(true)
        try {
          const created = await api.createSubmission(team.id, projectId, String(data.get('name')), String(data.get('description')))
          setSubmissions(await api.submissions(team.id, projectId)); setSubmissionId(created.id); form.reset()
        } catch (error) { report(error) } finally { setBusy(false) }
      }}>
        <h3>Create submission</h3>
        <label>Submission name<input name="name" required maxLength={200} /></label>
        <label>Description<textarea name="description" maxLength={4000} /></label>
        <button disabled={busy}>{busy ? 'Creating…' : 'Create submission'}</button>
      </form>}
    </aside>
    <div>{submissionId ? <SubmissionWorkspace key={submissionId} team={team} submissionId={submissionId} readiness={readiness} report={report} /> :
      <section className="eval-card"><h2>Select a submission</h2><p>Choose a submission to view immutable document versions and scan history.</p></section>}</div>
  </div>
}

interface Bundle { id: string; versions: DocumentVersion[]; createdAt: string; ready: boolean; status: string }
function bundlesOf(documents: DocumentVersion[], role: 'DT' | 'Source'): Bundle[] {
  const groups = new Map<string, DocumentVersion[]>()
  for (const d of documents.filter(d => d.role === role)) groups.set(d.bundleId, [...(groups.get(d.bundleId) ?? []), d])
  return [...groups.entries()].map(([id, versions]) => {
    const ready = versions.every(v => v.status === 'Ready')
    const rejected = versions.filter(v => v.status === 'Rejected').length
    const waiting = versions.filter(v => v.status === 'Quarantined').length
    return {
      id, ready, versions: [...versions].sort((a, b) => a.relativePath.localeCompare(b.relativePath)),
      createdAt: versions.map(v => v.createdAt).sort()[0],
      status: ready ? 'Ready' : rejected ? `${rejected} file(s) rejected${role === 'DT' ? ' — upload a corrected bundle' : ''}`
        : `${waiting} file(s) awaiting malware scan`,
    }
  }).sort((a, b) => b.createdAt.localeCompare(a.createdAt))
}
const dtBundles = (documents: DocumentVersion[]) => bundlesOf(documents, 'DT')

// Source evidence is chosen per file; an upload's group checkbox toggles all of its Ready files.
function EvidencePicker({ documents, selected, disabled, onChange }: {
  documents: DocumentVersion[]; selected: string[]; disabled: boolean; onChange: (evidence: string[]) => void
}) {
  const uploads = bundlesOf(documents, 'Source')
  const ready = uploads.flatMap(u => u.versions.filter(v => v.status === 'Ready').map(v => v.id))
  const toggle = (ids: string[], on: boolean) =>
    onChange(on ? [...new Set([...selected, ...ids])] : selected.filter(id => !ids.includes(id)))
  const fileRow = (v: DocumentVersion, label: string) => <label key={v.id}>
    <input type="checkbox" checked={selected.includes(v.id)} disabled={disabled || v.status !== 'Ready'}
      onChange={event => toggle([v.id], event.currentTarget.checked)} />
    <span className="eval-mono">{label}</span> · {v.status}{v.error ? ` — ${v.error}` : ''}</label>
  return <fieldset className="eval-select">
    <legend>Source evidence (optional, context only)</legend>
    <p>Source documents are snapshotted with the scan for traceability. They are never sent to the AI judge and are not required.
      Choose whole uploads or individual files.</p>
    {!uploads.length ? <p>No source documents uploaded.</p> : <p>
      {selected.length} of {ready.length} ready evidence file{ready.length === 1 ? '' : 's'} selected.{' '}
      <button type="button" disabled={disabled || !ready.length || ready.every(id => selected.includes(id))}
        onClick={() => toggle(ready, true)}>Select all evidence</button>
      <button type="button" disabled={disabled || !selected.length} onClick={() => onChange([])}>Clear evidence</button>
    </p>}
    {uploads.map(u => {
      const readyIds = u.versions.filter(v => v.status === 'Ready').map(v => v.id)
      const chosen = readyIds.filter(id => selected.includes(id)).length
      if (u.versions.length === 1) return <div className="eval-version" key={u.id}>
        {fileRow(u.versions[0], u.versions[0].relativePath || u.versions[0].fileName)}
        <span>{(u.versions[0].size / 1024).toFixed(1)} KB · {new Date(u.createdAt).toLocaleString()}</span>
      </div>
      return <div className="eval-version" key={u.id}>
        <label><input type="checkbox" checked={readyIds.length > 0 && chosen === readyIds.length} disabled={disabled || !readyIds.length}
          ref={element => { if (element) element.indeterminate = chosen > 0 && chosen < readyIds.length }}
          aria-label={`All ready files in the source upload of ${new Date(u.createdAt).toLocaleString()}`}
          onChange={event => toggle(readyIds, event.currentTarget.checked)} />
          Source upload of {u.versions.length} files · {new Date(u.createdAt).toLocaleString()}</label>
        <span>{chosen} selected · {u.status}</span>
        <details><summary>Choose files in this upload</summary>
          <div className="eval-files">{u.versions.map(v => fileRow(v, v.relativePath))}</div>
        </details>
      </div>
    })}
  </fieldset>
}

interface Selection { dt: string; brd: string; prd: string; evidence: string[] }
const emptySelection: Selection = { dt: '', brd: '', prd: '', evidence: [] }

function SubmissionWorkspace({ team, submissionId, readiness, report }: {
  team: Team; submissionId: string; readiness: Readiness | null; report: (error: unknown) => void
}) {
  const [detail, setDetail] = useState<SubmissionDetail | null>(null)
  const [policy, setPolicy] = useState<Policy | null>(null)
  const [selection, setSelection] = useState<Selection>(emptySelection)
  const [review, setReview] = useState(false)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [runId, setRunId] = useState('')
  const [compareId, setCompareId] = useState('')
  const [scan, setScan] = useState<Scan | null>(null)
  const [comparison, setComparison] = useState<Scan | null>(null)
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID())
  const writable = canWrite(team)
  const refresh = useCallback(async () => {
    const [s, p] = await Promise.all([api.submission(team.id, submissionId), api.policy(team.id)])
    setDetail(s); setPolicy(p)
  }, [team.id, submissionId])
  useEffect(() => {
    let active = true
    api.submission(team.id, submissionId).then(s => { if (active) setDetail(s) }).catch(report)
    api.policy(team.id).then(p => { if (active) setPolicy(p) }).catch(report)
    return () => { active = false }
  }, [team.id, submissionId, report])
  const pending = Boolean(detail?.documents.some(d => d.status === 'Quarantined') ||
    detail?.scans.some(s => ['Queued', 'Preparing', 'Evaluating'].includes(s.status)))
  useEffect(() => {
    if (!pending) return
    const timer = setInterval(() => { void refresh().catch(report) }, 6000)
    return () => clearInterval(timer)
  }, [pending, refresh, report])
  // A result request is scoped to the selected history row, including when polling.
  useEffect(() => {
    if (!runId) return
    let active = true
    const load = () => { void api.scan(team.id, runId).then(s => { if (active) setScan(s) }).catch(report) }
    load()
    const timer = pending ? setInterval(load, 6000) : undefined
    return () => { active = false; clearInterval(timer) }
  }, [team.id, runId, pending, report])
  useEffect(() => {
    if (!compareId) return
    let active = true
    api.scan(team.id, compareId).then(s => { if (active) setComparison(s) }).catch(report)
    return () => { active = false }
  }, [team.id, compareId, report])
  if (!detail) return <p role="status">Loading submission…</p>
  const bundles = dtBundles(detail.documents)
  const byRole = (role: DocumentVersion['role']) => detail.documents.filter(d => d.role === role)
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
  const dtBundle = bundles.find(b => b.id === selection.dt)
  const brd = detail.documents.find(d => d.id === selection.brd && d.role === 'BRD')
  const prd = detail.documents.find(d => d.id === selection.prd && d.role === 'PRD')
  const evidence = detail.documents.filter(d => selection.evidence.includes(d.id) && d.role === 'Source')
  const targets = [...(dtBundle?.versions ?? []), ...(brd ? [brd] : []), ...(prd ? [prd] : [])]
  const blockers = [
    !writable && (team.role === 'PlatformAdmin' ? 'Read-only platform administrator access cannot start scans.' : 'Reviewers cannot start scans.'),
    !readiness?.ready && 'The AI judge is not registered (see Judge readiness).',
    !policy?.thresholds && 'An administrator must set DT, BRD and PRD thresholds.',
    !targets.length && 'Select a DT bundle, a BRD version and/or a PRD version.',
    targets.some(t => t.status !== 'Ready') && 'Every selected file must be Ready.',
  ].filter((b): b is string => Boolean(b))
  const canScan = blockers.length === 0
  const choose = (change: Partial<Selection>) => {
    setSelection(current => ({ ...current, ...change })); setReview(false); setIdempotencyKey(crypto.randomUUID())
  }
  const download = (d: DocumentVersion) => <button disabled={d.status !== 'Ready'} aria-label={`Download ${d.relativePath}`}
    onClick={() => { void api.download(team.id, d.id, d.fileName).catch(report) }}>Download</button>
  const versionInfo = (d: DocumentVersion) => <>
    <span>{d.status} · {(d.size / 1024).toFixed(1)} KB · {new Date(d.createdAt).toLocaleString()}</span>
    <details><summary>Version details</summary><p className="eval-mono">{d.id}<br />SHA-256 {d.hash}</p></details>
    {d.error && <p>{d.error}</p>}
    {download(d)}
  </>
  const single = (role: 'BRD' | 'PRD') => <fieldset className="eval-select">
    <legend>{role} version</legend>
    <label><input type="radio" name={`${role}-${submissionId}`} checked={!selection[role === 'BRD' ? 'brd' : 'prd']}
      disabled={!writable || busy} onChange={() => choose(role === 'BRD' ? { brd: '' } : { prd: '' })} />Do not scan {role}</label>
    {!byRole(role).length && <p>No versions uploaded.</p>}
    {byRole(role).map(d => <div className="eval-version" key={d.id}>
      <label><input type="radio" name={`${role}-${submissionId}`} checked={selection[role === 'BRD' ? 'brd' : 'prd'] === d.id}
        disabled={!writable || d.status !== 'Ready' || busy} onChange={() => choose(role === 'BRD' ? { brd: d.id } : { prd: d.id })} />
        {d.fileName}</label>
      {versionInfo(d)}
    </div>)}
  </fieldset>
  return <>
    <section className="eval-card"><h2>{detail.submission.name}</h2><p>{detail.submission.description}</p>
      <p role="status" aria-live="polite">{notice}</p>
      <h3>Immutable versions and scan selection</h3>
      <p>Choose at most one DT bundle, one BRD version and one PRD version. Uploading a revision never alters an earlier scan.</p>
      <fieldset className="eval-select">
        <legend>DT bundle (Markdown files)</legend>
        <label><input type="radio" name={`DT-${submissionId}`} checked={!selection.dt} disabled={!writable || busy}
          onChange={() => choose({ dt: '' })} />Do not scan DT</label>
        {!bundles.length && <p>No DT bundles uploaded.</p>}
        {bundles.map(b => <div className="eval-version" key={b.id}>
          <label><input type="radio" name={`DT-${submissionId}`} checked={selection.dt === b.id} disabled={!writable || !b.ready || busy}
            onChange={() => choose({ dt: b.id })} />
            DT bundle of {b.versions.length} file{b.versions.length === 1 ? '' : 's'} · {new Date(b.createdAt).toLocaleString()}</label>
          <span>{b.status}</span>
          <details><summary>Files in this bundle</summary>
            <ul className="eval-files">{b.versions.map(v => <li key={v.id}>
              <span className="eval-mono">{v.relativePath}</span> · {v.status}{v.error ? ` — ${v.error}` : ''} {download(v)}
            </li>)}</ul>
          </details>
        </div>)}
      </fieldset>
      {single('BRD')}
      {single('PRD')}
      <EvidencePicker documents={detail.documents} selected={selection.evidence} disabled={!writable || busy}
        onChange={evidence => choose({ evidence })} />
      {writable && <UploadForm team={team} submissionId={submissionId} busy={busy} setBusy={setBusy} setNotice={setNotice}
        report={report} onUploaded={refresh} />}
      <details><summary>Import from a link — configuration required</summary>
        <p>SharePoint/OneDrive, Azure Blob and public HTTPS imports are blocked pending approved connector authorization and security controls.
          URLs are not fetched or saved. Other providers are not implemented.</p>
      </details>
    </section>
    <section className="eval-card">
      <h2>Manual scan review</h2>
      <p>No automatic or scheduled scans. Each selected artifact is scored by the AI judge on a 0–100 scale and passes at or above its threshold.</p>
      <p>Policy: {policy?.thresholds ? `v${policy.version} — DT ${policy.thresholds.dt}, BRD ${policy.thresholds.brd}, PRD ${policy.thresholds.prd}` : 'Not configured'}</p>
      {blockers.length > 0 && <ul aria-label="Why scanning is unavailable">{blockers.map(b => <li key={b}>{b}</li>)}</ul>}
      <button disabled={!canScan || busy} onClick={() => setReview(true)}>Review selection</button>
      {review && <div>
        <h3>Confirm immutable inputs</h3>
        <ul>
          {dtBundle && <li>DT bundle: {dtBundle.versions.length} Markdown file(s)</li>}
          {brd && <li>BRD: {brd.fileName} · {brd.id}</li>}
          {prd && <li>PRD: {prd.fileName} · {prd.id}</li>}
          {evidence.length > 0 && <li>Source evidence (not sent to the judge): {evidence.length} file{evidence.length === 1 ? '' : 's'}
            <ul className="eval-files eval-mono">{evidence.map(e => <li key={e.id}>{e.relativePath || e.fileName}</li>)}</ul></li>}
        </ul>
        <p>The server rechecks permissions, ready versions, the judge registration and policy, then pins snapshots. No browser-calculated verdict is trusted.</p>
        <button disabled={!canScan || busy} onClick={async () => {
          setBusy(true); setNotice('Requesting scan…')
          try {
            const run = await api.startScan(team.id, submissionId, targets.map(d => d.id), evidence.map(d => d.id), idempotencyKey)
            setScan(null); setRunId(run.id); setReview(false); setNotice('Scan queued. AI judge calls can take several minutes.'); await refresh()
            setIdempotencyKey(crypto.randomUUID())
          } catch (error) { setNotice('Scan not confirmed. Retry uses the same idempotency key.'); report(error) }
          finally { setBusy(false) }
        }}>Run scan</button>
        <button onClick={() => setReview(false)}>Cancel review</button>
      </div>}
    </section>
    <section className="eval-card">
      <h2>Scan history and comparison</h2>
      {!detail.scans.length && <p>No scans requested. No results are available.</p>}
      <ul>{[...detail.scans].sort((a, b) => b.createdAt.localeCompare(a.createdAt)).map(s => <li key={s.id}>
        <button aria-pressed={s.id === runId} onClick={() => { setScan(null); setRunId(s.id) }}>
          {new Date(s.createdAt).toLocaleString()} · {s.status} · {s.outcome || 'Inconclusive'}</button>
      </li>)}</ul>
      {runId && <label>Compare with another run<select value={compareId} onChange={event => { setCompareId(event.target.value); setComparison(null) }}>
        <option value="">No comparison</option>{detail.scans.filter(s => s.id !== runId).map(s => <option key={s.id} value={s.id}>
          {new Date(s.createdAt).toLocaleString()} · {s.outcome}</option>)}
      </select></label>}
    </section>
    <div className={compareId ? 'eval-comparison' : ''}>
      {scan && <Results scan={scan} documents={detail.documents} />}
      {comparison && compareId && <Results scan={comparison} documents={detail.documents} />}
    </div>
  </>
}

function UploadForm({ team, submissionId, busy, setBusy, setNotice, report, onUploaded }: {
  team: Team; submissionId: string; busy: boolean; setBusy: (busy: boolean) => void; setNotice: (notice: string) => void
  report: (error: unknown) => void; onUploaded: () => Promise<void>
}) {
  const [role, setRole] = useState<'DT' | 'BRD' | 'PRD' | 'Source'>('DT')
  const [bundle, setBundle] = useState<PreparedBundle | null>(null)
  const [file, setFile] = useState<File | null>(null)
  const multi = role === 'DT' || role === 'Source'
  const chooseBundle = (files: FileList | null) =>
    setBundle(files?.length ? (role === 'DT' ? prepareDtBundle : prepareSourceBundle)([...files]) : null)
  const ready = multi ? Boolean(bundle?.files.length && !bundle.errors.length) : Boolean(file)
  const folderPicker = (element: HTMLInputElement | null) => { element?.setAttribute('webkitdirectory', ''); element?.setAttribute('directory', '') }
  const preview = bundle && <div aria-live="polite">
    <p>{bundle.files.length} file(s) selected{bundle.skipped.length ? `; ${bundle.skipped.length} unsupported or hidden file(s) skipped` : ''}.</p>
    {bundle.errors.length > 0 && <ul role="alert">{bundle.errors.map(e => <li key={e}>{e}</li>)}</ul>}
    {bundle.files.length > 0 && <ul className="eval-files eval-mono" aria-label="Relative paths to upload">
      {bundle.files.map(f => <li key={f.path}>{f.path}</li>)}</ul>}
  </div>
  return <form onSubmit={async event => {
    event.preventDefault(); const form = event.currentTarget
    if (!ready) return
    const body = multi ? bundleFormData(role, bundle!.files) : singleFormData(role, file!)
    setBusy(true); setNotice('Uploading to private quarantine…')
    try {
      const result = await api.upload(team.id, submissionId, body)
      await onUploaded(); form.reset(); setBundle(null); setFile(null)
      setNotice(`Uploaded ${result.versions.length} file(s). Awaiting malware scanning and extraction; versions cannot be selected until Ready.`)
    } catch (error) { setNotice('Upload did not complete.'); report(error) } finally { setBusy(false) }
  }}>
    <h3>Upload a new version</h3>
    <label>Document type<select value={role} onChange={event => {
      setRole(event.target.value as typeof role); setBundle(null); setFile(null)
    }}>
      <option value="DT">DT bundle (Markdown folder or files)</option><option value="BRD">BRD</option>
      <option value="PRD">PRD</option><option value="Source">Source evidence (folder or files, context only)</option>
    </select></label>
    {role === 'DT' ? <>
      <p>Select the Design Thinking output folder (for example with <code>method-01-scope/…</code> sub-folders) or several Markdown files.
        Relative paths are kept; non-Markdown files are skipped. Up to 300 files, 20 MB each.</p>
      <label>Choose a DT folder<input type="file" multiple onChange={event => chooseBundle(event.currentTarget.files)} ref={folderPicker} /></label>
      <label>Or choose Markdown files<input type="file" multiple accept=".md,text/markdown" onChange={event => chooseBundle(event.currentTarget.files)} /></label>
      {preview}
    </> : role === 'Source' ? <>
      <p>Source documents are context-only evidence: snapshotted with a scan but never sent to the AI judge. Select a folder
        (sub-folders and relative paths are kept) or several files. PDF, DOCX, Markdown and text files are uploaded; other files are skipped.
        Up to 300 files, 20 MB each, 100 MB per submission. You choose which uploaded files to attach to each scan.</p>
      <label>Choose a source folder<input type="file" multiple onChange={event => chooseBundle(event.currentTarget.files)} ref={folderPicker} /></label>
      <label>Or choose source files<input type="file" multiple accept=".pdf,.docx,.md,.txt" onChange={event => chooseBundle(event.currentTarget.files)} /></label>
      {preview}
    </> : <>
      <p className="eval-hint">Markdown (.md) is recommended: structural checks rely on Markdown headings and tables.
        PDF, DOCX and TXT are accepted and their extracted text is judged, but headings and tables may be lost.</p>
      <label>PDF, DOCX, Markdown or text<input type="file" accept=".pdf,.docx,.md,.txt" required
        onChange={event => setFile(event.currentTarget.files?.[0] ?? null)} /></label>
      {file && markdownRecommended(file.name) &&
        <p className="eval-warning">{file.name} is not Markdown; structural scores may be lower than for a Markdown export.</p>}
    </>}
    <p>All uploads stay quarantined until the malware scanner reports them clean. Encrypted and image-only PDFs are unsupported.</p>
    <button disabled={busy || !ready}>{busy ? 'Working…' : 'Upload to quarantine'}</button>
  </form>
}