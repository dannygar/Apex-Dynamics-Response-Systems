import { auth } from './auth'
import type {
  AcceptedInvitation, AddPersonResult, AdminTeam, CreatedTeam, Invitation, InvitationResult, JoinableTeam, Me, Member, MyInvitations, PeopleSearch,
  PersonRequest, Policy, Project, Readiness, Scan, Submission, SubmissionDetail, Team, TeamDeletion, TeamInvitation, TeamRole, UploadResult,
} from './contracts'

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) { super(message); this.status = status }
}
export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Authorization', `Bearer ${await auth.token()}`)
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  // Same-origin only. Development uses the explicit Vite /api proxy.
  const response = await fetch(`/api${path}`, { ...init, headers, credentials: 'omit' })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string; blockers?: string[] } | null
    throw new ApiError(response.status, body?.error ?? body?.blockers?.join(' ') ??
      (response.status === 401 ? 'Your API sign-in was rejected. Check tenant, audience and API consent.' :
        response.status === 403 ? 'The access token is missing the required Evaluation delegated API scope.' :
        response.status === 404 ? 'Resource unavailable or you do not have permission.' : `API request failed (${response.status}).`))
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>
}
const path = (team: string) => `/teams/${encodeURIComponent(team)}`
const adminTeam = (team: string) => `/admin/teams/${encodeURIComponent(team)}`
const json = (body: unknown) => JSON.stringify(body)
export const api = {
  me: () => request<Me>('/me'),
  teams: () => request<Team[]>('/teams'),
  myInvitations: () => request<MyInvitations>('/invitations/mine'),
  acceptInvitation: (invitation: string, teamId: string) =>
    request<AcceptedInvitation>(`/invitations/${encodeURIComponent(invitation)}/accept`, { method: 'POST', body: json({ teamId }) }),
  readiness: (t: string) => request<Readiness>(`${path(t)}/readiness`),
  projects: (t: string) => request<Project[]>(`${path(t)}/projects`),
  createProject: (t: string, name: string) => request<Project>(`${path(t)}/projects`, { method: 'POST', body: json({ name }) }),
  submissions: (t: string, p: string) => request<Submission[]>(`${path(t)}/projects/${p}/submissions`),
  createSubmission: (t: string, p: string, name: string, description: string) =>
    request<Submission>(`${path(t)}/projects/${p}/submissions`, { method: 'POST', body: json({ name, description }) }),
  submission: (t: string, s: string) => request<SubmissionDetail>(`${path(t)}/submissions/${s}`),
  upload: (t: string, s: string, body: FormData) => request<UploadResult>(`${path(t)}/submissions/${s}/documents`, { method: 'POST', body }),
  policy: (t: string) => request<Policy>(`${path(t)}/policy`),
  savePolicy: (t: string, body: unknown) => request(`${path(t)}/policy`, { method: 'PUT', body: json(body) }),
  members: (t: string) => request<Member[]>(`${path(t)}/members`),
  saveMember: (t: string, subject: string, role: string) =>
    request(`${path(t)}/members`, { method: 'PUT', body: json({ subject, role }) }),
  removeMember: (t: string, subject: string) => request(`${path(t)}/members/${encodeURIComponent(subject)}`, { method: 'DELETE' }),
  // Self-service: join any team except the platform administration team (as Member), leave it, or create a team
  // (the creator becomes its Administrator).
  joinableTeams: () => request<JoinableTeam[]>('/teams/joinable'),
  createOwnTeam: (name: string) => request<CreatedTeam>('/teams', { method: 'POST', body: json({ name }) }),
  joinTeam: (t: string) => request<AcceptedInvitation>(`${path(t)}/join`, { method: 'POST' }),
  leaveTeam: (t: string) => request(`${path(t)}/leave`, { method: 'POST' }),
  // Team people picker (team administrators).
  searchPeople: (t: string, q: string, signal?: AbortSignal) =>
    request<PeopleSearch>(`${path(t)}/people?q=${encodeURIComponent(q)}`, { signal }),
  addPeople: (t: string, people: PersonRequest[]) =>
    request<{ results: AddPersonResult[] }>(`${path(t)}/invitations`, { method: 'POST', body: json({ people }) }),
  teamInvitations: (t: string) => request<TeamInvitation[]>(`${path(t)}/invitations`),
  revokeTeamInvitation: (t: string, id: string) => request(`${path(t)}/invitations/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  moveTeamInvitation: (t: string, id: string, suggestedTeamId: string | null) =>
    request<Invitation>(`${path(t)}/invitations/${encodeURIComponent(id)}`, { method: 'PATCH', body: json({ suggestedTeamId }) }),
  // Platform administration (members of the operator-flagged platform team only).
  adminTeams: () => request<AdminTeam[]>('/admin/teams'),
  createTeam: (name: string) => request<AdminTeam>('/admin/teams', { method: 'POST', body: json({ name }) }),
  deleteTeam: (t: string, confirmName: string) => request<TeamDeletion>(adminTeam(t), { method: 'DELETE', body: json({ confirmName }) }),
  adminMembers: (t: string) => request<Member[]>(`${adminTeam(t)}/members`),
  adminSaveMember: (t: string, subject: string, role: TeamRole) =>
    request(`${adminTeam(t)}/members/${encodeURIComponent(subject)}`, { method: 'PUT', body: json({ role }) }),
  adminRemoveMember: (t: string, subject: string) => request(`${adminTeam(t)}/members/${encodeURIComponent(subject)}`, { method: 'DELETE' }),
  invitations: () => request<Invitation[]>('/admin/invitations'),
  invite: (email: string, suggestedTeamId: string | null) =>
    request<InvitationResult>('/admin/invitations', { method: 'POST', body: json({ email, suggestedTeamId }) }),
  resendInvitation: (id: string) => request<InvitationResult>(`/admin/invitations/${encodeURIComponent(id)}/resend`, { method: 'POST' }),
  revokeInvitation: (id: string) => request(`/admin/invitations/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  changeInvitationSuggestion: (id: string, suggestedTeamId: string | null) =>
    request<Invitation>(`/admin/invitations/${encodeURIComponent(id)}`, { method: 'PATCH', body: json({ suggestedTeamId }) }),
  startScan: (t: string, s: string, targets: string[], evidence: string[], key: string) =>
    request<{ id: string }>(`${path(t)}/submissions/${s}/scans`, {
      method: 'POST', headers: { 'Idempotency-Key': key },
      body: json({ targetVersionIds: targets, evidenceVersionIds: evidence }),
    }),
  scan: (t: string, id: string) => request<Scan>(`${path(t)}/scans/${id}`),
  async download(t: string, id: string, fileName: string) {
    const response = await fetch(`/api${path(t)}/documents/${id}/download`, {
      headers: { Authorization: `Bearer ${await auth.token()}` }, credentials: 'omit',
    })
    if (!response.ok) throw new ApiError(response.status, `Download unavailable (${response.status}).`)
    const url = URL.createObjectURL(await response.blob())
    const anchor = document.createElement('a')
    anchor.href = url; anchor.download = fileName; anchor.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  },
}
