import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { api } from './api'
import { deliveryNotice as sharedDeliveryNotice, workspaceLink } from './access'
import type { AdminTeam, Invitation, InvitationResult, Me } from './contracts'
import { MemberManager, type MemberOperations } from './Members'
import { SuggestionEditor } from './People'
import { OutlookEmailButton } from './OutlookEmail'

const message = (error: unknown) => error instanceof Error ? error.message : 'The operation failed.'
const date = (value: string) => new Date(value).toLocaleDateString()
const plural = (count: number, noun: string) => `${count} ${noun}${count === 1 ? '' : 's'}`

// Platform administrator console. Visibility is a convenience: every /api/admin endpoint returns 404 to anyone
// who is not a member of the operator-flagged platform administration team.
export function Administration({ me, onTeamsChanged }: { me: Me; onTeamsChanged: () => Promise<unknown> | void }) {
  const [teams, setTeams] = useState<AdminTeam[] | null>(null)
  const [error, setError] = useState('')
  const [membersTeamId, setMembersTeamId] = useState('')
  const reload = useCallback(async () => {
    try { setTeams(await api.adminTeams()); setError('') } catch (e) { setError(message(e)) }
  }, [])
  useEffect(() => {
    let active = true
    api.adminTeams().then(t => { if (active) setTeams(t) }).catch(e => { if (active) setError(message(e)) })
    return () => { active = false }
  }, [])
  const changed = async () => { await reload(); await onTeamsChanged() }
  return <div className="eval-admin">
    <section className="eval-card">
      <h2>Administration</h2>
      <p>You are a platform administrator through membership of the platform administration team
        {me.memberships.find(m => m.isPlatformAdmin)?.teamName ? ` (${me.memberships.find(m => m.isPlatformAdmin)!.teamName})` : ''}.
        You can read every team in this tenant, create and permanently delete teams, manage any team's members and invite people.</p>
      {error && <p className="eval-error" role="alert">{error}</p>}
    </section>
    <TeamsSection teams={teams} onChanged={changed} onManage={id => {
      setMembersTeamId(id)
      document.getElementById('admin-members')?.scrollIntoView?.({ block: 'start' })
    }} />
    <MembersSection teams={teams} teamId={membersTeamId} setTeamId={setMembersTeamId} />
    <InvitationsSection teams={teams} />
  </div>
}

function TeamsSection({ teams, onChanged, onManage }: {
  teams: AdminTeam[] | null; onChanged: () => Promise<void>; onManage: (teamId: string) => void
}) {
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [deleting, setDeleting] = useState<AdminTeam | null>(null)
  const opener = useRef<HTMLButtonElement | null>(null)
  return <section className="eval-card" aria-labelledby="admin-teams">
    <h2 id="admin-teams">Teams</h2>
    {error && <p className="eval-error" role="alert">{error}</p>}
    {!teams ? <p role="status">Loading teams…</p> : <table className="eval-table">
      <caption>All teams in this tenant</caption>
      <thead><tr><th scope="col">Team</th><th scope="col">Members</th><th scope="col">Projects</th><th scope="col">Submissions</th>
        <th scope="col">Created</th><th scope="col">Actions</th></tr></thead>
      <tbody>{teams.map(t => <tr key={t.id}>
        <th scope="row">{t.name}{t.isPlatformAdmin && <> <span className="eval-badge">Platform administration team</span></>}</th>
        <td>{t.members}</td><td>{t.projects}</td><td>{t.submissions}</td><td>{date(t.createdAt)}</td>
        <td>
          <button aria-label={`Manage members of ${t.name}`} onClick={() => onManage(t.id)}>Manage members</button>
          <button aria-label={`Delete ${t.name}`} disabled={t.isPlatformAdmin || busy} aria-describedby={t.isPlatformAdmin ? 'admin-team-undeletable' : undefined}
            onClick={event => { opener.current = event.currentTarget; setDeleting(t) }}>Delete</button>
        </td>
      </tr>)}</tbody>
    </table>}
    {teams?.some(t => t.isPlatformAdmin) && <p id="admin-team-undeletable" className="eval-hint">The platform administration team cannot be deleted.</p>}
    <form onSubmit={async event => {
      event.preventDefault()
      const form = event.currentTarget
      const name = String(new FormData(form).get('name')).trim()
      setBusy(true); setNotice(''); setError('')
      try { const created = await api.createTeam(name); form.reset(); setNotice(`Team ${created.name} created. You are its Administrator; open it from Teams to add projects, or add members under Team members.`); await onChanged(); onManage(created.id) }
      catch (e) { setError(message(e)) } finally { setBusy(false) }
    }}>
      <h3>Create a team</h3>
      <label>Team name<input name="name" required maxLength={200} /></label>
      <button disabled={busy}>Create team</button>
    </form>
    <p role="status" aria-live="polite">{notice}</p>
    {deleting && <DeleteTeamDialog team={deleting} onCancel={() => { setDeleting(null); opener.current?.focus() }}
      onDeleted={async result => {
        setDeleting(null)
        setNotice(`Team ${deleting.name} was permanently deleted. ${plural(result.objectsDeleted, 'private file')} removed` +
          (result.objectDeleteFailures ? `; ${result.objectDeleteFailures} could not be removed and need operator cleanup.` : '.'))
        await onChanged()
      }} />}
  </section>
}

