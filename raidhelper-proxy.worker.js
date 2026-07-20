/*
 * Raid-Helper proxy + Discord officer gate — Cloudflare Worker
 * ------------------------------------------------------------
 * Two jobs in one Worker:
 *
 *  1. Discord OAuth login (/auth/login, /auth/callback). A raider signs in with
 *     Discord; the Worker reads their guild roles and grants access in two tiers:
 *     anyone holding HOME_ROLE_ID reaches the home page, while OFFICER_ROLE_IDS
 *     additionally unlock the Vash board + the Raid-Helper proxy. On success it
 *     mints a short-lived HMAC-signed session token (with an `officer` flag) which
 *     the pages store. No bot is needed — the `guilds.members.read` scope lets us
 *     read the signed-in user's own roles in the guild.
 *
 *  2. Raid-Helper CORS proxy (everything else). raid-helper.xyz blocks cross-origin
 *     browser requests, so the board routes API calls through here. Every proxied
 *     call now REQUIRES a valid officer session token (Authorization: Bearer …);
 *     without one the Worker returns 401 and never touches Raid-Helper. The
 *     Raid-Helper API token itself stays a Worker secret (RH_TOKEN), attached
 *     server-side.
 *
 * ── Secrets to set on the Worker (never commit these) ──────────────────────────
 *   RH_TOKEN               Raid-Helper API token (existing).
 *   DISCORD_CLIENT_SECRET  Discord app → OAuth2 → Client Secret.
 *   SESSION_SECRET         Any long random string; signs session tokens.
 *     npx wrangler secret put RH_TOKEN
 *     npx wrangler secret put DISCORD_CLIENT_SECRET
 *     npx wrangler secret put SESSION_SECRET
 *   (or Dashboard → the Worker → Settings → Variables and Secrets → Add → Secret.)
 *
 * ── Config below you must fill in / verify ─────────────────────────────────────
 *   DISCORD_CLIENT_ID  Discord app → General Information → Application ID.
 *   WORKER_BASE        This Worker's own URL (for the OAuth redirect_uri).
 *   APP_BASE           Where the static site is served (GitHub Pages).
 *
 * In the Discord Developer Portal (https://discord.com/developers/applications):
 *   OAuth2 → Redirects → add exactly:  <WORKER_BASE>/auth/callback
 */

const RAID_HELPER    = 'https://raid-helper.xyz/api';
// Origins allowed to call the proxy with fetch/XHR (the app pages). Origin only.
// The site is served from the custom domain (apex + www); the github.io URL is
// kept so a call that fires before GitHub's redirect still works.
const ALLOWED_ORIGINS = new Set([
  'https://wooback.info',
  'https://www.wooback.info',
  'https://cowdunstan.github.io'
]);
// Echo the caller's Origin back only if it's allow-listed — CORS permits a
// single origin, not a list. Returns null for a disallowed/absent origin.
function allowedOrigin(request) {
  const o = request.headers.get('Origin');
  return o && ALLOWED_ORIGINS.has(o) ? o : null;
}

/* ─────────────────────── Warcraft Logs (v2 API) ───────────────────────
 * The logs.html app pulls the guild's report list through here so the API
 * credentials stay server-side. Unlike the Raid-Helper proxy, this needs only
 * a valid (home-tier) session — Warcraft Logs reports are public information.
 *
 * Credentials come from a Warcraft Logs "v2 API client" (Client Credentials
 * flow). Create one at https://www.warcraftlogs.com/api/clients/ and set them
 * as Worker secrets:
 *     npx wrangler secret put WCL_CLIENT_ID
 *     npx wrangler secret put WCL_CLIENT_SECRET
 *
 * Then fill in the guild's Warcraft Logs identity below. You can read all three
 * off the guild's page URL: warcraftlogs.com/guild/<region>/<server>/<name>. */
