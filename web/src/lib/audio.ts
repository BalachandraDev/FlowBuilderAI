import { sha256OfBytes } from './sha256'
/** The longest single recording. About 13 MB as 16 kHz mono, which the AI can listen to in one go. Longer interviews are several recordings. */
export const MAX_RECORDING_SEC = 7 * 60
const TARGET_RATE = 16000

/** Why this browser cannot record, in words for the person, or null when it can. */
export function recordingProblem(): string | null {
  if (typeof window === 'undefined') return 'Recording is not available here.'
  if (!window.isSecureContext)
    return 'Recording needs a secure address. Open the app with https:// (or on this computer as localhost), then try again.'
  if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined')
    return 'This browser cannot record sound. Try Chrome, Edge, Safari or Firefox.'
  return null
}

/** The first recording format this browser can make. Chrome and Firefox make webm or ogg, Safari makes mp4. */
export function pickRecorderType(): string | undefined {
  if (typeof MediaRecorder === 'undefined' || !MediaRecorder.isTypeSupported) return undefined
  return ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'].find(t => MediaRecorder.isTypeSupported(t))
}

/** Plain 16-bit mono WAV: the one format every speech service accepts and every browser can play. */
export function encodeWav(samples: Float32Array, sampleRate: number): Blob {
  const bytes = samples.length * 2
  const buffer = new ArrayBuffer(44 + bytes)
  const v = new DataView(buffer)
  const text = (at: number, s: string) => { for (let i = 0; i < s.length; i++) v.setUint8(at + i, s.charCodeAt(i)) }
  text(0, 'RIFF'); v.setUint32(4, 36 + bytes, true); text(8, 'WAVE')
  text(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true)
  v.setUint32(24, sampleRate, true); v.setUint32(28, sampleRate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true)
  text(36, 'data'); v.setUint32(40, bytes, true)
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]))
    v.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true)
  }
  return new Blob([buffer], { type: 'audio/wav' })
}

/** What the browser recorded, as a 16 kHz mono WAV. The browser's own decoder does the work, so any format it can record, it can convert. */
export async function toWav16k(recording: Blob): Promise<{ wav: Blob; durationSec: number }> {
  const AudioCtx: typeof AudioContext = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
  const ctx = new AudioCtx()
  try {
    const decoded = await ctx.decodeAudioData(await recording.arrayBuffer())
    const length = Math.max(1, Math.ceil(decoded.duration * TARGET_RATE))
    const offline = new OfflineAudioContext(1, length, TARGET_RATE)          // one channel: stereo is mixed down
    const source = offline.createBufferSource()
    source.buffer = decoded
    source.connect(offline.destination)
    source.start()
    const rendered = await offline.startRendering()
    return { wav: encodeWav(rendered.getChannelData(0), TARGET_RATE), durationSec: Math.round(decoded.duration) }
  } finally {
    void ctx.close()
  }
}

export async function sha256Hex(data: Blob): Promise<string> {
  return sha256OfBytes(await data.arrayBuffer())
}

export const mmss = (seconds: number) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`

/** What went wrong with the microphone, in plain words. */
export function microphoneMessage(e: unknown): string {
  const name = e instanceof DOMException ? e.name : ''
  if (name === 'NotAllowedError' || name === 'SecurityError') return 'The microphone is blocked. Allow it in the browser (the lock icon next to the address), then try again.'
  if (name === 'NotFoundError' || name === 'OverconstrainedError') return 'No microphone was found on this device.'
  if (name === 'NotReadableError') return 'The microphone is being used by another app. Close it and try again.'
  return 'The microphone could not be started. ' + (e instanceof Error ? e.message : '')
}
