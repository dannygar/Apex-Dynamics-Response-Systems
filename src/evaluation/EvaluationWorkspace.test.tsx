// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import EvaluationWorkspace from './EvaluationWorkspace'

vi.mock('./auth', () => ({ identityConfigured: false, auth: {} }))
afterEach(cleanup)
it('explains missing configuration without presenting pretend teams or scores', () => {
  render(<EvaluationWorkspace />)
  expect(screen.getByRole('heading', { name: 'Sign-in configuration required' })).toBeTruthy()
  expect(screen.getByText(/no demonstration scores/)).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Run scan' })).toBeNull()
  expect(screen.queryByText('Pass')).toBeNull()
})
