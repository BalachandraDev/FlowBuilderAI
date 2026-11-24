export type ThemeChoice = 'system' | 'light' | 'dark'
const KEY = 'fc.theme'

export function getTheme(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY)
    return v === 'light' || v === 'dark' ? v : 'system'
  } catch { return 'system' }
}

export const isDark = (): boolean =>
  document.documentElement.dataset.theme === 'dark' || (document.documentElement.dataset.theme === 'system' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches)

export function applyTheme(choice: ThemeChoice = getTheme()): void {
  document.documentElement.dataset.theme = choice
  try { localStorage.setItem(KEY, choice) } catch { /* the choice lasts until the tab closes */ }
  const dark = choice === 'dark' || (choice === 'system' && window.matchMedia?.('(prefers-color-scheme: dark)').matches)
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#0f1216' : '#1b2430')
}
