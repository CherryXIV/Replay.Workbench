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

   Two data sources with different jobs:

   patchdiffs.js (PATCH_CHAIN / PATCH_DIFFS) records which opcode number
     became which at each game patch, read out of the binary's IPC vtable.
     Transpose runs on this and nothing else: it is exact, it covers every
     Dawntrail patch, and it never needs to know what a packet is called.

   opcodes.js (OPCODE_TABLES) holds IPC *names*, for the inspector's labels
     and for the handful of packets this tool looks up by name (NpcSpawn,
     PlaceFieldMarker, PartyPortraitInfo, the combat-timing set). Names come
     from a third-party dump that lags patches and has been wrong before —
     PartyList and PartyPortraitInfo both needed hand-correction — so they
     label packets; they no longer decide how packets get rewritten.

   Only the latest patch needs a pasted-in name table. Every older patch's
   names are projected backwards from it through the chain (patchTable()),
   so the inspector reads a 7.0 recording with the same names as a 7.5 one.
   Both live in patchchain.js, loaded before this file.
   ===================================================================== */
let fileBuild=0, filePatch=null;   // set by resolveOpcodes() from the loaded file
let patchOverride=null;            // the patch the user picked by hand, if any
let patchDetected=null;            // what the file's own opcodes say (detectPatch)

/* Which patch a file is on, most trustworthy source first.

   The file's opcodes beat BUILD_TO_PATCH because that table is typed in by hand
   and a wrong entry does not fail loudly: every packet still gets remapped, just
   onto the wrong packet type. Detection is only allowed to win when it accounts
   for the file exactly and no other patch comes close. */
function decidePatch(build){
  if(patchOverride) return patchOverride;
  if(patchDetected && patchDetected.confident) return patchDetected.patch;
  return BUILD_TO_PATCH[build]||null;
}

// Point the tool's parsing opcodes at the loaded file's patch (falls back to defaults).
function resolveOpcodes(build){
  fileBuild=build;
  filePatch=decidePatch(build);
  const t = filePatch ? patchTable(filePatch) : null;
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

// IPC names in `table` that share one opcode. Transpose maps packets by name, so a
// duplicated opcode value collapses two packet types into one: the client parses
// one of them with the other's struct and crashes. (A 3672-byte PartyList arriving
// on PlayerSpawn's opcode is how this bit us before.) Both the dev menu and
// transpose refuse such a table rather than write a replay that takes the game down.
function opcodeCollisions(table){
  const byOp=new Map();
  for(const name in table){ const op=table[name]; if(!byOp.has(op)) byOp.set(op,[]); byOp.get(op).push(name); }
  return [...byOp.entries()].filter(([,names])=>names.length>1);
}
function describeCollisions(cols,limit=2){
  return cols.slice(0,limit).map(([op,names])=>`${op} = ${names.join(" + ")}`).join("; ")
    + (cols.length>limit?`; +${cols.length-limit} more`:"");
}

// How to get from one patch to another. The diff chain is the real answer; the
// name tables are a stopgap for the one case the chain can't cover — a brand new
// patch registered through the dev menu, which has names published but no diff yet.
function remapPlan(from,to){
  const chain=patchChainMap(from,to);
  if(chain) return {ok:true, via:"diffs", map:chain.map, lost:chain.lost};

  const fromTable=OPCODE_TABLES[from], toTable=OPCODE_TABLES[to];
  if(!hasNames(fromTable)||!hasNames(toTable)) return {ok:false,reason:`no diff and no opcode table linking ${from} to ${to}`};
  // Remapping by name onto a table with a duplicated opcode collapses two packet
  // types into one and the client crashes reading one as the other. Refuse before
  // touching a byte — skipping the transpose is recoverable, shipping that isn't.
  for(const [patch,table,label] of [[to,toTable,"target"],[from,fromTable,"source"]]){
    const cols=opcodeCollisions(table);
    if(cols.length) return {ok:false,reason:`the ${label} table (${patch}) gives one opcode two packet names `+
      `(${describeCollisions(cols)}) — remapping onto it would crash the game; fix the table first`};
  }
  const map=new Map(), lost=new Map();
  for(const name in fromTable){
    if(toTable[name]!=null) map.set(fromTable[name],toTable[name]);
    else lost.set(fromTable[name],`${name} has no entry in ${to}`);
  }
  return {ok:true, via:"names", map, lost};
}

// Rewrite every segment opcode in a finished export buffer from its patch to LATEST_PATCH.
// Returns coverage info so the UI can be honest about how complete the remap is.
function transposeOpcodes(bytes){
  if(!filePatch) return {ok:false,reason:`no patch known for build ${fileBuild}`};
  if(filePatch===LATEST_PATCH) return {ok:false,reason:"already on the latest patch"};
  const plan=remapPlan(filePatch,LATEST_PATCH);
  if(!plan.ok) return plan;

  const dvb=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);
  const replayLen=dvb.getInt32(OFF_REPLAY_LEN,true);

  // First pass: what's actually in the file. Opcodes at 0xf000 and up are replay
  // control markers, not IPC, and are left alone.
  const hist=new Map();
  let off=0, segTotal=0;
  while(off<replayLen){
    const b=DATA_START+off;
    const op=dvb.getUint16(b,true);
    if(op<0xf000) hist.set(op,(hist.get(op)||0)+1);
    segTotal++;
    off+=SEG_HEADER+dvb.getUint16(b+2,true);
  }

  // An opcode we can't map keeps its old number. That's survivable on its own,
  // but if some *other* packet has since moved onto that number, the client reads
  // the leftovers with the wrong struct and dies. Check before writing anything.
  const targets=new Set();
  for(const op of hist.keys()){ const t=plan.map.get(op); if(t!==undefined) targets.add(t); }
  const stale=[...hist.keys()].filter(op=>!plan.map.has(op)&&targets.has(op));
  if(stale.length) return {ok:false,reason:`${stale.length} packet type(s) can't be remapped `+
    `(${stale.slice(0,3).map(o=>"0x"+o.toString(16)).join(", ")}${stale.length>3?", …":""}) and another packet `+
    `has moved onto their opcodes — the export would crash the client`};

  let rewritten=0, unknownSegs=0; const unknownKinds=new Set();
  off=0;
  while(off<replayLen){
    const b=DATA_START+off;
    const op=dvb.getUint16(b,true), len=dvb.getUint16(b+2,true);
    const to=plan.map.get(op);
    if(to!==undefined){ if(to!==op){ dvb.setUint16(b,to,true); rewritten++; } }
    else if(op<0xf000){ unknownSegs++; unknownKinds.add(op); }
    off+=SEG_HEADER+len;
  }
  return {ok:true, from:filePatch, to:LATEST_PATCH, via:plan.via, rewritten, segTotal,
          unknownSegs, unknownKinds:unknownKinds.size};
}

