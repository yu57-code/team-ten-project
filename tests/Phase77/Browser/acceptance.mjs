import {spawn} from "node:child_process";
import {mkdtemp,rm,writeFile} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import assert from "node:assert/strict";
const base="https://localhost:7018", profiles=[];
const chrome=process.env.PHASE77_CHROMIUM;
const delay=ms=>new Promise(r=>setTimeout(r,ms));
class WaitTimeout extends Error {}
let activeCase="Chromium startup", activeStep="open browser", activePage, expectation="browser debugger reachable";
function step(test, page, action, expected) {
 activeCase=test; activePage=page; activeStep=action; expectation=expected;
}
async function wait(fn,label,ms=15000){
 const at=Date.now()+ms; let probeError;
 while(Date.now()<at){try{if(await fn())return;}catch(e){probeError=e?.constructor?.name;}await delay(120);}
 throw new WaitTimeout(label,{cause:probeError});
}
let loginRequests=0, frames=0, pollMessages=0, leaks=0;
const sensitive=new Set();
async function profile(port,fallback=false){
 const dir=await mkdtemp(path.join(os.tmpdir(),"ImagingAI451-Chromium-"));
 const proc=spawn(chrome,["--user-data-dir="+dir,"--remote-debugging-port="+port,"--no-proxy-server","--disable-background-networking","about:blank"],{windowsHide:true,stdio:"ignore"});
 profiles.push({dir,proc});const root="http://127.0.0.1:"+port;
 await wait(async()=> (await fetch(root+"/json/version")).ok,"Chromium startup");
 return async function newPage(){
 const tab=await (await fetch(root+"/json/new?about:blank",{method:"PUT"})).json(),ws=new WebSocket(tab.webSocketDebuggerUrl);
 await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j});
 let id=0, documentSerial=0, socketHandshakes=0, documentBaseline=0, httpSequence=0;
 const responses=new Map(),requests=new Map(), http=[];
 const apiNames=new Set(["/api/auth/csrf","/api/auth/login","/api/auth/otp/verify","/api/auth/otp/resend","/api/auth/select-role","/api/auth/me","/api/auth/logout"]);
 const safeCodes=new Set(["invalid_csrf","invalid_request","invalid_credentials","invalid_session","invalid_role_selection","role_not_allowed","logout_required","rate_limited","service_unavailable","session_store_unavailable","session_unavailable","account_store_unavailable","pending_store_unavailable"]);
 const record=(p)=>{
   const u=new URL(p.response.url);
   if(u.origin!==base || !apiNames.has(u.pathname))return;
   const retry=Object.entries(p.response.headers).find(([k])=>k.toLowerCase()==="retry-after")?.[1];
   const row={sequence:++httpSequence,request:p.requestId,endpoint:u.pathname,status:p.response.status,retryAfter:retry!==undefined};
   http.push(row);
   if(http.length>40)http.shift();
 };

 const scan=(x)=>{const v=JSON.stringify(x)??"";for(const secret of sensitive)if(v.includes(secret)){leaks++;break}};
 const cmd=(method,params={})=>new Promise((resolve,reject)=>{const key=++id;const timer=setTimeout(()=>{responses.delete(key);reject(new WaitTimeout("Chromium diagnostic command"));},4000);
 responses.set(key,{resolve:r=>{clearTimeout(timer);resolve(r);},reject:e=>{clearTimeout(timer);reject(e);}});
 try{ws.send(JSON.stringify({id:key,method,params}));}catch(e){responses.get(key)?.reject(e);responses.delete(key);}});
 ws.onmessage=e=>{const m=JSON.parse(e.data),p=m.params;if(m.id){const request=responses.get(m.id);responses.delete(m.id);m.error?request?.reject(Error("browser command failed")):request?.resolve(m.result);return;}
 if(m.method==="Network.webSocketFrameReceived"||m.method==="Network.webSocketFrameSent"){frames++;scan(p.response.opcode===2?Buffer.from(p.response.payloadData,"base64").toString():p.response.payloadData);}
 if(m.method==="Runtime.consoleAPICalled"||m.method==="Runtime.exceptionThrown")scan(p);
 if(m.method==="Network.requestWillBeSent"){const u=p.request.url;scan(u);if(u===base+"/api/auth/login"||u===base+"/api/auth/otp/verify"){loginRequests++;}else{scan(p.request.postData);for(const each of p.request.postDataEntries??[])if(each.bytes)scan(Buffer.from(each.bytes,"base64").toString());}if(u.startsWith(base))requests.set(p.requestId,u);}
 if(m.method==="Page.frameNavigated"&&!p.frame.parentId)documentSerial++;
 if(m.method==="Network.webSocketHandshakeResponseReceived"&&[101,200].includes(p.response.status))socketHandshakes++;
 if(m.method==="Network.responseReceived"){
    record(p);
    if(fallback&&p.response.url.startsWith(base+"/_blazor?id="))pollMessages++;
 }
 if(m.method==="Network.responseReceivedExtraInfo"){
    const cookie=Object.entries(p.headers).find(([k])=>k.toLowerCase()==="set-cookie")?.[1];
    if(typeof cookie==="string")for(const line of cookie.split("\n")){
      const match=line.match(/^__Host-ImagingAI\.(?:DevAuth|DevRole|DevCsrf|DevOtp)=([^;]+)/);
      if(match?.[1])sensitive.add(match[1]);
    }
 }
 if(m.method==="Network.loadingFinished"&&requests.has(p.requestId)){const u=requests.get(p.requestId);requests.delete(p.requestId);
 if(u.includes("/reviewer")||u.includes("/admin")||u.includes("/login")||u.includes("/_blazor")||u.includes("/api/auth/"))cmd("Network.getResponseBody",{requestId:p.requestId}).then(r=>{const body=r.base64Encoded?Buffer.from(r.body,"base64").toString():r.body;
 const row=http.find(x=>x.request===p.requestId);
 if(row){try{const data=JSON.parse(body);if(safeCodes.has(data.code))row.code=data.code;
 if(data.state==="roleSelectionRequired"||data.state==="authenticated")row.state=data.state;}catch{}}
 if(u.endsWith("/csrf")){try{const token=JSON.parse(body).requestToken;if(typeof token==="string"&&token.length)sensitive.add(token)}catch{}}else scan(body);
 }).catch(()=>{});}
 };
 for(const domain of ["Runtime","Page","Network"])await cmd(domain+".enable");
 await cmd("Page.addScriptToEvaluateOnNewDocument",{source:fallback?"window.WebSocket=undefined;":`{const w=WebSocket;window.WebSocket=class extends w {constructor(...a){super(...a);if(new URL(a[0]).pathname==='/_blazor')window.ownedSocket=this;}}}`});
 const ev=async(expression)=>{const r=await cmd("Runtime.evaluate",{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw Error("browser expression");return r.result.value;};
 const body=()=>ev("document.body?.innerText");
 const nav=s=>cmd("Page.navigate",{url:base+s});
 const loginReady=()=>ev("location.pathname==='/login' && !location.search && !!document.querySelector('#acc') && !!document.querySelector('#pwd') && !!document.querySelector('.lf-submit:not([disabled])')");
 const ready=()=>wait(loginReady,"login route and active credentials submit");
 const diagnose=async()=>({
  ...(await ev(`({
    path: ["/login","/reviewer","/admin"].includes(location.pathname) ? location.pathname : "(other)",
    view: location.search==="?view=identity" ? "identity" : "login-or-other",
    credentialsForm: Boolean(document.querySelector("#acc")&&document.querySelector("#pwd")),
    submitEnabled: Boolean(document.querySelector(".lf-submit:not([disabled])")),
    roleChoice: Boolean(Array.from(document.querySelectorAll("button")).some(x=>x.innerText.includes("醫療／資料審核人員"))),
    authenticatedIdentityRendered: Boolean(document.querySelector(".ia-user-name")),
    logoutEnabled: Boolean(document.querySelector("button[aria-label=登出]:not([disabled])")),
    webSocketOpen: window.ownedSocket?.readyState===1
  })`).catch(()=>({documentProbeUnavailable:true}))),
  documentSerial,fullNavigationObserved:documentSerial>documentBaseline,socketHandshakes,pollMessages,authHttp:http.map(({request,sequence,...safe})=>safe),
  pendingCookiePresent:await cmd("Network.getCookies",{urls:[base]})
   .then(r=>r.cookies.some(c=>c.name==="__Host-ImagingAI.DevRole")).catch(()=>null)
 });

 const fill=(u,p)=>ev("document.querySelector('#acc').value="+JSON.stringify(u)+";document.querySelector('#pwd').value="+JSON.stringify(p));
 const click=()=>ev("document.querySelector('.lf-submit').click();true");
 const press=async()=>{await cmd("Page.bringToFront");await ev("document.querySelector('#pwd').focus();true");for(const type of ["rawKeyDown","char","keyUp"])await cmd("Input.dispatchKeyEvent",{type,key:"Enter",code:"Enter",text:type==="char"?"\r":"",windowsVirtualKeyCode:13});};
 const req=(u)=>ev("fetch("+JSON.stringify(u)+").then(r=>r.status)");
 const expectRole=(user,role)=>wait(()=>ev(`location.pathname==="${role==="SYSTEM_ADMIN"?"/admin":"/reviewer"}" &&
   document.querySelector(".ia-user-name")?.textContent.trim()===${JSON.stringify(user)} &&
   document.querySelector(".ia-brand-sub")?.textContent.trim()===${JSON.stringify(role==="SYSTEM_ADMIN"?"系統管理／維護人員":"醫療／資料審核人員")} &&
   document.body.innerText.includes("目前業務功能尚未開放") &&
   !!document.querySelector("button[aria-label=登出]:not([disabled])")`),"trusted home and interactive logout control");

 const logout=async(test)=>{
  await cmd("Page.bringToFront");
  step(test,page,"wait for the identity page logout control","enabled logout control in established Circuit");
  await wait(()=>ev("document.querySelector('button[aria-label=登出]:not([disabled])')!==null"),"logout control");
  const start=httpSequence, documentBefore=documentSerial;documentBaseline=documentBefore;
  step(test,page,"click logout and await API response","logout POST returns 204 after CSRF bootstrap");
  await ev("document.querySelector('button[aria-label=登出]').click();true");
  await wait(()=>http.filter(x=>x.sequence>start).some(x=>x.endpoint==="/api/auth/logout" || (x.endpoint==="/api/auth/csrf"&&x.status!==200)),
      "logout or failed CSRF response");
  const outcome=http.filter(x=>x.sequence>start).find(x=>x.endpoint==="/api/auth/logout");
  assert.equal(outcome?.status,204);
  step(test,page,"verify full navigation after 204","new document at /login without identity view");
  await wait(async()=>documentSerial>documentBefore&&await ev("location.pathname==='/login'&&!location.search"),"anonymous full navigation");
  step(test,page,"verify new anonymous Circuit readiness","account and password fields visible and login submit enabled");
  await ready();
  step(test,page,"verify server-side session state","GET me returns 401");
  assert.equal(await req("/api/auth/me"),401);
 };
 const page={cmd,ev,body,nav,ready,fill,click,press,req,expectRole,logout,diagnose,documents:()=>documentSerial,sequence:()=>httpSequence,markDocument:()=>{documentBaseline=documentSerial;},responsesAfter:(since,endpoint)=>http.filter(x=>x.sequence>since&&x.endpoint===endpoint),handshakes:()=>socketHandshakes,close:()=>ws.close()};
 return page;
 };
}
const lineReader=(await import("node:readline")).createInterface({input:process.stdin});
let otp=null;
lineReader.on("line", line=>{otp=line; if (/^[0-9]{6}$/.test(line)) sensitive.add(line);});
try {
 const a=await profile(17446),p=await a();
 const pass="phase75-test-password";sensitive.add(pass);
 const apiStatus=async(path,method="GET")=>p.ev("fetch("+JSON.stringify(path)+",{method:"+JSON.stringify(method)+",redirect:'manual'}).then(r=>r.status)");
 const enterOtp=async()=>{await wait(()=>otp!==null,"fake sender delivery"); await p.ev(`{
 const fields=document.querySelectorAll('[data-otp-digit]');
 const clipboard=new DataTransfer();
 clipboard.setData('text/plain',${JSON.stringify(otp)});
 fields[0].dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:clipboard}));
 if(Array.from(fields,x=>x.value).join('')!==${JSON.stringify(otp)})throw new Error('OTP paste did not populate six cells');
 true;
 }`);};
 const start=async(user="413570452")=>{otp=null;await p.fill(user,pass);await p.click();
 await wait(()=>p.ev("document.querySelectorAll('[data-otp-digit]').length===6 && !!document.querySelector('button[type=submit]:not(:disabled)')"),"OTP UI");};
 if(process.argv.includes("completion")){
   const {unlink}=await import("node:fs/promises");
   const scope=process.env.PHASE77_CONTROL;
   const tabs=await profile(17447), mutations=await profile(17448);
   const sole=await tabs(), third=await mutations();
   const begin=async(page,user)=>{
     otp=null;await page.fill(user,pass);await page.click();
     await wait(()=>page.ev("document.querySelectorAll('[data-otp-digit]').length===6"),"OTP prompt");
     await wait(()=>otp!==null,"OTP delivered");
   };
   const finish=async(page)=>{
     const code=otp;
     await wait(()=>page.ev(`{
       const f=document.querySelectorAll('[data-otp-digit]');if(f.length!==6)false;
       else{
        const data=new DataTransfer();data.setData('text/plain',${JSON.stringify(code)});
        f[0].dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:data}));
        Array.from(f,x=>x.value).join('')===${JSON.stringify(code)};
       }
     }`),"OTP paste ready");
     await page.click();
   };
   const cookies=async(page)=> (await page.cmd("Network.getCookies",{urls:[base]})).cookies;
   const get=(page,name)=>page.ev("import('/js/devAuth.js').then(api=>api["+JSON.stringify(name)+"]()).then(r=>r.status)");
   const post=(page,operation,body,token)=>page.ev(`
     (async()=>{const api=await import('/js/devAuth.js');
       const token=${JSON.stringify(token??null)}??(await api.getCsrf()).json.requestToken;
       return fetch('/api/auth/'+${JSON.stringify(operation)},{method:'POST',
         headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token},
         body:JSON.stringify(${JSON.stringify(body)})
       }).then(r=>r.status);
     })()`);
   const restoreCookie=(page,c)=>page.cmd("Network.setCookie",{name:c.name,value:c.value,url:base,path:"/",secure:true,httpOnly:true});
   const readyRole=page=>wait(()=>page.ev("document.body.innerText.includes('角色選擇剩餘')"),"role selection");
   step("C3",p,"reload role-selection document","old role ticket rejected");
   await p.nav("/login");await p.ready();await begin(p,"413570452");await finish(p);await readyRole(p);
   const oldRole=(await cookies(p)).find(c=>c.name==="__Host-ImagingAI.DevRole");assert.ok(oldRole);
   await p.nav("/login");await p.ready();
   assert.equal((await cookies(p)).some(c=>c.name===oldRole.name),false);
   await restoreCookie(p,oldRole);
   assert.equal(await post(p,"select-role",{roleCode:"MEDICAL_REVIEWER"}),401);
   console.log("PASS C3.1 role-selection reload cancels the old ticket");

   step("C3",sole,"return after OTP and replay the old OTP cookie","spent OTP cannot issue another Session");
   await sole.nav("/login");await sole.ready();
   await begin(sole,"413570359");
   const oldOtp=(await cookies(sole)).find(c=>c.name==="__Host-ImagingAI.DevOtp");assert.ok(oldOtp);
   const spent=otp;
   const otpDocument=sole.documents();
   await finish(sole);await sole.expectRole("413570359","MEDICAL_REVIEWER");
   await sole.ev("history.back();true");
   await wait(()=>sole.documents()>otpDocument+1,"browser back after OTP");
   assert.equal(await get(sole,"me"),200);
   assert.equal(await sole.ev("document.querySelectorAll('[data-otp-digit]').length"),0);
   await sole.nav("/reviewer");await sole.expectRole("413570359","MEDICAL_REVIEWER");await sole.logout("return-test logout");
   await restoreCookie(sole,oldOtp);
   assert.equal(await post(sole,"otp/verify",{code:spent}),401);
   assert.equal(await get(sole,"me"),401);
   console.log("PASS C3.2 browser back after OTP does not recreate the OTP stage; replay denied");

   const adminLogin=async(page)=>{
     await page.nav("/login");await page.ready();await begin(page,"413570438");await finish(page);
     await page.expectRole("413570438","SYSTEM_ADMIN");
   };
   const network=(page,offline)=>page.cmd("Network.emulateNetworkConditions",{offline,latency:0,downloadThroughput:-1,uploadThroughput:-1});
   const socketReconnect=async(page,socketCount)=>wait(async()=>page.handshakes()>socketCount &&
      await page.ev("window.ownedSocket?.readyState===1"),"replacement WebSocket",30000);
   step("C3",third,"healthy Circuit reconnect","same document and valid identity");
   await adminLogin(third);
   const doc=third.documents(), count=third.handshakes();
   await third.ev("window.ownedSocket.close(4001);true");
   await socketReconnect(third,count);
   assert.equal(third.documents(),doc);assert.equal(await get(third,"me"),200);
   await third.expectRole("413570438","SYSTEM_ADMIN");await third.logout("healthy reconnect logout");
   console.log("PASS C3.3 healthy WebSocket reconnect retains the same valid Circuit");

   for(const [label,suffix] of [["C3.4","disabled"],["C3.5","changed-role"]]){
     step("C3",third,label+" mutate account while disconnected","stale identity removed and old ticket denied");
     await adminLogin(third);
     const sockets=third.handshakes();
     await network(third,true);
     await third.ev("window.ownedSocket.close(4001);true");
     await wait(()=>third.ev("window.ownedSocket?.readyState===3"),"browser socket disconnected");
     await writeFile(scope+"."+suffix,"on",{flag:"wx"});
     try{
       await network(third,false);
       await socketReconnect(third,sockets);
       await wait(()=>third.ev("!document.querySelector('.ia-user-name,.ia-shell--admin,.ia-shell--reviewer')"),
         "revoked reconnect removes protected identity",72000);
       assert.equal(await get(third,"me"),401);
     }finally{await unlink(scope+"."+suffix);await network(third,false);}
     assert.equal(await get(third,"me"),401);
     console.log("PASS "+label+" synthetic account mutation while offline rejects the old ticket and removes protected Circuit content");
   }

   const other=await a();
   step("C3",p,"parallel selection in shared context","one selected role, one Session");
   await other.nav("/login");await other.ready();await p.nav("/login");await p.ready();
   await begin(p,"413570452");await finish(p);await readyRole(p);
   const token=await p.ev("import('/js/devAuth.js').then(x=>x.getCsrf()).then(r=>r.json.requestToken)");
   const results=await Promise.all([post(p,"select-role",{roleCode:"MEDICAL_REVIEWER"},token),
     post(other,"select-role",{roleCode:"SYSTEM_ADMIN"},token)]);
   assert.equal(results.filter(status=>status===200).length,1);
   assert.ok(results.every(status=>[200,400,401,403].includes(status)));
   const winner=results[0]===200?"MEDICAL_REVIEWER":"SYSTEM_ADMIN";
   for(const page of [p,other])
     assert.equal(await page.ev("import('/js/devAuth.js').then(x=>x.me()).then(r=>r.json.activeRoleCode)"),winner);
   assert.equal(leaks,0);
   console.log("PASS C3.6 concurrent distinct role selection produced one confirmation and one active role");
   p.close();sole.close();third.close();other.close();
 }else
 if(process.argv.includes("outage")){
   const pending=await (await profile(17447))();
   await p.nav("/login");await p.ready(); await start("413570438");await enterOtp();await p.click();
   await p.expectRole("413570438","SYSTEM_ADMIN");
   const ticket=(await p.cmd("Network.getCookies",{urls:[base]})).cookies.find(c=>c.name==="__Host-ImagingAI.DevAuth");
   assert.ok(ticket?.value);
   await pending.nav("/login");await pending.ready();otp=null;await pending.fill("413570359",pass);await pending.click();
   await wait(()=>pending.ev("document.querySelectorAll('[data-otp-digit]').length===6"),"pending before outage");
   await wait(()=>otp!==null,"pending OTP fixture");
   const pendingCode=otp;
   const signal=process.env.PHASE77_CONTROL;
   const {readFile}=await import("node:fs/promises");
   const ack=async suffix=>{try{return Boolean(await readFile(signal+suffix,"utf8"));}catch(error){if(error.code==="ENOENT")return false;throw error;}};
   step("OTP API outage",p,"stop owned API, request protected page using old cookie","503 without redirect or protected page");
   const stoppedAt=Date.now();
   await writeFile(signal+".stop","stop",{flag:"wx"});
   await wait(()=>ack(".stopped"),"owned API exit and port release");
   const request=(path,cookie=false)=>fetch(base+path,{redirect:"manual",cache:"no-store",signal:AbortSignal.timeout(9000),
     headers:cookie?{Cookie:ticket.name+"="+ticket.value}:{}});
   assert.equal((await request("/login")).status,200);
   const httpStarted=Date.now(), failure=await request("/admin",true);
   assert.equal(failure.status,503);assert.equal(failure.headers.has("location"),false);
   assert.doesNotMatch(await failure.text(),/ia-shell--admin|ia-user-name/);
   console.log("PASS outage HTTP 503, Location absent, protected HTML absent; "+(Date.now()-httpStarted)+" ms");
   assert.equal((await request("/api/auth/me",true)).status,502);
   await pending.ev(`{const i=document.querySelector('[data-otp-digit]');i.value=${JSON.stringify(pendingCode)};i.dispatchEvent(new Event('input',{bubbles:true}));true;}`);
   await pending.click();
   await wait(()=>pending.ev("location.pathname==='/login'&&!!document.querySelector('#acc')"),"OTP outage returns to login");
   assert.equal((await pending.cmd("Network.getCookies",{urls:[base]})).cookies.some(c=>c.name==="__Host-ImagingAI.DevAuth"),false);
   await wait(()=>p.ev("!document.querySelector('.ia-user-name,.ia-shell--admin')"),"outage existing Circuit revalidation",72000);
   console.log("PASS outage existing Circuit removed protected tree; "+(Date.now()-stoppedAt)+" ms");
   await writeFile(signal+".restart","restart",{flag:"wx"});
   await wait(()=>ack(".restarted"),"owned API recovery");
   assert.equal((await request("/api/auth/me",true)).status,401);
   const sockets=p.handshakes();
   await p.ev("window.ownedSocket.close(4001);true");
   await wait(()=>p.handshakes()>sockets,"outage replacement WebSocket",18000);
   assert.equal(await p.ev("!!document.querySelector('.ia-user-name,.ia-shell--admin')"),false);
   console.log("PASS recovered API rejects old ticket and replacement transport leaves revoked Circuit anonymous");
   p.close();pending.close();
 }else
 if(process.argv.includes("race")){
   const second=await a();
   await p.nav("/login"); await p.ready(); await second.nav("/login"); await second.ready();
   step("OTP shared browser context",p,"replace challenge from a second tab","first code fails, one parallel verification succeeds");
   await start("413570438");
   await wait(()=>otp!==null,"first OTP delivery");const first=otp;
   otp=null;await second.fill("413570438",pass);await second.click();
   await wait(()=>second.ev("document.querySelectorAll('[data-otp-digit]').length===6"),"second tab OTP");
   await wait(()=>otp!==null,"second OTP delivery");const next=otp;
   assert.notEqual(next,first);
   await p.ev(`{const i=document.querySelector('[data-otp-digit]');i.value=${JSON.stringify(first)};i.dispatchEvent(new Event('input',{bubbles:true}));true;}`);
   await p.click();
   await wait(()=>p.ev("document.body.innerText.includes('驗證碼錯誤或驗證已失效')"),"old shared challenge denied");
   const token=await p.ev("import('/js/devAuth.js').then(x=>x.getCsrf()).then(r=>r.json.requestToken)");
   const submit=page=>page.ev(`fetch('/api/auth/otp/verify',{
      method:'POST',headers:{'X-CSRF-TOKEN':${JSON.stringify(token)},'Content-Type':'application/json'},
      body:JSON.stringify({code:${JSON.stringify(next)}})
   }).then(r=>r.status)`);
   const results=await Promise.all([submit(p),submit(second)]);
   assert.equal(results.filter(status=>status===200).length,1);
   assert.ok(results.every(status=>[200,400,401,403].includes(status)));
   await p.nav("/admin");await p.expectRole("413570438","SYSTEM_ADMIN");
   await second.nav("/admin");await second.expectRole("413570438","SYSTEM_ADMIN");
   await p.logout("shared browser logout");
   assert.equal(await second.req("/api/auth/me"),401);
   await wait(()=>second.ev("!document.querySelector('.ia-user-name')"),"second tab Circuit revocation",72000);
   assert.ok(frames>0);assert.equal(leaks,0);
   console.log("PASS shared-context replacement, parallel OTP single success and second-tab logout/Circuit revocation");
   p.close();second.close();
 }else
 if(process.argv.includes("timed")){
   const newRolePage=await (await profile(17447))(), newOtpPage=await (await profile(17448))();
   await p.nav("/login"); await p.ready();
   await start("413570438"); await enterOtp(); await p.click(); await p.expectRole("413570438","SYSTEM_ADMIN");
   const sessionAt=Date.now();
   const sessionCookie=(await p.cmd("Network.getCookies",{urls:[base]})).cookies.find(c=>c.name==="__Host-ImagingAI.DevAuth");
   assert.ok(sessionCookie?.value);
   await newRolePage.nav("/login"); await newRolePage.ready();
   otp=null; await newRolePage.fill("413570452",pass); await newRolePage.click();
   await wait(()=>newRolePage.ev("document.querySelectorAll('[data-otp-digit]').length===6"),"timed role OTP stage");
   await wait(()=>otp!==null,"timed role code");
   await newRolePage.ev(`{const n=document.querySelector('[data-otp-digit]');n.value=${JSON.stringify(otp)};n.dispatchEvent(new Event('input',{bubbles:true}));true;}`);
   await newRolePage.click();
   await wait(()=>newRolePage.ev("document.body.innerText.includes('角色選擇剩餘')"),"timed role pending");
   const roleAt=Date.now();
   await newOtpPage.nav("/login"); await newOtpPage.ready();
   otp=null; await newOtpPage.fill("413570359",pass); await newOtpPage.click();
   await wait(()=>newOtpPage.ev("document.querySelectorAll('[data-otp-digit]').length===6"),"timed OTP pending");
   await wait(()=>otp!==null,"timed OTP code");
   const otpAt=Date.now(), expiredCode=otp;
   const rawPost=(page,operation,payload)=>page.ev(`import('/js/devAuth.js').then(async api=>{
     const csrf=await api.getCsrf();
     return fetch('/api/auth/'+${JSON.stringify(operation)},{
       method:'POST',headers:{'X-CSRF-TOKEN':csrf.json.requestToken,'Content-Type':'application/json'},
       body:JSON.stringify(${JSON.stringify(payload)})
     }).then(r=>r.status);
   })`);
   await Promise.all([
      (async()=>{await delay(Math.max(0,roleAt+120000-Date.now()));
         assert.equal(await rawPost(newRolePage,"select-role",{roleCode:"MEDICAL_REVIEWER"}),401);
         console.log("PASS Chromium role pending natural expiry 120s");})(),
      (async()=>{await delay(Math.max(0,otpAt+300000-Date.now()));
         assert.equal(await rawPost(newOtpPage,"otp/verify",{code:expiredCode}),401);
         console.log("PASS Chromium OTP natural expiry 300s");})(),
      (async()=>{for(const elapsed of [1200,1780]){
          await delay(Math.max(0,sessionAt+elapsed*1000-Date.now()));
          assert.equal(await apiStatus("/api/auth/me"),200);
          const current=(await p.cmd("Network.getCookies",{urls:[base]})).cookies.find(c=>c.name==="__Host-ImagingAI.DevAuth");
          assert.ok(current?.value===sessionCookie.value);
       }
       await delay(Math.max(0,sessionAt+1800000-Date.now()));
       await p.cmd("Network.setCookie",{name:sessionCookie.name,value:sessionCookie.value,url:base,secure:true,httpOnly:true,path:"/"});
       assert.equal(await apiStatus("/api/auth/me"),401);
       await wait(()=>p.ev("!document.querySelector('.ia-user-name')"),"expired Circuit revokes",72000);
       console.log("PASS Chromium Session natural expiry 30min, no renewal and old ticket refused");})()
   ]);
   p.close();newRolePage.close();newOtpPage.close();
 }else{
 step("OTP Chromium",p,"open login and validate gates","OTP stage before role or identity");
 await p.nav("/login");await p.ready();await start();
 assert.equal(await apiStatus("/api/auth/me"),401);
 assert.equal(await apiStatus("/api/auth/otp/verify"),404);
 assert.equal(await apiStatus("/api/auth/otp/resend","POST"),400);
 const pendingCookies=(await p.cmd("Network.getCookies",{urls:[base]})).cookies;
 const pendingCookie=pendingCookies.find(c=>c.name==="__Host-ImagingAI.DevOtp");
 assert.ok(pendingCookie?.secure && pendingCookie.httpOnly && pendingCookie.session && pendingCookie.sameSite==="Lax");
 assert.equal(await p.ev("import('/js/devAuth.js').then(x=>x.selectRole('SYSTEM_ADMIN')).then(r=>r.status)"),401);
 assert.equal(await apiStatus("/api/upload"),404);
 assert.equal(await apiStatus("/api/upload","POST"),404);
 assert.equal(await p.ev("!!document.querySelector('.ia-user-name')"),false);
 await p.click();
 await wait(()=>p.ev("document.body.innerText.includes('請確認輸入資料後再試')"),"empty OTP message");
 const wrong=otp==="000000"?"000001":"000000";
 await p.ev("let fields=document.querySelectorAll('[data-otp-digit]');fields[0].value="+JSON.stringify(wrong)+";fields[0].dispatchEvent(new Event('input',{bubbles:true}));true");
 await p.click();
 await wait(()=>p.ev("document.body.innerText.includes('驗證碼錯誤或驗證已失效')"),"wrong OTP message");
 await enterOtp();
 step("OTP Chromium",p,"verify then select reviewer","no identity before selection and trusted reviewer after selection");
 await p.click();
 await wait(()=>p.ev("document.body.innerText.includes('角色選擇剩餘')"),"role picker");
 assert.equal(await apiStatus("/api/auth/me"),401);
 await p.ev("Array.from(document.querySelectorAll('button')).find(b=>b.innerText.trim()==='醫療／資料審核人員').click();true");
 await p.expectRole("413570452","MEDICAL_REVIEWER");
 assert.equal(await apiStatus("/admin"),403);
 await p.logout("true logout");
 await start();
 step("OTP Chromium",p,"reload pending OTP","returns to credentials and consumes previous cookie");
 await p.nav("/login");await p.ready();
 assert.equal((await p.cmd("Network.getCookies",{urls:[base]})).cookies.some(c=>c.name==="__Host-ImagingAI.DevOtp"),false);
 await start();await enterOtp();await p.click();
 await wait(()=>p.ev("document.body.innerText.includes('角色選擇剩餘')"),"second role picker");
 await p.ev("Array.from(document.querySelectorAll('button')).find(b=>b.innerText.trim()==='系統管理／維護人員').click();true");
 await p.expectRole("413570452","SYSTEM_ADMIN");
 assert.equal(await apiStatus("/reviewer"),403);
 await p.logout("dual administrator logout");
 await start("413570438");
 await enterOtp();await p.click();
 await p.expectRole("413570438","SYSTEM_ADMIN");
 await p.logout("single administrator logout");
 const documentsAfterLogout=p.documents();
 await p.ev("history.back();true");
 await wait(()=>p.documents()>documentsAfterLogout,"browser back opens a fresh document");
 await p.ready();
 assert.equal(await apiStatus("/api/auth/me"),401);
 assert.equal(await p.ev("!!document.querySelector('.ia-user-name')"),false);
 console.log("PASS browser back after logout does not restore protected identity");
 assert.equal(await p.ev("JSON.stringify([Object.keys(localStorage),Object.keys(sessionStorage)])"),"[[],[]]");
 if(!process.argv.includes("quick")){
 step("OTP Chromium",p,"wait 60 real seconds before resend","disabled before the deadline, new OTP afterward");
 await start("413570359");
 await wait(()=>otp!==null,"first resend fixture delivery");
 const originalOtp=otp;
 const readSeconds=()=>p.ev("Number(document.body.innerText.match(/驗證碼剩餘 (\\d+) 秒/)?.[1])");
 const started=Date.now();
 assert.equal(await p.ev("Array.from(document.querySelectorAll('button')).find(x=>x.innerText.trim()==='重新發送').disabled"),true);
 assert.equal(await p.ev("import('/js/devAuth.js').then(async x=>{const csrf=await x.getCsrf();return fetch('/api/auth/otp/resend',{method:'POST',headers:{'X-CSRF-TOKEN':csrf.json.requestToken}}).then(r=>r.status)})"),429);
 await delay(60000);
 await wait(()=>p.ev("Array.from(document.querySelectorAll('button')).some(x=>x.innerText.trim()==='重新發送'&&!x.disabled)"),"resend unlocked");
 const remaining=await readSeconds();
 otp=null;
 await p.ev("Array.from(document.querySelectorAll('button')).find(x=>x.innerText.trim()==='重新發送').click();true");
 await wait(()=>otp!==null,"new code delivered");
 assert.ok(otp!==originalOtp);
 assert.ok((await readSeconds())<=remaining);
 const revised=otp;
 otp=originalOtp;await enterOtp();await p.click();
 await wait(()=>p.ev("document.body.innerText.includes('驗證碼錯誤或驗證已失效')"),"old OTP denied");
 otp=revised;await enterOtp();await p.click();await p.expectRole("413570359","MEDICAL_REVIEWER");
 await p.logout("resend login logout");
 console.log("PASS Chromium real 60s resend, old OTP refused and original deadline retained; elapsed "+(Date.now()-started)+" ms");
 }
 assert.ok(frames>0); assert.equal(leaks,0);
 console.log("PASS real Chromium HTTPS/YARP single and dual role OTP, reload cancellation, no SignalR disclosure, role boundaries and logout");
 p.close();
 }
} catch(error) {
 console.error("FAIL browser "+JSON.stringify({step:activeStep,kind:error.constructor.name,source:String(error.stack).split("\n").filter(x=>x.includes("acceptance.mjs")).slice(0,3),actual:await activePage?.diagnose().catch(()=>null)}));
 process.exitCode=1;
} finally {
 lineReader.close();process.stdin.pause();
 for(const {proc,dir} of profiles){proc.kill();await new Promise(r=>{if(proc.exitCode!==null)r();else proc.once("exit",r)});await rm(dir,{recursive:true,force:true,maxRetries:4,retryDelay:250});}
}








