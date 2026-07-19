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

/* ───────────────────────── Hamburger menu ───────────────────────── */
(function(){
  function ready(fn){
    if(document.readyState !== 'loading') fn();
    else document.addEventListener('DOMContentLoaded', fn);
  }
  ready(function(){
    const toggle = document.getElementById('navToggle');
    const drawer = document.getElementById('navDrawer');

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
