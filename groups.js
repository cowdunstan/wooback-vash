/* ───────────────────────── 2 group organisation ─────────────────────────
   The guild runs the same raid twice at different times — one Raid-Helper signup
   for mains, a second for alts — so a raider can attend both, once on each
   character. This page merges those two signups into one pool and deals them
   into two 25-man groups.

   The rule the whole page is built around: **one person holds at most one slot
   per group**. Someone signed up to both raids has several chips in the pool,
   but their main and their alt can never sit in the same group. Identity is the
   roster link (GET /api/members), not the character name — that is what lets us
   tell "Tero's warrior" and "Tero's priest" apart from two different raiders.

   The shared Raid-Helper plumbing (event fetching, class tables, role
   classifiers, chip markup) is RH in menu.js, which board.html uses too. */

const STORE_KEY = 'vashj_groups';

let pool = [];                      // every candidate chip, placed or not
let groups = { 1: [], 2: [] };      // chip ids, in drop order
let groupSize = 25;
let picked = { a: null, b: null };  // { id, title } of the two loaded signups
let draggingId = '';                // chip being dragged, for the clash hint

function setStatus(msg, isErr){
  const el = document.getElementById('groupsStatus');
  el.textContent = msg || '';
  el.style.color = isErr ? 'var(--amber)' : 'var(--text-dim)';
}

function chipById(id){ return pool.find(c => c.id === id); }
function placedIds(){ return new Set([...groups[1], ...groups[2]]); }

/* ───────────────────────── Placing ─────────────────────────
   Every move goes through place() / sendToPool(), and place() always removes
   before it inserts. "In both groups at once" is therefore not a case to check
   for — it cannot be represented. The two things it does refuse are a full
   group and a second character belonging to someone already in that group. */

function removeFromGroups(id){
  [1,2].forEach(g => {
    const i = groups[g].indexOf(id);
    if(i !== -1) groups[g].splice(i, 1);
  });
}

// Who in `group` already belongs to the same person as `chip`, if anyone.
function clashIn(group, chip){
  return groups[group]
    .map(chipById)
    .find(c => c && c.id !== chip.id && c.personId === chip.personId);
}

function place(id, group){
  const chip = chipById(id);
  if(!chip) return;

  const clash = clashIn(group, chip);
  if(clash){
    setStatus(`${chip.name} can't join Group ${group} — ${clash.name} is already in it, and they're the same raider (${chip.personName}).`, true);
    return;
  }
  // Count the slot the chip is about to vacate, if it's already in this group.
  const size = groups[group].length - (groups[group].includes(id) ? 1 : 0);
  if(size >= groupSize){
    setStatus(`Group ${group} is full (${groupSize}). Move someone out first.`, true);
    return;
  }

  removeFromGroups(id);
  groups[group].push(id);
  setStatus(`${chip.name} → Group ${group}.`);
  save();
  renderAll();
}

function sendToPool(id){
  const chip = chipById(id);
  if(!chip || !placedIds().has(id)) return;
  removeFromGroups(id);
  setStatus(`${chip.name} → back to the pool.`);
  save();
  renderAll();
}

function clearGroups(){
  groups = { 1: [], 2: [] };
  setStatus('Both groups emptied — everyone is back in the pool.');
  save();
  renderAll();
}

function startOver(){
  pool = [];
  groups = { 1: [], 2: [] };
  picked = { a: null, b: null };
  itemCheck = [];
  try{ localStorage.removeItem(STORE_KEY); }catch(e){}
  setStatus('Cleared. Load two signups to start again.');
  renderAll();
}

function changeGroupSize(delta){
  groupSize = Math.max(5, Math.min(40, groupSize + delta));
  // Shrinking can strand people: the overflow goes back to the pool.
  let spilled = 0;
  [1,2].forEach(g => { spilled += groups[g].splice(groupSize).length; });
  document.getElementById('countGroup').textContent = groupSize;
  if(spilled) setStatus(`Group size is now ${groupSize} — ${spilled} raider${spilled===1?'':'s'} went back to the pool.`);
  save();
  renderAll();
}

/* ───────────────────────── Building the pool ─────────────────────────
   Two signups plus the roster links become one list of chips:
     • signed up to both raids → a chip per character linked to that member, so
       an officer can pick which one goes where;
     • signed up to one raid   → their main only (or, with no main recorded,
       the name they signed up with);
     • no roster link at all   → the signup as-is, flagged UNLINKED because the
       one-per-group rule can't see who they really are. */

