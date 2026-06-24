"use strict";
/* The inspector runs as a self-contained module (window.Inspector) so it can
   share a page with the playback module without their globals colliding. */
(function(){
/* =====================================================================
   FFXIVReplay .dat — format constants (from the FFXIVReplay struct)
   Header        : 0x68 bytes
   ChapterArray  : 0x4 + 0xC*64 bytes
   Chapter       : int type; uint offset; uint ms      (0xC bytes)
   data starts at: 0x68 + 0x304 = 0x36C
   DataSegment   : u16 opcode; u16 dataLength; u32 ms; u32 objectID (12) + payload
   ===================================================================== */
const HEADER_SIZE = 0x68;
const CHAPTER_ENTRY = 0xC;
const MAX_CHAPTERS = 64;
const CHAPTER_ARRAY = 0x4 + CHAPTER_ENTRY * MAX_CHAPTERS; // 0x304
const DATA_START = HEADER_SIZE + CHAPTER_ARRAY;           // 0x36C
const SEG_HEADER = 12;

const OFF_REPLAY_LEN = 0x48;
const OFF_TOTAL_MS = 0x18;
const OFF_DISPLAYED_MS = 0x1C;
const OFF_TIMESTAMP = 0x14;
const OFF_VERSION = 0x0C;
const OFF_OSTYPE = 0x0E;
const OFF_BUILD = 0x10;
const OFF_CONTENTID = 0x20;
const OFF_INFO = 0x28;
const OFF_LOCALCID = 0x30;
const OFF_JOBS = 0x38;
const OFF_PLAYERINDEX = 0x40;
/* Per-patch opcode tables + current-build constants live in opcodes.js
   (loaded before this file). */

const PULL_START_TYPES = [2,5];
/* SPAWN/WAYMARK/WAYMARK_PRESET are re-resolved per loaded file's patch in parse()
   (see resolveOpcodes) so the tool reads old recordings correctly. DIRECTOR has no
   named entry in FFXIVOpcodes, so it stays a fixed fallback. */
const DIRECTOR_OPCODE = 0x03E4;
let SPAWN_OPCODE = 0x0113;          // NpcSpawn
let WAYMARK_OPCODE = 0x0255;        // PlaceFieldMarker
let WAYMARK_PRESET_OPCODE = 0x02AB; // PlaceFieldMarkerPreset
/* Opcodes for combat timing (resolved per patch in resolveOpcodes()).
   FirstAttack fires on the first hit against the boss — the real engage — which
   is what starts the combat timer. COMBAT_OPS (casts/effects) mark combat
   actions; their last one ends combat, and their first one is the fallback start
   for the rare pull whose engage had no fresh FirstAttack. */
let FIRST_ATTACK_OPCODE = 0;
let COMBAT_OPS = new Set();
/* Chapter types that mark a countdown (1 = Countdown, 3 = Countdown(3)). */
const COUNTDOWN_CHAPTER_TYPES = [1,3];
/* Real combat is continuous (an action every couple seconds), so we split combat
   actions into clusters separated by idle gaps longer than this. A tiny trailing
   cluster (fewer actions than COMBAT_MIN_CLUSTER) after such a gap is post-fight
   noise — a DoT tick or stray cast seconds after the boss died / the party wiped —
   and is trimmed; a real combat segment is always far denser. */
const COMBAT_GAP_MS = 10000;
const COMBAT_MIN_CLUSTER = 8;
const BATCH_LOOKBACK = 8000;
const BATCH_MS_WINDOW = 2000;
const MIN_BATCH_SPAWNS = 20;

const MAGIC = [70,70,88,73,86,82,69,80,76,65,89,0]; // "FFXIVREPLAY\0"
const CHAPTER_TYPE_NAMES = {1:"Countdown",2:"Start/Restart",3:"Countdown(3)",4:"Event Cutscene",5:"Barrier Down"};
/* A small, partial Job-ID → abbreviation map for display only. */
const JOB_ABBR = {0:"—",1:"GLA",2:"PGL",3:"MRD",4:"LNC",5:"ARC",6:"CNJ",7:"THM",
  19:"PLD",20:"MNK",21:"WAR",22:"DRG",23:"BRD",24:"WHM",25:"BLM",26:"ACN",27:"SMN",28:"SCH",
  29:"ROG",30:"NIN",31:"MCH",32:"DRK",33:"AST",34:"SAM",35:"RDM",36:"BLU",37:"GNB",38:"DNC",
  39:"RPR",40:"SGE",41:"VPR",42:"PCT"};

/* ---- app state ---- */
let raw = null;          // Uint8Array of the loaded file
let dv = null;           // DataView over raw
let fileName = "";
let segs = [];           // {offset,opcode,dataLength,ms,oid}
let chapters = [];       // {type,offset,ms}
let pulls = [];          // pull chapters with computed ranges
let players = [];        // {name, offsets:[...], jobIndex}
let selectedPull = -1;
let lastGhostsDropped = 0; // stale instance-load duplicates removed by the last buildPull

/* ---- byte helpers (little-endian, like the game) ---- */
const u16=(o)=>dv.getUint16(o,true);
const u32=(o)=>dv.getUint32(o,true);
const i32=(o)=>dv.getInt32(o,true);

function decodeName(off){ // null-terminated within 32-byte field
  let end=off; while(end<off+32 && raw[end]!==0) end++;
  return new TextDecoder().decode(raw.subarray(off,end));
}

/* =====================================================================
   Opcodes: per-patch resolution + transpose
   ===================================================================== */
let fileBuild=0, filePatch=null;   // set by resolveOpcodes() from the loaded file

// Point the tool's parsing opcodes at the loaded file's patch (falls back to defaults).
function resolveOpcodes(build){
  fileBuild=build;
  filePatch=BUILD_TO_PATCH[build]||null;
  const t = filePatch ? OPCODE_TABLES[filePatch] : null;
  if(t){
    if(t.NpcSpawn!=null) SPAWN_OPCODE=t.NpcSpawn;
    if(t.PlaceFieldMarker!=null) WAYMARK_OPCODE=t.PlaceFieldMarker;
    if(t.PlaceFieldMarkerPreset!=null) WAYMARK_PRESET_OPCODE=t.PlaceFieldMarkerPreset;
  } else {
    // unknown build: keep the latest-patch defaults
    SPAWN_OPCODE=0x0113; WAYMARK_OPCODE=0x0255; WAYMARK_PRESET_OPCODE=0x02AB;
  }
  COMBAT_OPS=new Set();
  for(const name of ["ActorCast","Effect","AoeEffect8","AoeEffect16","AoeEffect24","AoeEffect32"]){
    if(t && t[name]!=null) COMBAT_OPS.add(t[name]);
  }
  FIRST_ATTACK_OPCODE = (t && t.FirstAttack!=null) ? t.FirstAttack : 0;
}

// Build an old->new opcode map by matching IPC names between two patch tables.
function opcodeRemap(fromPatch,toPatch){
  const from=OPCODE_TABLES[fromPatch], to=OPCODE_TABLES[toPatch];
  if(!from||!to) return null;
  const map=new Map();
  for(const name in from){ if(to[name]!=null && from[name]!==to[name]) map.set(from[name],to[name]); }
  return map;
}

// Rewrite every segment opcode in a finished export buffer from its patch to LATEST_PATCH.
// Returns coverage info so the UI can be honest about how complete the remap is.
function transposeOpcodes(bytes){
  if(!filePatch) return {ok:false,reason:`no opcode table for build ${fileBuild}`};
  if(filePatch===LATEST_PATCH) return {ok:false,reason:"already on the latest patch"};
  const map=opcodeRemap(filePatch,LATEST_PATCH);
  if(!map) return {ok:false,reason:"missing patch table"};
  const dvb=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);
  const replayLen=dvb.getInt32(OFF_REPLAY_LEN,true);
  const named=new Set(Object.values(OPCODE_TABLES[filePatch]));
  let off=0, rewritten=0, segTotal=0, unknownSegs=0; const unknownKinds=new Set();
  while(off<replayLen){
    const b=DATA_START+off;
    const op=dvb.getUint16(b,true), len=dvb.getUint16(b+2,true);
    segTotal++;
    if(map.has(op)) dvb.setUint16(b,map.get(op),true), rewritten++;
    else if(op<0xf000 && !named.has(op)){ unknownSegs++; unknownKinds.add(op); } // not an IPC name we know
    off+=SEG_HEADER+len;
  }
  return {ok:true, from:filePatch, to:LATEST_PATCH, rewritten, segTotal, unknownSegs, unknownKinds:unknownKinds.size};
}

/* =====================================================================
   Parse
   ===================================================================== */
function parse(buffer){
  raw = new Uint8Array(buffer);
  dv = new DataView(raw.buffer);
  for(let i=0;i<MAGIC.length;i++) if(raw[i]!==MAGIC[i]) throw new Error("Not an FFXIVREPLAY .dat (bad header).");

  resolveOpcodes(i32(OFF_BUILD));

  const replayLength = i32(OFF_REPLAY_LEN);

  // walk segments
  segs=[]; let off=0;
  while(off < replayLength){
    const b = DATA_START+off;
    const opcode=u16(b), dataLength=u16(b+2), ms=u32(b+4), oid=u32(b+8);
    segs.push({offset:off,opcode,dataLength,ms,oid,total:SEG_HEADER+dataLength});
    off += SEG_HEADER+dataLength;
  }

  // chapters
  chapters=[]; const clen=i32(HEADER_SIZE);
  for(let i=0;i<clen;i++){
    const e=HEADER_SIZE+4+i*CHAPTER_ENTRY;
    chapters.push({type:i32(e),offset:u32(e+4),ms:u32(e+8)});
  }

  // pull chapters with ranges
  const o2i=new Map(segs.map((s,i)=>[s.offset,i]));
  const chapIndex=new Map(chapters.map((c,i)=>[c,i]));
  const pullChapters=chapters.filter(c=>PULL_START_TYPES.includes(c.type));
  pulls = pullChapters.map((pc,n)=>{
    const startIndex=o2i.get(pc.offset);
    const endIndex = (n<pullChapters.length-1) ? o2i.get(pullChapters[n+1].offset) : segs.length;
    const lastMs = endIndex>startIndex ? segs[endIndex-1].ms : pc.ms;
    const respawnStart=findRespawnBatchStart(startIndex);
    const batchCount=countSpawns(respawnStart,startIndex);
    // Cap combat at the wipe: when the party dies the arena resets (mass despawn
    // then re-spawn for the next attempt). Post-wipe DoT ticks and the reset's own
    // spawn effects keep firing for several seconds after — and run almost to the
    // restart — so we end combat at that reset (the next pull's respawn batch).
    const combatEnd = (n<pullChapters.length-1) ? findRespawnBatchStart(endIndex) : endIndex;
    const combat=combatSpan(startIndex,combatEnd);
    const nextMs = (n<pullChapters.length-1) ? pullChapters[n+1].ms : Infinity;
    let countdown=findCountdownChapter(pc,nextMs);
    let countdownIndex=(countdown && o2i.has(countdown.offset)) ? o2i.get(countdown.offset) : -1;
    if(countdownIndex<0) countdown=null; // no segment to anchor to -> can't keep it
    return {chapter:pc,n:n+1,startIndex,endIndex,lengthMs:Math.max(0,lastMs-pc.ms),
            respawnStart,batchCount,combatMs:combat.ms,countdown,countdownIndex};
  });

  // players: scan 32-byte name fields
  players = findPlayers();

  return {replayLength};
}

function findRespawnBatchStart(pullIndex){
  const lo=Math.max(0,pullIndex-BATCH_LOOKBACK);
  const spawns=[];
  for(let i=lo;i<pullIndex;i++) if(segs[i].opcode===SPAWN_OPCODE) spawns.push(i);
  if(spawns.length===0) return pullIndex;
  const clusters=[]; let cur=[spawns[0]];
  for(let k=1;k<spawns.length;k++){
    const i=spawns[k];
    if(segs[i].ms-segs[cur[cur.length-1]].ms<=BATCH_MS_WINDOW) cur.push(i);
    else{clusters.push(cur);cur=[i];}
  }
  clusters.push(cur);
  let chosen=null;
  for(let c of clusters) if(c.length>=MIN_BATCH_SPAWNS) chosen=c;
  if(!chosen) chosen=clusters[clusters.length-1];
  return Math.min(...chosen);
}
function countSpawns(a,b){let n=0;for(let i=a;i<b;i++)if(segs[i].opcode===SPAWN_OPCODE)n++;return n;}

/* Actual combat time within a pull: the real engage to the last combat action.
   The engage is the first FirstAttack (first hit on the boss), which excludes the
   countdown, run-in and pre-pull casts. If a pull's engage produced no fresh
   FirstAttack (a wipe-recovery re-pull) its first FirstAttack is actually a late
   add — detected by lots of combat already having happened before it — so we fall
   back to the first combat action there. */
function combatSpan(startIndex,endIndex){
  const actMs=[]; const faMarks=[];
  for(let i=startIndex;i<endIndex;i++){
    const op=segs[i].opcode;
    if(op===FIRST_ATTACK_OPCODE) faMarks.push({ms:segs[i].ms,before:actMs.length});
    else if(COMBAT_OPS.has(op)) actMs.push(segs[i].ms);
  }
  if(actMs.length===0) return {ms:0};
  // engage = first FirstAttack with <15% of the pull's combat actions before it
  let startMs=actMs[0];
  for(const m of faMarks){ if(m.before < actMs.length*0.15){ startMs=m.ms; break; } }
  // end = drop trailing post-fight noise: peel off small gap-separated clusters
  // (DoT ticks / stray casts after the kill or wipe) until reaching the dense
  // combat. A mid-fight intermission gap is followed by a large cluster, so the
  // real fight is never trimmed.
  let end=actMs.length-1;
  while(end>0){
    let cs=end; // start of the cluster ending at `end`
    while(cs>0 && actMs[cs]-actMs[cs-1] <= COMBAT_GAP_MS) cs--;
    if(cs>0 && (end-cs+1) < COMBAT_MIN_CLUSTER) end=cs-1; // trailing noise cluster
    else break;
  }
  return {ms:Math.max(0,actMs[end]-startMs)};
}

/* The countdown chapter that belongs to a pull. Despite the name, the game logs
   a type-1 "Countdown" chapter at the *engage* — the moment the countdown ends
   and the boss fight starts (FFXIVClientStructs: Countdown = "Start of boss
   fight"). It therefore sits just *after* the pull's Start/Restart chapter, not
   before it. So it's the first Countdown chapter strictly after this pull's
   start and before the next pull begins. Returns the chapter, or null. */
function findCountdownChapter(pullChapter,nextMs){
  for(const c of chapters){
    if(c.ms<=pullChapter.ms) continue;
    if(c.ms>=nextMs) break;
    if(COUNTDOWN_CHAPTER_TYPES.includes(c.type)) return c;
  }
  return null;
}

function findPlayers(){
  // a 32-byte field: "First Last\0" + null padding, two cap-initial parts
  const found=new Map(); const order=[];
  const isUpper=(b)=>b>=65&&b<=90;
  // digits are allowed so the scanner reads our own anonymized "Player N" fields
  // to the end; looksLikeName() still gates what actually counts as a name.
  const isNameChar=(b)=>(b>=65&&b<=90)||(b>=97&&b<=122)||(b>=48&&b<=57)||b===32||b===39||b===45;
  for(let i=0;i+32<=DATA_START+segDataBytes();i++){
    if(!isUpper(raw[i])) continue;
    let len=0; while(len<32 && isNameChar(raw[i+len])) len++;
    if(len===0||len>31) continue;
    let padded=true; for(let j=len;j<32;j++){if(raw[i+j]!==0){padded=false;break;}}
    if(!padded) continue;
    const s=new TextDecoder().decode(raw.subarray(i,i+len));
    if(!looksLikeName(s)) continue;
    if(!found.has(s)){found.set(s,[]);order.push(s);}
    found.get(s).push(i);
  }
  // map to job via header jobs[] using player order in header isn't reliable; show job by index
  return order.map((name,idx)=>({name,offsets:found.get(name),jobIndex:idx}));
}
function segDataBytes(){return i32(OFF_REPLAY_LEN);}
function looksLikeName(s){
  if(/^Player \d{1,3}$/.test(s)) return true; // anonymized names this tool writes
  const parts=s.split(" ");
  if(parts.length!==2) return false;
  for(const p of parts){
    if(p.length<2||p.length>15) return false;
    if(!(p[0]>="A"&&p[0]<="Z")) return false;
    for(const c of p){const ok=/[A-Za-z'\-]/.test(c); if(!ok) return false;}
  }
  return true;
}

/* =====================================================================
   Render
   ===================================================================== */
function fmtClock(ms){
  let s=Math.floor(ms/1000), msec=ms%1000;
  const h=Math.floor(s/3600); s%=3600;
  const m=Math.floor(s/60); s%=60;
  const pad=(x,n=2)=>String(x).padStart(n,"0");
  return h>0?`${h}:${pad(m)}:${pad(s)}`:`${pad(m)}:${pad(s)}.${pad(msec,3)}`;
}
function fmtBytes(n){return n<1024?`${n} B`:n<1048576?`${(n/1024).toFixed(0)} KB`:`${(n/1048576).toFixed(1)} MB`;}

function renderHeader(){
  const ts=u32(OFF_TIMESTAMP);
  const info=raw[OFF_INFO];
  const flags=[]; if(info&1)flags.push("up-to-date"); if(info&2)flags.push("locked"); if(info&4)flags.push("completed");
  const localCID = dv.getBigUint64(OFF_LOCALCID,true);
  const jobs=[]; for(let i=0;i<8;i++) jobs.push(raw[OFF_JOBS+i]);
  const playerIndex=raw[OFF_PLAYERINDEX];
  const cells=[
    ["format version", u16(OFF_VERSION), ""],
    ["os", u16(OFF_OSTYPE)===3?"Windows":u16(OFF_OSTYPE)===5?"Mac":u16(OFF_OSTYPE), ""],
    ["game build", i32(OFF_BUILD)===LATEST_GAME_BUILD ? i32(OFF_BUILD) : `${i32(OFF_BUILD)} (outdated)`, i32(OFF_BUILD)===LATEST_GAME_BUILD ? "" : "amber"],
    ["recorded", new Date(ts*1000).toISOString().replace("T"," ").replace(/\..+/,"")+" UTC", "cyan"],
    ["content id", u16(OFF_CONTENTID), ""],
    ["total length", fmtClock(u32(OFF_TOTAL_MS)), "cyan"],
    ["info flags", flags.join(", ")||"none", ""],
    ["recorder", `player ${playerIndex+1}`, "amber"],
    ["jobs", jobs.map(j=>JOB_ABBR[j]||j).join(" "), ""],
    ["local CID", "0x"+localCID.toString(16), ""],
    ["replay length", fmtBytes(i32(OFF_REPLAY_LEN)), ""],
    ["segments", segs.length.toLocaleString(), ""],
  ];
  document.getElementById("readout").innerHTML = cells.map(([k,v,c])=>
    `<div class="cell"><div class="k">${k}</div><div class="v ${c}">${v}</div></div>`).join("");
  document.getElementById("h-file").textContent = fileName;
}

function renderTimeline(){
  const totalMs=u32(OFF_TOTAL_MS)||1;
  const axis=document.getElementById("tlaxis");
  axis.innerHTML="";
  const track=document.createElement("div"); track.className="tl-track"; axis.appendChild(track);

  // segments between consecutive pull starts
  pulls.forEach((p,idx)=>{
    const startMs=p.chapter.ms;
    const endMs = idx<pulls.length-1 ? pulls[idx+1].chapter.ms : totalMs;
    const left=(startMs/totalMs)*100, width=Math.max(0.4,((endMs-startMs)/totalMs)*100);
    const seg=document.createElement("div");
    seg.className="tl-seg"+(idx===selectedPull?" sel":"");
    seg.style.left=left+"%"; seg.style.width=width+"%";
    seg.style.background = idx===selectedPull?"var(--phosphor)":"var(--phosphor-deep)";
    seg.title=`Pull ${p.n} — ${fmtClock(startMs)}`;
    seg.onclick=()=>selectPull(idx);
    track.appendChild(seg);
    // tick every few pulls
    if(idx%3===0){
      const t=document.createElement("div"); t.className="tl-tick"; t.style.left=left+"%";
      t.textContent=fmtClock(startMs).replace(/\.\d+$/,""); axis.appendChild(t);
    }
  });

  // waymark placement flags
  segs.forEach(s=>{
    if(s.opcode===WAYMARK_PRESET_OPCODE || s.opcode===WAYMARK_OPCODE){
      // skip empty presets
      if(s.opcode===WAYMARK_PRESET_OPCODE && isEmptyPreset(s)) return;
      const f=document.createElement("div"); f.className="tl-flag wm";
      f.style.left=(s.ms/totalMs)*100+"%"; f.title="Waymark @ "+fmtClock(s.ms);
      axis.appendChild(f);
    }
  });

  document.getElementById("t-count").textContent=`${pulls.length} pulls · ${fmtClock(totalMs)}`;
}

function renderPullTable(){
  const tb=document.getElementById("pulltbody"); tb.innerHTML="";
  pulls.forEach((p,idx)=>{
    const tr=document.createElement("tr");
    if(idx===selectedPull) tr.className="sel";
    const cd = p.countdown ? `<span class="cd" title="engage (countdown chapter) ${fmtClock(p.countdown.ms-p.chapter.ms)} into this pull">⏱</span>` : "";
    tr.innerHTML=`<td class="num">${p.n}</td>
      <td>${CHAPTER_TYPE_NAMES[p.chapter.type]||p.chapter.type}${cd}</td>
      <td>${fmtClock(p.chapter.ms)}</td>
      <td class="dim">${fmtClock(p.lengthMs)}</td>
      <td class="num">${p.combatMs?fmtClock(p.combatMs):'<span class="dim">—</span>'}</td>
      <td class="dim">${p.batchCount} spawns</td>`;
    tr.onclick=()=>selectPull(idx);
    tb.appendChild(tr);
  });
}

function renderPlayers(){
  const wrap=document.getElementById("players"); wrap.innerHTML="";
  const recorderIdx=raw[OFF_PLAYERINDEX];
  players.forEach((p,idx)=>{
    const isRec = idx===recorderIdx;
    const div=document.createElement("div");
    div.className="pl"+(isRec?" rec":"");
    div.innerHTML=`<span class="idx">${idx+1}</span>
      <input type="text" value="${escapeHtml(p.name)}" maxlength="31" data-idx="${idx}">
      ${isRec?'<span class="reclabel">REC</span>':''}`;
    wrap.appendChild(div);
  });
  document.getElementById("pl-count").textContent=`${players.length} found`;
  wrap.querySelectorAll("input").forEach(inp=>{
    inp.addEventListener("input",e=>{
      players[+e.target.dataset.idx].newName=e.target.value;
      emitNames();
    });
  });
}
function escapeHtml(s){return s.replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));}

/* =====================================================================
   Select
   ===================================================================== */
function selectPull(idx){
  selectedPull=idx;
  renderTimeline(); renderPullTable();
  const p=pulls[idx];
  document.getElementById("pull-sel").textContent=`pull ${p.n} · ${CHAPTER_TYPE_NAMES[p.chapter.type]} · ${fmtClock(p.chapter.ms)}`;
  document.getElementById("btn-split").disabled=false;
  document.getElementById("export-hint").textContent=`Pull ${p.n} ready to export (opens at ${fmtClock(p.chapter.ms)}, ${p.batchCount} actors respawned).`;
}

/* =====================================================================
   Apply name edits (size-preserving, every occurrence)
   ===================================================================== */
function applyNameEdits(target){
  const enc=new TextEncoder();
  for(const p of players){
    if(p.newName===undefined || p.newName===p.name) continue;
    const nb=enc.encode(p.newName);
    if(nb.length>31){ throw new Error(`"${p.newName}" is ${nb.length} bytes (max 31).`); }
    for(const off of p.offsets){
      for(let k=0;k<32;k++) target[off+k]=0;        // clear the 32-byte field
      target.set(nb,off);                            // write new name
    }
  }
}

/* =====================================================================
   Split a single pull  (port of split_pulls_small.py)
   ===================================================================== */
function isEmptyPreset(s){
  const base=DATA_START+s.offset+SEG_HEADER;
  for(let i=0;i<96;i++) if(raw[base+i]!==0) return false;
  return true;
}
function segRaw(srcBytes,s){ const b=DATA_START+s.offset; return srcBytes.subarray(b,b+s.total); }
function rebasedSeg(srcBytes,s,newMs){
  const out=srcBytes.slice(DATA_START+s.offset, DATA_START+s.offset+s.total);
  const ms=Math.max(0,newMs);
  new DataView(out.buffer).setUint32(4,ms>>>0,true);
  return out;
}

function buildPull(idx,opts){
  // source bytes (optionally with name edits applied)
  let src=raw;
  if(opts.applyNames){
    src=raw.slice();             // copy
    applyNameEdits(src);
  }

  const p=pulls[idx];
  const pullIndex=p.startIndex, endIndex=p.endIndex;
  const pullStartMs=p.chapter.ms;

  // setup block: 0 .. director packet inclusive
  let directorIndex=segs.findIndex(s=>s.opcode===DIRECTOR_OPCODE);
  const setupEnd = directorIndex>=0 ? directorIndex+1 : pulls[0].startIndex;

  let carryStart=p.respawnStart;
  if(carryStart<setupEnd) carryStart=pullIndex;

  const anchorMs = pullStartMs; // timeline zero for the carried range

  // Keep this pull's countdown: the game's type-1 "Countdown" chapter marks the
  // engage (start of the boss fight), which sits inside the pull, just after the
  // Start/Restart. It's already within the carried range, so we don't move any
  // boundaries — we just emit a second chapter entry for it so the exported file
  // exposes the engage as a selectable chapter (jump straight to the fight).
  const cdOn = opts.countdown && p.countdownIndex>=pullIndex && p.countdownIndex<endIndex;
  const countdownIndex = cdOn ? p.countdownIndex : -1;

  // Instance-load duplicates: the setup block spawns every actor present at
  // zone-in. For pulls after the first, some of those (e.g. the boss's dormant
  // intro copy) are stale — the despawn/cleanup that removes them lives in the
  // gap between setup and the respawn batch, which this reconstruction drops, so
  // carrying their spawn leaves a frozen ghost next to the real, re-spawned actor.
  // Remove a setup NpcSpawn when the actor never appears in this pull AND a live
  // actor of the same model is spawned in the pull (i.e. it is a true duplicate).
  const pullOids=new Set(), liveModels=new Set();
  const npcModel=(s)=> s.dataLength>=0x48 ? dv.getUint32(DATA_START+s.offset+SEG_HEADER+0x44,true) : -1;
  for(let i=carryStart;i<endIndex;i++){
    pullOids.add(segs[i].oid);
    if(segs[i].opcode===SPAWN_OPCODE){ const m=npcModel(segs[i]); if(m>=0) liveModels.add(m); }
  }
  let ghostsDropped=0;

  const parts=[];

  // 1) setup, original ms (minus stale instance-load duplicates)
  for(let i=0;i<setupEnd;i++){
    const s=segs[i];
    if(s.opcode===SPAWN_OPCODE && !pullOids.has(s.oid)){
      const m=npcModel(s);
      if(m>=0 && liveModels.has(m)){ ghostsDropped++; continue; }
    }
    parts.push(segRaw(src,segs[i]));
  }

  // 2+3) [countdown/respawn .. next pull], rebased; inject waymarks at the pull start
  let chapterNewOffset=-1, countdownNewOffset=-1, written=byteLen(parts);
  for(let i=carryStart;i<endIndex;i++){
    if(i===countdownIndex) countdownNewOffset=byteLen(parts);
    if(i===pullIndex){
      // chapter points at the pull start (the waymark packets are emitted here at ms=0,
      // right before the pull's own first packet — same as the validated Python splitter)
      chapterNewOffset=byteLen(parts);
      if(opts.waymarks) injectWaymarks(src,parts,pullIndex);
    }
    parts.push(rebasedSeg(src,segs[i],segs[i].ms-anchorMs));
  }
  if(chapterNewOffset<0) chapterNewOffset=byteLen(parts);

  const body=concat(parts);
  const lastMs = endIndex>carryStart ? Math.max(0,segs[endIndex-1].ms-anchorMs):0;

  // header
  const header=src.slice(0,HEADER_SIZE);
  const hv=new DataView(header.buffer);
  hv.setInt32(OFF_REPLAY_LEN,body.length,true);
  hv.setUint32(OFF_TOTAL_MS,lastMs>>>0,true);
  hv.setUint32(OFF_DISPLAYED_MS,lastMs>>>0,true);

  // chapter array: the pull start, then the countdown/engage (if kept). Chapters
  // are ascending: the Start/Restart comes first, the engage a little later.
  const ca=new Uint8Array(CHAPTER_ARRAY);
  const cav=new DataView(ca.buffer);
  if(cdOn && countdownNewOffset>=0){
    cav.setInt32(0,2,true);
    cav.setInt32(4,p.chapter.type,true);                          // chapter[0] = start/restart
    cav.setUint32(8,chapterNewOffset>>>0,true);
    cav.setUint32(12,Math.max(0,pullStartMs-anchorMs)>>>0,true);
    cav.setInt32(4+CHAPTER_ENTRY,p.countdown.type,true);          // chapter[1] = countdown/engage
    cav.setUint32(8+CHAPTER_ENTRY,countdownNewOffset>>>0,true);
    cav.setUint32(12+CHAPTER_ENTRY,Math.max(0,p.countdown.ms-anchorMs)>>>0,true);
  } else {
    cav.setInt32(0,1,true);
    cav.setInt32(4,p.chapter.type,true);
    cav.setUint32(8,chapterNewOffset>>>0,true);
    cav.setUint32(12,0,true);
  }

  lastGhostsDropped=ghostsDropped;
  return concat([header,ca,body]);
}
function injectWaymarks(src,parts,pullIndex){
  const latestIndividual=new Map(); let latestPreset=null;
  for(let j=0;j<pullIndex;j++){
    const sj=segs[j];
    if(sj.opcode===WAYMARK_OPCODE){ latestIndividual.set(raw[DATA_START+sj.offset+SEG_HEADER],sj); }
    else if(sj.opcode===WAYMARK_PRESET_OPCODE && !isEmptyPreset(sj)){ latestPreset=sj; }
  }
  if(latestPreset){ parts.push(rebasedSeg(src,latestPreset,0)); }
  else { [...latestIndividual.keys()].sort((a,b)=>a-b).forEach(k=>parts.push(rebasedSeg(src,latestIndividual.get(k),0))); }
}
function byteLen(parts){let n=0;for(const p of parts)n+=p.length;return n;}
function concat(parts){const n=byteLen(parts);const out=new Uint8Array(n);let o=0;for(const p of parts){out.set(p,o);o+=p.length;}return out;}

/* =====================================================================
   Save / full-file rename
   ===================================================================== */
function buildRenamedFull(){
  const out=raw.slice();
  applyNameEdits(out);
  return out;
}
async function download(bytes,name){
  const blob=new Blob([bytes],{type:"application/octet-stream"});
  // Prefer the File System Access API so the user can pick where to save.
  if(window.showSaveFilePicker){
    try{
      const handle=await window.showSaveFilePicker({
        suggestedName:name,
        types:[{description:"Replay data",accept:{"application/octet-stream":[".dat"]}}],
      });
      const writable=await handle.createWritable();
      await writable.write(blob);
      await writable.close();
      return true;
    }catch(err){
      if(err && err.name==="AbortError") return false; // user cancelled
      // fall through to the download fallback on any other error
    }
  }
  const url=URL.createObjectURL(blob);
  const a=document.createElement("a"); a.href=url; a.download=name; a.click();
  setTimeout(()=>URL.revokeObjectURL(url),1000);
  return true;
}

/* =====================================================================
   Wire up
   ===================================================================== */
// Broadcast the current name map so the playback module can relabel dots live.
function emitNames(){
  const map={};
  for(const p of players){ map[p.name] = (p.newName!==undefined ? p.newName : p.name); }
  document.dispatchEvent(new CustomEvent("rw-names",{detail:map}));
}

function loadBytes(name, buffer){
  fileName=name;
  {
    try{
      parse(buffer);
      // waymark availability for the checkbox
      const hasWaymark = segs.some(s=>(s.opcode===WAYMARK_PRESET_OPCODE&&!isEmptyPreset(s))||s.opcode===WAYMARK_OPCODE);
      const wmCheck=document.getElementById("wm-check"), wm=document.getElementById("wm"), wmSub=document.getElementById("wm-sub");
      if(hasWaymark){ wmCheck.classList.remove("disabled"); wm.disabled=false; wmSub.textContent="Carry the last waymarks into the pull"; }
      else{ wmCheck.classList.add("disabled"); wm.checked=false; wm.disabled=true; wmSub.textContent="None captured in this file"; }

      // opcode transpose (which also re-stamps the build): only when we have a patch
      // table for this build and it isn't already the latest
      const curBuild=i32(OFF_BUILD);
      const tCheck=document.getElementById("transpose-check"), tBox=document.getElementById("transpose"),
            tSub=document.getElementById("transpose-sub");
      if(filePatch && filePatch!==LATEST_PATCH){
        tCheck.classList.remove("disabled"); tBox.disabled=false; tBox.checked=true;
        tSub.textContent=`Remap ${filePatch} to ${LATEST_PATCH}`;
      } else if(filePatch===LATEST_PATCH){
        tCheck.classList.add("disabled"); tBox.checked=false; tBox.disabled=true;
        tSub.textContent=`Already on the latest patch (${LATEST_PATCH})`;
      } else {
        tCheck.classList.add("disabled"); tBox.checked=false; tBox.disabled=true;
        tSub.textContent=`No opcode table for build ${curBuild}? add one to transpose`;
      }

      renderHeader(); renderTimeline(); renderPullTable(); renderPlayers();
      ["p-header","p-timeline","p-pulls","p-players","p-controls"].forEach(id=>document.getElementById(id).classList.remove("hidden"));
      selectedPull=-1;
      document.getElementById("btn-split").disabled=true;
      document.getElementById("pull-sel").textContent="none selected";
      document.getElementById("export-hint").textContent="Select a pull from the timeline or table to enable export.";
      toast(`Loaded ${pulls.length} pulls, ${players.length} players.`);
      emitNames(); // sync playback to the freshly loaded (unedited) names
    }catch(err){ toast(err.message,true); }
  }
}

// If "Transpose opcodes" is on, remap every packet to the latest patch and stamp the
// latest build (a transposed file must also be on the latest build to load). Mutates
// bytes in place; returns a status fragment for the toast, or "" if not applied.
function applyTransposeIfChecked(bytes){
  const box=document.getElementById("transpose");
  if(box.disabled || !box.checked) return "";
  new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength).setInt32(OFF_BUILD,LATEST_GAME_BUILD,true);
  const r=transposeOpcodes(bytes);
  if(!r.ok) return ` (transpose skipped: ${r.reason})`;
  let s=` · ${r.from}→${r.to}: ${r.rewritten}/${r.segTotal} packets remapped`;
  if(r.unknownSegs>0) s+=`, ${r.unknownSegs} unmapped`;
  return s;
}

