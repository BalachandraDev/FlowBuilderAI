// Builds the browser demo into ../site/app:  npm run build:demo
// (a script instead of "cross-env", so setting the variable works the same on Windows, macOS and Linux)
import { spawnSync } from 'node:child_process'
const env = { ...process.env, VITE_DEMO: 'true' }
const run = (cmd, args) => { const r = spawnSync(cmd, args, { stdio: 'inherit', env, shell: process.platform === 'win32' }); if (r.status !== 0) process.exit(r.status ?? 1) }
run('npx', ['tsc', '-b'])
run('npx', ['vite', 'build'])