const WCL_OAUTH = 'https://www.warcraftlogs.com/oauth/token';  // shared across game versions
// Warcraft Logs partitions data by game version onto separate hosts, each with its
// own GraphQL endpoint and report URLs (www = Retail, classic, fresh, sod, …). The
// wooback guild lives on the Fresh (Classic Anniversary) realm Dreamscythe, so we
// talk to fresh.warcraftlogs.com — a Fresh guild is not visible on the www API.
const WCL_HOST  = 'https://fresh.warcraftlogs.com';
const WCL_GQL   = WCL_HOST + '/api/v2/client';
// The guild's Warcraft Logs identity, read from its page URL
// (fresh.warcraftlogs.com/guild/<region>/<server>/<name>).
const WCL_GUILD_NAME   = 'wooback';
const WCL_GUILD_SERVER = 'dreamscythe';   // realm slug as it appears in the URL
const WCL_GUILD_REGION = 'US';            // 'US' | 'EU' | ...

const DISCORD_API       = 'https://discord.com/api';
const DISCORD_CLIENT_ID = '1528486803751829554';
const GUILD_ID          = '1462481995119722649';
// Any one of these Discord roles grants officer access (the Vash board + the
// Raid-Helper proxy).
const OFFICER_ROLE_IDS  = [
  '1493701583668514836',
  '1470117612896649357',
  '1470862434720940304'
];
// Broader access to the home page (any member with this role). Officers
// implicitly have it too.
const HOME_ROLE_ID       = '1474961634186236118';

// This Worker's public URL. The OAuth redirect_uri is WORKER_BASE + '/auth/callback'
// and must be registered verbatim in the Discord Developer Portal.
const WORKER_BASE  = 'https://wooback-vash.cowdunstan.workers.dev';
const REDIRECT_URI = WORKER_BASE + '/auth/callback';

// The static site. Landing page = <APP_BASE>/  (index.html), board = board.html.
// Served from the custom domain wooback.info (root). GitHub Pages redirects the
// old cowdunstan.github.io/wooback-vash URL here, so this is the canonical home.
const APP_BASE = 'https://wooback.info';

// Session lifetime, seconds.
const SESSION_TTL = 6 * 60 * 60;

/* ───────────────────────────── CORS ─────────────────────────────
 * corsHeaders() carries everything except Access-Control-Allow-Origin. The
 * allowed origin is per-request (see allowedOrigin) and gets stamped on at the
 * router via withCors, which keeps cached responses origin-agnostic. */
function corsHeaders() {
  return {
    'Access-Control-Allow-Methods': 'GET, OPTIONS',
    'Access-Control-Allow-Headers': 'Authorization, Content-Type',
    'Access-Control-Max-Age': '86400',
    'Vary': 'Origin'
  };
}
// Stamp the resolved Allow-Origin onto an outgoing response. Rebuilds the
// response so it works even when the headers are immutable (e.g. a cache hit).
function withCors(response, origin) {
  if (!origin) return response;
  const r = new Response(response.body, response);
  r.headers.set('Access-Control-Allow-Origin', origin);
  return r;
}
function jsonResponse(status, obj) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { ...corsHeaders(), 'Content-Type': 'application/json' }
  });
}

