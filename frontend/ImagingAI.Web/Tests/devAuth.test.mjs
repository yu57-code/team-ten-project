import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL("../wwwroot/js/devAuth.js", import.meta.url), 'utf8');
const api = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const tests = [];
const test = (name, run) => tests.push([name, run]);
const json = (body, status = 200, headers = {}) => Response.json(body, {status, headers});
const csrf = token => json({requestToken: token});
const text = (body, status, headers = {}) => new Response(body, {status, headers});
function replies(...queue) {
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({url, ...init});
    assert.equal(init.credentials, 'same-origin');
    assert.equal(init.mode, 'same-origin');
    assert.equal(init.cache, 'no-store');
    assert.equal(init.redirect, 'manual');
    assert.ok(url.startsWith('/api/auth/'));
    if (!queue.length) throw new Error('extra fetch');
    const next = queue.shift();
    if (next instanceof Error) throw next;
    return next;
  };
  return calls;
}
test('exports only fixed auth operations', async () => {
  assert.deepEqual(Object.keys(api).sort(), ['getCsrf','login','logout','me','prepareOtp','recoverLogin','resendOtp','selectRole','verifyOtp']);
  const calls = replies(csrf('probe'));
  assert.equal((await api.getCsrf()).json.requestToken, 'probe');
  assert.equal(calls[0].url, '/api/auth/csrf');
});
test('login POST has current CSRF and only supplied credentials', async () => {
  const calls = replies(csrf('anon-1'), json({state:'authenticated'}));
  await api.login('synthetic', 'test-only');
  assert.deepEqual(calls.map(x=>[x.url,x.method]), [['/api/auth/csrf','GET'],['/api/auth/login','POST']]);
  assert.equal(calls[1].headers['X-CSRF-TOKEN'], 'anon-1');
  assert.equal(calls[1].headers['Content-Type'], 'application/json');
  assert.deepEqual(JSON.parse(calls[1].body), {username:'synthetic',password:'test-only'});
});
test('state changes acquire a new CSRF before every mutation', async () => {
  const calls = replies(csrf('anonymous'), json({state:'roleSelectionRequired'}),
    csrf('pending'), json({state:'authenticated'}), csrf('authenticated'), new Response(null,{status:204}));
  await api.login('synthetic', 'test-only');
  await api.selectRole('SYSTEM_ADMIN');
  assert.equal((await api.logout()).status, 204);
  assert.deepEqual(calls.filter(c=>c.method==='POST').map(c=>c.headers['X-CSRF-TOKEN']),
    ['anonymous','pending','authenticated']);
  assert.deepEqual(JSON.parse(calls[3].body), {roleCode:'SYSTEM_ADMIN'});
  assert.equal(calls[5].body, undefined);
});
test('no CSRF caching across separate invocations', async () => {
  const calls = replies(csrf('first-context'), json({state:'authenticated'}),
    csrf('second-context'), json({state:'authenticated'}));
  await api.login('a','a-secret');
  await api.login('b','b-secret');
  assert.equal(calls[1].headers['X-CSRF-TOKEN'], 'first-context');
  assert.equal(calls[3].headers['X-CSRF-TOKEN'], 'second-context');
});
test('me is GET without CSRF or body', async () => {
  const calls = replies(json({userId:13}));
  await api.me();
  assert.equal(calls[0].url, '/api/auth/me');
  assert.equal(calls[0].method, 'GET');
  assert.equal(calls[0].headers['X-CSRF-TOKEN'], undefined);
  assert.equal(calls[0].body, undefined);
});
test('logout 204 does not try to read response body', async () => {
  replies(csrf('ok'), {status:204, headers:new Headers(), text() {throw new Error('must not read');}});
  assert.equal((await api.logout()).status,204);
});
test('failed csrf prevents logout even when bootstrap returns 204', async () => {
  const calls = replies(new Response(null,{status:204}));
  const result = await api.logout();
  assert.equal(result.duringCsrf, true);
  assert.equal(calls.length,1);
});
test('invalid csrf JSON stops operation without POST', async () => {
  const calls = replies(json({requestToken:12}));
  assert.equal((await api.login('a','b')).transportError,'protocol_error');
  assert.equal(calls.length,1);
});
test('malformed JSON success stays unparsed for C# to reject', async () => {
  replies(text('{',200,{'Content-Type':'application/json'}));
  const r=await api.me();
  assert.equal(r.status,200);
  assert.equal(r.json,undefined);
});
test('429 preserves Retry-After header and ProblemDetails', async () => {
  replies(json({code:'rate_limited'},429,{'Content-Type':'application/problem+json','Retry-After':'13'}));
  const r = await api.me();
  assert.equal(r.retryAfter,'13');
  assert.equal(r.json.code,'rate_limited');
});
for (const status of [400,401,403,404,502,503]) {
  test('non JSON HTTP '+status+' preserves HTTP status', async () => {
    replies(text('<html>synthetic failure</html>',status,{'Content-Type':'text/html'}));
    const r=await api.me();
    assert.equal(r.status,status);
    assert.equal(r.json,undefined);
  });
}
test('manual redirect is protocol error', async () => {
  for(const response of [new Response(null,{status:302}),{status:0,type:'opaqueredirect'}]) {
    replies(response);
    assert.equal((await api.me()).transportError,'protocol_error');
  }
});
test('failed fetch reports generic network error without details', async () => {
  replies(new TypeError('synthetic TLS detail'));
  assert.deepEqual(await api.me(), {transportError:'network_error'});
});
test('consumed selection is never automatically retried', async () => {
  const calls = replies(csrf('pending'),new TypeError('synthetic failed response'));
  assert.equal((await api.selectRole('SYSTEM_ADMIN')).transportError,'network_error');
  assert.equal(calls.filter(x=>x.url.endsWith('select-role')).length,1);
});
test('no sensitive browser persistence, cookie introspection or console', async () => {
  assert.doesNotMatch(source,/document\s*\.\s*cookie|localStorage|sessionStorage|console\s*\./);
});

let failed = 0;
for (const [name, run] of tests) {
  try { await run(); console.log('PASS '+name); }
  catch(e) { failed++; console.log('FAIL '+name+': '+e.constructor.name); }
}
console.log('JavaScript tests='+tests.length+' passed='+(tests.length-failed)+' failed='+failed);
process.exitCode=failed ? 1 : 0;



