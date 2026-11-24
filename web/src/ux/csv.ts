/** A table as a CSV file that Excel opens correctly (UTF-8 with a byte order mark, one row per line, text in quotes when it needs them). */
export type Cell = string | number | boolean | null | undefined

export function toCsv(rows: Cell[][]): string {
  const cell = (v: Cell) => {
    const s = v === null || v === undefined ? '' : String(v)
    // a leading = + - or @ would be run as a formula by a spreadsheet: a quote in front keeps it text
    const safe = /^[=+\-@\t\r]/.test(s) && Number.isNaN(Number(s)) ? `'${s}` : s
    return /[",\r\n]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe
  }
  return rows.map(r => r.map(cell).join(',')).join('\r\n')
}

/** Saves text as a file in the browser, without asking the server for anything. */
export function saveText(fileName: string, text: string, type = 'text/csv;charset=utf-8'): void {
  const blob = new Blob(['﻿', text], { type })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url; a.download = fileName
  document.body.appendChild(a); a.click(); a.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}

export const downloadCsv = (fileName: string, rows: Cell[][]) => saveText(fileName.endsWith('.csv') ? fileName : fileName + '.csv', toCsv(rows))

/** Today's date as 2026-11-20, for a file name. */
export const stamp = () => new Date().toISOString().slice(0, 10)