document.getElementById("btn-split").addEventListener("click",async()=>{
  if(selectedPull<0) return;
  try{
    const opts={
      waymarks: document.getElementById("wm").checked,
      applyNames: document.getElementById("applynames").checked,
      countdown: document.getElementById("keepcd").checked,
    };
    const bytes=buildPull(selectedPull,opts);
    const note=applyTransposeIfChecked(bytes);
    const ghosts=lastGhostsDropped ? ` · removed ${lastGhostsDropped} stale duplicate spawn${lastGhostsDropped>1?"s":""}` : "";
    const base=fileName.replace(/\.dat$/i,"");
    const saved=await download(bytes,`pull${pulls[selectedPull].n}_${base}.dat`);
    if(saved) toast(`Exported pull ${pulls[selectedPull].n} (${fmtBytes(bytes.length)})${note}${ghosts}.`);
  }catch(err){ toast(err.message,true); }
});

document.getElementById("btn-anon").addEventListener("click",()=>{
  players.forEach((p,idx)=>{ p.newName=`Player ${idx+1}`; });
  document.querySelectorAll("#players input").forEach(inp=>{
    inp.value=players[+inp.dataset.idx].newName;
  });
  emitNames();
  toast(`Anonymized ${players.length} names — export to save.`);
});

document.getElementById("btn-names").addEventListener("click",async()=>{
  try{
    const bytes=buildRenamedFull();
    const note=applyTransposeIfChecked(bytes);
    const saved=await download(bytes,`RENAMED_${fileName}`);
    if(saved) toast(`Exported full recording with edited names (${fmtBytes(bytes.length)})${note}.`);
  }catch(err){ toast(err.message,true); }
});