/* =====================================================================
   Parse
   ===================================================================== */
function parse(buffer){
  raw = new Uint8Array(buffer);
  dv = new DataView(raw.buffer);
  for(let i=0;i<MAGIC.length;i++) if(raw[i]!==MAGIC[i]) throw new Error("Not an FFXIVREPLAY .dat (bad header).");

  const replayLength = i32(OFF_REPLAY_LEN);

  // walk segments
  segs=[]; let off=0;
  const hist=new Map();
  while(off < replayLength){
    const b = DATA_START+off;
    const opcode=u16(b), dataLength=u16(b+2), ms=u32(b+4), oid=u32(b+8);
    segs.push({offset:off,opcode,dataLength,ms,oid,total:SEG_HEADER+dataLength});
    hist.set(opcode,(hist.get(opcode)||0)+1);
    off += SEG_HEADER+dataLength;
  }

  // Which patch this is has to wait for the segment walk: the answer comes from
  // the opcodes themselves, with the build number as a fallback. Everything
  // below reads packets by name (spawns, waymarks, combat), so it runs after.
  patchDetected = (typeof detectPatch==="function") ? detectPatch(hist) : null;
  resolveOpcodes(i32(OFF_BUILD));

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

/* Patch controls: which patch the file was recorded on, and whether it can be
   transposed to the latest. The patch is read out of the file's own opcodes
   (detectPatch), with the build number as a fallback and the picker as the last
   word. The tooltip says which of the three answered, because when the build
   table and the file disagree, the build table is the one that's wrong. */
function renderPatchControls(){
  const sel=document.getElementById("src-patch"), wrap=document.getElementById("src-patch-wrap");
  const fromBuild=BUILD_TO_PATCH[fileBuild]||null;
  const det=patchDetected;
  if(!sel.options.length){
    sel.appendChild(new Option("unknown",""));
    for(let i=PATCH_CHAIN.length-1;i>=0;i--) sel.appendChild(new Option(PATCH_CHAIN[i],PATCH_CHAIN[i]));
  }
  // A dev-menu table isn't in the chain but is a legitimate answer while it's registered.
  if(filePatch && !inChain(filePatch) && !sel.querySelector(`option[value="${filePatch}"]`))
    sel.insertBefore(new Option(filePatch,filePatch),sel.options[1]);
  sel.value = filePatch || "";
  sel.disabled=false; wrap.classList.remove("disabled");

  const source = patchOverride ? "you picked it"
    : (det && det.confident) ? `read from the file's opcodes (${Math.round(det.packets*100)}% fit, next best ${det.runnerUp})`
    : fromBuild ? `from build ${fileBuild}`
    : "not identified";
  wrap.title = `Patch: ${source}`;

  const tCheck=document.getElementById("transpose-check"), tBox=document.getElementById("transpose"),
        tSub=document.getElementById("transpose-sub");
  const enable=(on)=>{ tCheck.classList.toggle("disabled",!on); tBox.disabled=!on; if(!on) tBox.checked=false; };
  const old=oldSizedPackets();
  if(!filePatch){
    enable(false);
    tSub.textContent = det
      ? `Couldn't identify the patch - closest is ${det.patch} at ${Math.round(det.packets*100)}%; pick one`
      : `Build ${fileBuild} isn't a patch we know - pick the patch it was recorded on`;
    return;
  }
  if(filePatch===LATEST_PATCH){
    enable(false);
    tSub.textContent=`Already on the latest patch (${LATEST_PATCH})`;
    return;
  }
  const plan=remapPlan(filePatch,LATEST_PATCH);
  if(!plan.ok){ enable(false); tSub.textContent=`Can't remap ${filePatch}: ${plan.reason}`; return; }
  enable(true); tBox.checked=true;
  const hops=(inChain(filePatch)&&inChain(LATEST_PATCH)) ? PATCH_POS.get(LATEST_PATCH)-PATCH_POS.get(filePatch) : 0;
  let msg = plan.via==="diffs"
    ? `Remap ${filePatch} to ${LATEST_PATCH} through ${hops} patch${hops===1?"":"es"} of opcode diffs`
    : `Remap ${filePatch} to ${LATEST_PATCH} by IPC name (no diff for ${LATEST_PATCH} yet)`;
  // Say it out loud when the build table would have sent this file down the wrong
  // chain: a one-hotfix-off guess remaps every packet onto the wrong packet type.
  if(!patchOverride && det && det.confident && fromBuild && fromBuild!==det.patch)
    msg += ` - build ${fileBuild} is listed as ${fromBuild}, but the packets say ${det.patch}`;
  // Renumbering alone isn't enough on a recording old enough that the structs have
  // since grown, and the gap is silent: the export looks fine and the client
  // refuses it. Say so on the option itself.
  if(old.size){
    msg += ` · also resizing ${old.size} packet type${old.size===1?"":"s"} (` +
           [...old.entries()].sort().map(([n,sz])=>`${n} ${sz}→${MIGRATE_TARGET[n]}`).join(", ") + ")";
    // InitZone can't be resized by splicing (it drops bytes as well as adding
    // them), and rebuilding it needs a template recording to copy a working one
    // from. That lives in the desktop app, which is where this kind of fine-tuning
    // belongs; here the packet is passed through untouched and said so.
    if(old.has("InitZone"))
      msg += " · InitZone is left as-is - rebuilding it needs the desktop app";
  }
  tSub.textContent=msg;
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

      renderPatchControls();

      // anonymize works on any loaded file with players; off by default
      const aCheck=document.getElementById("anon-check"), aBox=document.getElementById("anon-appear"),
            aRaceW=document.getElementById("anon-race-wrap"), aRace=document.getElementById("anon-race");
      const anonOk=players.length>0;
      aCheck.classList.toggle("disabled",!anonOk); aBox.disabled=!anonOk;
      const raceOn=anonOk && aBox.checked;
      aRaceW.classList.toggle("disabled",!raceOn); aRace.disabled=!raceOn;

      // strip party portraits: needs the file's opcode table to find PartyPortraitInfo
      const spCheck=document.getElementById("strip-portrait-check"), spBox=document.getElementById("strip-portrait");
      const spTable=patchTable(filePatch);
      const spOk = !!(spTable && spTable.PartyPortraitInfo!=null);
      spCheck.classList.toggle("disabled",!spOk); spBox.disabled=!spOk;
      if(!spOk) spBox.checked=false;

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

/* =====================================================================
   Payload migration — resize the packets whose struct grew.

   Transpose renumbers opcodes, which is the whole job only while a packet's
   layout holds still. It doesn't for everything: between 7.16h and 7.55h2 five
   packet types changed size, and a client handed a 112-byte InitZone where it
   expects 136 stops reading at packet zero. So this runs alongside transpose —
   neither is any use without the other, and a file with one applied and not the
   other is worse than the original.

   Where the layouts come from: a 7.16h and a 7.55h recording of the same duty,
   which makes the comparison controlled — same cast, same arena, and a field
   that is constant in one is constant in the other. Confirmed the only way that
   finally counts, by loading the converted recording in the live client.

   Runs before transpose, so packets are still on the file's own opcodes and are
   found by name in the file's own patch. The other way round means picking
   packets by numbers that meant something else in the older patch.
   ===================================================================== */
// What each payload must measure on the current patch (7.51–7.55h2 recordings).
const MIGRATE_TARGET={PlayerSpawn:664, NpcSpawn:656, ActorControlSelf:40, Countdown:64, InitZone:136};

/* The measured moves. `inserts` are runs of zero bytes to splice in, in OLD
   payload coordinates — each offset is the byte its run goes in front of, so the
   entries never shift one another. Whatever they leave short of the target is
   zero padding at the tail.

   PlayerSpawn      proven rather than inferred: every field was located against
                    the party-portrait packet (customize block, job byte and both
                    dye channels are byte-identical to the spawn's, and it did not
                    change size). Job lands at +2, everything from the gear array
                    onward at +4, so the inserts fall in (126,140) and (157,164) —
                    runs of zero padding in BOTH patches, which is why splicing
                    zeros reproduces the real layout exactly. The last 4 are tail,
                    zero in both.
   NpcSpawn         same shape, established statistically. A per-byte value
                    distribution steps cleanly 0 → +2 → +4, and each transition is
                    confirmed by a distinctive field landing on its counterpart:
                    the 0x00/0x10 byte at old 146 → new 148 and 0x00/0x01 at old
                    147 → new 149 (+2), while 0x00/0x30 at old 148 → new 152 (+4).
   ActorControlSelf plain tail growth — the 8 added bytes are zero in all 4488
                    current samples, and matched packets are byte-identical
                    across the first 32.
   Countdown        a 16-byte head appeared: object id old +0 → new +16, second id
                    +4 → +20, name +11 → +27. The first 8 bytes of that head are
                    the initiating player's character key, filled in from the file
                    rather than left zero.

   InitZone is deliberately absent — it DELETES 8 bytes as well as adding 32, so
   no splice can express it. Rebuilding it needs a working payload to copy from,
   which is a desktop-app job; here it is passed through and reported. */
const MIGRATIONS=[
  {packet:"PlayerSpawn",      from:656, to:664, inserts:[[126,2],[157,2]]}, // remaining 4 pad the tail
  {packet:"NpcSpawn",         from:648, to:656, inserts:[[124,2],[148,2]]}, // remaining 4 pad the tail
  {packet:"ActorControlSelf", from:32,  to:40,  inserts:[]},
  {packet:"Countdown",        from:48,  to:64,  inserts:[[0,16]]},
];

function migrateOnePayload(m, payload){
  const out=new Uint8Array(m.to);
  let read=0, write=0;
  for(const [at,count] of [...m.inserts].sort((a,b)=>a[0]-b[0])){
    if(at>payload.length || write+count>m.to) break;
    const run=Math.min(at-read, m.to-write);
    if(run>0) out.set(payload.subarray(read,read+run), write);
    write+=run+count; // the spliced bytes are already zero
    read=at;
  }
  const rest=Math.min(payload.length-read, m.to-write);
  if(rest>0) out.set(payload.subarray(read,read+rest), write);
  return out;
}

/* Object id -> character key, off this file's PlayerSpawn packets. Both spawn
   layouts keep the key at payload +0 and the spawning player's object id in the
   segment header, so this doesn't care which one it's reading. */
function spawnKeyMap(bytes, dv, replayLen, spawnOp){
  const map=new Map();
  if(spawnOp==null) return map;
  let off=0;
  while(off<replayLen){
    const b=DATA_START+off, op=dv.getUint16(b,true), len=dv.getUint16(b+2,true);
    if(op===spawnOp && len>=8){
      const oid=dv.getUint32(b+8,true);
      const key=dv.getBigUint64(b+SEG_HEADER,true);
      if(oid && key) map.set(oid,key);
    }
    off+=SEG_HEADER+len;
  }
  return map;
}

// Packet name -> the old size it was found at, for anything the client would
// reject. Empty means the file needs no resizing.
function oldSizedPackets(){
  const t=patchTable(filePatch); const found=new Map();
  if(!t) return found;
  for(const [name,target] of Object.entries(MIGRATE_TARGET)){
    const op=t[name]; if(op==null) continue;
    const hit=segs.find(s=>s.opcode===op && s.dataLength!==target);
    if(hit) found.set(name,hit.dataLength);
  }
  return found;
}

/* Resize every packet that needs it, and fix up what the sizes invalidate: the
   replay length, and every chapter offset, which points into the data stream and
   so moves by however many bytes grew before it.

   The InitZone is left alone and reported: it can't be spliced, and rebuilding it
   from a template recording is a desktop-app job. Returns {bytes, note}: a NEW
   array when anything was resized, else the original. */
function migratePayloads(bytes){
  const t=patchTable(filePatch);
  if(!t) return {bytes, note:""};
  const opToName=new Map();
  for(const name of Object.keys(MIGRATE_TARGET)) if(t[name]!=null) opToName.set(t[name],name);
  if(!opToName.size) return {bytes, note:""};

  const dv=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);
  const replayLen=dv.getInt32(OFF_REPLAY_LEN,true);
  const keys=spawnKeyMap(bytes,dv,replayLen,t.PlayerSpawn);

  const chunks=[]; const shifts=[]; const counts=new Map(); const blocked=new Set();
  let off=0, grown=0, bodyLen=0;
  while(off<replayLen){
    const b=DATA_START+off, op=dv.getUint16(b,true), len=dv.getUint16(b+2,true), p=b+SEG_HEADER;
    const header=bytes.slice(b,b+SEG_HEADER);
    const payload=bytes.subarray(p,p+len);
    const name=opToName.get(op);
    let resized=null;
    if(name!=null && len!==MIGRATE_TARGET[name]){
      if(name==="InitZone"){
        blocked.add(`InitZone is ${len} bytes - rebuilding it needs the desktop app`);
      } else {
        const m=MIGRATIONS.find(x=>x.packet===name && x.from===len);
        if(m){
          resized=migrateOnePayload(m,payload);
          // Countdown's new head opens with the character key; the packet still
          // carries the player's object id, so it can be looked up.
          if(name==="Countdown" && len>=4){
            const key=keys.get(dv.getUint32(p,true));
            if(key!=null) new DataView(resized.buffer).setBigUint64(0,key,true);
          }
        } else blocked.add(`${name} is ${len} bytes, a size no measured layout covers`);
      }
    }
    if(resized){
      new DataView(header.buffer).setUint16(2,resized.length,true);
      counts.set(name,(counts.get(name)||0)+1);
      grown+=resized.length-len;
      chunks.push(header,resized); bodyLen+=SEG_HEADER+resized.length;
    } else {
      chunks.push(header,payload); bodyLen+=SEG_HEADER+len;
    }
    off+=SEG_HEADER+len;
    shifts.push([off,grown]);
  }
  if(!counts.size && !blocked.size) return {bytes, note:""};

  const trailing=bytes.subarray(DATA_START+replayLen);
  const out=new Uint8Array(DATA_START+bodyLen+trailing.length);
  out.set(bytes.subarray(0,DATA_START));
  let w=DATA_START;
  for(const c of chunks){ out.set(c,w); w+=c.length; }
  out.set(trailing,w);

  const ov=new DataView(out.buffer);
  ov.setInt32(OFF_REPLAY_LEN,bodyLen,true);
  const nch=Math.max(0,Math.min(ov.getInt32(HEADER_SIZE,true),MAX_CHAPTERS));
  for(let i=0;i<nch;i++){
    const e=HEADER_SIZE+4+i*CHAPTER_ENTRY, at=ov.getUint32(e+4,true);
    let delta=0;
    for(const [end,g] of shifts){ if(end>at) break; delta=g; }
    ov.setUint32(e+4,at+delta,true);
  }

  const detail=[...counts.entries()].sort().map(([n,c])=>`${n} x${c}`).join(", ");
  let note = counts.size ? ` · resized ${detail} (${grown>=0?"+":""}${grown.toLocaleString()} bytes)` : "";
  for(const why of blocked) note+=` · NOT resized: ${why}`;
  return {bytes:out, note};
}

/* Resize old packets, then remap opcodes — the two halves of "make this load on
   the current patch". Returns {bytes, note}: a NEW array when anything grew. */
function applyPatchUpgradeIfChecked(bytes){
  const box=document.getElementById("transpose");
  if(box.disabled || !box.checked) return {bytes, note:""};
  const m=migratePayloads(bytes);
  return {bytes:m.bytes, note:m.note+applyTransposeIfChecked(m.bytes)};
}

// If "Transpose opcodes" is on, remap every packet to the latest patch and stamp the
// latest build (a transposed file must also be on the latest build to load). Mutates
// bytes in place; returns a status fragment for the toast, or "" if not applied.
function applyTransposeIfChecked(bytes){
  const box=document.getElementById("transpose");
  if(box.disabled || !box.checked) return "";
  // Remap first, stamp second. A file stamped to the latest build but still
  // carrying its old opcodes is the one combination that loads and then crashes,
  // so the build only moves once the packets actually did.
  const r=transposeOpcodes(bytes);
  if(!r.ok) return ` (transpose skipped: ${r.reason})`;
  new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength).setInt32(OFF_BUILD,LATEST_GAME_BUILD,true);
  let s=` · ${r.from}→${r.to} via ${r.via}: ${r.rewritten}/${r.segTotal} packets remapped`;
  if(r.unknownSegs>0) s+=`, ${r.unknownSegs} unmapped`;
  return s;
}