let chipSeq = 0;
function makeChipId(){ return 'g' + (chipSeq++); }

// A roster character or a raw signup, in the shape RH.chipHTML and the role
// classifiers expect. Roster classes come back capitalised ("Priest").
function toChip(src, person, tag){
  const cls = String(src.cls || '').toLowerCase().trim();
  const spec = String(src.spec || '').toLowerCase().replace(/[^a-z]/g, '');
  return {
    id: makeChipId(),
    personId: person.personId,
    personName: person.name,
    name: src.name,
    cls: RH.CLASS_COLORS[cls] ? cls : (RH.CLASS_COLORS[RH.SPEC_TO_CLASS[spec]] ? RH.SPEC_TO_CLASS[spec] : ''),
    role: cls || RH.SPEC_TO_CLASS[spec] || '',
    spec: spec,
    num: null,
    status: person.status,
    tag: tag || '',
    source: person.sources.join('+')
  };
}

// The worst status wins: a raider tentative on one signup isn't a firm yes.
function mergeStatus(a, b){
  const rank = { active: 0, tentative: 1, bench: 2 };
  return (rank[b] || 0) > (rank[a] || 0) ? b : a;
}

function buildPool(signupsA, signupsB, members){
  // Two ways to recognise a raider, in order of how much they can be trusted.
  // The Discord id is who signed up, full stop; a character name only matches
  // when the roster already knows that character, which is exactly what fails
  // for a fresh alt or a name typed with a guild tag on it.
  const byDiscord = new Map();
  const byName = new Map();
  members.forEach(m => {
    if(m.discordUserId) byDiscord.set(String(m.discordUserId), m);
    (m.characters || []).forEach(c => byName.set(String(c.name || '').toLowerCase(), m));
  });

  // Fold both signups into one entry per person.
  const persons = new Map();
  let byDiscordCount = 0;
  [['a', signupsA], ['b', signupsB]].forEach(([source, signups]) => {
    signups.forEach(s => {
      const viaDiscord = s.userId ? byDiscord.get(s.userId) : null;
      const member = viaDiscord || byName.get(s.name.toLowerCase()) || null;
      if(viaDiscord) byDiscordCount++;
      // Even with no roster row, a Discord id still pairs someone's two signups
      // as one person — so they can't be double-booked into one group.
      const personId = member ? 'm:' + member.id
                     : s.userId ? 'd:' + s.userId
                     : 'u:' + s.name.toLowerCase();
      let p = persons.get(personId);
      if(!p){
        p = {
          personId,
          member,
          name: member ? (member.nickname || member.displayName || member.discordUsername || s.name) : s.name,
          status: s.status || 'active',
          sources: [],
          signups: []
        };
        persons.set(personId, p);
      }
      p.status = mergeStatus(p.status, s.status || 'active');
      if(!p.sources.includes(source)) p.sources.push(source);
      p.signups.push(s);
    });
  });

  const chips = [];
  let bothCount = 0, unlinkedCount = 0, noMainCount = 0;
  persons.forEach(p => {
    if(!p.member){
      unlinkedCount++;
      // No roster to expand, so they get the characters they actually signed up
      // with — one each when they signed up to both raids.
      if(p.sources.length > 1) bothCount++;
      p.signups.forEach(s => chips.push(toChip(s, p, 'UNLINKED')));
      return;
    }
    const characters = p.member.characters || [];
    if(p.sources.length > 1){
      bothCount++;
      // Every character they have, so the officer chooses which raid gets which.
      const all = characters.length ? characters : p.signups;
      all.forEach(c => chips.push(toChip(c, p, c.isMain ? 'MAIN' : 'ALT')));
      return;
    }
    const main = characters.find(c => c.isMain);
    if(main){
      chips.push(toChip(main, p, 'MAIN'));
    } else {
      noMainCount++;
      chips.push(toChip(p.signups[0], p, 'NO MAIN'));
    }
  });

  pool = chips;
  groups = { 1: [], 2: [] };
  itemCheck = [];        // the old results point at chips that no longer exist

  const notes = [];
  if(bothCount) notes.push(`${bothCount} signed up to both`);
  if(byDiscordCount) notes.push(`${byDiscordCount} matched by Discord id`);
  if(noMainCount) notes.push(`${noMainCount} with no main on the roster`);
  if(unlinkedCount) notes.push(`${unlinkedCount} not linked to a member`);
  setStatus(`${persons.size} raiders, ${chips.length} characters in the pool` +
            (notes.length ? ` — ${notes.join(', ')}.` : '.'));
}

