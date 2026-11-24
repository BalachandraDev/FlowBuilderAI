import { useEffect, type ReactElement } from 'react'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider, useQueryClient } from '@tanstack/react-query'
import { ApiError } from './api/client'
import { keys } from './api/hooks'
import type { Entry } from './api/types'
import { Shell } from './components/Shell'
import { Skeletons, ToastProvider } from './components/ui'
import { onEntrySent, startOutboxSync } from './offline/outbox'
import { AiUsage } from './pages/AiUsage'
import { AdminHome } from './pages/admin/AdminHome'
import { AiSetup } from './pages/admin/AiSetup'
import { UsersPage } from './pages/admin/Users'
import { ClientsPage } from './pages/admin/Clients'
import { DatabasePage } from './pages/admin/Database'
import { HandlingPage } from './pages/admin/Handling'
import { ModulesPage } from './pages/admin/Modules'
import { Documents } from './pages/Documents'
import { FlowPage } from './pages/Flow'
import { Home } from './pages/Home'
import { Login } from './pages/Login'
import { NewProject } from './pages/NewProject'
import { Workspace } from './pages/Workspace'
import { SessionProvider, useSession } from './state/session'

export function makeQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 10_000,
        refetchOnWindowFocus: true,
        // an error the person has to act on (not allowed, not found) will not get better by asking again
        retry: (count, error) => !(error instanceof ApiError && error.status < 500 && error.status !== 408 && error.status !== 429) && count < 2,
      },
    },
  })
}

/** Everything except the sign-in page needs a signed-in user. */
function RequireSession() {
  const { ready, user } = useSession()
  const qc = useQueryClient()
  useEffect(() => {
    if (!user) return undefined
    // when a waiting note reaches the server, show the server's copy of it and refresh the numbers that depend on it
    onEntrySent(e => {
      // the server's copy goes into the list at once, so the note turns "Saved" without waiting for the list to be fetched again
      qc.setQueryData<Entry[]>(keys.entries(e.flowId), old => (old && !old.some(x => x.id === e.id) ? [...old, e] : old))
      void qc.invalidateQueries({ queryKey: keys.entries(e.flowId) })
      for (const k of ['flows', 'flow']) void qc.invalidateQueries({ queryKey: [k] })
    })
    const stop = startOutboxSync()
    return () => { stop(); onEntrySent(undefined) }
  }, [user, qc])
  if (!ready) return <div style={{ padding: 24 }}><Skeletons count={2} /></div>
  if (!user) return <Navigate to="/login" replace />
  return <Shell />
}

/** The administrator does not work on requests: users make the documents. Their pages send an administrator to the admin pages. */
function NotAdmin({ children }: { children: ReactElement }) {
  const { isAdmin } = useSession()
  return isAdmin ? <Navigate to="/admin" replace /> : children
}

/** The pages only an administrator may open. */
function AdminOnly({ children }: { children: ReactElement }) {
  const { isAdmin } = useSession()
  return isAdmin ? children : <Navigate to="/" replace />
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route element={<RequireSession />}>
        <Route path="/" element={<Home />} />
        <Route path="/new" element={<NotAdmin><NewProject /></NotAdmin>} />
        <Route path="/p/:projectId" element={<NotAdmin><Workspace /></NotAdmin>} />
        <Route path="/flow/:flowId" element={<NotAdmin><FlowPage /></NotAdmin>} />
        <Route path="/documents" element={<Documents />} />
        <Route path="/usage" element={<AiUsage />} />
        <Route path="/admin" element={<AdminOnly><AdminHome /></AdminOnly>} />
        <Route path="/admin/users" element={<AdminOnly><UsersPage /></AdminOnly>} />
        <Route path="/admin/clients" element={<AdminOnly><ClientsPage /></AdminOnly>} />
        <Route path="/admin/modules" element={<AdminOnly><ModulesPage /></AdminOnly>} />
        <Route path="/admin/handling" element={<AdminOnly><HandlingPage /></AdminOnly>} />
        <Route path="/admin/database" element={<AdminOnly><DatabasePage /></AdminOnly>} />
        <Route path="/admin/ai" element={<AdminOnly><AiSetup /></AdminOnly>} />
        <Route path="*" element={<div className="alert warn">That page does not exist.</div>} />
      </Route>
    </Routes>
  )
}

export default function App() {
  const client = makeQueryClient()
  return (
    <QueryClientProvider client={client}>
      <ToastProvider>
        <SessionProvider>
          <BrowserRouter>
            <AppRoutes />
          </BrowserRouter>
        </SessionProvider>
      </ToastProvider>
    </QueryClientProvider>
  )
}
