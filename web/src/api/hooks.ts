import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { del, get, post, put } from './client'
import type {
  AdminOverview, AiAdmin, HandlingRow, AiUsageSummary, AnalysisRun, AuditLog, Clarification, Client, CrossFlowLink, Dashboard, Entry, Figure, Flow, FlowContentDto, FlowVersionSummary, GeneratedDocument, Language,
  LibraryDocument, MasterDepartment, Paged, PendingItem, Project, ProjectDepartment, RequestKind, SystemInfo, UserAccount,
} from './types'

export const keys = {
  content: (flowId: string) => ['content', flowId] as const,
  versions: (flowId: string) => ['versions', flowId] as const,
  clarifications: (flowId: string) => ['clarifications', flowId] as const,
  runs: (flowId: string) => ['runs', flowId] as const,
  links: (projectId: string) => ['links', projectId] as const,
  categories: ['categories'] as const,
  projects: ['projects'] as const,
  departments: (projectId: string) => ['departments', projectId] as const,
  flows: (projectId: string) => ['flows', projectId] as const,
  flow: (flowId: string) => ['flow', flowId] as const,
  entries: (flowId: string) => ['entries', flowId] as const,
  figures: (flowId: string) => ['figures', flowId] as const,
  dashboard: (projectId: string) => ['dashboard', projectId] as const,
  pending: (projectId: string) => ['pending', projectId] as const,
  audit: (projectId: string) => ['audit', projectId] as const,
  languages: ['languages'] as const,
}

export const useSystemInfo = () =>
  useQuery({ queryKey: ['system-info'], queryFn: () => get<SystemInfo>('system/info'), staleTime: 60_000, retry: false })
/** Every generated document of everyone: the shared library. */
export const useLibrary = (enabled = true) => useQuery({ queryKey: ['library'], queryFn: () => get<LibraryDocument[]>('library'), enabled })
/** The documents of one flow. While a guide is being made the caller passes `watching` so it checks again every two seconds. */
export const useFlowDocuments = (flowId?: string, watching = false) =>
  useQuery({ queryKey: ['flow-documents', flowId ?? ''], queryFn: () => get<GeneratedDocument[]>(`flows/${flowId}/documents`), enabled: !!flowId, refetchInterval: watching ? 2000 : false })
/** Tokens the AI used in a month ("2026-11", or this month when empty). Everyone's for an administrator, one's own for anybody else. */
export const useAiUsage = (month?: string) =>
  useQuery({ queryKey: ['ai-usage', month ?? 'now'], queryFn: () => get<AiUsageSummary>('ai-usage', { month }), staleTime: 15_000 })
export const useHandling = () => useQuery({ queryKey: ['admin-handling'], queryFn: () => get<HandlingRow[]>('admin/handling') })
export const useAdminOverview = (enabled = true) => useQuery({ queryKey: ['admin-overview'], queryFn: () => get<AdminOverview>('admin/overview'), enabled })
export const useAiAdmin = () => useQuery({ queryKey: ['admin-ai'], queryFn: () => get<AiAdmin>('admin/ai') })
export const useUsers = (enabled = true) => useQuery({ queryKey: ['users'], queryFn: () => get<UserAccount[]>('users'), enabled })
/** Clients: all of them for an administrator, the active ones the user is assigned to for anybody else. */
export const useClients = (enabled = true) => useQuery({ queryKey: ['clients'], queryFn: () => get<Client[]>('clients'), enabled })

export const useProjects = (enabled = true) => useQuery({ queryKey: keys.projects, queryFn: () => get<Project[]>('projects'), enabled })
export const useDepartments = (projectId?: string) =>
  useQuery({ queryKey: keys.departments(projectId ?? ''), queryFn: () => get<ProjectDepartment[]>(`projects/${projectId}/departments`), enabled: !!projectId })
export const useFlows = (projectId?: string) =>
  useQuery({ queryKey: keys.flows(projectId ?? ''), queryFn: () => get<Flow[]>(`projects/${projectId}/flows`), enabled: !!projectId })
export const useFlow = (flowId?: string) =>
  useQuery({ queryKey: keys.flow(flowId ?? ''), queryFn: () => get<Flow>(`flows/${flowId}`), enabled: !!flowId })