/* ───────────────────────── Loading the two signups ───────────────────────── */

function reportError(err, notFoundMsg){
  console.error('Groups page call failed:', err);
  if(err.status === 401){ setStatus('Session expired — signing you out…', true); setTimeout(logout, 1200); return; }
  if(err.status === 403){ setStatus('Unauthorized — officer access, and the Raid-Helper token, are needed.', true); return; }
  if(err.status === 404){ setStatus(notFoundMsg, true); return; }
  setStatus(err.message + ' See the browser console.', true);
}

async function loadEvents(){
  setStatus('Loading events…');
  let events;
  try {
    events = await RH.listEvents();
  } catch(err){
    reportError(err, 'Server not found — check RH.SERVER_ID in menu.js.');
    return;
  }
  if(!events.length){
    setStatus(events.total
      ? `No recent or upcoming events (server has ${events.total} total).`
      : 'No events found on that server.', true);
    return;
  }

  const options = events.map(ev => {
    const id = ev.id || ev.eventId || '';
    const when = RH.fmtEventDate(ev);
    const title = String(ev.title || 'Untitled event').replace(/</g, '&lt;');
    return `<option value="${id}">${when ? when + ' — ' : ''}${title}</option>`;
  }).join('');

  const a = document.getElementById('rhEventA');
  const b = document.getElementById('rhEventB');
  a.innerHTML = options;
  b.innerHTML = options;
  // Two different events by default — the two most recent, oldest of the pair first.
  a.selectedIndex = Math.min(1, events.length - 1);
  b.selectedIndex = 0;
  document.getElementById('rhEventPickRow').style.display = 'flex';
  setStatus(`Found ${events.length} recent/upcoming event${events.length===1?'':'s'} — pick the two raids and load their signups.`);
}

async function loadSignups(){
  const a = document.getElementById('rhEventA');
  const b = document.getElementById('rhEventB');
  const idA = a && a.value, idB = b && b.value;
  if(!idA || !idB){ setStatus('Pick both signups first.', true); return; }
  if(idA === idB){ setStatus('Those are the same event — pick the two different raids.', true); return; }

  setStatus('Loading both signups and the roster…');
  let evA, evB, members;
  try {
    [evA, evB, members] = await Promise.all([
      RH.fetchEvent(idA),
      RH.fetchEvent(idB),
      fetchMembers()
    ]);
  } catch(err){
    reportError(err, 'One of those events is gone (it may have been deleted).');
    return;
  }

  const signupsA = RH.mapSignups(evA.signUps || evA.signups || [], makeChipId);
  const signupsB = RH.mapSignups(evB.signUps || evB.signups || [], makeChipId);
  if(!signupsA.length && !signupsB.length){ setStatus('Neither event has any usable signups.', true); return; }

  picked = {
    a: { id: idA, title: evA.title || 'event ' + idA },
    b: { id: idB, title: evB.title || 'event ' + idB }
  };
  buildPool(signupsA, signupsB, members);
  save();
  renderAll();
}

// The roster links, so two characters can be recognised as one raider.
async function fetchMembers(){
  let res;
  try {
    res = await fetch(API_BASE + '/api/members', { headers: RH.headers() });
  } catch(err){
    const e = new Error('Could not reach the API — is the backend up and is this origin allowed?');
    e.cause = err;
    throw e;
  }
  if(!res.ok){
    const e = new Error('The roster returned HTTP ' + res.status + '.');
    e.status = res.status;
    throw e;
  }
  return res.json();
}

/* ───────────────────────── Item check ─────────────────────────
   "Who has Dragonspine Trophy, and which group are they in?" — the question an
   officer asks while balancing two raids. GET /api/items/list is every item the
   guild is currently wearing, each with its wearers, drawn from each character's
   latest gear snapshot. We ask for the whole list once and match the wanted
   names in the browser: the single-item route resolves a name through the loot
   log, so an item nobody was ever *awarded* through the log wouldn't be found
   that way even when half the raid is wearing it.

   Matches are stored on the chips themselves, so they survive a refresh and
   follow a raider as they're dragged between groups. */

