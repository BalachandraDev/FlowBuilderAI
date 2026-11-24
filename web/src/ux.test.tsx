import { render, renderHook, screen, waitFor, within, act } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { makeQueryClient } from './App'
import { auth } from './api/client'
import { ActionMenu } from './components/ActionMenu'
import { DataTable, type Column } from './components/DataTable'
import { Drawer } from './components/Drawer'
import { Pager, pageOf } from './components/Pager'
import { Field, ToastProvider, useNotices, useToast } from './components/ui'
import { SessionProvider } from './state/session'
import { toCsv } from './ux/csv'
import { NotificationBell } from './ux/Notifications'
import { usePref } from './ux/prefs'

beforeEach(() => { vi.unstubAllGlobals(); auth.clear() })

/** The providers the building blocks need (the person, the data cache, messages), without any page. */
const Providers = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={makeQueryClient()}><ToastProvider><SessionProvider><MemoryRouter>{children}</MemoryRouter></SessionProvider></ToastProvider></QueryClientProvider>
)
const mount = (ui: ReactNode) => render(<Providers>{ui}</Providers>)

describe('remembered preferences', () => {
  it('keeps a choice after the page is closed and opened again, and goes back to the default on reset', () => {
    const first = renderHook(() => usePref<string>('colour', 'blue'), { wrapper: Providers })
    expect(first.result.current[0]).toBe('blue')
    act(() => first.result.current[1]('green'))
    expect(first.result.current[0]).toBe('green')
    first.unmount()

    const second = renderHook(() => usePref<string>('colour', 'blue'), { wrapper: Providers })
    expect(second.result.current[0]).toBe('green')                          // a new page load finds the choice
    act(() => second.result.current[2]())
    expect(second.result.current[0]).toBe('blue')
    expect(localStorage.getItem('fc.pref.anon.colour')).toBeNull()
  })

  it('shares one choice between every part of the page that uses it, and accepts a function to change it', () => {
    function Pair() {
      const [a, setA] = usePref<number>('count', 1)
      const [b] = usePref<number>('count', 1)
      return <><button onClick={() => setA(n => n + 1)}>more</button><output>{a}-{b}</output></>
    }
    mount(<Pair />)
    userEvent.setup().click(screen.getByText('more'))
    return waitFor(() => expect(screen.getByText('2-2')).toBeInTheDocument())
  })

  it('survives a browser that refuses to store anything', () => {
    const blocked = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    try {
      const hook = renderHook(() => usePref<string>('colour', 'blue'), { wrapper: Providers })
      act(() => hook.result.current[1]('green'))
      expect(hook.result.current[0]).toBe('green')                            // it still works for this visit
    } finally { blocked.mockRestore() }
  })
})

describe('a table saved as CSV', () => {
  it('quotes what needs quoting and keeps a spreadsheet from running a typed formula', () => {
    expect(toCsv([['Name', 'Note'], ['Asha, K', 'said "hello"'], ['=SUM(A1)', '-5'], ['two\nlines', null]])).toBe('Name,Note\r\n"Asha, K","said ""hello"""\r\n\'=SUM(A1),-5\r\n"two\nlines",')
  })
})

describe('a field that explains itself', () => {
  it('shows what is required, says what is wrong beside the input, and links the two for a screen reader', () => {
    mount(<Field label="Email" required error="Enter a valid email address." help="They sign in with this.">{(id, aria) => <input id={id} {...aria} />}</Field>)
    const input = screen.getByLabelText('Email')
    expect(input).toBeInvalid()
    expect(input).toBeRequired()
    expect(input).toHaveAccessibleDescription(/Enter a valid email address\..*They sign in with this\./)
    expect(screen.getByText('Enter a valid email address.')).toBeInTheDocument()
    expect(input.closest('.field')).toHaveClass('required', 'invalid')
  })

  it('has nothing to say when nothing is wrong, and old callers that ignore the extra argument still work', () => {
    mount(<Field label="Name">{id => <input id={id} />}</Field>)
    const input = screen.getByLabelText('Name')
    expect(input).toBeValid()
    expect(input.closest('.field')).not.toHaveClass('invalid')
  })
})

