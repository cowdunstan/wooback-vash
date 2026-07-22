/* ───────────────────────── Shared nav + session ─────────────────────────
   Loaded by home.html and board.html. Provides the session helpers used to
   authorize Raid-Helper calls and to drive the shared hamburger menu.

   The session token is `base64url(payloadJSON).base64url(HMAC)`. Only the
   Worker verifies the signature (on every API call); here we just decode the
   payload to read `name`, `officer`, and `exp` for UX decisions. */

function sessionToken(){
  try{ return localStorage.getItem('vashj_session') || ''; }catch(e){ return ''; }
}
function sessionPayload(){
  const t = sessionToken();
  if(!t) return null;
  try{
    let p = t.split('.')[0].replace(/-/g,'+').replace(/_/g,'/');
    while(p.length % 4) p += '=';
    return JSON.parse(atob(p));
  }catch(e){ return null; }
}
function validSession(tok){
  if(tok === undefined) tok = sessionToken();
  if(!tok) return false;
  try{
    let p = tok.split('.')[0].replace(/-/g,'+').replace(/_/g,'/');
    while(p.length % 4) p += '=';
    const payload = JSON.parse(atob(p));
    return payload.exp && payload.exp > Math.floor(Date.now()/1000);
  }catch(e){ return false; }
}
function sessionName(){
  const p = sessionPayload();
  return p && p.name ? p.name : '';
}
function isOfficer(){
  const p = sessionPayload();
  return !!(p && p.officer);
}
function logout(){
  try{ localStorage.removeItem('vashj_session'); }catch(e){}
  location.replace('index.html');
}

/* Sliding renewal. A token expires on a fixed window, so without this an active
   raider gets bounced back to Discord the moment `exp` passes. Once a session is
   past the halfway point of its own window we quietly trade it for a fresh one
   on page load. The API caps how long a session can be renewed for; a failure
   here is silent — the existing token still works until it expires. */
function refreshSessionIfStale(){
  const p = sessionPayload();
  if(!p || !p.exp) return;
  const now = Math.floor(Date.now() / 1000);
  if(p.exp <= now) return;
  // Halfway between issue and expiry. Tokens minted before `iat` existed have no
  // issue time, so fall back to "renew inside the last day".
  const halfway = p.iat ? p.iat + (p.exp - p.iat) / 2 : p.exp - 86400;
  if(now < halfway) return;

  fetch(API_BASE + '/auth/refresh', {
    method: 'POST',
    headers: { 'Authorization': 'Bearer ' + sessionToken() }
  }).then(function(res){
    return res.ok ? res.json() : null;
  }).then(function(data){
    if(data && data.session){
      try{ localStorage.setItem('vashj_session', data.session); }catch(e){}
    }
  }).catch(function(){});
}

/* ───────────────────────── Shared API base ─────────────────────────
   Every page talks to the same .NET backend (server/WoobackVash.Api): Discord
   OAuth, board save/load, loot, attendance, and the Warcraft-Logs / Raid-Helper
   proxies all live behind this host. Localhost points at the dev server on 8080;
   everything else at the deployed Fly app. Defined here so no page redefines it. */
const API_BASE = (location.hostname === 'localhost' || location.hostname === '127.0.0.1')
  ? 'http://localhost:8080'
  : 'https://wooback-vash-api.fly.dev';

/* ───────────────────────── Shared item links ─────────────────────────
   Every item name on the site points at item.html — who has it equipped, how often
   it has dropped, and every roll on it. Wowhead still supplies the hover tooltip:
   the widget keys off `data-wowhead`, not the href, so an internal link tooltips
   exactly like an external one.

   WOWHEAD_DOMAIN is the expansion the Anniversary realms are on: 'classic'
   (vanilla), 'tbc', 'wotlk'. One line to flip when the guild progresses. It has
   to ride along in `data-wowhead` too: without it the widget reads the link as
   retail — retail tooltip data, and a retail href once it rewrites the link. */
const WOWHEAD_DOMAIN = 'tbc';
var whTooltips = { colorLinks:true, iconizeLinks:true, renameLinks:true };

function whEsc(s){ return String(s == null ? '' : s).replace(/[&<>"]/g, function(c){
  return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]; }); }

/* An item's page on this site. Awards typed by hand carry no id, so those fall
   back to the name — the API resolves either. */
function itemHref(id, name){
  return id ? 'item.html?id=' + encodeURIComponent(id)
            : 'item.html?name=' + encodeURIComponent(name || '');
}

/* An item link. `html` is already-escaped markup when given (the loot history
   passes its search highlight through), otherwise `name` is escaped here. `extra`
   is the gear list's `&ench=…&gems=…` suffix, which rides along on the tooltip. */
function itemLink(id, name, extra, html){
  const inner = html != null ? html : whEsc(name || (id ? 'Item ' + id : ''));
  const tip = id ? ' data-wowhead="' + whEsc(wowheadTip(id, extra)) + '"' : '';
  if(!id && !name) return inner;
  return '<a href="' + whEsc(itemHref(id, name)) + '"' + tip + ' class="item-link">' + inner + '</a>';
}

