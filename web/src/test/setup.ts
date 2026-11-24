import '@testing-library/jest-dom/vitest'
import 'fake-indexeddb/auto'
import { afterEach } from 'vitest'
import { cleanup } from '@testing-library/react'

afterEach(() => {
  cleanup()
  localStorage.clear()
})

// jsdom cannot make addresses for files, scroll, or play media
if (!URL.createObjectURL) {
  URL.createObjectURL = () => 'blob:test'
  URL.revokeObjectURL = () => undefined
}
window.scrollTo = () => undefined
Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true })      // a real browser treats localhost as secure
HTMLAnchorElement.prototype.click = function click() { /* a download link: nothing to navigate to in a test */ }

// jsdom has no matchMedia
if (!window.matchMedia) {
  window.matchMedia = ((q: string) => ({ matches: false, media: q, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false })) as typeof window.matchMedia
}
