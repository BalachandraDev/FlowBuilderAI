import { useRef, useState } from 'react'
import { ArrowDown, ArrowUp, ImagesSquare, Trash } from '@phosphor-icons/react'
import { useQueryClient } from '@tanstack/react-query'
import { api, del, describeError } from '../api/client'
import { keys, useContent, useFigures } from '../api/hooks'
import type { Figure, Flow } from '../api/types'
import { useApiImage } from '../lib/files'
import { preparePhoto } from '../lib/image'
import { Alert, ConfirmDialog, Skeletons, useToast } from './ui'

const ACCEPT = 'image/png,image/jpeg,image/webp,image/*'
const MAX_FIGURES = 20

/** "screen shot 12.png" becomes "screen shot 12": a caption to start from, which the person then corrects. */
export const captionFromName = (name: string) => name.replace(/\.[a-z0-9]{2,5}$/i, '').replace(/[_-]+/g, ' ').trim().slice(0, 300)

function Thumb({ id }: { id: string }) {
  const { url, failed } = useApiImage(`figures/${id}/image`)
  if (failed) return <div className="figure-thumb muted small">The picture could not be loaded.</div>
  if (!url) return <div className="figure-thumb skeleton" aria-busy="true" />
  return <img className="figure-thumb" src={url} alt="" />
}

function FigureRow({ figure, steps, index, count, canChange, onMove }: {
  figure: Figure; steps: { id: string; label: string }[]; index: number; count: number; canChange: boolean; onMove: (dir: -1 | 1) => void
}) {
  const qc = useQueryClient()
  const toast = useToast()
  const [caption, setCaption] = useState(figure.caption)
  const [confirm, setConfirm] = useState(false)
  const [busy, setBusy] = useState(false)
  const refresh = () => qc.invalidateQueries({ queryKey: keys.figures(figure.flowId) })

  async function save(next: { caption?: string; stepId?: string | null }) {
    setBusy(true)
    try { await api(`figures/${figure.id}`, { method: 'PUT', body: { caption: next.caption ?? caption, stepId: next.stepId === undefined ? figure.stepId : next.stepId } }); await refresh() }
    catch (e) { toast(describeError(e), 'error') } finally { setBusy(false) }
  }
  async function remove() {
    setConfirm(false); setBusy(true)
    try { await del(`figures/${figure.id}`); toast('The picture was removed.', 'good'); await refresh() } catch (e) { toast(describeError(e), 'error'); setBusy(false) }
  }
  const step = steps.find(s => s.id === figure.stepId)

  return (
    <li className="figure-row">
      <Thumb id={figure.id} />
      <div className="figure-main">
        <div className="figure-no"><b>Figure {index + 1}</b><span className="muted small">{figure.originalName} · {Math.max(1, Math.round(figure.sizeBytes / 1024))} KB · {figure.uploadedByName}</span></div>
        {canChange ? (
          <>
            <label className="sr-only" htmlFor={`cap-${figure.id}`}>Caption of figure {index + 1}</label>
            <input id={`cap-${figure.id}`} className="input" value={caption} maxLength={300} placeholder="What does this picture show?"
              onChange={e => setCaption(e.target.value)} onBlur={() => { if (caption.trim() !== figure.caption) void save({ caption: caption.trim() }) }} />
            <label className="sr-only" htmlFor={`step-${figure.id}`}>Step of figure {index + 1}</label>
            <select id={`step-${figure.id}`} className="input" value={figure.stepId ?? ''} disabled={busy} onChange={e => void save({ stepId: e.target.value || null })}>
              <option value="">Not tied to a step (printed with the attachments)</option>
              {figure.stepId && !step && <option value={figure.stepId}>{figure.stepId}</option>}
              {steps.map(s => <option key={s.id} value={s.id}>{s.label}</option>)}
            </select>
            <div className="note-actions">
              <button className="btn text" disabled={busy || index === 0} onClick={() => onMove(-1)} aria-label={`Move figure ${index + 1} earlier`}><ArrowUp size={16} aria-hidden />Earlier</button>
              <button className="btn text" disabled={busy || index === count - 1} onClick={() => onMove(1)} aria-label={`Move figure ${index + 1} later`}><ArrowDown size={16} aria-hidden />Later</button>
              <button className="btn text" disabled={busy} onClick={() => setConfirm(true)} aria-label={`Remove figure ${index + 1}`}><Trash size={16} aria-hidden />Remove</button>
            </div>
          </>
        ) : (
          <>
            <p className="note-text">{figure.caption || <span className="muted">No caption.</span>}</p>
            {step && <div className="muted small">Printed with step: {step.label}</div>}
          </>
        )}
      </div>
      {confirm && <ConfirmDialog title="Remove this picture?" message="It will no longer be printed in the document the next time it is made." okText="Remove" danger onConfirm={() => void remove()} onClose={() => setConfirm(false)} />}
    </li>
  )
}

