# FlowCapture: deployment and operations

This is for whoever installs and looks after the server. What was and was not verified is listed in section 11.

## 1. What runs where

| Piece | Where | Notes |
|---|---|---|
| API (ASP.NET Core, .NET 9) | A container or a Windows or Linux host | Needs a **browser** (Chrome, Chromium or Edge) for diagrams and **LibreOffice** for PDF. The Docker image has both. |
| Database | SQL Server 2019 or later, or Azure SQL | Migrations run by themselves at start-up. Background jobs (Hangfire) use the same database. |
| Files (audio, diagrams, documents, templates) | A folder or volume (`Storage:Provider=FileSystem`), or Azure Blob Storage (`AzureBlob`) | Back this up together with the database. |
| Speech to text | Azure Speech (optional) | Without it recordings upload but cannot be transcribed; typed notes work. |
| AI | Google Gemini (free tier) by default; Anthropic optional | Called from the server only. The browser never holds the key. Needs outbound HTTPS to `generativelanguage.googleapis.com`. |
| Cover logo | `templates/logo.png` (optional) | The company logo on the cover of the implementation document. Without it the cover is text only. |
| PDF and diagrams | Chrome, Chromium or Edge on the server | Draws the diagram and prints the implementation guide to PDF. Present in the Docker image. |
| Web app | Built files in the API's `wwwroot`, served by the API | One address for the app and the API. Phones and computers open the link; phones can install it. Needs **HTTPS** for installing, offline use and (later) the microphone. See `docs/05-web-app.md`. |

TLS is expected to end at a reverse proxy or the platform's front door (Azure App Service, Front Door, nginx, Caddy). The API itself speaks plain HTTP on port 8080 in the container.

## 2. Quick start with Docker Compose

```powershell
copy .env.example .env      # then edit .env: SQL password, JWT key, admin email and password, Anthropic key
docker compose up -d --build
# wait about a minute, then
curl http://localhost:8080/health/ready
```

`/health/ready` answers `Healthy`, or `Degraded` when no AI key is set yet (everything except the AI parts still works). Sign in as the administrator and open **AI setup** to add the free key. Sign in with the admin you put in `.env`, then **change that password** and remove `ADMIN_PASSWORD` from `.env`: it is only used when the database has no users.

The compose file runs SQL Server as `sa` for convenience. For a real installation create a login that owns only the `FlowCapture` database and use that in the connection string.

## 3. Configuration reference

