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

/* ───────────────────────── Shared Raid-Helper + class helpers ─────────────────────────
   Everything two pages now need to turn a Raid-Helper signup list into draggable
   raiders: the class/spec tables, the role classifiers, the event fetchers, and
   the chip markup. board.html (app.js) imports a roster onto the Vash board;
   groups.html (groups.js) splits two signups into two 25-man groups.

   It all hangs off one `RH` object rather than bare globals on purpose: app.js
   declares `const CLASS_COLORS` at its own top level, and board.html loads this
   file first, so a global of the same name here would be a duplicate-const
   SyntaxError that kills the board outright. */
const RH = (function(){

  // Raid-Helper rejects cross-origin browser requests, so every call routes
  // through our own backend (server/WoobackVash.Api), which forwards the path
  // unchanged to https://raid-helper.xyz/api, adds the token, and returns the
  // CORS header the browser needs. The server id is fixed — nothing to enter.
  const SERVER_ID = '1462481995119722649';
  // List events whose start time is no older than this many days (upcoming
  // events are always included). Raise it to reach further back.
  const WINDOW_DAYS = 7;

  const CLASS_COLORS = {
    warrior:'#C79C6E', paladin:'#F58CBA', hunter:'#ABD473', rogue:'#FFF569',
    priest:'#FFFFFF', shaman:'#0070DE', mage:'#69CCF0', warlock:'#9482C9', druid:'#FF7D0A'
  };
  const ROLE_FALLBACK = { tank:'#8f9ba8' };

  // Tentative/Bench rows carry a spec tag but no class header, so infer the class
  // from the spec. Ambiguous specs (holy/restoration/protection) pick the most
  // common class; that only matters when no class header is present.
  const SPEC_TO_CLASS = {
    arms:'warrior', fury:'warrior', protection:'warrior',
    assassination:'rogue', combat:'rogue', subtlety:'rogue',
    beastmastery:'hunter', marksmanship:'hunter', survival:'hunter',
    arcane:'mage', fire:'mage', frost:'mage',
    affliction:'warlock', demonology:'warlock', destruction:'warlock',
    discipline:'priest', shadow:'priest', holy:'priest',
    balance:'druid', feral:'druid', guardian:'druid', restoration:'druid',
    retribution:'paladin',
    enhancement:'shaman', elemental:'shaman'
  };

  // Raid-Helper signup buttons that represent a status rather than a class.
  const STATUS_MAP = {
    bench:'bench', standby:'bench',
    tentative:'tentative', late:'tentative',
    absence:'absence', absent:'absence', declined:'absence'
  };

  // Specs decide the role when the roster provides them; otherwise fall back to class.
  const HEALING_SPECS = ['holy','restoration','discipline'];
  const HEALER_CLASSES = ['priest','paladin','druid','shaman'];
  const RANGED_SPECS = ['shadow','elemental','balance','arcane','fire','frost',
                        'affliction','demonology','destruction','beastmastery',
                        'marksmanship','survival'];
  const PURE_RANGE = ['mage','warlock','hunter'];

  function isTank(m){
    return m.role === 'tank' || m.spec === 'protection' || m.spec === 'guardian';
  }
  function isHealer(m){
    if(isTank(m)) return false;
    if(m.spec) return HEALING_SPECS.includes(m.spec);
    return HEALER_CLASSES.includes(m.cls);
  }
  function isRanged(m){
    if(isTank(m) || isHealer(m)) return false;
    if(m.spec) return RANGED_SPECS.includes(m.spec);
    return PURE_RANGE.includes(m.cls);
  }

  // The session, as the Authorization header the gated proxy wants.
  function headers(){
    const t = sessionToken();
    return t ? { 'Authorization': 'Bearer ' + t } : {};
  }

  // Human-readable date for an event, for the picker labels.
  function fmtEventDate(ev){
    const t = Number(ev.startTime || ev.startTimestamp || 0);
    if(t){
      const d = new Date(t * 1000);
      if(!isNaN(d.getTime())){
        return d.toLocaleDateString(undefined, { month:'short', day:'numeric' }) +
               ' ' + d.toLocaleTimeString(undefined, { hour:'2-digit', minute:'2-digit' });
      }
    }
    return [ev.date, ev.time].filter(Boolean).join(' ');
  }

  // One fetch against the proxy. HTTP failures throw an Error carrying `.status`
  // so each page can phrase its own message (401 → sign out, 403 → no token set,
  // 404 → gone); a network failure throws with `.status` unset.
  async function get(path){
    let res;
    try {
      res = await fetch(API_BASE + path, { headers: headers() });
    } catch(err){
      const e = new Error('Could not reach the proxy — is the backend up and is this origin allowed?');
      e.cause = err;
      throw e;
    }
    if(!res.ok){
      const e = new Error('Raid-Helper returned HTTP ' + res.status + '.');
      e.status = res.status;
      throw e;
    }
    return res.json();
  }

  // Every event on the server, newest first, cut to the last WINDOW_DAYS
  // (upcoming events always survive the cut). `total` is the unfiltered count,
  // so a page can say "none recent, but the server has N".
  async function listEvents(){
    const data = await get('/v4/servers/' + encodeURIComponent(SERVER_ID) + '/events');
    // The field name for the events array isn't verified against a live token —
    // log the raw response so an unexpected shape is easy to diagnose.
    console.debug('Raid-Helper server-events response:', data);
    const all = data.postedEvents || data.events || data.scheduledEvents ||
                (Array.isArray(data) ? data : []);
    const cutoff = Date.now() / 1000 - WINDOW_DAYS * 86400;
    const events = all.filter(function(ev){
      return Number(ev.startTime || ev.startTimestamp || 0) >= cutoff;
    });
    events.sort(function(a,b){ return Number(b.startTime || 0) - Number(a.startTime || 0); });
    events.total = all.length;
    return events;
  }

  // One event, signups included.
  function fetchEvent(eventId){
    return get('/v4/events/' + encodeURIComponent(eventId));
  }

  // Turn Raid-Helper signups into the internal roster shape the pages use.
  // `makeId` mints each raider's id: the board threads its own counter through
  // because that counter rides along in its saved snapshots.
  function mapSignups(signups, makeId){
    let n = 0;
    const nextId = makeId || function(){ return 'rh' + (n++); };
    const out = [];
    const seen = new Set();
    (signups || []).forEach(function(s){
      const name = String(s.name || '').replace(/[`:]/g,'').replace(/^\[|\]$/g,'').trim();
      if(!name) return;

      const classKey = String(s.className || '').toLowerCase().trim();
      const spec = String(s.specName || '').toLowerCase().replace(/[^a-z]/g,'');

      // status may live in className (Bench/Late/Tentative/Absence) or a status field
      const status = STATUS_MAP[classKey]
                  || STATUS_MAP[String(s.status || '').toLowerCase()]
                  || 'active';
      if(status === 'absence') return;           // absent raiders never reach a board

      // resolve a real class: className if valid, else infer from spec
      const cls  = CLASS_COLORS[classKey] ? classKey
                 : (CLASS_COLORS[SPEC_TO_CLASS[spec]] ? SPEC_TO_CLASS[spec] : '');
      const role = cls || classKey || SPEC_TO_CLASS[spec] || '';

      const key = name.toLowerCase();
      if(seen.has(key)) return;                  // de-dupe by name, first signup wins
      seen.add(key);

      out.push({ id:nextId(), name, cls, role, spec, num:null, status });
    });
    return out;
  }

  // A draggable raider. `tag` is an optional extra pill (the groups page marks
  // MAIN / ALT / UNLINKED); `status` renders the tentative/bench pill.
  function chipHTML(member){
    const color = CLASS_COLORS[member.cls] || ROLE_FALLBACK[member.role] || '#2ee6ab';
    const num = member.num != null ? `<span class="num">${member.num}</span>` : '';
    const roleTag = member.cls ? member.cls.slice(0,3).toUpperCase()
                  : (member.role ? member.role.slice(0,3).toUpperCase() : '');
    const tag = member.tag
      ? `<span class="stag ${String(member.tag).toLowerCase().replace(/[^a-z]+/g,'-')}">${whEsc(member.tag)}</span>` : '';
    const status = (member.status && member.status !== 'active')
      ? `<span class="stag ${member.status}">${member.status === 'tentative' ? 'TENT' : 'BENCH'}</span>` : '';
    return `<span class="chip" draggable="true" data-id="${member.id}" style="--class-color:${color}">
              ${num}${whEsc(member.name)}${roleTag?`<span class="cls">${roleTag}</span>`:''}${tag}${status}
            </span>`;
  }

  // Drag-out on every chip inside `el`, and drop-onto-`el` handled by `onDrop`.
  function wirePoolDrag(el, onDrop){
    el.querySelectorAll('.chip').forEach(function(c){
      c.addEventListener('dragstart', function(e){
        e.dataTransfer.setData('text/plain', c.dataset.id);
        e.dataTransfer.effectAllowed = 'move';
      });
    });
    el.ondragover = function(e){ e.preventDefault(); el.style.background='rgba(46,230,171,.08)'; };
    el.ondragleave = function(){ el.style.background=''; };
    el.ondrop = function(e){
      e.preventDefault();
      el.style.background='';
      const id = e.dataTransfer.getData('text/plain');
      if(id) onDrop(id);
    };
  }

  return {
    SERVER_ID, WINDOW_DAYS, CLASS_COLORS, ROLE_FALLBACK, SPEC_TO_CLASS, STATUS_MAP,
    HEALING_SPECS, HEALER_CLASSES, RANGED_SPECS, PURE_RANGE,
    isTank, isHealer, isRanged, headers, fmtEventDate,
    listEvents, fetchEvent, mapSignups, chipHTML, wirePoolDrag
  };
})();

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
  { href:'groups.html',     label:'2 group organisation', officer:true },
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
