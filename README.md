# FlowCapture

FlowCapture turns what a hospital department tells an implementation consultant into a **workflow diagram** and an **implementation guide (PDF)** for the developers, for BACKBONE (HMS) implementations by AOSTA India Pvt Ltd. It is a web app: open a link on a phone or a computer, sign in, and it installs like an app.

**Status:** complete and tested end to end, with caveats. The free AI (Google Gemini) has been tried for real for analysis and voice notes; photo reading and the optional AI wording are tested only against a faked service, and real analyses were sometimes slow or refused by the free limit. See `docs/05-web-app.md` section 9. The implementation document is made in seconds from the analysed workflow (no second AI call), and one button on the project page goes from notes to document.

## Quick start with the published Docker image

The image (the API and the web app together, with Chromium for the diagrams and PDF) is built by GitHub Actions and published to the GitHub Container Registry as `ghcr.io/balachandradev/flowbuilderai:latest`. It needs SQL Server, so the compose file starts one beside it:

```bash
cp .env.example .env            # set SQL_SA_PASSWORD and JWT_SIGNING_KEY (32+ random characters)
docker compose -f docker-compose.release.yml up -d
# open http://localhost:8080
```

Set `FLOWCAPTURE_IMAGE` in `.env` only to use another image or version. A project page (https://balachandradev.github.io/FlowBuilderAI/; what it is, screenshots, these steps) is published with GitHub Pages from the `site/` folder. Pages is static hosting, so the app itself runs from the image, not from Pages. `.github/workflows/` holds the three workflows: `ci.yml` (tests), `docker.yml` (image) and `pages.yml` (site).

## How it works

Two kinds of login:

- **Administrator:** adds users (and stops them), changes their name or sign-in email, makes clients and assigns users to them, sets up the free AI by pasting a key, sees every project and the totals.
- **User:** picks one of their clients, makes projects for it, and reads the documents of the projects of the clients they work for. Client > Project > Module > NR/CR > document.

A user's journey, on one page:

1. **New project:** a name, nothing else.
2. **Add notes**, in any mix: **record voice** in the browser, **upload photos** of handwritten or printed notes, or **type**. The AI turns voice and photos into text; patient identifiers are masked in text before it is sent.
3. **Analyse:** the AI structures the notes into steps, rules, requirements and open questions (checked against a JSON schema; every analysis and edit is a version, and the user's locked edits are never overwritten). Answer the questions and analyse again if wanted.
4. **Workflow and implementation document:** the diagram, and **one PDF implementation document** laid out like the company's requirement documents: a branded cover, then purpose, scope, who is involved, the flow explained stage by stage (with the diagram on its own landscape page), where it touches the system, the rules and requirements, and what is still open. It explains the flow and its touch points; it is not a development plan. The same server can also produce the numbered Word/PDF requirement documents of the earlier design through its API.
5. **AI usage:** a page that shows how many tokens the AI has used this month, what for (analysis, documents, voice, photos), by day, by project and (for the administrator) by person, with the latest calls.

Everything that needs an AI uses one free Gemini key the administrator pastes into the app (no restart, stored encrypted). A second free key, Groq, can take over the voice notes so the Gemini allowance goes further; if Groq is busy the recording goes to Gemini. Work is filed as module, then new requirement (NR) or change request (CR), each with its own document (for example `IMPL-WARD-CR-001`), and screenshots can be attached and printed in the document. Claude (paid) and a Demo mode with a fixed example are the other choices.

## Repository

| Folder | Content |
|---|---|
| `web/` | **The web app** (React + TypeScript + Vite): screens, offline outbox, tests, real-browser tests |
| `src/` | **The server:** `Domain`, `Shared`, `Application`, `Infrastructure`, `Api` (the API also serves the built web app) |
| `tests/` | Server unit and integration tests, shared `TestSupport` |
| `docs/` | `00` decisions, `01` architecture, `02` database, `03` API contract, `04` RGD document mapping, `05` web app, `06` deployment and operations |
| `prompts/`, `templates/` | AI prompt pack and schemas; the default Word template |
| `scripts/` | `demo-setup.ps1` creates two demo users |
| `archive/` | The cancelled Blazor web host and .NET MAUI mobile app, kept in case anything is wanted from them. Not built, not part of the solution. Safe to delete |
| `Dockerfile`, `docker-compose.yml`, `.env.example` | One image with the web app and the API, and a stack with SQL Server |

## Run it

You need the .NET 9 SDK, SQL Server LocalDB (or any SQL Server), **Node.js 22 or later** (to build the web app), and Chrome or Edge (the server uses it to draw diagrams and make the PDF; LibreOffice is needed only for the older Word-to-PDF documents). The AI key is pasted into the app by the administrator.

```powershell
cd web
npm install
npm run build                # writes the app into src\FlowCapture.Api\wwwroot
cd ..
dotnet run --project src\FlowCapture.Api --urls http://localhost:5199
.\scripts\demo-setup.ps1                        # once, in another terminal: two demo users
```

Open **http://localhost:5199**. The first start creates an administrator: `admin@flowcapture.local` / `ChangeMe-Dev-12345` (development only; production uses `Seed__AdminEmail` and `Seed__AdminPassword`). The demo users are `asha@demo.local` and `ravi@demo.local` (password `Demo-Passw0rd-123`). To try everything without an AI key, start the API with `Ai__Provider=Demo`. Swagger is at `/swagger`, health at `/health/ready`. **Recording needs `localhost` or HTTPS** (see `docs/05-web-app.md` section 7).

In Visual Studio, press F5 on `FlowCapture.Api`: the first build runs the web build for you when Node.js is installed. To work on the app live with hot reload: `cd web; npm run dev` (http://localhost:5173, forwards the API calls to port 5199).

**Sharing a link:** the app is the API's address. For others to open it, host it (see `docs/06-deployment.md`) or, for a short demo, use a tunnel such as Visual Studio Dev Tunnels. Installing on a phone and offline use need an HTTPS address (`docs/05-web-app.md` section 3).

With Docker (SQL Server and FlowCapture, with Chromium and LibreOffice inside):

```powershell
copy .env.example .env      # fill it in
docker compose up -d --build     # then open http://localhost:8080
```

Every setting is in `docs/06-deployment.md` section 3.

## Tests

```powershell
dotnet test FlowCapture.sln                  # the server: unit tests and API integration tests (about 5 minutes)
cd web; npm test                             # the web app: helpers, API client, outbox and every screen (a few seconds)
cd web; npm run typecheck
# real Chrome, desktop and phone size, against a running server with the demo data
# (start the API with Ai__Provider=Demo and RateLimit__AuthPerMinute=1000 for these):
cd web; $env:FLOWCAPTURE_URL="http://localhost:5199"; npm run e2e
```

Tests that need Chrome, LibreOffice or the built web app are skipped, not failed, where those are missing. Real Gemini, Anthropic and Azure Speech are never called by the tests. To run the server tests on SQL Server: `$env:FLOWCAPTURE_TEST_SQLSERVER = "(localdb)\MSSQLLocalDB"`.

## Database migrations

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> --project src\FlowCapture.Infrastructure --startup-project src\FlowCapture.Api --output-dir Persistence\Migrations
```

On SQL Server the API applies pending migrations when it starts.

## Security and privacy in short

- Patient names, UHIDs and phone numbers are masked in text before it reaches the AI. **Recordings and photos are not masked**: they go to the AI as they are. Google's free tier may use what it receives to improve its products, so ask people not to include patient details, or use a paid plan; see `docs/06-deployment.md` section 8.
- The AI key lives on the server only, encrypted in the database, and is never sent to a browser. Sign-in uses short-lived JWTs and rotating refresh tokens with reuse detection.
- The web app may only load its own files and talk only to its own server (a strict Content-Security-Policy); API responses carry no-store and security headers; sign-in is rate limited; everything except health checks and the app's files needs a token.
- The audit trail records who changed what and when, and every document download, and never copies free text.
- In the browser, only the refresh token and notes not yet sent are kept on the device, and signing out removes them (it asks first if notes are waiting).

## What is not verified

- **The web app on a real phone, in Safari and in Firefox, and installed from a real HTTPS address.** It was checked in desktop Chrome at desktop and phone size.
- **How the Word document and the diagrams look to a person**; they are tested for content and validity.
- **Production hosting beyond one machine:** the Docker image builds and runs, and the diagram and PDF tools work inside it, but a real reverse proxy, Azure, Blob Storage, several instances and load have not been tried.
- **Real Gemini, Anthropic and Azure Speech calls.** The Gemini client is covered by tests against a faked service; no key was available to try the real one. How well it hears Tamil/Hindi mixed with English, and reads real handwriting, is untested.
- **Recording on a real phone** (needs HTTPS). It was tested with Chrome's fake microphone.
- **Server email of documents** is not built.

## Known dependency advisory

The server has none (`dotnet list package --vulnerable`). `npm audit` in `web/` reports 6 low and moderate advisories (`deepmerge`, `glob`, `minimatch`) that all come from `workbox-build`, the tool that writes the service worker when the app is built. They run on the build machine only and are not part of what users download. The suggested fix downgrades the PWA plugin, so it was left as is; check again before each release.
