// Builds web/src/demo/fixtures.json from a recording made by record.mjs against a real server in Demo AI mode.
//   node demo-tools/build-fixtures.mjs <recording folder>
import fs from 'node:fs'
const rec = process.argv[2]
if (!rec) { console.error('usage: node demo-tools/build-fixtures.mjs <recording folder>'); process.exit(1) }
const calls = JSON.parse(fs.readFileSync(`${rec}/calls.json`, 'utf8'))
const last = (role, method, re) => [...calls].reverse().find(c => c.role === role && c.method === method && re.test(c.path))
const j = c => JSON.parse(c.body)

const flow = j(last('user', 'GET', /flows\/[0-9a-f-]+$/))
const projects = j(last('user', 'GET', /\/projects$/))[0]

// the document as the server drafted it; the values that belong to one project become {{placeholders}}, filled in again for every project
let outline = JSON.stringify(j(last('user', 'GET', /guide-outline/)))
const swap = (from, to) => { outline = outline.split(from).join(to) }
swap(projects.hospitalName, '{{PROJECT}}')
swap(`IMPL-${projects.code}-NR-001`, '{{DOCNO}}')
swap(projects.code, '{{CODE}}')
swap(flow.requestCode, '{{REQ}}')
swap(projects.leadName, '{{USER}}')
swap('24-11-2026', '{{DATE}}')
// the public demo carries no company branding
swap('AOSTA INDIA PRIVATE LIMITED', 'FlowBuilderAI')
swap('<div class=\\"product\\">BACKBONE</div>', '<div class=\\"product\\">DEMO</div>')   // the outline is JSON text here, so its quotes are escaped
console.log('AOSTA left:', outline.split('AOSTA').length - 1, ' BACKBONE left:', outline.split('BACKBONE').length - 1)

const clar = j(last('user', 'GET', /clarifications$/)).map(c => ({ ...c, status: 'Open', answer: null }))
const content = j(last('user', 'GET', /flows\/.+\/content$/))
const modules = j(last('admin', 'GET', /master\/departments$/))
const out = {
  outline,
  svg: fs.readFileSync(`${rec}/diagram.svg`, 'utf8'),
  content: { content: content.content, lockedIds: content.lockedIds, source: content.source },
  clarifications: clar.map(({ questionKey, section, question, options, isBlocking }) => ({ questionKey, section, question, options, isBlocking })),
  modules: modules.map(m => ({ code: m.code, name: m.name })),
  languages: j(last('user', 'GET', /master\/languages/)),
  categories: j(last('user', 'GET', /requirement-categories/)),
  aiAdmin: (() => { const a = j(last('admin', 'GET', /admin\/ai$/)); return { ...a, hasKey: false, keyHint: null, groqKeyHint: null } })(),
  notice: j(last('user', 'GET', /system\/info/)).notice,
}
fs.writeFileSync(new URL('../src/demo/fixtures.json', import.meta.url), JSON.stringify(out))
console.log('fixtures written:', Math.round(JSON.stringify(out).length / 1024), 'KB;', out.modules.length, 'modules,', out.clarifications.length, 'questions')
