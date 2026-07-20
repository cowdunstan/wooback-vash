# wooback-vash

Guild tools for the wooback Discord server, gated behind Discord access in **two
tiers**: a **home** hub open to any member with the home role, and the Lady Vashj
(Phase 2) raid-assignment **board** reserved for **officers**.

- **`index.html`** — public landing page. "Sign in with Discord" only.
- **`home.html`** — the default page after sign-in: a welcome hub with a hamburger
  menu (Home / Warcraft Logs / Vash assignments) and app cards. Open to any
  signed-in tier.
- **`logs.html`** — the **Warcraft Logs** app: the full list of the guild's
  uploaded reports (newest first), each linked straight to Warcraft Logs. Open to
  any signed-in tier (logs are public).
- **`board.html`** — the assignment board. Only reachable with a valid **officer**
  session; `app.js` + `styles.css` power it.
- **`sheet.html`** — a read-only view of the guild's loot / BIS-priority sheet,
  open to **any signed-in tier**; it embeds the sheet's `/preview` URL in an
  `<iframe>` (see `SHEET_EMBED_URL` in the file), so Google's own formatting,
  colors, merged banners, and tabs are preserved. It reads the live sheet via its
  "anyone with the link" share setting, so the sign-in gate here is for the app's
  flow, not a data barrier.
- **`menu.js`** — shared session helpers + hamburger menu, used by every page.
- **`raidhelper-proxy.worker.js`** — Cloudflare Worker doing three jobs:
  1. Discord OAuth (`/auth/login`, `/auth/callback`) — verifies the signed-in
     user's guild roles and issues a short-lived HMAC-signed session token whose
     `officer` flag records their tier.
  2. Raid-Helper CORS proxy — every proxied call requires an **officer** session
     token, so roster data is never returned to non-officers.
  3. Warcraft Logs proxy (`/wcl/reports`) — pulls the guild's report list from the
     Warcraft Logs v2 API with server-side credentials. Requires any valid session
     (home tier is enough, since logs are public).

## How the gate works

1. Landing page sends the user to `<worker>/auth/login`.
2. The Worker redirects to Discord (`identify guilds.members.read` scope — no bot
   needed), then Discord calls back to `<worker>/auth/callback`.
3. The Worker reads the user's roles in the guild via
   `/users/@me/guilds/{guild}/member`:
   - holds an `OFFICER_ROLE_IDS` role → officer session (`officer: true`);
   - holds `HOME_ROLE_ID` (or is an officer) → home session (`officer: false`);
   - neither → redirected back to the landing page with a "no access" message.
   On success it mints a signed session and redirects to `home.html#session=…`.
4. The pages store the session; `board.html` attaches it to every Raid-Helper
   request. The Worker rejects Raid-Helper calls with no session (`401`) or a
   non-officer session (`403`).

The client-side checks (home requires a valid session; the board additionally
requires the `officer` flag and hides the Vash menu item / card otherwise) only
decide what to *show*; the real enforcement is the Worker refusing data without a
valid officer signature.

## One-time setup

### 1. Create the Discord application
- https://discord.com/developers/applications → **New Application**.
- **General Information** → copy the **Application ID** → put it in
  `raidhelper-proxy.worker.js` as `DISCORD_CLIENT_ID`.
- **OAuth2** → **Redirects** → add exactly:
  `https://wooback-vash.cowdunstan.workers.dev/auth/callback`
  (must match `WORKER_BASE` + `/auth/callback`).
- **OAuth2** → copy the **Client Secret** (used as a Worker secret below).

### 2. Verify the config constants in `raidhelper-proxy.worker.js`
- `DISCORD_CLIENT_ID` — from step 1.
- `GUILD_ID` — `1462481995119722649` (same as the Raid-Helper server ID).
- `OFFICER_ROLE_IDS` — the three officer role IDs (already filled in).
- `HOME_ROLE_ID` — the broader role that unlocks the home page (already filled in).
- `WORKER_BASE` — this Worker's public URL.
- `APP_BASE` — where GitHub Pages serves the site (currently
  `https://cowdunstan.github.io/wooback-vash`). **Confirm this matches your Pages
  URL** — a project page lives under `/<repo>/`.

### 3. Set the Worker secrets (never commit these)
```
npx wrangler secret put RH_TOKEN               # Raid-Helper API token (existing)
npx wrangler secret put DISCORD_CLIENT_SECRET  # from the Discord app
npx wrangler secret put SESSION_SECRET         # any long random string
npx wrangler secret put WCL_CLIENT_ID          # Warcraft Logs v2 API client id
npx wrangler secret put WCL_CLIENT_SECRET      # Warcraft Logs v2 API client secret
```
(Or Dashboard → the Worker → Settings → Variables and Secrets → Add → Secret.)

### 4. Connect the loot sheet
- No "Publish to web" needed. `SHEET_EMBED_URL` in `sheet.html` is just the
  sheet's own link with `/edit?usp=sharing` replaced by `/preview`. To point it
  at a different sheet, swap the id.
- The sheet's **General access must be "Anyone with the link → Viewer"** so
  members see it without a Google login. (If you set it to specific accounts
  instead, the frame still works but each viewer must be signed into an
  authorized Google account.)
- Note: "anyone with the link" means the sheet's *contents* are reachable by
  anyone who has that link. Fine for a BIS/loot guide, but don't put anything you
  wouldn't want outside the guild on a tab of this sheet.

### 5. Set up the Warcraft Logs app
The **Warcraft Logs** app (`logs.html`) pulls the guild's report list through the
Worker's `/wcl/reports` route. The guild identity is already set in
`raidhelper-proxy.worker.js` — wooback on the **Fresh** (Classic Anniversary) realm
**Dreamscythe (US)**, so the route targets `fresh.warcraftlogs.com` (a Fresh guild
is not visible on the retail `www` API). The `WCL_HOST` / `WCL_GUILD_*` constants
at the top of the file capture this; change them if the guild ever moves.

All that's left is the API credentials:
1. Create a v2 API client at https://www.warcraftlogs.com/api/clients/ (any name;
   the redirect URL is unused for the Client Credentials flow).
2. Copy the **Client ID** and **Client Secret** into the `WCL_CLIENT_ID` and
   `WCL_CLIENT_SECRET` secrets above.

Until those two secrets are set, the page shows a "not set on the Worker yet"
message instead of logs. (The OAuth token endpoint stays on `www.warcraftlogs.com`
— it's shared across all game versions.)

### 6. Deploy
```
npx wrangler deploy
```

## Local note
`ALLOWED_ORIGIN` in the Worker locks the Raid-Helper proxy to the GitHub Pages
origin, so the board's API calls only work from the deployed site (not from a
`localhost` preview).
