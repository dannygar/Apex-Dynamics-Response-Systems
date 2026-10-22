// Client-side preparation of uploads. The API re-validates everything; these checks only give early feedback.
import type { Role } from './contracts'

export const MAX_DT_FILES = 300
export const MAX_SOURCE_FILES = 300
export const SOURCE_EXTENSIONS = ['.pdf', '.docx', '.md', '.txt']
export const MAX_FILE_BYTES = 20 * 1024 * 1024
export const MAX_SUBMISSION_BYTES = 100 * 1024 * 1024
export const MAX_PATH_LENGTH = 260

export interface BundleFile { file: File; path: string }
export interface PreparedBundle { files: BundleFile[]; skipped: string[]; errors: string[] }

export function relativePath(file: File): string {
  return (file.webkitRelativePath || file.name).replace(/\\/g, '/')
}

// Mirrors Artifacts.NormalizePath on the server: relative, no '.', '..', drive, empty or padded segments.
export function pathProblem(path: string, markdownOnly = true): string | null {
  if (!path || path.length > MAX_PATH_LENGTH) return `Path must be 1–${MAX_PATH_LENGTH} characters`
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(path)) return 'Path contains control characters'
  if (path.startsWith('/') || path.includes(':')) return 'Path must be relative'
  if (path.split('/').some(s => !s || s === '.' || s === '..' || s.trim() !== s)) return "Path has '.', '..', empty or padded segments"
  if (markdownOnly && !path.toLowerCase().endsWith('.md')) return 'DT bundles accept Markdown (.md) files only'
  return null
}

interface BundleRules { extensions: string[]; maxFiles: number; markdownOnly: boolean; kind: string; none: string }
const DT_RULES: BundleRules = { extensions: ['.md'], maxFiles: MAX_DT_FILES, markdownOnly: true, kind: 'A DT bundle',
  none: 'No Markdown (.md) files were selected.' }
const SOURCE_RULES: BundleRules = { extensions: SOURCE_EXTENSIONS, maxFiles: MAX_SOURCE_FILES, markdownOnly: false,
  kind: 'A source evidence upload', none: 'No PDF, DOCX, Markdown or text files were selected.' }

function prepareBundle(selection: File[], rules: BundleRules): PreparedBundle {
  const files: BundleFile[] = []
  const skipped: string[] = []
  const errors: string[] = []
  const seen = new Set<string>()
  for (const file of selection) {
    const path = relativePath(file)
    const name = path.split('/').pop() ?? ''
    // Folder pickers include images, OS metadata and hidden files; only supported document types are uploaded.
    if (!rules.extensions.some(ext => name.toLowerCase().endsWith(ext)) || name.startsWith('.')) { skipped.push(path); continue }
    const problem = pathProblem(path, rules.markdownOnly)
    if (problem) { errors.push(`${path}: ${problem}`); continue }
    const key = path.toLowerCase()
    if (seen.has(key)) { errors.push(`${path}: duplicate path`); continue }
    seen.add(key)
    if (file.size === 0) errors.push(`${path}: file is empty`)
    else if (file.size > MAX_FILE_BYTES) errors.push(`${path}: larger than 20 MB`)
    files.push({ file, path })
  }
  files.sort((a, b) => a.path.localeCompare(b.path))
  if (!files.length && !errors.length) errors.push(rules.none)
  if (files.length > rules.maxFiles) errors.push(`${rules.kind} can contain at most ${rules.maxFiles} files (selected ${files.length}).`)
  if (files.reduce((sum, f) => sum + f.file.size, 0) > MAX_SUBMISSION_BYTES) errors.push('The selection exceeds the 100 MB submission limit.')
  return { files, skipped, errors }
}

export const prepareDtBundle = (selection: File[]) => prepareBundle(selection, DT_RULES)
// Source evidence: a folder or several files (PDF, DOCX, Markdown, text) uploaded together as one bundle.
export const prepareSourceBundle = (selection: File[]) => prepareBundle(selection, SOURCE_RULES)

export function bundleFormData(role: 'DT' | 'Source', bundle: BundleFile[]): FormData {
  const body = new FormData()
  body.set('role', role)
  for (const { file, path } of bundle) {
    body.append('file', file, path.split('/').pop())
    body.append('paths', path)
  }
  return body
}

export const dtFormData = (bundle: BundleFile[]) => bundleFormData('DT', bundle)

export function singleFormData(role: Exclude<Role, 'DT'>, file: File): FormData {
  const body = new FormData()
  body.set('role', role)
  body.append('file', file, file.name)
  return body
}

export const markdownRecommended = (name: string) => !name.toLowerCase().endsWith('.md')
