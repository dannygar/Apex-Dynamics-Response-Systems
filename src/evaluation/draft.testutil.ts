import { fireEvent, waitFor } from '@testing-library/react'
import { expect, vi } from 'vitest'

// Test helper: clicks an "Email from Outlook" button and returns what was copied (HTML + plain text) and the mailto opened.
class FakeClipboardItem { readonly items: Record<string, Blob>; constructor(items: Record<string, Blob>) { this.items = items } }
const read = (blob: Blob) => new Promise<string>(resolve => {
  const reader = new FileReader()
  reader.onload = () => resolve(String(reader.result))
  reader.readAsText(blob)
})

export async function outlookEmail(button: Element) {
  const written: Record<string, Blob>[] = []
  vi.stubGlobal('ClipboardItem', FakeClipboardItem)
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
    write: vi.fn(async (items: FakeClipboardItem[]) => { written.push(items[0].items) }), writeText: vi.fn(async () => undefined),
  } })
  const open = vi.spyOn(window, 'open').mockImplementation(() => null)
  open.mockClear()
  fireEvent.click(button)
  await waitFor(() => expect(open).toHaveBeenCalled())
  const item = written.at(-1)!
  return { html: await read(item['text/html']), text: await read(item['text/plain']), mailto: String(open.mock.calls[0][0]), target: open.mock.calls[0][1] }
}