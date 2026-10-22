// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminTeam, Me } from './contracts'
import EvaluationWorkspace from './EvaluationWorkspace'
import { Administration } from './Administration'
import { outlookEmail } from './draft.testutil'

// Test-only API doubles; the application has no mock client.
const mocks = vi.hoisted(() => ({
  api: {
    me: vi.fn(), teams: vi.fn(), myInvitations: vi.fn(), acceptInvitation: vi.fn(), readiness: vi.fn(), projects: vi.fn(), policy: vi.fn(),
    adminTeams: vi.fn(), createTeam: vi.fn(), deleteTeam: vi.fn(), adminMembers: vi.fn(), adminSaveMember: vi.fn(), adminRemoveMember: vi.fn(),
    invitations: vi.fn(), invite: vi.fn(), resendInvitation: vi.fn(), revokeInvitation: vi.fn(), members: vi.fn(),
    joinTeam: vi.fn(), leaveTeam: vi.fn(), joinableTeams: vi.fn(), createOwnTeam: vi.fn(),
    searchPeople: vi.fn(), addPeople: vi.fn(), teamInvitations: vi.fn(), revokeTeamInvitation: vi.fn(),
    changeInvitationSuggestion: vi.fn(), moveTeamInvitation: vi.fn(),
  },
}))
vi.mock('./auth', () => ({
  identityConfigured: true,
  auth: { account: async () => 'person@example.test', signOut: vi.fn(), signIn: vi.fn() },
}))
vi.mock('./api', () => ({ api: mocks.api }))

const INVITATION = '6f1c2a40-0d6e-4c55-9a0b-6f3d2d1e9b10'
const identity = (change: Partial<Me> = {}): Me => ({ subject: 'subject', tenantId: 'tenant', name: 'Person', isPlatformAdmin: false,
  platformAdminTeamId: null, memberships: [], pendingInvitationCount: 0, ...change })
const adminIdentity = identity({ isPlatformAdmin: true, platformAdminTeamId: 'admin-team',
  memberships: [{ teamId: 'admin-team', teamName: 'Platform admins', role: 'Member', isPlatformAdmin: true }] })
const adminTeam = (id: string, name: string, change: Partial<AdminTeam> = {}): AdminTeam => ({ id, name, isPlatformAdmin: false,
  createdAt: '2026-10-20T10:00:00Z', members: 2, projects: 3, submissions: 4, documents: 5, scans: 6, ...change })
const adminTeams = [adminTeam('admin-team', 'Platform admins', { isPlatformAdmin: true, members: 1, projects: 0, submissions: 0, documents: 0, scans: 0 }),
  adminTeam('team-a', 'Alpha team'), adminTeam('team-b', 'Bravo team')]
const readiness = { ready: false, blockers: ['Judge worker has not registered recently.'], judge: null, lastHeartbeat: null,
  scale: { minimum: 0, maximum: 100 }, scanner: 's', imports: 'i', pdf: 'p' }

afterEach(() => { cleanup(); sessionStorage.clear(); window.history.replaceState(null, '', '/') })
beforeEach(() => {
  Object.values(mocks.api).forEach(fn => fn.mockReset())
  mocks.api.teams.mockResolvedValue([])
  mocks.api.readiness.mockResolvedValue(readiness)
  mocks.api.projects.mockResolvedValue([])
  mocks.api.policy.mockResolvedValue({ version: 0, thresholds: null })
  mocks.api.adminTeams.mockResolvedValue(adminTeams)
  mocks.api.invitations.mockResolvedValue([])
  mocks.api.adminMembers.mockResolvedValue([])
  mocks.api.joinableTeams.mockResolvedValue([])
  mocks.api.teamInvitations.mockResolvedValue([])
})

