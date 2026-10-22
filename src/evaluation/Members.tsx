import { useEffect, useId, useState } from 'react'
import { memberName } from './access'
import type { Member, TeamRole } from './contracts'

const ROLES: TeamRole[] = ['Administrator', 'Member', 'Reviewer']
const message = (error: unknown) => error instanceof Error ? error.message : 'The operation failed.'

export interface MemberOperations {
  load: () => Promise<Member[]>
  save: (subject: string, role: TeamRole) => Promise<unknown>
  remove: (subject: string) => Promise<unknown>
}

// Shared by team settings (team administrators) and the platform console (any team). The server enforces the
// last-administrator and platform-team guards; the UI explains them before a request is made.
export function MemberManager({ teamName, operations, keepOneMember = false }: {
  teamName: string; operations: MemberOperations; keepOneMember?: boolean
}) {
  const [members, setMembers] = useState<Member[] | null>(null)
  const [drafts, setDrafts] = useState<Record<string, TeamRole>>({})
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const hint = useId()
  useEffect(() => {
    let active = true
    operations.load().then(m => { if (active) { setMembers(m); setDrafts({}) } })
      .catch(e => { if (active) setError(message(e)) })
    return () => { active = false }
  }, [operations])
  const run = async (action: () => Promise<unknown>, done: string) => {
    setBusy(true); setNotice(''); setError('')
    try { await action(); setMembers(await operations.load()); setDrafts({}); setNotice(done) }
    catch (e) { setError(message(e)) } finally { setBusy(false) }
  }
  const administrators = members?.filter(m => m.role === 'Administrator').length ?? 0
  return <div className="eval-members">
    <h3>Members of {teamName}</h3>
    {error && <p className="eval-error" role="alert">{error}</p>}
    {!members ? !error && <p role="status">Loading members…</p> : !members.length ? <p>No members yet. Add the first administrator below.</p> :
      <table className="eval-table">
        <caption>Members of {teamName}</caption>
        <thead><tr><th scope="col">Member</th><th scope="col">Entra object ID</th><th scope="col">Role</th><th scope="col">Actions</th></tr></thead>
        <tbody>{members.map(m => {
          const name = memberName(m)
          const draft = drafts[m.subject] ?? m.role
          const lastAdministrator = m.role === 'Administrator' && administrators <= 1
          const lastMember = keepOneMember && members.length <= 1
          return <tr key={m.subject}>
            <td>{name}{m.email && m.displayName ? <><br /><span className="eval-muted">{m.email}</span></> : null}</td>
            <td className="eval-mono">{m.subject}</td>
            <td><label className="eval-inline">Role for {name}<select value={draft} disabled={busy}
              onChange={event => setDrafts(d => ({ ...d, [m.subject]: event.target.value as TeamRole }))}>
              {ROLES.map(r => <option key={r}>{r}</option>)}
            </select></label>
              {lastAdministrator && <span className="eval-hint">Last administrator: add another administrator before changing or removing this one.</span>}
              {lastMember && !lastAdministrator && <span className="eval-hint">The platform administration team must keep at least one member.</span>}
            </td>
            <td>
              <button aria-label={`Change role for ${name}`} disabled={busy || draft === m.role || (lastAdministrator && draft !== 'Administrator')}
                onClick={() => run(() => operations.save(m.subject, draft), `${name} is now ${draft}.`)}>Change role</button>
              <button aria-label={`Remove ${name}`} disabled={busy || lastAdministrator || lastMember}
                onClick={() => run(() => operations.remove(m.subject), `${name} was removed from ${teamName}.`)}>Remove</button>
            </td>
          </tr>
        })}</tbody>
      </table>}
    <form onSubmit={async event => {
      event.preventDefault()
      const form = event.currentTarget
      const data = new FormData(form)
      const subject = String(data.get('subject')).trim()
      await run(() => operations.save(subject, String(data.get('role')) as TeamRole), 'Membership saved.')
      form.reset()
    }}>
      <h4>Add a member by Entra object ID</h4>
      <label>Entra object ID<input name="subject" required pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
        aria-describedby={hint} /></label>
      <p id={hint} className="eval-hint">The user's object ID (GUID) from Microsoft Entra ID. To add someone outside the directory, send an invitation.</p>
      <label>Role<select name="role" defaultValue="Member">{ROLES.map(r => <option key={r}>{r}</option>)}</select></label>
      <button disabled={busy}>Add member</button>
    </form>
    <p role="status" aria-live="polite">{notice}</p>
  </div>
}
