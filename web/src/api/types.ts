// Mirrors the API's JSON (src/FlowCapture.Shared/Dtos.cs). Property names are camelCase, dates are ISO strings.

export interface Paged<T> { items: T[]; page: number; pageSize: number; total: number }
export interface Job { jobId: string; kind: string; state: string; percent: number | null; message: string | null; resultId: string | null; createdAt: string }

/** What a PDF would contain, shown before it is made so the person can choose: the headings with their pages as printed, the requests, and the document's look. */
export interface GuidePart { flowId: string; html: string }
export interface GuideSection { key: string; title: string; detail: string; parts: GuidePart[] }
export interface GuideRequest { flowId: string; code: string; title: string; module: string }
export interface GuideOutline { sections: GuideSection[]; requests: GuideRequest[]; style: string; cover: string }/** What to put in the PDF. Leaving a part out means all of it. */
export interface GuideChoice { sections?: string[]; flowIds?: string[]; /** Headings the person renamed: section key to the new heading. */ titles?: Record<string, string> }

export interface ProjectMembership { projectId: string; projectCode: string; projectRole: 'Executive' | 'Lead' | 'Reviewer' | string }
export interface UserProfile { id: string; email: string; displayName: string; roles: string[]; projects: ProjectMembership[] }
export interface TokenResponse { accessToken: string; accessTokenExpiresAt: string; refreshToken: string; user: UserProfile }

export interface Project { id: string; code: string; hospitalName: string; location: string; startDate: string; leadUserId: string; leadName: string; status: string; monthlyAiTokenLimit: number | null; rowVersion: string; clientId?: string | null; clientName?: string | null }
export interface DbInfo { server: string; database: string; provider: string; usingSavedServer: boolean; problem: string | null }
export interface DbTest { ok: boolean; message: string; databaseExists: boolean; existingTables: string[]; missingTables: string[] }
export interface DbTableProgress { table: string; sourceRows: number; inserted: number; alreadyThere: number; failed: number; errors: string[] }
export interface DbMove { state: 'idle' | 'running' | 'done' | 'failed'; step: string; startedAt: string | null; finishedAt: string | null; tables: DbTableProgress[]; notes: string[]; canUse: boolean; target: string | null }
export interface DbUse { saved: boolean; restartNeeded: boolean; restarting: boolean; message: string }
/** A customer. Administrators see all clients and who is assigned (userIds); a user sees only the active ones they are assigned to. */
export interface Client { id: string; name: string; location: string; isActive: boolean; projectCount: number; userIds: string[] }
export interface UserRef { id: string; displayName: string }
export interface ProjectDepartment { id: string; moduleCode: string; name: string; isCustom: boolean; assignees: UserRef[]; flowCount: number; entryCount: number; confirmedCount: number }

export type FlowStatus = 'Draft' | 'Analysed' | 'UnderReview' | 'Confirmed' | 'DocumentsGenerated'
export interface Flow {
  id: string; projectId: string; projectDepartmentId: string; moduleCode: string; departmentName: string; menuName: string
  status: FlowStatus; currentVersionNo: number; confirmedVersionNo: number | null; entryCount: number; openQuestionCount: number
  lastActivityAt: string; rowVersion: string
  /** NR (a new requirement), CR (a change request) or BUG (a bug to fix), and its number inside the module: NR-003. */
  kind?: RequestKind; requestNo?: number; requestCode?: string
}
export type RequestKind = 'NR' | 'CR' | 'BUG'
/** A module from the master list (Pharmacy, Billing...) that can be picked when a module is added to a project. */
export interface MasterDepartment { id: string; code: string; name: string; isActive: boolean }

export interface Entry {
  id: string; flowId: string; refNo: number; kind: 'Text' | 'Voice' | 'Image'; capturedAt: string; capturedBy: string; capturedByName: string; language: string
  rawText: string | null; notes: string | null; intervieweeName: string | null; intervieweeRole: string | null
  audioState: string; audioDurationSec: number | null; transcriptState: string; isDeleted: boolean; rowVersion: string
}
export interface CreateEntryRequest { id: string; kind: 'Text' | 'Voice' | 'Image'; capturedAt: string; language: string; rawText: string | null; notes: string | null; intervieweeName: string | null; intervieweeRole: string | null }
export interface Transcript { entryId: string; status: string; language: string; rawText: string | null; maskedText: string | null; userEditedText: string | null; avgConfidence: number | null; maskedCount: number; error: string | null }

