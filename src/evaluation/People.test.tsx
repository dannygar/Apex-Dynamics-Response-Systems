// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PeopleSearch, Person, Team } from './contracts'
import { InvitePeople } from './People'
import { outlookEmail } from './draft.testutil'

// Test-only API doubles; the application has no mock client.
const mocks = vi.hoisted(() => ({
  api: { searchPeople: vi.fn(), addPeople: vi.fn(), teamInvitations: vi.fn(), revokeTeamInvitation: vi.fn(), teams: vi.fn(), moveTeamInvitation: vi.fn() },
}))
vi.mock('./api', () => ({ api: mocks.api }))

const team: Team = { id: 'team', name: 'Fixture team', role: 'Administrator' }
const person = (subject: string, displayName: string, email: string | null, change: Partial<Person> = {}): Person =>
  ({ subject, displayName, email, source: 'platform', guest: false, member: false, ...change })
const ada = person('ada-id', 'Ada Lovelace', 'ada@apex.example')
const adam = person('adam-id', 'Adam Member', 'adam@apex.example', { member: true })
const guest = person('guest-id', 'Ada Guest', null, { source: 'directory', guest: true })
const found: PeopleSearch = { results: [ada, adam, guest], directoryAvailable: true, directoryNote: null }
const NOTE = "Directory search isn't available for your account; type an email address to invite someone new."

afterEach(cleanup)
beforeEach(() => {
  Object.values(mocks.api).forEach(fn => fn.mockReset())
  mocks.api.teamInvitations.mockResolvedValue([])
  mocks.api.teams.mockResolvedValue([team])
  mocks.api.searchPeople.mockImplementation(async (_team: string, q: string) =>
    q.toLowerCase().startsWith('ada') ? found : { results: [], directoryAvailable: false, directoryNote: NOTE })
})
const renderPicker = (onAdded = vi.fn()) => { render(<InvitePeople team={team} onAdded={onAdded} />); return onAdded }
const combobox = () => screen.getByRole('combobox', { name: 'Find people by name or email' }) as HTMLInputElement
const type = (value: string) => fireEvent.change(combobox(), { target: { value } })
const key = (name: string) => fireEvent.keyDown(combobox(), { key: name })

