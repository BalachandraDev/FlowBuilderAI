import { useCallback, useEffect, useRef, useState } from 'react'
import { Microphone, Pause, Play, Stop, ArrowCounterClockwise } from '@phosphor-icons/react'
import { describeError } from '../api/client'
import { useLanguages } from '../api/hooks'
import type { Entry } from '../api/types'
import { MAX_RECORDING_SEC, microphoneMessage, mmss, pickRecorderType, recordingProblem, toWav16k } from '../lib/audio'
import { uploadVoice } from '../lib/upload'
import { Alert, ConfirmDialog, Dialog, Field, useToast } from './ui'
import { uuid } from '../lib/uuid'

type Phase = 'idle' | 'recording' | 'paused' | 'review' | 'sending' | 'failed'

const savedLanguage = () => { try { return localStorage.getItem('fc.lang') ?? 'en-IN' } catch { return 'en-IN' } }

/** Record a spoken note: start, pause, stop, listen back, then send. The app turns it into text afterwards. */
export function RecordDialog({ flowId, onClose, onSent }: { flowId: string; onClose: () => void; onSent: (entry: Entry) => void }) {
  const toast = useToast()
  const languages = useLanguages()
  const problem = recordingProblem()
  const [phase, setPhase] = useState<Phase>('idle')
  const [seconds, setSeconds] = useState(0)
  const [level, setLevel] = useState(0)
  const [error, setError] = useState<string | null>(problem)
  const [progress, setProgress] = useState(0)
  const [language, setLanguage] = useState(savedLanguage)
  const [previewUrl, setPreviewUrl] = useState<string | null>(null)
  const [confirmClose, setConfirmClose] = useState(false)

  const recorder = useRef<MediaRecorder | null>(null)
  const stream = useRef<MediaStream | null>(null)
  const chunks = useRef<Blob[]>([])
  const recording = useRef<Blob | null>(null)
  const entryId = useRef(uuid())          // the same note id on every retry, so a retry never makes a second note
  const clock = useRef<{ base: number; since: number | null; timer: number | null }>({ base: 0, since: null, timer: null })
  const meter = useRef<{ ctx: AudioContext | null; raf: number | null }>({ ctx: null, raf: null })
  const stopped = useRef(false)

  const elapsed = () => clock.current.base + (clock.current.since ? (Date.now() - clock.current.since) / 1000 : 0)

  const release = useCallback(() => {
    if (clock.current.timer) window.clearInterval(clock.current.timer)
    clock.current.timer = null
    if (meter.current.raf) cancelAnimationFrame(meter.current.raf)
    meter.current.raf = null
    void meter.current.ctx?.close().catch(() => undefined)
    meter.current.ctx = null
    stream.current?.getTracks().forEach(t => t.stop())
    stream.current = null
    setLevel(0)
  }, [])

  useEffect(() => {
    stopped.current = false
    return () => { stopped.current = true; release(); if (recorder.current?.state && recorder.current.state !== 'inactive') { recorder.current.onstop = null; recorder.current.stop() } }
  }, [release])
  useEffect(() => () => { if (previewUrl) URL.revokeObjectURL(previewUrl) }, [previewUrl])

  function stop() {
    clock.current.base = elapsed(); clock.current.since = null
    if (recorder.current && recorder.current.state !== 'inactive') recorder.current.stop()
  }

  async function start() {
    setError(null)
    try {
      const media = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } })
      stream.current = media
      const type = pickRecorderType()
      const rec = new MediaRecorder(media, type ? { mimeType: type } : undefined)
      chunks.current = []
      rec.ondataavailable = e => { if (e.data.size > 0) chunks.current.push(e.data) }
      rec.onstop = () => {
        release()
        if (stopped.current) return
        const blob = new Blob(chunks.current, { type: rec.mimeType || type || 'audio/webm' })
        recording.current = blob
        setPreviewUrl(prev => { if (prev) URL.revokeObjectURL(prev); return URL.createObjectURL(blob) })
        setSeconds(Math.round(clock.current.base))
        setPhase('review')
      }
      rec.start(1000)
      recorder.current = rec
      clock.current = { base: 0, since: Date.now(), timer: null }
      clock.current.timer = window.setInterval(() => {
        const s = elapsed()
        setSeconds(Math.floor(s))
        if (s >= MAX_RECORDING_SEC) stop()
      }, 250)

      // a moving level bar shows the microphone is hearing something
      const AudioCtx: typeof AudioContext | undefined = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext
      if (AudioCtx) {
        const ctx = new AudioCtx()
        const analyser = ctx.createAnalyser()
        analyser.fftSize = 512
        ctx.createMediaStreamSource(media).connect(analyser)
        const data = new Uint8Array(analyser.fftSize)
        meter.current.ctx = ctx
        let last = 0
        const tick = (t: number) => {
          if (t - last > 60) {
            analyser.getByteTimeDomainData(data)
            let peak = 0
            for (const v of data) peak = Math.max(peak, Math.abs(v - 128))
            setLevel(Math.min(1, peak / 64))
            last = t
          }
          meter.current.raf = requestAnimationFrame(tick)
        }
        meter.current.raf = requestAnimationFrame(tick)
      }
      setPhase('recording')
    } catch (e) {
      release()
      setError(microphoneMessage(e))
    }
  }

  function pauseOrResume() {
    const rec = recorder.current
    if (!rec) return
    if (rec.state === 'recording') { rec.pause(); clock.current.base = elapsed(); clock.current.since = null; setPhase('paused') }
    else if (rec.state === 'paused') { rec.resume(); clock.current.since = Date.now(); setPhase('recording') }
  }

  async function send() {
    if (!recording.current) return
    setPhase('sending'); setError(null); setProgress(1)
    try {
      const { wav, durationSec } = await toWav16k(recording.current)
      const entry = await uploadVoice(flowId, wav, durationSec, language, setProgress, entryId.current)
      try { localStorage.setItem('fc.lang', language) } catch { /* default stays */ }
      toast('Recording sent. It is being turned into text.', 'good')
      onSent(entry)
      onClose()
    } catch (e) {
      setError(e instanceof DOMException || (e instanceof Error && /decode/i.test(e.message)) ? 'This recording could not be read by the browser. Record it again.' : describeError(e))
      setPhase('failed')
    }
  }

  function again() {
    recording.current = null
    setPreviewUrl(prev => { if (prev) URL.revokeObjectURL(prev); return null })
    clock.current = { base: 0, since: null, timer: null }
    entryId.current = uuid()
    setSeconds(0); setError(null); setPhase('idle')
  }

  function tryClose() {
    if (phase === 'sending') return                                   // let it finish
    if (phase === 'review' || phase === 'failed' || ((phase === 'recording' || phase === 'paused') && seconds > 5)) setConfirmClose(true)
    else onClose()
  }

  const live = phase === 'recording' || phase === 'paused'
  return (
    <>
      <Dialog title="Record a voice note" onClose={tryClose} actions={
        <>
          {phase === 'idle' && <button className="btn" onClick={onClose}>Cancel</button>}
          {live && <button className="btn" onClick={tryClose}>Cancel</button>}
          {(phase === 'review' || phase === 'failed') && <button className="btn" onClick={again}><ArrowCounterClockwise size={18} aria-hidden />Record again</button>}
          {phase === 'review' && <button className="btn primary" onClick={() => void send()}>Use this recording</button>}
          {phase === 'failed' && <button className="btn primary" onClick={() => void send()}>Try again</button>}
        </>
      }>
        {error && <Alert tone="error">{error}</Alert>}

        {phase === 'idle' && !problem && (
          <div className="rec">
            <p className="muted">Speak naturally. You can pause, and listen to it before it is sent. Up to {MAX_RECORDING_SEC / 60} minutes in one recording: for a longer interview, record it in parts.</p>
            <Field label="Language spoken">
              {id => (
                <select id={id} className="input" value={language} onChange={e => setLanguage(e.target.value)}>
                  {(languages.data?.filter(l => l.isActive) ?? [{ code: 'en-IN', name: 'English (India)' }]).map(l => <option key={l.code} value={l.code}>{l.name}</option>)}
                </select>
              )}
            </Field>
            <button className="rec-start" onClick={() => void start()} aria-label="Start recording"><Microphone size={34} weight="fill" aria-hidden /></button>
            <div className="rec-hint">Tap to start</div>
          </div>
        )}

        {live && (
          <div className="rec" role="group" aria-label="Recording">
            <div className={`rec-time ${phase === 'recording' ? 'on' : ''}`} aria-live="off">{mmss(seconds)}</div>
            <div className="rec-meter" aria-hidden><span style={{ transform: `scaleX(${phase === 'recording' ? Math.max(0.04, level) : 0.04})` }} /></div>
            <div className="rec-hint">{phase === 'recording' ? 'Recording…' : 'Paused'}</div>
            <div className="row" style={{ justifyContent: 'center', gap: 16 }}>
              <button className="btn lg" onClick={pauseOrResume}>{phase === 'recording' ? <><Pause size={20} weight="fill" aria-hidden />Pause</> : <><Play size={20} weight="fill" aria-hidden />Resume</>}</button>
              <button className="btn danger lg" onClick={stop}><Stop size={20} weight="fill" aria-hidden />Stop</button>
            </div>
          </div>
        )}

        {(phase === 'review' || phase === 'failed') && previewUrl && (
          <div className="rec">
            <div className="rec-hint">Recorded {mmss(seconds)}. Listen to it, then use it.</div>
            <audio controls src={previewUrl} style={{ width: '100%' }} />
          </div>
        )}

        {phase === 'sending' && (
          <div className="rec" role="status">
            <div className="rec-hint">Sending your recording…</div>
            <div className="meter" aria-label="Sending progress"><span style={{ width: `${progress}%` }} /></div>
            <div className="muted small">{progress}%. Keep this window open.</div>
          </div>
        )}
      </Dialog>
      {confirmClose && <ConfirmDialog title="Throw this recording away?" message="It has not been sent yet, so it would be lost." okText="Throw it away" danger
        onClose={() => setConfirmClose(false)} onConfirm={() => { setConfirmClose(false); onClose() }} />}
    </>
  )
}
