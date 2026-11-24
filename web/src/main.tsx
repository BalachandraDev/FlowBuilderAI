import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { registerSW } from 'virtual:pwa-register'
import App from './App'
import { applyTheme } from './lib/theme'
import '@fontsource-variable/geist'
import '@fontsource-variable/geist-mono'
import './styles.css'

applyTheme()
registerSW({ immediate: true })           // keeps the app itself available offline and updates it quietly in the background

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
