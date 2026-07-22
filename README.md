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
- **`groups.html`** — **2 group organisation** (**officers only**): the guild runs
  the same raid twice at different times — one Raid-Helper signup for **mains**, a
  second for **alts** — so this page loads *both* signups, resolves them against
  the roster links, and splits them into two 25-man groups by drag-and-drop or one
  **Auto-allocate** press (balancing tanks / healers / ranged / melee and spreading
  classes). **One person holds at most one slot per group**: someone signed up to
  both raids contributes a chip per character they own and takes a slot in each
  group, never two in the same one. Identity is the **Discord user id** the
  Raid-Helper signup carries, matched against `Member.discordUserId` — so a
  raider is recognised even when they sign up on an alt the roster has never
  seen; a character-name match against `/api/members` is the fallback for a
  signup with no user id. A signup that matches no member is flagged `UNLINKED`
  on its chip, though its Discord id still pairs that person's two signups so
  they can't be double-booked either. Someone signed up to only one raid appears as their main. An **item
  check** answers "who has Dragonspine Trophy, and which group are they in?":
  type any comma-separated item names and every wearer is pilled on their chip
  and listed by group, read from each character's latest gear snapshot via
  `/api/items/list`. The arrangement is saved in the browser (`localStorage`),
  not on the server.
- **`attendance.html`** — **attendance** app (**officers only**): imports a
  Warcraft Logs report and marks its guild-tagged players present. Characters not
  yet linked to a member are created **unclaimed** for the roster to adopt.