describe('Joining a team', () => {
  it('preselects the suggested team, accepts the invitation and opens the workspace', async () => {
    mocks.api.me.mockResolvedValueOnce(identity({ pendingInvitationCount: 1 }))
      .mockResolvedValue(identity({ memberships: [{ teamId: 'team-b', teamName: 'Bravo team', role: 'Member', isPlatformAdmin: false }] }))
    mocks.api.teams.mockResolvedValueOnce([]).mockResolvedValue([{ id: 'team-b', name: 'Bravo team', role: 'Member', isPlatformAdmin: false }])
    mocks.api.joinableTeams.mockResolvedValue([{ id: 'team-a', name: 'Alpha team', members: 2 }, { id: 'team-b', name: 'Bravo team', members: 1 }])
    mocks.api.myInvitations.mockResolvedValue({
      invitations: [{ id: INVITATION, suggestedTeamId: 'team-b', suggestedTeamName: 'Bravo team', expiresAt: '2026-11-03T10:00:00Z' }],
      joinableTeams: [{ id: 'team-a', name: 'Alpha team' }, { id: 'team-b', name: 'Bravo team' }],
    })
    mocks.api.acceptInvitation.mockResolvedValue({ teamId: 'team-b', teamName: 'Bravo team', role: 'Member' })
    render(<EvaluationWorkspace />)
    await screen.findByRole('heading', { name: 'Join a team' })
    const suggested = await screen.findByRole('radio', { name: 'Bravo team (suggested)' }) as HTMLInputElement
    expect(suggested.checked).toBe(true)
    expect((screen.getByRole('radio', { name: 'Alpha team' }) as HTMLInputElement).checked).toBe(false)
    fireEvent.click(screen.getByRole('radio', { name: 'Alpha team' }))
    fireEvent.click(screen.getByRole('radio', { name: 'Bravo team (suggested)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Accept and join team' }))
    await waitFor(() => expect(mocks.api.acceptInvitation).toHaveBeenCalledWith(INVITATION, 'team-b'))
    expect(await screen.findByText('You joined Bravo team as Member.')).toBeTruthy()
    const select = screen.getByRole('combobox', { name: 'Authorized team' }) as HTMLSelectElement
    expect(select.value).toBe('team-b')
    expect(screen.queryByRole('heading', { name: 'Join a team' })).toBeNull()
    expect(mocks.api.joinTeam).not.toHaveBeenCalled()
  })

  it('surfaces a rejected acceptance in the join screen', async () => {
    mocks.api.me.mockResolvedValue(identity({ pendingInvitationCount: 1 }))
    mocks.api.joinableTeams.mockResolvedValue([{ id: 'team-a', name: 'Alpha team', members: 3 }])
    mocks.api.myInvitations.mockResolvedValue({
      invitations: [{ id: INVITATION, suggestedTeamId: null, suggestedTeamName: null, expiresAt: '2026-11-03T10:00:00Z' }],
      joinableTeams: [{ id: 'team-a', name: 'Alpha team' }],
    })
    mocks.api.acceptInvitation.mockRejectedValue(new Error('This invitation has expired. Ask a platform administrator to resend it.'))
    render(<EvaluationWorkspace />)
    const accept = await screen.findByRole('button', { name: 'Accept and join team' }) as HTMLButtonElement
    expect(accept.disabled).toBe(true)
    fireEvent.click(screen.getByRole('radio', { name: 'Alpha team' }))
    fireEvent.click(accept)
    expect((await screen.findByRole('alert')).textContent).toContain('expired')
  })

  it('offers Get started to signed-in users without teams and joins without an invitation', async () => {
    mocks.api.me.mockResolvedValueOnce(identity())
      .mockResolvedValue(identity({ memberships: [{ teamId: 'team-a', teamName: 'Alpha team', role: 'Member', isPlatformAdmin: false }] }))
    mocks.api.teams.mockResolvedValueOnce([]).mockResolvedValue([{ id: 'team-a', name: 'Alpha team', role: 'Member', isPlatformAdmin: false }])
    mocks.api.joinableTeams.mockResolvedValue([{ id: 'team-a', name: 'Alpha team', members: 2 }, { id: 'team-b', name: 'Bravo team', members: 1 }])
    mocks.api.joinTeam.mockResolvedValue({ teamId: 'team-a', teamName: 'Alpha team', role: 'Member' })
    render(<EvaluationWorkspace />)
    expect(await screen.findByRole('heading', { name: 'Get started' })).toBeTruthy()
    expect(screen.queryByText(/need an invitation/)).toBeNull()
    expect(screen.queryByRole('combobox', { name: 'Authorized team' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Back to workspace' })).toBeNull()
    expect(mocks.api.myInvitations).not.toHaveBeenCalled()
    expect(await screen.findByText('2 members')).toBeTruthy()
    const join = screen.getByRole('button', { name: 'Join team' }) as HTMLButtonElement
    expect(join.disabled).toBe(true)
    fireEvent.click(screen.getByRole('radio', { name: 'Alpha team' }))
    fireEvent.click(join)
    await waitFor(() => expect(mocks.api.joinTeam).toHaveBeenCalledWith('team-a'))
    expect(await screen.findByText('You joined Alpha team as Member.')).toBeTruthy()
    expect((screen.getByRole('combobox', { name: 'Authorized team' }) as HTMLSelectElement).value).toBe('team-a')
    expect(mocks.api.acceptInvitation).not.toHaveBeenCalled()
  })

  it('creates a team from Get started and lands in it as Administrator', async () => {
    mocks.api.me.mockResolvedValueOnce(identity())
      .mockResolvedValue(identity({ memberships: [{ teamId: 'new-team', teamName: 'New team', role: 'Administrator', isPlatformAdmin: false }] }))
    mocks.api.teams.mockResolvedValueOnce([]).mockResolvedValue([{ id: 'new-team', name: 'New team', role: 'Administrator', isPlatformAdmin: false, canManage: true }])
    mocks.api.createOwnTeam.mockResolvedValue({ id: 'new-team', name: 'New team', role: 'Administrator' })
    render(<EvaluationWorkspace />)
    expect(await screen.findByText('There are no other teams to join yet. Create one below.')).toBeTruthy()
    const create = screen.getByRole('button', { name: 'Create team' }) as HTMLButtonElement
    expect(create.disabled).toBe(true)
    fireEvent.change(screen.getByRole('textbox', { name: 'Team name' }), { target: { value: '  New team ' } })
    fireEvent.click(create)
    await waitFor(() => expect(mocks.api.createOwnTeam).toHaveBeenCalledWith('New team'))
    expect(await screen.findByText(/You created New team and are its Administrator/)).toBeTruthy()
    expect((screen.getByRole('combobox', { name: 'Authorized team' }) as HTMLSelectElement).value).toBe('new-team')
    expect(await screen.findByText('New team / Administrator')).toBeTruthy()
  })

  it('shows a rejected team name in Get started', async () => {
    mocks.api.me.mockResolvedValue(identity())
    mocks.api.createOwnTeam.mockRejectedValue(new Error('A team with that name already exists.'))
    render(<EvaluationWorkspace />)
    fireEvent.change(await screen.findByRole('textbox', { name: 'Team name' }), { target: { value: 'Alpha team' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create team' }))
    expect((await screen.findByRole('alert')).textContent).toContain('already exists')
  })

  it('lets members open Get started from the workspace and go back', async () => {
    mocks.api.me.mockResolvedValue(identity({ memberships: [{ teamId: 'team-a', teamName: 'Alpha team', role: 'Member', isPlatformAdmin: false }] }))
    mocks.api.teams.mockResolvedValue([{ id: 'team-a', name: 'Alpha team', role: 'Member', isPlatformAdmin: false }])
    mocks.api.joinableTeams.mockResolvedValue([{ id: 'team-b', name: 'Bravo team', members: 1 }])
    render(<EvaluationWorkspace />)
    fireEvent.click(await screen.findByRole('button', { name: 'Join or create a team' }))
    expect(await screen.findByRole('radio', { name: 'Bravo team' })).toBeTruthy()
    expect(screen.getByText('1 member')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Back to workspace' }))
    expect(await screen.findByRole('combobox', { name: 'Authorized team' })).toBeTruthy()
  })

  it('lets ordinary members leave a team after confirming', async () => {
    mocks.api.me.mockResolvedValueOnce(identity({ memberships: [{ teamId: 'team-a', teamName: 'Alpha team', role: 'Member', isPlatformAdmin: false }] }))
      .mockResolvedValue(identity())
    mocks.api.teams.mockResolvedValueOnce([{ id: 'team-a', name: 'Alpha team', role: 'Member', isPlatformAdmin: false }]).mockResolvedValue([])
    mocks.api.leaveTeam.mockResolvedValue(undefined)
    render(<EvaluationWorkspace />)
    await screen.findByRole('option', { name: 'Alpha team · Member' })
    fireEvent.change(screen.getByRole('combobox', { name: 'Authorized team' }), { target: { value: 'team-a' } })
    fireEvent.click(await screen.findByRole('button', { name: 'Leave team' }))
    const confirm = await screen.findByRole('alertdialog')
    expect(confirm.textContent).toContain('you lose access until you join it again')
    fireEvent.click(within(confirm).getByRole('button', { name: 'Stay in team' }))
    expect(screen.queryByRole('alertdialog')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Leave team' }))
    fireEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Leave team' }))
    await waitFor(() => expect(mocks.api.leaveTeam).toHaveBeenCalledWith('team-a'))
    // With no team left, the user is back on Get started.
    expect(await screen.findByRole('heading', { name: 'Get started' })).toBeTruthy()
  })

  it('keeps the invitation link through sign-in, routes members to the join screen and cleans the address bar', async () => {
    window.history.replaceState(null, '', `/?invitation=${INVITATION}#evaluation`)
    mocks.api.me.mockResolvedValue(identity({ pendingInvitationCount: 1,
      memberships: [{ teamId: 'team-a', teamName: 'Alpha team', role: 'Member', isPlatformAdmin: false }] }))
    mocks.api.teams.mockResolvedValue([{ id: 'team-a', name: 'Alpha team', role: 'Member', isPlatformAdmin: false }])
    mocks.api.joinableTeams.mockResolvedValue([{ id: 'team-b', name: 'Bravo team', members: 4 }])
    mocks.api.myInvitations.mockResolvedValue({
      invitations: [
        { id: 'aaaaaaaa-0000-4000-8000-000000000000', suggestedTeamId: null, suggestedTeamName: null, expiresAt: '2026-11-01T10:00:00Z' },
        { id: INVITATION, suggestedTeamId: 'team-b', suggestedTeamName: 'Bravo team', expiresAt: '2026-11-03T10:00:00Z' }],
      joinableTeams: [{ id: 'team-b', name: 'Bravo team' }],
    })
    render(<EvaluationWorkspace />)
    expect(window.location.search).toBe('')
    expect(window.location.hash).toBe('#evaluation')
    expect(sessionStorage.getItem('evaluation.invitation')).toBe(INVITATION)
    await screen.findByRole('heading', { name: 'Join a team' })
    expect(((await screen.findByRole('radio', { name: 'Bravo team (suggested)' })) as HTMLInputElement).checked).toBe(true)
    expect((screen.getByRole('combobox', { name: 'Invitation' }) as HTMLSelectElement).value).toBe(INVITATION)
    fireEvent.click(screen.getByRole('button', { name: 'Back to workspace' }))
    expect(await screen.findByRole('combobox', { name: 'Authorized team' })).toBeTruthy()
    expect(sessionStorage.getItem('evaluation.invitation')).toBeNull()
    expect(screen.getByRole('button', { name: 'Join another team' })).toBeTruthy()
  })
})

describe('Platform administrator read-only access', () => {
  it('marks non-member teams read-only and hides mutation controls', async () => {
    mocks.api.me.mockResolvedValue(adminIdentity)
    mocks.api.teams.mockResolvedValue([{ id: 'admin-team', name: 'Platform admins', role: 'Member', isPlatformAdmin: true },
      { id: 'team-a', name: 'Alpha team', role: 'PlatformAdmin', isPlatformAdmin: false }])
    mocks.api.projects.mockResolvedValue([{ id: 'project', name: 'Readable project' }])
    render(<EvaluationWorkspace />)
    const select = await screen.findByRole('combobox', { name: 'Authorized team' })
    expect(await screen.findByRole('option', { name: 'Alpha team · Read-only (platform admin)' })).toBeTruthy()
    fireEvent.change(select, { target: { value: 'team-a' } })
    const note = await screen.findByRole('note')
    expect(note.textContent).toContain('Read-only (platform admin)')
    expect(screen.getByText('Alpha team / Read-only (platform admin)')).toBeTruthy()
    expect(await screen.findByRole('option', { name: 'Readable project' })).toBeTruthy()
    expect(screen.queryByText('Create a project')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Team settings' }))
    expect(await screen.findByText(/join this team to set its thresholds and manage its members/)).toBeTruthy()
    expect(mocks.api.members).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Administration' })).toBeTruthy()
  })

  it('joins a read-only team as Member, unlocking project creation, and can leave again', async () => {
    const readOnly = [{ id: 'admin-team', name: 'Platform admins', role: 'Member', isPlatformAdmin: true },
      { id: 'team-a', name: 'Alpha team', role: 'PlatformAdmin', isPlatformAdmin: false }]
    const joined = [readOnly[0], { ...readOnly[1], role: 'Member', canManage: true }]
    mocks.api.me.mockResolvedValue(adminIdentity)
    mocks.api.teams.mockResolvedValueOnce(readOnly).mockResolvedValueOnce(joined).mockResolvedValue(readOnly)
    mocks.api.projects.mockResolvedValue([])
    mocks.api.joinTeam.mockResolvedValue({ teamId: 'team-a', teamName: 'Alpha team', role: 'Member' })
    mocks.api.leaveTeam.mockResolvedValue(undefined)
    mocks.api.members.mockResolvedValue([])
    mocks.api.policy.mockResolvedValue({ version: 0, thresholds: null })
    render(<EvaluationWorkspace />)
    await screen.findByRole('option', { name: 'Alpha team · Read-only (platform admin)' })
    fireEvent.change(screen.getByRole('combobox', { name: 'Authorized team' }), { target: { value: 'team-a' } })
    expect(screen.queryByText('Create a project')).toBeNull()
    fireEvent.click(await screen.findByRole('button', { name: 'Join this team as Member' }))
    await waitFor(() => expect(mocks.api.joinTeam).toHaveBeenCalledWith('team-a'))
    expect(await screen.findByText(/You joined Alpha team as Member/)).toBeTruthy()
    expect(await screen.findByText('Alpha team / Member')).toBeTruthy()
    expect(screen.getByText('Create a project')).toBeTruthy()
    expect(screen.queryByRole('note')).toBeNull()
    // Joined platform administrators manage the team's thresholds and memberships.
    fireEvent.click(screen.getByRole('button', { name: 'Team settings' }))
    expect(await screen.findByText("You manage this team's settings as a platform administrator.")).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Save new policy version' })).toBeTruthy()
    await waitFor(() => expect(mocks.api.members).toHaveBeenCalledWith('team-a'))
    fireEvent.click(screen.getByRole('button', { name: 'Submissions' }))
    fireEvent.click(screen.getByRole('button', { name: 'Leave team' }))
    const confirm = await screen.findByRole('alertdialog')
    fireEvent.click(within(confirm).getByRole('button', { name: 'Leave team' }))
    await waitFor(() => expect(mocks.api.leaveTeam).toHaveBeenCalledWith('team-a'))
    expect(await screen.findByText(/You left Alpha team/)).toBeTruthy()
    expect(await screen.findByRole('button', { name: 'Join this team as Member' })).toBeTruthy()
  })

  it('never offers join or leave for the platform administration team', async () => {
    mocks.api.me.mockResolvedValue(adminIdentity)
    mocks.api.teams.mockResolvedValue([{ id: 'admin-team', name: 'Platform admins', role: 'Member', isPlatformAdmin: true }])
    mocks.api.projects.mockResolvedValue([])
    render(<EvaluationWorkspace />)
    await screen.findByRole('option', { name: 'Platform admins · Member' })
    fireEvent.change(screen.getByRole('combobox', { name: 'Authorized team' }), { target: { value: 'admin-team' } })
    expect(await screen.findByText('Platform admins / Member')).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Leave team' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Join this team as Member' })).toBeNull()
  })

  it('does not offer Administration to ordinary members', async () => {
    mocks.api.me.mockResolvedValue(identity({ memberships: [{ teamId: 'team-a', teamName: 'Alpha team', role: 'Administrator', isPlatformAdmin: false }] }))
    mocks.api.teams.mockResolvedValue([{ id: 'team-a', name: 'Alpha team', role: 'Administrator', isPlatformAdmin: false }])
    render(<EvaluationWorkspace />)
    await screen.findByRole('combobox', { name: 'Authorized team' })
    expect(screen.queryByRole('button', { name: 'Administration' })).toBeNull()
    expect(mocks.api.adminTeams).not.toHaveBeenCalled()
  })
})

describe('Administration console', () => {
  const renderConsole = (onTeamsChanged = vi.fn()) => render(<Administration me={adminIdentity} onTeamsChanged={onTeamsChanged} />)

  it('requires the exact team name before permanent deletion and lists what is removed', async () => {
    const changed = vi.fn()
    mocks.api.deleteTeam.mockResolvedValue({ id: 'team-a', rows: {}, objectsDeleted: 7, objectDeleteFailures: 0 })
    renderConsole(changed)
    const teams = await screen.findByRole('table', { name: 'All teams in this tenant' })
    expect((within(teams).getByRole('button', { name: 'Delete Platform admins' }) as HTMLButtonElement).disabled).toBe(true)
    expect(within(teams).getByText('Platform administration team')).toBeTruthy()
    fireEvent.click(within(teams).getByRole('button', { name: 'Delete Alpha team' }))
    const dialog = screen.getByRole('dialog', { name: 'Permanently delete Alpha team?' })
    expect(dialog.textContent).toContain('3 projects and 4 submissions')
    expect(dialog.textContent).toContain('5 document versions and their private files')
    expect(dialog.textContent).toContain('6 scans with their results')
    const confirm = within(dialog).getByRole('button', { name: 'Permanently delete team' }) as HTMLButtonElement
    const input = within(dialog).getByRole('textbox', { name: /Type the team name/ })
    expect(document.activeElement).toBe(input)
    expect(confirm.disabled).toBe(true)
    fireEvent.change(input, { target: { value: 'alpha team' } })
    expect(confirm.disabled).toBe(true)
    fireEvent.change(input, { target: { value: 'Alpha team ' } })
    expect(confirm.disabled).toBe(true)
    fireEvent.change(input, { target: { value: 'Alpha team' } })
    expect(confirm.disabled).toBe(false)
    fireEvent.click(confirm)
    await waitFor(() => expect(mocks.api.deleteTeam).toHaveBeenCalledWith('team-a', 'Alpha team'))
    expect(await screen.findByText(/Team Alpha team was permanently deleted\. 7 private files removed\./)).toBeTruthy()
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(changed).toHaveBeenCalled()
  })

  it('shows a conflict from the API inside the deletion dialog and can be cancelled', async () => {
    mocks.api.deleteTeam.mockRejectedValue(new Error('The team has queued or running scans. Wait until they finish, then delete the team.'))
    renderConsole()
    fireEvent.click(await screen.findByRole('button', { name: 'Delete Bravo team' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Bravo team' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Permanently delete team' }))
    expect((await within(dialog).findByRole('alert')).textContent).toContain('queued or running scans')
    fireEvent.keyDown(dialog, { key: 'Escape' })
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('reports unconfirmed Microsoft delivery, offers an Outlook draft and excludes the admin team from suggestions', async () => {
    mocks.api.invite.mockResolvedValue({ delivery: 'email', reused: false, sentAs: 'admin', note: null,
      invitation: { id: 'i', email: 'guest@partner.example', redeemUrl: 'https://login.microsoftonline.com/redeem?rd=x', suggestedTeamName: 'Bravo team' } })
    renderConsole()
    const suggested = await screen.findByRole('combobox', { name: 'Suggested team (optional)' })
    await within(suggested).findByRole('option', { name: 'Alpha team' })
    expect(within(suggested).queryByRole('option', { name: 'Platform admins' })).toBeNull()
    fireEvent.change(screen.getByRole('textbox', { name: 'Email address' }), { target: { value: ' guest@partner.example ' } })
    fireEvent.change(suggested, { target: { value: 'team-b' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send invitation' }))
    await waitFor(() => expect(mocks.api.invite).toHaveBeenCalledWith('guest@partner.example', 'team-b'))
    expect(await screen.findByText(/will email guest@partner\.example from invites@microsoft\.com on your behalf\. Delivery isn't confirmed/)).toBeTruthy()
    const draft = await outlookEmail(screen.getByRole('button', { name: 'Email invitation from Outlook' }))
    // Only recipient and subject go in the mailto, so Outlook keeps the sender's signature; the body is pasted.
    expect(draft.mailto).toBe('mailto:guest%40partner.example?subject=Invitation%20to%20the%20Apex%20Evaluation%20workspace')
    expect(draft.target).toBe('_self')
    expect(draft.html).toMatch(/<a href="https:\/\/login\.microsoftonline\.com\/redeem\?rd=x"[^>]*>Accept invitation<\/a>/)
    expect(draft.html).toContain(`>${window.location.origin}/#evaluation</a>`)
    expect(draft.html).toContain('Suggested team: <strong>Bravo team</strong>')
    expect(draft.text).toContain('Suggested team: Bravo team')
    expect(await screen.findByText(/Invitation copied\. In the new Outlook message, click above your signature and press Ctrl\+V/)).toBeTruthy()
    expect(mocks.api.invitations).toHaveBeenCalledTimes(2)
  })

  it('explains when the app identity had to send the invitation', async () => {
    mocks.api.invite.mockResolvedValue({ delivery: 'email', reused: true, sentAs: 'app', note: 'Your account can\'t invite guests.',
      invitation: { id: 'i', email: 'guest@partner.example', redeemUrl: null, suggestedTeamName: null } })
    renderConsole()
    fireEvent.change(await screen.findByRole('textbox', { name: 'Email address' }), { target: { value: 'guest@partner.example' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send invitation' }))
    expect(await screen.findByText(/as the app\. Your account can't invite guests\..*existing pending invitation was updated/)).toBeTruthy()
  })

  it('tells administrators to share the workspace link for existing directory members', async () => {
    mocks.api.invite.mockResolvedValue({ delivery: 'existing-member', reused: false, invitation: { id: 'i', email: 'colleague@apex.example' } })
    renderConsole()
    fireEvent.change(await screen.findByRole('textbox', { name: 'Email address' }), { target: { value: 'colleague@apex.example' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send invitation' }))
    await waitFor(() => expect(mocks.api.invite).toHaveBeenCalledWith('colleague@apex.example', null))
    expect(await screen.findByText(`This person is already in the directory — share the workspace link: ${window.location.origin}/#evaluation`)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Copy workspace link' })).toBeTruthy()
  })

  it('lists invitations with status, resend, revoke and redeem-link copy', async () => {
    const writeText = vi.fn(async () => undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    mocks.api.invitations.mockResolvedValue([
      { id: 'p', email: 'pending@partner.example', status: 'Pending', delivery: 'email', suggestedTeamId: 'team-a', suggestedTeamName: 'Alpha team',
        invitedSubject: 's', invitedBy: 'a', createdAt: '2026-10-20T10:00:00Z', expiresAt: '2026-11-03T10:00:00Z', acceptedAt: null,
        acceptedTeamId: null, acceptedTeamName: null, redeemUrl: 'https://login.microsoftonline.com/redeem?x' },
      { id: 'a', email: 'accepted@partner.example', status: 'Accepted', delivery: 'existing-member', suggestedTeamId: null, suggestedTeamName: null,
        invitedSubject: 's2', invitedBy: 'a', createdAt: '2026-10-20T10:00:00Z', expiresAt: '2026-11-03T10:00:00Z', acceptedAt: '2026-10-21T10:00:00Z',
        acceptedTeamId: 'team-b', acceptedTeamName: 'Bravo team', redeemUrl: null },
    ])
    mocks.api.revokeInvitation.mockResolvedValue(undefined)
    renderConsole()
    const table = await screen.findByRole('table', { name: 'Invitations' })
    expect(within(table).getByText('Accepted (joined Bravo team)')).toBeTruthy()
    expect(within(table).queryByRole('button', { name: 'Resend invitation to accepted@partner.example' })).toBeNull()
    fireEvent.click(within(table).getByRole('button', { name: 'Copy redeem link for pending@partner.example' }))
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('https://login.microsoftonline.com/redeem?x'))
    expect(within(table).getByRole('button', { name: 'Email invitation to pending@partner.example from Outlook' })).toBeTruthy()
    expect(within(table).queryByRole('button', { name: 'Email invitation to accepted@partner.example from Outlook' })).toBeNull()
    fireEvent.click(within(table).getByRole('button', { name: 'Revoke invitation to pending@partner.example' }))
    await waitFor(() => expect(mocks.api.revokeInvitation).toHaveBeenCalledWith('p'))
    expect(await screen.findByText('Invitation to pending@partner.example revoked.')).toBeTruthy()
  })

  it('changes the suggested team of a pending invitation without sending email and offers an Outlook draft', async () => {
    const pending = { id: 'p', email: 'pending@partner.example', status: 'Pending', delivery: 'email', suggestedTeamId: 'team-a',
      suggestedTeamName: 'Alpha team', invitedSubject: 's', invitedBy: 'a', createdAt: '2026-10-20T10:00:00Z', expiresAt: '2026-11-03T10:00:00Z',
      acceptedAt: null, acceptedTeamId: null, acceptedTeamName: null, redeemUrl: 'https://login.microsoftonline.com/redeem?x' }
    const accepted = { ...pending, id: 'a', email: 'accepted@partner.example', status: 'Accepted', acceptedTeamName: 'Alpha team' }
    const moved = { ...pending, suggestedTeamId: 'team-b', suggestedTeamName: 'Bravo team' }
    mocks.api.invitations.mockResolvedValueOnce([pending, accepted]).mockResolvedValue([moved, accepted])
    mocks.api.changeInvitationSuggestion.mockResolvedValue(moved)
    renderConsole()
    const table = await screen.findByRole('table', { name: 'Invitations' })
    expect(within(table).queryByRole('combobox', { name: 'Suggested team for accepted@partner.example' })).toBeNull()
    const select = await within(table).findByRole('combobox', { name: 'Suggested team for pending@partner.example' }) as HTMLSelectElement
    expect(select.value).toBe('team-a')
    expect(within(select).queryByRole('option', { name: 'Platform admins' })).toBeNull()
    const change = within(table).getByRole('button', { name: 'Change suggested team for pending@partner.example' }) as HTMLButtonElement
    expect(change.disabled).toBe(true)
    fireEvent.change(select, { target: { value: 'team-b' } })
    fireEvent.click(change)
    await waitFor(() => expect(mocks.api.changeInvitationSuggestion).toHaveBeenCalledWith('p', 'team-b'))
    expect(mocks.api.resendInvitation).not.toHaveBeenCalled()
    expect(await screen.findByText(/Suggested team for pending@partner\.example is now Bravo team\. No email was sent/)).toBeTruthy()
    const body = (await outlookEmail(screen.getByRole('button', { name: 'Email invitation from Outlook' }))).text
    expect(body).toContain('Suggested team: Bravo team')
    fireEvent.change(within(table).getByRole('combobox', { name: 'Suggested team for pending@partner.example' }), { target: { value: '' } })
    mocks.api.changeInvitationSuggestion.mockResolvedValue({ ...pending, suggestedTeamId: null, suggestedTeamName: null })
    fireEvent.click(within(table).getByRole('button', { name: 'Change suggested team for pending@partner.example' }))
    await waitFor(() => expect(mocks.api.changeInvitationSuggestion).toHaveBeenLastCalledWith('p', null))
  })

  it('manages members of any team and guards the last administrator', async () => {
    mocks.api.adminMembers.mockResolvedValue([
      { subject: '11111111-1111-4111-8111-111111111111', role: 'Administrator', email: 'lead@apex.example', displayName: 'Team Lead' },
      { subject: '22222222-2222-4222-8222-222222222222', role: 'Member', email: null, displayName: null }])
    mocks.api.adminSaveMember.mockResolvedValue(undefined)
    renderConsole()
    fireEvent.click(await screen.findByRole('button', { name: 'Manage members of Alpha team' }))
    const members = await screen.findByRole('table', { name: 'Members of Alpha team' })
    expect(within(members).getByText('22222222-2222-4222-8222-222222222222', { selector: 'td:first-child' })).toBeTruthy()
    expect((within(members).getByRole('button', { name: 'Remove Team Lead' }) as HTMLButtonElement).disabled).toBe(true)
    fireEvent.change(within(members).getByRole('combobox', { name: 'Role for Team Lead' }), { target: { value: 'Member' } })
    expect((within(members).getByRole('button', { name: 'Change role for Team Lead' }) as HTMLButtonElement).disabled).toBe(true)
    fireEvent.change(screen.getByRole('textbox', { name: 'Entra object ID' }), { target: { value: '33333333-3333-4333-8333-333333333333' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Role' }), { target: { value: 'Administrator' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add member' }))
    await waitFor(() => expect(mocks.api.adminSaveMember).toHaveBeenCalledWith('team-a', '33333333-3333-4333-8333-333333333333', 'Administrator'))
  })
})
