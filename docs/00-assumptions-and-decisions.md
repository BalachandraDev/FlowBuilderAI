# FlowCapture: assumptions and decisions (Phase 1)

The eight Phase 0 questions were not answered, so these are the defaults used. Each row says what to change if the default is wrong.

| # | Topic | Default used | If you disagree |
|---|---|---|---|
| 1 | Unit of one document | **One RGD per Flow** (department + menu/sub-process), holding many requirement rows (`REQ001..`). Optional consolidated project document. Document number `MRD-003` = module code + running number, **per project and module**, issued the first time that flow's document is generated and then kept stable across document versions. | Changing to "one RGD per single requirement" only changes the generator and `GeneratedDocument` grain, not the capture or AI design. |
| 2 | Category codes | `CR` Change Request, `NR` New Requirement, `ER` Enhancement. Stored in an admin master table (`RequirementCategory`), injected into the prompt and schema at runtime. "Assigned by" = the Implementation Lead of the project (editable on the document). | Edit the master data. No code change. |
| 3 | .NET version | **.NET 10 LTS** (.NET 8 and 9 both reach end of support in Nov 2026). The machine used for this work has only the .NET 9 SDK, so Phase 2 starts with installing the .NET 10 SDK and the MAUI workload. | Fall back to .NET 9 / 8: change `TargetFramework` only. Nothing in the design depends on 10. |
| 4 | Hosting | Azure: App Service (API), a worker container (Hangfire, Mermaid and PDF rendering), Azure SQL, Blob Storage, Key Vault, Azure AI Speech, FCM for push. An on-prem mapping is in `01-architecture.md`. | All external services sit behind interfaces (`IBlobStore`, `ISpeechToTextService`, `IFlowAnalysisService`, `IPushSender`). |
| 5 | Languages | `en-IN`, `ta-IN`, `hi-IN` (+ English code-mixing) as the initial admin-managed list. Azure AI Speech multi-language / continuous language identification. | Admin master data; add `ml-IN`, `te-IN`, etc. |
| 6 | Auth | Standalone ASP.NET Core Identity + JWT access token (15 min) + rotating refresh token. One organisation (AOSTA), many projects, per-project membership. | An `IIdentityProvider` seam is kept so SSO (OIDC) can replace the login endpoint later. |
| 7 | Word template | Rebuilt from the PDF: layout, headings and tables of RGD-MRD-003. Logo is a placeholder image until you supply the AOSTA logo. | Drop the real `RGD_Template.docx` and logo into `/templates`. |
| 8 | Scope | MVP = text + voice capture, offline sync, part + whole analysis, review loop, diagrams, .docx + PDF, share sheet, dashboard. **Phase 2 (later):** photo/attachments, iOS builds, email-sending from the server (the Android share sheet covers email in the MVP). | Say what moves. |

## Other decisions worth your attention

1. **A "Flow" entity is added** to your entity list. A `Flow` is one department's menu/sub-process within a project (for example MRD / "IP Case Sheet Receive"). Entries attach to a Flow; analysis, versions, comments, clarifications, status and the RGD all belong to the Flow. Entries with no menu name go to a default flow called "General" for that department.
2. **30-second target is at risk for the model call itself.** A full flow JSON is about 3k to 6k output tokens, which is roughly 40 to 90 s at typical generation speeds. So **all analysis runs are asynchronous jobs** (202 + progress + push), never held on an HTTP request. The 30 s goal is met only for small flows. I will measure it in Phase 2 and tune with streaming, prompt caching and a smaller `max_tokens` where possible. I would not promise 30 s for every flow.
3. **Diagrams are generated twice, trusted once.** Claude returns Mermaid (as you asked), but the server validates it and, after any manual edit or on a parse failure, **regenerates it deterministically from the structured steps/decisions**. A diagram can then never disagree with the data on screen.
4. **User edits are protected by the server, not by the prompt.** Locked items are sent to the AI as "authoritative", and then the server compares the result and restores any locked item the model changed.
5. **PHI:** masking runs on the transcript or typed text before any AI call, and only masked text is sent to Claude. Raw **audio** still goes to the speech vendor. Whether that is acceptable for a hospital must be decided by you or the client (options are a cloud STT with a regional endpoint and contractual no-retention terms, which you would need to confirm with the vendor, or a self-hosted STT engine; both sit behind the same interface). Masking is best-effort (regex plus name dictionary); it will not catch everything, so the UI shows the masked text for the user to review before analysis.
6. **Whole-project analysis runs on structured flows, not raw entries.** It accepts flows at status `Analysed` or higher. Documents and the combined diagram require `Confirmed`.
7. **Hand-offs are proposals.** Cross-department links from the AI are saved as `CrossFlowLink` with status `Proposed`; the Lead accepts or rejects each. Only accepted links appear in the combined diagram.
8. **Document status vs flow status:** the flow lifecycle you specified is kept as is. Document approval by a Reviewer is tracked on the document version (`Pending / Approved / Rejected`), not as another flow status.

## Changes made while building Phase 2

