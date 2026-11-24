import { useState, type InputHTMLAttributes } from 'react'
import { Eye, EyeSlash } from '@phosphor-icons/react'

/**
 * A password box with an eye beside it: press it to see what was typed (to check it), press again to hide it. The box keeps the cursor
 * when the eye is pressed, and it is hidden again whenever the screen it is on closes. The eye is named "Show password" / "Hide password"
 * on purpose, without the field's own name, so a field is still found by its label alone.
 */
export function PasswordInput({ className, ...rest }: Omit<InputHTMLAttributes<HTMLInputElement>, 'type'>) {
  const [shown, setShown] = useState(false)
  const label = shown ? 'Hide password' : 'Show password'
  return (
    <div className="pw-wrap">
      <input {...rest} className={`input${className ? ` ${className}` : ''}`} type={shown ? 'text' : 'password'} spellCheck={false} autoCapitalize="off" autoCorrect="off" />
      <button type="button" className="pw-eye" aria-label={label} aria-pressed={shown} title={label}
        onMouseDown={e => e.preventDefault()} onClick={() => setShown(s => !s)}>
        {shown ? <EyeSlash size={20} aria-hidden /> : <Eye size={20} aria-hidden />}
      </button>
    </div>
  )
}