let toastTimer=null;
function toast(msg,isErr=false){
  const t=document.getElementById("toast");
  t.textContent=msg; t.className="show"+(isErr?" err":"");
  clearTimeout(toastTimer); toastTimer=setTimeout(()=>t.className=isErr?"err":"",2600);
}

/* =====================================================================
   Dev menu — Konami code (↑ ↑ ↓ ↓ ← → ← →) opens a panel to register an
   opcode table + build number at runtime, so a new game patch can be tested
   before it's baked into opcodes.js. The table is added to OPCODE_TABLES /
   BUILD_TO_PATCH and the loaded file (if any) is re-parsed so it takes effect
   immediately. Nothing is persisted — it lives for the life of the tab.
   ===================================================================== */
const KONAMI=["ArrowUp","ArrowUp","ArrowDown","ArrowDown","ArrowLeft","ArrowRight","ArrowLeft","ArrowRight"];
const DEV_HINT_DEFAULT="Registers this opcode table for the build, then re-parses the loaded file. Plain {name:opcode} maps and a full FFXIVOpcodes opcodes.json are both accepted.";
let konamiPos=0;
document.addEventListener("keydown",e=>{
  // don't capture arrows while the user is typing in a field
  const t=e.target;
  if(t && (t.tagName==="INPUT"||t.tagName==="TEXTAREA"||t.isContentEditable)){ konamiPos=0; return; }
  if(e.key===KONAMI[konamiPos]) konamiPos++;
  else konamiPos = (e.key===KONAMI[0]) ? 1 : 0;
  if(konamiPos===KONAMI.length){ konamiPos=0; openDevMenu(); }
});