/* ─────────────────── base64url + HMAC session tokens ─────────────────── */
function b64urlEncode(bytes) {
  let bin = '';
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
function b64urlDecode(str) {
  str = str.replace(/-/g, '+').replace(/_/g, '/');
  while (str.length % 4) str += '=';
  const bin = atob(str);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
async function hmacKey(secret) {
  return crypto.subtle.importKey(
    'raw', new TextEncoder().encode(secret),
    { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify']
  );
}
// token = base64url(payloadJSON) + '.' + base64url(HMAC(payloadJSON))
async function signSession(payload, secret) {
  const body = b64urlEncode(new TextEncoder().encode(JSON.stringify(payload)));
  const key = await hmacKey(secret);
  const sig = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(body));
  return body + '.' + b64urlEncode(new Uint8Array(sig));
}
async function verifySession(token, secret) {
  if (!token || !secret || token.indexOf('.') === -1) return null;
  const [body, sig] = token.split('.');
  if (!body || !sig) return null;
  const key = await hmacKey(secret);
  let ok;
  try {
    ok = await crypto.subtle.verify('HMAC', key, b64urlDecode(sig), new TextEncoder().encode(body));
  } catch (e) { return null; }
  if (!ok) return null;
  let payload;
  try { payload = JSON.parse(new TextDecoder().decode(b64urlDecode(body))); }
  catch (e) { return null; }
  if (!payload.exp || payload.exp < Math.floor(Date.now() / 1000)) return null;
  return payload;
}

/* ─────────────────────────── cookies ─────────────────────────── */
function parseCookies(header) {
  const out = {};
  (header || '').split(';').forEach(part => {
    const i = part.indexOf('=');
    if (i === -1) return;
    out[part.slice(0, i).trim()] = decodeURIComponent(part.slice(i + 1).trim());
  });
  return out;
}
const STATE_COOKIE = 'rh_oauth_state';
function setStateCookie(state) {
  return `${STATE_COOKIE}=${state}; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=600`;
}
const CLEAR_STATE_COOKIE = `${STATE_COOKIE}=; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=0`;

// Redirect the browser back to the static site, passing a result in the fragment
// (fragments never hit a server, so a session token stays out of request logs).
function redirectToApp(path, key, value, clearCookie) {
  const headers = { 'Location': `${APP_BASE}${path}#${key}=${encodeURIComponent(value)}` };
  if (clearCookie) headers['Set-Cookie'] = clearCookie;
  return new Response(null, { status: 302, headers });
}

/* ─────────────────────────── OAuth ─────────────────────────── */
function handleLogin() {
  const state = b64urlEncode(crypto.getRandomValues(new Uint8Array(16)));
  const auth = new URL(DISCORD_API + '/oauth2/authorize');
  auth.searchParams.set('client_id', DISCORD_CLIENT_ID);
  auth.searchParams.set('redirect_uri', REDIRECT_URI);
  auth.searchParams.set('response_type', 'code');
  auth.searchParams.set('scope', 'identify guilds.members.read');
  auth.searchParams.set('state', state);
  return new Response(null, {
    status: 302,
    headers: { 'Location': auth.toString(), 'Set-Cookie': setStateCookie(state) }
  });
}

async function handleCallback(request, url, env) {
  const params = url.searchParams;
  const cookies = parseCookies(request.headers.get('Cookie'));
  const savedState = cookies[STATE_COOKIE];

  if (params.get('error')) {
    return redirectToApp('/', 'error', params.get('error'), CLEAR_STATE_COOKIE);
  }
  const code = params.get('code');
  const state = params.get('state');
  if (!code || !state || !savedState || state !== savedState) {
    return redirectToApp('/', 'error', 'state_mismatch', CLEAR_STATE_COOKIE);
  }
  if (!env.DISCORD_CLIENT_SECRET || !env.SESSION_SECRET) {
    return redirectToApp('/', 'error', 'worker_misconfigured', CLEAR_STATE_COOKIE);
  }

  // Exchange the code for a user access token.
  let tok;
  try {
    const r = await fetch(DISCORD_API + '/oauth2/token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        client_id: DISCORD_CLIENT_ID,
        client_secret: env.DISCORD_CLIENT_SECRET,
        grant_type: 'authorization_code',
        code,
        redirect_uri: REDIRECT_URI
      })
    });
    if (!r.ok) return redirectToApp('/', 'error', 'token_http_' + r.status, CLEAR_STATE_COOKIE);
    tok = await r.json();
  } catch (e) {
    return redirectToApp('/', 'error', 'token_exchange_failed', CLEAR_STATE_COOKIE);
  }

  // Read the signed-in user's membership (incl. roles) in the guild.
  let member;
  try {
    const r = await fetch(DISCORD_API + `/users/@me/guilds/${GUILD_ID}/member`, {
      headers: { 'Authorization': `Bearer ${tok.access_token}` }
    });
    if (r.status === 404) return redirectToApp('/', 'denied', 'not_in_server', CLEAR_STATE_COOKIE);
    if (!r.ok) return redirectToApp('/', 'error', 'member_http_' + r.status, CLEAR_STATE_COOKIE);
    member = await r.json();
  } catch (e) {
    return redirectToApp('/', 'error', 'member_fetch_failed', CLEAR_STATE_COOKIE);
  }

  const roles = member.roles || [];
  const isOfficer = roles.some(r => OFFICER_ROLE_IDS.includes(r));
  const hasHome   = isOfficer || roles.includes(HOME_ROLE_ID);
  if (!hasHome) {
    return redirectToApp('/', 'denied', 'no_access', CLEAR_STATE_COOKIE);
  }

  const user = member.user || {};
  const session = await signSession({
    uid: user.id,
    name: user.global_name || user.username || 'Raider',
    officer: isOfficer,
    exp: Math.floor(Date.now() / 1000) + SESSION_TTL
  }, env.SESSION_SECRET);

  return redirectToApp('/home.html', 'session', session, CLEAR_STATE_COOKIE);
}

