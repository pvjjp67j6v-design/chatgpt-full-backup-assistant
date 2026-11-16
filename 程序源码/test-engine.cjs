const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('assert');
const crypto=require('crypto').webcrypto;
const root=__dirname;
function el(){let text='';return {style:{},sheet:{cssRules:[],insertRule(){}},append(){},appendChild(){},remove(){},setAttribute(){},addEventListener(){},
  get innerHTML(){return text},set innerHTML(x){text=x.replace(/&amp;/g,'&').replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&quot;/g,'"')},get textContent(){return text},set textContent(x){text=x}};}
const sandbox={console,URL,URLSearchParams,Blob,TextEncoder,TextDecoder,Uint8Array,ArrayBuffer,crypto,structuredClone,AbortController,
  atob,btoa,setTimeout,clearTimeout,setInterval,clearInterval,
  document:{createElement:el,head:el(),body:el(),documentElement:{lang:'zh-CN'},cookie:'',addEventListener(){},querySelectorAll(){return []}},
  navigator:{language:'zh-CN',userAgent:'Node test'},location:new URL('https://chatgpt.com/'),
  localStorage:{getItem(){return null},setItem(){},removeItem(){}},MutationObserver:class{observe(){}},
  fetch:async()=>{throw Error('network forbidden in fixture test')},
  FileReader:class {readAsDataURL(blob){blob.arrayBuffer().then(x=>{this.result='data:'+blob.type+';base64,'+Buffer.from(x).toString('base64');this.onload()})}}
};
sandbox.window=sandbox;sandbox.self=sandbox;sandbox.top=sandbox;sandbox.addEventListener=()=>{};
let code=fs.readFileSync(path.join(root,'bridge.user.js'),'utf8');
code=code.replace('setTimeout(desktopBridge,1500);',`globalThis.engine={processConversation,conversationToMarkdown,extractImageFiles,addImageFilesToZip,generateZip,ScriptStorage,JSZip:jszip.default,desktopBridge};`);
vm.createContext(sandbox);vm.runInContext(code,sandbox,{timeout:10000});
(async()=>{
  const e=sandbox.engine;
  const settings={'exporter:enable_timestamp':true,'exporter:timestamp_markdown':true,'exporter:timestamp_24h':true,'exporter:enable_sources':true};
  e.ScriptStorage.get=k=>settings[k];
  const id='00000000-0000-4000-8000-000000000001',image='data:image/png;base64,iVBORw0KGgo=';
  const raw={id,title:'图片与时间戳测试',create_time:1700000000,update_time:1700000001,current_node:'a',mapping:{
    u:{id:'u',parent:null,children:['a'],message:{id:'u',author:{role:'user'},recipient:'all',create_time:1700000000,content:{content_type:'multimodal_text',parts:['图片',{content_type:'image_asset_pointer',asset_pointer:image}]}}},
    a:{id:'a',parent:'u',children:[],message:{id:'a',author:{role:'assistant'},recipient:'all',create_time:1700000001,content:{content_type:'text',parts:['# 示例\n\n公式 $x^2$ 和 **粗体**']},metadata:{model_slug:'gpt-4o'}}}
  }};
  const meta=['title','source','create_time','update_time'].map(name=>({name,value:'{'+name+'}'}));
  const embed=e.conversationToMarkdown(e.processConversation(raw,{enableThinking:true}),meta,'embed');
  assert(embed.includes(image));assert(embed.includes('<time datetime='));assert(embed.includes('create_time:'));assert(embed.includes('update_time:'));assert(embed.includes(id));
  const assets=new Map(),separate=await e.extractImageFiles(raw,assets);
  const files=e.conversationToMarkdown(e.processConversation(separate,{enableThinking:true}),meta,'files');
  assert(files.includes('assets/image-'));assert(!files.includes('data:image/'));assert.equal(assets.size,1);assert(raw.mapping.u.message.content.parts[1].asset_pointer===image);
  const zip=new e.JSZip();zip.file('ChatGPT-test.md',files);e.addImageFilesToZip(zip,assets);
  const blob=await e.generateZip(zip), reopened=await e.JSZip.loadAsync(await blob.arrayBuffer());
  assert.equal(Object.keys(reopened.files).filter(x=>x.endsWith('.png')).length,1);
  // Exercise the real bridge retry boundary with an injected HTTP 429.
  sandbox.location=new URL('https://chatgpt.com/#backupToken=test&attempt=one');
  let events=[];
  sandbox.GM_xmlhttpRequest=opts=>{
    const p=new URL(opts.url).pathname;events.push({p,data:opts.data?JSON.parse(opts.data):null});
    const value=p==='/state'?{manifest:[],done:[]}:{ok:true};
    queueMicrotask(()=>opts.onload({status:200,responseText:JSON.stringify(value)}));
  };
  // fetch was lexically captured at engine initialization, so make its original delegate mutable.
  // Run a separate realm with a failing network delegate from the start.
  const failureSandbox={...sandbox,engine:undefined,fetch:async()=>({ok:false,status:429,statusText:'Too Many Requests'})};
  failureSandbox.window=failureSandbox;failureSandbox.self=failureSandbox;failureSandbox.top=failureSandbox;
  vm.createContext(failureSandbox);vm.runInContext(code,failureSandbox,{timeout:10000});
  await failureSandbox.engine.desktopBridge();
  assert(events.some(x=>x.p==='/failed' && x.data.message.includes('429')));
  assert(!events.some(x=>x.p==='/complete'));
  // Full bridge: fail after JSON commit, then resume in a new browser realm.
  const id2='00000000-0000-4000-8000-000000000002';
  let store={manifest:[],done:[],cache:{},chunks:{},saved:{},failed:false,complete:false,fetchCount:0},failOnce=true;
  const rpc=opts=>{
    const url=new URL(opts.url),p=url.pathname,d=opts.data?JSON.parse(opts.data):{};
    let result={ok:true},status=200;
    try {
      if(p==='/state')result={manifest:store.manifest,done:store.done};
      if(p==='/manifest')store.manifest=d.items;
      if(p==='/cache') {if(opts.method==='GET')result={raw:store.cache[url.searchParams.get('id')]||null};else store.cache[d.id]=d.raw;}
      const key=d.type+':'+d.part;
      if(p==='/begin')store.chunks[key]=[];
      if(p==='/chunk')store.chunks[key].push(Buffer.from(d.data,'base64'));
      if(p==='/commit') {
        if(d.type==='files'&&failOnce){failOnce=false;throw Error('simulated disk failure');}
        store.saved[key]=Buffer.concat(store.chunks[key]);store.done.push(key);
      }
      if(p==='/failed')store.failed=true;
      if(p==='/complete')store.complete=true;
    }catch(e){status=400;result={error:e.message};}
    queueMicrotask(()=>opts.onload({status,responseText:JSON.stringify(result)}));
  };
  const mockFetch=async url=>{
    const u=new URL(url);let data;
    if(u.pathname==='/api/auth/session')data={accessToken:'test'};
    else if(u.pathname.includes('/accounts/check/'))data={accounts:{}};
    else if(u.pathname==='/backend-api/conversations')data={items:[{id,title:'同名'},{id:id2,title:'同名'}],total:2};
    else if(u.pathname.includes('/gizmos/snorlax/sidebar'))data={items:[],cursor:null};
    else if(u.pathname.includes('/conversation/')){store.fetchCount++;data=structuredClone(raw);data.id=u.pathname.split('/').pop();data.title='同名';}
    else throw Error('unexpected test URL '+url);
    return {ok:true,json:async()=>data,headers:{get(){return null}}};
  };
  for(let attempt=0;attempt<2;attempt++) {
    const s={...sandbox,engine:undefined,fetch:mockFetch,GM_xmlhttpRequest:rpc,location:new URL('https://chatgpt.com/#backupToken=test&attempt='+attempt)};
    s.window=s;s.self=s;s.top=s;vm.createContext(s);vm.runInContext(code,s,{timeout:10000});await s.engine.desktopBridge();
    if(attempt===0){assert(store.failed);assert(!store.complete);assert.deepEqual(store.done,['json:1']);}
  }
  assert(store.complete);assert.equal(store.fetchCount,2);assert.equal(store.done.length,3);
  assert.equal(JSON.parse(store.saved['json:1']).length,2);
  for(const type of ['files','embed']) {
    const z=await e.JSZip.loadAsync(store.saved[type+':1']);
    assert.equal(Object.keys(z.files).filter(x=>x.endsWith('.md')).length,2);
    assert.equal(JSON.parse(await z.file('_backup-manifest.json').async('string')).ids.length,2);
  }
  fs.writeFileSync(path.join(root,'导出引擎测试结果.txt'),'PASS: real Exporter renderer; both image modes; ZIP assets; timestamps; four metadata fields; immutable original JSON; HTTP 429 failure policy; full three-format pipeline; duplicate chat titles; failure after JSON followed by new-tab resume; successful batches and cached conversations reused.\n');
  console.log('Engine and failure-path checks: PASS');
})().catch(e=>{console.error(e);process.exitCode=1});