const $dev=(id)=>document.getElementById(id);
// Last build the user applied via the dev menu. Remembered so reopening the
// menu shows what they entered, not the loaded file's embedded build.
let devBuild="";
function openDevMenu(){
  // Prefer the last build the user applied; fall back to the loaded file's build.
  if(devBuild) $dev("dev-build").value=devBuild;
  else if(fileBuild) $dev("dev-build").value=String(fileBuild);
  devHint(DEV_HINT_DEFAULT,false);
  $dev("devmenu").classList.remove("hidden");
  $dev("dev-json").focus();
}
function closeDevMenu(){ $dev("devmenu").classList.add("hidden"); }
function devHint(msg,isErr){ const h=$dev("dev-hint"); h.textContent=msg; h.classList.toggle("dev-err",!!isErr); }

// Accept either a plain {name:opcode} object or a FFXIVOpcodes opcodes.json
// (array of regions, or one region object). Returns a {name:opcode} map or null.
function normalizeOpcodeTable(parsed){
  if(parsed && typeof parsed==="object" && !Array.isArray(parsed) && !parsed.lists && !parsed.region){
    const out={};
    for(const k in parsed){ const v=parsed[k]; if(Number.isFinite(v)) out[k]=v; }
    if(Object.keys(out).length) return out;
  }
  const regions = Array.isArray(parsed) ? parsed : (parsed && parsed.lists ? [parsed] : null);
  if(regions){
    const r = regions.find(x=>x && x.region==="Global") || regions[0];
    const list = r && r.lists && r.lists.ServerZoneIpcType;
    if(Array.isArray(list)){
      const out={};
      for(const e of list){ if(e && typeof e.name==="string" && Number.isFinite(e.opcode)) out[e.name]=e.opcode; }
      if(Object.keys(out).length) return out;
    }
  }
  return null;
}

