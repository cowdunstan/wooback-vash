# Migration plan: Cloudflare Worker → .NET 8 + EF Core backend

**Goal:** replace the stateless Cloudflare Worker with a .NET 8 backend that (a) does everything
the Worker does today — Discord OAuth, Raid-Helper proxy, Warcraft Logs proxy — and (b) adds real
persistence in Postgres: identity links (Discord ↔ WoW main ↔ alts), Vash board state, and
loot/attendance history.

## Decisions locked (from Q&A)

| Decision | Choice |
|---|---|
| Scope | **Full replacement** — retire the Worker entirely |
| Persist | Identity links, Vash board state, loot/attendance history |
| Host + DB | **Container on Fly.io/Render + Postgres** |
| Auth | **.NET owns Discord OAuth** |
| Frontend | **Stays on GitHub Pages** (`wooback.info`); session carried as a **bearer token** in `localStorage` (unchanged transport) |
| Delivery | Roadmap first (this doc) → scaffold on approval |

## Target architecture

```
Browser (GitHub Pages, wooback.info)
  index/home/board/logs/sheet.html + app.js
        │  Authorization: Bearer <session token>   (unchanged shape)
        ▼
.NET 8 API  (container on Fly.io)  ── Postgres (Fly Postgres / Supabase)
  • /auth/login, /auth/callback   → Discord OAuth, mint session token
  • /v4/*                         → Raid-Helper proxy   (officer-gated)
  • /wcl/reports                  → Warcraft Logs proxy (any signed-in tier)
  • /api/*                        → persistence (members, board, loot, attendance)
        │
        ▼
  Discord API · raid-helper.xyz · fresh.warcraftlogs.com
```

**Token compatibility matters.** The pages decode the token client-side today —
`JSON.parse(atob(token.split('.')[0]))` and read `payload.exp` / `payload.officer`
(see `index.html`, `home.html`, `menu.js`). The .NET backend will mint the **same
`base64url(payloadJSON).base64url(HMAC-SHA256)` shape** so the frontend needs **zero**
token-parsing changes — only the three base URLs move.

## Tech stack

- **.NET 8 (LTS)**, **ASP.NET Core Minimal APIs** — small surface, low ceremony.
- **EF Core 8 + Npgsql** (Postgres provider), code-first migrations.
- **Postgres** with a `jsonb` column for the board snapshot (see data model).
- **Serilog** for logging; **IMemoryCache** to replicate the Worker's 5-min WCL cache.
- **Docker** multi-stage build; **fly.toml** for deploy.

## Data model (EF Core entities)

Identity, loot, and attendance are normalized; the board snapshot is stored as `jsonb`
(it mirrors the in-memory `{roster, assignments}` shape in `app.js`, so no lossy mapping
up front — we can normalize later if reporting needs it).

```
Member                         // one per Discord user, upserted at login
  Id (guid)  DiscordUserId (unique)  DiscordUsername  DisplayName
  LastSeenAt  CreatedAt
  Characters : Character[]

Character                      // main + alts, linked to a Member
  Id  MemberId (FK, nullable)  Name  Class  Spec  Realm
  IsMain (bool)  Notes

RaidEvent                      // mirrors a Raid-Helper event (or a manual one)
  Id  RhEventId (nullable, unique)  Title  StartsAt  Zone

BoardLayout                    // the Vash board state for an event
  Id  RaidEventId (FK, nullable)  Name
  State (jsonb)                // { roster:[{id,name,cls,spec,status}], assignments:{range,healer,chaser} }
  UpdatedByMemberId  UpdatedAt

LootAward
  Id  RaidEventId (FK)  CharacterId (FK)  ItemName  ItemId (nullable)
  AwardedByMemberId  AwardedAt  Note

AttendanceRecord
  Id  RaidEventId (FK)  CharacterId (FK)  Status (present|late|bench|absent)  Note
```

Optional later: `AuditEntry` (who changed which assignment when) — deferred.

## API surface

**Ported from the Worker (keep paths so `app.js` changes are minimal):**

| Path | Method | Gate | Notes |
|---|---|---|---|
| `/auth/login` | GET | none | 302 → Discord authorize |
| `/auth/callback` | GET | none | exchange code, read guild roles, **upsert Member**, mint token, 302 → `wooback.info/home.html#session=…` |
| `/v4/*` | GET | officer | transparent Raid-Helper proxy (RH_TOKEN attached server-side) |
| `/wcl/reports` | GET | any tier | Warcraft Logs report list, 5-min cache |