describe('messages', () => {
  function Sender() {
    const toast = useToast()
    const { notices, unread } = useNotices()
    const [undone, setUndone] = useState(false)
    return (
      <>
        <button onClick={() => toast('Saved.', 'good')}>save</button>
        <button onClick={() => toast('Marked as final.', 'good', { action: { label: 'Undo', onClick: () => setUndone(true) } })}>mark</button>
        <button onClick={() => toast('Could not save.', 'error')}>fail</button>
        <output aria-label="state">{notices.length} / {unread} / {undone ? 'undone' : 'kept'}</output>
      </>
    )
  }

  it('offers an action on a message (Undo) that does what it says and closes the message', async () => {
    mount(<Sender />)
    const user = userEvent.setup()
    await user.click(screen.getByText('mark'))
    await user.click(await screen.findByRole('button', { name: 'Undo' }))
    expect(screen.getByLabelText('state')).toHaveTextContent('undone')
    expect(screen.queryByRole('button', { name: 'Undo' })).not.toBeInTheDocument()
  })

  it('keeps what the app said in the activity list, counts what is new, and forgets it on Clear', async () => {
    mount(<><Sender /><NotificationBell /></>)
    const user = userEvent.setup()
    await user.click(screen.getByText('save')); await user.click(screen.getByText('fail'))
    expect(screen.getByLabelText('state')).toHaveTextContent('2 / 2')
    const bell = screen.getByRole('button', { name: 'Activity, 2 new' })
    await user.click(bell)
    const panel = screen.getByRole('region', { name: 'Recent activity' })
    expect(within(panel).getByText('Could not save.')).toBeInTheDocument()
    expect(within(panel).getByText('Saved.')).toBeInTheDocument()
    expect(within(panel).getAllByRole('listitem')[0]).toHaveTextContent('Could not save.')       // newest first
    expect(screen.getByRole('button', { name: 'Activity' })).toBeInTheDocument()                  // seen: no count any more
    await user.click(within(panel).getByRole('button', { name: /Clear/ }))
    expect(within(panel).getByText(/Nothing yet/)).toBeInTheDocument()
  })
})

