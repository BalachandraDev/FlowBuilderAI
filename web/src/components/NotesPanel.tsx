import { useEffect, useMemo, useRef, useState } from 'react'
import { Camera, CaretDown, ImageSquare, Microphone, NotePencil, Paperclip, PencilSimple, Trash, ArrowClockwise, Waveform } from '@phosphor-icons/react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, del, describeError, get } from '../api/client'
import { keys, useEntries, useLanguages, useSystemInfo } from '../api/hooks'
import type { Entry, Flow, Transcript } from '../api/types'
import { formatWhen } from '../lib/format'
import { useApiImage } from '../lib/files'
import { ATTACH_ACCEPT, attachFiles } from '../lib/attach'
import { mmss } from '../lib/audio'
import { discardQueued, enqueueEntry, type QueuedEntry } from '../offline/outbox'
import { useQueued } from '../offline/useQueued'
import { useSession } from '../state/session'
import { RecordDialog } from './RecordDialog'
import { Alert, ConfirmDialog, Dialog, Pill, Skeletons, useToast } from './ui'
import { uuid } from '../lib/uuid'

const draftKey = (flowId: string) => `fc.draft.${flowId}`
const loadDraft = (flowId: string): { id: string; text: string } | null => { try { const raw = localStorage.getItem(draftKey(flowId)); return raw ? JSON.parse(raw) : null } catch { return null } }
const saveDraft = (flowId: string, d: { id: string; text: string } | null) => { try { if (d && d.text.trim()) localStorage.setItem(draftKey(flowId), JSON.stringify(d)); else localStorage.removeItem(draftKey(flowId)) } catch { /* storage blocked */ } }

type Tone = 'good' | 'progress' | 'warn' | 'bad' | ''

/** What happened to a note, in words a person understands. */
export function noteStatus(e: Entry): { text: string; tone: Tone } {
  if (e.kind === 'Text') return { text: 'Saved', tone: 'good' }
  if (e.kind === 'Voice' && e.audioState === 'Failed') return { text: 'Upload failed', tone: 'bad' }
  if (e.kind === 'Voice' && e.audioState !== 'Complete') return { text: 'Uploading…', tone: 'progress' }
  if (e.transcriptState === 'Failed') return { text: 'Could not be read', tone: 'bad' }
  if (e.transcriptState === 'Ready') return { text: 'Ready', tone: 'good' }
  return { text: e.kind === 'Image' ? 'Reading the photo…' : 'Turning into text…', tone: 'progress' }
}

const KIND_LABEL = { Text: 'Typed note', Voice: 'Voice note', Image: 'Photo of notes' } as const

function NoteImage({ entryId }: { entryId: string }) {
  const [big, setBig] = useState(false)
  const { url, failed } = useApiImage(`entries/${entryId}/image`)
  if (failed) return <div className="muted small">The photo could not be loaded.</div>
  if (!url) return <div className="note-thumb skeleton" aria-busy="true" aria-label="Loading the photo" />
  return (
    <>
      <button className="note-thumb" onClick={() => setBig(true)} aria-label="Enlarge the photo"><img src={url} alt="Photo of notes" /></button>
      {big && <Dialog title="Photo of notes" onClose={() => setBig(false)} actions={<button className="btn" onClick={() => setBig(false)}>Close</button>}><img src={url} alt="Photo of notes" style={{ maxWidth: '100%', borderRadius: 8 }} /></Dialog>}
    </>
  )
}

function NoteAudio({ entryId }: { entryId: string }) {
  const [wanted, setWanted] = useState(false)
  const { url, failed } = useApiImage(`entries/${entryId}/audio`, undefined, wanted)
  if (!wanted) return <button className="btn text" style={{ minHeight: 32, padding: 0 }} onClick={() => setWanted(true)}><Waveform size={16} aria-hidden />Listen</button>
  if (failed) return <div className="muted small">The recording could not be loaded.</div>
  return url ? <audio controls autoPlay src={url} style={{ width: '100%', maxWidth: 360 }} /> : <div className="muted small">Loading…</div>
}

