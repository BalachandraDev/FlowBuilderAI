import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { registerSW } from 'virtual:pwa-register'
import App from './App'
import { applyTheme } from './lib/theme'
import '@fontsource-variable/geist'
import '@fontsource-variable/geist-mono'
import './styles.css'

applyTheme()
registerSW({ immediate: true })           // keeps the app itself available offline and updates it quietly in the background (does nothing in the browser demo)

async function start() {
  // The browser demo (GitHub Pages) has no server: a small backend inside the page answers the API calls.
  if (import.meta.env.VITE_DEMO === 'true') (await import('./demo/install')).installDemo()
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  )
}
void start()
