// Switches the app to the browser demo: API calls are answered by demo/backend.ts instead of a server, and a small panel lets a visitor
// sign in as the consultant or the administrator, or start again. Only loaded when the app is built with VITE_DEMO=true.
import { DEMO_ACCOUNTS, handleDemoRequest, resetDemoData } from './backend'

const REFRESH_KEY = 'fc.refresh'
const OPEN_KEY = 'fc.demo.panel'

export function installDemo(options: { latencyMs?: number; banner?: boolean } = {}) {
  const real = window.fetch.bind(window)
  const latency = options.latencyMs ?? 90
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const req = input instanceof Request ? input : null
    const url = req ? req.url : input instanceof URL ? input.href : String(input)
    if (!url.includes('/api/v1/')) return real(input, init)
    if (init?.signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    const body = init?.body ?? (req ? await req.clone().text() : undefined)
    if (latency > 0) await new Promise(r => setTimeout(r, latency))
    const res = await handleDemoRequest({ url, method: init?.method ?? req?.method ?? 'GET', headers: init?.headers ?? req?.headers, body })
    return res ?? real(input, init)
  }
  if (options.banner !== false) mountPanel()
}

async function signInAs(kind: keyof typeof DEMO_ACCOUNTS) {
  const a = DEMO_ACCOUNTS[kind]
  const res = await handleDemoRequest({ url: '/api/v1/auth/login', method: 'POST', body: JSON.stringify({ email: a.email, password: a.password }) })
  const t = (await res?.json()) as { refreshToken: string } | undefined
  if (!t) return
  try { localStorage.setItem(REFRESH_KEY, t.refreshToken) } catch { /* the sign-in page still works by hand */ }
  try { sessionStorage.setItem(OPEN_KEY, '0') } catch { /* the panel may stay open */ }          // signed in: the panel gets out of the way
  location.hash = '#/'
  location.reload()
}

function mountPanel() {
  const style = document.createElement('style')
  style.textContent = `
  .demo-dock{position:fixed;left:12px;bottom:12px;z-index:2147483000;font:14px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif;color:#0f172a;max-width:min(360px,calc(100vw - 24px))}
  @media (max-width:820px){.demo-dock{bottom:calc(76px + env(safe-area-inset-bottom))}}
  .demo-chip{display:inline-flex;align-items:center;gap:8px;border:1px solid #c9d3ea;background:#fff;color:#1d3a8a;border-radius:999px;padding:8px 14px;font-weight:600;cursor:pointer;box-shadow:0 6px 20px -8px rgba(15,23,42,.4)}
  .demo-chip i{width:8px;height:8px;border-radius:50%;background:#f59e0b;display:inline-block}
  .demo-panel{margin-bottom:8px;background:#fff;border:1px solid #c9d3ea;border-radius:14px;padding:14px 14px 12px;box-shadow:0 18px 40px -18px rgba(15,23,42,.5)}
  .demo-panel h2{font-size:15px;margin:0 0 6px}
  .demo-panel p{margin:0 0 10px;color:#475569}
  .demo-panel .demo-row{display:flex;flex-wrap:wrap;gap:8px;margin:0 0 10px}
  .demo-panel button,.demo-panel a.demo-btn{font:inherit;border-radius:9px;border:1px solid #c9d3ea;background:#fff;color:#0f172a;padding:7px 11px;cursor:pointer;text-decoration:none}
  .demo-panel button.primary{background:#2457d6;border-color:#2457d6;color:#fff}
  .demo-cred{display:flex;flex-direction:column;margin:0 0 6px;font-size:13px;color:#475569}
  .demo-cred b{color:#0f172a;font-weight:600}.demo-panel code{font-size:12px;white-space:nowrap;background:#eef2f8;border-radius:5px;padding:1px 5px}
  @media (prefers-color-scheme:dark){.demo-chip,.demo-panel{background:#121a2b;border-color:#2b3a5c;color:#eef2fa}.demo-chip{color:#b8c9ff}.demo-panel p,.demo-cred{color:#aab5cf}.demo-cred b{color:#eef2fa}.demo-panel code{background:#1b2740}.demo-panel button,.demo-panel a.demo-btn{background:#121a2b;border-color:#2b3a5c;color:#eef2fa}.demo-panel button.primary{background:#6c93ff;border-color:#6c93ff;color:#0b1220}}
  `
  document.head.appendChild(style)

  const dock = document.createElement('div')
  dock.className = 'demo-dock'
  dock.setAttribute('role', 'region'); dock.setAttribute('aria-label', 'Demo controls')
  const c = DEMO_ACCOUNTS.consultant, a = DEMO_ACCOUNTS.admin
  dock.innerHTML = `
    <div class="demo-panel" id="demo-panel" hidden>
      <h2>Demo: runs in your browser</h2>
      <p>No server and no AI call: the app talks to a small demo backend inside this page. The analysis returns one fixed example, as the real app does in its demo mode. Your changes stay in this browser.</p>
      <div class="demo-row">
        <button class="primary" data-act="consultant">Sign in as consultant</button>
        <button data-act="admin">Sign in as administrator</button>
      </div>
      <div class="demo-cred"><b>${c.label}</b><span><code>${c.email}</code> <code>${c.password}</code></span></div>
      <div class="demo-cred"><b>${a.label}</b><span><code>${a.email}</code> <code>${a.password}</code></span></div>
      <div class="demo-row" style="margin-top:10px">
        <button data-act="reset">Reset demo</button>
        <a class="demo-btn" href="../" data-act="about">About the project</a>
      </div>
    </div>
    <button class="demo-chip" id="demo-chip" aria-expanded="false" aria-controls="demo-panel"><i aria-hidden="true"></i>Demo</button>`
  document.body.appendChild(dock)

  const panel = dock.querySelector<HTMLElement>('#demo-panel')!
  const chip = dock.querySelector<HTMLButtonElement>('#demo-chip')!
  const set = (open: boolean) => { panel.hidden = !open; chip.setAttribute('aria-expanded', String(open)); try { sessionStorage.setItem(OPEN_KEY, open ? '1' : '0') } catch { /* remembering is optional */ } }
  let signedIn = false
  try { signedIn = !!localStorage.getItem(REFRESH_KEY) } catch { /* treated as signed out */ }
  let remembered: string | null = null
  try { remembered = sessionStorage.getItem(OPEN_KEY) } catch { /* nothing remembered */ }
  // open at first on a wide screen (it explains the demo); on a phone it would cover the sign-in form, so it starts closed there
  set(remembered === null ? !signedIn && window.innerWidth > 820 : remembered === '1')
  chip.addEventListener('click', () => set(Boolean(panel.hidden)))
  dock.addEventListener('click', e => {
    const act = (e.target as HTMLElement).closest<HTMLElement>('[data-act]')?.dataset.act
    if (act === 'consultant' || act === 'admin') void signInAs(act)
    if (act === 'reset') { resetDemoData(); try { localStorage.removeItem(REFRESH_KEY) } catch { /* already gone */ } location.hash = '#/login'; location.reload() }
  })
  dock.addEventListener('keydown', e => { if (e.key === 'Escape' && !panel.hidden) { set(false); chip.focus() } })
}