describe('a drawer', () => {
  function Host() {
    const [open, setOpen] = useState(false)
    return <><button onClick={() => setOpen(true)}>details</button>{open && <Drawer title="Bed allocation" subtitle="IMPL-1" onClose={() => setOpen(false)} footer={<button>Verify</button>}><button>inside</button></Drawer>}</>
  }

  it('opens over the page, keeps the focus inside, closes with Escape and gives the focus back', async () => {
    mount(<Host />)
    const user = userEvent.setup()
    const opener = screen.getByText('details')
    await user.click(opener)
    const drawer = await screen.findByRole('dialog', { name: 'Bed allocation' })
    expect(drawer).toContainElement(document.activeElement as HTMLElement)
    await user.tab(); await user.tab(); await user.tab(); await user.tab()
    expect(drawer).toContainElement(document.activeElement as HTMLElement)                         // Tab never leaves it
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(opener).toHaveFocus()
  })

  it('closes with the close button and with a click outside, but not with a click inside', async () => {
    mount(<Host />)
    const user = userEvent.setup()
    await user.click(screen.getByText('details'))
    await user.click(await screen.findByText('inside'))
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Close the details' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await user.click(screen.getByText('details'))
    await user.click(document.querySelector('.drawer-backdrop')!)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('a menu of actions', () => {
  it('opens with a click, moves with the arrow keys, runs the chosen action and closes; Escape closes it and returns to the button', async () => {
    const copy = vi.fn(), remove = vi.fn()
    mount(<ActionMenu label="More actions for IMPL-1" items={[{ label: 'Copy the number', onSelect: copy }, { label: 'Disabled one', onSelect: () => undefined, disabled: true }, { label: 'Delete', onSelect: remove, danger: true }]} />)
    const user = userEvent.setup()
    const button = screen.getByRole('button', { name: 'More actions for IMPL-1' })
    expect(button).toHaveAttribute('aria-expanded', 'false')
    await user.click(button)
    const menu = screen.getByRole('menu')
    expect(within(menu).getByRole('menuitem', { name: 'Copy the number' })).toHaveFocus()
    await user.keyboard('{ArrowDown}')
    expect(within(menu).getByRole('menuitem', { name: 'Delete' })).toHaveFocus()                   // the disabled one is skipped
    await user.keyboard('{ArrowDown}')
    expect(within(menu).getByRole('menuitem', { name: 'Copy the number' })).toHaveFocus()          // and it wraps round
    await user.keyboard('{Enter}')
    expect(copy).toHaveBeenCalledOnce()
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()

    await user.click(button)
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    expect(button).toHaveFocus()
    expect(remove).not.toHaveBeenCalled()
  })
})

interface Row { when: string; what: string; project: string; tokens: number }
const rows: Row[] = [
  { when: '2026-11-03', what: 'Analysing notes', project: 'Pharmacy', tokens: 900 },
  { when: '2026-11-01', what: 'Voice notes', project: 'Ward', tokens: 120 },
  { when: '2026-11-02', what: 'Analysing notes', project: 'Ward', tokens: 4500 },
  { when: '2026-11-04', what: 'Photos of notes', project: 'Billing', tokens: 120 },
]
const columns: Column<Row>[] = [
  { key: 'when', header: 'When', text: r => r.when, cell: r => r.when },
  { key: 'what', header: 'What', text: r => r.what, cell: r => r.what },
  { key: 'project', header: 'Project', text: r => r.project, cell: r => r.project },
  { key: 'tokens', header: 'Tokens', numeric: true, text: r => r.tokens, cell: r => r.tokens.toLocaleString('en-IN') },
]
const table = (extra: object = {}) => mount(<DataTable id="t" label="Calls" rows={rows} columns={columns} rowKey={(r, i) => r.when + i} exportName="calls" empty="No calls." {...extra} />)
const bodyOrder = () => within(screen.getByRole('table', { name: 'Calls' })).getAllByRole('row').slice(1).map(r => within(r).getAllByRole('cell')[0].textContent)

describe('a data table', () => {
  it('searches every shown column, with every word, and says when nothing matches', async () => {
    table()
    const user = userEvent.setup()
    await user.type(screen.getByRole('searchbox', { name: /Search Calls/ }), 'ward analysing')
    expect(bodyOrder()).toEqual(['2026-11-02'])
    expect(screen.getByText(/Showing 1–1 of 1/)).toHaveTextContent('Showing 1–1 of 1 (4 in all)')
    await user.clear(screen.getByRole('searchbox', { name: /Search Calls/ }))
    await user.type(screen.getByRole('searchbox', { name: /Search Calls/ }), 'zzz')
    expect(screen.getByText(/Nothing matches/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Clear the search' }))
    expect(bodyOrder()).toHaveLength(4)
  })

  it('sorts by a column, flips on a second click, sorts numbers as numbers, and sorts by several columns with Shift', async () => {
    table()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: /^When/ }))
    expect(bodyOrder()).toEqual(['2026-11-01', '2026-11-02', '2026-11-03', '2026-11-04'])
    expect(screen.getByRole('columnheader', { name: /When/ })).toHaveAttribute('aria-sort', 'ascending')
    await user.click(screen.getByRole('button', { name: /^When/ }))
    expect(bodyOrder()[0]).toBe('2026-11-04')

    await user.click(screen.getByRole('button', { name: /^Tokens/ }))                      // 120, 120, 900, 4500: a number, not text ("4500" before "900")
    expect(bodyOrder()).toEqual(['2026-11-01', '2026-11-04', '2026-11-03', '2026-11-02'])
    await user.keyboard('{Shift>}')
    await user.click(screen.getByRole('button', { name: /^When/ }))                         // then by date among equal tokens
    await user.keyboard('{/Shift}')
    expect(bodyOrder().slice(0, 2)).toEqual(['2026-11-01', '2026-11-04'])
  })

  it('starts in the order it was told to, and Reset view puts everything back', async () => {
    table({ defaultSort: { key: 'when', dir: 'desc' } })
    const user = userEvent.setup()
    expect(bodyOrder()[0]).toBe('2026-11-04')
    await user.type(screen.getByRole('searchbox', { name: /Search Calls/ }), 'ward')
    await user.click(screen.getByRole('button', { name: /^Project/ }))
    await user.click(screen.getByRole('button', { name: 'Reset view' }))
    expect(screen.getByRole('searchbox', { name: /Search Calls/ })).toHaveValue('')
    expect(bodyOrder()[0]).toBe('2026-11-04')
    expect(screen.queryByRole('button', { name: 'Reset view' })).not.toBeInTheDocument()
  })

  it('hides and shows columns (and remembers which), but never lets the last column go', async () => {
    const view = table()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Columns' }))
    const group = screen.getByRole('group', { name: 'Columns shown' })
    await user.click(within(group).getByRole('checkbox', { name: 'Project' }))
    expect(screen.queryByRole('columnheader', { name: /Project/ })).not.toBeInTheDocument()
    view.unmount()

    table()                                                                                    // opened again: still hidden
    expect(screen.queryByRole('columnheader', { name: /Project/ })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Columns' }))
    const again = screen.getByRole('group', { name: 'Columns shown' })
    for (const name of ['When', 'What']) await user.click(within(again).getByRole('checkbox', { name }))
    expect(within(again).getByRole('checkbox', { name: 'Tokens' })).toBeDisabled()              // one column must stay
  })

  it('pages through the rows, with a page size that is remembered', async () => {
    const many: Row[] = Array.from({ length: 23 }, (_, i) => ({ when: `2026-11-${String(i + 1).padStart(2, '0')}`, what: 'Analysing notes', project: 'P' + i, tokens: i }))
    const view = mount(<DataTable id="many" label="Calls" rows={many} columns={columns} rowKey={(r, i) => r.when + i} exportName="calls" empty="No calls." />)
    const user = userEvent.setup()
    expect(bodyOrder()).toHaveLength(10)
    expect(screen.getByText(/Showing 1–10 of 23/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Next page' }))
    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Next page' }))
    expect(bodyOrder()).toHaveLength(3)
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled()
    await user.selectOptions(screen.getByLabelText('Rows per page'), '25')
    expect(bodyOrder()).toHaveLength(23)
    view.unmount()
    mount(<DataTable id="many" label="Calls" rows={many} columns={columns} rowKey={(r, i) => r.when + i} exportName="calls" empty="No calls." />)
    expect(bodyOrder()).toHaveLength(23)                                                          // 25 a page is remembered
  })

  it('takes what is shown away as a CSV file, with the filter and the chosen columns applied', async () => {
    let saved: Blob | null = null
    const create = vi.spyOn(URL, 'createObjectURL').mockImplementation(b => { saved = b as Blob; return 'blob:x' })
    table()
    const user = userEvent.setup()
    await user.type(screen.getByRole('searchbox', { name: /Search Calls/ }), 'ward')
    await user.click(screen.getByRole('button', { name: 'Export CSV' }))
    expect(create).toHaveBeenCalledOnce()
    const text = await new Promise<string>(done => { const reader = new FileReader(); reader.onload = () => done(String(reader.result)); reader.readAsText(saved!) })
    expect(text.replace('﻿', '')).toBe('When,What,Project,Tokens\r\n2026-11-01,Voice notes,Ward,120\r\n2026-11-02,Analysing notes,Ward,4500')
    create.mockRestore()
  })

  it('refreshes when asked, shows loading and an empty message, and every cell is labelled for a narrow screen', async () => {
    const refresh = vi.fn()
    const view = table({ onRefresh: refresh })
    await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh Calls' }))
    expect(refresh).toHaveBeenCalledOnce()
    expect(document.querySelector('td[data-label="Project"]')).toBeInTheDocument()
    view.unmount()
    mount(<DataTable id="e" label="Calls" rows={[]} columns={columns} rowKey={(_, i) => String(i)} exportName="calls" empty="No calls this month." />)
    expect(screen.getByText('No calls this month.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Export CSV' })).toBeDisabled()
  })
})

describe('the pager', () => {
  it('tells where the person is and changes the page and the page size', async () => {
    const seen: number[] = []
    function Host() {
      const [page, setPage] = useState(0)
      const [size, setSize] = useState(10)
      const all = Array.from({ length: 31 }, (_, i) => i)
      seen.splice(0, seen.length, ...pageOf(all, page, size))
      return <Pager total={all.length} page={page} size={size} onPage={setPage} onSize={setSize} />
    }
    mount(<Host />)
    const user = userEvent.setup()
    expect(screen.getByText('Showing 1–10 of 31')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Next page' }))
    expect(seen[0]).toBe(10)
    await user.selectOptions(screen.getByLabelText('Rows per page'), '50')
    expect(screen.getByText('Showing 1–31 of 31')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Pages' })).not.toBeInTheDocument()                // one page: no buttons
  })

  it('stays out of the way for a short list and for none at all', () => {
    mount(<Pager total={0} page={0} size={10} onPage={() => undefined} onSize={() => undefined} />)
    expect(screen.queryByText(/Showing/)).not.toBeInTheDocument()
  })
})
