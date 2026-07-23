# Working in this repo

Guild tools for the wooback Discord: a static frontend on GitHub Pages (`wooback.info`)
talking to a .NET 8 backend on Fly.io with Postgres.

**`README.md` is the spec.** Architecture, every route, every config key, the auth
model, and the deploy steps live there. Read the relevant section before changing
behaviour, and update it in the same commit when behaviour changes. This file covers
only *how to work here*.

## Layout

- **Frontend — repo root.** One `.html` per app (`board`, `groups`, `members`,
  `loot`, `attendance`, `character`, `item`, `loot-prio`, …), plus `menu.js` (shared
  session helpers, `API_BASE`, the nav, item links, and the `RH` Raid-Helper module),
  `app.js`, `groups.js`, `loot-prio.js`, `styles.css`.
- **Backend — `server/WoobackVash.Api`.** `Api/` (endpoints), `Auth/`, `Config/`,
  `Data/` (EF Core + `Migrations/`), `Proxy/`, `Services/`.

No bundler, no build step for the frontend, and **no test project**. Plain browser JS
and `dotnet`.

## Verification bar

Don't report a change as done without doing the applicable checks.

- Touched JS → `node --check <file>`.
- Touched `server/` → `dotnet build` (absolute `--project` path, see Shell below).
- Touched UI → actually drive it in the browser. Use the **`local-dev`** skill to bring
  the stack up and the **`session-token`** skill to sign in.
  - To decide whether an element really renders, count `getClientRects().length`.
    Computed `display` misreports it and has produced a wrong "verified" claim before.
- Changed a model → generate the EF migration in the same commit; the app applies
  migrations on startup.

If something genuinely can't be exercised locally — the live Google-sheet proxy, a real
Warcraft Logs import, pasting into Gargul — **say so plainly** in the report instead of
letting it read as tested. That is the house norm and it is worth keeping.

## Shell

PowerShell and Bash are both available, and they take different syntax.

- **Never use a PowerShell here-string (`@'…'@`) in the Bash tool.** It has mangled
  commit messages twice. For a multi-line commit message use a Bash heredoc, or stay
  in PowerShell for the whole command.
- Always give `dotnet` and `dotnet ef` an **absolute** `--project` path. A relative one
  has failed here (`InvokeMethodOnNull` when its output was piped).
- Use `preview_start` for servers, never Bash. `.claude/launch.json` defines both.

## Git

- **Branch off `main` before committing.** Work has a habit of accumulating uncommitted
  on `main` and being branched at PR time; branch first instead.
- Subject: an imperative sentence describing the *effect*, sentence case, no
  prefix or scope — `Open the roster to everyone, read-only`,
  `List the members holding no character on their own`.
- Body: prose explaining **why**, and what deliberately did **not** change. Look at
  `git log` for the register; it is discursive, not bulleted changelog.
- Keep the trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Auth: the client only decides what to show

Client-side `isOfficer()` checks hide affordances. **The API is the enforcement** — it
rejects officer routes with 401/403 regardless. Never fix a permission problem in the
page alone; fix the endpoint's `RequireOfficer` / `RequireSession` and treat the page
change as cosmetic (and say so).

## Things that must change together

- `SHEET_DOCS` in `sheet.html` ↔ `LootSheet:Docs` in
  `server/WoobackVash.Api/appsettings.json`. Both name the same Google docs.
- An origin change ↔ `AllowedOrigins` in **both** `appsettings.json` and
  `appsettings.Development.json`.
- The session payload shape (`Auth/SessionTokenService.cs`, `SessionPayload`) ↔
  `sessionPayload()` in `menu.js`, which decodes `uid/name/officer/exp/iat`
  client-side. The record's doc comment spells out that these must not drift.
- A contract change between the HTML and `menu.js` (renaming `API_BASE`, changing what
  `renderNav` expects) ships under a ~10-minute GitHub Pages cache-skew window — ship
  it, wait it out, then rely on it. See README → *Asset caching*.
- `WOWHEAD_DOMAIN` in `menu.js` and `LEVEL_CAP` in `groups.js` both track the current
  expansion — flip them together when the guild moves on (item tooltips ↔ the max-level
  filter on the 2-group page).
