# wooback-raid-tools

Guild tools for the wooback Discord server, gated behind Discord access in **two
tiers**: a **home** hub open to any member with the home role, and the officer
tools (the Lady Vashj assignment **board**, the **attendance** and **loot** logs,
and the **roster**) reserved for **officers**.

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
- **`attendance.html`** — **attendance** app (**officers only**): imports a
  Warcraft Logs report and marks its guild-tagged players present. Characters not
  yet linked to a member are created **unclaimed** for the roster to adopt.
- **`loot.html`** — **loot** log (**officers only**): every awarded item, saved to
  the guild database. Paste a **Gargul** export to import a whole raid night at once —
  winners (class-coloured), disenchants, and every bid (stored per-character as rolls);
  re-importing is idempotent (deduped on Gargul's per-award checksum). One-off items
  can still be added by hand. Awards stand alone — they don't need a raid event.
- **`members.html`** — **roster & alts** (**officers only**): the Discord↔main↔alts
  identity links; set mains, add/reassign characters, and **claim** the unclaimed
  characters that attendance & loot create. **Import from Discord** seeds the roster
  by creating a link row for every Discord member holding the member or officer role
  (needs the bot token below), so officers don't have to wait for each raider to
  sign in first.
- **`character.html`** — the **character sheet**, open to any signed-in tier:
  one character's raid setup (class/spec/role), the gear it last raided in —
  every item with its **enchant and gems** — plus everything it has won, every
  roll it has made, and its attendance, with an alt switcher across the member's
  characters. Reached from a name anywhere on the roster, loot history or loot
  stats; opened bare (`character.html`) it resolves to your own main.
  Gear comes from **Warcraft Logs**, not Blizzard: the attendance import stores a
  snapshot per character per report (see below), so the sheet reads the database,
  not a live API, and older nights stay browsable in a picker. A combat log
  carries only item/enchant/gem **ids**, so names, icons and stats come from
  **Wowhead tooltips** — `WOWHEAD_DOMAIN` at the top of the page selects the
  expansion (`classic`, `tbc`, …) and is the one line to change as the guild
  progresses.
- **`sheet.html`** — a read-only `<iframe>` of the guild's loot / BIS sheet
  (`SHEET_EMBED_URL`), open to any signed-in tier. Reads the live sheet via its
  "anyone with the link" share setting, so the sign-in gate here is for the app's
  flow, not a data barrier.
- **`menu.js`** — shared session helpers, the `API_BASE` constant, and the
  hamburger menu, used by every page.

### Asset caching

Assets are referenced with plain URLs — no `?v=` stamps. **Changing `menu.js`,
`app.js` or `styles.css` is a one-file change; no HTML needs touching.**

GitHub Pages serves every file, HTML included, with `Cache-Control: max-age=600`
plus an `ETag`. So a stale copy lives at most ~10 minutes, after which the
browser revalidates and gets a cheap `304` or the new bytes. Version stamps in
the HTML wouldn't shrink that window — the *HTML* carrying the stamp is cached
for the same 600s — so they only cost a rewrite of every page per JS change.

The one real exposure is skew: a page loaded fresh can pull a `menu.js` cached
up to 10 minutes earlier. Keep that in mind for a change that breaks the
contract between HTML and `menu.js` (renaming `API_BASE`, changing what
`renderNav` expects) — ship it, wait out the window, then rely on it. For
anything urgent, a hard refresh (Ctrl-F5) bypasses the cache immediately.

Each page points at the backend through a single constant: `AUTH_BASE`
(`index.html`), `RH_PROXY` + `API_BASE` (`app.js`), `WCL_BASE` (`logs.html`),
`API_BASE` (`loot.html`, `attendance.html`, `members.html`) — all
`https://wooback-vash-api.fly.dev`.

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
  jsonb), `/api/members` + `/api/characters` (Discord↔main↔alts identity links,
  incl. `?linked=false` for unclaimed characters, and `POST /api/members/import-discord`
  to seed link rows from the guild's member/officer roster via a bot token),
  `/api/loot` (awards; `POST /import` bulk-loads a Gargul export, deduped on checksum),
  and `/api/attendance` — `POST /import` pulls a WCL report's guild-tagged roster
  into present rows (creating unclaimed characters), with `GET /events` and
  `GET ?code=` to browse it. The same import makes a second WCL call for the
  report's `playerDetails`, storing each character's **gear snapshot** (items,
  enchants, gems) for that night and refreshing their spec/role; a failure there
  is reported back but never costs the attendance rows.
- **Character sheet** (any signed-in session) — `GET /api/characters/sheet`
  (`?id=`, `?name=`, or nothing for the caller's own main) returns the character,
  its alts, the newest gear snapshot with the list of earlier ones, its loot,
  rolls and attendance; `GET /api/characters/sheet/history?id=&code=` returns one
  earlier snapshot.
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
  "Discord__BotToken=<discord bot token>" `
  "Session__Secret=<any long random string>" `
  "RaidHelper__Token=<raid-helper API token>" `
  "WarcraftLogs__ClientId=<wcl v2 client id>" `
  "WarcraftLogs__ClientSecret=<wcl v2 client secret>" `
  "Blizzard__ClientId=<battle.net client id>" `
  "Blizzard__ClientSecret=<battle.net client secret>"
```
`Discord__BotToken` powers **Import from Discord** on the roster page. Create a bot
under the same Discord application (**Bot → Reset Token**), enable the **Server
Members Intent** (Bot → Privileged Gateway Intents), and add the bot to the guild.
It's only used to list members server-side; OAuth login itself needs no bot.
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

### 5. Blizzard credentials
**Sync guild from Blizzard** on the roster page pulls the wooback guild roster from
the Blizzard Game Data API and stamps each character with the guild it's in, so
officers can see who has left and ignore them. Create a client at
https://develop.battle.net/access/clients (the redirect URL is unused for the Client
Credentials flow) and set its id/secret as the `Blizzard__*` secrets above. The guild
identity lives in `appsettings.json` (`Blizzard` section): wooback on **Dreamscythe
(US)** via the `profile-classicann-us` namespace. Dreamscythe is a Classic
**Anniversary** realm and only that namespace can see it — retail (`profile-us`) and
Classic Era (`profile-classic1x-us`) both 404 on the guild, and the realm is missing
from their realm indexes entirely. Officer-gated, and the sync only updates guild
fields: it never creates, deletes, or ignores a character.

### 6. Loot sheet
- No "Publish to web" needed. `SHEET_EMBED_URL` in `sheet.html` is the sheet's own
  link with `/edit?usp=sharing` replaced by `/preview`. Swap the id to point it at
  a different sheet.
- The sheet's **General access must be "Anyone with the link → Viewer"** so members
  see it without a Google login. Anyone with the link can read its contents, so
  don't put anything guild-private on a tab of this sheet.

### 7. CORS / origins
The backend's allowed browser origins (`wooback.info`, `www.wooback.info`,
`cowdunstan.github.io`) are in `appsettings.json` (`AllowedOrigins`). The board's
API calls only work from those origins, not a bare `localhost` preview.

## Local development
The backend needs a local Postgres and secrets via user-secrets. See
`server/WoobackVash.Api` — `dotnet run` with a `ConnectionStrings:Default` and
`dotnet user-secrets set "Discord:ClientSecret" …`. Docker builds behind a
TLS-inspecting proxy/AV need the extra root CA in `server/ca-certs/`
(see `server/ca-certs/README.md`).
