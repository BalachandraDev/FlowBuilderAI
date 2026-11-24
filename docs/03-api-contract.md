# FlowCapture: REST API contract (v1)

Base path `/api/v1`. OpenAPI / Swagger UI at `/swagger` (Dev and Test only). JSON, camelCase, UTC ISO-8601 timestamps.

## Implementation status (end of Phase 4)

Everything in section 2 is implemented and covered by the integration tests **except**:

| Not yet built | Planned |
|---|---|
| `/documents/{id}/versions/{n}/email` | later (the phone's share sheet covers the MVP) |

Differences from the first draft of this contract (the code and Swagger are authoritative):

- **Phase 4, documents.** `POST /flows/{id}/documents` and `POST /projects/{id}/documents` return `202` with a `JobDto` (`Location: /api/v1/jobs/{id}`); poll `GET /jobs/{id}` until `state` is `Succeeded` or `Failed` (`message` carries the reason). Both need an `Idempotency-Key`. A flow that is not confirmed gives `422 not_confirmed`; a project with nothing confirmed gives `422 nothing_to_document`.
- **A project document is one document per project** (`PRJ-{code}-C01`) that gains a version each time it is generated, not a new `C02` each time. A flow document keeps its number (`MRD-001`) for ever; each generation adds a version.
- `GET /projects/{id}/documents` returns project-level and flow-level documents; `GET /flows/{id}/documents` returns that flow's. Both return `DocumentDto` with all versions, newest first.
- `GET /documents/{id}/versions/{n}/download?format=docx|pdf` is audited (`AuditLog` action `Download`). An unknown format gives `422`; an unknown version `404`.
- `POST /documents/{id}/versions/{n}/approve` takes `{ approved, note }`; a rejection needs a note (`400`); only a `Pending` version can be decided (`422`).
- `POST /projects/{id}/diagram/combined` draws the diagram **synchronously** and returns `DiagramInfoDto` (not `202`). `GET` returns `404` until it has been drawn. `GET /flows/{id}/diagram?format=mmd` needs no browser and works in any status after the first analysis (`422 not_analysed` before).
- Admin templates: `GET/POST /admin/document-templates` (multipart `file`, `name`, `version`; uploads are checked against the required placeholders and styles, `400` with the reasons) and `POST /admin/document-templates/{id}/activate`.
- **Phase 5.** `POST /admin/maintenance/run` (Admin) runs the housekeeping now and returns `{ stuckAnalyses, stuckDocuments, stuckTranscripts, refreshTokensRemoved, idempotencyRecordsRemoved }`. Every response carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a restrictive `Content-Security-Policy` and `Cache-Control: no-store` (Swagger excepted). A job that was interrupted ends as `Failed` with code `interrupted`. `GET /audit?projectId=` (Lead) now includes document, document version, document job, flow version and diagram events for that project, and `Download` entries.
- When the server has no browser or no LibreOffice, the call (or the job) fails with a `503`/job message that says which one is missing.

- `EntryDto` has `refNo` (the stable `E#` number) and `isDeleted`; `AnalysisRunDto` has `warnings` and, for whole-project runs, `result`.
- `POST /projects/{id}/flows:analyse-department` returns `{ runs, skipped }`; flows that cannot be analysed yet (for example a recording still being transcribed) are listed in `skipped` with the reason.
- Sync payloads: `CreateEntry` = `SyncCreateEntryPayload`, `UpdateEntry` = `UpdateEntryRequest`, `CreateComment` = `SyncCreateCommentPayload`, `AnswerClarification` = `AnswerClarificationOp`, `DeleteEntry` = empty object. The entity id is `SyncOpDto.entityId`.
- `GET /entries/{id}/transcript` also returns `error` when transcription failed. `POST /entries/{id}/transcribe` takes an optional `{ language }`.
- Errors that carry data: `409` includes `serverCopy`; `422` and `429` include a machine-readable `code` (for example `transcript_pending`, `blocking_questions`, `ai_budget_exceeded`).
- `GET /health/ready` returns `Degraded` (HTTP 200) when no Anthropic key is configured.

## 1. Conventions

| Item | Rule |
|---|---|
| Auth | `Authorization: Bearer <jwt>` on everything except `/auth/login`, `/auth/refresh`, `/health/*`. |
| Roles | `EXE` Implementation Executive, `LEAD` Implementation Lead, `REV` Reviewer, `ADM` Admin. Project-scoped endpoints also require **membership of that project** (ADM bypasses). "own" = the user's own records. |
| Correlation | Client may send `X-Correlation-Id`; server always returns one and logs it. |
| Idempotency | Creating `POST`s accept `Idempotency-Key` (GUID). Repeating a key returns the original response (stored for 24 h). Captured entities also use client-generated ids. |
| Concurrency | Mutable resources carry `rowVersion` (base64). Send `If-Match: <rowVersion>` on `PUT`/`DELETE`; mismatch returns `409`. Flow content edits send `baseVersionNo` in the body instead. |
| Async work | Returns `202 Accepted`, `Location: /api/v1/jobs/{jobId}`, body `JobDto`. Completion also triggers a push notification. |
| Errors | RFC 7807 `application/problem+json`: `type`, `title`, `status`, `detail`, `traceId`, and `errors` (field to messages) for 400. Codes: 400 validation, 401, 403, 404, 409 conflict, 422 business rule (e.g. wrong status), 429 AI budget or rate limit, 5xx. |
| Paging | `?page=1&pageSize=25` (max 100). Response: `{ items, page, pageSize, total }`. |

## 2. Endpoints

### Auth and devices
| Method | Path | Roles | Description |
|---|---|---|---|
| POST | `/auth/login` | public | Email + password. Returns tokens and profile. |
| POST | `/auth/refresh` | public | Rotate refresh token. |
| POST | `/auth/logout` | any | Revoke refresh token. |
| GET | `/auth/me` | any | Profile, roles, project memberships. |
| PUT | `/devices` | any | Register or refresh a push token. |

### Users and admin master data (ADM)
| Method | Path | Roles | Description |
|---|---|---|---|
| GET / POST | `/users` | ADM (GET also LEAD) | List / create user. |
| PUT | `/users/{id}` | ADM | Update profile, active flag, roles. |
| POST | `/users/{id}/reset-password` | ADM | Issue a temporary password. |
| GET / POST / PUT | `/master/departments`, `/master/departments/{id}` | GET any, write ADM | Department master. |
| GET / PUT | `/master/requirement-categories` | GET any, PUT ADM | Category codes and definitions. |
| GET / PUT | `/master/languages` | GET any, PUT ADM | Languages and STT locales. |
| GET / PUT | `/settings` | ADM | AI model, token limits, max audio minutes, PHI patterns, name dictionary. |
| GET / POST | `/admin/prompts` | ADM | List prompt templates / add a new version. |
| POST | `/admin/prompts/{id}/activate` | ADM | Make a version the active one for its key. |
| GET / POST | `/admin/document-templates` | ADM | List / upload a `.docx` template. |
| POST | `/admin/document-templates/{id}/activate` | ADM | Activate a template. |

### Projects and departments
| Method | Path | Roles | Description |
|---|---|---|---|
| GET | `/projects` | any (own memberships; ADM all) | List. |
| POST | `/projects` | LEAD, ADM | Create (hospital, code, location, start date, lead, status). |
| GET / PUT | `/projects/{id}` | member / LEAD | Read / update. |
| GET / PUT | `/projects/{id}/members` | member / LEAD, ADM | List / replace members and project roles. |
| GET | `/projects/{id}/departments` | member | Departments with counts and assignees. |
| POST | `/projects/{id}/departments` | LEAD | Add a master department or a custom one. |
| PUT | `/projects/{id}/departments/{pdId}/assignments` | LEAD | Replace assigned executives. |
| GET | `/projects/{id}/dashboard` | member | `DashboardDto`. |
| GET | `/projects/{id}/pending` | LEAD | "What's pending" list. |

### Flows
| Method | Path | Roles | Description |
|---|---|---|---|
| GET | `/projects/{id}/flows?departmentId=&status=` | member | List flows. |
| POST | `/projects/{id}/flows` | EXE, LEAD | Create a flow (department + menu name). Idempotent on (department, menu name). |
| GET | `/flows/{id}` | member | Flow header, status, current version summary, counts. |
| GET | `/flows/{id}/content` | member | Current version content (`flow.v1`) with `versionNo`, `lockedIds`. |
| PUT | `/flows/{id}/content` | EXE (flows they are assigned to), LEAD | Manual edit; creates a version. Body: full content, `baseVersionNo`, `lockedIds`, `changeNote`. Server revalidates, regenerates Mermaid, and moves `Confirmed`/`DocumentsGenerated` to `UnderReview`. |
| POST | `/flows/{id}/submit-review` | EXE, LEAD | Analysed to UnderReview. |
| POST | `/flows/{id}/confirm` | **LEAD** | Body `{ versionNo }`. UnderReview to Confirmed. 422 if blocking questions are open or the version is not the current one. |
| POST | `/flows/{id}/reopen` | LEAD | Confirmed to UnderReview without editing. |

### Capture entries, audio, transcripts
| Method | Path | Roles | Description |
|---|---|---|---|
| POST | `/flows/{id}/entries` | EXE, LEAD | Create entry (client GUID in body). Idempotent. |
| GET | `/flows/{id}/entries` | member | List entries with sync/transcript status. |
| GET | `/entries/{id}` | member | One entry. |
| PUT | `/entries/{id}` | own (EXE) / LEAD | Edit text, notes, interviewee, language. `If-Match`. |
| DELETE | `/entries/{id}` | own (EXE) / LEAD | Soft delete. Blocked with 422 if it is the only source of a Confirmed flow. |
| POST | `/audio-uploads` | EXE, LEAD | Init resumable upload: entry id, size, sha256, content type, duration, chunk size. |
| GET | `/audio-uploads/{uploadId}` | uploader | Received chunk numbers and state. |
| PUT | `/audio-uploads/{uploadId}/chunks/{n}` | uploader | Binary chunk, `Content-MD5` optional. Idempotent per chunk. |
| POST | `/audio-uploads/{uploadId}/complete` | uploader | Verify size and checksum, assemble, enqueue transcription. 202. |
| GET | `/entries/{id}/audio` | member | Stream audio (HTTP range supported). |
| GET | `/entries/{id}/transcript` | member | Raw, masked and edited text, status, confidence. |
| PUT | `/entries/{id}/transcript` | EXE own, LEAD | Save user-corrected transcript (server re-masks). |
| POST | `/entries/{id}/transcribe` | EXE own, LEAD | Re-run transcription (language override allowed). 202. |

### Sync
| Method | Path | Roles | Description |
|---|---|---|---|
| POST | `/sync/push` | EXE, LEAD | Batch of ops (`CreateEntry`, `UpdateEntry`, `DeleteEntry`, `CreateComment`, `AnswerClarification`). Per-op result: `Applied`, `Duplicate`, `Conflict` (+ server copy), `Rejected` (+ reason). Max 100 ops. |
| GET | `/sync/pull?since=<cursor>&projectId=` | member | Changes since the cursor: entries, transcripts, clarifications, flow status and version numbers, comments, assignments. Returns `nextCursor` and `hasMore`. |

### Analysis
| Method | Path | Roles | Description |
|---|---|---|---|
| POST | `/flows/{id}/analyses` | EXE (assigned), LEAD | Part analysis. Body: `{ mode: Initial or Reanalyse, entryIds? }`. 202. 429 if budget exceeded. `entryIds` omitted = all entries of the flow. Re-analysis uses the current version, its locks and answered questions. |
| POST | `/projects/{id}/flows:analyse-department` | EXE, LEAD | Convenience: queue a part analysis for every flow of one department. Body `{ projectDepartmentId }`. 202 with one job per flow. |
| POST | `/projects/{id}/analyses` | LEAD | Whole-project analysis (background). Body `{ flowIds? }`, default all flows at `Analysed` or later. 202. |
| GET | `/analysis-runs/{runId}` | member | Status, tokens, duration, error, result version number. |
| GET | `/flows/{id}/analyses` | member | Run history for a flow. |
| POST | `/analysis-runs/{runId}/cancel` | requester, LEAD | Cancel if still `Queued`. |
| GET | `/projects/{id}/cross-links?status=` | member | Hand-offs from whole analysis. |
| PUT | `/cross-links/{id}` | LEAD | Accept or reject (`status`). |

### Versions
| Method | Path | Roles | Description |
|---|---|---|---|
| GET | `/flows/{id}/versions` | member | Version list: number, source, author, time, change summary, confirmed flag. |
| GET | `/flows/{id}/versions/{n}` | member | Full content of version n. |
| GET | `/flows/{id}/versions/{a}/diff/{b}` | member | Item-level diff: steps, decisions, rules, requirements and other sections (added, removed, modified, with before/after). |
| POST | `/flows/{id}/versions/{n}/rollback` | LEAD | New version copying version n. Moves status to `UnderReview` if it was confirmed. |

### Clarifications and comments
| Method | Path | Roles | Description |
|---|---|---|---|
| GET | `/flows/{id}/clarifications?status=` | member | Open and answered questions. |
| PUT | `/clarifications/{id}` | EXE, LEAD | Answer (`answer`) or dismiss (`status: Dismissed`, `reason`), mark `isBlocking` (LEAD). |
| GET | `/flows/{id}/comments?section=` | member | Threads. |
| POST | `/flows/{id}/comments` | any member (REV included) | Comment on a section or item. Client GUID. |
| PUT | `/comments/{id}/resolve` | LEAD, REV | Resolve or reopen. |

### Diagrams and documents
| Method | Path | Roles | Description |
|---|---|---|---|
| GET | `/flows/{id}/diagram?format=svg\|png\|mmd&versionNo=` | member | Diagram of the current or given version (rendered on demand if missing). Available in any status. |
| POST | `/flows/{id}/diagram/regenerate` | EXE, LEAD | Rebuild diagram from structure. |
| GET | `/projects/{id}/diagram/combined?format=svg\|png\|mmd` | member | Combined swimlane diagram (from confirmed flows + accepted cross-links). |
| POST | `/projects/{id}/diagram/combined` | LEAD | Generate the combined diagram. 202. |
| POST | `/flows/{id}/documents` | LEAD | Generate the RGD (docx + pdf) from the confirmed version. 202. Requires status `Confirmed` or later. |
| POST | `/projects/{id}/documents` | LEAD | Consolidated project document from all confirmed flows. Body: `{ flowIds? }`. 202. |
| GET | `/projects/{id}/documents` | member | Documents and their latest version. |
| GET | `/documents/{id}` | member | Document with all versions. |
| GET | `/documents/{id}/versions/{n}/download?format=docx\|pdf` | member | File download. Audited. |
| POST | `/documents/{id}/versions/{n}/approve` | REV, LEAD | Approve or reject (`approved`, `note`). |
| POST | `/documents/{id}/versions/{n}/email` | LEAD | Email the file to given addresses (**Phase 2**; the Android share sheet covers MVP). |

### Jobs, dashboard, audit, usage, health
| Method | Path | Roles | Description |
|---|---|---|---|
| GET | `/jobs/{jobId}` | requester, LEAD | `JobDto` with state and progress. |
| GET | `/audit?entityType=&entityId=&projectId=&from=&to=&userId=` | LEAD (own projects), ADM | Audit trail. |
| GET | `/usage/ai?projectId=&month=yyyy-MM` | LEAD, ADM | Token and cost totals vs limit. |
| GET | `/health/live`, `/health/ready` | public | Liveness, readiness (SQL, blob, Hangfire, AI key). |

### The simple journey (added with the two-login web app)
| Method | Path | Who | Description |
|---|---|---|---|
| POST | `/projects/quick` | user, ADM | `{ name, description? }`: a project (the caller is its lead), one "General" department and one workflow, in one call. Idempotent. Returns `QuickProjectDto`. |
| POST | `/flows/{id}/images` | can capture | Multipart `File` (JPEG, PNG or WebP, 8 MB at most), optional `Language`, `EntryId` (the same id twice makes one note), `Notes`. Creates an `Image` entry and queues reading it into text. Business-rule errors are HTTP 422 with a `code`. |
| GET | `/entries/{id}/image` | member | The stored photo. |
| POST | `/entries/{id}/transcribe` | can capture | Also retries a photo that could not be read. |
| POST | `/flows/{id}/guide` | can capture | Queues the implementation guide (PDF) for the current workflow version. Returns a `JobDto` (`Kind: GuideDocument`), poll `/jobs/{id}`. A second call while one runs returns the running job. |
| GET | `/library` | any signed-in user | Every generated document: `LibraryDocumentDto` (`Kind` is `Guide` or `Requirements`, `Formats` is `["pdf"]` or `["pdf","docx"]`). |
| GET | `/library/{documentId}/diagram?format=png\|svg` | any signed-in user | The diagram the document was made from. |
| GET | `/documents/{id}/versions/{n}/download?format=pdf` | any signed-in user | Any user may read and download any document. A guide has no Word file (HTTP 422 `pdf_only` for `docx`). |
| GET | `/ai-usage?month=yyyy-MM` | any signed-in user | Tokens the AI used in the month, by purpose, day, project, and person: `AiUsageSummaryDto`. An administrator sees everyone's (`allUsers`), anybody else only their own. Written by every AI call (analysis, the implementation document, voice, photos, key tests). |
| GET | `/system/info` | any signed-in user | `{ ai: "gemini" \| "anthropic" \| "demo" \| "no-key", notice }`. |
| GET | `/admin/overview` | ADM | Users, projects, flows, notes, documents, and whether the AI is ready. |
| GET, PUT | `/admin/ai` | ADM | `{ provider, providers, hasKey, keyHint, geminiModel }`. PUT `{ provider?, geminiModel?, geminiApiKey? }`: `null` leaves the key, an empty text removes it. The key is encrypted and never returned. |
| POST | `/admin/ai/test` | ADM | Checks the saved Gemini key: lists the models it can use and asks one for a short answer. |
| PUT | `/users/{id}` | ADM | An administrator cannot stop or demote themselves (HTTP 422 `self_lockout`). |

Entry kinds are now `Text`, `Voice` and `Image`. Voice is sent as before (`/audio-uploads`, resumable pieces), as 16 kHz mono WAV from the web app.

## 3. DTOs (C#, in `FlowCapture.Shared`)

```csharp
// ---- common ----
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
public record JobDto(Guid JobId, string Kind, string State /* Queued|Running|Succeeded|Failed */,
                     int? Percent, string? Message, Guid? ResultId, DateTime CreatedAt);

// ---- auth ----
public record LoginRequest(string Email, string Password, string? DeviceId);
public record TokenResponse(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, UserProfileDto User);
public record RefreshRequest(string RefreshToken);
public record UserProfileDto(Guid Id, string Email, string DisplayName, string[] Roles,
                             IReadOnlyList<ProjectMembershipDto> Projects);
public record ProjectMembershipDto(Guid ProjectId, string ProjectCode, string ProjectRole);
public record RegisterDeviceRequest(string Platform, string PushToken);

// ---- projects ----
public record CreateProjectRequest(string HospitalName, string Code, string Location, DateOnly StartDate, Guid LeadUserId, string Status);
public record ProjectDto(Guid Id, string Code, string HospitalName, string Location, DateOnly StartDate,
                         Guid LeadUserId, string LeadName, string Status, string RowVersion);
public record ProjectDepartmentDto(Guid Id, string ModuleCode, string Name, bool IsCustom,
                                   IReadOnlyList<UserRefDto> Assignees, int FlowCount, int EntryCount, int ConfirmedCount);
public record AddProjectDepartmentRequest(Guid? DepartmentId, string? CustomName, string? ModuleCode);
public record UserRefDto(Guid Id, string DisplayName);

// ---- flows ----
public record CreateFlowRequest(Guid ProjectDepartmentId, string MenuName);
public record FlowDto(Guid Id, Guid ProjectId, Guid ProjectDepartmentId, string ModuleCode, string DepartmentName,
                      string MenuName, string Status, int CurrentVersionNo, int? ConfirmedVersionNo,
                      int EntryCount, int OpenQuestionCount, DateTime LastActivityAt, string RowVersion);
public record FlowContentDto(Guid FlowId, int VersionNo, string Source, JsonElement Content /* flow.v1 */,
                             string[] LockedIds, DateTime CreatedAt, string CreatedByName);
public record UpdateFlowContentRequest(int BaseVersionNo, JsonElement Content, string[] LockedIds, string? ChangeNote);
public record ConfirmFlowRequest(int VersionNo);

// ---- capture ----
public record CreateEntryRequest(Guid Id, string Kind /* Text|Voice */, DateTime CapturedAt, string Language,
                                 string? RawText, string? Notes, string? IntervieweeName, string? IntervieweeRole);
public record EntryDto(Guid Id, Guid FlowId, string Kind, DateTime CapturedAt, Guid CapturedBy, string CapturedByName,
                       string Language, string? RawText, string? Notes, string? IntervieweeName, string? IntervieweeRole,
                       string AudioState /* None|Uploading|Complete|Failed */, int? AudioDurationSec,
                       string TranscriptState /* None|Queued|Processing|Ready|Failed */, string RowVersion);
public record InitAudioUploadRequest(Guid EntryId, long SizeBytes, string Sha256, string ContentType, int DurationSec, int ChunkSizeBytes);
public record AudioUploadDto(Guid UploadId, int ChunkSizeBytes, int TotalChunks, int[] ReceivedChunks, string State);
public record TranscriptDto(Guid EntryId, string Status, string Language, string? RawText, string? MaskedText,
                            string? UserEditedText, double? AvgConfidence, int MaskedCount);
public record SaveTranscriptRequest(string Text);

// ---- sync ----
public record SyncPushRequest(string DeviceId, IReadOnlyList<SyncOpDto> Ops);
public record SyncOpDto(Guid OpId, string Type, Guid EntityId, string? BaseRowVersion, JsonElement Payload);
public record SyncPushResponse(IReadOnlyList<SyncOpResultDto> Results);
public record SyncOpResultDto(Guid OpId, string Result /* Applied|Duplicate|Conflict|Rejected */,
                              string? NewRowVersion, JsonElement? ServerCopy, string? Reason);
public record SyncPullResponse(string NextCursor, bool HasMore, IReadOnlyList<EntryDto> Entries,
                               IReadOnlyList<TranscriptDto> Transcripts, IReadOnlyList<ClarificationDto> Clarifications,
                               IReadOnlyList<FlowDto> Flows, IReadOnlyList<CommentDto> Comments);

// ---- analysis ----
public record StartAnalysisRequest(string Mode /* Initial|Reanalyse */, Guid[]? EntryIds);
public record StartProjectAnalysisRequest(Guid[]? FlowIds);
public record AnalysisRunDto(Guid Id, string Scope, string Mode, string Status, Guid? FlowId, string Model,
                             int? InputTokens, int? OutputTokens, int? DurationMs, int Attempts,
                             string? ErrorCode, string? ErrorMessage, int? ResultVersionNo,
                             DateTime? StartedAt, DateTime? FinishedAt);
public record CrossFlowLinkDto(Guid Id, Guid FromFlowId, string FromItemId, Guid ToFlowId, string ToItemId,
                               string Description, string[] DataPassed, string Basis, string Status);
public record DecideLinkRequest(string Status /* Accepted|Rejected */);

// ---- versions ----
public record FlowVersionSummaryDto(int VersionNo, string Source, string AuthorName, DateTime CreatedAt,
                                    string? ChangeSummary, bool IsConfirmed);
public record FlowDiffDto(int FromVersion, int ToVersion, IReadOnlyList<DiffItemDto> Items);
public record DiffItemDto(string Section, string ItemId, string Change /* Added|Removed|Modified */,
                          JsonElement? Before, JsonElement? After);

// ---- clarifications and comments ----
public record ClarificationDto(Guid Id, Guid FlowId, string QuestionKey, string Section, string Question,
                               string[]? Options, string Status, bool IsBlocking, string? Answer, string RowVersion);
public record UpdateClarificationRequest(string? Answer, string? Status, string? DismissReason, bool? IsBlocking);
public record CommentDto(Guid Id, Guid FlowId, string Section, string? ItemId, Guid? ParentCommentId,
                         string Text, bool IsResolved, string AuthorName, DateTime CreatedAt);
public record CreateCommentRequest(Guid Id, string Section, string? ItemId, Guid? ParentCommentId, string Text);

// ---- outputs ----
public record GenerateDocumentRequest(bool IncludeDiagrams = true, string? AssignedBy = null);
public record GenerateProjectDocumentRequest(Guid[]? FlowIds, string? AssignedBy);
public record DocumentDto(Guid Id, string DocNumber, string Title, string Scope, Guid? FlowId,
                          int CurrentVersionNo, IReadOnlyList<DocumentVersionDto> Versions);
public record DocumentVersionDto(int VersionNo, DateTime GeneratedAt, string GeneratedByName,
                                 string ApprovalStatus, string TemplateVersion, int[] SourceFlowVersionNos);
public record ApproveDocumentRequest(bool Approved, string? Note);

// ---- dashboard ----
public record DashboardDto(Guid ProjectId, int DepartmentsTotal, int FlowsTotal,
                           int Captured, int Analysed, int Confirmed, int Documented,
                           int OpenClarifications, int BlockingClarifications, DateTime? LastActivityAt,
                           IReadOnlyList<DepartmentProgressDto> Departments,
                           AiUsageDto AiUsage);
public record DepartmentProgressDto(Guid ProjectDepartmentId, string ModuleCode, string Name,
                                    int Entries, int Flows, int Analysed, int Confirmed, int Documented, int OpenQuestions);
public record PendingItemDto(string Kind /* UnanalysedEntries|OpenQuestions|AwaitingConfirm|AwaitingDocument|LinkToDecide|DocumentToApprove */,
                             Guid? FlowId, string Title, string Detail, DateTime Since);
public record AiUsageDto(string Month, long InputTokens, long OutputTokens, decimal EstimatedCost, long? Limit);
```

Notes:
- `JsonElement Content` holds `flow.v1` exactly as in `prompts/schemas/flow.v1.json`. The server validates it on every write, so the mobile app edits sections but cannot store invalid structure.
- The Mobile project references only `FlowCapture.Shared`; the `flow.v1` model is mirrored as C# records there (`FlowContent` and its parts) with System.Text.Json attributes, generated from the schema by hand in Phase 2 and kept in sync by a unit test that round-trips `mrd-003.expected.json`.
