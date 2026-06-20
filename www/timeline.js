"use strict";
/* The playback view runs as a self-contained module (window.Playback) so it can
   share a page with the inspector module without their globals colliding. */
(function(){
/* =====================================================================
   Positional playback for FFXIVReplay .dat recordings.

   Reads the same file the main inspector reads, but instead of splitting
   pulls it reconstructs where every actor was over time and plays it back
   on a top-down map.

   How the positions are recovered:
     - Every data segment header carries the moving actor's objectID
       (opcode u16, dataLength u16, ms u32, objectID u32 = 12 bytes).
     - ActorMove packets pack the new position as three u16 (x,y,z) at
       payload offset 6/8/10. We map the u16 range onto world units and
       plot the ground plane (x, z). Height (y) is ignored for a map view.
     - ActorSetPos packets carry full floats (x,y,z at payload 8/12/16).
     - PlayerSpawn / NpcSpawn carry the actor's name; we scan the payload
       for the 32-byte name field so we can label dots instead of object
       IDs. Scanning (rather than a fixed offset) keeps this working across
       game patches, whose struct layouts drift.

   Because the map auto-fits to the recorded bounds, the absolute world
   scale of the u16 decode doesn't matter to the visualisation — the decode
   is linear, so the path shape and relative spacing are preserved.
   opcodes.js (loaded first) supplies the per-patch opcode tables.
   ===================================================================== */

const HEADER_SIZE = 0x68;
const CHAPTER_ENTRY = 0xC;
const MAX_CHAPTERS = 64;
const CHAPTER_ARRAY = 0x4 + CHAPTER_ENTRY * MAX_CHAPTERS;
const DATA_START = HEADER_SIZE + CHAPTER_ARRAY;
const SEG_HEADER = 12;

const OFF_REPLAY_LEN = 0x48;
const OFF_TOTAL_MS = 0x18;
const OFF_BUILD = 0x10;
const OFF_PLAYERINDEX = 0x40;

const MAGIC = [70,70,88,73,86,82,69,80,76,65,89,0]; // "FFXIVREPLAY\0"
const PULL_START_TYPES = [2,5];
const CHAPTER_TYPE_NAMES = {1:"Countdown",2:"Start/Restart",3:"Countdown(3)",4:"Event Cutscene",5:"Barrier Down"};

/* If an actor produced no packet for this long, treat it as gone (despawn /
   left the area) and stop drawing it rather than freezing a stale dot. */
const STALE_MS = 12000;
/* How much of the recent path to draw behind each actor, in ms. */
const TRAIL_MS = 6000;

/* ---- state ---- */
let raw=null, dv=null, fileName="";
let segs=[];
let chapters=[], pulls=[];
let actors=new Map();      // oid -> {oid,name,isPlayer,color,frames:[{ms,x,z,rot}]}
let actorList=[];          // sorted, stable order for the legend
let waymarks=[];           // {ms,id,active,x,z} placement events, sorted by ms
let timeMin=0, timeMax=1;  // ms span of all movement
let recorderOid=-1;
let opTable=null, opPatch=null; // resolved opcode table for the loaded file (for diagnostics)

const WAYMARK_LABELS=["A","B","C","D","1","2","3","4"];
const WAYMARK_COLORS=["#e8654f","#f2b84c","#5fb0ff","#8b7cf6"]; // A/1 red B/2 yellow C/3 blue D/4 purple

/* view / playback */
let rangeStart=0, rangeEnd=1; // current playback window (full file or one pull)
let curMs=0;
let playing=false;
let speed=1;
let showNpcs=false;
let showTrails=true;
let showCasts=true;
let showWaymarks=true;
let lastFrameT=0;

/* user zoom/pan applied on top of the auto-fit view */
let zoom=1, panX=0, panZ=0;

/* ---- little-endian helpers ---- */
const u16=(o)=>dv.getUint16(o,true);
const u32=(o)=>dv.getUint32(o,true);
const i32=(o)=>dv.getInt32(o,true);
const f32=(o)=>dv.getFloat32(o,true);

/* u16 packed coordinate -> world units. 2000/65535 ≈ 0.030518; centred at 0. */
const unpackCoord=(v)=> v*(2000/65535)-1000;

/* =====================================================================
   Parse
   ===================================================================== */
function resolvePatch(build){
  const patch = (typeof BUILD_TO_PATCH!=="undefined" && BUILD_TO_PATCH[build]) || null;
  return patch && OPCODE_TABLES[patch] ? {patch, table:OPCODE_TABLES[patch]} : null;
}

function parse(buffer){
  raw=new Uint8Array(buffer);
  dv=new DataView(raw.buffer);
  for(let i=0;i<MAGIC.length;i++) if(raw[i]!==MAGIC[i]) throw new Error("Not an FFXIVREPLAY .dat (bad header).");

  const build=i32(OFF_BUILD);
  const resolved=resolvePatch(build);
  if(!resolved) throw new Error(`No opcode table for build ${build}. Add it to opcodes.js to read movement.`);
  const T=resolved.table;
  opTable=T; opPatch=resolved.patch;
  const OP={
    move:T.ActorMove, setpos:T.ActorSetPos,
    pspawn:T.PlayerSpawn, nspawn:T.NpcSpawn,
    cast:T.ActorCast,
    party:T.UpdatePartyMemberPositions,
    allianceN:T.UpdateAllianceNormalMemberPositions,
    allianceS:T.UpdateAllianceSmallMemberPositions,
    mark:T.PlaceFieldMarker,
    preset:T.PlaceFieldMarkerPreset,
  };

  const replayLength=i32(OFF_REPLAY_LEN);

  // walk segments
  segs=[]; let off=0;
  while(off<replayLength){
    const b=DATA_START+off;
    const opcode=u16(b), dataLength=u16(b+2), ms=u32(b+4), oid=u32(b+8);
    segs.push({offset:off,opcode,dataLength,ms,oid});
    off+=SEG_HEADER+dataLength;
  }

  // chapters / pulls (for the "jump to pull" selector)
  chapters=[]; const clen=i32(HEADER_SIZE);
  for(let i=0;i<clen;i++){
    const e=HEADER_SIZE+4+i*CHAPTER_ENTRY;
    chapters.push({type:i32(e),offset:u32(e+4),ms:u32(e+8)});
  }
  const totalMs=u32(OFF_TOTAL_MS)||(segs.length?segs[segs.length-1].ms:0);
  const pullChapters=chapters.filter(c=>PULL_START_TYPES.includes(c.type));
  pulls=pullChapters.map((pc,n)=>{
    const endMs = n<pullChapters.length-1 ? pullChapters[n+1].ms : totalMs;
    return {n:n+1, type:pc.type, startMs:pc.ms, endMs};
  });

  buildTracks(OP);
  parseWaymarks(OP);
  // make sure the scrubbable range also covers waymark placements (they can sit
  // before the first / after the last movement packet)
  if(waymarks.length){
    timeMin=Math.min(timeMin, waymarks[0].ms);
    timeMax=Math.max(timeMax, waymarks[waymarks.length-1].ms);
  }
  return {build, patch:resolved.patch, totalMs};
}

/* Waymark placements over time. Individual PlaceFieldMarker packets are
   [u8 id, u8 active, u16 pad, int32 x, int32 y, int32 z] with coords in
   milli-units (÷1000), same world space as ActorMove. The 8-at-once
   PlaceFieldMarkerPreset uses a struct-of-arrays layout; since recordings that
   carry real data usually use individual packets, the preset is decoded
   best-effort and only kept if every marker lands within the arena bounds. */
function parseWaymarks(OP){
  waymarks=[];
  const b3=()=>{ // expanded world bounds from movement, to sanity-gate presets
    let mnx=Infinity,mxx=-Infinity,mnz=Infinity,mxz=-Infinity,any=false;
    for(const a of actorList) for(const f of a.frames){ any=true;
      if(f.x<mnx)mnx=f.x; if(f.x>mxx)mxx=f.x; if(f.z<mnz)mnz=f.z; if(f.z>mxz)mxz=f.z; }
    if(!any) return null;
    const mx=(mxx-mnx)*0.6+15, mz=(mxz-mnz)*0.6+15;
    return {mnx:mnx-mx,mxx:mxx+mx,mnz:mnz-mz,mxz:mxz+mz};
  };
  const bounds=b3();
  for(const s of segs){
    const base=DATA_START+s.offset+SEG_HEADER;
    if(OP.mark!=null && s.opcode===OP.mark && s.dataLength>=16){
      const id=raw[base];
      if(id>7) continue;
      waymarks.push({ms:s.ms, id, active:raw[base+1]!==0, x:i32(base+4)/1000, z:i32(base+12)/1000});
    } else if(OP.preset!=null && s.opcode===OP.preset && s.dataLength>=96){
      // skip empty preset (all zero)
      let allZero=true; for(let i=0;i<s.dataLength;i++){ if(raw[base+i]!==0){ allZero=false; break; } }
      if(allZero) continue;
      // struct-of-arrays: header, then int32 x[8], y[8], z[8]
      const hdr = s.dataLength>=104 ? 8 : 4;
      const cand=[];
      for(let id=0;id<8;id++){
        const x=i32(base+hdr+id*4)/1000, z=i32(base+hdr+64+id*4)/1000;
        if(x===0 && z===0) continue;
        cand.push({ms:s.ms, id, active:true, x, z});
      }
      if(cand.length && (!bounds || cand.every(w=>w.x>=bounds.mnx&&w.x<=bounds.mxx&&w.z>=bounds.mnz&&w.z<=bounds.mxz)))
        for(const w of cand) waymarks.push(w);
    }
  }
  waymarks.sort((a,b)=>a.ms-b.ms);
}

// Markers active at a given time: latest placement per id, if its last state is on.
function activeWaymarksAt(ms){
  const latest={};
  for(const w of waymarks){ if(w.ms<=ms) latest[w.id]=w; }
  const out=[];
  for(const id in latest){ if(latest[id].active) out.push(latest[id]); }
  return out;
}

function looksLikeName(s){
  if(/^Player \d{1,3}$/.test(s)) return true; // anonymized names the inspector writes
  const parts=s.split(" ");
  if(parts.length!==2) return false;
  for(const p of parts){
    if(p.length<2||p.length>15) return false;
    if(!(p[0]>="A"&&p[0]<="Z")) return false;
    for(const c of p) if(!/[A-Za-z'\-]/.test(c)) return false;
  }
  return true;
}
// Find a 32-byte null-terminated character name inside a spawn payload.
function scanName(base, len){
  const isUpper=(b)=>b>=65&&b<=90;
  // digits allowed so "Player N" reads to its end; looksLikeName still gates.
  const isNameChar=(b)=>(b>=65&&b<=90)||(b>=97&&b<=122)||(b>=48&&b<=57)||b===32||b===39||b===45;
  const end=base+len;
  for(let i=base;i+2<=end;i++){
    if(!isUpper(raw[i])) continue;
    let l=0; while(l<32 && i+l<end && isNameChar(raw[i+l])) l++;
    if(l<3||l>31) continue;
    const s=new TextDecoder().decode(raw.subarray(i,i+l));
    if(looksLikeName(s)) return s;
  }
  return null;
}
// Order in which player names first appear in the data section. This is the same
// scan the inspector uses to number players, so the playback list matches the
// editor (and the in-game party order). Returns Map(name -> first-seen index).
function fileNameOrder(){
  const order=new Map(); let idx=0;
  const end=DATA_START+i32(OFF_REPLAY_LEN);
  const isUpper=(b)=>b>=65&&b<=90;
  const isNameChar=(b)=>(b>=65&&b<=90)||(b>=97&&b<=122)||(b>=48&&b<=57)||b===32||b===39||b===45;
  for(let i=DATA_START;i+32<=end;i++){
    if(!isUpper(raw[i])) continue;
    let len=0; while(len<32 && isNameChar(raw[i+len])) len++;
    if(len===0||len>31) continue;
    let pad=true; for(let j=len;j<32;j++){ if(raw[i+j]!==0){ pad=false; break; } }
    if(!pad) continue;
    const s=new TextDecoder().decode(raw.subarray(i,i+len));
    if(!looksLikeName(s)) continue;
    if(!order.has(s)) order.set(s, idx++);
  }
  return order;
}

function ensureActor(oid){
  let a=actors.get(oid);
  // spawnName = the name decoded from the recording; name = display label, which
  // the inspector can override live via name edits without losing the original.
  if(!a){ a={oid,name:null,spawnName:null,isPlayer:false,frames:[],casts:[]}; actors.set(oid,a); }
  return a;
}

let nameOverrides=null; // {originalSpawnName: editedName} pushed from the inspector
function displayName(a){
  if(nameOverrides && a.spawnName!=null && nameOverrides[a.spawnName]!=null) return nameOverrides[a.spawnName];
  return a.spawnName;
}

function buildTracks(OP){
  actors=new Map();
  const aliveUntil=new Map();   // oid -> ms of its last packet of ANY kind
  for(const s of segs){
    const base=DATA_START+s.offset+SEG_HEADER;
    // Heartbeat: any packet carrying this oid proves the actor is still in the
    // zone, even ones with no position (casts, status, control). We use this so
    // a player who stops to cast doesn't disappear between movement packets.
    const prev=aliveUntil.get(s.oid);
    if(prev==null||s.ms>prev) aliveUntil.set(s.oid,s.ms);

    if(s.opcode===OP.move){
      const x=unpackCoord(u16(base+6));
      const z=unpackCoord(u16(base+10));
      const rot=(raw[base]/255)*Math.PI*2;
      ensureActor(s.oid).frames.push({ms:s.ms,x,z,rot});
    } else if(OP.setpos!=null && s.opcode===OP.setpos){
      if(s.dataLength>=20){
        const x=f32(base+8), z=f32(base+16);
        if(Number.isFinite(x)&&Number.isFinite(z))
          ensureActor(s.oid).frames.push({ms:s.ms,x,z,rot:null});
      }
    } else if(OP.cast!=null && s.opcode===OP.cast){
      // ActorCast: action id (u16 @0) + cast time (float @8). NOTE: the position
      // field in a cast packet is the ability's AIM point (ground target), not
      // where the caster stands — so we deliberately do not move the dot from a
      // cast. We only record the event to pulse a ring at the caster's real
      // (movement-derived) position.
      let ct=f32(base+8); if(!Number.isFinite(ct)||ct<0||ct>60) ct=0;
      ensureActor(s.oid).casts.push({ms:s.ms, action:u16(base), ct});
    } else if(s.opcode===OP.pspawn || (OP.nspawn!=null && s.opcode===OP.nspawn)){
      const a=ensureActor(s.oid);
      const nm=scanName(base, s.dataLength);
      if(nm){ a.spawnName=nm; a.name=displayName(a); }
      if(s.opcode===OP.pspawn) a.isPlayer=true;
    }
  }

  // keep only actors that actually moved (we need at least one position to draw)
  actors.forEach((a,oid)=>{ if(a.frames.length===0){ actors.delete(oid); return; } a.aliveUntil=aliveUntil.get(oid)||0; });
  // sort each track by time (it already is, but spawns can interleave)
  actors.forEach(a=>a.frames.sort((p,q)=>p.ms-q.ms));

  // order players to match the editor / the file: by where each name first
  // appears in the data (which is the in-game party order), then unnamed actors.
  const nameOrder=fileNameOrder();
  const fileIdx=a=> (a.spawnName!=null && nameOrder.has(a.spawnName)) ? nameOrder.get(a.spawnName) : Infinity;
  actorList=[...actors.values()].sort((a,b)=>{
    if(a.isPlayer!==b.isPlayer) return a.isPlayer?-1:1;
    const oa=fileIdx(a), ob=fileIdx(b);
    if(oa!==ob) return oa-ob;
    if(!!a.name!==!!b.name) return a.name?-1:1;
    return a.oid-b.oid;
  });
  actorList.forEach((a,i)=>{ a.color=colorFor(a,i); a.visible=true; });

  // The recorder (local player) is captured at much higher frequency than
  // anyone else, so the player with the most movement frames is almost
  // certainly them. Ring them in amber.
  recorderOid=-1; let mostFrames=0;
  for(const a of actorList){ if(a.isPlayer && a.frames.length>mostFrames){ mostFrames=a.frames.length; recorderOid=a.oid; } }

  // Densify sparse player tracks with party/alliance member-position packets.
  integratePartyPositions([OP.party, OP.allianceN, OP.allianceS]);

  // global time bounds from movement
  timeMin=Infinity; timeMax=0;
  actors.forEach(a=>{
    timeMin=Math.min(timeMin,a.frames[0].ms);
    timeMax=Math.max(timeMax,a.frames[a.frames.length-1].ms);
  });
  if(!Number.isFinite(timeMin)){ timeMin=0; timeMax=1; }
}

/* The server throttles other players' ActorMove to ~1 Hz, leaving 15–20 s gaps
   where a moving player's dot would freeze. The party/alliance member-position
   packets carry every member's position together (~every 5 s, same packed-u16
   coords as ActorMove), so they fill those gaps. These packets carry no object
   IDs — positions are in a fixed slot order — so we recover the slot→actor
   mapping by correlating each slot's positions against the known ActorMove
   tracks (a correct match sits ~0 units away; a wrong one is metres off). Only
   confident, in-bounds matches are merged, so a bad decode adds nothing. */
function integratePartyPositions(ops){
  const players=actorList.filter(a=>a.isPlayer && a.frames.length>1);
  if(players.length<2) return 0;
  // world bounds (+margin) from trusted ActorMove frames, as a sanity gate
  let minX=Infinity,maxX=-Infinity,minZ=Infinity,maxZ=-Infinity;
  for(const a of players) for(const f of a.frames){
    if(f.x<minX)minX=f.x; if(f.x>maxX)maxX=f.x; if(f.z<minZ)minZ=f.z; if(f.z>maxZ)maxZ=f.z;
  }
  const mX=(maxX-minX)*0.25+5, mZ=(maxZ-minZ)*0.25+5;
  const inBounds=(x,z)=> x>=minX-mX&&x<=maxX+mX&&z>=minZ-mZ&&z<=maxZ+mZ;

  // nearest ORIGINAL frame to ms, within 4 s (used only for correlation scoring)
  const nearestPos=(f,ms)=>{
    let lo=0,hi=f.length-1,idx=0;
    if(ms<=f[0].ms) idx=0; else if(ms>=f[hi].ms) idx=hi;
    else{ while(lo<=hi){ const m=(lo+hi)>>1; if(f[m].ms<=ms){idx=m;lo=m+1;} else hi=m-1; } }
    const c=Math.abs(f[Math.min(idx+1,f.length-1)].ms-ms)<Math.abs(f[idx].ms-ms)?f[idx+1]:f[idx];
    return Math.abs(c.ms-ms)<=4000 ? c : null;
  };

  let added=0;
  for(const op of ops){
    if(op==null) continue;
    const psegs=segs.filter(s=>s.opcode===op);
    if(!psegs.length) continue;
    const maxSlots=Math.min(24, Math.floor(psegs[0].dataLength/8)); // 8-byte entries
    if(maxSlots<1) continue;
    const slots=Array.from({length:maxSlots},()=>[]);
    for(const s of psegs){
      const base=DATA_START+s.offset+SEG_HEADER;
      const n=Math.min(maxSlots, Math.floor(s.dataLength/8));
      for(let k=0;k<n;k++){
        const e=base+k*8;
        if(u16(e)===0) continue;            // empty slot
        const x=unpackCoord(u16(e+2)), z=unpackCoord(u16(e+6)); // entry: flag,x,y,z
        if(!inBounds(x,z)) continue;
        slots[k].push({ms:s.ms,x,z});
      }
    }
    const used=new Set();
    for(const samples of slots){
      if(samples.length<3) continue;
      let best=null, bestMed=Infinity;
      for(const a of players){
        if(used.has(a.oid)) continue;
        const ds=[];
        for(const smp of samples){ const p=nearestPos(a.frames,smp.ms); if(p) ds.push(Math.hypot(p.x-smp.x,p.z-smp.z)); }
        if(ds.length<3) continue;
        ds.sort((u,v)=>u-v);
        const med=ds[ds.length>>1];
        if(med<bestMed){ bestMed=med; best=a; }
      }
      if(best && bestMed<=4){ // confident slot→actor match
        used.add(best.oid);
        for(const smp of samples){ best.frames.push({ms:smp.ms,x:smp.x,z:smp.z,rot:null,src:"party"}); added++; }
        best.frames.sort((p,q)=>p.ms-q.ms);
      }
    }
  }
  return added;
}

const PALETTE=["#39d4c8","#f2b84c","#8b7cf6","#e8654f","#5fb0ff","#7ed957",
  "#ff8fce","#ffd166","#a0e8af","#c792ea","#ff9e64","#56d4dd"];
function colorFor(a,i){
  if(a.isPlayer || a.name) return PALETTE[i%PALETTE.length];
  return "#4d5a6b"; // dim grey for unnamed NPCs
}

/* =====================================================================
   Playback model — position of an actor at a given ms (interpolated)
   ===================================================================== */
function sampleAt(a, ms){
  const f=a.frames;
  // Present from a touch before the first position until the last heartbeat
  // packet of any kind (movement, cast, status…), so stationary casters persist.
  const until=Math.max(a.aliveUntil||0, f[f.length-1].ms);
  if(ms<f[0].ms-2000 || ms>until+2000) return null;
  // binary search for last frame <= ms
  let lo=0, hi=f.length-1, idx=0;
  if(ms<=f[0].ms) idx=0;
  else if(ms>=f[hi].ms) idx=hi;
  else{
    while(lo<=hi){ const mid=(lo+hi)>>1; if(f[mid].ms<=ms){idx=mid;lo=mid+1;} else hi=mid-1; }
  }
  const a0=f[idx], a1=f[Math.min(idx+1,f.length-1)];
  // interpolate only across a short gap; otherwise hold the last known spot
  // (the actor was standing still — e.g. casting — not teleporting).
  if(a1!==a0 && a1.ms-a0.ms<=STALE_MS && ms>=a0.ms){
    const t=(ms-a0.ms)/(a1.ms-a0.ms);
    return {x:a0.x+(a1.x-a0.x)*t, z:a0.z+(a1.z-a0.z)*t, rot:a0.rot, fresh:true};
  }
  return {x:a0.x, z:a0.z, rot:a0.rot, fresh:(ms-a0.ms)<3000};
}

function visibleActors(){
  return actorList.filter(a=>a.visible && (showNpcs || a.isPlayer || a.name));
}

/* =====================================================================
   Canvas rendering
   ===================================================================== */
const cv=document.getElementById("stage");
const ctx=cv.getContext("2d");
let view={ox:0,oz:0,scale:1}; // world->screen

function fitBounds(){
  let minX=Infinity,maxX=-Infinity,minZ=Infinity,maxZ=-Infinity,any=false;
  const ext=(x,z)=>{ any=true; if(x<minX)minX=x; if(x>maxX)maxX=x; if(z<minZ)minZ=z; if(z>maxZ)maxZ=z; };
  for(const a of visibleActors())
    for(const f of a.frames){ if(f.ms<rangeStart||f.ms>rangeEnd) continue; ext(f.x,f.z); }
  if(showWaymarks) for(const w of waymarks) ext(w.x,w.z); // keep waymarks in frame
  if(!any){ view={cx:0,cz:0,span:1}; return; }
  const pad=0.12;
  let spanX=Math.max(2,maxX-minX), spanZ=Math.max(2,maxZ-minZ);
  const cx=(minX+maxX)/2, cz=(minZ+maxZ)/2;
  const span=Math.max(spanX,spanZ)*(1+pad);
  view={cx,cz,span};
}
function resetView(){ zoom=1; panX=0; panZ=0; }

function resize(){
  const wrap=cv.parentElement;
  const w=wrap.clientWidth, h=wrap.clientHeight;
  const dpr=window.devicePixelRatio||1;
  cv.width=w*dpr; cv.height=h*dpr;
  cv.style.width=w+"px"; cv.style.height=h+"px";
  ctx.setTransform(dpr,0,0,dpr,0,0);
  draw();
}

// effective pixels-per-world-unit, after applying user zoom
function pxPerUnit(){ return Math.min(cv.clientWidth,cv.clientHeight)/(view.span/zoom); }
function worldToScreen(x,z){
  const w=cv.clientWidth, h=cv.clientHeight, s=pxPerUnit();
  const cx=view.cx+panX, cz=view.cz+panZ;
  // FFXIV: +x east, +z south. Screen: +x right, +y down -> z maps to y directly.
  return [w/2+(x-cx)*s, h/2+(z-cz)*s, s];
}
function screenToWorld(sx,sy){
  const w=cv.clientWidth, h=cv.clientHeight, s=pxPerUnit();
  return { x:(sx-w/2)/s + view.cx+panX, z:(sy-h/2)/s + view.cz+panZ };
}

function draw(){
  const w=cv.clientWidth, h=cv.clientHeight;
  ctx.clearRect(0,0,w,h);
  // backdrop grid
  ctx.fillStyle="#0d1117"; ctx.fillRect(0,0,w,h);
  drawGrid(w,h);

  // waymarks (beneath actors)
  if(showWaymarks){
    ctx.textAlign="center"; ctx.textBaseline="middle";
    for(const wm of activeWaymarksAt(curMs)){
      const [sx,sy]=worldToScreen(wm.x,wm.z);
      const col=WAYMARK_COLORS[wm.id%4];
      ctx.globalAlpha=0.16; ctx.fillStyle=col;
      ctx.beginPath(); ctx.arc(sx,sy,12,0,Math.PI*2); ctx.fill();
      ctx.globalAlpha=0.85; ctx.strokeStyle=col; ctx.lineWidth=2;
      ctx.beginPath(); ctx.arc(sx,sy,12,0,Math.PI*2); ctx.stroke();
      ctx.fillStyle=col; ctx.font="bold 12px ui-monospace,Consolas,monospace";
      ctx.fillText(WAYMARK_LABELS[wm.id], sx, sy);
    }
    ctx.globalAlpha=1; ctx.textAlign="left"; ctx.textBaseline="alphabetic";
  }

  const vis=visibleActors();
  // trails
  if(showTrails){
    for(const a of vis){
      const f=a.frames;
      ctx.lineWidth=1.5; ctx.strokeStyle=a.color; ctx.globalAlpha=0.35;
      ctx.beginPath(); let started=false, prevMs=null;
      for(const fr of f){
        if(fr.ms<curMs-TRAIL_MS||fr.ms>curMs||fr.ms<rangeStart||fr.ms>rangeEnd) continue;
        const [sx,sy]=worldToScreen(fr.x,fr.z);
        if(!started||(prevMs!=null&&fr.ms-prevMs>STALE_MS)){ ctx.moveTo(sx,sy); started=true; }
        else ctx.lineTo(sx,sy);
        prevMs=fr.ms;
      }
      ctx.stroke(); ctx.globalAlpha=1;
    }
  }

  // dots
  for(const a of vis){
    const p=sampleAt(a,curMs);
    if(!p) continue;
    const [sx,sy]=worldToScreen(p.x,p.z);
    const r = a.isPlayer ? 6 : 4;
    // facing wedge
    if(p.rot!=null){
      ctx.strokeStyle=a.color; ctx.globalAlpha=p.fresh?0.9:0.4; ctx.lineWidth=2;
      ctx.beginPath(); ctx.moveTo(sx,sy);
      ctx.lineTo(sx+Math.sin(p.rot)*(r+8), sy+Math.cos(p.rot)*(r+8));
      ctx.stroke();
    }
    ctx.globalAlpha=p.fresh?1:0.45;
    ctx.fillStyle=a.color;
    ctx.beginPath(); ctx.arc(sx,sy,r,0,Math.PI*2); ctx.fill();
    if(a.oid===recorderOid){ ctx.strokeStyle="#f2b84c"; ctx.lineWidth=2; ctx.beginPath(); ctx.arc(sx,sy,r+3,0,Math.PI*2); ctx.stroke(); }
    // label
    if(a.isPlayer||a.name){
      ctx.globalAlpha=p.fresh?0.95:0.4;
      ctx.fillStyle="#d6e2f0"; ctx.font="11px ui-monospace,Consolas,monospace";
      ctx.fillText(a.name||("0x"+a.oid.toString(16)), sx+r+4, sy+4);
    }
    ctx.globalAlpha=1;
  }

  // cast pulses — a ring that fills over the cast's duration, drawn at the
  // caster's actual position (not the ability's aim point).
  if(showCasts){
    for(const a of vis){
      if(!a.casts.length) continue;
      let active=null;
      for(const c of a.casts){
        const dur=Math.max(400, c.ct*1000);
        if(curMs>=c.ms && curMs<=c.ms+dur){ active={c,dur}; }
      }
      if(!active) continue;
      const p=sampleAt(a,curMs); if(!p) continue;
      const [sx,sy]=worldToScreen(p.x,p.z);
      const prog=Math.min(1,(curMs-active.c.ms)/active.dur);
      ctx.strokeStyle="#f2b84c"; ctx.lineWidth=2.5;
      ctx.globalAlpha=0.85;
      ctx.beginPath(); ctx.arc(sx,sy,(a.isPlayer?9:7)+prog*6,-Math.PI/2,-Math.PI/2+prog*Math.PI*2); ctx.stroke();
      ctx.globalAlpha=0.18;
      ctx.beginPath(); ctx.arc(sx,sy,(a.isPlayer?9:7)+6,0,Math.PI*2); ctx.stroke();
      ctx.globalAlpha=1;
    }
  }

  // clock
  ctx.fillStyle="#7d8da0"; ctx.font="12px ui-monospace,Consolas,monospace";
  ctx.fillText(fmtClock(curMs-rangeStart)+" / "+fmtClock(rangeEnd-rangeStart), 12, h-12);
}

function drawGrid(w,h){
  // grid lines every 5 world units, faded
  if(!view.span) return;
  const size=Math.min(w,h), s=size/view.span;
  const step=niceStep(view.span);
  ctx.strokeStyle="#1b2531"; ctx.lineWidth=1;
  const x0=view.cx-view.span/2, x1=view.cx+view.span/2;
  const z0=view.cz-view.span/2, z1=view.cz+view.span/2;
  ctx.beginPath();
  for(let gx=Math.ceil(x0/step)*step; gx<=x1; gx+=step){ const [sx]=worldToScreen(gx,view.cz); ctx.moveTo(sx,0); ctx.lineTo(sx,h); }
  for(let gz=Math.ceil(z0/step)*step; gz<=z1; gz+=step){ const [,sy]=worldToScreen(view.cx,gz); ctx.moveTo(0,sy); ctx.lineTo(w,sy); }
  ctx.stroke();
}
function niceStep(span){
  const target=span/8;
  const pow=Math.pow(10,Math.floor(Math.log10(target)));
  const n=target/pow;
  const m = n<1.5?1 : n<3?2 : n<7?5 : 10;
  return m*pow;
}

/* =====================================================================
   Time formatting
   ===================================================================== */
function fmtClock(ms){
  ms=Math.max(0,ms);
  let s=Math.floor(ms/1000); const m=Math.floor(s/60); s%=60;
  return `${String(m).padStart(2,"0")}:${String(s).padStart(2,"0")}`;
}

/* =====================================================================
   Playback loop
   ===================================================================== */
// The animation loop only runs while playing; when paused we draw on demand
// (scrub, toggles) so the page can go idle instead of spinning every frame.
function tick(t){
  if(!playing){ lastFrameT=0; return; }
  if(!lastFrameT) lastFrameT=t;
  const dt=(t-lastFrameT)*speed;
  lastFrameT=t;
  curMs+=dt;
  if(curMs>=rangeEnd){ curMs=rangeEnd; setPlaying(false); syncScrubber(); draw(); return; }
  syncScrubber();
  draw();
  requestAnimationFrame(tick);
}

function setPlaying(p){
  const was=playing;
  playing=p; lastFrameT=0;
  const b=document.getElementById("btn-play");
  b.textContent=p?"❚❚ Pause":"▶ Play";
  b.classList.toggle("playing",p);
  if(p && curMs>=rangeEnd) curMs=rangeStart;
  if(p && !was) requestAnimationFrame(tick);
}

function syncScrubber(){
  const sc=document.getElementById("scrub");
  sc.value=String(((curMs-rangeStart)/(rangeEnd-rangeStart||1))*1000|0);
  document.getElementById("clock").textContent=fmtClock(curMs-rangeStart)+" / "+fmtClock(rangeEnd-rangeStart);
}

/* =====================================================================
   Range / pull selection
   ===================================================================== */
function setRange(start,end){
  rangeStart=start; rangeEnd=Math.max(start+1,end);
  curMs=start; resetView(); fitBounds(); syncScrubber(); draw();
}

function buildPullSelect(){
  const sel=document.getElementById("pullsel");
  sel.innerHTML="";
  const full=document.createElement("option");
  full.value="-1"; full.textContent=`Whole recording (${fmtClock(timeMax-timeMin)})`;
  sel.appendChild(full);
  pulls.forEach((p,i)=>{
    const o=document.createElement("option");
    o.value=String(i);
    o.textContent=`Pull ${p.n} · ${CHAPTER_TYPE_NAMES[p.type]||p.type} · ${fmtClock(p.startMs)} (${fmtClock(p.endMs-p.startMs)})`;
    sel.appendChild(o);
  });
  sel.onchange=()=>{
    const v=+sel.value;
    if(v<0) setRange(timeMin,timeMax);
    else setRange(pulls[v].startMs, pulls[v].endMs);
    setPlaying(false); syncScrubber();
  };
}

function buildLegend(){
  const wrap=document.getElementById("legend"); wrap.innerHTML="";
  const named=actorList.filter(a=>a.isPlayer||a.name);
  const npcCount=actorList.length-named.length;
  named.forEach(a=>{
    const row=document.createElement("label"); row.className="leg-row";
    row.innerHTML=`<input type="checkbox" ${a.visible?"checked":""}>
      <span class="sw" style="background:${a.color}"></span>
      <span class="nm">${a.name?escapeHtml(a.name):"0x"+a.oid.toString(16)}</span>
      <span class="cnt">${a.frames.length}</span>`;
    row.querySelector("input").addEventListener("change",e=>{ a.visible=e.target.checked; fitBounds(); draw(); });
    wrap.appendChild(row);
  });
  document.getElementById("npc-note").textContent =
    npcCount>0 ? `+ ${npcCount} unnamed actor${npcCount>1?"s":""} (toggle “show NPCs”)` : "";
}
function escapeHtml(s){return s.replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));}

/* =====================================================================
   File loading + module API
   ===================================================================== */
function loadBytes(name, buffer){
  fileName=name;
  try{
    const info=parse(buffer);
    if(actorList.length===0) throw new Error("No movement packets found in this recording.");
    document.getElementById("meta").textContent =
      `${fileName} · patch ${info.patch} · ${actorList.filter(a=>a.isPlayer||a.name).length} named, ${actors.size} total`;
    buildPullSelect(); buildLegend();
    setRange(timeMin,timeMax);
    setPlaying(false); syncScrubber();
    document.getElementById("viewer").classList.remove("hidden");
    resize();
    toast(`Loaded ${actors.size} actors over ${fmtClock(timeMax-timeMin)}.`);
  }catch(err){ toast(err.message,true); }
}

// Relabel dots from the inspector's edits, keyed by each actor's original
// (spawn-packet) name. Called whenever a name is edited or anonymized.
function applyNames(map){
  nameOverrides=map||null;
  for(const a of actorList) a.name=displayName(a);
  buildLegend(); draw();
}
document.addEventListener("rw-names",e=>{ if(actorList.length) applyNames(e.detail); });

// The canvas needs a real size; recompute when the playback tab becomes visible.
function onShown(){ resize(); draw(); }

/* Inspect what position-bearing packets the loaded file actually contains, so a
   denser-movement source (party/alliance member positions) can be decoded with
   the right byte layout instead of guessed. Returns a JSON-friendly summary. */
function diag(){
  if(!opTable) return {error:"no file loaded"};
  const nameOf={}; for(const k in opTable) nameOf[opTable[k]]=k;
  const hist=new Map();
  for(const s of segs) hist.set(s.opcode,(hist.get(s.opcode)||0)+1);

  const candidates=["ActorMove","ActorSetPos","UpdatePartyMemberPositions",
    "UpdateAllianceNormalMemberPositions","UpdateAllianceSmallMemberPositions",
    "PlayerSpawn","NpcSpawn"];
  const detail={};
  for(const nm of candidates){
    const op=opTable[nm];
    if(op==null){ detail[nm]={present:false}; continue; }
    const list=segs.filter(s=>s.opcode===op);
    const sizes={}; for(const s of list) sizes[s.dataLength]=(sizes[s.dataLength]||0)+1;
    let firstHex=null;
    if(list.length){
      const s=list[0], b=DATA_START+s.offset+SEG_HEADER, arr=[];
      for(let i=0;i<Math.min(s.dataLength,80);i++) arr.push(raw[b+i].toString(16).padStart(2,"0"));
      firstHex=arr.join(" ");
    }
    detail[nm]={present:true, opcode:op, count:list.length, payloadSizes:sizes, firstHex};
  }
  // per-actor ActorMove frame counts, to show the throttling asymmetry
  const perActor=actorList.filter(a=>a.isPlayer||a.spawnName)
    .map(a=>({name:a.name||("0x"+a.oid.toString(16)), oid:a.oid, frames:a.frames.length}))
    .sort((x,y)=>y.frames-x.frames);
  const top=[...hist.entries()].sort((a,b)=>b[1]-a[1]).slice(0,16)
    .map(([op,c])=>({name:nameOf[op]||("0x"+op.toString(16)), count:c}));
  const wmSample=waymarks.slice(0,10).map(w=>({id:WAYMARK_LABELS[w.id],active:w.active,x:+w.x.toFixed(1),z:+w.z.toFixed(1),ms:w.ms}));
  return {patch:opPatch, segCount:segs.length, players:perActor, candidates:detail, topOpcodes:top,
    timeMin, timeMax, waymarks:waymarks.length, waymarkSample:wmSample};
}

/* ---- controls wiring ---- */
document.getElementById("btn-play").addEventListener("click",()=>setPlaying(!playing));
document.getElementById("scrub").addEventListener("input",e=>{
  curMs=rangeStart+(+e.target.value/1000)*(rangeEnd-rangeStart);
  setPlaying(false); syncScrubber(); draw();
});
document.querySelectorAll(".spd").forEach(b=>b.addEventListener("click",()=>{
  speed=+b.dataset.s;
  document.querySelectorAll(".spd").forEach(x=>x.classList.toggle("on",x===b));
}));
document.getElementById("chk-npc").addEventListener("change",e=>{ showNpcs=e.target.checked; fitBounds(); draw(); });
document.getElementById("chk-trail").addEventListener("change",e=>{ showTrails=e.target.checked; draw(); });
document.getElementById("chk-cast").addEventListener("change",e=>{ showCasts=e.target.checked; draw(); });
const chkWm=document.getElementById("chk-waymark");
if(chkWm) chkWm.addEventListener("change",e=>{ showWaymarks=e.target.checked; fitBounds(); draw(); });

/* ---- zoom & pan on the map ---- */
cv.addEventListener("wheel",e=>{
  e.preventDefault();
  const r=cv.getBoundingClientRect();
  const mx=e.clientX-r.left, my=e.clientY-r.top;
  const before=screenToWorld(mx,my);
  zoom=Math.min(40, Math.max(0.6, zoom*Math.exp(-e.deltaY*0.0015)));
  const after=screenToWorld(mx,my);          // keep the point under the cursor fixed
  panX+=before.x-after.x; panZ+=before.z-after.z;
  draw();
},{passive:false});
let dragging=false, dragX=0, dragY=0;
cv.addEventListener("mousedown",e=>{ dragging=true; dragX=e.clientX; dragY=e.clientY; cv.style.cursor="grabbing"; });
window.addEventListener("mousemove",e=>{
  if(!dragging) return;
  const s=pxPerUnit();
  panX-=(e.clientX-dragX)/s; panZ-=(e.clientY-dragY)/s;
  dragX=e.clientX; dragY=e.clientY; draw();
});
window.addEventListener("mouseup",()=>{ if(dragging){ dragging=false; cv.style.cursor=""; } });
cv.addEventListener("dblclick",()=>{ resetView(); draw(); });   // reset zoom/pan

window.addEventListener("resize",resize);
window.addEventListener("keydown",e=>{
  // only drive playback when its tab is the visible one
  const tab=document.getElementById("tab-playback");
  if(!tab || tab.classList.contains("hidden")) return;
  if(e.target.tagName==="INPUT"&&e.target.type==="text") return;
  if(!actorList.length) return;
  if(e.code==="Space"){ e.preventDefault(); setPlaying(!playing); }
  else if(e.code==="ArrowRight"){ curMs=Math.min(rangeEnd,curMs+1000); setPlaying(false); syncScrubber(); draw(); }
  else if(e.code==="ArrowLeft"){ curMs=Math.max(rangeStart,curMs-1000); setPlaying(false); syncScrubber(); draw(); }
});

let toastTimer=null;
function toast(msg,isErr=false){
  const t=document.getElementById("toast");
  t.textContent=msg; t.className="show"+(isErr?" err":"");
  clearTimeout(toastTimer); toastTimer=setTimeout(()=>t.className=isErr?"err":"",2800);
}

/* Public API — the shell feeds the file and toggles visibility. */
window.Playback = { load: loadBytes, onShown, applyNames, diag };
})();
