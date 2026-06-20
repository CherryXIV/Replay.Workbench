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
  const pullChapters=chapters.filter(c=>PULL_START_TYPES.includes(c.type));
  pulls = pullChapters.map((pc,n)=>{
    const startIndex=o2i.get(pc.offset);
    const endIndex = (n<pullChapters.length-1) ? o2i.get(pullChapters[n+1].offset) : segs.length;
    const lastMs = endIndex>startIndex ? segs[endIndex-1].ms : pc.ms;
    const respawnStart=findRespawnBatchStart(startIndex);
    const batchCount=countSpawns(respawnStart,startIndex);
    return {chapter:pc,n:n+1,startIndex,endIndex,lengthMs:Math.max(0,lastMs-pc.ms),respawnStart,batchCount};
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
    tr.innerHTML=`<td class="num">${p.n}</td>
      <td>${CHAPTER_TYPE_NAMES[p.chapter.type]||p.chapter.type}</td>
      <td>${fmtClock(p.chapter.ms)}</td>
      <td class="dim">${fmtClock(p.lengthMs)}</td>
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

  // 2+3) [respawn .. next pull], rebased; inject waymarks at the pull start
  let chapterNewOffset=-1, written=byteLen(parts);
  for(let i=carryStart;i<endIndex;i++){
    if(i===pullIndex){
      // chapter points at the pull start (the waymark packets are emitted here at ms=0,
      // right before the pull's own first packet — same as the validated Python splitter)
      chapterNewOffset=byteLen(parts);
      if(opts.waymarks) injectWaymarks(src,parts,pullIndex);
    }
    parts.push(rebasedSeg(src,segs[i],segs[i].ms-pullStartMs));
  }
  if(chapterNewOffset<0) chapterNewOffset=byteLen(parts);

  const body=concat(parts);
  const lastMs = endIndex>carryStart ? Math.max(0,segs[endIndex-1].ms-pullStartMs):0;

  // header
  const header=src.slice(0,HEADER_SIZE);
  const hv=new DataView(header.buffer);
  hv.setInt32(OFF_REPLAY_LEN,body.length,true);
  hv.setUint32(OFF_TOTAL_MS,lastMs>>>0,true);
  hv.setUint32(OFF_DISPLAYED_MS,lastMs>>>0,true);

  // chapter array: single chapter at the pull start
  const ca=new Uint8Array(CHAPTER_ARRAY);
  const cav=new DataView(ca.buffer);
  cav.setInt32(0,1,true);
  cav.setInt32(4,p.chapter.type,true);
  cav.setUint32(8,chapterNewOffset>>>0,true);
  cav.setUint32(12,0,true);

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
      if(hasWaymark){ wmCheck.classList.remove("disabled"); wm.disabled=false; wmSub.textContent="carry the latest placement into the pull"; }
      else{ wmCheck.classList.add("disabled"); wm.checked=false; wm.disabled=true; wmSub.textContent="none captured in this recording"; }

      // opcode transpose (which also re-stamps the build): only when we have a patch
      // table for this build and it isn't already the latest
      const curBuild=i32(OFF_BUILD);
      const tCheck=document.getElementById("transpose-check"), tBox=document.getElementById("transpose"),
            tSub=document.getElementById("transpose-sub");
      if(filePatch && filePatch!==LATEST_PATCH){
        tCheck.classList.remove("disabled"); tBox.disabled=false; tBox.checked=true;
        tSub.textContent=`remap ${filePatch} → ${LATEST_PATCH} packets (also sets build)`;
      } else if(filePatch===LATEST_PATCH){
        tCheck.classList.add("disabled"); tBox.checked=false; tBox.disabled=true;
        tSub.textContent=`already on the latest patch (${LATEST_PATCH})`;
      } else {
        tCheck.classList.add("disabled"); tBox.checked=false; tBox.disabled=true;
        tSub.textContent=`no opcode table for build ${curBuild} — add one to transpose`;
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

/* Public API — the shell loads the file and feeds both modules. */
window.Inspector = { load: loadBytes };
})();
