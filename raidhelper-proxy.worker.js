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
// Origin allowed to call the proxy with fetch/XHR (the board page). Origin only.
const ALLOWED_ORIGIN = 'https://cowdunstan.github.io';

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
// VERIFY this matches how your GitHub Pages site is actually served (a project
// page lives under /<repo>/, a user/org page at the domain root).
const APP_BASE = 'https://cowdunstan.github.io/wooback-vash';

// Session lifetime, seconds.
const SESSION_TTL = 6 * 60 * 60;

/* ───────────────────────────── CORS ───────────────────────────── */
function corsHeaders() {
  return {
    'Access-Control-Allow-Origin': ALLOWED_ORIGIN,
    'Access-Control-Allow-Methods': 'GET, OPTIONS',
    'Access-Control-Allow-Headers': 'Authorization, Content-Type',
    'Access-Control-Max-Age': '86400',
    'Vary': 'Origin'
  };
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
  async fetch(request, env) {
    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: corsHeaders() });
    }

    const url = new URL(request.url);
    if (url.pathname === '/auth/login')    return handleLogin();
    if (url.pathname === '/auth/callback') return handleCallback(request, url, env);

    return handleProxy(request, url, env);
  }
};
