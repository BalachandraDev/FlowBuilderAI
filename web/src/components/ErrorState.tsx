import { ArrowClockwise } from '@phosphor-icons/react'
import { describeError } from '../api/client'

/** Something could not be loaded: the reason in plain words, and a button to try again without leaving the page. */
export function ErrorState({ error, onRetry, retrying = false }: { error: unknown; onRetry: () => void; retrying?: boolean }) {
  return (
    <div className="alert error errorstate" role="alert">
      <div className="grow"><b>This could not be loaded.</b><div>{describeError(error)}</div></div>
      <button className="btn" onClick={onRetry} disabled={retrying}><ArrowClockwise size={16} aria-hidden />{retrying ? 'Trying…' : 'Try again'}</button>
    </div>
  )
}
