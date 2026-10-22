import type { Invitation, InvitationSender, Member, Team, TeamAccessRole } from './contracts'

// Presentation helpers only; the API enforces every permission.
export const READ_ONLY_LABEL = 'Read-only (platform admin)'
export const roleLabel = (role: TeamAccessRole) => role === 'PlatformAdmin' ? READ_ONLY_LABEL : role
export const canWrite = (team: Team) => team.role === 'Administrator' || team.role === 'Member' || Boolean(team.canManage)
// Thresholds and memberships: team Administrators, and platform administrators in teams they joined (the API decides).
export const canManage = (team: Team) => team.role === 'Administrator' || Boolean(team.canManage)
export const workspaceLink = () => `${window.location.origin}/#evaluation`

const INVITATION_KEY = 'evaluation.invitation'
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

// The invitation redirect (?invitation=<id>#evaluation) is a navigation hint, not an authorization. It is kept in
// sessionStorage so it survives the Entra sign-in redirect, and removed from the address bar immediately.
export function captureInvitationHint(): string | null {
  const url = new URL(window.location.href)
  const value = url.searchParams.get('invitation')
  if (value !== null) {
    if (GUID.test(value)) sessionStorage.setItem(INVITATION_KEY, value.toLowerCase())
    url.searchParams.delete('invitation')
    window.history.replaceState(window.history.state, '', `${url.pathname}${url.search}${url.hash}`)
  }
  return sessionStorage.getItem(INVITATION_KEY)
}
export const clearInvitationHint = () => sessionStorage.removeItem(INVITATION_KEY)
export const memberName = (m: Member) => m.displayName || m.email || m.subject
// A plausible single address (the API validates authoritatively).
export const isEmailAddress = (value: string) => value.length <= 254 && /^[^\s@<>()",;:]+@[^\s@<>()",;:]+\.[^\s@<>()",;:.]{2,}$/.test(value)

// Microsoft only confirms that it accepted an invitation for sending, never delivery.
export function deliveryNotice(result: { email: string; sentAs?: InvitationSender | null; note?: string | null; reused?: boolean }) {
  const sender = result.sentAs === 'admin'
    ? `Microsoft accepted the invitation and will email ${result.email} from invites@microsoft.com on your behalf.`
    : `Microsoft accepted the invitation and will email ${result.email} from invites@microsoft.com as the app.`
  return [sender, result.note ?? '',
    'Delivery isn\'t confirmed: Microsoft\'s invitation emails often take several minutes and corporate mail filters may quarantine them. For immediate delivery, also send the invitation from your own mailbox.',
    result.reused ? 'The existing pending invitation was updated.' : ''].filter(Boolean).join(' ')
}

const escapeHtml = (value: string) => value.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

// An invitation the administrator sends from their own mailbox; recipients' mail filters often hold Microsoft's
// invites@microsoft.com messages, while mail from a colleague is delivered normally. Browsers can only pre-fill plain text
// into a new mail (mailto:), so the formatted body (Accept invitation button with the same redeem link as Microsoft's
// email, plus clickable links) is put on the clipboard and the mailto: carries only the recipient and subject. The empty
// body keeps the sender's Outlook signature; they paste above it.
export function invitationDraft(invitation: Pick<Invitation, 'email' | 'redeemUrl' | 'suggestedTeamName'>) {
  const accept = invitation.redeemUrl ?? workspaceLink()
  const workspace = workspaceLink()
  const intro = 'You are invited to the Apex Evaluation workspace, where teams submit Design Thinking, BRD and PRD documents for AI-assisted scoring.'
  const lines = [
    'Hello,', '', intro, '',
    ...(invitation.suggestedTeamName ? [`Suggested team: ${invitation.suggestedTeamName}`, ''] : []),
    ...(invitation.redeemUrl
      ? ['Accept the invitation (sign in with this email address):', accept, '', 'Then open the workspace and choose your team:', workspace]
      : ['Open the workspace, sign in with your work account, and choose your team:', accept]),
  ]
  const link = (url: string) => `<a href="${escapeHtml(url)}" style="color:#0f6cbd;word-break:break-all">${escapeHtml(url)}</a>`
  const html = [
    '<div style="font-family:Segoe UI,Arial,sans-serif;font-size:11pt;color:#242424">',
    '<p>Hello,</p>', `<p>${escapeHtml(intro)}</p>`,
    ...(invitation.suggestedTeamName ? [`<p>Suggested team: <strong>${escapeHtml(invitation.suggestedTeamName)}</strong></p>`] : []),
    '<table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin:16px 0"><tr>',
    `<td bgcolor="#0f6cbd" style="background:#0f6cbd;border-radius:4px;padding:10px 24px">`,
    `<a href="${escapeHtml(accept)}" target="_blank" style="font-family:Segoe UI,Arial,sans-serif;font-size:12pt;font-weight:bold;color:#ffffff;text-decoration:none">`,
    'Accept invitation</a></td></tr></table>',
    invitation.redeemUrl
      ? `<p>Sign in with this email address. If the button doesn't work, open this link:<br>${link(accept)}</p><p>Then open the workspace and choose your team:<br>${link(workspace)}</p>`
      : `<p>Sign in with your work account and choose your team. If the button doesn't work, open this link:<br>${link(accept)}</p>`,
    '</div>',
  ].join('')
  const subject = 'Invitation to the Apex Evaluation workspace'
  return {
    subject, body: lines.join('\n'), html, acceptUrl: accept,
    mailto: `mailto:${encodeURIComponent(invitation.email)}?subject=${encodeURIComponent(subject)}`,
  }
}