// If "Strip party portraits" is on, physically remove every PartyPortraitInfo
// packet from the data stream and fix up the replay length + chapter offsets.
// Must run before transpose, while packets still carry the file's own opcodes.
// Returns {bytes, note}: a NEW array when anything was removed, else the original.
function stripPartyPortraitsIfChecked(bytes){
  const box=document.getElementById("strip-portrait");
  if(box.disabled || !box.checked) return {bytes, note:""};
  const t=patchTable(filePatch);
  const op=t ? t.PartyPortraitInfo : null;
  if(op==null) return {bytes, note:""};
  const dv=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);
  const replayLen=dv.getInt32(OFF_REPLAY_LEN,true);

  // Find every portrait segment by data-stream offset (relative to DATA_START).
  const removed=[]; let off=0;
  while(off<replayLen){
    const len=dv.getUint16(DATA_START+off+2,true), total=SEG_HEADER+len;
    if(dv.getUint16(DATA_START+off,true)===op) removed.push({at:off,total});
    off+=total;
  }
  if(!removed.length) return {bytes, note:" · no portrait packets to strip"};
  const removedBytes=removed.reduce((a,r)=>a+r.total,0);

  // Rebuild: header + chapter array unchanged, body minus the portrait segments,
  // plus any trailing bytes after the data area.
  const out=new Uint8Array(bytes.length-removedBytes);
  out.set(bytes.subarray(0,DATA_START),0);
  let w=DATA_START; off=0;
  while(off<replayLen){
    const total=SEG_HEADER+dv.getUint16(DATA_START+off+2,true);
    if(dv.getUint16(DATA_START+off,true)!==op){ out.set(bytes.subarray(DATA_START+off,DATA_START+off+total),w); w+=total; }
    off+=total;
  }
  out.set(bytes.subarray(DATA_START+replayLen),w); // trailing bytes, if any

  const ov=new DataView(out.buffer);
  ov.setInt32(OFF_REPLAY_LEN,replayLen-removedBytes,true);
  // Each chapter offset must drop by the bytes removed strictly before it.
  const clen=ov.getInt32(HEADER_SIZE,true);
  for(let i=0;i<clen && i<MAX_CHAPTERS;i++){
    const e=HEADER_SIZE+4+i*CHAPTER_ENTRY, choff=ov.getUint32(e+4,true);
    let shift=0; for(const r of removed) if(r.at<choff) shift+=r.total;
    ov.setUint32(e+4,(choff-shift)>>>0,true);
  }
  return {bytes:out, note:` · stripped ${removed.length} portrait packet${removed.length>1?"s":""}`};
}