/* Paperdoll slot order and labels, as the gear snapshots key them. The character
   sheet reads the whole list (it lays gear out in paperdoll order); the item page
   only needs one label. */
const SLOT_ORDER = [
  ['head','Head'], ['neck','Neck'], ['shoulder','Shoulder'], ['back','Back'],
  ['chest','Chest'], ['shirt','Shirt'], ['tabard','Tabard'], ['wrist','Wrist'],
  ['hands','Hands'], ['waist','Waist'], ['legs','Legs'], ['feet','Feet'],
  ['finger1','Ring 1'], ['finger2','Ring 2'], ['trinket1','Trinket 1'], ['trinket2','Trinket 2'],
  ['mainhand','Main hand'], ['offhand','Off hand'], ['ranged','Ranged']
];
function slotLabel(key){
  for(let i = 0; i < SLOT_ORDER.length; i++) if(SLOT_ORDER[i][0] === key) return SLOT_ORDER[i][1];
  return key || '';
}

/* The item on Wowhead itself — only the item page links out. */
function wowheadHref(id){ return 'https://www.wowhead.com/' + WOWHEAD_DOMAIN + '/item=' + id; }

/* The `data-wowhead` payload the widget reads. `extra` is the gear list's
   `&ench=…&gems=…` suffix. */
function wowheadTip(id, extra){
  return 'domain=' + WOWHEAD_DOMAIN + '&item=' + id + (extra || '');
}

/* Wowhead's widget rewrites the links it finds when it loads. Re-running it after
   a re-render is what attaches tooltips to the new links. */
function loadWowhead(){
  if(window.$WowheadPower && window.$WowheadPower.refreshLinks){
    window.$WowheadPower.refreshLinks();
    return;
  }
  if(document.getElementById('wh-power')) return;
  const s = document.createElement('script');
  s.id = 'wh-power';
  s.src = 'https://wow.zamimg.com/js/tooltips.js';
  document.body.appendChild(s);
}

/* ───────────────────────── Shared nav links ─────────────────────────
   The one source of truth for the hamburger drawer, rendered on every page by
   renderNav() below. Officer-only links carry `officer:true` and are hidden for
   home-tier members by the same [data-officer-only] pass used elsewhere. */
const NAV_LINKS = [
  { href:'home.html',       label:'Home' },
  { href:'logs.html',       label:'Warcraft Logs' },
  { href:'board.html',      label:'Vash assignments', officer:true },
  { href:'attendance.html', label:'Attendance',       officer:true },
  { href:'loot.html',       label:'Loot log',         officer:true },
  { href:'members.html',    label:'Roster & alts',    officer:true },
  { href:'character.html',  label:'Character sheet' },
  { href:'items.html',      label:'Items' },
  { href:'loot-history.html', label:'Loot history' },
  { href:'loot-stats.html', label:'Loot stats' },
  { href:'sheet.html',      label:'Loot sheet' }
];

function renderNav(drawer){
  const here = (location.pathname.split('/').pop() || 'index.html').toLowerCase();
  drawer.innerHTML = NAV_LINKS.map(function(l){
    const active = l.href.toLowerCase() === here ? ' class="active"' : '';
    const officer = l.officer ? ' data-officer-only' : '';
    const label = l.label.replace(/&/g, '&amp;');
    return '<a href="' + l.href + '"' + active + officer + '>' + label + '</a>';
  }).join('');
}

/* ───────────────────────── Hamburger menu ───────────────────────── */
(function(){
  function ready(fn){
    if(document.readyState !== 'loading') fn();
    else document.addEventListener('DOMContentLoaded', fn);
  }
  ready(function(){
    const toggle = document.getElementById('navToggle');
    const drawer = document.getElementById('navDrawer');

    // Build the nav from the shared list before anything reads its links.
    if(drawer) renderNav(drawer);

    // Keep a long-lived session alive without a trip back to Discord.
    refreshSessionIfStale();

    // Show who's signed in, wherever a page exposes the slot.
    const who = document.getElementById('authWho');
    if(who){
      const name = sessionName();
      who.textContent = name ? 'Signed in as ' + name : '';
    }

    // Hide officer-only nav/hub items for home-tier members.
    if(!isOfficer()){
      document.querySelectorAll('[data-officer-only]').forEach(function(el){
        el.style.display = 'none';
      });
    }

    if(!toggle || !drawer) return;

    function open(){ drawer.classList.add('open'); toggle.setAttribute('aria-expanded','true'); }
    function close(){ drawer.classList.remove('open'); toggle.setAttribute('aria-expanded','false'); }
    function isOpen(){ return drawer.classList.contains('open'); }

    toggle.addEventListener('click', function(e){
      e.stopPropagation();
      isOpen() ? close() : open();
    });
    document.addEventListener('click', function(e){
      if(isOpen() && !drawer.contains(e.target) && e.target !== toggle) close();
    });
    document.addEventListener('keydown', function(e){
      if(e.key === 'Escape' && isOpen()) close();
    });
  });
})();
