"use strict";
/* =====================================================================
   Patch chain - shared by the workbench and the opcode inspector.

   patchdiffs.js says which opcode number became which at every game patch,
   straight out of the binary's IPC vtable. This file turns those hops into
   the two questions the tools actually ask:

     patchChainMap(from,to)  how do this patch's opcodes reach that patch's?
     patchTable(patch)       what was each IPC name's opcode back then?

   The first is what transpose runs on, and it needs no names at all. The
   second exists only for labels and for the few packets looked up by name;
   it works by carrying the latest patch's names *backwards* down the same
   chain, so exactly one hand-maintained name table is needed no matter how
   far back a recording goes.
   ===================================================================== */

const PATCH_POS=new Map((typeof PATCH_CHAIN!=="undefined"?PATCH_CHAIN:[]).map((v,i)=>[v,i]));
const inChain=(p)=>PATCH_POS.has(p);

// Decode one hop (the move from the previous patch to `version`) out of the packed
// data, once. `lost` is everything the diff saw on the old side but could not carry
// forward: candidates it couldn't tell apart, plus opcodes the patch deleted.
const hopCache=new Map();
function patchHop(version){
  if(hopCache.has(version)) return hopCache.get(version);
  const packed=(typeof PATCH_DIFFS!=="undefined") ? PATCH_DIFFS[version] : null;
  let hop=null;
  if(packed){
    const o=unpackOpcodes(packed.o), n=unpackOpcodes(packed.n);
    const map=new Map();
    for(let i=0;i<o.length;i++) map.set(o[i],n[i]);
    hop={version, map, lost:new Set([...unpackOpcodes(packed.a), ...unpackOpcodes(packed.r)])};
  }
  hopCache.set(version,hop);
  return hop;
}

// Every opcode a patch is known to use: the old side of the hop that leaves it.
const universeCache=new Map();
function patchUniverse(patch){
  if(universeCache.has(patch)) return universeCache.get(patch);
  const i=PATCH_POS.get(patch);
  let set=null;
  if(i!=null){
    const next=(i+1<PATCH_CHAIN.length) ? patchHop(PATCH_CHAIN[i+1]) : null;
    if(next) set=new Set([...next.map.keys(), ...next.lost]);
    else { const own=patchHop(patch); if(own) set=new Set(own.map.values()); }
  }
  universeCache.set(patch,set);
  return set;
}

// Compose the hops from `from` up to `to`, one patch at a time, over every opcode
// `from` is known to have used. Returns {map, lost} where lost says which patch
// dropped each opcode and why — an opcode that falls out mid-chain cannot be
// carried the rest of the way, and pretending otherwise is how you ship a replay
// that crashes the client.
const chainCache=new Map();
function patchChainMap(from,to){
  if(!inChain(from)||!inChain(to)) return null;
  const i=PATCH_POS.get(from), j=PATCH_POS.get(to);
  if(j<i) return null;
  const key=from+">"+to;
  if(chainCache.has(key)) return chainCache.get(key);

  const map=new Map(), lost=new Map();
  if(j>i){
    const first=patchHop(PATCH_CHAIN[i+1]);
    if(!first){ chainCache.set(key,null); return null; }
    for(const op of first.map.keys()) map.set(op,op);
    for(const op of first.lost) map.set(op,op);
    for(let k=i+1;k<=j;k++){
      const hop=patchHop(PATCH_CHAIN[k]);
      if(!hop){ chainCache.set(key,null); return null; }
      for(const [orig,cur] of map){
        const next=hop.map.get(cur);
        if(next!==undefined) map.set(orig,next);
        else { map.delete(orig); lost.set(orig, hop.lost.has(cur) ? `dropped in ${hop.version}` : `absent from the ${hop.version} diff`); }
      }
    }
  }
  const result={map,lost};
  chainCache.set(key,result);
  return result;
}

/* Which patch was this recording made on? Ask the file, not the build number.

   Every patch reshuffles the whole IPC vtable, so a recording's set of opcodes
   only fits the patch it was actually made on: score each candidate by how many
   of the file's packets its vtable accounts for and can carry all the way to
   `to`, and the right patch comes out at 100% while its neighbours sit well
   below. That matters because the alternative — a hand-maintained build number
   table — is exactly the kind of thing that goes wrong quietly: guess the patch
   one hotfix off and every opcode still remaps, just to the wrong packet.

   Returns {patch, packets, kinds, runnerUp, margin, confident} or null. */
function detectPatch(hist, to){
  to = to || LATEST_PATCH;
  if(!inChain(to)) return null;
  let total=0, kindTotal=0;
  for(const [op,n] of hist){ if(op<0xf000){ total+=n; kindTotal++; } }
  if(!total) return null;

  const scores=[];
  for(const from of PATCH_CHAIN){
    if(PATCH_POS.get(from)>PATCH_POS.get(to)) continue;
    const uni=patchUniverse(from);
    const chain=(from===to) ? null : patchChainMap(from,to);
    if(!uni || (from!==to && !chain)) continue;
    let packets=0, kinds=0;
    for(const [op,n] of hist){
      if(op>=0xf000) continue;
      // in this patch's vtable, and still standing at the far end of the chain
      if(from===to ? uni.has(op) : chain.map.has(op)){ packets+=n; kinds++; }
    }
    scores.push({patch:from, packets:packets/total, kinds:kinds/kindTotal});
  }
  if(!scores.length) return null;
  scores.sort((a,b)=> (b.packets-a.packets) || (b.kinds-a.kinds));

  const best=scores[0], next=scores[1]||null;
  // Score on opcode *kinds*, not packet share: one chatty opcode (ActorMove is
  // half the file) pins the packet share near 100% for several patches, while
  // the count of opcodes a patch can account for separates them cleanly.
  const margin=next ? best.kinds-next.kinds : 1;
  return {
    patch:best.patch, packets:best.packets, kinds:best.kinds,
    runnerUp: next?next.patch:null, margin,
    // Only worth acting on when the fit is exact and nothing else comes close.
    confident: best.packets>=0.9999 && best.kinds>=0.9999 && margin>0.01
  };
}

// A pasted-in table only counts if it actually has entries. An empty one is a
// placeholder for a patch someone registered a build number for without pasting
// the names — that should fall through to derivation, not report zero names.
const hasNames=(t)=>!!t && Object.keys(t).length>0;

// IPC names for a patch. The latest patch's table is pasted in by hand (and is the
// one that gets hand-corrected); everything older is that table's names carried
// backwards down the chain. A pasted table wins where one exists, so the tables
// already verified against real recordings keep behaving exactly as they did.
const tableCache=new Map();
function patchTable(patch){
  if(!patch) return null;
  if(hasNames(OPCODE_TABLES[patch])) return OPCODE_TABLES[patch];
  if(tableCache.has(patch)) return tableCache.get(patch);
  let out=null;
  const latest=OPCODE_TABLES[LATEST_PATCH];
  const chain=(hasNames(latest) && inChain(patch) && inChain(LATEST_PATCH)) ? patchChainMap(patch,LATEST_PATCH) : null;
  if(chain){
    const back=new Map();
    for(const [was,now] of chain.map) back.set(now,was);
    out={};
    for(const name in latest){ const here=back.get(latest[name]); if(here!==undefined) out[name]=here; }
  }
  tableCache.set(patch,out);
  return out;
}