/** A recording or photo that is still being turned into text. */
export const isBeingRead = (e: Entry) => e.kind !== 'Text' && !e.isDeleted && (e.transcriptState === 'Queued' || e.transcriptState === 'Processing' || (e.kind === 'Voice' && e.audioState !== 'Complete' && e.audioState !== 'Failed'))
/** The notes of a flow. While a recording or photo is being read it checks every two seconds, otherwise every fifteen. */
export const useEntries = (flowId?: string) =>
  useQuery({
    queryKey: keys.entries(flowId ?? ''), queryFn: () => get<Entry[]>(`flows/${flowId}/entries`), enabled: !!flowId,
    refetchInterval: q => (q.state.data?.some(isBeingRead) ? 2000 : 15_000),
  })
export const useFigures = (flowId?: string) =>
  useQuery({ queryKey: keys.figures(flowId ?? ''), queryFn: () => get<Figure[]>(`flows/${flowId}/figures`), enabled: !!flowId })
export const useDashboard = (projectId?: string, enabled = true) =>
  useQuery({ queryKey: keys.dashboard(projectId ?? ''), queryFn: () => get<Dashboard>(`projects/${projectId}/dashboard`), enabled: !!projectId && enabled })
export const usePending = (projectId?: string, enabled = true) =>
  useQuery({ queryKey: keys.pending(projectId ?? ''), queryFn: () => get<PendingItem[]>(`projects/${projectId}/pending`), enabled: !!projectId && enabled })
export const useLanguages = () =>
  useQuery({ queryKey: keys.languages, queryFn: () => get<Language[]>('master/languages'), staleTime: 60 * 60_000 })

export const useContent = (flowId?: string, enabled = true) =>
  useQuery({ queryKey: keys.content(flowId ?? ''), queryFn: () => get<FlowContentDto>(`flows/${flowId}/content`), enabled: !!flowId && enabled, staleTime: 0, gcTime: 0 })
export const useVersions = (flowId?: string) =>
  useQuery({ queryKey: keys.versions(flowId ?? ''), queryFn: () => get<FlowVersionSummary[]>(`flows/${flowId}/versions`), enabled: !!flowId })
export const useClarifications = (flowId?: string) =>
  useQuery({ queryKey: keys.clarifications(flowId ?? ''), queryFn: () => get<Clarification[]>(`flows/${flowId}/clarifications`), enabled: !!flowId })
export const useCategories = () =>
  useQuery({ queryKey: keys.categories, queryFn: () => get<{ code: string; isActive: boolean }[]>('master/requirement-categories'), staleTime: 60 * 60_000 })
export const useLinks = (projectId?: string, enabled = true) =>
  useQuery({ queryKey: keys.links(projectId ?? ''), queryFn: () => get<CrossFlowLink[]>(`projects/${projectId}/cross-links`), enabled: !!projectId && enabled })

/** The analyses of a flow, newest first. While one is queued or running it checks again every two seconds. */
export const useRuns = (flowId?: string) =>
  useQuery({
    queryKey: keys.runs(flowId ?? ''),
    queryFn: () => get<AnalysisRun[]>(`flows/${flowId}/analyses`),
    enabled: !!flowId,
    refetchInterval: q => (q.state.data?.[0] && ['Queued', 'Running'].includes(q.state.data[0].status) ? 2000 : false),
  })

/** After an analysis or an edit, everything that shows the flow is out of date. */
export function useRefreshFlow() {
  const qc = useQueryClient()
  return (flowId: string, projectId?: string) => {
    for (const key of [keys.flow(flowId), keys.content(flowId), keys.versions(flowId), keys.clarifications(flowId), keys.runs(flowId), keys.entries(flowId)])
      void qc.invalidateQueries({ queryKey: key })
    void qc.invalidateQueries({ queryKey: ['flows'] })
    void qc.invalidateQueries({ queryKey: ['departments'] })
    void qc.invalidateQueries({ queryKey: ['dashboard'] })
    void qc.invalidateQueries({ queryKey: ['pending'] })
    if (projectId) void qc.invalidateQueries({ queryKey: keys.links(projectId) })
  }
}