function NoteRow({ entry, canChange }: { entry: Entry; canChange: boolean }) {
  const qc = useQueryClient()
  const toast = useToast()
  const status = noteStatus(entry)
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState('')
  const [confirm, setConfirm] = useState(false)
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState(false)
  const ready = entry.kind !== 'Text' && entry.transcriptState === 'Ready'
  const transcript = useQuery({ queryKey: ['transcript', entry.id, entry.transcriptState], queryFn: () => get<Transcript>(`entries/${entry.id}/transcript`), enabled: ready })
  const shown = entry.kind === 'Text' ? (entry.rawText ?? '') : (transcript.data?.userEditedText ?? transcript.data?.maskedText ?? '')
  const refresh = () => { void qc.invalidateQueries({ queryKey: keys.entries(entry.flowId) }); void qc.invalidateQueries({ queryKey: ['transcript', entry.id] }); void qc.invalidateQueries({ queryKey: keys.flow(entry.flowId) }) }

  async function save() {
    setBusy(true)
    try {
      if (entry.kind === 'Text')
        await api(`entries/${entry.id}`, { method: 'PUT', ifMatch: entry.rowVersion, body: { language: entry.language, rawText: text.trim(), notes: entry.notes, intervieweeName: entry.intervieweeName, intervieweeRole: entry.intervieweeRole } })
      else await api(`entries/${entry.id}/transcript`, { method: 'PUT', body: { text: text.trim() } })
      toast('Saved.', 'good'); setEditing(false); refresh()
    } catch (e) { toast(describeError(e), 'error') } finally { setBusy(false) }
  }
  async function remove() {
    setConfirm(false); setBusy(true)
    try { await del(`entries/${entry.id}`); toast('Note removed.', 'good'); refresh() } catch (e) { toast(describeError(e), 'error'); setBusy(false) }
  }
  async function retry() {
    setBusy(true)
    try { await api(`entries/${entry.id}/transcribe`, { method: 'POST', body: {} }); refresh() } catch (e) { toast(describeError(e), 'error') } finally { setBusy(false) }
  }

  const Icon = entry.kind === 'Voice' ? Microphone : entry.kind === 'Image' ? ImageSquare : NotePencil
  // a note that needs attention, or is being edited, stays open; the others are one tidy line each so a long list does not push the next steps off the screen
  const expanded = open || editing || status.tone === 'bad'
  const title = `${KIND_LABEL[entry.kind]}${entry.kind === 'Voice' && entry.audioDurationSec ? ` · ${mmss(entry.audioDurationSec)}` : ''}`
  return (
    <li className={`note note-row ${status.tone === 'bad' ? 'is-bad' : ''} ${expanded ? 'is-open' : ''}`}>
      <button type="button" className="note-summary" aria-expanded={expanded} aria-label={`${title}, note E${entry.refNo}: ${expanded ? 'hide' : 'read or edit'}`} onClick={() => { if (!editing) setOpen(o => !o) }}>
        <span className="note-icon" aria-hidden><Icon size={18} weight="bold" /></span>
        <span className="note-body">
          <span className="note-head">
            <b>{title}</b>
            <span className="muted small">E{entry.refNo} · {entry.capturedByName} · {formatWhen(entry.capturedAt)}</span>
            <Pill tone={status.tone}>{status.text}</Pill>
          </span>
          {!expanded && shown && <span className="note-preview">{shown}</span>}
        </span>
        <CaretDown size={16} className="note-caret" aria-hidden />
      </button>
      {entry.transcriptState === 'Failed' && <TranscriptError entryId={entry.id} />}
      {expanded && (
        <div className="note-detail">
          {entry.kind === 'Image' && <NoteImage entryId={entry.id} />}
          {entry.kind === 'Voice' && entry.audioState === 'Complete' && <NoteAudio entryId={entry.id} />}
          {editing ? (
            <>
              <textarea className="input" rows={8} value={text} onChange={e => setText(e.target.value)} aria-label="Note text" />
              <div className="row" style={{ marginTop: 8 }}>
                <button className="btn primary" disabled={busy || !text.trim()} onClick={() => void save()}>Save</button>
                <button className="btn" disabled={busy} onClick={() => setEditing(false)}>Cancel</button>
              </div>
            </>
          ) : (
            shown && <p className="note-text">{shown}</p>
          )}
          {!editing && canChange && (
            <div className="note-actions">
              {(entry.kind === 'Text' || ready) && <button className="btn text" disabled={busy} onClick={() => { setText(shown); setEditing(true) }}><PencilSimple size={16} aria-hidden />Edit text</button>}
              {entry.kind !== 'Text' && entry.transcriptState === 'Failed' && <button className="btn text" disabled={busy} onClick={() => void retry()}><ArrowClockwise size={16} aria-hidden />Try again</button>}
              <button className="btn text" disabled={busy} onClick={() => setConfirm(true)}><Trash size={16} aria-hidden />Remove</button>
            </div>
          )}
        </div>
      )}
      {confirm && <ConfirmDialog title="Remove this note?" message="It will no longer be used when the notes are analysed." okText="Remove" danger onConfirm={() => void remove()} onClose={() => setConfirm(false)} />}
    </li>
  )
}