/* =====================================================================
   Player anonymization — swap every party member to a chosen race (keeping
   their gender), redress them in their job's artifact gear, and blank names.
   Identity leaks from three packets, so all are rewritten:
     PlayerSpawn  (664B now, 656B on early Dawntrail recordings — the layout is
       picked from the packet's own length) — the in-arena model: race + AF gear (model IDs)
                                   + facewear/glasses id (stripped to 0)
                                   + title id (stripped to 0)
                                   + current/home world (both set to ANON_WORLD)
     party-member appearance     — the "Party Members" portraits: race + AF gear
       (1408B = 8x176, gear stored as item IDs; matched by length) +
       facewear/glasses id (stripped to 0)
                                   + mainhand/offhand weapon model (swapped to AF)
     PartyList    (3672B = 8x456 + a 24-byte trailer; matched by length) — the
       party panel's roster, which keeps its own copy of each member's home world
       and would otherwise go on naming worlds the spawn packets no longer admit to
     ActorControl category 504   — the status icon again, re-sent after the spawn.
       The spawn byte is not the last word on it: these land seconds in and the
       client acts on them from then on, so stripping only the spawn leaves the
       icon correct until the first of these plays and puts the original back.
     plus every name string, replaced length-preserving across the file.
   AF gear comes from JOB_AF_GEAR (afgear.js): item IDs for the appearance packet,
   [model,variant] armor + [model,base,variant] weapon for the spawn packet.
   ===================================================================== */
