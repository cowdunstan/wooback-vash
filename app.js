/* ───────────────────────── Officer session ─────────────────────────
   The board.html guard already redirected non-officers away. The shared session
   helpers (sessionToken/sessionName/logout/isOfficer) live in menu.js, which is
   loaded before this file — as does the shared `RH` module: the class/spec
   tables, the role classifiers, the Raid-Helper fetchers and the chip markup,
   all of which groups.html uses too. Aliased here so the board's own code reads
   the way it always has. */
const rhHeaders = RH.headers;
const CLASS_COLORS = RH.CLASS_COLORS;
const SPEC_TO_CLASS = RH.SPEC_TO_CLASS;

// Non-class section headers and the status they assign.
const SPECIAL_STATUS = {
  tentative:'tentative', tentatives:'tentative', late:'tentative',
  bench:'bench', benched:'bench', standby:'bench',
  absence:'absence', absent:'absence', declined:'absence', out:'absence'
};

let roster = [];          // {id, name, cls}
let counts = { range: 6, healer: 5, chaser: 3 };
let assignments = { range: [], healer: [], chaser: [] };
let idCounter = 0;

function parseRoster(){
  const raw = document.getElementById('rosterInput').value;
  if(!raw.trim()) return;
  const CLASS_NAMES = ['warrior','paladin','hunter','rogue','priest','shaman','mage','warlock','druid','tank'];
  const found = [];
  const seen = new Set();
  let currentClass = null;
  let currentStatus = 'active';   // 'active' | 'tentative' | 'bench' | 'absence'

  raw.split('\n').forEach(rawLine=>{
    // strip invisible separators and emoji-style tag punctuation
    let line = rawLine.replace(/[\u200b-\u200f\u202a-\u202e\ufeff]/g,'')
                      .replace(/\u00a0/g,' ').trim();
    if(!line) return;

    // section header:  ":Priest: Priest (4)" / "Priest (4)" / ":Bench: Bench (1) : ..."
    const hdr = line.match(/^(?::[\w+-]+:\s*)?([A-Za-z]+)\s*\(\d+\)\s*(.*)$/);
    if(hdr){
      const key = hdr[1].toLowerCase();
      const rest = hdr[2] || '';
      if(CLASS_NAMES.includes(key)){
        currentClass = key;
        currentStatus = 'active';
        // inline members on the same line (old backtick format)
        const re = /`\s*(\d+)\s*`\s*([^`]+)/g;
        let m;
        while((m = re.exec(rest)) !== null){
          addMember(m[2], currentClass, '', parseInt(m[1],10), 'active');
        }
        return;
      }
      if(SPECIAL_STATUS[key]){
        currentClass = null;                       // no class header for these groups
        currentStatus = SPECIAL_STATUS[key];
        parseInlineMembers(rest, currentStatus);   // members can trail on the header line
        return;
      }
      // unrecognised header — leave the current section context untouched
      return;
    }

    // count/summary row: ":Ranged: Ranged 8 :Ranged:"
    const stripped = line.replace(/:[\w+-]+:/g,'').trim();
    if(/^[\d\s]*(melee|ranged|healers?|tanks?|dps)[\d\s]*$/i.test(stripped)) return;

    // member with spec tag:  ":Holy: 3 Youngjoe"
    const mem = line.match(/^:([\w+-]+?)\d*:\s*(\d+)?\s*(.+)$/);
    if(mem && currentClass){
      addMember(mem[3], currentClass, mem[1].toLowerCase(), mem[2] ? parseInt(mem[2],10) : null, currentStatus);
      return;
    }
    // spec-tagged rows under a special (non-class) section: infer class from spec
    if(mem && currentStatus !== 'active'){
      parseInlineMembers(line, currentStatus);
      return;
    }

    // bare "12 Name" under a class header
    const bare = line.match(/^(\d+)?\s*([A-Za-zÀ-ÿ0-9'’_\[\]-]+)$/);
    if(bare && currentClass){
      addMember(bare[2], currentClass, '', bare[1] ? parseInt(bare[1],10) : null, currentStatus);
      return;
    }
    if(bare && currentStatus !== 'active'){
      parseInlineMembers(line, currentStatus);
      return;
    }

    // fallback "Name Class"
    const parts = stripped.split(/[,\t]+|\s+/).filter(Boolean);
    if(parts.length > 1){
      const last = parts[parts.length-1].toLowerCase();
      if(CLASS_COLORS[last]) addMember(parts.slice(0,-1).join(' '), last, '', null, 'active');
    }
  });

  // Parse a comma-separated run of members that carry only a spec tag (no class
  // header), e.g. ":Arcane: 23 TCoody, :Enhancement: 27 [VEGA]" or "10 Easyhealing, ...".
  function parseInlineMembers(text, status){
    if(!text) return;
    text.split(',').forEach(piece=>{
      // drop a leading separator colon ("... (2) : :Arcane: ...") without eating a :Spec: tag
      const p = piece.trim().replace(/^:\s+/,'').trim();
      if(!p) return;
      const m = p.match(/^(?::([\w+-]+?)\d*:\s*)?(\d+)?\s*(.+)$/);
      if(!m) return;
      const spec = m[1] ? m[1].toLowerCase() : '';
      const cls  = SPEC_TO_CLASS[spec] || '';
      addMember(m[3], cls, spec, m[2] ? parseInt(m[2],10) : null, status);
    });
  }

  function addMember(name, cls, spec, num, status){
    status = status || 'active';
    if(status === 'absence') return;   // absent raiders never reach the board
    name = String(name).replace(/[`:]/g,'').replace(/^\[|\]$/g,'').trim();
    if(!name) return;
    const key = name.toLowerCase();
    if(seen.has(key)) return;
    seen.add(key);
    found.push({
      id: 'r'+(idCounter++),
      name: name,
      cls: CLASS_COLORS[cls] ? cls : '',
      role: cls,
      spec: spec || '',
      num: (num == null ? null : num),
      status: status
    });
  }

  if(!found.length){
    alert('Could not read any names.\n\nExpected either:\n  :Priest: Priest (4)\n  :Holy: 3 Youngjoe\n\nor:\n  Priest (4)  `7` Nightbrew  `3` Youngjoe');
    return;
  }

  found.sort((a,b)=> (a.num ?? 999) - (b.num ?? 999));
  roster = found;
  autoFill();
}

// Specs decide the role when the roster provides them; otherwise fall back to
// class. Shared with the groups page — see RH in menu.js.
const isTank = RH.isTank, isHealer = RH.isHealer, isRanged = RH.isRanged;

function autoFill(){
  // Only confirmed raiders are auto-placed; tentative/bench wait in the pool.
  const active  = roster.filter(m => (m.status || 'active') === 'active');
  const healers = active.filter(isHealer);
  const ranged  = active.filter(isRanged);

  // chase team: 1 elemental shaman, 1 mage, 1 shadow priest
  // (+1 healer when the raid is running more than 6 healers)
  const chasers = [];
  const takeFirst = pred => {
    const pick = active.find(m => !chasers.includes(m.id) && pred(m));
    if(pick) chasers.push(pick.id);
    return !!pick;
  };
  takeFirst(m => m.cls === 'shaman' && m.spec === 'elemental');
  takeFirst(m => m.cls === 'mage' && !isHealer(m));
  takeFirst(m => m.cls === 'priest' && m.spec === 'shadow');

  if(healers.length > 6){
    const extra = healers.find(m => !chasers.includes(m.id));
    if(extra) chasers.push(extra.id);
  }

  counts.chaser = Math.max(1, chasers.length || 3);
  const chaseSet = new Set(chasers);
  const freeHealers = healers.filter(m => !chaseSet.has(m.id));
  const freeRanged  = ranged.filter(m => !chaseSet.has(m.id));

  counts.healer = Math.max(1, freeHealers.length);
  counts.range  = Math.max(1, freeRanged.length);

  assignments = {
    range:  new Array(counts.range).fill(null),
    healer: new Array(counts.healer).fill(null),
    chaser: new Array(counts.chaser).fill(null)
  };
  chasers.forEach((id,i)=>{ assignments.chaser[i] = id; });
  freeHealers.forEach((m,i)=>{ if(i < counts.healer) assignments.healer[i] = m.id; });
  freeRanged.forEach((m,i)=>{ if(i < counts.range) assignments.range[i] = m.id; });

  renderAll();
}

function changeCount(type, delta){
  counts[type] = Math.max(1, Math.min(12, counts[type] + delta));
  document.getElementById('count'+type[0].toUpperCase()+type.slice(1)).textContent = counts[type];
  const arr = assignments[type];
  if(counts[type] > arr.length){
    while(arr.length < counts[type]) arr.push(null);
  } else {
    const removed = arr.splice(counts[type]);
    removed.forEach(id=>{ if(id) unassignId(id, false); });
  }
  renderAll();
}

function assignedIds(){
  return new Set([...assignments.range, ...assignments.healer, ...assignments.chaser].filter(Boolean));
}

function unassignId(id, rerender=true){
  ['range','healer','chaser'].forEach(type=>{
    const i = assignments[type].indexOf(id);
    if(i!==-1) assignments[type][i] = null;
  });
  if(rerender) renderAll();
}

function unassignAll(){
  assignments.range = assignments.range.map(()=>null);
  assignments.healer = assignments.healer.map(()=>null);
  assignments.chaser = assignments.chaser.map(()=>null);
  renderAll();
}

function clearAll(){
  roster = [];
  assignments = { range: new Array(counts.range).fill(null), healer: new Array(counts.healer).fill(null), chaser: new Array(counts.chaser).fill(null) };
  document.getElementById('rosterInput').value = '';
  renderAll();
}

function memberById(id){ return roster.find(m=>m.id===id); }

const chipHTML = RH.chipHTML;

// Wire drag-out on chips and drop-to-unassign on a pool container. Status is
// stored on the member, so a chip returns to the right section.
function wirePool(el){
  RH.wirePoolDrag(el, id => unassignId(id));
}

function renderPool(){
  const used = assignedIds();
  const avail = roster.filter(m=>!used.has(m.id));
  const reserve = avail.filter(m => m.status === 'tentative' || m.status === 'bench');
  const active  = avail.filter(m => !m.status || m.status === 'active');

  const wrap = document.getElementById('reserveWrap');
  const rEl = document.getElementById('reservePool');
  if(reserve.length){
    wrap.style.display = '';
    rEl.innerHTML = reserve.map(m=>chipHTML(m)).join('');
    wirePool(rEl);
  } else {
    wrap.style.display = 'none';
    rEl.innerHTML = '';
  }

  const el = document.getElementById('rosterPool');
  el.innerHTML = active.length ? active.map(m=>chipHTML(m)).join('') : '<span class="pool-empty">Everyone is assigned. Nice.</span>';
  wirePool(el);
}

function circlePositions(n, cx, cy, rx, ry, startDeg){
  const pts = [];
  for(let i=0;i<n;i++){
    const angle = (startDeg + (360/n)*i) * Math.PI/180;
    pts.push({ x: cx + rx*Math.cos(angle), y: cy + ry*Math.sin(angle) });
  }
  return pts;
}

// Rock blocks segments 1, 8 and 12, leaving two open arcs: segments 2-7 and 9-11.
// Range spreads evenly along those arcs — spots need not sit on a segment centre.
const OPEN_ARCS = [
  { from: -90 + 30*1,  to: -90 + 30*7  },   // start of seg 2 -> end of seg 7
  { from: -90 + 30*8,  to: -90 + 30*11 }    // start of seg 9 -> end of seg 11
];
function segmentPositions(n, cx, cy, r){
  const spans = OPEN_ARCS.map(a => a.to - a.from);
  const total = spans.reduce((s,v)=>s+v, 0);
  // share the raiders between arcs in proportion to their length
  const counts = spans.map(s => Math.max(1, Math.round(n * s / total)));
  let drift = n - counts.reduce((s,v)=>s+v, 0);
  // When n is small every arc can already be at its floor of 1, leaving a negative
  // drift that can never be corrected — bail out then instead of spinning forever.
  // (Extra positions are harmless: renderSlotGroup only reads as many as it needs.)
  for(let i=0; drift !== 0; i=(i+1)%counts.length){
    if(drift > 0){ counts[i]++; drift--; }
    else if(counts[i] > 1){ counts[i]--; drift++; }
    else if(!counts.some(c => c > 1)) break;
  }

  const pts = [];
  OPEN_ARCS.forEach((arc, ai)=>{
    const k = counts[ai];
    const span = arc.to - arc.from;
    // inset from the arc ends so nobody hugs the rock edge
    const pad = Math.min(12, span / (k + 1));
    const lo = arc.from + pad, hi = arc.to - pad;
    for(let i=0;i<k;i++){
      const deg = k === 1 ? (lo + hi)/2 : lo + (hi - lo) * (i / (k - 1));
      const a = deg * Math.PI/180;
      pts.push({ x: cx + r*Math.cos(a), y: cy + r*Math.sin(a) });
    }
  });
  return pts;
}

function renderSlotGroup(containerId, type, positions, extraClass){
  const el = document.getElementById(containerId);
  el.innerHTML = '';
  assignments[type].forEach((id, i)=>{
    const pos = positions[i] || {x:320,y:220};
    const member = id ? memberById(id) : null;
    const div = document.createElement('div');
    div.className = 'slot' + (extraClass?' '+extraClass:'');
    div.style.left = pos.x+'px';
    div.style.top = pos.y+'px';
    const color = member ? (CLASS_COLORS[member.cls] || RH.ROLE_FALLBACK[member.role] || '') : '';
    div.innerHTML = `<div class="ring ${member?'filled':''}" style="${member?`border-color:${color||'var(--teal)'}`:''}">
        ${member ? `<span class="name-wrap"><span class="name-chip" draggable="true" data-id="${member.id}" style="background:${color||'#2ee6ab'}">${member.name}</span></span>` : (type==='healer'?'heal':'range')}
      </div>
      <div class="label">${type==='healer'?'Healer':'Range'} ${i+1}</div>`;

    const ring = div.querySelector('.ring');

    if(member){
      const chip = div.querySelector('.name-chip');
      chip.title = member.name + ' — drag to move, double-click to remove';
      chip.addEventListener('dragstart', e=>{
        e.stopPropagation();
        e.dataTransfer.setData('text/plain', member.id);
        e.dataTransfer.effectAllowed = 'move';
      });
      chip.addEventListener('dblclick', e=>{ e.stopPropagation(); unassignId(member.id); });
    }

    ring.addEventListener('dragover', e=>{ e.preventDefault(); ring.classList.add(member?'swap-target':'drag-over'); });
    ring.addEventListener('dragleave', ()=> ring.classList.remove('drag-over','swap-target'));
    ring.addEventListener('drop', e=>{
      e.preventDefault();
      ring.classList.remove('drag-over','swap-target');
      const draggedId = e.dataTransfer.getData('text/plain');
      if(!draggedId || draggedId === id) return;
      dropInto(type, i, draggedId);
    });
    el.appendChild(div);
  });
}

// Place a raider in a slot. If they came from another slot and the target is
// taken, the two swap instead of one being sent back to the pool.
function dropInto(type, index, draggedId){
  const origin = findSlot(draggedId);
  const occupant = assignments[type][index];
  if(origin){
    assignments[origin.type][origin.index] = occupant || null;
  } else if(occupant){
    // dragged in from the pool — displaced raider returns to the pool
    unassignId(occupant, false);
  }
  assignments[type][index] = draggedId;
  renderAll();
}

function findSlot(id){
  for(const type of ['range','healer','chaser']){
    const index = assignments[type].indexOf(id);
    if(index !== -1) return { type, index };
  }
  return null;
}

function renderChase(){
  const el = document.getElementById('chaseSlots');
  el.innerHTML = '';
  assignments.chaser.forEach((id,i)=>{
    const member = id ? memberById(id) : null;
    const div = document.createElement('div');
    div.className = 'chase-slot' + (member?' filled':'');
    div.innerHTML = member
      ? `<span class="name-chip" draggable="true" data-id="${member.id}">${member.name}</span>`
      : `Chaser ${i+1}`;

    if(member){
      const chip = div.querySelector('.name-chip');
      chip.title = member.name + ' — drag to move, double-click to remove';
      chip.addEventListener('dragstart', e=>{
        e.stopPropagation();
        e.dataTransfer.setData('text/plain', member.id);
        e.dataTransfer.effectAllowed = 'move';
      });
      chip.addEventListener('dblclick', e=>{ e.stopPropagation(); unassignId(member.id); });
    }

    div.addEventListener('dragover', e=>{ e.preventDefault(); div.style.background='rgba(232,163,61,.22)'; });
    div.addEventListener('dragleave', ()=>{ div.style.background=''; });
    div.addEventListener('drop', e=>{
      e.preventDefault();
      div.style.background='';
      const draggedId = e.dataTransfer.getData('text/plain');
      if(!draggedId || draggedId === id) return;
      dropInto('chaser', i, draggedId);
    });
    el.appendChild(div);
  });
}

function renderAll(){
  document.getElementById('countRange').textContent = counts.range;
  document.getElementById('countHealer').textContent = counts.healer;
  document.getElementById('countChaser').textContent = counts.chaser;
  document.getElementById('captureGuild').textContent = document.getElementById('guildName').value || 'Raid';

  const rangePos = segmentPositions(counts.range, 320, 214, 134);
  const healerPos = circlePositions(counts.healer, 320, 214, 61, 61, -90);

  renderSlotGroup('rangeSlots', 'range', rangePos, '');
  renderSlotGroup('healerSlots', 'healer', healerPos, 'healer');
  renderChase();
  renderPool();
}

document.getElementById('guildName').addEventListener('input', ()=>{
  document.getElementById('captureGuild').textContent = document.getElementById('guildName').value || 'Raid';
});

function exportImage(){
  html2canvas(document.getElementById('capture'), { backgroundColor: '#081210', scale: 2 }).then(canvas=>{
    const link = document.createElement('a');
    const guild = (document.getElementById('guildName').value || 'raid').replace(/\s+/g,'_');
    link.download = `vashj-p2-${guild}.png`;
    link.href = canvas.toDataURL('image/png');
    link.click();
  });
}

/* ───────────────────────── Board save / load ─────────────────────────
   Officers persist the whole board (guild name, slot counts, roster and
   assignments) to the .NET API under currentBoardKey — the loaded Raid-Helper
   event, or "default" for a hand-built board. Saving is explicit (the "Save
   layout" button); loading is explicit too so it never clobbers a fresh import. */
function setBoardStatus(msg, isErr){
  const el = document.getElementById('boardStatus');
  if(!el) return;
  el.textContent = msg || '';
  el.style.color = isErr ? 'var(--amber)' : 'var(--text-dim)';
}

// The exact in-memory board state, as saved. Mirrors what renderAll() reads.
function boardSnapshot(){
  return {
    guildName: document.getElementById('guildName').value || '',
    counts: counts,
    roster: roster,
    assignments: assignments,
    idCounter: idCounter
  };
}

// Restore a saved snapshot into the live state and re-render.
function restoreBoard(s){
  if(!s || typeof s !== 'object') return;
  if(typeof s.guildName === 'string') document.getElementById('guildName').value = s.guildName;
  if(s.counts && typeof s.counts === 'object') counts = s.counts;
  if(Array.isArray(s.roster)) roster = s.roster;
  if(s.assignments && typeof s.assignments === 'object') assignments = s.assignments;
  if(typeof s.idCounter === 'number') idCounter = s.idCounter;
  renderAll();
}

async function saveBoard(){
  const key = currentBoardKey || 'default';
  setBoardStatus('Saving…');
  try {
    const res = await fetch(`${API_BASE}/api/board?key=${encodeURIComponent(key)}`, {
      method: 'PUT',
      headers: Object.assign({ 'Content-Type': 'application/json' }, rhHeaders()),
      body: JSON.stringify({ state: boardSnapshot(), title: document.getElementById('guildName').value || null })
    });
    if(res.status === 401){ setBoardStatus('Session expired — sign in again.', true); return; }
    if(res.status === 403){ setBoardStatus('Officer access required.', true); return; }
    if(!res.ok){ setBoardStatus('Save failed (HTTP ' + res.status + ').', true); return; }
    setBoardStatus(key === 'default' ? 'Saved the default board.' : 'Saved for this event.');
  } catch(err){
    setBoardStatus('Could not reach the save service.', true);
    console.error('Board save failed:', err);
  }
}

async function loadBoard(){
  const key = currentBoardKey || 'default';
  setBoardStatus('Loading saved…');
  try {
    const res = await fetch(`${API_BASE}/api/board?key=${encodeURIComponent(key)}`, { headers: rhHeaders() });
    if(res.status === 401){ setBoardStatus('Session expired — sign in again.', true); return; }
    if(res.status === 403){ setBoardStatus('Officer access required.', true); return; }
    if(!res.ok){ setBoardStatus('Load failed (HTTP ' + res.status + ').', true); return; }
    const data = await res.json();
    if(!data || !data.found){
      setBoardStatus(key === 'default' ? 'No saved default board yet.' : 'No saved layout for this event yet.');
      return;
    }
    restoreBoard(data.state);
    setBoardStatus('Loaded the saved layout.');
  } catch(err){
    setBoardStatus('Could not reach the save service.', true);
    console.error('Board load failed:', err);
  }
}

/* ───────────────────────── Raid-Helper API integration ─────────────────────────
   Press "Load events" to list this server's events, then pick one to pull its
   signups onto the board. There is no token in this page: it is a secret on the
   backend (RaidHelper__Token), attached server-side by the proxy. The fetching
   itself lives in RH (menu.js) — the groups page uses the same calls; what's
   left here is the board's own picker, status line and roster hand-off. */

// The storage key for the current board: a Raid-Helper event id once one is
// loaded, otherwise "default" for a manually built board.
let currentBoardKey = 'default';

function setRhStatus(msg, isErr){
  const el = document.getElementById('rhStatus');
  el.textContent = msg || '';
  el.style.color = isErr ? 'var(--amber)' : 'var(--text-dim)';
}

// Report a failed RH call. Returns nothing useful — callers just bail after it.
// A 401 means the session died under us, so hand the raider back to Discord.
function reportRhError(err, notFoundMsg){
  console.error('Raid-Helper call failed:', err);
  if(err.status === 401){ setRhStatus('Session expired — signing you out…', true); setTimeout(logout, 1200); return; }
  if(err.status === 403){ setRhStatus('Unauthorized — the Raid-Helper token isn’t set on the backend.', true); return; }
  if(err.status === 404){ setRhStatus(notFoundMsg, true); return; }
  setRhStatus(err.message + ' See the browser console.', true);
}

// List every event on the server (the proxy supplies the token) and fill the picker.
async function loadServerEvents(){
  setRhStatus('Loading events…');

  let events;
  try {
    events = await RH.listEvents();
  } catch(err){
    reportRhError(err, 'Server not found — check RH.SERVER_ID in menu.js.');
    return;
  }

  const sel = document.getElementById('rhEventSelect');
  const row = document.getElementById('rhEventPickRow');
  if(!events.length){
    row.style.display = 'none';
    setRhStatus(events.total
      ? `No recent or upcoming events (server has ${events.total} total).`
      : 'No events found on that server.', true);
    return;
  }
  // Newest first, so the latest event is pre-selected.
  sel.innerHTML = events.map(ev=>{
    const id = ev.id || ev.eventId || '';
    const when = RH.fmtEventDate(ev);
    const title = String(ev.title || 'Untitled event').replace(/</g,'&lt;');
    return `<option value="${id}">${when ? when + ' — ' : ''}${title}</option>`;
  }).join('');
  row.style.display = 'flex';
  setRhStatus(`Found ${events.length} recent/upcoming event${events.length===1?'':'s'} — pick one to load its signups.`);
}

// Load the signups for whichever event is chosen in the picker.
function loadSelectedEvent(){
  const sel = document.getElementById('rhEventSelect');
  const id = sel && sel.value;
  if(!id){ setRhStatus('No event selected.', true); return; }
  fetchEventById(id);
}

// Fetch one event's signups and drop them onto the board.
async function fetchEventById(eventId){
  if(!eventId){ setRhStatus('No event id given.', true); return; }
  // Saves/loads for this board now key off the loaded event.
  currentBoardKey = String(eventId);
  setRhStatus('Loading signups…');

  let data;
  try {
    data = await RH.fetchEvent(eventId);
  } catch(err){
    reportRhError(err, 'Event not found (it may have been deleted).');
    return;
  }

  // The board's own id counter mints the ids, because it rides along in saved snapshots.
  const members = RH.mapSignups(data.signUps || data.signups || [], () => 'r' + (idCounter++));
  if(!members.length){ setRhStatus('No usable signups found on that event.', true); return; }

  roster = members;
  roster.sort((a,b)=> (a.num ?? 999) - (b.num ?? 999));
  autoFill();
  setRhStatus(`Loaded ${members.length} signups from “${data.title || 'event ' + eventId}”.`);
}


renderAll();