- **`loot.html`** — **loot** log (**officers only**): every awarded item, saved to
  the guild database. Paste a **Gargul** export to import a whole raid night at once —
  winners (class-coloured), disenchants, and every bid (stored per-character as rolls);
  re-importing is idempotent (deduped on Gargul's per-award checksum). One-off items
  can still be added by hand. Awards stand alone — they don't need a raid event.
- **`members.html`** — **roster & alts**, readable by any signed-in tier and
  **editable by officers only**: the Discord↔main↔alts identity links; set mains,
  add/reassign characters, and **claim** the unclaimed characters that attendance
  & loot create. Everyone else sees the same lists with every control that writes
  hidden — the API is what enforces it, since both GETs take any session while
  every write stays officer-gated (or self-only).
  **Import from Discord** seeds the roster
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
  not a live API, and older nights stay browsable in a picker. The log names each
  item and spells out its enchant ("+7 Spell Power and +4 Critical Strike") —
  that text exists nowhere else, since an enchant id is not a spell id — while
  **gems** arrive as bare item ids and are named by **Wowhead**, which also
  supplies every hover tooltip. `WOWHEAD_DOMAIN` in `menu.js` selects the
  expansion (`classic`, `tbc`, …) and is the one line to change as the guild
  progresses. Item names link to `item.html`, not out to Wowhead. A slot can list more than one item: `playerDetails` covers the whole
  night, so a mid-raid swap shows up as "also worn".
- **`item.html`** — the **item page**, open to any signed-in tier: which guild
  characters have the item equipped in their latest logged raid (a gem matches the
  piece it is socketed into), how many times it has dropped, who it was awarded to
  and every roll on it, plus its icon, hover tooltip and a Wowhead link. Every item
  name on the site links here — the loot log, loot history, loot stats, and each
  item, gem and "also worn" entry on a character sheet.
- **`sheet.html`** — a read-only `<iframe>` of the guild's loot / BIS sheets
  (`SHEET_DOCS`, one per phase with a button to switch between them), open to any
  signed-in tier. Reads the live sheets via their "anyone with the link" share
  setting, so the sign-in gate here is for the app's flow, not a data barrier.
- **`loot-prio.html`** — **loot prio** (**officers only**): the join between the
  loot sheets and who is actually raiding. Pick one Raid-Helper signup and a raid
  — **Black Temple** or **Mount Hyjal** from the P3 sheet, **Serpentshrine
  Cavern** or **Tempest Keep** from the P2 one — and every boss's items come back
  with the characters signed up that hold prio, in the sheet's own order. Both
  sheets write prio as **spec tokens**
  (`Cuffs of Devastation → Arcane > Balance > Ele > Destro`); `loot-prio.js` maps
  those to specs (`SPEC_TOKENS`) and matches them against the spec each raider
  signed up with, falling back to the spec a Warcraft Logs import last saw for
  that character. P2 spells the same specs out where P3 abbreviates
  (`Retribution` for `Ret`, `Feral Tank` for `Bear`), so one table carries both —
  its longer keys are matched exactly before any P3 key is tried as a substring.

  Two of the guild's tokens are ambiguous as text — `Resto` is the druid *or* the
  shaman, `Holy` the paladin *or* the priest — and the sheet settles them with
  **cell colour**: every token is filled with that class's colour. So the page
  reads the tab's **embedded view** (`/sheet/loot`, which keeps the fills) rather
  than the CSV export (which doesn't), maps each fill to a class
  (`SHEET_CLASS_FILLS`, nearest-colour so a shade off still lands) and narrows the
  token to it. That is what lets *Bracers of Martydom* (`Holy` in white → priests)
  and *Howling Wind Bracers* (`Resto` in blue → the shaman) come out as different
  people, and why a repeated chain like `Holy > Resto > Holy > Resto` reads
  correctly as pally, resto sham, priest, resto druid. If the embedded view ever
  stops parsing the page falls back to the CSV, says so in its status line, and
  marks the tokens it can no longer separate with a `?`. A token the table doesn't
  know is reported rather than silently dropped.
  `MS > OS` items are shown as open to everyone. Each candidate carries **HAS**
  (already wearing it, from the gear snapshots), **WON** (already awarded it) and
  a `×N` tally of their wins in the last 28 days, so a tie inside a tier has a
  tiebreaker. **Copy as text** dumps the whole plan for pasting into Discord.
  Which tabs make up which raid is `RAID_TABS` in `loot-prio.js`. The two sheets
  are shaped differently and the entry says so: a P3 raid is **one tab** that
  banners each boss inside it, a P2 raid is **one tab per boss** (plus its trash
  tab, plus the shared tier-set tab) and each names the section it belongs to.

  **Tier tokens** get special handling, because a token drops as one item and is
  redeemed into a class-specific piece: the sheet names the token (`Chestguard of
  the Forgotten Conqueror`), but the winner wears the piece (`Lightbringer
  Chestguard`), which is all the gear snapshots see. `TIER_TOKENS` in `loot-prio.js`
  maps each token to the item ids of every class piece it turns into, so **a raider
  wearing the upgrade counts as holding the token** — HAS fires, they are struck out
  and prio moves past them, and they drop from the soft-reserve export. The match is
  per class and per slot: a paladin holding the Conqueror shoulder is detected by the
  paladin shoulder piece, not the priest's or a helm. P3 names its tier tokens in
  full; P2's shared tier-set tab writes them bare under a slot banner (`Tier sets —
  Helms` → `Vanquished Champion`), so `loot-prio.js` rejoins banner and label into
  the real item name (`Helm of the Vanquished Champion`) before parsing — otherwise
  all five slots collapse to one lookup and never resolve to an item id.

  **Gargul SR string** turns the plan into a soft-reserve import. A soft reserve
  is flat where the sheet's prio is ordered, so the rule is: **the top tier
  reserves each item, minus anyone who already has it** — and when that empties a
  whole tier, prio drops to the next one down. The page shows the same walk it
  exports: someone skipped is struck through, and the tier that ended up
  reserving has its rank badge lit. The sheet's spec token rides along as each
  player's Gargul note, so the addon can say *why* they hold it.

  The string is `base64(zlib(JSON))` — the current format from Gargul's
  `Classes/SoftRes.lua` (`importGargulData`), not the CSV one it warns is
  deprecated. `CompressionStream('deflate')` supplies the zlib wrapper
  `LibDeflate:DecompressZlib` expects. Item **ids** come from
  `POST /api/items/resolve`, because the sheet carries names only. `plusOnes` is
  0 for everyone since we track none — Gargul asks before overwriting plus-ones
  it already has, and the page says to answer **No**.
- **`menu.js`** — shared session helpers, the `API_BASE` constant, the hamburger
  menu, the item-link helpers (`itemLink`, `loadWowhead`, `SLOT_ORDER`,
  `WOWHEAD_DOMAIN`) every page renders items with, and the **`RH`** module: the
  Raid-Helper event fetchers (`listEvents`, `fetchEvent`, `mapSignups`), the
  class/spec tables, the role classifiers (`isTank`/`isHealer`/`isRanged`) and the
  draggable-chip markup shared by `board.html` and `groups.html`. It is one `RH`
  object rather than bare globals because `app.js` declares names like
  `CLASS_COLORS` itself — a global of the same name here would be a duplicate-
  `const` SyntaxError.

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
  guild roles and issues an HMAC-signed session token whose `officer` flag records
  their tier. The token format is `base64url(payload).base64url(HMAC)` with payload
  `{ uid, name, officer, exp, iat }`; pages decode it client-side.
- **Session renewal** — `POST /auth/refresh`, any valid session. Returns a token
  with a fresh `exp` (same uid/name/officer, original `iat`), so an active raider
  is never bounced back to Discord. `menu.js` calls it on page load once a session
  is past the halfway point of its window. Sessions last `Session:TtlSeconds`
  (7 days) and can be renewed until `Session:MaxLifetimeSeconds` (30 days) after
  the original sign-in — that cap is what bounds how stale the `officer` flag can
  get, since roles are only re-read at login.
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
- **Item page** (any signed-in session) — `GET /api/items?id=` or `?name=` returns
  the item (name, icon, quality, item level), who has it equipped in their latest
  gear snapshot, how often it dropped, and every award with its rolls. An id also
  picks up hand-typed awards that carry only the name, so both reach one page.
- **Loot-sheet proxy** — `GET /sheet/loot?doc=&gid=`, officer-gated. Returns one
  tab of one of the guild's Google loot sheets (cached 10 min per doc/tab/view)
  for `loot-prio.html`. `?doc=` names a phase — `p3` (the default, so an older
  client keeps working) or `p2` — and must be a key of `LootSheet:Docs`;
  `?format=html` (the default) is the embedded view, which keeps the cell colours
  the sheets encode class in, and `?format=csv` is the plain export, ~9 KB against
  ~140 KB but with no formatting at all. There
  is no credential here — the docs are link-shared, so both answer an
  anonymous GET; the proxy exists only because Google sends no CORS header. The
  document ids are `LootSheet:Docs` in `appsettings.json`, the same docs
  `SHEET_DOCS` in `sheet.html` embeds — **change both together**, and keep
  their General access on "Anyone with the link → Viewer" or both pages break.
- **Item-name lookup** — `POST /api/items/resolve` `{ names: [...] }`, officer-gated,
  max 500 names. Returns `{ resolved: { name: id }, unresolved: [...] }`. Bridges the
  loot sheet (names) to a Gargul soft reserve (ids) via Blizzard's item search on
  the **static** classicann namespace (`Blizzard:StaticNamespace`) — TBC's item
  table, so "Cuffs of Devastation" is 30870 and not the retail item that reused the
  name. That search is fuzzy, so results are re-matched against the name asked for:
  exact, then one-typo (the sheet has a few — `Bracers of Martydom`,
  `Antonida's Aegis`, `Mymidon's Treads`), then again with trailing words dropped,
  which resolves annotated rows like `Shroud of the Highborne Healer Prio`. The
  Warglaives are the one place the sheet distinguishes two items sharing a name,
  as `(MH)` / `(OH)`; those are picked apart by id. Ids are cached for the process
  lifetime — an item's id never changes. Currently 203 of the sheet's 209 names
  resolve; the rest are reported, never guessed.
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
5. On each page load a session past its halfway point is swapped for a fresh one
   via `POST /auth/refresh`, so steps 1–3 only repeat after 30 days of renewals
   (or a week away from the site).

Because tokens are signed with `Session__Secret`, changing that value logs
everybody out at once — keep it stable across deploys.

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

### 6. Loot sheets
- One document per phase: **P3** (Black Temple, Hyjal) and **P2** (SSC, Tempest
  Keep). Both are listed in `SHEET_DOCS` in `sheet.html` and in `LootSheet:Docs`
  in the backend's `appsettings.json` — **keep the two lists in step**.
- No "Publish to web" needed. `sheet.html` builds each embed from the document id
  with `/preview`, which is the sheet's own share link minus `/edit?usp=sharing`.
  Swap an id to point a phase at a different sheet.
- Each sheet's **General access must be "Anyone with the link → Viewer"** so
  members see it without a Google login. Anyone with the link can read its
  contents, so don't put anything guild-private on a tab of either sheet.

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