function TranscriptError({ entryId }: { entryId: string }) {
  const t = useQuery({ queryKey: ['transcript', entryId, 'Failed'], queryFn: () => get<Transcript>(`entries/${entryId}/transcript`) })
  return <div className="note-error">{t.data?.error ?? 'It could not be turned into text.'}</div>
}

function QueuedNote({ item }: { item: QueuedEntry }) {
  return (
    <li className="note is-queued">
      <div className="note-icon" aria-hidden><NotePencil size={20} weight="bold" /></div>
      <div className="note-body">
        <div className="note-head"><b>Typed note</b><span className="muted small">{formatWhen(item.request.capturedAt)}</span>
          <Pill tone={item.rejected ? 'bad' : 'warn'}>{item.rejected ? 'Needs attention' : 'Saved on this device, waiting to send'}</Pill></div>
        <p className="note-text">{item.request.rawText}</p>
        {item.rejected && <div className="note-error">{item.rejected}</div>}
        {item.rejected && <div className="note-actions"><button className="btn text" onClick={() => void discardQueued(item.id)}><Trash size={16} aria-hidden />Remove</button></div>}
      </div>
    </li>
  )
}

/** Everything about adding notes to a project: type, record, photograph, and see what has been added. */
export function NotesPanel({ flow, canCapture }: { flow: Flow; canCapture: boolean }) {
  const { user, isLeadIn } = useSession()
  const qc = useQueryClient()
  const toast = useToast()
  const languages = useLanguages()
  const entries = useEntries(flow.id)
  const queued = useQueued(flow.id)
  const [typing, setTyping] = useState(false)
  const [recording, setRecording] = useState(false)
  const initial = useRef(loadDraft(flow.id))
  // One id per note being written: the server treats an id it already has as "the same note sent again", so every new note needs its own.
  const [draftId, setDraftId] = useState(() => initial.current?.id ?? uuid())
  const [text, setText] = useState(initial.current?.text ?? '')
  const [language, setLanguage] = useState(() => { try { return localStorage.getItem('fc.lang') ?? 'en-IN' } catch { return 'en-IN' } })
  const [sending, setSending] = useState<{ done: number; total: number } | null>(null)
  const [waitingForLimit, setWaitingForLimit] = useState(false)
  const [dragging, setDragging] = useState(false)
  const pick = useRef<HTMLInputElement>(null)
  const attach = useRef<HTMLInputElement>(null)
  const lead = isLeadIn(flow.projectId)

  useEffect(() => { if (initial.current?.text) setTyping(true) }, [])
  useEffect(() => { const t = window.setTimeout(() => saveDraft(flow.id, { id: draftId, text }), 400); return () => window.clearTimeout(t) }, [flow.id, draftId, text])

  const sentIds = useMemo(() => new Set(entries.data?.map(e => e.id)), [entries.data])
  const notes = (entries.data ?? []).filter(e => !e.isDeleted)
  const waiting = queued.filter(q => !sentIds.has(q.id))
  const mediaOk = useSystemInfo().data?.allowMedia !== false

  async function addText() {
    if (!text.trim()) return
    try {
      // a draft kept from an older visit can carry the id of a note that was already sent: never reuse it
      const id = sentIds.has(draftId) ? uuid() : draftId
      await enqueueEntry(flow.id, { id, kind: 'Text', capturedAt: new Date().toISOString(), language, rawText: text.trim(), notes: null, intervieweeName: null, intervieweeRole: null })
      try { localStorage.setItem('fc.lang', language) } catch { /* default stays */ }
      setDraftId(uuid())
      saveDraft(flow.id, null); setText(''); setTyping(false)
      void qc.invalidateQueries({ queryKey: keys.entries(flow.id) })
      toast('Note added.', 'good')
    } catch { toast('Could not save on this device. Check that the browser allows storage, then try again.', 'error') }
  }

  /** Photos, recordings and text files, in any mix and any number: each becomes a note of its own kind, three at a time. One bad file never stops the rest. */
  async function addFiles(files: FileList | File[] | null) {
    const list = [...(files ?? [])]
    if (list.length === 0 || sending) return
    setSending({ done: 0, total: list.length })
    setWaitingForLimit(false)
    const summary = await attachFiles(list, {
      flowId: flow.id, language, mediaOk,
      onProgress: (done, total) => { setSending({ done, total }); void qc.invalidateQueries({ queryKey: keys.entries(flow.id) }) },
      onWaiting: () => setWaitingForLimit(true),
      onAdded: (_file, kind, n, of) => {
        setWaitingForLimit(false)
        toast(kind === 'photo' ? (of > 1 ? `Photo ${n} of ${of} sent. It is being read.` : 'Photo sent. It is being read.')
          : kind === 'voice' ? (of > 1 ? `Recording ${n} of ${of} sent. It is being turned into text.` : 'Recording sent. It is being turned into text.')
          : kind === 'document' ? (of > 1 ? `File ${n} of ${of} read and added as a note.` : 'The file was read and added as a note.')
          : (of > 1 ? `Note ${n} of ${of} added.` : 'Note added.'), 'good')
      },
    })
    for (const f of summary.failed) toast(`${f.file}: ${f.message}`, 'error')
    setSending(null)
    setWaitingForLimit(false)
    void qc.invalidateQueries({ queryKey: keys.entries(flow.id) })
    void qc.invalidateQueries({ queryKey: keys.flow(flow.id) })
    void qc.invalidateQueries({ queryKey: keys.flows(flow.projectId) })        // the page's own count of notes, which decides whether "Analyse" is offered
    if (pick.current) pick.current.value = ''
    if (attach.current) attach.current.value = ''
  }

  const dropProps = canCapture ? {
    onDragOver: (e: React.DragEvent) => { if (e.dataTransfer?.types?.includes('Files')) { e.preventDefault(); setDragging(true) } },
    onDragLeave: (e: React.DragEvent) => { if (e.currentTarget === e.target) setDragging(false) },
    onDrop: (e: React.DragEvent) => { e.preventDefault(); setDragging(false); void addFiles(e.dataTransfer.files) },
  } : {}
  const none = !entries.isPending && notes.length === 0 && waiting.length === 0
  return (
    <div className={`notes${dragging ? ' is-dragging' : ''}`} {...dropProps}>
      {!canCapture && <Alert>You can read these notes but not add to them.</Alert>}
      {canCapture && (
        <div className="adders" role="group" aria-label="Add notes">
          <button className="adder" onClick={() => setRecording(true)} disabled={!mediaOk}><span className="adder-icon rec"><Microphone size={26} weight="fill" aria-hidden /></span><b>Record voice</b><span className="muted small">Talk, we write it down</span></button>
          <button className="adder" onClick={() => pick.current?.click()} disabled={!!sending || !mediaOk}><span className="adder-icon photo"><Camera size={26} weight="fill" aria-hidden /></span><b>Add photos</b><span className="muted small">Handwritten or printed notes</span></button>
          <button className="adder" onClick={() => attach.current?.click()} disabled={!!sending}><span className="adder-icon attach"><Paperclip size={26} weight="fill" aria-hidden /></span><b>Attach files</b><span className="muted small">Photos, recordings, Excel, PDF and text files, many at once</span></button>
          <button className="adder" onClick={() => setTyping(t => !t)} aria-expanded={typing}><span className="adder-icon type"><NotePencil size={26} weight="fill" aria-hidden /></span><b>Type a note</b><span className="muted small">Write it yourself</span></button>
          <input ref={pick} type="file" accept="image/*" multiple hidden onChange={e => void addFiles(e.target.files)} aria-label="Choose photos of notes" />
          <input ref={attach} type="file" accept={ATTACH_ACCEPT} multiple hidden onChange={e => void addFiles(e.target.files)} aria-label="Choose files to attach" />
        </div>
      )}
      {canCapture && !mediaOk && <Alert tone="info">Photos and voice recordings are switched off by the administrator. Type your notes instead.</Alert>}
      {canCapture && <p className="muted small drop-hint">You can also drop photos, recordings, Excel, PDF and text files here. Add more at any time: new input is used the next time you analyse.</p>}
      {sending && <div className="alert info" role="status">Adding file {Math.min(sending.done + 1, sending.total)} of {sending.total}…{waitingForLimit ? ' The free AI allows a limited number of photos and recordings a minute, so this waits a moment and carries on.' : ''}</div>}

      {canCapture && typing && (
        <div className="composer-card">
          <label htmlFor={`note-${flow.id}`} className="sr-only">Your note</label>
          <textarea id={`note-${flow.id}`} className="input" rows={6} autoFocus value={text} onChange={e => setText(e.target.value)} placeholder="Type what was said or shown, as you heard it. For example: the nurse opens the discharge menu and picks the ward…" />
          <div className="row" style={{ justifyContent: 'space-between' }}>
            <select className="input" style={{ width: 'auto' }} value={language} onChange={e => setLanguage(e.target.value)} aria-label="Language of the note">
              {(languages.data?.filter(l => l.isActive) ?? [{ code: 'en-IN', name: 'English (India)' }]).map(l => <option key={l.code} value={l.code}>{l.name}</option>)}
            </select>
            <div className="row">
              <button className="btn" onClick={() => setTyping(false)}>Close</button>
              <button className="btn primary" disabled={!text.trim()} onClick={() => void addText()}>Add note</button>
            </div>
          </div>
          {text.trim() && <div className="muted small">Draft kept on this device while you type.</div>}
        </div>
      )}

      {entries.isPending && <Skeletons count={2} />}
      {entries.isError && waiting.length === 0 && <Alert tone="error">{describeError(entries.error)}</Alert>}
      {none && <div className="empty" role="status"><b>No notes yet</b><div>Record a conversation, add photos of handwritten notes, attach recordings, Excel, PDF or text files, or type what you were told. You can mix them, and add more later.</div></div>}
      {(notes.length > 0 || waiting.length > 0) && <p className="muted small note-count">{notes.length + waiting.length} note{notes.length + waiting.length === 1 ? '' : 's'} · select one to read, listen, edit or remove it</p>}
      {(notes.length > 0 || waiting.length > 0) && (
        <ul className="note-list" aria-label="Notes added so far">
          {notes.map(e => <NoteRow key={e.id} entry={e} canChange={canCapture && (e.capturedBy === user?.id || lead)} />)}
          {waiting.map(q => <QueuedNote key={q.id} item={q} />)}
        </ul>
      )}
      {recording && <RecordDialog flowId={flow.id} onClose={() => setRecording(false)} onSent={() => { void qc.invalidateQueries({ queryKey: keys.entries(flow.id) }); void qc.invalidateQueries({ queryKey: keys.flows(flow.projectId) }) }} />}
    </div>
  )
}
