import { afterEach, describe, expect, it, vi } from 'vitest'
import { sha256OfBytes } from './sha256'
import { uuid } from './uuid'

afterEach(() => vi.unstubAllGlobals())

const bytes = (s: string) => new TextEncoder().encode(s).buffer as ArrayBuffer
const real = globalThis.crypto

describe('a plain http address (no secure context)', () => {
  it('still makes ids when crypto.randomUUID is missing, in the right shape and different each time', () => {
    vi.stubGlobal('crypto', { getRandomValues: real.getRandomValues.bind(real) })
    const a = uuid(), b = uuid()
    expect(a).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(a).not.toBe(b)
  })

  it('makes ids even with no crypto object at all', () => {
    vi.stubGlobal('crypto', undefined)
    expect(uuid()).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('gives the same SHA-256 without crypto.subtle as with it', async () => {
    const data = bytes('abc')
    const withSubtle = await sha256OfBytes(data)
    expect(withSubtle).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
    vi.stubGlobal('crypto', { getRandomValues: real.getRandomValues.bind(real) })
    expect(await sha256OfBytes(data)).toBe(withSubtle)
    expect(await sha256OfBytes(bytes(''))).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855')
    const long = bytes('a'.repeat(1000))
    vi.unstubAllGlobals()
    const expected = await sha256OfBytes(long)
    vi.stubGlobal('crypto', { getRandomValues: real.getRandomValues.bind(real) })
    expect(await sha256OfBytes(long)).toBe(expected)
  })
})