export interface Language { code: string; name: string; sttLocale: string; isActive: boolean }

export interface DepartmentProgress { projectDepartmentId: string; moduleCode: string; name: string; entries: number; flows: number; analysed: number; confirmed: number; documented: number; openQuestions: number }
export interface AiUsage { month: string; inputTokens: number; outputTokens: number; estimatedCost: number; limit: number | null; runs: number }
export interface Dashboard {
  projectId: string; departmentsTotal: number; flowsTotal: number; captured: number; analysed: number; confirmed: number; documented: number
  openClarifications: number; blockingClarifications: number; lastActivityAt: string | null; departments: DepartmentProgress[]; aiUsage: AiUsage
}
export interface PendingItem { kind: string; flowId: string | null; title: string; detail: string; since: string }
export interface AuditLog { id: number; at: string; userId: string | null; userName: string | null; action: string; entityType: string; entityId: string; projectId: string | null; oldValues: string | null; newValues: string | null; correlationId: string | null }

export interface AnalysisRun {
  id: string; scope: string; mode: string; status: 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'; flowId: string | null; model: string
  inputTokens: number | null; outputTokens: number | null; durationMs: number | null; attempts: number; errorCode: string | null; errorMessage: string | null
  resultVersionNo: number | null; warnings: string[]; startedAt: string | null; finishedAt: string | null; createdAt: string
}
export interface Clarification { id: string; flowId: string; questionKey: string; section: string; question: string; options: string[] | null; status: string; isBlocking: boolean; answer: string | null; rowVersion: string }
export interface FlowComment { id: string; flowId: string; section: string; itemId: string | null; parentCommentId: string | null; text: string; isResolved: boolean; authorName: string; createdAt: string }
export interface FlowVersionSummary { versionNo: number; source: string; authorName: string; createdAt: string; changeSummary: string | null; isConfirmed: boolean }
/** A picture attached to a request; it is printed in its implementation document with its caption. */
export interface Figure { id: string; flowId: string; caption: string; stepId: string | null; sortOrder: number; contentType: string; sizeBytes: number; originalName: string; uploadedByName: string; createdAt: string }
export interface FlowContentDto { flowId: string; versionNo: number; source: string; content: FlowContent; lockedIds: string[]; createdAt: string; createdByName: string }
export interface DiffItem { section: string; itemId: string; change: string; before: unknown; after: unknown }
export interface FlowDiff { fromVersion: number; toVersion: number; items: DiffItem[] }
export interface CrossFlowLink { id: string; fromFlowId: string; fromItemId: string; toFlowId: string; toItemId: string; description: string; dataPassed: string[]; basis: string; status: string }

export interface DocumentVersion { versionNo: number; generatedAt: string; generatedByName: string; approvalStatus: 'Pending' | 'Approved' | 'Rejected'; templateVersion: string; sourceFlowVersionNos: number[]; approvedByName: string | null; approvalNote: string | null
  /** When the maker marked this version as final, to give to the team (null: a working copy). */
  finalizedAt?: string | null; finalizedByName?: string | null; finalNote?: string | null }
