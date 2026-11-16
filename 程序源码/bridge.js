// This file is inserted inside the pinned MIT-licensed Exporter bundle.
async function desktopBridge() {
  const params = new URLSearchParams(location.hash.slice(1));
  const token = params.get('backupToken'), attempt = params.get('attempt');
  if (!token || !attempt || window.top !== window.self) return;
  const endpoint = 'http://127.0.0.1:18746';
  const badge = document.createElement('div');
  Object.assign(badge.style, {position:'fixed',right:'18px',bottom:'18px',zIndex:2147483647,
    padding:'14px 20px',background:'#174d46',color:'white',borderRadius:'12px',font:'14px sans-serif',maxWidth:'420px'});
  document.body.appendChild(badge);
  const tell = text => badge.textContent = 'ChatGPT 备份助手：' + text;
  function rpc(path, data) {
    return new Promise((resolve,reject)=>GM_xmlhttpRequest({
      method:data === undefined?'GET':'POST', url:endpoint+path,
      headers:{'X-Backup-Token':token,'X-Backup-Attempt':attempt,'Content-Type':'application/json'},
      data:data === undefined?undefined:JSON.stringify(data), timeout:120000,
      onload:r=>{try {let x=JSON.parse(r.responseText); if(r.status!==200) throw new Error(x.error||'本地保存失败'); resolve(x);}catch(e){reject(e);}},
      onerror:()=>reject(new Error('无法连接备份助手，请保持 EXE 打开')),
      ontimeout:()=>reject(new Error('本地保存超时'))
    }));
  }
  let active = true, heartbeat;
  const report = async text => {tell(text); await rpc('/progress',{text});};
  const pause = ms=>new Promise(r=>setTimeout(r,ms));
  const originalFetch = fetch;
  // Every HTTP request is bounded; errors, including 429, escape to the EXE retry policy.
  fetch = async (url, options={}) => {
    if (!active) throw new Error('任务已结束');
    const ctrl=new AbortController(), timer=setTimeout(()=>ctrl.abort(),90000);
    try {
      const res=await originalFetch(url,{...options,signal:ctrl.signal});
      if(!res.ok) throw new Error('HTTP '+res.status+' '+res.statusText);
      return res;
    } finally {clearTimeout(timer);}
  };
  try {
    await rpc('/claim',{});
    heartbeat=setInterval(()=>rpc('/heartbeat',{}).catch(()=>{active=false; clearInterval(heartbeat);}),5000);
    const settings={ 'exporter:language':'zh-CN','exporter:filename_format':'ChatGPT-{title}',
      'exporter:enable_timestamp':true,'exporter:timestamp_24h':true,'exporter:timestamp_markdown':true,
      'exporter:timestamp_html':true,'exporter:enable_meta':true,'exporter:enable_thinking':true,
      'exporter:enable_sources':true,'exporter:export_all_limit':20000 };
    ScriptStorage.get = key=>settings[key];
    const meta=['title','source','create_time','update_time'].map(name=>({name,value:'{'+name+'}'}));
    let state=await rpc('/state'), list=state.manifest;
    if(!list || !list.length) {
      await report('正在读取全部对话列表（包含项目对话）');
      const all=new Map();
      let expected=null;
      for(let offset=0;;offset+=100) {
        const page=await fetchConversations(offset,100);
        if(!Array.isArray(page.items)) throw new Error('对话列表格式已变化，未保存为成功');
        if(Number.isFinite(page.total)) expected=page.total;
        for(const item of page.items) all.set(item.id,item);
        if(offset>=20000 || all.size>20000 || (expected!==null && expected>20000)) throw new Error('对话超过 20000 条上限，请分批备份');
        if(!page.items.length || (expected!==null && offset+page.items.length>=expected)) break;
        if(page.items.length<100 && expected===null) break;
        await report('已读取 '+all.size+' 条对话');
      }
      if(expected!==null && all.size<expected) throw new Error('对话列表不完整：'+all.size+'/'+expected);
      const projects=await fetchProjects();
      for(const project of projects) {
        let cursor=0; const cursors=new Set();
        do {
          const page=await fetchProjectConversations(project.id,cursor,50);
          if(!Array.isArray(page.items)) throw new Error('项目对话列表格式已变化');
          for(const item of page.items) all.set(item.id,item);
          if(all.size>20000) throw new Error('对话超过 20000 条上限');
          cursor=page.cursor;
          if(cursor!=null) {if(cursors.has(cursor)) throw new Error('项目分页重复，停止以防漏备份'); cursors.add(cursor);}
        } while(cursor!=null);
      }
      list=Array.from(all.values()).map(x=>({id:x.id,title:x.title||'ChatGPT',update_time:x.update_time}));
      if(!list.length) throw new Error('未读到任何对话，请确认登录了正确的账号');
      await rpc('/manifest',{items:list});
      state=await rpc('/state');
    }
    const batchSize=25, totalParts=Math.ceil(list.length/batchSize);
    async function save(type,part,blob,ids) {
      await report('正在保存 '+type+'，第 '+part+'/'+totalParts+' 批');
      const data=await blobToDataURL(blob), base64=data.slice(data.indexOf(',')+1);
      const chunkSize=1024*1024;
      await rpc('/begin',{type,part,ids});
      for(let off=0;off<base64.length;off+=chunkSize) {
        await rpc('/chunk',{type,part,offset:off/4*3,data:base64.slice(off,off+chunkSize)});
      }
      await rpc('/commit',{type,part,ids});
    }
    for(let start=0;start<list.length;start+=batchSize) {
      const part=Math.floor(start/batchSize)+1, batch=list.slice(start,start+batchSize), ids=batch.map(x=>x.id);
      const pending=['json','files','embed'].filter(t=>!state.done.includes(t+':'+part));
      if(!pending.length) continue;
      const raw=[];
      for(const item of batch) {
        await report('读取对话 '+(start+raw.length+1)+'/'+list.length+'：'+item.title);
        let cached=await rpc('/cache?id='+encodeURIComponent(item.id));
        let conv=cached.raw;
        if(!conv) {
          conv=await fetchConversation(item.id);
          if(!conv.mapping || !Object.keys(conv.mapping).length) throw new Error('对话为空或结构不完整：'+item.title);
          await rpc('/cache',{id:item.id,raw:conv});
          await pause(350);
        }
        raw.push(conv);
      }
      if(pending.includes('json')) await save('json',part,new Blob([conversationToJson(raw)],{type:'application/json'}),ids);
      if(pending.some(t=>t!=='json')) {
        const hydrated=[];
        for(let i=0;i<raw.length;i++) {
          const conv=structuredClone(raw[i]);
          await report('处理图片 '+(start+i+1)+'/'+list.length);
          // Strict resolution: upstream's normal UI silently tolerates unavailable images.
          // A backup with unresolved images must instead be reported as failed.
          const sources=collectImageSources(conv);
          const unique=new Map();
          for(const image of sources) {
            if(!/^(sediment|file-service):\/\//.test(image.src)) {
              if(/^data:image\//.test(image.src)) continue;
              if(/^https?:/.test(image.src)) {
                const res=await fetch(image.src); image.replace(await blobToDataURL(await res.blob()));
              } else if(!image.src.startsWith('assets/')) throw new Error('不支持的图片地址：'+image.src.slice(0,70));
              continue;
            }
            const pointer=image.src.replace(/^file-service:/,'sediment:');
            if(!unique.has(pointer)) unique.set(pointer,await fetchImageFromPointer(pointer));
            const src=unique.get(pointer);
            if(!src || !src.startsWith('data:image/')) throw new Error('图片无法下载：'+raw[i].title);
            image.replace(src);
          }
          hydrated.push(conv);
        }
        for(const type of ['files','embed']) {
          if(!pending.includes(type)) continue;
          const zip=new jszip.default(), assets=new Map(), used=new Set();
          for(let i=0;i<hydrated.length;i++) {
            const conv=type==='files'?await extractImageFiles(hydrated[i],assets):hydrated[i];
            const processed=processConversation(conv,{enableThinking:true});
            let name=getFileNameWithFormat('ChatGPT-{title}','md',{title:processed.title,chatId:processed.id});
            if(used.has(name.toLowerCase())) name=name.slice(0,-3)+'-'+processed.id+'.md';
            used.add(name.toLowerCase());
            zip.file(name,standardizeLineBreaks(conversationToMarkdown(processed,meta,type==='files'?'files':'embed')));
          }
          addImageFilesToZip(zip,assets);
          zip.file('_backup-manifest.json',JSON.stringify({ids,type,exporter:'pionxzh 2.38.3',settings}));
          await save(type,part,await generateZip(zip),ids);
        }
      }
      state=await rpc('/state');
    }
    await rpc('/complete',{});
    tell('三类备份已完成并验证，可以关闭此标签页');
  } catch(e) {
    tell('本次尝试未完成：'+e.message+'。请查看 EXE 的重试状态');
    try {await rpc('/failed',{message:String(e.message||e)});} catch {}
  } finally {active=false; clearInterval(heartbeat);}
}
setTimeout(desktopBridge,1500);
