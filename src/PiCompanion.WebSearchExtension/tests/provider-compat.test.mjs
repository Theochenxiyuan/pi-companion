import assert from 'node:assert/strict'
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { spawnSync } from 'node:child_process'
import test from 'node:test'

async function fixture(t) {
  const root = await mkdtemp(join(tmpdir(), 'pi-provider-compat-'))
  t.after(() => rm(root, { recursive: true, force: true }))
  const providers = join(root, 'node_modules/pi-web-search/src/providers')
  await mkdir(providers, { recursive: true })
  await mkdir(join(root, 'scripts'))
  await mkdir(join(root, 'patches'))
  await copyFile(resolve('scripts/apply-provider-compat.mjs'), join(root, 'scripts/apply-provider-compat.mjs'))
  await copyFile(resolve('patches/pi-web-search+1.6.0.patch'), join(root, 'patches/pi-web-search+1.6.0.patch'))
  await writeFile(join(root, 'node_modules/pi-web-search/package.json'), JSON.stringify({ version: '1.6.0' }))
  for (const name of ['anthropic', 'auth', 'google', 'openai']) {
    await copyFile(resolve(`node_modules/pi-web-search/src/providers/${name}.ts`), join(providers, `${name}.ts`))
  }
  return {
    root,
    providers,
    run: () => spawnSync(process.execPath, [join(root, 'scripts/apply-provider-compat.mjs')], { cwd: root, encoding: 'utf8' }),
  }
}

test('provider compatibility can be reapplied without changing source files', async (t) => {
  const { providers, run } = await fixture(t)
  const source = await readFile(join(providers, 'auth.ts'), 'utf8')
  assert.match(source, /export function applyProviderHeaders/u)
  const result = run()
  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /0 files updated/u)
  assert.equal(await readFile(join(providers, 'auth.ts'), 'utf8'), source)
})

test('provider source drift aborts before writing any files', async (t) => {
  const { providers, run } = await fixture(t)
  const anthropicPath = join(providers, 'anthropic.ts')
  const original = (await readFile(anthropicPath, 'utf8')).replace(
    'import { applyProviderHeaders, getAuth, getProviderSessionHeaders }',
    'import { getAuth, getProviderSessionHeaders }',
  )
  await writeFile(anthropicPath, original)
  const authPath = join(providers, 'auth.ts')
  await writeFile(authPath, (await readFile(authPath, 'utf8')).replace(
    'export function applyProviderHeaders(', 'export function changedUpstreamHeaders(',
  ))
  const result = run()
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /Provider source changed/u)
  assert.equal(await readFile(anthropicPath, 'utf8'), original)
})

test('provider package upgrades require reviewing the pinned compatibility patch', async (t) => {
  const { root, run } = await fixture(t)
  await writeFile(join(root, 'node_modules/pi-web-search/package.json'), JSON.stringify({ version: '1.7.0' }))
  const result = run()
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /requires pi-web-search 1\.6\.0, received 1\.7\.0/u)
})
