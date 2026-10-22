import { useState } from 'react'
import { invitationDraft } from './access'
import type { Invitation } from './contracts'

const PASTE = 'Invitation copied. In the new Outlook message, click above your signature and press Ctrl+V (⌘V on Mac), then send.'
const PLAIN = 'Your browser copied the invitation as plain text only. In the new Outlook message, press Ctrl+V above your signature, then send.'
const FAILED = 'Copying is blocked in this browser. The Outlook message is open; copy the invitation link from the invitations list instead.'

// "Email from Outlook": copies the formatted invitation (Accept invitation button and clickable links) as HTML, then opens
// a new message to the invitee with only the subject filled in so the sender's signature stays.
export function OutlookEmailButton({ invitation, label = 'Email from Outlook', ariaLabel }: {
  invitation: Pick<Invitation, 'email' | 'redeemUrl' | 'suggestedTeamName'>; label?: string; ariaLabel?: string
}) {
  const [status, setStatus] = useState('')
  const send = async () => {
    const draft = invitationDraft(invitation)
    let outcome: string
    try {
      // The clipboard write must start inside the click; Outlook opening afterwards would take focus and block it.
      await navigator.clipboard.write([new ClipboardItem({
        'text/html': new Blob([draft.html], { type: 'text/html' }), 'text/plain': new Blob([draft.body], { type: 'text/plain' }),
      })])
      outcome = PASTE
    } catch {
      try { await navigator.clipboard.writeText(draft.body); outcome = PLAIN } catch { outcome = FAILED }
    }
    window.open(draft.mailto, '_self')
    setStatus(outcome)
  }
  return <>
    <button type="button" className="eval-button" aria-label={ariaLabel} onClick={() => void send()}>{label}</button>
    {status && <span role="status" className="eval-muted"> {status}</span>}
  </>
}
