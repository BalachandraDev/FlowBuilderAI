import { useEffect, useMemo, useRef, useState } from 'react'
import { ArrowCounterClockwise, Eye, NotePencil, PencilSimple } from '@phosphor-icons/react'
import { useQuery } from '@tanstack/react-query'
import { describeError, get } from '../api/client'
import type { GuideChoice, GuideOutline } from '../api/types'
import { usePref } from '../ux/prefs'
import { Alert, Dialog, Skeletons } from './ui'

const NUMBER = '\u0001'                                  // where the server leaves a section's number
/** The longest a heading can be: the server refuses more. */
export const MAX_HEADING = 120

const escapeHtml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
/** Spaces and line breaks tidied, as the server does. */
const tidy = (s: string) => s.replace(/\s+/g, ' ').trim()

/** The longest text a section can have: the server refuses more. */
export const MAX_TEXT = 8000
/** The headings whose text can be written by the person: the ones that are written text, lists and tables (the server checks the same). */
export const TEXT_EDITABLE: ReadonlySet<string> = new Set(['purpose', 'scope', 'requirements', 'roles', 'usecase', 'stages', 'touchpoints', 'open'])
/** Written text tidied as the server does: one line per paragraph, spaces collapsed, empty lines gone. A line that starts with "- " is a bullet. */
export const cleanText = (s: string) => s.replace(/\r\n?/g, '\n').split('\n').map(l => l.replace(/[ \t]+/g, ' ').trim()).filter(Boolean).join('\n')

/**
 * The words of a section as one line each, to start writing from: a paragraph is a line, a list item is "- item", a table row is its cells joined with a dash.
 * Pictures, the heading and the section number are left out. The person's version replaces the section's text, so lists and tables become plain lines.
 */
export function plainTextOf(html: string): string {
  const doc = new DOMParser().parseFromString(html.split(NUMBER).join('').replace(/<br\s*\/?>/gi, ' '), 'text/html')
  const lines: string[] = []
  const text = (el: Element) => tidy(el.textContent ?? '')
  const walk = (el: Element) => {
    const tag = el.tagName.toUpperCase()
    if (['H2', 'IMG', 'SVG', 'STYLE', 'SCRIPT'].includes(tag)) return
    if (tag === 'LI') { const t = text(el); if (t) lines.push('- ' + t); return }
    if (tag === 'TR') { const cells = [...el.children].map(text).filter(Boolean); if (cells.length) lines.push(cells.join(' – ')); return }
    if (tag === 'P' || tag === 'FIGCAPTION' || /^H[3-6]$/.test(tag)) { const t = text(el); if (t) lines.push(t); return }
    for (const child of el.childNodes) {
      if (child.nodeType === 1) walk(child as Element)
      else if (child.nodeType === 3) { const t = tidy(child.textContent ?? ''); if (t) lines.push(t) }
    }
  }
  walk(doc.body)
  return cleanText(lines.join('\n'))
}

/** A section made from the person's own lines, as the server writes it for the PDF: the heading, a paragraph for each line, a list for the lines that start with "- ". */
export function textSection(title: string, text: string): string {
  let html = `<section><h2><span>${NUMBER}</span>${escapeHtml(title)}</h2>`
  let list = false
  for (const line of cleanText(text).split('\n').filter(Boolean)) {
    if (line.startsWith('- ')) { if (!list) { html += '<ul>'; list = true } html += `<li>${escapeHtml(line.slice(2))}</li>` }
    else { if (list) { html += '</ul>'; list = false } html += `<p>${escapeHtml(line)}</p>` }
  }
  return html + (list ? '</ul>' : '') + '</section>'
}

/**
 * The document as it would be printed with the ticked headings and requests: the server's own page markup for each section (the very markup the PDF is
 * made from), numbered 1, 2, 3 without gaps, in the document's own style. A request's own document starts with its cover; a project or module PDF
 * shows each ticked request as a chapter. The page is shown in a frame that cannot run scripts.
 */