Every setting can be given as an environment variable with `__` for `:` (for example `Security__JwtSigningKey`). The values below are the defaults in `appsettings.json` unless noted.

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Default` | LocalDB | SQL Server connection string. |
| `Database:Provider` | `SqlServer` | `Sqlite` is for development and tests only. |
| `Security:JwtSigningKey` | none, **required** | 32+ random characters. Keep it in a secret store. Changing it signs everyone out. |
| `Security:AccessTokenMinutes` / `RefreshTokenDays` | 15 / 30 | Sign-in token lifetimes. |
| `Ai:Provider` | `Gemini` | Which AI does analysis, the guide, voice notes and photos: `Gemini` (Google's free tier), `Anthropic` (paid, text only), or `Demo` (one fixed example, for trying the app: the app shows a notice and `/health` reports Degraded; never for real work). **The administrator changes this in the app (AI setup) and it takes effect at once**; the setting here is only the starting value. |
| `Gemini:ApiKey` | empty | Normally left empty: the administrator pastes the key into the app, where it is saved encrypted (with a key derived from `Security:JwtSigningKey`). Setting it here is the fallback for a fixed deployment. |
| `Ai:GeminiModel` | empty | Empty picks the newest stable free Flash model the key can use. Also choosable in the app. |
| `Anthropic:ApiKey` | empty | Only if `Ai:Provider` is `Anthropic`. |
| `Ai:Model`, `Ai:Effort`, `Ai:MaxTokens` | `claude-sonnet-5-5`, `medium`, 16000 | Model settings. Admins can override in the app's settings. |
| `Ai:MonthlyTokenLimitGlobal` | 0 (none) | Monthly token cap for the whole server; a project can have its own. At the cap new analyses get HTTP 429. |
| `Stt:Provider` | `None` | `Azure`, with `Stt:Region` and `Stt:Key`. |
| `Storage:Provider` | `FileSystem` | `AzureBlob` with `Storage:ConnectionString` and `Storage:Container`. |
| `Storage:RootPath` | `App_Data/blobs` | Folder for `FileSystem`. In the image: `/data/blobs`. |
| `Jobs:Mode` | `Hangfire` | `Inline` runs jobs inside the API process (development, tests, very small installs). |
| `Jobs:Workers` | 4 | Hangfire workers. |
| `Hangfire:Dashboard` | `false` | `/hangfire`, Admin bearer token only. |
| `Diagrams:BrowserPath`, `Diagrams:NoSandbox`, `Diagrams:TimeoutSeconds` | auto, false, 60 | Chrome for diagrams. In the image: `/usr/bin/chromium` and `NoSandbox=true`. |
| `Documents:LibreOfficePath`, `Documents:TimeoutSeconds` | auto, 120 | LibreOffice for PDF. In the image: `/usr/bin/soffice`. |
| `Documents:TemplatePath` | `templates/RGD_Template.docx` | Word template stored on the first start. After that, templates are managed in the app (Admin). |
| `Maintenance:Enabled`, `IntervalHours` | true, 6 | Housekeeping (section 6). |
| `Maintenance:StuckJobMinutes` | 30 | A job that has not moved for this long is failed. |
| `Maintenance:RefreshTokenGraceDays`, `IdempotencyDays` | 7, 7 | Clean-up ages. |
| `Https:Redirect` | true | Redirect HTTP to HTTPS (and HSTS) outside Development. Set `false` when the proxy already does it and does not send `X-Forwarded-Proto`. |
| `ForwardedHeaders:Enabled` | false | Trust `X-Forwarded-For` and `-Proto` from the proxy, so the audit trail records the real client address. Then list the proxy in `KnownProxies` (array of IPs) or `KnownNetworks` (array of `10.0.0.0/8`), or set `TrustAllProxies=true` only when the container is reachable through the proxy alone. |
| `RateLimit:AuthPerMinute` | 10 | Sign-in attempts per minute per address. |
| `RateLimit:AiPerMinute` | 20 | Requests that start an AI call (analysis, document, photo, recording) per minute per signed-in user. Protects the shared free quota. |
| Groq:BaseUrl, Groq:TimeoutSeconds | https://api.groq.com/openai/v1/, 120 | Where voice notes are sent when a Groq key is saved in the Admin dashboard (the key itself is saved there, encrypted, never in configuration). |
| `Swagger:Enabled` | false (true in Development) | API explorer at `/swagger`. Keep off in production. |
| `Seed:AdminEmail`, `Seed:AdminPassword`, `Seed:AdminName` | empty | First administrator, only when no users exist. |
| `Serilog:*` | Information | Logs go to the console. Collect them with the platform (Docker logs, Azure Monitor, Seq). |

### Moving to another SQL Server from the app

An administrator can move the app to another SQL Server in **Database** (see `docs/05-web-app.md`): it makes the missing tables, copies the rows the new server does not have, and then saves the choice in `App_Data/database.override` (next to the app; the folder must be writable by the app, for IIS the app pool identity). The file is protected with a key derived from `Security:JwtSigningKey` and is read before `ConnectionStrings:Default` at start-up; delete the file, or use **Go back to the server the app was installed with**, to return to the configured server. Set `Database:OverridePath` to keep the file elsewhere. The account used needs the right to create a database and tables (for example `dbcreator` and `db_owner`) for the copy; afterwards the app itself only needs `db_owner` on its database. The connection is encrypted but does not check the server's certificate (`TrustServerCertificate=True`); do not leave a SQL Server open to the whole internet, and allow only this server's address in its firewall. Hangfire's own tables are made by Hangfire on the new server at the next start.

## 4. Before going live

1. **Secrets** in a secret store (Key Vault, Docker or Kubernetes secrets), never in the image or the repository. Rotate the JWT key and the Anthropic key if they were ever shared.
2. **HTTPS** at the proxy, with HSTS. Without it the app still works in a browser, but it cannot be installed on a phone and the service worker (offline opening) does not run.
3. **Admin password** changed, `Seed__AdminPassword` removed.
4. **SQL Server login** with the least rights (owner of its own database only). Encrypt the connection (`Encrypt=True`, and a real certificate instead of `TrustServerCertificate=True`).
5. **Backups** (section 7) set up and a restore tried once.
6. **Swagger and the Hangfire dashboard off**, unless you need them and have restricted who reaches them.
7. **Time zone and clock**: the server stores UTC; keep the host clock synchronised (sign-in tokens depend on it).
8. **Anthropic budget**: set `Ai:MonthlyTokenLimitGlobal` and a per-project limit so a mistake cannot run up cost. The dashboard shows usage per project.
9. **Data protection decision** (section 8) made and written down.

## 5. Running more than one API instance

It works with these rules:

- Use `Storage:Provider=AzureBlob` (or a shared volume). With the local folder each instance would see only its own files.
- Use `Jobs:Mode=Hangfire` so jobs are shared. `Inline` jobs live in one process.
- Start **one** instance first after an upgrade. Migrations run at start-up and two instances migrating together can collide. The upgrade that added final documents and their verification (`AddFinalDocuments`) adds three nullable columns to `DocumentVersions` (`FinalizedAt`, `FinalizedBy`, `FinalNote`); every document that exists before the upgrade is a working copy, so the administrator sees none until a user marks one as final. The upgrade that let people choose the headings of a PDF (`AddGuideSections`) adds one nullable column (`DocumentJobRuns.SectionsJson`). The upgrade that added the PDFs of a whole project and of each module (`AddCombinedGuideDocuments`) adds one nullable column (`ProjectDepartmentId`) to `GeneratedDocuments` and to `DocumentJobRuns`. The upgrade that added deleting users (`AddUserDeletedAt`) adds one nullable column (`Users.DeletedAt`). The upgrade that added clients (`AddClients`) adds two tables (`Clients`, `ClientUsers`) and one nullable column (`Projects.ClientId`); at the first start afterwards every existing project is filed under a client named like the project, and the people on it are assigned to that client (safe to run again: only projects without a client are touched). The upgrade that added the table of AI requests (`AddAiAttempts`) only adds a table; rows older than 7 days are removed as the app runs. The upgrades that added project and request deletion (`AddProjectDeletedAt`, `AddFlowDeletedAt`, one nullable column each) change nothing that exists. The upgrade that added modules and requests (`AddRequestKinds`) numbers every existing workflow as NR-001, NR-002... inside its module, in the order it was made, and `AddFlowFigures` adds the table of attached pictures; both only add, nothing is removed or renamed. Take a backup first (section 7) as for any upgrade.
- Chrome and LibreOffice run per instance; each instance runs at most two diagram renders and one PDF conversion at a time. Size memory for that (about 1 GB per instance is comfortable).
- The housekeeping is safe to run on every instance.

## 6. Housekeeping and stuck jobs

A background service runs two minutes after start and then every `Maintenance:IntervalHours`. An administrator can also run it with `POST /api/v1/admin/maintenance/run`, which returns what it did.

- **Interrupted jobs** (analysis, document generation, transcription that were queued or running and have not moved for 30 minutes, usually because the server restarted) are marked failed with the message "The job was interrupted ... Start it again." Without this a lost job would block the next one for that flow.
- Expired sign-in tokens and old stored responses for retried requests are deleted.
- **The audit trail is never deleted by the system.** It grows by a few rows per change and holds no free text. Decide a retention period (many hospitals keep it 7 years or more) and archive older rows with a scheduled SQL job when you reach it.
- Recordings and documents are kept until you remove them; there is no automatic deletion because the right period is your decision (section 8).

## 7. Backup and restore

Back up **both**, taken at about the same time:

1. The SQL Server database (`FlowCapture`). A nightly full backup plus transaction log backups is normal.
2. The blob store: the `/data` volume, or the Azure Blob container (turn on soft delete and versioning).

Restore: restore the database, restore the files, start the API. Documents and diagrams whose files are missing fail to download with "The stored file was not found" and can be generated again from the confirmed flow; audio cannot be recovered without its backup.

## 8. Data protection and patient information

- Typed text, transcripts and the text read from photos have patient names, UHIDs and phone numbers masked before they are sent to the AI for analysis. **Recordings and photos are not masked**: they go to the AI exactly as made (Google Gemini by default, or Azure Speech if `Stt:Provider=Azure`). Google's free tier may allow what it receives to be used to improve its products. If your policy does not allow that, ask people not to include patient details, move Google to a paid plan, or agree each vendor's data terms and region first. This is the open decision from Phase 1.
- Changing `Security:JwtSigningKey` signs everyone out **and** makes the saved AI key unreadable: enter it again in AI setup.
- The AI receives only masked text. Hospital and interviewee names are also removed from what the AI sees.
- The audit trail records who changed what, when and from which address, but never copies free text (notes, transcripts, answers, comments, approval notes).
- Downloads of documents are logged in the audit trail.
- In the browser, only two things are kept on the device: the sign-in refresh token and notes that have not been sent yet (plus a draft while typing). They sit in the browser's storage for that site, are not encrypted by the app, and are removed by signing out (which asks first if notes are waiting). Use a device lock, and do not use the app on a shared computer without signing out.
- Anthropic and Azure retention terms apply to what you send them; review them for your agreement.

## 9. The web app

There is nothing to install or sign. The Docker image builds the web app (Node 22) and puts it inside the API image, so `docker compose up` gives one address that serves both. Without Docker, build it once on the machine that builds the API (`cd web; npm install; npm run build`, or just build the API in Visual Studio with Node.js installed) and publish the API; the built files in `wwwroot` go with it.

Send users the link (the HTTPS address of the server). On a phone they open it and choose **Install app** or **Add to Home screen**. An update is delivered by itself: the page is checked on every visit, and the service worker swaps in the new files the next time the app is opened. Users never run an old version for long and no store is involved.

Reverse proxies must pass `/` and everything below it to the API (the API decides which paths are files, which are pages of the app and which are API calls).

## 10. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Document job fails: "No browser ... installed" or diagram calls return 503 | Chrome, Chromium or Edge is missing, or `Diagrams:BrowserPath` is wrong. In the image it is built in. |
| Job fails: "LibreOffice is not installed" | Install LibreOffice or set `Documents:LibreOfficePath`. |
| The PDF looks different from the Word file (fonts, spacing) | The fonts the template uses are not on the server. The image has Liberation (Arial), Carlito (Calibri) and DejaVu. Add the fonts your template uses. |
| Chrome crashes in a container | Run with `Diagrams:NoSandbox=true` (needed unless the container is given extra privileges), and give the container at least 1 GB memory. |
| `ai_budget_exceeded` (429) | The monthly token limit is reached. Raise it in settings or wait for the next month. |
| A job shows "interrupted" | The server restarted while it ran. Start it again. |
| Analysis says it needs the entry to reach the server | The note is still waiting on the device. Wait for the badge at the top to say "up to date". |
| Everyone is signed out | The JWT signing key changed. |
| "Install app" is missing on a phone, or the app does not open offline | The address is not HTTPS (a plain `http://192.168...` link works as a web page only). Use a hosted address or a tunnel. |
| The root address shows JSON saying the app is "not built" | `npm run build` has not been run in the `web` folder (or Node.js is not installed so the API build could not do it). |
| Users see an old version | The service worker updates on the next open; closing and reopening the app, or a hard refresh, loads the new one. |
| Sign-in says "Something went wrong" on a plain `http://` network address, and nothing reaches the server log | An old copy of the page that used `crypto.randomUUID` and `crypto.subtle`, which browsers only provide on `https` or `localhost`. Fixed: the app now has fallbacks, so a plain-http address works for sign-in, notes and photos (the microphone and installing on a phone still need https). Reload the page with Ctrl+F5, or close and reopen the installed app, to get the new copy. |
| 401 only on a new server address | Wrong clock on the device, or the sign-in expired. Sign in again. |
| Slow first request after start | Migrations and seeding. Check `/health/ready`. |