/* ─────────────────── Warcraft Logs reports (gated) ─────────────────── */
// Best-effort token cache. Workers may reuse an isolate across requests, so a
// module-level cache saves a token round-trip; it's fine if it doesn't persist.
let wclTokenCache = { token: null, exp: 0 };
async function getWclToken(env) {
  const now = Math.floor(Date.now() / 1000);
  if (wclTokenCache.token && wclTokenCache.exp - 60 > now) return wclTokenCache.token;
  if (!env || !env.WCL_CLIENT_ID || !env.WCL_CLIENT_SECRET) return null;
  let r;
  try {
    r = await fetch(WCL_OAUTH, {
      method: 'POST',
      headers: {
        'Authorization': 'Basic ' + btoa(`${env.WCL_CLIENT_ID}:${env.WCL_CLIENT_SECRET}`),
        'Content-Type': 'application/x-www-form-urlencoded'
      },
      body: 'grant_type=client_credentials'
    });
  } catch (e) { return null; }
  if (!r.ok) return null;
  const j = await r.json();
  if (!j.access_token) return null;
  wclTokenCache = { token: j.access_token, exp: now + (j.expires_in || 3600) };
  return j.access_token;
}

// The report list changes rarely, so we cache the assembled result at the edge.
// This keeps us well under the Warcraft Logs v2 API's hourly points budget — a
// burst of refreshes reuses one cached copy instead of re-paging the API (which
// is what earns a 429). Public data, so caching + reuse is safe.
const WCL_CACHE_TTL = 300; // seconds
function wclCacheKey() {
  const id = encodeURIComponent(`${WCL_HOST}|${WCL_GUILD_REGION}|${WCL_GUILD_SERVER}|${WCL_GUILD_NAME}`);
  return new Request('https://wcl-reports.cache/' + id, { method: 'GET' });
}