let itemCheck = [];   // [{ id, name, wearers:[chipId], others:n }]

// A short pill for a chip: initials, so "Dragonspine Trophy" reads "DT".
function itemAbbrev(name){
  const initials = String(name).split(/\s+/).filter(Boolean).map(w => w[0].toUpperCase()).join('');
  return initials.slice(0, 3) || '?';
}

async function checkItems(){
  const raw = document.getElementById('itemCheckInput').value || '';
  const wanted = raw.split(',').map(s => s.trim()).filter(Boolean);
  if(!wanted.length){ setStatus('Type one or more item names to check.', true); return; }
  if(!pool.length){ setStatus('Load the two signups first — there is nobody to check.', true); return; }

  setStatus('Checking who has ' + wanted.join(', ') + '…');
  let rows;
  try {
    rows = await fetchEquippedItems();
  } catch(err){
    reportError(err, 'The item list is empty — has an attendance import run yet?');
    return;
  }

  // Character name → the wanted items they're wearing.
  const byCharacter = new Map();
  const missing = [];
  itemCheck = [];

  wanted.forEach(want => {
    const lower = want.toLowerCase();
    // Exact name first; a substring match is the forgiving fallback for a
    // half-remembered name ("dragonspine").
    const row = rows.find(r => String(r.name || '').toLowerCase() === lower)
             || rows.find(r => String(r.name || '').toLowerCase().includes(lower));
    if(!row){ missing.push(want); return; }

    const wearers = [];
    let others = 0;
    (row.equipped || []).forEach(e => {
      const key = String(e.name || '').toLowerCase();
      const chip = pool.find(c => c.name.toLowerCase() === key);
      if(!chip){ others++; return; }
      wearers.push(chip.id);
      if(!byCharacter.has(key)) byCharacter.set(key, []);
      byCharacter.get(key).push(row.name);
    });
    itemCheck.push({ id: row.id, name: row.name, wearers, others });
  });

  pool.forEach(c => { c.items = byCharacter.get(c.name.toLowerCase()) || []; });

  const found = itemCheck.reduce((n, i) => n + i.wearers.length, 0);
  setStatus(`${found} raider${found===1?'':'s'} in these signups ${found===1?'has':'have'} one of those items` +
            (missing.length ? ` — nothing found for ${missing.join(', ')}.` : '.'),
            !!missing.length && !found);
  save();
  renderAll();
}

// Every item the guild is wearing. Session-gated (not officer-gated) like the
// rest of the item pages, so this is the same call items.html makes.
async function fetchEquippedItems(){
  let res;
  try {
    res = await fetch(API_BASE + '/api/items/list', { headers: RH.headers() });
  } catch(err){
    const e = new Error('Could not reach the API — is the backend up and is this origin allowed?');
    e.cause = err;
    throw e;
  }
  if(!res.ok){
    const e = new Error('The item list returned HTTP ' + res.status + '.');
    e.status = res.status;
    throw e;
  }
  return res.json();
}

// Where a chip currently sits, in words.
function whereIs(id){
  if(groups[1].includes(id)) return 'Group 1';
  if(groups[2].includes(id)) return 'Group 2';
  return 'Pool';
}

function renderItemCheck(){
  const el = document.getElementById('itemCheckResult');
  if(!itemCheck.length){ el.innerHTML = ''; return; }

  el.innerHTML = itemCheck.map(item => {
    const buckets = { 'Group 1': [], 'Group 2': [], 'Pool': [] };
    item.wearers.forEach(id => {
      const chip = chipById(id);
      if(chip) buckets[whereIs(id)].push(chip);
    });
    const lines = Object.keys(buckets).map(where => {
      const chips = buckets[where];
      if(!chips.length) return '';
      const names = chips.map(c => {
        // Whose alt it is, unless the raider is known by that character's name anyway.
        const who = c.personName && c.personName !== c.name
          ? `<span class="item-check-who">${whEsc(c.personName)}</span>` : '';
        return `<span style="color:${RH.CLASS_COLORS[c.cls] || 'var(--text)'}">${whEsc(c.name)}</span>${who}`;
      }).join(', ');
      return `<div class="item-check-row"><span class="item-check-where">${where}</span> <b>${chips.length}</b> ${names}</div>`;
    }).join('');

    const none = item.wearers.length ? '' : '<div class="item-check-row"><span class="item-check-where">—</span> nobody in these signups has it.</div>';
    const others = item.others
      ? `<div class="item-check-row item-check-others">${item.others} other guild character${item.others===1?'':'s'} ${item.others===1?'has':'have'} it, not signed up to either raid.</div>`
      : '';
    return `<div class="item-check-item"><div class="item-check-name">${itemLink(item.id, item.name)}</div>${lines}${none}${others}</div>`;
  }).join('');

  loadWowhead();   // tooltips on the item names we just wrote
}