export function DeleteTeamDialog({ team, onCancel, onDeleted }: {
  team: AdminTeam; onCancel: () => void; onDeleted: (result: { objectsDeleted: number; objectDeleteFailures: number }) => Promise<void> | void
}) {
  const [typed, setTyped] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const input = useRef<HTMLInputElement>(null)
  const title = useId(); const description = useId()
  useEffect(() => { input.current?.focus() }, [])
  const exact = typed === team.name
  return <div className="eval-dialog-backdrop">
    <div className="eval-dialog eval-card" role="dialog" aria-modal="true" aria-labelledby={title} aria-describedby={description}
      onKeyDown={event => { if (event.key === 'Escape' && !busy) onCancel() }}>
      <h2 id={title}>Permanently delete {team.name}?</h2>
      <div id={description}>
        <p>This cannot be undone. The following will be permanently removed:</p>
        <ul>
          <li>{plural(team.projects, 'project')} and {plural(team.submissions, 'submission')}</li>
          <li>{plural(team.documents, 'document version')} and their private files</li>
          <li>{plural(team.scans, 'scan')} with their results and evidence snapshots</li>
          <li>All policy versions and the team's audit history</li>
          <li>{plural(team.members, 'membership')}</li>
        </ul>
        <p>Pending invitations that suggest this team will no longer suggest it. Teams with queued or running scans cannot be deleted.</p>
      </div>
      {error && <p className="eval-error" role="alert">{error}</p>}
      <form onSubmit={async event => {
        event.preventDefault()
        if (!exact) return
        setBusy(true); setError('')
        try { await onDeleted(await api.deleteTeam(team.id, typed)) }
        catch (e) { setError(message(e)); setBusy(false) }
      }}>
        <label>Type the team name <strong className="eval-mono">{team.name}</strong> to confirm
          <input ref={input} value={typed} onChange={event => setTyped(event.target.value)} autoComplete="off" spellCheck={false} /></label>
        <button disabled={!exact || busy}>{busy ? 'Deleting…' : 'Permanently delete team'}</button>
        <button type="button" disabled={busy} onClick={onCancel}>Cancel</button>
      </form>
    </div>
  </div>
}

function MembersSection({ teams, teamId, setTeamId }: { teams: AdminTeam[] | null; teamId: string; setTeamId: (id: string) => void }) {
  const team = teams?.find(t => t.id === teamId)
  const operations = useMemo<MemberOperations>(() => ({
    load: () => api.adminMembers(teamId),
    save: (subject, role) => api.adminSaveMember(teamId, subject, role),
    remove: subject => api.adminRemoveMember(teamId, subject),
  }), [teamId])
  return <section className="eval-card" id="admin-members" aria-labelledby="admin-members-heading">
    <h2 id="admin-members-heading">Team members</h2>
    <label>Team<select value={teamId} onChange={event => setTeamId(event.target.value)}>
      <option value="">Select a team</option>
      {teams?.map(t => <option key={t.id} value={t.id}>{t.name}{t.isPlatformAdmin ? ' (platform administration team)' : ''}</option>)}
    </select></label>
    {team ? <MemberManager key={team.id} teamName={team.name} operations={operations} keepOneMember={team.isPlatformAdmin} /> :
      <p>Select a team to list, add, change or remove its members. Every team keeps at least one administrator.</p>}
    {team?.isPlatformAdmin && <p className="eval-hint">Every member of this team, in any role, is a platform administrator.</p>}
  </section>
}

function deliveryNotice(result: InvitationResult) {
  return sharedDeliveryNotice({ email: result.invitation.email, sentAs: result.sentAs, note: result.note, reused: result.reused })
}