describe('Invite people picker', () => {
  it('is an accessible combobox with grouped results and keyboard selection', async () => {
    renderPicker()
    const input = combobox()
    expect(input.getAttribute('aria-autocomplete')).toBe('list')
    expect(input.getAttribute('aria-expanded')).toBe('false')
    const listbox = document.getElementById(input.getAttribute('aria-controls')!)!
    expect(listbox.getAttribute('role')).toBe('listbox')
    type('a')
    expect(input.getAttribute('aria-expanded')).toBe('false')
    type('ada')
    expect(input.getAttribute('aria-expanded')).toBe('true')
    expect(await screen.findByText('Searching…')).toBeTruthy()
    const platform = await screen.findByRole('group', { name: 'On the platform' })
    const directory = screen.getByRole('group', { name: 'In the directory' })
    expect(mocks.api.searchPeople).toHaveBeenCalledTimes(1)
    expect(mocks.api.searchPeople.mock.calls[0].slice(0, 2)).toEqual(['team', 'ada'])
    const adaOption = within(platform).getByRole('option', { name: /Ada Lovelace/ })
    const adamOption = within(platform).getByRole('option', { name: /Adam Member/ })
    const guestOption = within(directory).getByRole('option', { name: /Ada Guest/ })
    expect(adamOption.getAttribute('aria-disabled')).toBe('true')
    expect(adamOption.textContent).toContain('Already a member')
    expect(within(guestOption).getByText('Guest')).toBeTruthy()
    expect(screen.queryByRole('option', { name: /by email/ })).toBeNull()
    key('ArrowDown')
    expect(input.getAttribute('aria-activedescendant')).toBe(adaOption.id)
    expect(adaOption.getAttribute('aria-selected')).toBe('true')
    key('ArrowDown') // Skips the disabled existing member.
    expect(input.getAttribute('aria-activedescendant')).toBe(guestOption.id)
    key('ArrowDown') // Wraps.
    expect(input.getAttribute('aria-activedescendant')).toBe(adaOption.id)
    key('ArrowUp')
    expect(input.getAttribute('aria-activedescendant')).toBe(guestOption.id)
    key('Escape')
    expect(input.getAttribute('aria-expanded')).toBe('false')
    expect(input.getAttribute('aria-activedescendant')).toBeNull()
    key('ArrowDown')
    expect(input.getAttribute('aria-expanded')).toBe('true')
    expect(input.getAttribute('aria-activedescendant')).toBe(adaOption.id)
    key('Enter')
    const chips = screen.getByRole('list', { name: 'Selected people' })
    expect(within(chips).getByText('Ada Lovelace')).toBeTruthy()
    expect(input.value).toBe('')
    expect(input.getAttribute('aria-expanded')).toBe('false')
    expect(screen.getByRole('button', { name: 'Add 1 person' })).toBeTruthy()
    // Already selected people can't be chosen twice.
    type('ada')
    expect((await screen.findByRole('option', { name: /Ada Lovelace/ })).getAttribute('aria-disabled')).toBe('true')
  })

  it('offers an email option for an unknown address and shows the directory note', async () => {
    renderPicker()
    type('New@Partner.example')
    const option = await screen.findByRole('option', { name: 'Invite new@partner.example by email' })
    expect(screen.getByText(NOTE)).toBeTruthy()
    expect(screen.queryByRole('group', { name: 'In the directory' })).toBeNull()
    fireEvent.click(option)
    const chips = screen.getByRole('list', { name: 'Selected people' })
    expect(within(chips).getByText('new@partner.example')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Remove new@partner.example' }))
    expect(screen.queryByRole('list', { name: 'Selected people' })).toBeNull()
    // No email option when a result already has that exact address.
    mocks.api.searchPeople.mockResolvedValue({ results: [ada], directoryAvailable: true, directoryNote: null })
    type('ada@apex.example')
    await screen.findByRole('option', { name: /Ada Lovelace/ })
    expect(screen.queryByRole('option', { name: /by email/ })).toBeNull()
  })

  it('adds the selected people and reports each result with Outlook drafts', async () => {
    mocks.api.addPeople.mockResolvedValue({ results: [
      { subject: 'ada-id', email: 'ada@apex.example', displayName: 'Ada Lovelace', status: 'added' },
      { email: 'new@partner.example', status: 'invited', sentAs: 'admin', note: null, redeemUrl: 'https://login.microsoftonline.com/redeem?rd=x' },
      { subject: 'adam-id', email: 'adam@apex.example', displayName: 'Adam Member', status: 'already-member' },
      { subject: 'gone-id', status: 'not-found' },
      { email: 'bad@partner.example', status: 'error', error: 'Microsoft Graph rejected the directory request (500).' },
    ] })
    mocks.api.teamInvitations.mockResolvedValueOnce([]).mockResolvedValue([
      { id: 'inv', email: 'new@partner.example', status: 'Pending', expiresAt: '2026-11-04T10:00:00Z', redeemUrl: 'https://login.microsoftonline.com/redeem?rd=x' }])
    const onAdded = renderPicker()
    type('ada')
    fireEvent.click(await screen.findByRole('option', { name: /Ada Lovelace/ }))
    type('new@partner.example')
    fireEvent.click(await screen.findByRole('option', { name: 'Invite new@partner.example by email' }))
    fireEvent.click(screen.getByRole('button', { name: 'Add 2 people' }))
    await waitFor(() => expect(mocks.api.addPeople).toHaveBeenCalledWith('team', [{ subject: 'ada-id' }, { email: 'new@partner.example' }]))
    const results = await screen.findByRole('list', { name: 'Results' })
    expect(onAdded).toHaveBeenCalled()
    expect(within(results).getByText(/Added to the team/)).toBeTruthy()
    const added = await outlookEmail(within(results).getByRole('button', { name: 'Email Ada Lovelace from Outlook' }))
    expect(added.mailto.startsWith('mailto:ada%40apex.example?subject=')).toBe(true)
    expect(added.mailto).not.toContain('body=')
    expect(added.text).toContain(`${window.location.origin}/#evaluation`)
    expect(added.text).toContain('Suggested team: Fixture team')
    expect(added.text).not.toContain('redeem')
    expect(within(results).getByText(/Invitation sent by Microsoft\..*on your behalf\. Delivery isn't confirmed/)).toBeTruthy()
    const invited = await outlookEmail(within(results).getByRole('button', { name: 'Email new@partner.example from Outlook' }))
    // The Accept invitation button uses the same redeem link as Microsoft's invitation email, and the link is also shown clickable.
    expect(invited.html).toMatch(/<a href="https:\/\/login\.microsoftonline\.com\/redeem\?rd=x"[^>]*>Accept invitation<\/a>/)
    expect(invited.html).toContain('>https://login.microsoftonline.com/redeem?rd=x</a>')
    expect(within(results).getByText(/Already a member/)).toBeTruthy()
    expect(within(results).getByText(/Not found/)).toBeTruthy()
    expect(within(results).getByText(/Microsoft Graph rejected the directory request/)).toBeTruthy()
    expect(screen.getByText('1 added, 1 invited, 1 already members, 1 not found, 1 failed.')).toBeTruthy()
    expect(screen.queryByRole('list', { name: 'Selected people' })).toBeNull()
    expect(await screen.findByRole('table', { name: 'Pending invitations for this team' })).toBeTruthy()
  })

  it('limits a request to 20 people', async () => {
    mocks.api.searchPeople.mockResolvedValue({ results: [], directoryAvailable: true, directoryNote: null })
    renderPicker()
    for (let i = 0; i < 21; i++) {
      type(`p${i}@partner.example`)
      fireEvent.click(await screen.findByRole('option', { name: `Invite p${i}@partner.example by email` }))
    }
    expect(screen.getByRole('alert').textContent).toContain('up to 20 people')
    expect(screen.getByRole('button', { name: 'Add 20 people' })).toBeTruthy()
  }, 20000)

  it('lists pending invitations for the team and revokes them', async () => {
    mocks.api.teamInvitations.mockResolvedValueOnce([
      { id: 'p', email: 'pending@partner.example', status: 'Pending', expiresAt: '2026-11-04T10:00:00Z', redeemUrl: 'https://login.microsoftonline.com/redeem?p' },
      { id: 'e', email: 'expired@partner.example', status: 'Expired', expiresAt: '2026-10-01T10:00:00Z', redeemUrl: null },
    ]).mockResolvedValue([])
    mocks.api.revokeTeamInvitation.mockResolvedValue(undefined)
    renderPicker()
    const table = await screen.findByRole('table', { name: 'Pending invitations for this team' })
    const draft = await outlookEmail(within(table).getByRole('button', { name: 'Email invitation to pending@partner.example from Outlook' }))
    expect(draft.html).toContain('href="https://login.microsoftonline.com/redeem?p"')
    expect(within(table).queryByRole('link', { name: /expired@partner\.example/ })).toBeNull()
    fireEvent.click(within(table).getByRole('button', { name: 'Revoke invitation to pending@partner.example' }))
    await waitFor(() => expect(mocks.api.revokeTeamInvitation).toHaveBeenCalledWith('team', 'p'))
    expect(await screen.findByText('Invitation to pending@partner.example revoked.')).toBeTruthy()
    expect(screen.getByText('No pending invitations for this team.')).toBeTruthy()
  })

  it('moves a pending invitation only to other teams the caller manages', async () => {
    mocks.api.teams.mockResolvedValue([
      { ...team, canManage: true }, { id: 'other', name: 'Other managed', role: 'Administrator', canManage: true },
      { id: 'joined', name: 'Member only', role: 'Member', canManage: false },
      { id: 'admins', name: 'Platform admins', role: 'Member', canManage: true, isPlatformAdmin: true },
    ])
    mocks.api.teamInvitations.mockResolvedValueOnce([
      { id: 'p', email: 'pending@partner.example', status: 'Pending', expiresAt: '2026-11-04T10:00:00Z', redeemUrl: null },
    ]).mockResolvedValue([])
    mocks.api.moveTeamInvitation.mockResolvedValue({ id: 'p', email: 'pending@partner.example', suggestedTeamId: 'other',
      suggestedTeamName: 'Other managed', redeemUrl: null })
    renderPicker()
    const select = await screen.findByRole('combobox', { name: 'Suggested team for pending@partner.example' }) as HTMLSelectElement
    await within(select).findByRole('option', { name: 'Other managed' })
    expect(select.value).toBe('team')
    expect(within(select).queryByRole('option', { name: 'Member only' })).toBeNull()
    expect(within(select).queryByRole('option', { name: 'Platform admins' })).toBeNull()
    fireEvent.change(select, { target: { value: 'other' } })
    fireEvent.click(screen.getByRole('button', { name: 'Move suggested team for pending@partner.example' }))
    await waitFor(() => expect(mocks.api.moveTeamInvitation).toHaveBeenCalledWith('team', 'p', 'other'))
    expect(await screen.findByText(/pending@partner\.example is now suggested for Other managed\. No email was sent/)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Email updated invitation to pending@partner.example from Outlook' })).toBeTruthy()
    expect(screen.getByText('No pending invitations for this team.')).toBeTruthy()
  })
})
