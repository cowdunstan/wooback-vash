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

/* ───────────────────────── Shared API base ─────────────────────────
   Every page talks to the same .NET backend (server/WoobackVash.Api): Discord
   OAuth, board save/load, loot, attendance, and the Warcraft-Logs / Raid-Helper
   proxies all live behind this host. Localhost points at the dev server on 8080;
   everything else at the deployed Fly app. Defined here so no page redefines it. */
const API_BASE = (location.hostname === 'localhost' || location.hostname === '127.0.0.1')
  ? 'http://localhost:8080'
  : 'https://wooback-vash-api.fly.dev';

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
