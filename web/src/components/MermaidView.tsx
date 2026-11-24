import { useEffect, useId, useRef, useState } from 'react'

let loading: Promise<typeof import('mermaid')['default']> | null = null
/** The diagram library is large, so it is only fetched the first time a diagram is shown. */
function loadMermaid() {
  loading ??= import('mermaid').then(m => m.default)
  return loading
}

/** Draws a Mermaid flowchart. If it cannot be drawn, says so and offers the source instead of showing a blank space. */
export function MermaidView({ source, dark }: { source: string; dark: boolean }) {
  const host = useRef<HTMLDivElement>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(true)
  const id = 'mmd' + useId().replace(/[^a-zA-Z0-9]/g, '')

  useEffect(() => {
    let cancelled = false
    setBusy(true)
    setProblem(null)
    if (!source.trim()) { setBusy(false); return }
    loadMermaid()
      .then(async mermaid => {
        mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: dark ? 'dark' : 'default', flowchart: { useMaxWidth: true, htmlLabels: false } })
        const { svg } = await mermaid.render(`${id}-${Date.now()}`, source)
        if (!cancelled && host.current) host.current.innerHTML = svg
      })
      .catch(e => { if (!cancelled) { setProblem(e instanceof Error ? e.message : 'The diagram could not be drawn.'); if (host.current) host.current.innerHTML = '' } })
      .finally(() => { if (!cancelled) setBusy(false) })
    return () => { cancelled = true }
  }, [source, dark, id])

  if (!source.trim()) return <div className="muted">There is no diagram yet.</div>
  return (
    <>
      {busy && <div className="skeleton" aria-busy="true" aria-label="Drawing the diagram" />}
      <div ref={host} className="diagram" role="img" aria-label="Flow diagram" />
      {problem && (
        <div className="alert info" role="status">
          <div>
            The diagram could not be drawn here ({problem}).
            <details><summary>Diagram source</summary><pre className="source">{source}</pre></details>
          </div>
        </div>
      )}
    </>
  )
}
