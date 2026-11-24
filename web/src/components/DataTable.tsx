import { useMemo, useState, type ReactNode } from 'react'
import { ArrowClockwise, CaretDown, CaretLeft, CaretRight, CaretUp, Columns, DownloadSimple, MagnifyingGlass, Printer } from '@phosphor-icons/react'
import { downloadCsv, stamp } from '../ux/csv'
import { usePref } from '../ux/prefs'
import { Skeletons } from './ui'

export interface Column<T> {
  key: string
  header: string
  /** What is shown in the cell. */
  cell: (row: T) => ReactNode
  /** The same cell as plain text or a number: used to search, to sort and to export. */
  text: (row: T) => string | number
  numeric?: boolean
  sortable?: boolean
  /** Hidden until the person turns it on in Columns. */
  defaultHidden?: boolean
}

interface Sort { key: string; dir: 'asc' | 'desc' }
const SIZES = [10, 25, 50, 100]

/**
 * A table the person can work with: search across the columns, sort by one column or by several (hold Shift to add one), choose the columns and how many
 * rows a page shows (both remembered), page through the rows, and take what is shown away as a CSV file that Excel opens. On a narrow screen each row
 * becomes a small card instead of a squeezed row. The data is what the page already loaded: nothing is asked of the server again unless Refresh is used.
 */