function applyDevMenu(){
  const buildRaw=$dev("dev-build").value.trim();
  const build=Number(buildRaw);
  if(!buildRaw || !Number.isInteger(build) || build<=0){ devHint("Enter a valid positive integer build number.",true); return; }
  let parsed;
  try{ parsed=JSON.parse($dev("dev-json").value); }
  catch(err){ devHint("Opcodes JSON didn't parse: "+err.message,true); return; }
  const table=normalizeOpcodeTable(parsed);
  if(!table){ devHint("Couldn't read an opcode table from that JSON (expected {name:opcode} or a FFXIVOpcodes opcodes.json).",true); return; }

  const patchKey="Custom-"+build;
  OPCODE_TABLES[patchKey]=table;
  BUILD_TO_PATCH[build]=patchKey;
  devBuild=buildRaw;
  const n=Object.keys(table).length;

  // Promote this table to "latest" so transpose targets it and the build
  // re-stamp uses it — Applying is the same as setting it as the latest patch.
  LATEST_PATCH=patchKey; LATEST_GAME_BUILD=build;
  closeDevMenu();

  if(raw){
    try{ loadBytes(fileName, raw.buffer.slice(0)); toast(`Registered ${n} opcodes for build ${build} (now latest) — re-parsed ${fileName}.`); }
    catch(err){ toast(err.message,true); }
  } else {
    toast(`Registered ${n} opcodes for build ${build} (now latest). Load a .dat to use it.`);
  }
}

// Wipe any values the browser restored from the previous session on reload —
// the dev menu is meant to be ephemeral, gone on refresh.
$dev("dev-build").value=""; $dev("dev-json").value="";
$dev("dev-apply").addEventListener("click",applyDevMenu);
$dev("dev-close").addEventListener("click",closeDevMenu);
$dev("dev-cancel").addEventListener("click",closeDevMenu);
$dev("dev-prefill").addEventListener("click",()=>{
  $dev("dev-build").value=String(LATEST_GAME_BUILD);
  $dev("dev-json").value=JSON.stringify(OPCODE_TABLES[LATEST_PATCH],null,0);
});
$dev("devmenu").addEventListener("click",e=>{ if(e.target===$dev("devmenu")) closeDevMenu(); });
document.addEventListener("keydown",e=>{ if(e.key==="Escape" && !$dev("devmenu").classList.contains("hidden")) closeDevMenu(); });

/* Public API — the shell loads the file and feeds both modules. */
window.Inspector = { load: loadBytes };
})();