/* PlayerSpawn payload offsets — deliberately NOT constants.

   The packet grew over Dawntrail, so a recording made before it grew keeps every
   field somewhere else, and a tool that assumes one layout does not fail loudly
   on the other: it matches no spawn packet at all, lists nobody, and reports a
   successful anonymize while leaving every real name in the file.

   Which layout a packet uses is answered by the packet — the segment header
   carries its payload length and each layout has its own — so nothing here has to
   know which patch first moved a field. Always match the PlayerSpawn opcode
   first: the sizes are only unique among PlayerSpawns (7.55h's NpcSpawn is 656
   bytes, the same as 7.16h's PlayerSpawn).

   664 is current. 656 was measured on a 7.16h recording by cross-referencing the
   party-portrait packet, whose customize block, job byte and both dye channels
   are byte-identical to the spawn's and which did not change size. The head —
   key, both weapons, the display flags — did not move at all; job moved +2 and
   everything from the gear array onward +4.

   title: the worn title (u16), a row in the game's Title sheet; 0 = none. Derived
     from three recordings of one character differing only in the title worn — 8,
     9 and 865. The 8 -> 865 pair moves both bytes, which is what makes it a u16
     rather than a byte plus padding, and 865 is far too large to be an index into
     one character's unlocked list, so it is the sheet row itself. Every NpcSpawn
     reads 0 here, as an NPC should.
   curWorld/homeWorld: the world the character is logged in on, and the one they
     belong to (u16 each). Measured on an eight-player recording: six distinct
     values across the party, and two members reading 65/81 and 65/408 — visitors
     on the recorder's own world 65. Those two are what tell the fields apart; a
     party sitting at home would have shown one repeated number and proved nothing.
   onlineStatus: the status icon beside the name (u8) — Busy, Role-playing, the
     mentor crowns — as a row id in the game's OnlineStatus sheet, used raw with no
     offset. Pinned by ten recordings of one character differing only in the status
     set in the search-info window: it is the sole byte of the spawn packet that
     moves between them, reading 12 for Busy, 22 for Role-playing, 23 for Looking
     for Party and 27-30 for the four mentor crowns — each exactly its sheet row.
     NPCs read 0, which is the anchor on the other side.
   face: facewear/glasses model id (u16) between the dye array and the name.
     0 = none; confirmed against a known replay (Vivi=457).
   weapon/weaponSub: mainhand + offhand, each a u64 packed as
     [model u16][base u16][variant u16][dye u16]. Confirmed by diffing two
     captures that changed only the weapon glamour (44732 -> 2001/76/2). */
