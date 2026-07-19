/*
 * Raid-Helper CORS proxy — Cloudflare Worker
 * ------------------------------------------
 * The Vashj board (index.html) runs in the browser, but raid-helper.xyz only
 * allows cross-origin requests from its own site, so a hosted page (e.g. GitHub
 * Pages) is blocked by CORS. This Worker calls Raid-Helper server-side (where
 * CORS does not apply) and re-sends the response with a header the browser accepts.
 *
 * The Raid-Helper API token is stored as a Worker SECRET (RH_TOKEN), never in the
 * page or the repo, so the site itself needs no token — the Worker attaches the
 * Authorization header here. It stays GET-only, so at worst someone who discovers
 * this URL can read the server's event data (a Raid-Helper token is scoped to one
 * server); it grants no write or account access.
 *
 * Deploy (free tier is plenty):
 *   1. https://dash.cloudflare.com  ->  Workers & Pages  ->  Create  ->  Worker
 *   2. Replace the generated code with this file, then Deploy.
 *   3. Set ALLOWED_ORIGIN below to the exact origin the page is served from
 *      (scheme + host, no trailing slash), e.g. https://cowdunstan.github.io
 *   4. Add the token as a secret named RH_TOKEN:
 *        - Dashboard: the Worker -> Settings -> Variables and Secrets ->
 *          Add -> type "Secret" -> name RH_TOKEN -> paste the token -> Save.
 *        - or Wrangler:  npx wrangler secret put RH_TOKEN   (paste when prompted)
 *
 * Alternatively deploy with Wrangler:  npx wrangler deploy raidhelper-proxy.worker.js
 */

const RAID_HELPER    = 'https://raid-helper.xyz/api';
// Lock this to your page's origin so the Worker can't be reused as an open proxy.
// Use '*' only for quick local testing — never in production.
const ALLOWED_ORIGIN = 'https://cowdunstan.github.io';

function corsHeaders() {
  return {
    'Access-Control-Allow-Origin': ALLOWED_ORIGIN,
    'Access-Control-Allow-Methods': 'GET, OPTIONS',
    'Access-Control-Allow-Headers': 'Authorization, Content-Type',
    'Access-Control-Max-Age': '86400',
    'Vary': 'Origin'
  };
}

export default {
  async fetch(request, env) {
    // Preflight
    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: corsHeaders() });
    }
    if (request.method !== 'GET') {
      return new Response('Method not allowed', { status: 405, headers: corsHeaders() });
    }

    const url = new URL(request.url);
    // Forward the path as-is: /v4/events/123  ->  https://raid-helper.xyz/api/v4/events/123
    const target = RAID_HELPER + url.pathname + url.search;

    const fwd = {};
    // Prefer the token configured on the Worker; fall back to a caller-supplied
    // header so the proxy still works if the secret hasn't been set yet.
    const token = (env && env.RH_TOKEN) || request.headers.get('Authorization');
    if (token) fwd['Authorization'] = token;

    let upstream;
    try {
      upstream = await fetch(target, { method: 'GET', headers: fwd });
    } catch (err) {
      return new Response(JSON.stringify({ error: 'upstream fetch failed', detail: String(err) }),
        { status: 502, headers: { ...corsHeaders(), 'Content-Type': 'application/json' } });
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
};
