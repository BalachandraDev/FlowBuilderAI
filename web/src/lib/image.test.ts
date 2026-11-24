import { afterEach, describe, expect, it, vi } from 'vitest'
import { preparePhoto } from './image'

afterEach(() => vi.unstubAllGlobals())
const file = (size: number, type: string, name = 'p.jpg') => new File([new Uint8Array(size)], name, { type })

describe('preparing a photo', () => {
  it('sends a small photo exactly as it is', async () => {
    const f = file(200_000, 'image/jpeg')
    expect(await preparePhoto(f)).toBe(f)
  })

  it('shrinks a large photo to a JPEG, keeping its proportions and a white background', async () => {
    const draws: unknown[][] = []
    const bitmap = { width: 4400, height: 2200, close: vi.fn() }
    vi.stubGlobal('createImageBitmap', vi.fn(async () => bitmap))
    const ctx = { fillStyle: '', fillRect: vi.fn(), drawImage: (...a: unknown[]) => draws.push(a) }
    let canvas!: HTMLCanvasElement
    const real = document.createElement.bind(document)
    vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
      const el = real(tag)
      if (tag === 'canvas') {
        canvas = el as HTMLCanvasElement
        canvas.getContext = (() => ctx) as unknown as typeof canvas.getContext
        canvas.toBlob = ((cb: BlobCallback, type?: string, q?: number) => { expect(type).toBe('image/jpeg'); expect(q).toBe(0.85); cb(new Blob(['small'], { type: 'image/jpeg' })) }) as typeof canvas.toBlob
      }
      return el
    })

    const out = await preparePhoto(file(5_000_000, 'image/jpeg'))

    expect(out.type).toBe('image/jpeg')
    expect(canvas.width).toBe(2200); expect(canvas.height).toBe(1100)               // longest side 2200, same shape
    expect(ctx.fillStyle).toBe('#ffffff'); expect(ctx.fillRect).toHaveBeenCalled()
    expect(draws[0].slice(1)).toEqual([0, 0, 2200, 1100])
    expect(bitmap.close).toHaveBeenCalled()
    vi.restoreAllMocks()
  })

  it('does not enlarge a photo that is already small in pixels but heavy in bytes', async () => {
    vi.stubGlobal('createImageBitmap', vi.fn(async () => ({ width: 800, height: 600, close() {} })))
    let canvas!: HTMLCanvasElement
    const real = document.createElement.bind(document)
    vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
      const el = real(tag)
      if (tag === 'canvas') { canvas = el as HTMLCanvasElement; canvas.getContext = (() => ({ fillRect() {}, drawImage() {} })) as unknown as typeof canvas.getContext; canvas.toBlob = ((cb: BlobCallback) => cb(new Blob(['x']))) as typeof canvas.toBlob }
      return el
    })
    await preparePhoto(file(3_000_000, 'image/png'))
    expect([canvas.width, canvas.height]).toEqual([800, 600])
    vi.restoreAllMocks()
  })

  it('falls back to the original when the browser cannot open it but the server can take it', async () => {
    vi.stubGlobal('createImageBitmap', vi.fn(async () => { throw new Error('cannot decode') }))
    const f = file(3_000_000, 'image/webp')
    expect(await preparePhoto(f)).toBe(f)
  })

  it('refuses a file nobody can read, or one that is too large to send as it is, with a plain message', async () => {
    vi.stubGlobal('createImageBitmap', vi.fn(async () => { throw new Error('cannot decode') }))
    await expect(preparePhoto(file(3_000_000, 'image/heic', 'iphone.heic'))).rejects.toThrow(/could not be prepared/)
    await expect(preparePhoto(file(9 * 1024 * 1024, 'image/png'))).rejects.toThrow(/could not be prepared/)
  })
})
