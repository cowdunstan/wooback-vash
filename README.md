# wooback-raid-tools

Guild tools for the wooback Discord server, gated behind Discord access in **two
tiers**: a **home** hub open to any member with the home role, and the officer
tools (the Lady Vashj assignment **board** and the **loot & attendance** log)
reserved for **officers**.

The site is a static frontend on **GitHub Pages** (`wooback.info`) talking to a
**.NET 8 backend** (`server/`) hosted on **Fly.io** with **Postgres**. The backend
handles Discord OAuth, proxies Raid-Helper and Warcraft Logs, and persists the
board, identity links, loot, and attendance.

> The backend replaced an earlier Cloudflare Worker. See `server/` for the API and
> `dotnet-migration-plan.md` for the migration history.

## Frontend (GitHub Pages)

- **`index.html`** — public landing page. "Sign in with Discord" only.
- **`home.html`** — the default page after sign-in: a welcome hub with a hamburger
  menu and app cards. Open to any signed-in tier.
- **`logs.html`** — the **Warcraft Logs** app: the guild's uploaded reports
  (newest first), each linked straight to Warcraft Logs. Any signed-in tier.
- **`board.html`** — the Lady Vashj assignment board (**officers only**); `app.js`
  + `styles.css` power it. Imports rosters from Raid-Helper, and **saves/loads**
  the whole board (roster + slot counts + assignments) to the backend per raid.
- **`loot.html`** — **loot & attendance** log (**officers only**): per-raid loot
  awards and attendance, saved to the guild database.
- **`sheet.html`** — a read-only `<iframe>` of the guild's loot / BIS sheet
  (`SHEET_EMBED_URL`), open to any signed-in tier. Reads the live sheet via its
  "anyone with the link" share setting, so the sign-in gate here is for the app's
  flow, not a data barrier.
- **`menu.js`** — shared session helpers + hamburger menu, used by every page.

Each page points at the backend through a single constant: `AUTH_BASE`
(`index.html`), `RH_PROXY` + `API_BASE` (`app.js`), `WCL_BASE` (`logs.html`),
`API_BASE` (`loot.html`) — all `https://wooback-vash-api.fly.dev`.

## Backend (`server/WoobackVash.Api`)

A .NET 8 Minimal-API app (EF Core + Npgsql). Routes:

- **Discord OAuth** — `/auth/login`, `/auth/callback`. Reads the signed-in user's
  guild roles and issues a short-lived HMAC-signed session token whose `officer`
  flag records their tier. The token format is `base64url(payload).base64url(HMAC)`
  with payload `{ uid, name, officer, exp }`; pages decode it client-side.
- **Raid-Helper proxy** — `GET /v4/*`, officer-gated. The Raid-Helper token is
  attached server-side (`RaidHelper__Token`) and never reaches the browser.
- **Warcraft Logs proxy** — `GET /wcl/reports`, any valid session (logs are
  public). Server-side v2 API credentials, a cached report list (officers can
  force a refresh), and an officer-only `/wcl/ratelimit` budget check.
- **Persistence** (officer-gated) — `/api/board` (save/load the board snapshot as
  jsonb), `/api/members` + `/api/characters` (Discord↔main↔alts identity links),
  `/api/loot` and `/api/attendance` (per-raid history).
- **Health** — `/healthz` (liveness), `/readyz` (DB reachability + error detail).

Non-secret config (Discord client id, guild id, role ids, WCL guild identity)
lives in `server/WoobackVash.Api/appsettings.json`.

## How the gate works

1. Landing page sends the user to `<API_BASE>/auth/login`.
2. The backend redirects to Discord (`identify guilds.members.read` scope — no bot
   needed), then Discord calls back to `<API_BASE>/auth/callback`.
3. The backend reads the user's roles via `/users/@me/guilds/{guild}/member`:
   - holds an `OFFICER_ROLE_IDS` role → officer session (`officer: true`);
   - holds `HOME_ROLE_ID` (or is an officer) → home session (`officer: false`);
   - neither → redirected back to the landing page with a "no access" message.
   On success it upserts the member, mints a signed session, and redirects to
   `home.html#session=…`.
4. Pages store the session and send it as `Authorization: Bearer <token>`. The
   backend rejects officer routes with no session (`401`) or a non-officer session
   (`403`). Client-side checks only decide what to *show*; the real enforcement is
   the backend refusing data without a valid signature.

## Setup

### 1. Discord application
- https://discord.com/developers/applications → your app.
- **OAuth2 → Redirects** → add exactly:
  `https://wooback-vash-api.fly.dev/auth/callback`
- **OAuth2** → copy the **Client Secret** (set as a backend secret below).
- The **Application ID**, `GUILD_ID`, `OFFICER_ROLE_IDS`, and `HOME_ROLE_ID` are
  already in `appsettings.json`.

### 2. Deploy the backend to Fly.io
From `server/`:
```
fly launch --no-deploy            # first time; creates the app from fly.toml
fly postgres create               # a Postgres cluster
fly postgres attach <pg-app> -a wooback-vash-api    # sets DATABASE_URL
fly deploy
```
The app applies EF Core migrations on startup, so the schema is created on first
boot against a reachable database.

### 3. Backend secrets
.NET binds nested config with **double underscores** — use these exact names
(not the flat `DISCORD_CLIENT_SECRET` style):
```
fly secrets set -a wooback-vash-api `
  "Discord__ClientSecret=<discord client secret>" `
  "Session__Secret=<any long random string>" `
  "RaidHelper__Token=<raid-helper API token>" `
  "WarcraftLogs__ClientId=<wcl v2 client id>" `
  "WarcraftLogs__ClientSecret=<wcl v2 client secret>"
```
`DATABASE_URL` is set by `fly postgres attach`. Setting secrets restarts the app.
Verify with `curl https://wooback-vash-api.fly.dev/readyz` → `{"db":"ok"}`.

### 4. Warcraft Logs credentials
Create a v2 API client at https://www.warcraftlogs.com/api/clients/ (any name; the
redirect URL is unused for the Client Credentials flow) and set its id/secret as
the `WarcraftLogs__*` secrets above. The guild identity — wooback on the **Fresh**
(Classic Anniversary) realm **Dreamscythe (US)**, so the route targets
`fresh.warcraftlogs.com` — is in `appsettings.json` (`WarcraftLogs` section); a
Fresh guild is not visible on the retail `www` API. The OAuth token endpoint stays
on `www.warcraftlogs.com` (shared across game versions).

### 5. Loot sheet
- No "Publish to web" needed. `SHEET_EMBED_URL` in `sheet.html` is the sheet's own
  link with `/edit?usp=sharing` replaced by `/preview`. Swap the id to point it at
  a different sheet.
- The sheet's **General access must be "Anyone with the link → Viewer"** so members
  see it without a Google login. Anyone with the link can read its contents, so
  don't put anything guild-private on a tab of this sheet.

### 6. CORS / origins
The backend's allowed browser origins (`wooback.info`, `www.wooback.info`,
`cowdunstan.github.io`) are in `appsettings.json` (`AllowedOrigins`). The board's
API calls only work from those origins, not a bare `localhost` preview.

## Local development
The backend needs a local Postgres and secrets via user-secrets. See
`server/WoobackVash.Api` — `dotnet run` with a `ConnectionStrings:Default` and
`dotnet user-secrets set "Discord:ClientSecret" …`. Docker builds behind a
TLS-inspecting proxy/AV need the extra root CA in `server/ca-certs/`
(see `server/ca-certs/README.md`).
