// Wire contracts for services/evaluation/Evaluation.Api. No browser acceptance logic.
export type Role = 'Source' | 'DT' | 'BRD' | 'PRD'
export type ScoredRole = Exclude<Role, 'Source'>
export type TeamRole = 'Administrator' | 'Member' | 'Reviewer'
// "PlatformAdmin" is read-only access granted to platform administrators on teams they are not members of (never stored).
export type TeamAccessRole = TeamRole | 'PlatformAdmin'
export type Layer = 'structure' | 'reference' | 'judge'
export interface Team { id: string; name: string; role: TeamAccessRole; isPlatformAdmin?: boolean; canManage?: boolean }
export interface Project { id: string; name: string }
export interface Submission { id: string; name: string; description: string }
export interface DocumentVersion {
  id: string; role: Role; fileName: string; relativePath: string; bundleId: string; hash: string; size: number
  status: string; error: string | null; textLength: number; createdAt: string
}
export interface UploadResult { bundleId: string; role: Role; versions: DocumentVersion[] }
export interface ScanSummary {
  id: string; status: string; outcome: string; error: string | null; createdAt: string
}
export interface SubmissionDetail {
  submission: Submission; documents: DocumentVersion[]; scans: ScanSummary[]
}
export interface Thresholds { version: number; dt: number; brd: number; prd: number }
export interface Policy {
  version: number; thresholds: Thresholds | null
  actor?: string; createdAt?: string
}
// Registered agent-eval sidecar contract, snapshotted immutably into every scan.
export interface JudgeContract {
  version: string; evaluator: string; sidecar: string
  reference: { id: string; sha: string }
  rubricsSha: string; profilesSha: string
  judge: { deployment: string; apiVersion: string; samples: number }
  scale: { minimum: number; maximum: number }
  layerWeights: Record<Layer, number>
  kinds: string[]
}
export interface Readiness {
  ready: boolean; blockers: string[]; judge: JudgeContract | null; lastHeartbeat: string | null
  scale: { minimum: number; maximum: number }; scanner: string; imports: string; pdf: string
}
export interface InputSnapshot {
  versionId: string; role: Role; hash: string; textLength: number; bundleId: string; relativePath: string
}
export interface CheckOutcome {
  id: string; layer: Layer; title: string; status: 'pass' | 'warn' | 'fail' | 'skip'
  score: number; weight: number; detail: string; evidence: string[]; guidance: string
}
export type Decision = 'Pass' | 'Fail' | 'Inconclusive' | 'Pending'
export interface ArtifactOutcome {
  role: ScoredRole; versionIds: string[]; decision: Decision; score100: number | null; threshold: number
  layers: { layer: Layer; weight: number; score: number | null }[]
  checks: CheckOutcome[]; summary: string; injectionDetected: boolean; truncated: boolean
  usage: { promptTokens: number; completionTokens: number; calls: number } | null
  invocationId: string | null; judgeStatus: string | null; errors: string[]
}
export interface ScanResult { outcome: Decision; artifacts: ArtifactOutcome[]; errors: string[] }
export interface Scan extends ScanSummary {
  submissionId: string
  inputs: InputSnapshot[]
  policy: Thresholds
  judge: JudgeContract
  result: ScanResult | null
  invocationIds: string[]
}
export interface Member { subject: string; role: TeamRole; email?: string | null; displayName?: string | null }
export interface Me {
  subject: string; tenantId: string; name: string | null
  isPlatformAdmin: boolean; platformAdminTeamId: string | null
  memberships: { teamId: string; teamName: string; role: TeamRole; isPlatformAdmin: boolean }[]
  pendingInvitationCount: number
}
export interface MyInvitations {
  invitations: { id: string; suggestedTeamId: string | null; suggestedTeamName: string | null; expiresAt: string }[]
  joinableTeams: { id: string; name: string }[]
}
export interface AcceptedInvitation { teamId: string; teamName: string; role: TeamRole }
export interface AdminTeam {
  id: string; name: string; isPlatformAdmin: boolean; createdAt: string
  members: number; projects: number; submissions: number; documents: number; scans: number
}
export interface TeamDeletion { id: string; rows: Record<string, number>; objectsDeleted: number; objectDeleteFailures: number }
export type InvitationDelivery = 'email' | 'existing-member'
export interface Invitation {
  id: string; email: string; status: 'Pending' | 'Accepted' | 'Revoked' | 'Expired'; delivery: InvitationDelivery
  suggestedTeamId: string | null; suggestedTeamName: string | null; invitedSubject: string; invitedBy: string
  createdAt: string; expiresAt: string; acceptedAt: string | null; acceptedTeamId: string | null; acceptedTeamName: string | null
  redeemUrl: string | null
}
export type InvitationSender = 'admin' | 'app'
export interface InvitationResult {
  delivery: InvitationDelivery; reused: boolean; invitation: Invitation
  sentAs?: InvitationSender | null; note?: string | null
}
// Self-service teams.
export interface JoinableTeam { id: string; name: string; members: number }
export interface CreatedTeam { id: string; name: string; role: TeamRole }
// Team people picker. Directory people come from the caller's own delegated directory view.
export interface Person {
  subject: string; displayName: string | null; email: string | null
  source: 'platform' | 'directory'; guest: boolean; member: boolean
}
export interface PeopleSearch { results: Person[]; directoryAvailable: boolean; directoryNote: string | null }
export type PersonRequest = { subject: string; email?: never } | { email: string; subject?: never }
export type AddPersonStatus = 'added' | 'already-member' | 'invited' | 'not-found' | 'error'
export interface AddPersonResult {
  subject?: string | null; email?: string | null; displayName?: string | null; status: AddPersonStatus
  sentAs?: InvitationSender | null; note?: string | null; redeemUrl?: string | null; error?: string | null
}
export interface TeamInvitation { id: string; email: string; status: 'Pending' | 'Expired'; expiresAt: string; redeemUrl: string | null }