/* title/curWorld/homeWorld/onlineStatus are inferred for 656, not measured: every
   sample carrying a title, a cross-world player or a set status is 664-byte. All
   four sit in the head, which the note above records as not having moved between
   the two layouts. */
const SPAWN_LAYOUTS=[
  {len:664, key:0, title:16, curWorld:20, homeWorld:22, onlineStatus:0x1B, weapon:0x30, weaponSub:0x38, display:0x74, job:151, gear:540, dye2:580, face:590, name:594, cust:626},
  {len:656, key:0, title:16, curWorld:20, homeWorld:22, onlineStatus:0x1B, weapon:0x30, weaponSub:0x38, display:0x74, job:149, gear:536, dye2:576, face:586, name:590, cust:622},
];
const spawnLayoutFor=(len)=>SPAWN_LAYOUTS.find(l=>l.len===len)||null;
// Sizes and flags that hold in every known layout.
// display flags: 0x40 = hide headgear, 0x80 = hide weapon — set when a player
// toggles those off on the character screen. We must clear them after
// re-dressing, or the AF helm/weapon we wrote stays invisible.
// dye2: per-slot second dye channel (Dawntrail), one byte per gear slot, packed
// right after the 40-byte gear array. Left intact it leaks the player's real
// dyes; on at least one capture the head slot's byte was non-zero only on the
// actor whose helm refused to render after re-dressing.
const PS_GEAR_N=40, PS_DYE2_N=10, PS_NAME_N=32, DISPLAY_HIDE_GEAR=0x40|0x80;
// party-member appearance payload: 8 members of this stride. Unlike the spawn
// packet this one has not moved — a 7.16h recording's portrait blocks match the
// current offsets field for field.
const AP_LEN=1408, AP_STRIDE=176, AP_JOB=17, AP_GEAR=80, AP_FACE=120, AP_CUST=124;
// PartyList: 8 roster slots of this stride, then a 24-byte trailer (3672 = 8*456+24).
// Every offset is pinned by something that identifies itself — the member names sit
// exactly 456 bytes apart, the key at +40 matches that player's PlayerSpawn key, and
// the world at +80 is the *home* world (the member who is 65/81 in her spawn reads 81
// here). There is no current-world field: hers is the only world-valued u16 in her
// whole block. Unfilled slots have a zero key.
const PL_LEN=3672, PL_STRIDE=456, PL_MEMBERS=8, PL_KEY=40, PL_HOME=80;
// The world every anonymized character is moved to. One shared value rather than a
// random one per player: a world id is not a name, so the point is to stop the roster
// narrowing who the party was, and eight players scattered across eight invented
// worlds would say more about them than eight on one.
const ANON_WORLD=91;
// The status icon every anonymized character is given: In Duty, row 43 of the
// OnlineStatus sheet. The honest answer rather than a blank — a recording only
// exists because someone was in a duty, so this is what their icon would have been
// had they set no status at all, and zeroing the field instead would leave every
// anonymized player reading the value the game gives NPCs. Worth stripping because
// the mentor crowns and the like are worn by few enough people to narrow a roster,
// and "Role-playing" on one member is the sort of detail that identifies a group.
const ANON_ONLINE_STATUS=43;
// ActorControl carries the status icon again after the spawn: category at +0 of the
// payload, the status itself as the first u32 argument at +4. Whose it is comes from
// the *segment header's* object id, not the payload — but the anonymizer never has to
// ask, since every character in the file is going to the same status anyway.
// Only the plain ActorControl was seen carrying category 504 — all 40 across the ten
// status recordings — but all three variants share the category/argument head, so all
// three are rewritten rather than betting the icon on which one a future recording uses.
const AC_STATUS_ICON=504, AC_CATEGORY=0, AC_PARAM1=4, AC_MIN_LEN=8;
const AC_OP_NAMES=["ActorControl","ActorControlSelf","ActorControlTarget"];