export function useAudit(projectId: string, enabled: boolean) {
  return useQuery({
    queryKey: keys.audit(projectId),
    queryFn: () => get<Paged<AuditLog>>('audit', { projectId, page: 1, pageSize: 100 }),
    enabled,
  })
}

/** Deletes a request (NR or CR): it leaves the lists. Nothing is erased on the server. */
export function useDeleteFlow(projectId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (flowId: string) => del(`flows/${flowId}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.flows(projectId) }); void qc.invalidateQueries({ queryKey: keys.departments(projectId) })
      void qc.invalidateQueries({ queryKey: ['library'] }); void qc.invalidateQueries({ queryKey: keys.dashboard(projectId) })
    },
  })
}

/** Deletes a project: it leaves every list. Nothing is erased on the server, so it can be brought back by the administrator. */
export function useDeleteProject() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (projectId: string) => del(`projects/${projectId}`),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: keys.projects }); void qc.invalidateQueries({ queryKey: ['library'] }); void qc.invalidateQueries({ queryKey: ['admin-overview'] }); void qc.invalidateQueries({ queryKey: ['ai-usage'] }) },
  })
}

export function useCreateFlow(projectId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (v: { projectDepartmentId: string; menuName: string; kind?: RequestKind }) => post<Flow>(`projects/${projectId}/flows`, v, { idempotent: true }),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: keys.flows(projectId) }); void qc.invalidateQueries({ queryKey: keys.departments(projectId) }) },
  })
}

/** The modules an administrator keeps in the master list, offered when a module is added to a project. They hardly ever change. */
export const useMasterDepartments = () =>
  useQuery({ queryKey: ['master-departments'], queryFn: () => get<MasterDepartment[]>('master/departments'), staleTime: 10 * 60_000 })

/**
 * Changes the heading of a request. The new heading shows at once (the screen does not wait for the server), and the old one comes back if the server refuses,
 * for example because the module already has a request with that name.
 */
export function useRenameFlow(projectId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (v: { flowId: string; menuName: string }) => put<Flow>(`flows/${v.flowId}`, { menuName: v.menuName }),
    onMutate: async v => {
      await qc.cancelQueries({ queryKey: keys.flows(projectId) })
      const before = qc.getQueryData<Flow[]>(keys.flows(projectId))
      if (before) qc.setQueryData<Flow[]>(keys.flows(projectId), before.map(f => (f.id === v.flowId ? { ...f, menuName: v.menuName } : f)))
      return { before }
    },
    onError: (_e, _v, ctx) => { if (ctx?.before) qc.setQueryData(keys.flows(projectId), ctx.before) },
    onSettled: (_d, _e, v) => {
      void qc.invalidateQueries({ queryKey: keys.flows(projectId) }); void qc.invalidateQueries({ queryKey: keys.flow(v.flowId) })
      void qc.invalidateQueries({ queryKey: ['library'] }); void qc.invalidateQueries({ queryKey: ['flow-documents', v.flowId] })
    },
  })
}

/** Changes the heading of a project (the hospital name). The server wants the whole project back, so the other values are sent as they are. */
export function useRenameProject() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (v: { project: Project; hospitalName: string }) => put<Project>(`projects/${v.project.id}`, {
      hospitalName: v.hospitalName, location: v.project.location, startDate: v.project.startDate, leadUserId: v.project.leadUserId,
      status: v.project.status, monthlyAiTokenLimit: v.project.monthlyAiTokenLimit,
    }),
    onMutate: async v => {
      await qc.cancelQueries({ queryKey: keys.projects })
      const before = qc.getQueryData<Project[]>(keys.projects)
      if (before) qc.setQueryData<Project[]>(keys.projects, before.map(p => (p.id === v.project.id ? { ...p, hospitalName: v.hospitalName } : p)))
      return { before }
    },
    onError: (_e, _v, ctx) => { if (ctx?.before) qc.setQueryData(keys.projects, ctx.before) },
    onSettled: () => { void qc.invalidateQueries({ queryKey: keys.projects }); void qc.invalidateQueries({ queryKey: ['library'] }); void qc.invalidateQueries({ queryKey: ['admin-overview'] }) },
  })
}