1. **Built on .NET 9** (the SDK installed here). `TargetFramework` is set in one place, `Directory.Build.props`. The Swashbuckle (6.9) and Azure Storage (12.23) versions are pinned for net9; bump them together when moving to .NET 10.
2. **No forced tool choice.** `claude-sonnet-5-5` rejects `tool_choice` of type `tool` or `any`. The client uses `auto`, the prompt says to call the tool, and a reply without the tool call is an error. The retry is a fresh request containing the errors and the previous output.
3. **Official Anthropic C# SDK** (13.8.0) is used instead of hand-written HTTP.
4. **`ConcurrencyStamp` instead of `rowversion`** so optimistic concurrency behaves the same on SQL Server and SQLite.
5. **Stable source references.** Each entry gets a permanent per-flow number (`E1`, `E2`...). An answered question is cited as `A<n>` where `<n>` is the number in its `Q<n>` key. Both survive re-analysis and deletes.
6. **Analysis runs fail instead of overwriting.** If a flow is edited while an analysis is running, the run ends with `stale_base` and nothing is saved.
7. **Analysis is never retried automatically** (it costs money). Transcription is retried 3 times with back-off when the speech service is busy.
8. **Cost estimate** uses list prices for Sonnet 5.5 as defaults ($2 / $10 per million tokens, $0.10 cached reads), editable in settings. Treat the figure as an estimate.
9. **Push notifications** are logged but not sent yet. The FCM adapter needs the Firebase project and is added with the mobile app (Phase 3).

## Changes made while building Phase 3

1. **The mobile app is three projects**, not one: `Mobile.Core` (offline engine, no MAUI), `Mobile.UI` (screens, no MAUI) and `Mobile` (MAUI shell). This is what lets almost all of it be built and tested without the Android tooling.
2. **Recordings are WAV (16 kHz, 16-bit, mono)**, not AAC or Opus. It is accepted by every speech service and makes pause and resume trivial, at about 1.9 MB a minute. Compression is a later improvement.
3. **The review editor is generated from the flow model**, so every section is editable and new fields appear without new screens.
4. **Diagrams are drawn on the phone** with a bundled Mermaid, so they also work offline. The server-side renderer in Phase 4 is for documents.
5. **Whole-project analysis and hand-off decisions are in the app** (Lead only).
6. **The app refuses to run with an unencrypted database.**
7. **The Android build is untested** (no Android tooling on the build machine). See `docs/05-mobile-app.md` section 7.

## Changes made while building Phase 4

1. **Diagrams are drawn by a headless Chrome or Edge** using the same bundled Mermaid 10.9.3 as the phone, instead of mermaid-cli or Kroki. No Node and no outside service (nothing leaves the server). The SVG is captured first, then a PNG at 2x for sharp print. The two copies of `mermaid.min.js` must stay identical (a unit test checks).
2. **PDF is made by LibreOffice** (`soffice --headless --convert-to pdf`) from the finished `.docx`, so the PDF is exactly what Word shows. Conversions run one at a time with their own profile folder.
3. **If a tool is missing, you are told.** No browser: diagrams fail with a `503` that says so. No LibreOffice: the job fails with that message. The `.docx` alone is never silently delivered as if it were complete.
4. **The Word template uses content controls**, so the cover details and body can be re-branded in Word without code. Tags and styles are listed in `docs/04-rgd-mapping.md`. A built-in template (`templates/RGD_Template.docx`) is used until you upload yours; the logo in it is a placeholder.
5. **Document numbers.** Flow documents are `{module}-{nnn}` per project and module and never change; a re-generation is a new version. The project document is one document per project with versions.
6. **Generation is a background job** (queued, running, succeeded or failed) so a slow render never times out a phone request. The Lead is notified when it finishes.
7. **Downloads are audited** and every version is kept and downloadable, including after the flow is edited or re-confirmed.
8. **On the phone, "download and share" is one tap:** the file is saved in the app's cache folder and the phone's share sheet opens (WhatsApp, email, Files, print). Direct email from the server is still not built.
9. **Linux containers** need Chromium, LibreOffice and fonts installed in the image, and `Diagrams:NoSandbox=true` when running as root. This is covered in the Phase 5 deployment notes.
10. **Only Leads see the project document and combined diagram on the phone** for now. Reviewers can approve flow documents there; approving a project document from the phone is not exposed yet (the API allows it).

## Changes made while building Phase 5

1. **Housekeeping.** A background service (and `POST /api/v1/admin/maintenance/run`) fails jobs that were interrupted by a restart, so a lost job can never block the next one, and removes expired sign-in tokens and old stored responses. **The audit trail is never deleted automatically**: its retention period is yours to set (`docs/06-deployment.md` section 6).
2. **Two audit problems found by the new tests and fixed:** the free-text approval note (and change summaries, dismissal reasons) was being copied into the audit trail, and document events were not tagged with their project, so a Lead's project view missed them.
3. **Dashboard and activity on the phone.** The Lead's project home now shows progress per department, and a new Activity page lists recent changes in plain words (Lead only).
4. **Hardening.** Security headers and no-store on every API response, support for running behind a reverse proxy (`ForwardedHeaders:*`), and `Newtonsoft.Json` raised to 13.0.3 because Hangfire pulled in a version with a high-severity advisory. The `NU1903` warning suppression was removed so such advisories show up in builds. One advisory remains (AngleSharp, a test-only dependency with no fixed release).
5. **Deployment.** `Dockerfile` (Chromium, LibreOffice and fonts included), `docker-compose.yml`, `.env.example`, and `docs/06-deployment.md` (configuration reference, go-live checklist, backup, privacy, APK signing, troubleshooting).

