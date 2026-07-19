/*
 * Raid-Helper CORS proxy — Cloudflare Worker
 * ------------------------------------------
 * The Vashj board (index.html) runs in the browser, but raid-helper.dev only
 * allows cross-origin requests from its own site, so a hosted page (e.g. GitHub
 * Pages) is blocked by CORS. This Worker calls Raid-Helper server-side (where
 * CORS does not apply) and re-sends the response with a header the browser
 * accepts. It also forwards your Authorization header, so your API key only ever
 * travels across infrastructure you control — never a third-party proxy.
 *
 * Deploy (free tier is plenty):
 *   1. https://dash.cloudflare.com  ->  Workers & Pages  ->  Create  ->  Worker
 *   2. Replace the generated code with this file, then Deploy.
 *   3. Set ALLOWED_ORIGIN below to the exact origin the page is served from
 *      (scheme + host, no trailing slash), e.g. https://cowdunstan.github.io
 *   4. Copy the Worker URL (…​.workers.dev) into the "CORS proxy base URL" field.
 *
 * Alternatively deploy with Wrangler:  npx wrangler deploy raidhelper-proxy.worker.js
 */

const RAID_HELPER    = 'https://raid-helper.dev/api';
// Lock this to your page's origin so the Worker can't be reused as an open proxy.
// Use '*' only for quick local testing — never with a real API key.
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
  async fetch(request) {
    // Preflight
    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: corsHeaders() });
    }
    if (request.method !== 'GET') {
      return new Response('Method not allowed', { status: 405, headers: corsHeaders() });
    }

    const url = new URL(request.url);
    // Forward the path as-is: /v2/events/123  ->  https://raid-helper.dev/api/v2/events/123
    const target = RAID_HELPER + url.pathname + url.search;

    const fwd = {};
    const auth = request.headers.get('Authorization');
    if (auth) fwd['Authorization'] = auth;

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