// A valid generic customize for (race, gender): default features, mid tones.
function customizeFor(race,gender){
  const tribe=(race-1)*2+1; // first clan of the race
  return [race,gender,1,50,tribe,1,1,0,128,1, 1,1,0,0,1,1,1,1,1,1, 1,0,0,0,0,0];
}
function writeCustomize(bytes,at,race){
  const gender=bytes[at+1]&1;            // preserve the player's gender
  bytes.set(customizeFor(race,gender),at);
}
// Write a weapon u64 [model][base][variant][dye=0] at `at`. wm is [model,base,
// variant] (from JOB_AF_GEAR), or null/undefined to clear the slot (no weapon).
function writeWeapon(dv,at,wm){
  dv.setUint16(at,   wm?wm[0]:0, true);
  dv.setUint16(at+2, wm?wm[1]:0, true);
  dv.setUint16(at+4, wm?wm[2]:0, true);
  dv.setUint16(at+6, 0, true);          // dye
}
// Overwrite every occurrence of `needle` with `repl` (same length) in place.
function replaceBytes(buf,needle,repl){
  const n=needle.length; let count=0;
  outer: for(let i=0;i<=buf.length-n;i++){
    for(let j=0;j<n;j++) if(buf[i+j]!==needle[j]){ continue outer; }
    buf.set(repl,i); count++; i+=n-1;
  }
  return count;
}

// If "Anonymize players" is on, rewrite spawn/appearance packets and names in
// place. Runs before transpose so packets are still in the file's own opcodes.
function applyAnonymizeIfChecked(bytes){
  const box=document.getElementById("anon-appear");
  if(box.disabled || !box.checked) return "";
  const race=parseInt(document.getElementById("anon-race").value,10)||1;
  const dv=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);
  const replayLen=dv.getInt32(OFF_REPLAY_LEN,true);
  const spawnTable=patchTable(filePatch);
  const spawnOp=spawnTable ? spawnTable.PlayerSpawn : null;
  const iconOps=new Set(spawnTable ? AC_OP_NAMES.map(n=>spawnTable[n]).filter(o=>o!=null) : []);
  const td=new TextDecoder();

  // Pass 1: gather real names + object IDs from PlayerSpawn. Each PlayerSpawn's
  // segment-header oid (b+8) is the spawning player's own actor/object ID.
  const labels=new Map(); // name string -> "Player N"
  const oids=new Set();   // real player object IDs to scramble
  let off=0;
  while(off<replayLen){
    const b=DATA_START+off, op=dv.getUint16(b,true), len=dv.getUint16(b+2,true), p=b+SEG_HEADER;
    const L=(spawnOp!=null && op===spawnOp) ? spawnLayoutFor(len) : null;
    if(L){
      let end=p+L.name; while(end<p+L.name+PS_NAME_N && bytes[end]!==0) end++;
      const nm=td.decode(bytes.subarray(p+L.name,end));
      if(nm && !labels.has(nm)) labels.set(nm,`Player ${labels.size+1}`);
      const oid=dv.getUint32(b+8,true);
      if(oid) oids.add(oid);
    }
    off+=SEG_HEADER+len;
  }

  // Build a random object ID for each player, kept in the player range
  // (high byte 0x10) so it still reads as a valid actor id. Avoid collisions
  // with any real id (and each other) so the length-preserving byte swaps below
  // can't chain or alias one player's packets onto another's.
  const idMap=new Map(); const usedIds=new Set(oids);
  for(const id of oids){
    let r;
    do{ r=(0x10000000 | (Math.floor(Math.random()*0x01000000)))>>>0; }while(usedIds.has(r));
    usedIds.add(r); idMap.set(id,r);
  }

  // Pass 2: race (+ gear) on spawn and appearance packets.
  let spawns=0, appears=0, dressed=0, rosters=0, icons=0;
  off=0;
  while(off<replayLen){
    const b=DATA_START+off, op=dv.getUint16(b,true), len=dv.getUint16(b+2,true), p=b+SEG_HEADER;
    const L=(spawnOp!=null && op===spawnOp) ? spawnLayoutFor(len) : null;
    if(L){
      writeCustomize(bytes,p+L.cust,race);
      const g=JOB_AF_GEAR[bytes[p+L.job]];
      if(g){ // dress the in-arena model: [model:u16][variant:u8][stain:u8] per slot
        g.gearModels.forEach(([m,v],s)=>{ dv.setUint16(p+L.gear+s*4,m,true); bytes[p+L.gear+s*4+2]=v; bytes[p+L.gear+s*4+3]=0; });
        writeWeapon(dv,p+L.weapon,g.weaponModel);   // mainhand -> AF weapon
        writeWeapon(dv,p+L.weaponSub,g.weaponSub);  // offhand  -> AF secondary (or cleared)
      } else { bytes.fill(0,p+L.gear,p+L.gear+PS_GEAR_N); writeWeapon(dv,p+L.weapon); writeWeapon(dv,p+L.weaponSub); }
      dv.setUint16(p+L.face,0,true); // strip facewear/glasses — it leaks identity
      dv.setUint16(p+L.title,0,true); // a rare title narrows the field hard
      bytes[p+L.onlineStatus]=ANON_ONLINE_STATUS; // status icon -> In Duty
      // Home world is a short list a real person is on, and a visitor's current
      // world says which one they travelled to; both narrow the party, so everyone
      // is moved to the same world instead.
      dv.setUint16(p+L.curWorld,ANON_WORLD,true);
      dv.setUint16(p+L.homeWorld,ANON_WORLD,true);
      dv.setUint16(p+L.display, dv.getUint16(p+L.display,true) & ~DISPLAY_HIDE_GEAR, true); // unhide helm/weapon so the AF gear renders
      bytes.fill(0,p+L.dye2,p+L.dye2+PS_DYE2_N); // clear residual 2nd-dye bytes for the redressed slots
      spawns++;
    } else if(len===AP_LEN){
      for(let i=0;i<AP_LEN/AP_STRIDE;i++){
        const e=p+i*AP_STRIDE, job=bytes[e+AP_JOB];
        if(dv.getUint32(e,true)===0 && dv.getUint32(e+4,true)===0) continue; // empty slot
        if(job<1 || job>42) continue; // not a member slot — leave it alone
        writeCustomize(bytes,e+AP_CUST,race);
        const g=JOB_AF_GEAR[job];
        if(g){ g.gear.forEach((id,s)=>dv.setUint32(e+AP_GEAR+s*4,id,true)); dressed++; }
        else bytes.fill(0,e+AP_GEAR,e+AP_GEAR+40);
        dv.setUint16(e+AP_FACE,0,true); // strip facewear/glasses here too
      }
      appears++;
    } else if(len===PL_LEN){
      // The roster's own copy of the home world. Left alone it survives every
      // other pass here.
      for(let i=0;i<PL_MEMBERS;i++){
        const e=p+i*PL_STRIDE;
        if(dv.getUint32(e+PL_KEY,true)===0 && dv.getUint32(e+PL_KEY+4,true)===0) continue; // empty slot
        dv.setUint16(e+PL_HOME,ANON_WORLD,true);
        rosters++;
      }
    } else if(iconOps.has(op) && len>=AC_MIN_LEN && dv.getUint16(p+AC_CATEGORY,true)===AC_STATUS_ICON){
      // The spawn byte above is not the last word on the icon — see AC_STATUS_ICON.
      dv.setUint32(p+AC_PARAM1,ANON_ONLINE_STATUS,true);
      icons++;
    }
    off+=SEG_HEADER+len;
  }

  // Pass 3: blank names everywhere (length-preserving).
  for(const [nm,label] of labels){
    const need=new TextEncoder().encode(nm);
    const rep=new Uint8Array(need.length);
    rep.set(new TextEncoder().encode(label).subarray(0,need.length));
    replaceBytes(bytes,need,rep);
  }

  // Pass 4: scramble object IDs — replace every little-endian occurrence of each
  // real player oid (segment headers + payload actor references) with its random
  // remap, length-preserving like the name swap.
  let idHits=0;
  for(const [id,r] of idMap){
    const need=new Uint8Array(4), rep=new Uint8Array(4);
    new DataView(need.buffer).setUint32(0,id,true);
    new DataView(rep.buffer).setUint32(0,r,true);
    idHits+=replaceBytes(bytes,need,rep);
  }

  return ` · anonymized ${labels.size} players (${spawns} spawns, ${dressed} dressed, ${rosters} roster entries, ${icons} status icons, ${idMap.size} ids→${idHits} refs)`;
}

