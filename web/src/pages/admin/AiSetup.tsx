import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ArrowSquareOut, CheckCircle, Eye, EyeSlash, Warning } from '@phosphor-icons/react'
import { api, describeError } from '../../api/client'
import { useAiAdmin } from '../../api/hooks'
import type { AiAdmin, AiTestResult } from '../../api/types'
import { Alert, Field, Skeletons, useToast } from '../../components/ui'
import { DailyLimits } from './DailyLimits'
import { ModelLimits } from './ModelLimits'

const PROVIDERS: Record<string, { name: string; text: string }> = {
  Gemini: { name: 'Free Gemini AI (recommended)', text: 'Google’s free tier. Reads photos, listens to voice notes and writes the analysis and the document with one key.' },
  Anthropic: { name: 'Claude', text: 'Needs a paid Anthropic key set on the server. Text only: voice notes and photos will not work.' },
  Demo: { name: 'Demo mode', text: 'No AI at all: every analysis returns one fixed example. Only for trying the app.' },
}

/** The admin signs up for a free Google AI Studio account once, pastes the key here, and everything that needs an AI works. */
export function AiSetup() {
  const ai = useAiAdmin()
  const qc = useQueryClient()
  const toast = useToast()
  const [key, setKey] = useState('')
  const [show, setShow] = useState(false)
  const [provider, setProvider] = useState('Gemini')
  const [model, setModel] = useState('')
  const [groqKey, setGroqKey] = useState('')
  const [showGroq, setShowGroq] = useState(false)
  const [voice, setVoice] = useState('Groq')
  const [busy, setBusy] = useState<'save' | 'test' | 'groq' | null>(null)
  const [test, setTest] = useState<AiTestResult | null>(null)
  const [groqTest, setGroqTest] = useState<AiTestResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const a = ai.data

  useEffect(() => { if (a) { setProvider(a.provider); setModel(a.geminiModel); setVoice(a.voiceProvider ?? 'Gemini') } }, [a])

  async function runTest() {
    setBusy('test'); setError(null)
    try { setTest(await api<AiTestResult>('admin/ai/test', { method: 'POST', body: {} })) } catch (e) { setError(describeError(e)) } finally { setBusy(null) }
  }

  async function runGroqTest() {
    setBusy('groq'); setError(null)
    try { setGroqTest(await api<AiTestResult>('admin/ai/test-groq', { method: 'POST', body: {} })) } catch (e) { setError(describeError(e)) } finally { setBusy(null) }
  }

  async function save() {
    if (!a) return
    setBusy('save'); setError(null)
    try {
      const body: Record<string, string> = {}
      if (provider !== a.provider) body.provider = provider
      if (model !== a.geminiModel) body.geminiModel = model
      if (key.trim()) body.geminiApiKey = key.trim()
      if (groqKey.trim()) body.groqApiKey = groqKey.trim()
      if (a.groqHasKey || groqKey.trim()) { if (voice !== (a.voiceProvider ?? 'Gemini')) body.voiceProvider = voice }
      const sentGroq = groqKey.trim() !== '', sentGemini = key.trim() !== ''
      const saved = await api<AiAdmin>('admin/ai', { method: 'PUT', body })
      qc.setQueryData(['admin-ai'], saved)
      void qc.invalidateQueries({ queryKey: ['admin-overview'] }); void qc.invalidateQueries({ queryKey: ['system-info'] })
      setKey(''); setGroqKey('')
      toast('Saved.', 'good')
      if (sentGroq && saved.groqHasKey) { setBusy(null); await runGroqTest() }
      if (sentGemini && saved.hasKey && provider === 'Gemini') { setBusy(null); await runTest() }
    } catch (e) { setError(describeError(e)) } finally { setBusy(prev => (prev === 'test' ? prev : null)) }
  }

  async function setFlag(flag: { aiWording?: boolean; allowMedia?: boolean }) {
    setBusy('save'); setError(null)
    try { qc.setQueryData(['admin-ai'], await api<AiAdmin>('admin/ai', { method: 'PUT', body: flag })); toast('Saved.', 'good') }
    catch (e) { setError(describeError(e)) } finally { setBusy(null) }
  }

  async function removeGroqKey() {
    setBusy('save'); setError(null)
    try { qc.setQueryData(['admin-ai'], await api<AiAdmin>('admin/ai', { method: 'PUT', body: { groqApiKey: '' } })); setGroqTest(null); toast('The Groq key was removed. Voice notes go back to Gemini.', 'good') }
    catch (e) { setError(describeError(e)) } finally { setBusy(null) }
  }

  async function removeKey() {
    setBusy('save'); setError(null)
    try { qc.setQueryData(['admin-ai'], await api<AiAdmin>('admin/ai', { method: 'PUT', body: { geminiApiKey: '' } })); setTest(null); void qc.invalidateQueries({ queryKey: ['admin-overview'] }); void qc.invalidateQueries({ queryKey: ['system-info'] }); toast('The key was removed.', 'good') }
    catch (e) { setError(describeError(e)) } finally { setBusy(null) }
  }

  if (ai.isPending) return <Skeletons count={3} />
  if (ai.isError || !a) return <Alert tone="error">{describeError(ai.error)}</Alert>
  const dirty = key.trim() !== '' || groqKey.trim() !== '' || provider !== a.provider || model !== a.geminiModel || ((a.groqHasKey ?? false) && voice !== (a.voiceProvider ?? 'Gemini'))

  return (
    <div className="narrow wide">
      <h1>AI setup</h1>
      <p className="muted">One free Gemini account can do everything: it reads photos of handwriting, analyses the notes and writes the implementation document, and listens to voice notes. Add a free Groq account to take over the voice notes and save the Gemini allowance.</p>

      <section className="panel">
        <h2><span className="num">1</span>Get a free key</h2>
        <ol className="plain-steps">
          <li>Open <a href="https://aistudio.google.com/apikey" target="_blank" rel="noopener noreferrer">Google AI Studio <ArrowSquareOut size={14} aria-hidden /></a> and sign in with a Google account. Use one that belongs to the company.</li>
          <li>Press <b>Create API key</b>. Choose a project if it asks, or let it make one.</li>
          <li>Copy the key (it starts with <code>AIza</code>) and paste it below.</li>
        </ol>
        <Field label="Gemini API key" help={a.hasKey ? `A key is saved (ends ${a.keyHint}). Paste a new one only to replace it.` : 'Nothing is saved yet.'}>
          {id => (
            <div className="row" style={{ flexWrap: 'nowrap' }}>
              <input id={id} className="input mono" type={show ? 'text' : 'password'} autoComplete="off" spellCheck={false} value={key} onChange={e => setKey(e.target.value)} placeholder={a.hasKey ? '••••••••••••••••' : 'AIza…'} />
              <button className="btn" type="button" onClick={() => setShow(s => !s)} aria-label={show ? 'Hide the key' : 'Show the key'}>{show ? <EyeSlash size={18} /> : <Eye size={18} />}</button>
            </div>
          )}
        </Field>
        <div className="row">
          <button className="btn primary" disabled={!dirty || busy !== null} onClick={() => void save()}>{busy === 'save' ? 'Saving…' : 'Save'}</button>
          <button className="btn" disabled={!a.hasKey || busy !== null} onClick={() => void runTest()}>{busy === 'test' ? 'Testing…' : 'Test the key'}</button>
          {a.hasKey && a.keyFromDashboard && <button className="btn text" disabled={busy !== null} onClick={() => void removeKey()}>Remove the key</button>}
        </div>
        {error && <Alert tone="error">{error}</Alert>}
        {test && (
          <div className={`alert ${test.ok ? 'good' : 'warn'}`} role="status">
            {test.ok ? <CheckCircle size={20} weight="fill" aria-hidden /> : <Warning size={20} weight="fill" aria-hidden />}<div>{test.message}</div>
          </div>
        )}
        {test?.probes && test.probes.length > 0 && (
          <table className="probes" aria-label="Which models answered just now">
            <thead><tr><th>Model</th><th>Answered?</th><th>Time</th></tr></thead>
            <tbody>
              {test.probes.map(p => (
                <tr key={p.model} className={p.ok ? 'ok' : 'bad'}>
                  <td className="mono">{p.model}</td><td>{p.ok ? 'Yes' : p.message}</td><td>{(p.milliseconds / 1000).toFixed(1)} s</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section className="panel">
        <h2><span className="num">2</span>Voice notes: a second free account (recommended)</h2>
        <p className="muted">Voice notes can be turned into text by Groq, which has its own free allowance. That keeps the Gemini allowance for analysing the notes and reading photos. If Groq is busy, the recording goes to Gemini, so nothing is lost.</p>
        <ol className="plain-steps">
          <li>Open <a href="https://console.groq.com/keys" target="_blank" rel="noopener noreferrer">Groq Console <ArrowSquareOut size={14} aria-hidden /></a> and sign up (free).</li>
          <li>Press <b>Create API Key</b>, copy it (it starts with <code>gsk_</code>) and paste it below.</li>
        </ol>
        <Field label="Groq API key" help={a.groqHasKey ? `A key is saved (ends ${a.groqKeyHint}). Paste a new one only to replace it.` : 'Nothing is saved yet. Voice notes use Gemini.'}>
          {id => (
            <div className="row" style={{ flexWrap: 'nowrap' }}>
              <input id={id} className="input mono" type={showGroq ? 'text' : 'password'} autoComplete="off" spellCheck={false} value={groqKey} onChange={e => setGroqKey(e.target.value)} placeholder={a.groqHasKey ? '••••••••••••••••' : 'gsk_…'} />
              <button className="btn" type="button" onClick={() => setShowGroq(s => !s)} aria-label={showGroq ? 'Hide the Groq key' : 'Show the Groq key'}>{showGroq ? <EyeSlash size={18} /> : <Eye size={18} />}</button>
            </div>
          )}
        </Field>
        {(a.groqHasKey || groqKey.trim() !== '') && (
          <div className="choices" role="radiogroup" aria-label="Who turns voice notes into text">
            <label className={`choice ${voice === 'Groq' ? 'on' : ''}`}>
              <input type="radio" name="voice" value="Groq" checked={voice === 'Groq'} onChange={() => setVoice('Groq')} />
              <span><b>Groq (recommended)</b><br /><span className="muted small">Fast, and it does not use the Gemini allowance.</span></span>
            </label>
            <label className={`choice ${voice === 'Gemini' ? 'on' : ''}`}>
              <input type="radio" name="voice" value="Gemini" checked={voice === 'Gemini'} onChange={() => setVoice('Gemini')} />
              <span><b>Gemini</b><br /><span className="muted small">One account for everything.</span></span>
            </label>
          </div>
        )}
        <div className="row">
          <button className="btn primary" disabled={!dirty || busy !== null} onClick={() => void save()}>{busy === 'save' ? 'Saving…' : 'Save'}</button>
          <button className="btn" disabled={!a.groqHasKey || busy !== null} onClick={() => void runGroqTest()}>{busy === 'groq' ? 'Testing…' : 'Test the Groq key'}</button>
          {a.groqHasKey && <button className="btn text" disabled={busy !== null} onClick={() => void removeGroqKey()}>Remove the Groq key</button>}
        </div>
        {groqTest && (
          <div className={`alert ${groqTest.ok ? 'good' : 'warn'}`} role="status">
            {groqTest.ok ? <CheckCircle size={20} weight="fill" aria-hidden /> : <Warning size={20} weight="fill" aria-hidden />}<div>{groqTest.message}</div>
          </div>
        )}
        {a.limits && a.limits.length > 0 && (
          <table className="probes" aria-label="What is left of the free allowance">
            <thead><tr><th>Service</th><th>Allowance</th><th>Left</th><th>Renews in</th></tr></thead>
            <tbody>
              {a.limits.map(l => (
                <tr key={l.provider + l.model + l.what}>
                  <td className="mono">{l.provider} {l.model}</td><td>{l.what}</td>
                  <td>{l.remaining ?? '-'} of {l.limit ?? '-'}</td><td>{l.renews ?? '-'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <ModelLimits a={a} />
      <DailyLimits a={a} number={4} />

      <section className="panel">
        <h2><span className="num">5</span>Which AI to use</h2>
        <div className="choices" role="radiogroup" aria-label="AI service">
          {a.providers.map(p => (
            <label key={p} className={`choice ${provider === p ? 'on' : ''}`}>
              <input type="radio" name="provider" value={p} checked={provider === p} onChange={() => setProvider(p)} />
              <span><b>{PROVIDERS[p]?.name ?? p}</b><br /><span className="muted small">{PROVIDERS[p]?.text}</span></span>
            </label>
          ))}
        </div>
        {provider === 'Gemini' && (
          <Field label="Gemini model" help="Leave on Automatic. It picks the newest free Flash model your key can use. Press “Test the key” to list the others.">
            {id => (
              <select id={id} className="input" value={model} onChange={e => setModel(e.target.value)}>
                <option value="">Automatic (recommended)</option>
                {model && !test?.models.some(m => m.id === model) && <option value={model}>{model}</option>}
                {test?.models.map(m => <option key={m.id} value={m.id}>{m.id}</option>)}
              </select>
            )}
          </Field>
        )}
        <div className="row"><button className="btn primary" disabled={!dirty || busy !== null} onClick={() => void save()}>Save</button></div>
      </section>

      <section className="panel">
        <h2><span className="num">6</span>Speed and privacy</h2>
        <label className={`choice switch ${a.allowMedia !== false ? 'on' : ''}`}>
          <input type="checkbox" checked={a.allowMedia !== false} disabled={busy !== null} onChange={e => void setFlag({ allowMedia: e.target.checked })} />
          <span><b>Allow photos and voice recordings</b><br /><span className="muted small">They are sent to the AI as they are, so patient names or numbers inside them cannot be hidden. Switch this off to allow typed notes only.</span></span>
        </label>
        <label className={`choice switch ${a.aiWording === true ? 'on' : ''}`}>
          <input type="checkbox" checked={a.aiWording === true} disabled={busy !== null} onChange={e => void setFlag({ aiWording: e.target.checked })} />
          <span><b>Let the AI rewrite the document wording</b><br /><span className="muted small">Off (recommended): the document is made from the analysed workflow in a few seconds and uses no extra tokens. On: one more AI call polishes the wording, which takes about a minute and uses tokens.</span></span>
        </label>
      </section>

      <Alert tone="warn">
        <div><b>About privacy.</b> The free tier is run by Google. Its terms may allow content sent on the free tier to be used to improve Google’s products, so check Google’s current terms before using real hospital material. The app hides patient numbers, phone numbers and names in text before it is sent, but it cannot hide them inside a photo or a recording. Ask people not to include patient details, or switch Google to a paid plan.</div>
      </Alert>
    </div>
  )
}
