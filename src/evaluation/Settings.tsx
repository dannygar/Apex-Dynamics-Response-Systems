import { useEffect, useMemo, useState } from 'react'
import { canManage } from './access'
import { api } from './api'
import type { Policy, Readiness, Team } from './contracts'
import { thresholdProblem } from './format'
import { MemberManager, type MemberOperations } from './Members'
import { InvitePeople } from './People'

const ROLES = ['DT', 'BRD', 'PRD'] as const

export function Settings({ team, readiness, report }: {
  team: Team; readiness: Readiness | null; report: (error: unknown) => void
}) {
  const [policy, setPolicy] = useState<Policy | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [problems, setProblems] = useState<Partial<Record<typeof ROLES[number], string>>>({})
  const [membersVersion, setMembersVersion] = useState(0)
  const memberOperations = useMemo<MemberOperations>(() => ({
    load: () => api.members(team.id),
    save: (subject, role) => api.saveMember(team.id, subject, role),
    remove: subject => api.removeMember(team.id, subject),
  }), [team.id])
  useEffect(() => {
    let active = true
    api.policy(team.id).then(p => { if (active) setPolicy(p) }).catch(report)
    return () => { active = false }
  }, [team.id, report])
  const scale = readiness?.scale ?? { minimum: 0, maximum: 100 }
  return <section className="eval-card">
    <h2>Team settings</h2>
    <p>Scores use a 0–100 scale (the agent-eval score × 100). A DT bundle, BRD or PRD passes when its validated score is
      at or above its threshold; otherwise it fails. Threshold edits create a new policy version; existing scans keep their captured policy.</p>
    <p>Current policy: {policy?.thresholds ? `v${policy.version} — DT ${policy.thresholds.dt}, BRD ${policy.thresholds.brd}, PRD ${policy.thresholds.prd}` : 'Not configured — scans cannot start until all three thresholds are set.'}</p>
    {canManage(team) ? <>
      {team.role !== 'Administrator' && <p className="eval-hint">You manage this team's settings as a platform administrator.</p>}
      <form key={policy?.version} noValidate onSubmit={async event => {
        event.preventDefault()
        if (!policy) return
        const data = new FormData(event.currentTarget)
        const found = Object.fromEntries(ROLES.map(role => [role, thresholdProblem(String(data.get(role) ?? ''))]).filter(([, p]) => p))
        setProblems(found)
        if (Object.keys(found).length) { setNotice('Fix the highlighted thresholds.'); return }
        setBusy(true); setNotice('')
        try {
          await api.savePolicy(team.id, { expectedVersion: policy.version,
            dt: Number(data.get('DT')), brd: Number(data.get('BRD')), prd: Number(data.get('PRD')) })
          setPolicy(await api.policy(team.id)); setNotice('Policy version saved.')
        } catch (error) { report(error) } finally { setBusy(false) }
      }}>
        <fieldset disabled={busy || !policy}>
          <legend>Separate acceptance thresholds (0–100)</legend>
          {ROLES.map(role => <label key={role}>{role} threshold
            <input name={role} type="number" inputMode="decimal" step="0.1" required min={scale.minimum} max={scale.maximum}
              aria-invalid={Boolean(problems[role])} aria-describedby={problems[role] ? `threshold-${role}-error` : undefined}
              defaultValue={policy?.thresholds?.[role.toLowerCase() as 'dt' | 'brd' | 'prd']} />
            {problems[role] && <span id={`threshold-${role}-error`} className="eval-field-error">{problems[role]}</span>}
          </label>)}
          <button>Save new policy version</button>
        </fieldset>
      </form>
      <h3>Memberships</h3>
      {!team.isPlatformAdmin && <InvitePeople team={team} onAdded={() => setMembersVersion(v => v + 1)} />}
      <MemberManager key={membersVersion} teamName={team.name} operations={memberOperations} />
    </> : <p>{team.role === 'PlatformAdmin' ? 'Read-only (platform admin): join this team to set its thresholds and manage its members.' :
      'Only team administrators can edit thresholds or memberships.'}</p>}
    <p role="status" aria-live="polite">{notice}</p>
  </section>
}
