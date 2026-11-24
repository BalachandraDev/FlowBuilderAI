import { CaretLeft, CaretRight } from '@phosphor-icons/react'

export const PAGE_SIZES = [10, 25, 50, 100]

/** Where the person is in a long list: "Showing 1–25 of 61", how many rows a page holds (remembered by the page), and Previous and Next. */
export function Pager({ total, page, size, onPage, onSize }: { total: number; page: number; size: number; onPage: (p: number) => void; onSize: (n: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / size))
  const at = Math.min(page, pages - 1)
  if (total === 0) return null
  return (
    <div className="dtable-foot">
      <span className="muted small" role="status">Showing {at * size + 1}–{Math.min(total, at * size + size)} of {total}</span>
      {total > PAGE_SIZES[0] && (
        <label className="field-inline dtable-size"><span>Per page</span>
          <select className="input" value={size} onChange={e => { onSize(Number(e.target.value)); onPage(0) }} aria-label="Rows per page">{PAGE_SIZES.map(n => <option key={n} value={n}>{n}</option>)}</select></label>
      )}
      {pages > 1 && (
        <div className="dtable-pages" role="group" aria-label="Pages">
          <button className="icon-btn" onClick={() => onPage(at - 1)} disabled={at === 0} aria-label="Previous page"><CaretLeft size={18} aria-hidden /></button>
          <span className="small">Page {at + 1} of {pages}</span>
          <button className="icon-btn" onClick={() => onPage(at + 1)} disabled={at >= pages - 1} aria-label="Next page"><CaretRight size={18} aria-hidden /></button>
        </div>
      )}
    </div>
  )
}

/** The rows of one page. */
export const pageOf = <T,>(rows: T[], page: number, size: number): T[] => {
  const pages = Math.max(1, Math.ceil(rows.length / size))
  const at = Math.min(page, pages - 1)
  return rows.slice(at * size, at * size + size)
}