/* ───────────────────────── Auto-allocate ─────────────────────────
   Deal the confirmed raiders across both groups so the two raids look alike:
   tanks first, then healers, ranged and melee, and inside each of those the
   classes are spread rather than clumped. A chip only lands in a group that has
   room and doesn't already hold that person, so the one-per-group rule shapes
   the split instead of being checked afterwards. */

function bucketOf(chip){
  if(RH.isTank(chip)) return 'tank';
  if(RH.isHealer(chip)) return 'healer';
  if(RH.isRanged(chip)) return 'ranged';
  return 'melee';
}

// Order a bucket so no two of the same class are dealt back to back: take one
// from each class in turn until every class list is empty.
function interleaveByClass(chips){
  const byClass = new Map();
  chips.forEach(c => {
    const k = c.cls || 'unknown';
    if(!byClass.has(k)) byClass.set(k, []);
    byClass.get(k).push(c);
  });
  // Biggest class first, so the class most at risk of clumping gets spread widest.
  const lists = [...byClass.values()].sort((x, y) => y.length - x.length);
  const out = [];
  for(let i = 0; out.length < chips.length; i++){
    lists.forEach(l => { if(i < l.length) out.push(l[i]); });
  }
  return out;
}

function countIn(group, pred){
  return groups[group].map(chipById).filter(c => c && pred(c)).length;
}

function autoAllocate(){
  groups = { 1: [], 2: [] };
  const candidates = pool.filter(c => (c.status || 'active') === 'active');
  let stranded = 0;

  ['tank','healer','ranged','melee'].forEach(bucket => {
    const chips = interleaveByClass(candidates.filter(c => bucketOf(c) === bucket));
    chips.forEach(chip => {
      const eligible = [1,2].filter(g =>
        groups[g].length < groupSize && !clashIn(g, chip));
      if(!eligible.length){ stranded++; return; }
      // Fewest of this bucket, then of this class, then overall; Group 1 breaks ties.
      eligible.sort((x, y) =>
        countIn(x, c => bucketOf(c) === bucket) - countIn(y, c => bucketOf(c) === bucket) ||
        countIn(x, c => c.cls === chip.cls) - countIn(y, c => c.cls === chip.cls) ||
        groups[x].length - groups[y].length ||
        x - y);
      groups[eligible[0]].push(chip.id);
    });
  });

  const waiting = pool.length - groups[1].length - groups[2].length;
  setStatus(`Auto-allocated — Group 1: ${groups[1].length}, Group 2: ${groups[2].length}.` +
            (stranded ? ` ${stranded} had no group left with a free slot and nobody of theirs already in it.` : '') +
            (waiting ? ` ${waiting} still in the pool.` : ''));
  save();
  renderAll();
}

/* ───────────────────────── Rendering ───────────────────────── */

// Chips light up their clash while being dragged, so a refused drop is visible
// before it happens rather than as an error afterwards.
function wireDragFeedback(el){
  el.querySelectorAll('.chip').forEach(c => {
    c.addEventListener('dragstart', () => {
      draggingId = c.dataset.id;
      const chip = chipById(draggingId);
      if(!chip) return;
      [1,2].forEach(g => {
        const list = document.getElementById('groupList' + g);
        list.classList.toggle('clash', !!clashIn(g, chip));
        list.classList.toggle('full', groups[g].length >= groupSize && !groups[g].includes(chip.id));
      });
    });
    c.addEventListener('dragend', clearDragFeedback);
    c.addEventListener('dblclick', () => sendToPool(c.dataset.id));
  });
}
// Pin an item pill onto every chip whose character is wearing one. Done after
// the markup is written rather than inside RH.chipHTML: the item check is this
// page's concern, not the board's.
function decorateItems(el){
  el.querySelectorAll('.chip').forEach(c => {
    const chip = chipById(c.dataset.id);
    if(!chip || !chip.items || !chip.items.length) return;
    c.insertAdjacentHTML('beforeend', chip.items.map(name =>
      `<span class="stag item" title="${whEsc(name)}">${whEsc(itemAbbrev(name))}</span>`).join(''));
  });
}

