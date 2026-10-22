// Local integration check only: temporary empty DB, no seeded identities or judge calls.
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { once } from 'node:events'

const dotnet = process.env.DOTNET_HOST_PATH || 'dotnet'
const directory = await mkdtemp(join(tmpdir(), 'apex-evaluation-'))
const project = resolve('services/evaluation/Evaluation.Api')
const environment = {
  ...process.env,
  ConnectionStrings__Evaluation: `Data Source=${join(directory, 'evaluation.db')}`,
  Database__Provider: 'Sqlite',
  Storage__Provider: 'Local',
  Storage__Root: join(directory, 'private'),
  // Do not inherit real identity configuration for an unauthenticated smoke check.
  Entra__TenantId: '',
  Entra__Audience: '',
}
let server
async function run(args) {
  const child = spawn(dotnet, args, { env: environment, stdio: 'inherit' })
  const result = await Promise.race([
    once(child, 'exit').then(([code]) => code),
    once(child, 'error').then(([error]) => { throw error }),
  ])
  if (result !== 0) throw new Error(`dotnet command exited ${result}`)
}
try {
  await run(['build', project, '--nologo'])
  // Repeat migrations to verify initialization is idempotent on durable storage.
  await run(['run', '--no-build', '--project', project, '--', '--initialize-database'])
  await run(['run', '--no-build', '--project', project, '--', '--initialize-database'])
  // Port 0 avoids competing with the user's API/dev server.
  server = spawn(dotnet, [join(project, 'bin/Debug/net8.0/Evaluation.Api.dll'), '--urls', 'http://127.0.0.1:0'], {
    env: environment, stdio: ['ignore', 'pipe', 'pipe'],
  })
  const address = await new Promise((accept, reject) => {
    let output = ''
    const timeout = setTimeout(() => reject(new Error('API readiness timed out')), 30000)
    server.on('error', reject)
    server.on('exit', code => reject(new Error(`API exited before readiness (${code})`)))
    server.stdout.on('data', chunk => {
      output += chunk.toString()
      const match = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)
      if (match) { clearTimeout(timeout); accept(match[1]) }
    })
    server.stderr.on('data', chunk => process.stderr.write(chunk))
  })
  const health = await fetch(`${address}/health`)
  const status = await health.json()
  if (health.status !== 200 || status.status !== 'running') throw new Error('Unexpected health response')
  for (const path of ['/api/teams', '/api/teams/00000000-0000-0000-0000-000000000001/projects']) {
    const response = await fetch(`${address}${path}`)
    if (response.status !== 401) throw new Error(`Unauthenticated access was not denied: ${path}`)
  }
  console.log('PASS: durable migrations (twice), live API liveness, unauthenticated access denial.')
} finally {
  if (server && server.exitCode === null) {
    const exited = once(server, 'exit')
    server.kill()
    await exited
  }
  await rm(directory, { recursive: true, force: true })
}
