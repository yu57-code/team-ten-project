import assert from "node:assert/strict";
import {spawn} from "node:child_process";
import {mkdtemp,rm} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
const base="https://localhost:17451", port=17452;
const work=process.argv[2], dir=await mkdtemp(path.join(os.tmpdir(),"ImagingAI47-Mock-"));
const noProxy="http://127.0.0.1:1/";
const chromium=process.env.PHASE77_CHROMIUM;
const jobs=[];
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function wait(f,label){const end=Date.now()+16000;while(Date.now()<end){try{if(await f())return;}catch{}await sleep(100);}throw Error(label);}
let count=0;
const pass=s=>{count++;console.log("PASS "+s);};
try {
 const web=spawn("dotnet",["bin/Debug/net8.0/ImagingAI.Web.dll","--urls",base],{cwd:work,windowsHide:true,stdio:"ignore",
 env:{...Object.fromEntries(Object.entries(process.env).filter(([key])=>!key.startsWith("EmailOtp__"))),APPDATA:dir,ASPNETCORE_ENVIRONMENT:"Development",DOTNET_ENVIRONMENT:"Development",Auth__EnableDevelopmentLogin:"false",ApiSettings__BaseUrl:noProxy,ReverseProxy__Clusters__backend__Destinations__destination1__Address:noProxy}});
 jobs.push(web);
 await wait(async()=>(await fetch(base+"/login")).ok,"Mock Web readiness");
 const chrome=spawn(chromium,["--user-data-dir="+dir,"--remote-debugging-port="+port,"--no-proxy-server","--disable-background-networking","about:blank"],{windowsHide:true,stdio:"ignore"});
 jobs.push(chrome);
 await wait(async()=>(await fetch("http://127.0.0.1:"+port+"/json/version")).ok,"Chromium readiness");
 const target=await (await fetch("http://127.0.0.1:"+port+"/json/new?about:blank",{method:"PUT"})).json();
 const ws=new WebSocket(target.webSocketDebuggerUrl);await new Promise((ok,no)=>{ws.onopen=ok;ws.onerror=no});
 let id=0, sockets=0, frames=0, wireName=false, wirePassword=false, api=0, apiWrites=0, css=0;
 const pending=new Map();
 const call=(method,params={})=>new Promise((resolve,reject)=>{const key=++id;pending.set(key,{resolve,reject});ws.send(JSON.stringify({id:key,method,params}));});
 ws.onmessage=e=>{
 const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p?.reject(Error("CDP error")):p?.resolve(m.result);return;}
 if(m.method==="Network.webSocketHandshakeResponseReceived")sockets++;
 if(m.method==="Network.webSocketFrameReceived")frames++;
 if(m.method==="Network.webSocketFrameSent"){const f=m.params.response;const data=f.opcode===2?Buffer.from(f.payloadData,"base64").toString():f.payloadData;wireName ||= data.includes("phase47-mock-");wirePassword ||= data.includes("isolated-mock-input");}
 if(m.method==="Network.responseReceived"&&m.params.response.url.startsWith(base)&&m.params.response.url.includes(".css")&&m.params.response.status===200)css++;
 if(m.method==="Network.requestWillBeSent"){const r=m.params.request;if(r.url.startsWith(base+"/api/")){api++;if(!["GET","HEAD"].includes(r.method))apiWrites++;}}
 };
 for(const d of ["Page","Runtime","Network"])await call(d+".enable");
 const ev=async expression=>{const r=await call("Runtime.evaluate",{expression,returnByValue:true});if(r.exceptionDetails)throw Error("page evaluation");return r.result.value;};
 const button=text=>ev("Array.from(document.querySelectorAll('button')).find(b=>b.textContent.trim()==="+JSON.stringify(text)+").click();true");
 const set=async(selector,value)=>{
   await ev("document.querySelector("+JSON.stringify(selector)+").focus();true");
   await call("Input.insertText",{text:value});
   await sleep(180);
 };
 const pathIs=s=>ev("location.pathname==="+JSON.stringify(s));
 const has=s=>ev("document.body.innerText.includes("+JSON.stringify(s)+")");
 await call("Page.bringToFront");
 await call("Page.navigate",{url:base+"/login"});
 await wait(async()=>sockets>0&&frames>=3&&await has("單一入口登入"),"login and websocket");
 assert.ok(await ev("!!document.querySelector('.lf-pill-logo')&&!!document.querySelector('#acc')&&!!document.querySelector('#pwd')"));assert.ok(css>0);
 await wait(async()=>{if(await has("請聯絡系統管理人員重設密碼。"))return true;await button("忘記密碼？");return false;},"interactive login callback");
 pass("Mock login, logo, local CSS and live SignalR");
 for(const [route,label,user,business,businessText,menus] of [
 ["/reviewer","醫療／資料審核人員","phase47-mock-reviewer","/reviewer/structured-data","結構化資料",["總覽","上傳病例","待覆核","異常佇列","結構化資料","個人處理紀錄","稽核軌跡"]],
 ["/admin","系統管理／維護人員","phase47-mock-admin","/admin/users","使用者與角色權限",["系統監控","使用者與角色權限","稽核軌跡","系統設定與備份"]]])
 {
   await ev(`{let input=document.querySelector("#acc");input.focus();input.value=${JSON.stringify(user)};input.dispatchEvent(new InputEvent("input",{bubbles:true}));true}`);
   await wait(()=>wireName&&ev("document.querySelector(\x27#acc\x27).value.length>0"),"account bound to Circuit");
   await set("#pwd","isolated-mock-input");
   await sleep(500);
   console.log("INFO mock input diagnostics",JSON.stringify({wireName,wirePassword,dom:await ev("({account:!!document.querySelector(\x27#acc\x27).value,password:!!document.querySelector(\x27#pwd\x27).value})")}));
   await button("下一步");
   try{await wait(()=>has("輸入驗證碼"),"Mock OTP step");}catch(e){console.error("mock login diagnostic",JSON.stringify(await ev("({path:location.pathname,error:document.querySelector(\x27.lf-error\x27)?.textContent,submitDisabled:document.querySelector(\x27.lf-submit\x27)?.disabled})")));throw e;}
   for(let i=0;i<6;i++)await set(".lf-otp:nth-child("+(i+1)+")",String(i+1));
   await button("驗證並登入");
   await wait(()=>has("驗證成功"),"Mock role chooser");
   await button(label);
   await wait(async()=>await pathIs(route)&&await has(user),"Mock role home "+route);
   assert.deepEqual(await ev("Array.from(document.querySelectorAll('.ia-nav-text')).map(x=>x.textContent.trim())"),menus);
   assert.ok(await ev("!!document.querySelector('.ia-brand-logo svg path')&&!!document.querySelector('.ia-shell')&&!!document.querySelector('button[aria-label=登出]')"));
   assert.equal(await has("目前業務功能尚未開放"),false);
   pass("Mock credentials, OTP, role, original home and ordered sidebar "+route);
   await ev("Array.from(document.querySelectorAll('.ia-nav a')).find(a=>a.pathname==="+JSON.stringify(business)+").click();true");
   await wait(async()=>await pathIs(business)&&await has(businessText),"Mock business route "+business);
   pass("Original Mock business display "+business);
   await ev("document.querySelector('button[aria-label=登出]').click();true");
   await wait(async()=>await pathIs("/login")&&await has("單一入口登入"),"Mock logout");
   pass("Mock logout "+route);
 }
 assert.equal(api,0);assert.equal(apiWrites,0);
 pass("Mock browser sent no API requests and did not activate true login");
 console.log("Phase 4.7 Mock Chromium checks="+count+" passed="+count);
 ws.close();
} catch(e){console.error("FAIL Mock Chromium "+(e.constructor.name==="Error"?e.message:e.constructor.name));process.exitCode=1;}
finally{
 for(const j of jobs.reverse())j.kill();
 await sleep(600);
 if(path.dirname(path.resolve(dir))===path.resolve(os.tmpdir())&&path.basename(dir).startsWith("ImagingAI47-Mock-"))
 await rm(dir,{recursive:true,force:true,maxRetries:4}).catch(()=>{console.error("FAIL cleanup");process.exitCode=1;});
}