**New persistence (`/api/*`, bearer-gated; writes officer-gated):**

| Path | Method | Purpose |
|---|---|---|
| `/api/members` | GET | list members + linked characters |
| `/api/members/{id}/characters` | GET/PUT | manage main + alts links |
| `/api/characters` | POST/PUT/DELETE | CRUD characters |
| `/api/board?event={rhEventId}` | GET/PUT | load / save Vash board snapshot |
| `/api/board/list` | GET | saved layouts |
| `/api/loot?event=…` | GET/POST | loot history; `DELETE /api/loot/{id}` |
| `/api/attendance?event=…` | GET/POST | attendance records |

Auth = ASP.NET middleware that verifies the HMAC token; an `[officer]` policy guards writes and `/v4/*`.

## Config & secrets (move off Worker constants → env / `fly secrets`)

Secrets: `DISCORD_CLIENT_SECRET`, `SESSION_SECRET` (**reuse the current value** so live sessions
stay valid across cutover), `RH_TOKEN`, `WCL_CLIENT_ID`, `WCL_CLIENT_SECRET`, `DATABASE_URL`.

Non-secret config (today hard-coded in the Worker): `DISCORD_CLIENT_ID`, `GUILD_ID`,
`OFFICER_ROLE_IDS`, `HOME_ROLE_ID`, `APP_BASE=https://wooback.info`, allowed CORS origins,
WCL guild identity (`wooback`/`dreamscythe`/`US`), and the new **`API_BASE`** used to build
`redirect_uri = API_BASE + /auth/callback`.

⚠️ **Discord portal:** add the new `<API_BASE>/auth/callback` to OAuth2 → Redirects
(keep the old Worker one until cutover is done).

## Phased migration

Each phase is independently shippable; the Worker keeps running until Phase 4.

- **Phase 0 — Scaffold.** Create `server/` solution: Minimal API, EF Core + Npgsql, `/healthz`,
  Dockerfile, `fly.toml`. Provision Fly app + Postgres. Deploy an empty-but-live API. *No frontend change.*
- **Phase 1 — OAuth in .NET.** Port `/auth/login` + `/auth/callback`; upsert `Member`; mint
  token-compatible session. Add callback to Discord portal. Point `AUTH_BASE` (index.html) at the API.
  Verify login end-to-end. *Worker still proxies.*
- **Phase 2 — Proxies in .NET.** Port `/v4/*` (Raid-Helper) and `/wcl/reports` (with cache).
  Point `RH_PROXY` (app.js) and `WCL_BASE` (logs.html) at the API. **Retire the Worker.**
- **Phase 3 — Persistence: board + identity.** `/api/board` GET/PUT and members/characters.
  Wire `board.html`/`app.js` to auto-load on event select and save on change.
- **Phase 4 — Loot & attendance.** Endpoints + minimal UI (new tab or board panel).
- **Phase 5 — Cleanup.** Delete `raidhelper-proxy.worker.js`, `wrangler.toml`; update `README.md`.

## Frontend changes (total)

Three constants repointed, plus new fetches in the board for save/load:
- `index.html:26` `AUTH_BASE`
- `app.js:515` `RH_PROXY`
- `logs.html:76` `WCL_BASE`

## Risks / notes

- **Cold starts.** Fly can scale machines to zero → first request after idle is slow (proxy calls feel
  laggy). Mitigation: keep 1 machine warm (small cost) or accept the delay.
- **WCL rate limits.** Replicate the Worker's cache (`IMemoryCache`, 5-min TTL) or the guild will hit the
  hourly points budget on refresh bursts.
- **Bearer token in `localStorage`** stays (per decision) — XSS-readable; acceptable for this tool. HttpOnly
  cookies would need same-origin hosting, which we chose against.
- **DB backups.** Enable Fly Postgres snapshots (or use Supabase's managed backups).
- **Secret parity.** Reusing `SESSION_SECRET` avoids logging everyone out at cutover.

## Open questions before scaffolding

1. **Fly.io or Render?** (Plan assumes Fly; Render is equivalent — I'll match whichever.)
2. **Monorepo layout** — put the backend in `server/` inside this repo, or a separate repo? (Plan assumes `server/`.)
3. **Board save UX** — auto-save on every drag, or an explicit "Save layout" button? (Plan assumes auto-save, debounced.)
