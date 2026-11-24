import { describe, expect, it } from 'vitest'
import { MAX_RECORDING_SEC, encodeWav, microphoneMessage, mmss, recordingProblem } from './audio'

const read = (blob: Blob) => new Promise<DataView>(resolve => { const r = new FileReader(); r.onload = () => resolve(new DataView(r.result as ArrayBuffer)); r.readAsArrayBuffer(blob) })
const text = (v: DataView, at: number, n: number) => String.fromCharCode(...Array.from({ length: n }, (_, i) => v.getUint8(at + i)))

describe('WAV encoding', () => {
  it('writes a plain 16-bit mono file that every speech service accepts', async () => {
    const wav = encodeWav(new Float32Array([0, 0.5, -0.5, 1, -1]), 16000)
    const v = await read(wav)
    expect(wav.type).toBe('audio/wav')
    expect(wav.size).toBe(44 + 5 * 2)
    expect(text(v, 0, 4)).toBe('RIFF'); expect(text(v, 8, 4)).toBe('WAVE'); expect(text(v, 12, 4)).toBe('fmt '); expect(text(v, 36, 4)).toBe('data')
    expect(v.getUint32(4, true)).toBe(36 + 10)             // size of everything after the first 8 bytes
    expect(v.getUint16(20, true)).toBe(1)                  // PCM
    expect(v.getUint16(22, true)).toBe(1)                  // one channel
    expect(v.getUint32(24, true)).toBe(16000)              // sample rate
    expect(v.getUint32(28, true)).toBe(32000)              // bytes per second
    expect(v.getUint16(34, true)).toBe(16)                 // bits per sample
    expect(v.getUint32(40, true)).toBe(10)                 // size of the sound data
  })

  it('turns the samples into numbers in the right range, and clips what is too loud', async () => {
    const v = await read(encodeWav(new Float32Array([0, 0.5, -0.5, 1, -1, 3, -3]), 16000))
    const sample = (i: number) => v.getInt16(44 + i * 2, true)
    expect(sample(0)).toBe(0)
    expect(sample(1)).toBe(Math.floor(0.5 * 0x7fff))
    expect(sample(2)).toBe(-0x4000)
    expect(sample(3)).toBe(0x7fff); expect(sample(4)).toBe(-0x8000)
    expect(sample(5)).toBe(0x7fff); expect(sample(6)).toBe(-0x8000)          // louder than possible: clipped, not wrapped around
  })

  it('makes a valid file even for silence of no length', async () => {
    const v = await read(encodeWav(new Float32Array(0), 16000))
    expect(v.byteLength).toBe(44)
    expect(v.getUint32(40, true)).toBe(0)
  })

  it('keeps one recording to a length the AI can listen to in one go (about 13 MB as 16 kHz mono)', () => {
    expect(MAX_RECORDING_SEC).toBe(420)
    expect(MAX_RECORDING_SEC * 16000 * 2).toBeLessThan(14 * 1024 * 1024)
  })
})

describe('words for the person', () => {
  it('shows a time as minutes and seconds', () => {
    expect(mmss(0)).toBe('0:00'); expect(mmss(5)).toBe('0:05'); expect(mmss(84)).toBe('1:24'); expect(mmss(419.9)).toBe('6:59'); expect(mmss(600)).toBe('10:00')
  })

  it('explains why a browser cannot record', () => {
    // jsdom is a secure page but has no microphone or recorder
    expect(recordingProblem()).toContain('cannot record sound')
    Object.defineProperty(window, 'isSecureContext', { value: false, configurable: true })
    try { expect(recordingProblem()).toContain('secure address') } finally { Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true }) }
  })

  it('turns microphone errors into what to do about them', () => {
    expect(microphoneMessage(new DOMException('x', 'NotAllowedError'))).toContain('blocked')
    expect(microphoneMessage(new DOMException('x', 'NotFoundError'))).toContain('No microphone')
    expect(microphoneMessage(new DOMException('x', 'NotReadableError'))).toContain('another app')
    expect(microphoneMessage(new Error('boom'))).toContain('boom')
  })
})
