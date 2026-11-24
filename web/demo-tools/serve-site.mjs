// Serves the ../site folder the way GitHub Pages does (under /FlowBuilderAI/), to try the demo locally:  node demo-tools/serve-site.mjs [port]
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../site')
const prefix = '/FlowBuilderAI/'
const port = Number(process.argv[2] ?? process.env.PORT ?? 4173)
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.woff': 'font/woff', '.ico': 'image/x-icon', '.webmanifest': 'application/manifest+json' }
http.createServer((req, res) => {
  const url = new URL(req.url ?? '/', 'http://x')
  if (url.pathname === '/') { res.writeHead(302, { Location: prefix }); res.end(); return }
  if (!url.pathname.startsWith(prefix)) { res.writeHead(404); res.end('Not found'); return }
  let file = path.join(root, decodeURIComponent(url.pathname.slice(prefix.length)))
  if (!file.startsWith(root)) { res.writeHead(403); res.end(); return }
  if (fs.existsSync(file) && fs.statSync(file).isDirectory()) file = path.join(file, 'index.html')
  if (!fs.existsSync(file)) { res.writeHead(404, { 'Content-Type': 'text/plain' }); res.end('Not found'); return }   // like Pages: no fallback to index.html
  res.writeHead(200, { 'Content-Type': types[path.extname(file)] ?? 'application/octet-stream' })
  fs.createReadStream(file).pipe(res)
}).listen(port, () => console.log(`site on http://localhost:${port}${prefix}`))