function InvitationsSection({ teams }: { teams: AdminTeam[] | null }) {
  const [invitations, setInvitations] = useState<Invitation[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [shareLink, setShareLink] = useState('')
  const [draftFor, setDraftFor] = useState<Invitation | null>(null)
  const suggestions = teams?.filter(t => !t.isPlatformAdmin) ?? []
  useEffect(() => {
    let active = true
    api.invitations().then(i => { if (active) setInvitations(i) }).catch(e => { if (active) setError(message(e)) })
    return () => { active = false }
  }, [])
  const run = async (action: () => Promise<string>) => {
    setBusy(true); setNotice(''); setError(''); setShareLink(''); setDraftFor(null)
    try { const done = await action(); setInvitations(await api.invitations()); setNotice(done) }
    catch (e) { setError(message(e)) } finally { setBusy(false) }
  }
  const describe = (result: InvitationResult) => {
    setDraftFor(result.invitation)
    if (result.delivery === 'existing-member') {
      setShareLink(workspaceLink())
      return `This person is already in the directory — share the workspace link: ${workspaceLink()}`
    }
    return deliveryNotice(result)
  }
  const copy = async (text: string, done: string) => {
    try { await navigator.clipboard.writeText(text); setNotice(done) }
    catch { setNotice(`Copy is unavailable in this browser. Select and copy: ${text}`) }
  }
  return <section className="eval-card" aria-labelledby="admin-invitations">
    <h2 id="admin-invitations">Invitations</h2>
    <p>Microsoft Entra emails the invitation from invites@microsoft.com, on your behalf when your account may invite guests.
      Because recipients' mail filters often hold those emails, send each invitation from your own mailbox as well.
      Invitees accept, sign in, then choose the team they join (the suggestion is preselected). Invitations expire after 14 days.</p>
    <form onSubmit={event => {
      event.preventDefault()
      const form = event.currentTarget
      const data = new FormData(form)
      const email = String(data.get('email')).trim()
      const suggested = String(data.get('suggested') ?? '')
      void run(async () => { const text = describe(await api.invite(email, suggested || null)); form.reset(); return text })
    }}>
      <label>Email address<input name="email" type="email" required maxLength={254} autoComplete="off" /></label>
      <label>Suggested team (optional)<select name="suggested" defaultValue="">
        <option value="">No suggestion</option>
        {suggestions.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
      </select></label>
      <button disabled={busy}>{busy ? 'Sending (Microsoft takes a few seconds)…' : 'Send invitation'}</button>
    </form>
    {error && <p className="eval-error" role="alert">{error}</p>}
    <div role="status" aria-live="polite">
      {notice && <p>{notice}</p>}
      {shareLink && <button type="button" onClick={() => copy(shareLink, 'Workspace link copied.')}>Copy workspace link</button>}
      {draftFor && <p className="eval-actions">
        <OutlookEmailButton key={`${draftFor.id}:${draftFor.suggestedTeamId ?? ''}`} invitation={draftFor} label="Email invitation from Outlook" />
      </p>}
    </div>
    {invitations && (!invitations.length ? <p>No invitations yet.</p> : <table className="eval-table">
      <caption>Invitations</caption>
      <thead><tr><th scope="col">Email</th><th scope="col">Status</th><th scope="col">Delivery</th><th scope="col">Suggested team</th>
        <th scope="col">Expires</th><th scope="col">Actions</th></tr></thead>
      <tbody>{invitations.map(i => {
        const open = i.status === 'Pending' || i.status === 'Expired'
        return <tr key={i.id}>
          <th scope="row">{i.email}</th>
          <td>{i.status}{i.status === 'Accepted' && i.acceptedTeamName ? ` (joined ${i.acceptedTeamName})` : ''}</td>
          <td>{i.delivery === 'email' ? 'Microsoft email (delivery not confirmed)' : 'Existing directory member'}</td>
          <td>{open && suggestions.length
            ? <SuggestionEditor invitation={i} teams={suggestions} busy={busy}
              onSave={teamId => run(async () => {
                const updated = await api.changeInvitationSuggestion(i.id, teamId)
                setDraftFor(updated)
                return `Suggested team for ${i.email} is now ${updated.suggestedTeamName ?? 'none'}. No email was sent — ` +
                  'the invitee sees the new team when they sign in. Use Resend or email them from Outlook to let them know.'
              })} />
            : i.suggestedTeamName ?? 'None'}</td>
          <td>{i.status === 'Accepted' || i.status === 'Revoked' ? '—' : date(i.expiresAt)}</td>
          <td>
            {open && <button aria-label={`Resend invitation to ${i.email}`} disabled={busy}
              onClick={() => run(async () => describe(await api.resendInvitation(i.id)))}>Resend</button>}
            {open && <button aria-label={`Revoke invitation to ${i.email}`} disabled={busy}
              onClick={() => run(async () => { await api.revokeInvitation(i.id); return `Invitation to ${i.email} revoked.` })}>Revoke</button>}
            {i.redeemUrl && <button aria-label={`Copy redeem link for ${i.email}`}
              onClick={() => copy(i.redeemUrl!, `Redeem link for ${i.email} copied. Share it only with the invitee.`)}>Copy redeem link</button>}
            {i.status === 'Pending' && <OutlookEmailButton invitation={i} ariaLabel={`Email invitation to ${i.email} from Outlook`} />}
          </td>
        </tr>
      })}</tbody>
    </table>)}
  </section>
}