/**
 * Screenshots and photos that belong to a request. They are printed in its implementation document with a caption, next to the step
 * they are tied to. The AI does not read them, so they cost nothing from the free allowance.
 */
export function FigurePanel({ flow, canCapture }: { flow: Flow; canCapture: boolean }) {
  const figures = useFigures(flow.id)
  const content = useContent(flow.id, flow.currentVersionNo > 0)
  const qc = useQueryClient()
  const toast = useToast()
  const input = useRef<HTMLInputElement>(null)
  const [sending, setSending] = useState<{ done: number; total: number } | null>(null)
  const list = figures.data ?? []

  const stepList = ((content.data?.content?.steps as { id: string; action?: string }[] | undefined) ?? [])
  const steps = stepList.map((s, i) => ({ id: s.id, label: `${i + 1}. ${(s.action ?? s.id).slice(0, 70)}` }))

  async function attach(files: FileList | File[] | null) {
    const chosen = [...(files ?? [])]
    if (chosen.length === 0 || sending) return
    const room = MAX_FIGURES - list.length
    if (room <= 0) { toast(`A request can have up to ${MAX_FIGURES} pictures. Remove one first.`, 'error'); return }
    const take = chosen.slice(0, room)
    if (take.length < chosen.length) toast(`Only ${take.length} of the ${chosen.length} pictures fit (the most is ${MAX_FIGURES}).`, '')
    setSending({ done: 0, total: take.length })
    let added = 0
    for (const file of take) {
      try {
        const blob = await preparePhoto(file)
        const form = new FormData()
        form.append('File', blob, /\.(png|jpe?g|webp)$/i.test(file.name) && blob === file ? file.name : file.name.replace(/\.[a-z0-9]+$/i, '') + '.jpg')
        form.append('Caption', captionFromName(file.name))
        await api<Figure>(`flows/${flow.id}/figures`, { method: 'POST', body: form })
        added++
      } catch (e) { toast(`${file.name}: ${describeError(e)}`, 'error') }
      setSending(s => (s ? { ...s, done: s.done + 1 } : s))
    }
    setSending(null)
    if (input.current) input.current.value = ''
    await qc.invalidateQueries({ queryKey: keys.figures(flow.id) })
    if (added > 0) toast(added === 1 ? 'The picture was attached.' : `${added} pictures were attached.`, 'good')
  }

  async function move(figure: Figure, dir: -1 | 1) {
    try { qc.setQueryData(keys.figures(flow.id), await api<Figure[]>(`figures/${figure.id}/move?direction=${dir}`, { method: 'POST', body: {} })) }
    catch (e) { toast(describeError(e), 'error') }
  }

  return (
    <section className="panel" id="pictures" aria-labelledby="h-pictures">
      <h2 id="h-pictures"><span className="num"><ImagesSquare size={14} weight="bold" aria-hidden /></span>Pictures for the document</h2>
      <p className="muted small" style={{ marginTop: 0 }}>Screenshots of the screens, photos of forms or reports. They are printed in the implementation document with their caption, next to the step you tie them to. The AI does not read them, so they use none of the free allowance.</p>
      {canCapture && (
        <div className="row">
          <button className="btn" disabled={!!sending || list.length >= MAX_FIGURES} onClick={() => input.current?.click()}><ImagesSquare size={18} aria-hidden />Attach pictures</button>
          <input ref={input} type="file" accept={ACCEPT} multiple hidden aria-label="Choose pictures to attach" onChange={e => void attach(e.target.files)} />
          <span className="muted small">{list.length} of {MAX_FIGURES}</span>
        </div>
      )}
      {sending && <div className="alert info" role="status">Attaching picture {Math.min(sending.done + 1, sending.total)} of {sending.total}…</div>}
      {figures.isPending && <Skeletons count={1} />}
      {figures.isError && <Alert tone="error">{describeError(figures.error)}</Alert>}
      {figures.isSuccess && list.length === 0 && <div className="empty" role="status"><b>No pictures yet</b><div>{canCapture ? 'Attach a screenshot or a photo and it appears in the document.' : 'Nobody has attached a picture to this request.'}</div></div>}
      {list.length > 0 && (
        <ol className="figure-list" aria-label="Pictures attached">
          {list.map((f, i) => <FigureRow key={f.id + f.caption + (f.stepId ?? '')} figure={f} steps={steps} index={i} count={list.length} canChange={canCapture} onMove={dir => void move(f, dir)} />)}
        </ol>
      )}
    </section>
  )
}