## Browser demo (added after Phase 5, at your request)

1. **A browser version for demos** (`web/FlowCapture.Web`, `docs/07-browser-demo.md`): the phone's screens served as a web page, one private session per browser tab, talking to the API. **Online only and typed notes only** (no offline mode; the microphone is not available over plain HTTP). It is a demo and review tool, not a field tool.
2. To make that possible the offline core can now be registered once per session instead of once per app (`AddMobileCore(..., ServiceLifetime.Scoped)`); the phone still uses one of everything.
3. A sync that was queued when a session ended is now ignored instead of logging an error (this also removes a possible failure on a phone when the app is closing).

## Direction change: one web app instead of a native app (at your request)

1. **The .NET MAUI app and the Blazor web host were cancelled.** The client is now a **web app** in `web/` (React + TypeScript) that opens from a shared link on any phone or computer and installs on a phone like an app. The old code is in `archive/blazor-and-maui/` (not built, not in the solution; delete it when you are sure). The changes to the pages of the earlier phases (Phase 3 and "Browser demo" above) describe that cancelled client.
2. **Same server, one address.** The API serves the built web app, so there is one link, no CORS, and the app can only talk to its own server. The page has a strict Content-Security-Policy; API responses keep theirs.
3. **Simpler offline.** The native design (encrypted local database, pull sync, conflict copies) is not built. The web app saves typed notes in the browser and sends them with retry, keeps a draft while typing, and opens with no signal; everything else needs a connection. This is a real step down from the original idea of full offline work; say if full offline matters and it can be added.
4. **HTTPS needed for the microphone.** Browsers allow the microphone, installing and service workers only on HTTPS (or `localhost`). Voice recording is built (see "Simpler product" below) and says so plainly when the address is not secure.
5. **Built in steps**, then reshaped (next section).

## Direction change: a simpler product with two logins (at your request)

1. **Two logins.** An **administrator** makes users, sets up the AI and sees everything; a **user** makes projects, adds notes, analyses, gets the diagram and the guide, and can read **all** documents (not other users' projects). The earlier per-project roles (executive, reviewer, assignments) and the department/menu structure stay in the server but are hidden: a new project is one workflow in a "General" department. The old department pages, project overview, activity list and department-wide analysis were removed from the web app; the Word requirement documents and hand-offs remain in the API.
2. **A free AI, set up by the administrator.** Voice notes, photos, analysis and the guide all use Google Gemini's free tier. The admin pastes one key into the app; it is stored encrypted and changes apply at once. Claude (paid) and a Demo mode stay available. Model names change often, so the app picks the newest stable Flash model the key can use, or lets the admin choose from the key's own list. *Not tried against the real service (no key was available); covered by tests against a faked one.*
3. **Voice notes:** recorded in the browser (up to 7 minutes each), converted to 16 kHz WAV there, sent in resumable pieces, and turned into text by Gemini. **Photos:** shrunk to 2200 px in the browser, read into text by Gemini. Both then flow through the same masking and analysis as typed text.
4. **The implementation guide** is a new document type: the AI proposes approach, screens, data, logic, tests and a phased plan from the analysed workflow; the page is built from that and the workflow, and printed to PDF by Chrome or Edge on the server (no Word template or LibreOffice needed). One document per workflow; each update is a new version.
5. **Privacy changes.** Recordings and photos are not masked and go to Google. Google's free tier may use what it receives to improve its products. The AI setup page says so; decide whether your policy allows it before using real material.
6. **Documents are readable by every signed-in user** (as asked); projects, notes and editing stay private to their owner and the admin.
7. **Local data cleared** at the start of this change: the FlowCapture database and stored files were deleted (other databases on the machine were not touched); the administrator is created again on the next start.
6. **Needs Node.js to build** (not to run): the web app is built once with `npm run build` (the API build does it on first build when Node.js is installed; the Docker image does it too).
7. `docs/05-mobile-app.md` and `docs/07-browser-demo.md` moved to `archive/blazor-and-maui/docs/`; `docs/05-web-app.md` replaces them.

## Open items for you (none block going further)

- Confirm or correct rows 1, 2, 3 and 4 above.
- Supply the real `RGD_Template.docx` and the AOSTA logo. Until then documents carry the built-in template and a placeholder logo. If your template does not contain the content controls and styles listed in `docs/04-rgd-mapping.md`, the upload tells you exactly what to add.
- Decide the PHI policy for audio and photos (they are not masked and go to Google's free tier).
- Create the free Gemini key, paste it in AI setup, press **Test the key**, and try one real analysis, one real recording and one real photo of your handwriting.
- Tell me whether full offline work (reading and editing without a connection) matters, or whether typed notes that wait and send are enough.
