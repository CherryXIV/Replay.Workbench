"use strict";
/* =====================================================================
   Page shell: the drop zone, feeding each file to the editor
   (window.Inspector, app.js).

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

  function readFile(f){
    const r=new FileReader();
    r.onload=()=>loadBytes(f.name, r.result);
    r.readAsArrayBuffer(f);
  }

  function loadBytes(name, buffer){
    try{ Inspector.load(name, buffer); }catch(e){ console.error("inspector:",e); return; }
    drop.classList.add("compact");
    document.getElementById("editor").classList.remove("hidden");
  }
})();
