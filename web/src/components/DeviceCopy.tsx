import { useEffect, useState } from 'react'
import { FolderOpen } from '@phosphor-icons/react'
import { canChooseFolder, chooseFolder, chosenFolderName, forgetFolder, isPhone, saveCopyEnabled, setSaveCopyEnabled } from '../lib/deviceCopy'

/** One line under the document: whether a copy is saved on this device when a document is made, and (on a computer) in which folder. */
export function DeviceCopy() {
  const [on, setOn] = useState(saveCopyEnabled)
  const [folder, setFolder] = useState<string | null>(null)
  const phone = isPhone()
  const canPick = canChooseFolder()
  useEffect(() => { void chosenFolderName().then(setFolder) }, [])

  return (
    <div className="device-copy">
      <label className="check switch">
        <input type="checkbox" checked={on} onChange={e => { setOn(e.target.checked); setSaveCopyEnabled(e.target.checked) }} />
        <span>
          <b>Keep a copy on {phone ? 'this phone' : 'this computer'}</b>
          <br /><span className="muted small">
            {phone ? 'A PDF is saved in the Downloads of this phone each time a document is made.'
              : folder ? `Each new PDF is saved in the folder “${folder}”.`
                : canPick ? 'Each new PDF is saved in the Downloads folder, or in a folder you choose.' : 'Each new PDF is saved in the Downloads folder of this computer.'}
          </span>
        </span>
      </label>
      {on && canPick && (
        <div className="row">
          <button className="btn" onClick={() => void chooseFolder().then(n => { if (n) setFolder(n) })}><FolderOpen size={18} aria-hidden />{folder ? 'Change the folder' : 'Choose a folder'}</button>
          {folder && <button className="btn text" onClick={() => void forgetFolder().then(() => setFolder(null))}>Use Downloads instead</button>}
        </div>
      )}
    </div>
  )
}