/** Where a version of a document stands: a working copy, final (waiting for the administrator), verified by the administrator, or returned to its maker. */
export type DocumentStage = 'Working' | 'Final' | 'Verified' | 'Returned'
// ---- the simple journey: quick projects, the shared library, the admin dashboard ----
export interface QuickProject { projectId: string; departmentId: string; flowId: string; name: string; code: string }
export interface LibraryDocument {
  id: string; docNumber: string; title: string
  /** Guide: one request's document. ModuleGuide: one PDF of every request in a module. ProjectGuide: one big PDF of the whole project. */
  kind: 'Guide' | 'ModuleGuide' | 'ProjectGuide' | 'Requirements'; projectId: string; projectName: string; flowId: string | null
  versionNo: number; createdAt: string; createdByName: string; formats: string[]; hasDiagram: boolean
  /** For a module's PDF: the module. */
  moduleId?: string | null
  stage?: DocumentStage; finalizedAt?: string | null; finalizedByName?: string | null; finalNote?: string | null
  /** The administrator's reason when the document was returned. */
  reviewNote?: string | null; verifiedByName?: string | null; verifiedAt?: string | null
  clientId?: string | null; clientName?: string | null
}
export interface AdminOverview { users: number; activeUsers: number; projects: number; flows: number; notes: number; documents: number; aiProvider: string; aiReady: boolean; awaitingVerification?: number; clients?: number }
/** One line of who handles what: a user on a client with their work (a client without people, or a person without a client, has a line too). */
export interface HandlingRow {
  clientId: string | null; clientName: string | null; clientActive: boolean; userId: string | null; userName: string | null; userEmail: string | null; userActive: boolean
  projects: number; projectsStarted: number; modules: number; requests: number; awaitingVerification: number; verified: number; returned: number; lastFinalAt: string | null
}
/** One free allowance as the AI service last reported it. */
export interface AiLimit { provider: string; model: string; what: string; limit: number | null; remaining: number | null; renews: string | null; at: string }
export interface AiAdmin {
  provider: string; providers: string[]; hasKey: boolean; keyHint: string | null; geminiModel: string; keyFromDashboard: boolean; aiWording?: boolean; allowMedia?: boolean
  groqHasKey?: boolean; groqKeyHint?: string | null; voiceProvider?: string; groqSttModel?: string; limits?: AiLimit[] | null; quotas?: AiQuota[] | null; modelQuotas?: AiModelQuota[] | null
}
export interface AiProbe { model: string; ok: boolean; milliseconds: number; message: string }
export interface AiTestResult { ok: boolean; message: string; model: string | null; models: { id: string; name: string }[]; probes?: AiProbe[] | null }
export interface AiUsageTotals { calls: number; inputTokens: number; outputTokens: number; totalTokens: number }
export interface AiUsageKind { kind: 'Analysis' | 'Guide' | 'Voice' | 'Photo' | 'Test' | string; calls: number; inputTokens: number; outputTokens: number; totalTokens: number }
export interface AiUsageWho { id: string | null; name: string; calls: number; totalTokens: number }
export interface AiUsageCall { at: string; kind: string; who: string; project: string; model: string; inputTokens: number; outputTokens: number; durationMs: number; success: boolean }
export interface AiUsageSummary {
  month: string; allUsers: boolean; provider: string; totals: AiUsageTotals; today: AiUsageTotals; lastMonth: AiUsageTotals
  byKind: AiUsageKind[]; byDay: { day: string; calls: number; totalTokens: number }[]; byUser: AiUsageWho[]; byProject: AiUsageWho[]; recent: AiUsageCall[]
  byModel?: AiModelUsage[]; quotas?: AiQuota[]; modelQuotas?: AiModelQuota[]
}
/** What one AI service used in its free-allowance day against the limit the administrator set (null = no limit), and when its day ends. */
export interface AiQuota { provider: string; usedRequests: number; usedTokens: number; limitRequests: number | null; limitTokens: number | null; resetsAt: string; reached: boolean; message: string | null }
export interface AiModelUsage { provider: string; model: string; calls: number; inputTokens: number; outputTokens: number; totalTokens: number }
export interface UserAccount { id: string; email: string; displayName: string; isActive: boolean; roles: string[]; preferredLanguage: string | null }
/** One Gemini model against its own free limits. null limit = not limited by the app. */
export interface AiModelQuota { model: string; name: string; usedToday: number; perDay: number | null; usedLastMinute: number; perMinute: number | null; tokensLastMinute: number; tokensPerMinute: number | null; reached: boolean; resetsAt: string; isPreset: boolean }
/** What the free Gemini has left today across the models in use. requestsLeft is null when a model is not limited by the app. */
export interface AiCapacity { reached: boolean; requestsLeft: number | null; resetsAt: string; message: string | null }
export interface SystemInfo { ai: string; notice: string | null; allowMedia?: boolean; capacity?: AiCapacity | null }

export interface GeneratedDocument { id: string; docNumber: string; title: string; scope: 'Flow' | 'Project' | 'Guide'; flowId: string | null; currentVersionNo: number; versions: DocumentVersion[] }

// The flow content (flow.v1). Kept loose on purpose: the editor is driven by the shape of the data, so a new field shows up without code.
export type FlowContent = Record<string, unknown> & {
  flowTitle: string
  mermaid: string
  steps: { id: string; actor: string; action: string }[]
}