function clearDragFeedback(){
  draggingId = '';
  [1,2].forEach(g => {
    const list = document.getElementById('groupList' + g);
    list.classList.remove('clash', 'full');
  });
}

function renderPool(){
  const placed = placedIds();
  const avail = pool.filter(c => !placed.has(c.id));
  const reserve = avail.filter(c => c.status === 'tentative' || c.status === 'bench');
  const active = avail.filter(c => !c.status || c.status === 'active');

  const wrap = document.getElementById('reserveWrap');
  const rEl = document.getElementById('reservePool');
  if(reserve.length){
    wrap.style.display = '';
    rEl.innerHTML = reserve.map(RH.chipHTML).join('');
    RH.wirePoolDrag(rEl, sendToPool);
    wireDragFeedback(rEl);
    decorateItems(rEl);
  } else {
    wrap.style.display = 'none';
    rEl.innerHTML = '';
  }

  const el = document.getElementById('groupPool');
  el.innerHTML = active.length
    ? active.map(RH.chipHTML).join('')
    : '<span class="pool-empty">' + (pool.length ? 'Everyone is in a group.' : 'No signups loaded yet — load the two events above.') + '</span>';
  RH.wirePoolDrag(el, sendToPool);
  wireDragFeedback(el);
  decorateItems(el);
}

function renderGroup(g){
  const chips = groups[g].map(chipById).filter(Boolean);
  document.getElementById('groupCount' + g).textContent = chips.length + '/' + groupSize;

  const tally = ['tank','healer','ranged','melee']
    .map(b => `<span>${b === 'tank' ? 'Tanks' : b === 'healer' ? 'Healers' : b === 'ranged' ? 'Ranged' : 'Melee'} <b>${chips.filter(c => bucketOf(c) === b).length}</b></span>`)
    .join('');
  document.getElementById('groupTally' + g).innerHTML = tally;

  const list = document.getElementById('groupList' + g);
  list.innerHTML = chips.length
    ? chips.map(RH.chipHTML).join('')
    : '<span class="pool-empty">Drag raiders here.</span>';
  RH.wirePoolDrag(list, id => place(id, g));
  wireDragFeedback(list);
  decorateItems(list);
}

function renderAll(){
  const label = document.getElementById('loadedEvents');
  label.textContent = picked.a && picked.b
    ? `Raid 1: ${picked.a.title}  ·  Raid 2: ${picked.b.title}`
    : '';
  document.getElementById('countGroup').textContent = groupSize;
  renderPool();
  renderGroup(1);
  renderGroup(2);
  renderItemCheck();   // last: the summary reads where everyone ended up
}

/* ───────────────────────── Persistence ─────────────────────────
   Browser-local only: an officer's half-finished split survives a refresh, but
   it isn't shared with anyone else. */
function save(){
  try{
    localStorage.setItem(STORE_KEY, JSON.stringify({ pool, groups, groupSize, picked, chipSeq, itemCheck }));
  }catch(e){}
}

function restore(){
  let saved = null;
  try{ saved = JSON.parse(localStorage.getItem(STORE_KEY) || 'null'); }catch(e){}
  if(!saved || !Array.isArray(saved.pool)) return false;
  pool = saved.pool;
  groups = { 1: (saved.groups && saved.groups[1]) || [], 2: (saved.groups && saved.groups[2]) || [] };
  groupSize = saved.groupSize || 25;
  picked = saved.picked || { a: null, b: null };
  chipSeq = saved.chipSeq || pool.length;
  itemCheck = Array.isArray(saved.itemCheck) ? saved.itemCheck : [];
  return true;
}

if(restore()) setStatus('Picked up where you left off. Load events again to start from fresh signups.');
renderAll();
