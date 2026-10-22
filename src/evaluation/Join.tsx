import { useEffect, useId, useState } from 'react'
import { api } from './api'
import type { JoinableTeam, MyInvitations, TeamRole } from './contracts'

const message = (error: unknown) => error instanceof Error ? error.message : 'The operation failed.'
export interface JoinedTeam { teamId: string; teamName: string; role: TeamRole; created: boolean }

// Self-service onboarding: join any team except the platform administration team, or create a team. Pending
// invitations only preselect a suggested team; the API decides what the caller may join.
export function GetStarted({ hint, hasInvitations, onJoined, onCancel }: {
  hint: string | null; hasInvitations: boolean; onJoined: (joined: JoinedTeam) => Promise<void> | void; onCancel?: () => void
}) {
  const [teams, setTeams] = useState<JoinableTeam[] | null>(null)
  const [invitations, setInvitations] = useState<MyInvitations['invitations']>([])
  const [invitationId, setInvitationId] = useState('')
  const [teamId, setTeamId] = useState('')
  const [name, setName] = useState('')
  const [busy, setBusy] = useState<'' | 'join' | 'create'>('')
  const [error, setError] = useState('')
  const heading = useId(); const joinHeading = useId(); const createHeading = useId()
  useEffect(() => {
    let active = true
    // Invitations are optional context; joining works without them.
    Promise.all([api.joinableTeams(), hasInvitations ? api.myInvitations().catch(() => null) : Promise.resolve(null)]).then(([joinable, mine]) => {
      if (!active) return
      const pending = mine?.invitations ?? []
      const chosen = pending.find(i => i.id === hint) ?? pending[0]
      setTeams(joinable); setInvitations(pending); setInvitationId(chosen?.id ?? '')
      setTeamId(chosen?.suggestedTeamId && joinable.some(t => t.id === chosen.suggestedTeamId) ? chosen.suggestedTeamId : '')
    }).catch(e => { if (active) setError(message(e)) })
    return () => { active = false }
  }, [hint, hasInvitations])
  const invitation = invitations.find(i => i.id === invitationId)
  return <section className="eval-card" aria-labelledby={heading}>
    <h2 id={heading}>Get started</h2>
    <p>Join an existing team or create your own. Anyone in your organization can join any team, and the person who creates a
      team becomes its Administrator.</p>
    {error && <p className="eval-error" role="alert">{error}</p>}
    <section aria-labelledby={joinHeading}>
      <h3 id={joinHeading}>Join a team</h3>
      {!teams ? !error && <p role="status">Loading teams…</p> : <form onSubmit={async event => {
        event.preventDefault()
        if (!teamId) return
        setBusy('join'); setError('')
        try {
          const joined = invitation ? await api.acceptInvitation(invitation.id, teamId) : await api.joinTeam(teamId)
          await onJoined({ ...joined, created: false })
        } catch (e) { setError(message(e)); setBusy('') }
      }}>
        {invitations.length > 0 && <p>You have been invited to the Apex Evaluation workspace. Joining a team uses your invitation;
          the suggested team is preselected, but you can choose any team.</p>}
        {invitations.length > 1 && <label>Invitation<select value={invitationId} disabled={Boolean(busy)} onChange={event => {
          const next = invitations.find(i => i.id === event.target.value)
          setInvitationId(event.target.value)
          if (next?.suggestedTeamId && teams.some(t => t.id === next.suggestedTeamId)) setTeamId(next.suggestedTeamId)
        }}>{invitations.map(i => <option key={i.id} value={i.id}>
          {i.suggestedTeamName ? `Suggested team ${i.suggestedTeamName}` : 'No suggested team'} · expires {new Date(i.expiresAt).toLocaleDateString()}
        </option>)}</select></label>}
        {invitation && <p>{invitation.suggestedTeamName ? <>Suggested team: <strong>{invitation.suggestedTeamName}</strong>. </> : null}
          This invitation expires {new Date(invitation.expiresAt).toLocaleString()}.</p>}
        {!teams.length ? <p role="status">There are no other teams to join yet. Create one below.</p> :
          <fieldset className="eval-select" disabled={Boolean(busy)}>
            <legend>Choose a team</legend>
            {teams.map(t => <div key={t.id} className="eval-choice">
              <label className="eval-inline">
                <input type="radio" name="join-team" value={t.id} checked={teamId === t.id} onChange={() => setTeamId(t.id)} />
                {t.name}{t.id === invitation?.suggestedTeamId ? ' (suggested)' : ''}
              </label>
              <span className="eval-muted">{t.members} member{t.members === 1 ? '' : 's'}</span>
            </div>)}
          </fieldset>}
        <p className="eval-hint">You join as a Member: you can work with the team's projects, submissions and scans. A team
          administrator can change your role.</p>
        {teams.length > 0 && <button disabled={Boolean(busy) || !teamId}>
          {busy === 'join' ? 'Joining…' : invitation ? 'Accept and join team' : 'Join team'}</button>}
      </form>}
    </section>
    <section aria-labelledby={createHeading}>
      <h3 id={createHeading}>Create a team</h3>
      <form onSubmit={async event => {
        event.preventDefault()
        const teamName = name.trim()
        if (!teamName) return
        setBusy('create'); setError('')
        try {
          const created = await api.createOwnTeam(teamName)
          await onJoined({ teamId: created.id, teamName: created.name, role: created.role, created: true })
        } catch (e) { setError(message(e)); setBusy('') }
      }}>
        <label>Team name<input value={name} onChange={event => setName(event.target.value)} required maxLength={200}
          disabled={Boolean(busy)} autoComplete="off" /></label>
        <p className="eval-hint">You become the team's Administrator: you set its thresholds and invite people.</p>
        <button disabled={Boolean(busy) || !name.trim()}>{busy === 'create' ? 'Creating…' : 'Create team'}</button>
      </form>
    </section>
    {onCancel && <button type="button" onClick={onCancel}>Back to workspace</button>}
  </section>
}