// Enable the race dropdown only while "Anonymize players" is checked.
document.getElementById("anon-appear").addEventListener("change",e=>{
  const on=!e.target.disabled && e.target.checked;
  document.getElementById("anon-race-wrap").classList.toggle("disabled",!on);
  document.getElementById("anon-race").disabled=!on;
});

document.getElementById("btn-split").addEventListener("click",async()=>{
  if(selectedPull<0) return;
  try{
    const opts={
      waymarks: document.getElementById("wm").checked,
      applyNames: document.getElementById("applynames").checked,
      countdown: document.getElementById("keepcd").checked,
    };
    let bytes=buildPull(selectedPull,opts);
    const anon=applyAnonymizeIfChecked(bytes);
    const strip=stripPartyPortraitsIfChecked(bytes); bytes=strip.bytes;
    const up=applyPatchUpgradeIfChecked(bytes); bytes=up.bytes;
    const note=anon+strip.note+up.note;
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
    let bytes=buildRenamedFull();
    const anon=applyAnonymizeIfChecked(bytes);
    const strip=stripPartyPortraitsIfChecked(bytes); bytes=strip.bytes;
    const up=applyPatchUpgradeIfChecked(bytes); bytes=up.bytes;
    const note=anon+strip.note+up.note;
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
  // Reject a self-contradicting table here, at the door. Registering it promotes it
  // to the transpose target, and every export made against it would crash the game.
  const cols=opcodeCollisions(table);
  if(cols.length){
    devHint(`Rejected: this table gives one opcode two packet names (${describeCollisions(cols)}). `+
            `Transpose maps packets by name, so those two packet types would collapse onto a single `+
            `opcode and the client would crash reading one as the other. Fix the duplicate and re-apply.`,true);
    return;
  }

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

/* Picking a patch by hand re-parses the file: the patch decides which opcode is
   NpcSpawn, PlaceFieldMarker and so on, so the pull list and timeline have to be
   rebuilt against it, not just the transpose. */
document.getElementById("src-patch").addEventListener("change",e=>{
  patchOverride=e.target.value||null;
  if(!raw) return;
  try{ loadBytes(fileName, raw.buffer.slice(0)); }
  catch(err){ toast(err.message,true); }
});

/* Public API — the shell loads the file and feeds both modules. A fresh file
   drops any hand-picked patch; the new one gets read from its own build. */
window.Inspector = { load:(name,buffer)=>{ patchOverride=null; loadBytes(name,buffer); } };
})();
