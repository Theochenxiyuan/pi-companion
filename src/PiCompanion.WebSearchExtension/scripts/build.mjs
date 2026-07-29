import { copyFile, mkdir, rm } from 'node:fs/promises'
import { build } from 'esbuild'

await rm('dist', { recursive: true, force: true })
await mkdir('dist', { recursive: true })
await build({
  entryPoints: ['src/index.ts'],
  bundle: true,
  platform: 'node',
  format: 'esm',
  target: 'node20',
  outfile: 'dist/pi-web-search.mjs',
  legalComments: 'none',
  alias: {
    '@earendil-works/pi-coding-agent': './src/pi-coding-agent-runtime.ts',
    '@earendil-works/pi-ai/compat': './src/pi-ai-compat.ts',
  },
})
await copyFile('legal/pi-web-search.mjs.LEGAL.txt', 'dist/pi-web-search.mjs.LEGAL.txt')