export function composePreview(outline: GuideOutline, sections: Set<string>, requests: Set<string>, titles: Record<string, string> = {}, texts: Record<string, string> = {}): string {
  const ticked = outline.sections.filter(s => sections.has(s.key))
  /** A section as printed: a heading the person renamed is written as each section writes it, </span>Heading</h2> (the same swap the server makes for the PDF). */
  const named = (s: { key: string; title: string }, html: string) => {
    const custom = tidy(titles[s.key] ?? '')
    return custom && custom !== s.title ? html.split(`</span>${escapeHtml(s.title)}</h2>`).join(`</span>${escapeHtml(custom)}</h2>`) : html
  }
  /** The ticked sections of one request (a request's own document has a single page for each heading, whatever its id). */
  const chapter = (flowId?: string) => {
    let n = 0
    return ticked.map(s => {
      const part = flowId === undefined ? s.parts[0] : s.parts.find(p => p.flowId === flowId)
      // text the person wrote replaces the generated text (the key names the request in a project or module PDF, as the server's does)
      const written = TEXT_EDITABLE.has(s.key) ? cleanText(texts[flowId === undefined ? s.key : `${flowId}/${s.key}`] ?? '') : ''
      return part ? `<div id="sec-${s.key}-${flowId ?? 'one'}">${named(s, written ? textSection(s.title, written) : part.html).split(NUMBER).join(String(++n))}</div>` : ''
    }).join('')
  }
  let body: string
  if (outline.requests.length === 0) {
    body = (outline.cover ? `<div class="paper">${outline.cover}</div>` : '') + (ticked.length > 0 ? `<div class="paper">${chapter()}</div>` : '')
  } else {
    body = outline.requests.filter(r => requests.has(r.flowId)).map(r =>
      `<div class="paper" id="req-${r.flowId}"><div class="chapter-head"><div class="chapter-code">${escapeHtml(r.code)}</div><h1>${escapeHtml(r.title)}</h1><div class="meta">${escapeHtml(r.module)}</div></div>${chapter(r.flowId)}</div>`).join('')
  }
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><style>${outline.style}</style></head><body>${body}</body></html>`
}

/**
 * Shown before a PDF is made: on one side every heading the PDF would have (and, for the PDF of a project or a module, every request that can go in
 * it) to tick; on the other the document itself, exactly as it will print, changing as things are ticked. Everything starts ticked. When everything
 * is ticked nothing is sent, so the PDF is the complete one.
 */
export function ChooseContent({ title, outlinePath, confirmLabel, onChoose, onClose }: {
  title: string
  /** Where the headings come from, for example `flows/{id}/guide-outline`. */
  outlinePath: string
  confirmLabel: string
  onChoose: (choice: GuideChoice) => void
  onClose: () => void
}) {
  const outline = useQuery({ queryKey: ['guide-outline', outlinePath], queryFn: () => get<GuideOutline>(outlinePath), gcTime: 0, staleTime: 0, retry: false })
  const data = outline.data
  const [sections, setSections] = useState<Set<string>>(new Set())
  const [requests, setRequests] = useState<Set<string>>(new Set())
  /** On a narrow screen only one side is shown at a time. */
  const [pane, setPane] = useState<'choose' | 'preview'>('choose')
  const frame = useRef<HTMLIFrameElement>(null)
  /** The headings this person renamed, remembered for this document so the next PDF of it starts with them. */
  const [titles, setTitles] = usePref<Record<string, string>>(`guide-titles.${outlinePath}`, {})
  const [renaming, setRenaming] = useState<string | null>(null)
  const [draft, setDraft] = useState('')
  /** The text this person wrote under headings, remembered for this document. `base` is the generated text it was written over: if the notes change and the generated text is no longer that, the old edit is not used. */
  const [written, setWritten] = usePref<Record<string, { text: string; base: string }>>(`guide-texts.${outlinePath}`, {})
  /** The heading whose text is being written and which request's text it is (a project or module PDF has the text of each request). */
  const [writing, setWriting] = useState<{ key: string; ref: string } | null>(null)
  const [textDraft, setTextDraft] = useState('')

  // everything is ticked to begin with
  useEffect(() => {
    if (!data) return
    setSections(new Set(data.sections.map(s => s.key)))
    setRequests(new Set(data.requests.map(r => r.flowId)))
  }, [data])

  const toggle = <T,>(set: Set<T>, put: (s: Set<T>) => void, item: T) => { const next = new Set(set); if (next.has(item)) next.delete(item); else next.add(item); put(next) }
  const allSections = !!data && sections.size === data.sections.length
  const allRequests = !data || requests.size === data.requests.length
  const manyRequests = (data?.requests.length ?? 0) > 1
  const modules = [...new Set((data?.requests ?? []).map(r => r.module))]
  const ready = !!data && sections.size > 0 && (requests.size > 0 || data.requests.length === 0)      // a single request's document has no list of requests
  /** Only the headings of this outline that really differ from the standard ones (an old remembered one for a heading that is gone is ignored). */
  const renamed = useMemo(() => {
    const out: Record<string, string> = {}
    for (const s of data?.sections ?? []) { const t = tidy(titles[s.key] ?? ''); if (t && t !== s.title) out[s.key] = t }
    return out
  }, [data, titles])
  /** A request's own document has plain heading keys; a project or module PDF names the request in front, as the server's keys do. */
  const own = (data?.requests.length ?? 0) === 0
  const refOf = (key: string, flowId: string) => (own ? key : `${flowId}/${key}`)
  /** The generated text of every heading of every request, as lines to start from. */
  const generated = useMemo(() => {
    const out: Record<string, string> = {}
    for (const s of data?.sections ?? []) if (TEXT_EDITABLE.has(s.key)) for (const p of s.parts) out[(data!.requests.length === 0 ? s.key : `${p.flowId}/${s.key}`)] = plainTextOf(p.html)
    return out
  }, [data])
  /** Only the text that really differs from the generated text, and was written over the generated text as it is now (an edit of text that has since changed is not used). */
  const texts = useMemo(() => {
    const used: Record<string, string> = {}; let outdated = 0
    for (const [ref, w] of Object.entries(written)) {
      if (!(ref in generated)) continue
      const text = cleanText(w.text)
      if (cleanText(w.base) !== generated[ref]) { outdated++; continue }
      if (text && text !== generated[ref]) used[ref] = text
    }
    return { used, outdated }
  }, [written, generated])
  const page = useMemo(() => (data ? composePreview(data, sections, requests, renamed, texts.used) : ''), [data, sections, requests, renamed, texts.used])

  function startWriting(key: string, ref: string) { setWriting({ key, ref }); setTextDraft(texts.used[ref] ?? generated[ref] ?? ''); setRenaming(null) }
  function saveWriting() {
    if (!writing) return
    const t = cleanText(textDraft)
    setWritten(prev => { const next = { ...prev }; if (!t || t === generated[writing.ref]) delete next[writing.ref]; else next[writing.ref] = { text: t, base: generated[writing.ref] ?? '' }; return next })
    setWriting(null)
  }
  const resetText = (ref: string) => setWritten(prev => { const next = { ...prev }; delete next[ref]; return next })

  function startRename(key: string, current: string) { setRenaming(key); setDraft(current) }
  function saveRename(key: string, standard: string) {
    const t = tidy(draft)
    if (!t) return
    setTitles(prev => { const next = { ...prev }; if (t === standard) delete next[key]; else next[key] = t; return next })
    setRenaming(null)
  }
  const resetHeading = (key: string) => setTitles(prev => { const next = { ...prev }; delete next[key]; return next })

  /** Scrolls the document to a heading (and shows the document, on a narrow screen). */
  function show(key: string) {
    setPane('preview')
    const firstRequest = data?.requests.find(r => requests.has(r.flowId))?.flowId ?? 'one'
    window.setTimeout(() => frame.current?.contentDocument?.getElementById(`sec-${key}-${firstRequest}`)?.scrollIntoView({ block: 'start' }), 50)
  }

  function confirm() {
    if (!data) return
    const used = Object.fromEntries(Object.entries(renamed).filter(([key]) => sections.has(key)))        // a heading that is not printed needs no new name
    const usedTexts = Object.fromEntries(Object.entries(texts.used).filter(([ref]) => {                  // text of a heading or a request that is not printed is not sent
      const slash = ref.lastIndexOf('/')
      return sections.has(ref.slice(slash + 1)) && (slash < 0 || requests.has(ref.slice(0, slash)))
    }))
    onChoose({
      sections: allSections ? undefined : data.sections.filter(s => sections.has(s.key)).map(s => s.key),
      flowIds: allRequests ? undefined : data.requests.filter(r => requests.has(r.flowId)).map(r => r.flowId),
      ...(Object.keys(used).length > 0 ? { titles: used } : {}),
      ...(Object.keys(usedTexts).length > 0 ? { texts: usedTexts } : {}),
    })
  }

  return (
    <Dialog wide title={title} onClose={onClose} actions={<>
      <button className="btn" onClick={onClose}>Cancel</button>
      <button className="btn primary" onClick={confirm} disabled={!ready}>{confirmLabel}</button>
    </>}>
      {outline.isPending && <Skeletons count={3} />}
      {outline.isError && <Alert tone="error">{describeError(outline.error)}</Alert>}

      {data && (
        <>
          <div className="choose-tabs" role="tablist" aria-label="Choose or preview">
            <button role="tab" aria-selected={pane === 'choose'} className={pane === 'choose' ? 'on' : ''} onClick={() => setPane('choose')}>Choose</button>
            <button role="tab" aria-selected={pane === 'preview'} className={pane === 'preview' ? 'on' : ''} onClick={() => setPane('preview')}>See the document</button>
          </div>
          <div className={`choose-layout ${pane}`}>
            <div className="choose-side">
              <p className="muted small">Tick what should be in the PDF, press the pencil to give a heading your own wording, and the note pad to write the text under it yourself. The document on the right shows exactly what will be printed. The cover is always there.</p>
              <div className="choose-head">
                <h3>Headings</h3>
                <span className="muted small" role="status">{sections.size} of {data.sections.length} ticked</span>
                <button className="btn link" onClick={() => setSections(new Set(data.sections.map(s => s.key)))} disabled={allSections}>Tick all</button>
                <button className="btn link" onClick={() => setSections(new Set())} disabled={sections.size === 0}>Untick all</button>
              </div>
              <ul className="choose-list" aria-label="Headings">
                {data.sections.map(s => {
                  const name = renamed[s.key] ?? s.title                  // what the heading is called in this PDF
                  return (
                    <li key={s.key} className={`choose-row${renaming === s.key ? ' is-renaming' : ''}${writing?.key === s.key ? ' is-writing' : ''}`}>
                      {renaming === s.key ? (
                        <form className="heading-edit" data-escape-own onSubmit={e => { e.preventDefault(); saveRename(s.key, s.title) }} onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); setRenaming(null) } }}>
                          <label className="sr-only" htmlFor={`heading-${s.key}`}>Heading for {name}</label>
                          <input id={`heading-${s.key}`} className="input" autoFocus value={draft} maxLength={MAX_HEADING} onChange={e => setDraft(e.target.value)} onFocus={e => e.currentTarget.select()} />
                          <button className="btn primary" type="submit" disabled={!tidy(draft)}>Save</button>
                          <button className="btn" type="button" onClick={() => setRenaming(null)}>Cancel</button>
                          {tidy(draft) === '' && <div className="muted small heading-edit-note" role="status">Type a heading, or press Cancel to keep “{name}”.</div>}
                        </form>
                      ) : (
                        <>
                          <label className="check">
                            <input type="checkbox" checked={sections.has(s.key)} onChange={() => toggle(sections, setSections, s.key)} />
                            <span><b>{name}</b>{renamed[s.key] && <span className="muted small"> (renamed)</span>}{s.parts.some(p => texts.used[refOf(s.key, p.flowId)] !== undefined) && <span className="muted small"> (text edited)</span>}<br /><span className="muted small">{renamed[s.key] ? `Standard heading: ${s.title}` : s.detail}</span></span>
                          </label>
                          <button className="btn link" aria-label={`Rename the heading ${name}`} title="Rename this heading" onClick={() => startRename(s.key, name)}><PencilSimple size={16} aria-hidden /></button>
                          {renamed[s.key] && <button className="btn link" aria-label={`Use the standard heading ${s.title}`} title={`Back to “${s.title}”`} onClick={() => resetHeading(s.key)}><ArrowCounterClockwise size={16} aria-hidden /></button>}
                          {TEXT_EDITABLE.has(s.key) && s.parts.length > 0 && (
                            <button className="btn link" aria-label={`Edit the text under ${name}`} title="Edit the text under this heading" onClick={() => startWriting(s.key, refOf(s.key, s.parts[0].flowId))}><NotePencil size={16} aria-hidden /></button>
                          )}
                          {s.parts.some(p => texts.used[refOf(s.key, p.flowId)] !== undefined) && (
                            <button className="btn link" aria-label={`Use the generated text under ${name}`} title="Back to the generated text" onClick={() => s.parts.forEach(p => resetText(refOf(s.key, p.flowId)))}><ArrowCounterClockwise size={16} aria-hidden /></button>
                          )}
                          <button className="btn link" aria-label={`Show ${name} in the document`} disabled={!sections.has(s.key)} onClick={() => show(s.key)}><Eye size={16} aria-hidden />Show</button>
                        </>
                      )}
                      {writing?.key === s.key && (
                        <form className="text-edit" data-escape-own onSubmit={e => { e.preventDefault(); saveWriting() }} onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); setWriting(null) } }}>
                          {s.parts.length > 1 && (
                            <label className="text-edit-request">Request
                              <select className="input" value={writing.ref} onChange={e => { setWriting({ key: s.key, ref: e.target.value }); setTextDraft(texts.used[e.target.value] ?? generated[e.target.value] ?? '') }}>
                                {s.parts.map(p => { const r = data.requests.find(x => x.flowId === p.flowId); return <option key={p.flowId} value={refOf(s.key, p.flowId)}>{r ? `${r.code} ${r.title}` : p.flowId}</option> })}
                              </select>
                            </label>
                          )}
                          <label htmlFor={`text-${s.key}`}>Text under {name}</label>
                          <textarea id={`text-${s.key}`} className="input" rows={10} autoFocus value={textDraft} maxLength={MAX_TEXT} onChange={e => setTextDraft(e.target.value)} />
                          <div className="muted small">One line for each paragraph. Start a line with “- ” to make it a bullet. What you write replaces the generated text under this heading in the PDF; a list or table there becomes plain lines. It is kept for the next PDF of this document.</div>
                          <div className="text-edit-actions">
                            <button className="btn primary" type="submit">Save text</button>
                            <button className="btn" type="button" onClick={() => setWriting(null)}>Cancel</button>
                            <button className="btn link" type="button" onClick={() => setTextDraft(generated[writing.ref] ?? '')}>Use the generated text</button>
                          </div>
                        </form>
                      )}
                    </li>
                  )
                })}
              </ul>
              {Object.keys(renamed).length > 0 && (
                <p className="muted small">{Object.keys(renamed).length} heading{Object.keys(renamed).length === 1 ? '' : 's'} renamed. Your wording is kept for the next PDF of this document. <button className="btn link" onClick={() => setTitles({})}>Use the standard headings</button></p>
              )}
              {Object.keys(texts.used).length > 0 && (
                <p className="muted small">{Object.keys(texts.used).length} text{Object.keys(texts.used).length === 1 ? '' : 's'} written by you. <button className="btn link" onClick={() => setWritten({})}>Use the generated text everywhere</button></p>
              )}
              {texts.outdated > 0 && <Alert tone="warn">{texts.outdated} of your edited texts {texts.outdated === 1 ? 'was' : 'were'} not used: the notes were analysed again since you wrote {texts.outdated === 1 ? 'it' : 'them'}, so the generated text under {texts.outdated === 1 ? 'that heading is' : 'those headings are'} new. Press the pencil on a heading to write it again.</Alert>}
              {sections.size === 0 && <Alert tone="warn">Tick at least one heading.</Alert>}

              {manyRequests && (
                <>
                  <div className="choose-head">
                    <h3>Requests</h3>
                    <span className="muted small" role="status">{requests.size} of {data.requests.length} ticked</span>
                    <button className="btn link" onClick={() => setRequests(new Set(data.requests.map(r => r.flowId)))} disabled={allRequests}>Tick all</button>
                    <button className="btn link" onClick={() => setRequests(new Set())} disabled={requests.size === 0}>Untick all</button>
                  </div>
                  {modules.map(m => (
                    <ul key={m} className="choose-list" aria-label={`Requests of ${m}`}>
                      <li className="choose-module">{m}</li>
                      {data.requests.filter(r => r.module === m).map(r => (
                        <li key={r.flowId}>
                          <label className="check">
                            <input type="checkbox" checked={requests.has(r.flowId)} onChange={() => toggle(requests, setRequests, r.flowId)} />
                            <span><b>{r.title}</b><br /><span className="muted small">{r.code}</span></span>
                          </label>
                        </li>
                      ))}
                    </ul>
                  ))}
                  {requests.size === 0 && <Alert tone="warn">Tick at least one request.</Alert>}
                </>
              )}
              {!manyRequests && data.requests.length === 1 && <p className="muted small">Request: {data.requests[0].title} ({data.requests[0].code})</p>}
            </div>

            <div className="choose-doc">
              {ready
                ? <iframe ref={frame} title="The document as it will print" sandbox="allow-same-origin" srcDoc={page} />
                : <div className="empty" role="status"><b>Nothing to show</b><div>Tick at least one heading{manyRequests ? ' and one request' : ''} to see the document.</div></div>}
            </div>
          </div>
        </>
      )}
    </Dialog>
  )
}