Every error response carries a `traceId`; the same id is on every log line of that request. Ask users to quote it.

## 11. What was verified, and what was not

Verified on the build machine (Windows, .NET 9, Node 26): the whole test suite (unit, API integration on SQLite and on SQL Server LocalDB, the web app's unit and screen tests, and its real-Chrome tests at desktop and phone size), real Word, PDF (LibreOffice) and diagram (Chrome) output, and the housekeeping and audit behaviour.

Verified with Docker on the same machine:

- The image builds. Chromium, as the non-root `app` user with `--no-sandbox`, draws a diagram with the bundled Mermaid and takes a PNG; LibreOffice converts the template to a PDF; the Liberation and Carlito fonts are installed.
- `docker compose up` starts SQL Server and the API; the API applies the migrations, seeds the admin and the template, answers `/health/ready`, signs the admin in and the compose health check turns healthy. The same image also ran against SQLite (Production environment).
- Not run inside the container: a full flow-to-document generation, because that needs an Anthropic key. The drawing and PDF steps were each run in the container, and the whole pipeline is covered by the tests on the host.

Notes from building the image: the Debian mirror is reached over HTTPS in the `Dockerfile` because a plain-HTTP download of the large Chromium package was cut off by this network's filter; the same line is harmless elsewhere. A page given to Chrome must declare `<meta charset="utf-8">` (the generated diagram page does), otherwise the bundled Mermaid script is misread on Linux and nothing is drawn.

Not verified:

- Behind a real reverse proxy (the forwarded-headers settings follow the framework's documented behaviour but no test exercises them), on Azure, with Azure Blob Storage, a multi-instance setup, or Hangfire under load.
- The web app on a real phone, in Safari or in Firefox, and installed from a real HTTPS address (checked in desktop Chrome at phone size, where the manifest, icons and service worker work on `localhost`).
- Real Anthropic and Azure Speech calls (tests use fakes).
- Load and soak testing. The design assumes tens of users, not thousands.
