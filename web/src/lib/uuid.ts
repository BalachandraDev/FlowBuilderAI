/**
 * A random id. `crypto.randomUUID` exists only on secure addresses (https or localhost); on a plain http address such as http://192.168.0.30:5499,
 * which is how colleagues open the app inside the network, it is missing and every request would fail. `getRandomValues` works everywhere.
 */
export function uuid(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  const b = new Uint8Array(16)
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') crypto.getRandomValues(b)
  else for (let i = 0; i < 16; i++) b[i] = Math.floor(Math.random() * 256)
  b[6] = (b[6] & 0x0f) | 0x40      // version 4
  b[8] = (b[8] & 0x3f) | 0x80      // variant
  const h = [...b].map(x => x.toString(16).padStart(2, '0')).join('')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`
}
