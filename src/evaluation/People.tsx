import { useEffect, useId, useState, type KeyboardEvent } from 'react'
import { api } from './api'
import { deliveryNotice, isEmailAddress } from './access'
import { OutlookEmailButton } from './OutlookEmail'
import type { AddPersonResult, Invitation, PeopleSearch, Person, PersonRequest, Team, TeamInvitation } from './contracts'

const MAX_PEOPLE = 20
const MIN_QUERY = 2
const DEBOUNCE_MS = 250
const message = (error: unknown) => error instanceof Error ? error.message : 'The operation failed.'
const personLabel = (p: Person) => p.displayName || p.email || p.subject
const date = (value: string) => new Date(value).toLocaleDateString()

interface Choice { key: string; label: string; detail: string | null; request: PersonRequest }
interface Option { id: string; choice: Choice; person: Person | null; disabled: boolean; selected: boolean }
interface SearchState { q: string; data: PeopleSearch | null; error: string | null }

// Pending invitations keep their suggested team editable; saving changes only the preselected team on the Join screen.
export function SuggestionEditor({ invitation, teams, busy, onSave, label = 'Change' }: {
  invitation: Pick<Invitation, 'email' | 'suggestedTeamId'>; teams: { id: string; name: string }[]; busy: boolean
  onSave: (teamId: string | null) => void; label?: string
}) {
  const current = invitation.suggestedTeamId ?? ''
  const [choice, setChoice] = useState(current)
  return <span className="eval-actions">
    <select aria-label={`Suggested team for ${invitation.email}`} value={choice} disabled={busy} onChange={e => setChoice(e.target.value)}>
      <option value="">No suggestion</option>
      {current && !teams.some(t => t.id === current) && <option value={current} disabled>Current team</option>}
      {teams.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
    </select>
    <button type="button" aria-label={`${label} suggested team for ${invitation.email}`} disabled={busy || choice === current}
      onClick={() => onSave(choice || null)}>{label}</button>
  </span>
}

// Team people picker: an ARIA 1.2 combobox (list autocomplete) over platform people and the caller's own delegated
// directory view, plus an email option for someone new. The API decides who is added, invited or not found.
export function InvitePeople({ team, onAdded }: { team: Team; onAdded: () => void }) {
  const ids = useId()
  const inputId = `${ids}-input`, listboxId = `${ids}-listbox`, hintId = `${ids}-hint`, headingId = `${ids}-heading`, selectedId = `${ids}-selected`
  const [query, setQuery] = useState('')
  const [search, setSearch] = useState<SearchState | null>(null)
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(-1)
  const [selected, setSelected] = useState<Choice[]>([])
  const [results, setResults] = useState<AddPersonResult[] | null>(null)
  const [invitations, setInvitations] = useState<TeamInvitation[] | null>(null)
  const [managed, setManaged] = useState<Team[]>([])
  const [moved, setMoved] = useState<Invitation | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const trimmed = query.trim()
  const focusInput = () => document.getElementById(inputId)?.focus()

  useEffect(() => {
    if (trimmed.length < MIN_QUERY) return
    const controller = new AbortController()
    const timer = setTimeout(() => {
      api.searchPeople(team.id, trimmed, controller.signal)
        .then(data => { if (!controller.signal.aborted) { setSearch({ q: trimmed, data, error: null }); setActive(-1) } })
        .catch(e => { if (!controller.signal.aborted) setSearch({ q: trimmed, data: null, error: message(e) }) })
    }, DEBOUNCE_MS)
    return () => { clearTimeout(timer); controller.abort() }
  }, [team.id, trimmed])
  useEffect(() => {
    let current = true
    api.teamInvitations(team.id).then(i => { if (current) setInvitations(i) }).catch(e => { if (current) setError(message(e)) })
    api.teams().then(t => { if (current) setManaged(t.filter(x => x.canManage && !x.isPlatformAdmin)) }).catch(() => { if (current) setManaged([]) })
    return () => { current = false }
  }, [team.id])

  const current = trimmed.length >= MIN_QUERY && search?.q === trimmed ? search : null
  const data = current?.data ?? null
  const searching = trimmed.length >= MIN_QUERY && !current
  const expanded = open && trimmed.length >= MIN_QUERY
  const chosen = new Set(selected.map(s => s.key))
  const toOption = (p: Person): Option => ({
    id: `${ids}-option-${p.subject}`, person: p, disabled: p.member || chosen.has(p.subject), selected: chosen.has(p.subject),
    choice: { key: p.subject, label: personLabel(p), detail: p.email, request: { subject: p.subject } },
  })
  const platform = (data?.results ?? []).filter(p => p.source === 'platform').map(toOption)
  const directory = (data?.results ?? []).filter(p => p.source === 'directory').map(toOption)
  const typedEmail = trimmed.toLowerCase()
  const emailOption: Option | null = data && isEmailAddress(typedEmail) && !data.results.some(p => p.email?.toLowerCase() === typedEmail) ? {
    id: `${ids}-option-email`, person: null, disabled: chosen.has(`email:${typedEmail}`), selected: chosen.has(`email:${typedEmail}`),
    choice: { key: `email:${typedEmail}`, label: typedEmail, detail: null, request: { email: typedEmail } },
  } : null
  const options = [...platform, ...directory, ...(emailOption ? [emailOption] : [])]
  const activeOption = expanded ? options[active] : undefined

  const choose = (option: Option) => {
    if (option.disabled) return
    if (selected.length >= MAX_PEOPLE) { setError(`You can add up to ${MAX_PEOPLE} people at a time.`); return }
    setSelected(s => [...s, option.choice]); setQuery(''); setOpen(false); setActive(-1); setError('')
    focusInput()
  }
  const move = (delta: 1 | -1) => {
    const enabled = options.flatMap((o, i) => o.disabled ? [] : [i])
    if (!enabled.length) { setActive(-1); return }
    const position = enabled.indexOf(active)
    setActive(position < 0 ? enabled[delta > 0 ? 0 : enabled.length - 1] : enabled[(position + delta + enabled.length) % enabled.length])
  }
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      setOpen(true)
      move(event.key === 'ArrowDown' ? 1 : -1)
    } else if (event.key === 'Enter') {
      if (activeOption) { event.preventDefault(); choose(activeOption) }
    } else if (event.key === 'Escape') {
      event.preventDefault()
      if (expanded) { setOpen(false); setActive(-1) } else setQuery('')
    }
  }
  const add = async () => {
    setBusy(true); setError(''); setNotice(''); setMoved(null)
    try {
      const response = await api.addPeople(team.id, selected.map(s => s.request))
      setResults(response.results); setSelected([])
      const count = (status: AddPersonResult['status']) => response.results.filter(r => r.status === status).length
      setNotice([`${count('added')} added`, `${count('invited')} invited`, `${count('already-member')} already members`,
        `${count('not-found')} not found`, `${count('error')} failed`].join(', ') + '.')
      onAdded()
      if (count('invited')) setInvitations(await api.teamInvitations(team.id))
    } catch (e) { setError(message(e)) } finally { setBusy(false) }
  }
  const revoke = async (invitation: TeamInvitation) => {
    setBusy(true); setError(''); setNotice(''); setMoved(null)
    try {
      await api.revokeTeamInvitation(team.id, invitation.id)
      setInvitations(await api.teamInvitations(team.id)); setNotice(`Invitation to ${invitation.email} revoked.`)
    } catch (e) { setError(message(e)) } finally { setBusy(false) }
  }
  const moveInvitation = async (invitation: TeamInvitation, target: string | null) => {
    setBusy(true); setError(''); setNotice(''); setMoved(null)
    try {
      const updated = await api.moveTeamInvitation(team.id, invitation.id, target)
      setInvitations(await api.teamInvitations(team.id)); setMoved(updated)
      setNotice(`${invitation.email} is now ${updated.suggestedTeamName ? `suggested for ${updated.suggestedTeamName}` : 'without a suggested team'}. ` +
        'No email was sent; email them from Outlook so they know which team to choose.')
    } catch (e) { setError(message(e)) } finally { setBusy(false) }
  }
  const targets = [team, ...managed.filter(t => t.id !== team.id)]
  const renderOption = (o: Option) => {
    const index = options.indexOf(o)
    return <li key={o.id} id={o.id} role="option" aria-selected={index === active} aria-disabled={o.disabled || undefined}
      aria-label={o.person ? undefined : `Invite ${o.choice.label} by email`}
      className={`eval-option${index === active ? ' eval-option-active' : ''}`} onClick={() => choose(o)}>
      {o.person ? <>
        <span>{o.choice.label}</span>
        {o.person.email && o.person.email !== o.choice.label && <span className="eval-muted"> {o.person.email}</span>}
        {o.person.guest && <> <span className="eval-badge">Guest</span></>}
        {o.person.member && <span className="eval-muted"> · Already a member</span>}
      </> : <>Invite {o.choice.label} by email</>}
      {o.selected && <span className="eval-muted"> · Selected</span>}
    </li>
  }
  const group = (label: string, items: Option[], key: string) => items.length > 0 && <li role="presentation" key={key}>
    <ul role="group" aria-labelledby={`${ids}-${key}`} className="eval-list">
      <li role="presentation" id={`${ids}-${key}`} className="eval-option-group">{label}</li>
      {items.map(renderOption)}
    </ul>
  </li>
  const resultText = (r: AddPersonResult, who: string) => {
    const outlook = (email: string, redeemUrl: string | null) => <OutlookEmailButton ariaLabel={`Email ${who} from Outlook`} invitation={{ email, redeemUrl, suggestedTeamName: team.name }} />
    switch (r.status) {
      case 'added': return <>Added to the team. {r.email && outlook(r.email, null)}</>
      case 'already-member': return <>Already a member.</>
      case 'invited': return <>Invitation sent by Microsoft. {deliveryNotice({ email: r.email ?? who, sentAs: r.sentAs, note: r.note })}{' '}
        {r.email && outlook(r.email, r.redeemUrl ?? null)}</>
      case 'not-found': return <>Not found in the platform or your directory.</>
      default: return <>{r.error || 'The person could not be added.'}</>
    }
  }
  return <section className="eval-people" aria-labelledby={headingId}>
    <h4 id={headingId}>Invite people</h4>
    <p className="eval-hint">Find people already on the platform or in your organization's directory. People you add join
      {` ${team.name}`} as Members right away; someone new receives a Microsoft invitation by email.</p>
    {error && <p className="eval-error" role="alert">{error}</p>}
    <div className="eval-combobox">
      <label htmlFor={inputId}>Find people by name or email</label>
      <input id={inputId} type="text" role="combobox" value={query} autoComplete="off" spellCheck={false} maxLength={100}
        aria-expanded={expanded} aria-controls={listboxId} aria-autocomplete="list" aria-describedby={hintId}
        aria-activedescendant={activeOption?.id}
        onChange={event => { setQuery(event.target.value); setOpen(true); setActive(-1) }}
        onKeyDown={onKeyDown} onBlur={() => { setOpen(false); setActive(-1) }} />
      <p id={hintId} className="eval-hint">Type at least {MIN_QUERY} characters, or a full email address to invite someone new.
        Use the arrow keys and Enter to choose.</p>
      <div className="eval-popup" hidden={!expanded}>
        <div role="status" aria-live="polite">
          {expanded && searching && 'Searching…'}
          {expanded && data && !options.length && 'No people found. Type a full email address to invite someone new.'}
        </div>
        {current?.error && expanded && <p className="eval-error">{current.error}</p>}
        <ul id={listboxId} role="listbox" aria-label="People" className="eval-list" onMouseDown={event => event.preventDefault()}>
          {group('On the platform', platform, 'platform')}
          {group('In the directory', directory, 'directory')}
          {emailOption && renderOption(emailOption)}
        </ul>
      </div>
      {data && !data.directoryAvailable && data.directoryNote && <p className="eval-hint">{data.directoryNote}</p>}
    </div>
    {selected.length > 0 && <div>
      <h5 id={selectedId}>Selected people</h5>
      <ul className="eval-chips" aria-labelledby={selectedId}>
        {selected.map(s => <li key={s.key}>
          {s.label}{s.detail && s.detail !== s.label ? <span className="eval-muted"> {s.detail}</span> : null}
          <button type="button" aria-label={`Remove ${s.label}`} disabled={busy}
            onClick={() => { setSelected(list => list.filter(x => x.key !== s.key)); focusInput() }}>×</button>
        </li>)}
      </ul>
      <button type="button" disabled={busy} onClick={add}>
        {busy ? 'Adding…' : `Add ${selected.length} ${selected.length === 1 ? 'person' : 'people'}`}</button>
    </div>}
    <div role="status" aria-live="polite">{notice && <p>{notice}</p>}
      {moved && <p><OutlookEmailButton ariaLabel={`Email updated invitation to ${moved.email} from Outlook`} invitation={moved} /></p>}</div>
    {results && <ul aria-label="Results" className="eval-results">
      {results.map((r, i) => {
        const who = r.displayName || r.email || r.subject || 'Unknown person'
        return <li key={`${i}:${who}`}><strong>{who}</strong>: {resultText(r, who)}</li>
      })}
    </ul>}
    <h5>Pending invitations for this team</h5>
    {!invitations ? <p role="status">Loading invitations…</p> : !invitations.length ? <p>No pending invitations for this team.</p> :
      <table className="eval-table">
        <caption>Pending invitations for this team</caption>
        <thead><tr><th scope="col">Email</th><th scope="col">Status</th><th scope="col">Expires</th><th scope="col">Suggested team</th>
          <th scope="col">Actions</th></tr></thead>
        <tbody>{invitations.map(i => <tr key={i.id}>
          <th scope="row">{i.email}</th><td>{i.status}</td><td>{date(i.expiresAt)}</td>
          <td><SuggestionEditor invitation={{ email: i.email, suggestedTeamId: team.id }} teams={targets} busy={busy} label="Move"
            onSave={target => moveInvitation(i, target)} /></td>
          <td>
            {i.status === 'Pending' && <OutlookEmailButton ariaLabel={`Email invitation to ${i.email} from Outlook`} invitation={{ email: i.email, redeemUrl: i.redeemUrl, suggestedTeamName: team.name }} />}
            <button aria-label={`Revoke invitation to ${i.email}`} disabled={busy} onClick={() => revoke(i)}>Revoke</button>
          </td>
        </tr>)}</tbody>
      </table>}
  </section>
}