// Pull every report for the guild (paged, newest first) and return a slim list.
async function handleWclReports(request, url, env, ctx) {
  if (request.method !== 'GET') {
    return new Response('Method not allowed', { status: 405, headers: corsHeaders() });
  }
  // Any valid session is enough — logs are public, so home-tier members get in.
  const auth = request.headers.get('Authorization') || '';
  const m = auth.match(/^Bearer\s+(.+)$/i);
  const session = m ? await verifySession(m[1], env.SESSION_SECRET) : null;
  if (!session) {
    return jsonResponse(401, { error: 'unauthorized', detail: 'Sign-in required.' });
  }

  if (!WCL_GUILD_SERVER || !WCL_GUILD_REGION) {
    return jsonResponse(501, { error: 'not_configured', detail: 'Warcraft Logs guild is not set on the Worker yet.' });
  }

  // Serve a fresh cached copy if we have one (the gate above already ran).
  const cache = caches.default;
  const cacheKey = wclCacheKey();
  const hit = await cache.match(cacheKey);
  if (hit) return hit;

  const token = await getWclToken(env);
  if (!token) {
    return jsonResponse(501, { error: 'not_configured', detail: 'Warcraft Logs API credentials are not set on the Worker yet.' });
  }

  const query = `query($name:String!,$server:String!,$region:String!,$page:Int!){` +
    `reportData{reports(guildName:$name,guildServerSlug:$server,guildServerRegion:$region,limit:100,page:$page){` +
    `has_more_pages data{code title startTime endTime zone{name} owner{name}}}}}`;

  const reports = [];
  let page = 1;
  try {
    // Cap the walk so a huge history can't run us into subrequest limits.
    while (page <= 15) {
      const r = await fetch(WCL_GQL, {
        method: 'POST',
        headers: { 'Authorization': `Bearer ${token}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ query, variables: {
          name: WCL_GUILD_NAME, server: WCL_GUILD_SERVER, region: WCL_GUILD_REGION, page
        } })
      });
      // Rate limited — the WCL v2 hourly points budget is spent. Serve a stale
      // cached copy if one exists, otherwise ask the user to wait it out.
      if (r.status === 429) {
        const stale = await cache.match(cacheKey);
        if (stale) return stale;
        return jsonResponse(429, {
          error: 'rate_limited',
          detail: 'Warcraft Logs is rate-limiting the guild tools right now. Try again in a minute.'
        });
      }
      if (!r.ok) return jsonResponse(502, { error: 'upstream', detail: 'Warcraft Logs API returned ' + r.status });
      const j = await r.json();
      const node = j && j.data && j.data.reportData && j.data.reportData.reports;
      if (!node) {
        const gqlErr = j && j.errors && j.errors[0] && j.errors[0].message;
        return jsonResponse(502, { error: 'upstream', detail: gqlErr || 'Unexpected Warcraft Logs response.' });
      }
      for (const rep of (node.data || [])) {
        reports.push({
          code: rep.code,
          title: rep.title,
          startTime: rep.startTime,
          endTime: rep.endTime,
          zone: (rep.zone && rep.zone.name) || '',
          owner: (rep.owner && rep.owner.name) || '',
          url: WCL_HOST + '/reports/' + rep.code
        });
      }
      if (!node.has_more_pages) break;
      page++;
    }
  } catch (err) {
    return jsonResponse(502, { error: 'upstream fetch failed', detail: String(err) });
  }

  const guildUrl = `${WCL_HOST}/guild/${WCL_GUILD_REGION.toLowerCase()}/` +
    `${WCL_GUILD_SERVER}/${encodeURIComponent(WCL_GUILD_NAME)}`;
  const body = JSON.stringify({ guild: WCL_GUILD_NAME, guildUrl, reports });
  const headers = { ...corsHeaders(), 'Content-Type': 'application/json', 'Cache-Control': `max-age=${WCL_CACHE_TTL}` };
  const response = new Response(body, { status: 200, headers });
  // Store a copy at the edge; waitUntil keeps it off the response's critical path.
  const put = cache.put(cacheKey, response.clone());
  if (ctx && ctx.waitUntil) ctx.waitUntil(put); else await put;
  return response;
}

/* ─────────────────── Raid-Helper proxy (gated) ─────────────────── */
async function handleProxy(request, url, env) {
  if (request.method !== 'GET') {
    return new Response('Method not allowed', { status: 405, headers: corsHeaders() });
  }

  // Require a valid officer session before doing anything. A plain (home-tier)
  // session is not enough — Raid-Helper stays officer-only.
  const auth = request.headers.get('Authorization') || '';
  const m = auth.match(/^Bearer\s+(.+)$/i);
  const session = m ? await verifySession(m[1], env.SESSION_SECRET) : null;
  if (!session) {
    return jsonResponse(401, { error: 'unauthorized', detail: 'Sign-in required.' });
  }
  if (!session.officer) {
    return jsonResponse(403, { error: 'forbidden', detail: 'Officer access required.' });
  }

  const target = RAID_HELPER + url.pathname + url.search;
  const fwd = {};
  if (env && env.RH_TOKEN) fwd['Authorization'] = env.RH_TOKEN;

  let upstream;
  try {
    upstream = await fetch(target, { method: 'GET', headers: fwd });
  } catch (err) {
    return jsonResponse(502, { error: 'upstream fetch failed', detail: String(err) });
  }
  const body = await upstream.text();
  return new Response(body, {
    status: upstream.status,
    headers: {
      ...corsHeaders(),
      'Content-Type': upstream.headers.get('Content-Type') || 'application/json'
    }
  });
}

/* ─────────────────────────── router ─────────────────────────── */
export default {
  async fetch(request, env, ctx) {
    const origin = allowedOrigin(request);

    if (request.method === 'OPTIONS') {
      const headers = corsHeaders();
      if (origin) headers['Access-Control-Allow-Origin'] = origin;
      return new Response(null, { status: 204, headers });
    }

    const url = new URL(request.url);
    let response;
    if (url.pathname === '/auth/login')         response = handleLogin();
    else if (url.pathname === '/auth/callback') response = await handleCallback(request, url, env);
    else if (url.pathname === '/wcl/reports')   response = await handleWclReports(request, url, env, ctx);
    else                                        response = await handleProxy(request, url, env);

    return withCors(response, origin);
  }
};