export function DataTable<T>({ id, label, rows, columns, rowKey, rowClass, exportName, empty, loading = false, onRefresh, refreshing = false, defaultSort }: {
  id: string; label: string; rows: T[]; columns: Column<T>[]; rowKey: (row: T, index: number) => string; rowClass?: (row: T) => string
  exportName: string; empty: string; loading?: boolean; onRefresh?: () => void; refreshing?: boolean; defaultSort?: Sort
}) {
  const [query, setQuery] = useState('')
  const [sorts, setSorts] = useState<Sort[]>(defaultSort ? [defaultSort] : [])
  const [page, setPage] = useState(0)
  const [size, setSize] = usePref<number>(`table.${id}.size`, 10)
  const [hidden, setHidden, resetHidden] = usePref<string[]>(`table.${id}.hidden`, columns.filter(c => c.defaultHidden).map(c => c.key))
  const [picking, setPicking] = useState(false)

  const shownColumns = columns.filter(c => !hidden.includes(c.key))
  const view = useMemo(() => {
    const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
    let list = words.length === 0 ? rows : rows.filter(r => { const hay = shownColumns.map(c => String(c.text(r))).join(' ').toLowerCase(); return words.every(w => hay.includes(w)) })
    if (sorts.length > 0) {
      const by = sorts.map(s => ({ col: columns.find(c => c.key === s.key)!, dir: s.dir === 'asc' ? 1 : -1 })).filter(s => s.col)
      list = [...list].sort((a, b) => {
        for (const { col, dir } of by) {
          const x = col.text(a), y = col.text(b)
          const d = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), undefined, { numeric: true, sensitivity: 'base' })
          if (d !== 0) return d * dir
        }
        return 0
      })
    }
    return list
  }, [rows, query, sorts, shownColumns.map(c => c.key).join('|')])   // eslint-disable-line react-hooks/exhaustive-deps

  const pages = Math.max(1, Math.ceil(view.length / size))
  const at = Math.min(page, pages - 1)
  const slice = view.slice(at * size, at * size + size)
  const changed = query !== '' || sorts.length !== (defaultSort ? 1 : 0) || (defaultSort && (sorts[0]?.key !== defaultSort.key || sorts[0]?.dir !== defaultSort.dir)) || hidden.join() !== columns.filter(c => c.defaultHidden).map(c => c.key).join() || size !== 10

  function sortBy(key: string, add: boolean) {
    setPage(0)
    setSorts(list => {
      const current = list.find(s => s.key === key)
      const flipped: Sort = { key, dir: current?.dir === 'asc' ? 'desc' : 'asc' }
      if (add) return current ? list.map(s => (s.key === key ? flipped : s)) : [...list, { key, dir: 'asc' }]
      return [flipped]
    })
  }
  const indicator = (key: string) => {
    const i = sorts.findIndex(s => s.key === key)
    if (i < 0) return null
    return <span className="sort-mark" aria-hidden>{sorts[i].dir === 'asc' ? <CaretUp size={12} weight="bold" /> : <CaretDown size={12} weight="bold" />}{sorts.length > 1 && <sup>{i + 1}</sup>}</span>
  }

  function exportCsv() {
    downloadCsv(`${exportName}-${stamp()}`, [shownColumns.map(c => c.header), ...view.map(r => shownColumns.map(c => c.text(r)))])
  }
  function reset() { setQuery(''); setSorts(defaultSort ? [defaultSort] : []); setPage(0); setSize(10); resetHidden() }

  return (
    <div className="dtable" aria-label={label}>
      <div className="dtable-bar">
        <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Search {label}</span>
          <input className="input" type="search" placeholder="Search this table" value={query} onChange={e => { setQuery(e.target.value); setPage(0) }} /></label>
        <div className="dtable-tools">
          <div className="dtable-cols">
            <button className="btn" aria-haspopup="true" aria-expanded={picking} onClick={() => setPicking(p => !p)}><Columns size={16} aria-hidden />Columns</button>
            {picking && (
              <div className="menu dtable-menu" role="group" aria-label="Columns shown">
                {columns.map(c => (
                  <label key={c.key} className="check"><input type="checkbox" checked={!hidden.includes(c.key)} disabled={!hidden.includes(c.key) && shownColumns.length === 1}
                    onChange={() => setHidden(h => (h.includes(c.key) ? h.filter(k => k !== c.key) : [...h, c.key]))} /><span>{c.header}</span></label>
                ))}
              </div>
            )}
          </div>
          <button className="btn" onClick={exportCsv} disabled={view.length === 0}><DownloadSimple size={16} aria-hidden />Export CSV</button>
          <button className="btn print-hide" onClick={() => window.print()}><Printer size={16} aria-hidden />Print</button>
          {onRefresh && <button className="btn" onClick={onRefresh} disabled={refreshing} aria-label={`Refresh ${label}`}><ArrowClockwise size={16} aria-hidden />{refreshing ? 'Refreshing…' : 'Refresh'}</button>}
          {changed && <button className="btn link" onClick={reset}>Reset view</button>}
        </div>
      </div>

      {loading && <Skeletons count={3} />}
      {!loading && rows.length === 0 && <p className="muted small">{empty}</p>}
      {!loading && rows.length > 0 && view.length === 0 && <div className="empty" role="status"><b>Nothing matches “{query}”</b><button className="btn link" onClick={() => setQuery('')}>Clear the search</button></div>}
      {!loading && view.length > 0 && (
        <>
          <div className="table-wrap">
            <table className="plain dtable-table" aria-label={label}>
              <thead>
                <tr>
                  {shownColumns.map(c => {
                    const s = sorts.find(x => x.key === c.key)
                    return (
                      <th key={c.key} className={c.numeric ? 'num-col' : ''} aria-sort={s ? (s.dir === 'asc' ? 'ascending' : 'descending') : c.sortable === false ? undefined : 'none'}>
                        {c.sortable === false ? c.header : <button className="th-sort" onClick={e => sortBy(c.key, e.shiftKey)} title="Sort by this column. Hold Shift to sort by several.">{c.header}{indicator(c.key)}</button>}
                      </th>
                    )
                  })}
                </tr>
              </thead>
              <tbody>
                {slice.map((r, i) => (
                  <tr key={rowKey(r, at * size + i)} className={rowClass?.(r) ?? ''}>
                    {shownColumns.map(c => <td key={c.key} data-label={c.header} className={c.numeric ? 'num-col' : ''}>{c.cell(r)}</td>)}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="dtable-foot">
            <span className="muted small" role="status">Showing {at * size + 1}–{Math.min(view.length, at * size + size)} of {view.length}{view.length !== rows.length ? ` (${rows.length} in all)` : ''}</span>
            <label className="field-inline dtable-size"><span>Rows</span>
              <select className="input" value={size} onChange={e => { setSize(Number(e.target.value)); setPage(0) }} aria-label="Rows per page">{SIZES.map(n => <option key={n} value={n}>{n}</option>)}</select></label>
            <div className="dtable-pages" role="group" aria-label="Pages">
              <button className="icon-btn" onClick={() => setPage(at - 1)} disabled={at === 0} aria-label="Previous page"><CaretLeft size={18} aria-hidden /></button>
              <span className="small">Page {at + 1} of {pages}</span>
              <button className="icon-btn" onClick={() => setPage(at + 1)} disabled={at >= pages - 1} aria-label="Next page"><CaretRight size={18} aria-hidden /></button>
            </div>
          </div>
        </>
      )}
    </div>
  )
}
