"use strict";
/* =====================================================================
   Page shell: one shared drop zone + tab switching feeding two modules
   (window.Inspector = editor, window.Playback = position map). Each module
   parses its own copy of the file, so the editor's in-place name edits never
   disturb the playback buffer. Name edits flow editor→playback over a
   "rw-names" CustomEvent (see app.js / timeline.js).

   Nothing is persisted: the recording lives only in memory for the life of the
   tab. A refresh clears it and the user re-drops the file. We also delete any
   IndexedDB store left behind by older builds that used to stash the file.
   ===================================================================== */
(function(){
  try{ indexedDB.deleteDatabase("replayWorkbench"); }catch(_){}

  const drop=document.getElementById("drop"), fileInput=document.getElementById("file");
  ["dragenter","dragover"].forEach(ev=>drop.addEventListener(ev,e=>{e.preventDefault();drop.classList.add("over");}));
  ["dragleave","drop"].forEach(ev=>drop.addEventListener(ev,e=>{e.preventDefault();drop.classList.remove("over");}));
  drop.addEventListener("drop",e=>{const f=e.dataTransfer.files[0]; if(f) readFile(f);});
  fileInput.addEventListener("change",e=>{const f=e.target.files[0]; if(f) readFile(f);});

  let loaded=false;
  // honor a deep link like index.html#playback (e.g. the old timeline.html URL)
  let activeTab = location.hash==="#playback" ? "playback" : "editor";

  function readFile(f){
    const r=new FileReader();
    r.onload=()=>loadBytes(f.name, r.result);
    r.readAsArrayBuffer(f);
  }

  function loadBytes(name, buffer){
    let ok=false;
    if(window.Inspector){ try{ Inspector.load(name, buffer.slice(0)); ok=true; }catch(e){ console.error("inspector:",e); } }
    if(window.Playback){ try{ Playback.load(name, buffer.slice(0)); ok=true; }catch(e){ console.error("playback:",e); } }
    if(!ok) return;
    loaded=true;
    drop.classList.add("compact");
    document.getElementById("tabs").classList.remove("hidden");
    showTab(activeTab);
  }

  function showTab(which){
    activeTab=which;
    document.getElementById("tab-editor").classList.toggle("hidden", which!=="editor");
    document.getElementById("tab-playback").classList.toggle("hidden", which!=="playback");
    document.querySelectorAll(".tabbtn").forEach(b=>b.classList.toggle("cur", b.dataset.tab===which));
    if(which==="playback" && window.Playback) Playback.onShown();
  }
  document.querySelectorAll(".tabbtn").forEach(b=>b.addEventListener("click",()=>{ if(loaded) showTab(b.dataset.tab); }));
})();
