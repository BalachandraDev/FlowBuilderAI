import { useEffect } from 'react'

/** While something typed has not been saved, closing or reloading the page asks first. (Moving to another page of the app is asked about by the page itself.) */
export function useUnsavedWarning(dirty: boolean) {
  useEffect(() => {
    if (!dirty) return undefined
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])
}
