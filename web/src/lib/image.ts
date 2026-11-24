const MAX_SIDE = 2200
const KEEP_AS_IS_BYTES = 1_500_000
const SERVER_LIMIT = 8 * 1024 * 1024
const ACCEPTED = ['image/jpeg', 'image/png', 'image/webp']

/** Phone photos are large and sometimes in a format the server does not read. A 2200 pixel JPEG is plenty to read handwriting and is much faster to send. */
export async function preparePhoto(file: File): Promise<Blob> {
  const small = file.size <= KEEP_AS_IS_BYTES && ACCEPTED.includes(file.type)
  if (small) return file
  try {
    const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' })
    const scale = Math.min(1, MAX_SIDE / Math.max(bitmap.width, bitmap.height))
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(bitmap.width * scale))
    canvas.height = Math.max(1, Math.round(bitmap.height * scale))
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('no canvas')
    ctx.fillStyle = '#ffffff'                                  // a transparent PNG would turn black in a JPEG
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
    bitmap.close?.()
    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.85))
    if (blob) return blob
  } catch { /* the browser could not open it: send it as it is if the server can take it */ }
  if (ACCEPTED.includes(file.type) && file.size <= SERVER_LIMIT) return file
  throw new Error('This photo could not be prepared. Try another one, or take it again.')
}
