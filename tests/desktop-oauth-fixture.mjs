import assert from 'node:assert/strict';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {createServer} from 'node:http';
import {createRequire} from 'node:module';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {isolatedIdentityProvider} from './school-teacher-allocation-fixture.mjs';

export async function verifyDesktopOAuth({prisma, origin, fixture, fromKv, directory, evidence, web, runDesktop}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, classes, managerId, pin} = fixture;
  const authorization = '/accounts/oauth/desktop/';
  const actorEmail = 'claim-desktop-owner@example.test', teacherEmail = 'claim-desktop-no-scope@example.test';
  const headers = {'Content-Type': 'application/json'};
  const checks = [], held = new Set(), claimed = new Map(), acknowledgements = new Map(), cancelled = new Set();
  const dropClaims = new Set(), dropAcknowledgements = new Set(), holdAcknowledgements = new Map(), captured = new Set();
  let browser, provider, context, isolatedOrigin, actorId, oldWebToken, ownerToken, successorToken, noScopeId, lastCancelledId;
  const previousFrontend = process.env.FRONTEND_URL;
  const forwardedHeaders = req => Object.fromEntries(Object.entries(req.headers)
    .filter(([key]) => !['host', 'content-length', 'connection', 'transfer-encoding', 'accept-encoding'].includes(key)));
  const relay = createServer(async (req, res) => {
    try {
      const url = new URL(req.url, isolatedOrigin);
      const body = await read(req);
      if (url.pathname === '/__oauth-web-complete') return res.writeHead(200, {'Content-Type': 'text/html'}).end('<title>Web handoff</title><p>Web handoff</p>');
      if (url.pathname.startsWith('/__desktop-oauth/')) {
        await checkpoint(url.pathname.slice('/__desktop-oauth/'.length), body ? JSON.parse(body) : {});
        res.writeHead(204).end();
        return;
      }
      const claim = url.pathname.match(/^\/accounts\/oauth\/desktop\/([\w-]{43})\/claim$/);
      const ack = url.pathname.match(/^\/accounts\/oauth\/desktop\/([\w-]{43})\/acknowledge$/);
      const cancel = url.pathname.match(/^\/accounts\/oauth\/desktop\/([\w-]{43})\/cancel$/);
      if (claim && held.has(claim[1])) return res.writeHead(200, headers).end(JSON.stringify({success: true, data: {state: 'PENDING'}}));
      if (ack && holdAcknowledgements.has(ack[1])) await holdAcknowledgements.get(ack[1]).wait;
      const forward = await fetch(origin + req.url, {method: req.method, body: body || undefined, redirect: 'manual', headers: forwardedHeaders(req)});
      const content = Buffer.from(await forward.arrayBuffer());
      if (claim && forward.ok) {
        const result = JSON.parse(content.toString()).data;
        if (result.state === 'COMPLETE') {
          assert.ok(!claimed.has(claim[1]));
          claimed.set(claim[1], {token: result.access_token, refresh: result.refresh_token});
          if (dropClaims.has(claim[1])) { res.destroy(); return; }
        }
      }
      if (ack && forward.ok) {
        assert.ok(!acknowledgements.has(ack[1]));
        acknowledgements.set(ack[1], JSON.parse(body));
        if (dropAcknowledgements.has(ack[1])) { res.destroy(); return; }
      }
      if (cancel && forward.status === 204) cancelled.add(cancel[1]);
      const out = Object.fromEntries([...forward.headers].filter(([key]) => !['content-length', 'content-encoding', 'transfer-encoding', 'connection', 'set-cookie'].includes(key)));
      if (forward.headers.getSetCookie().length) out['set-cookie'] = forward.headers.getSetCookie();
      res.writeHead(forward.status, out).end(content);
    } catch (error) {
      console.error('Desktop OAuth fixture failed:', error.message);
      res.writeHead(500).end();
    }
  });

  function read(stream) {
    return (async () => {
      const parts = [];
      for await (const chunk of stream) parts.push(chunk);
      return parts.length ? Buffer.concat(parts) : null;
    })();
  }
  const listen = server => new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1',
    () => resolve(`http://127.0.0.1:${server.address().port}`)));
  const close = server => { server.closeAllConnections(); return new Promise(resolve => server.close(resolve)); };
  async function json(path, {method = 'GET', token, body, status = 200, extra = {}} = {}) {
    const response = await fetch(origin + path, {method, headers: {...headers, ...extra, ...(token ? {Authorization: `Bearer ${token}`} : {})},
      body: body === undefined ? undefined : JSON.stringify(body)});
    assert.equal(response.status, status, path);
    const data = await response.json();
    return data.data ?? data;
  }
  const memberships = token => json('/api/v2/me/schools', {token});
  const roles = async token => Object.fromEntries((await memberships(token)).map(row => [row.schoolId, row.role]));
  const profile = (token, status = 200) => json('/accounts/profile', {token, status});
  const management = id => `/api/v2/admin/schools/${id}/management-accounts`;
  const photos = (token, status) => json(`/api/v2/npep/schools/${school.id}/study-photos?termId=${term.id}&classId=${classes[0]}`,
    {token, status, extra: {'X-NPEP-Version': '0.15', 'X-Request-Id': randomUUID()}});
  const login = async () => (await json('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username: 'admin', password: pin}})).access_token;
  async function change(token, accountId, role) {
    const operation = {action: 'ROLE', accountId, role};
    const preview = await json(management(school.id) + '/preview', {method: 'POST', token, body: operation});
    assert.ok(preview.canConfirm);
    return json(management(school.id), {method: 'POST', token,
      body: {...operation, requestId: randomUUID(), expectedFingerprint: preview.fingerprint}});
  }
  async function setIdentity(email) {
    await context.addCookies([{name: 'np_fixture_identity', value: encodeURIComponent(email), url: provider.issuer, httpOnly: true}]);
  }
  function browserRequest(url) {
    const parsed = new URL(url);
    const id = parsed.pathname.match(/^\/accounts\/oauth\/desktop\/([\w-]{43})\/browser$/)?.[1];
    assert.ok(id && parsed.origin === isolatedOrigin && !parsed.search && !parsed.hash);
    return id;
  }
  async function openBrowser(url, code, page = null) {
    const id = browserRequest(url);
    assert.equal(code, id.slice(0, 8));
    const tab = page ?? await context.newPage();
    let providerRequests = 0;
    function counted(request) { if (new URL(request.url()).origin === provider.issuer) providerRequests++; }
    tab.on('request', counted);
    try {
      const response = await tab.goto(url, {waitUntil: 'load'});
      assert.equal(response.status(), 200);
      assert.ok(await tab.getByRole('heading', {name: '核对桌面登录'}).isVisible());
      assert.equal(await tab.locator('code').innerText(), code);
      assert.ok((await tab.locator('main').innerText()).includes(isolatedOrigin));
      assert.equal(providerRequests, 0, 'opening a link with an authenticated provider cookie must stop at the explicit review');
      return {id, tab};
    } finally { tab.off('request', counted); }
  }
  async function confirm(tab) {
    await tab.getByRole('button', {name: /继续前往/}).click();
    await tab.getByRole('heading', {name: '浏览器认证已完成'}).waitFor();
    // The provider callback used its existing one-time code; the completion view carries no KV credentials.
    const page = await tab.locator('main').innerText();
    assert.ok(!/access_token|refresh_token|oauth_code/.test(page));
    assert.ok(!/access_token|refresh_token|oauth_code/.test(tab.url()));
  }
  async function waitFor(check, expected) {
    for (let pass = 0; pass < 60; pass++) {
      if (check()) return;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error(expected);
  }
  async function waitForClaims(id, acknowledged = true) {
    await waitFor(() => acknowledged ? acknowledgements.has(id) : claimed.has(id), 'The active Console did not finish the expected claim phase.');
    return claimed.get(id).token;
  }
  async function checkpoint(step, data) {
    if (step === 'initial-two-windows') {
      await setIdentity(actorEmail);
      const left = await openBrowser(data.left, data.leftCode);
      const original = await openBrowser(data.right, data.rightCode);
      const reopened = await openBrowser(data.rightReopen, data.rightCode);
      assert.notEqual(left.id, original.id);
      assert.equal(reopened.id, original.id);
      await original.tab.getByRole('button', {name: /继续前往/}).click();
      await original.tab.getByRole('heading', {name: '桌面请求已结束'}).waitFor();
      held.add(left.id);
      held.add(reopened.id);
      await reopened.tab.screenshot({path: join(evidence, '01-edge-review-reopened.png')});
      await confirm(reopened.tab);
      await reopened.tab.screenshot({path: join(evidence, '02-edge-browser-finished-unclaimed.png')});
      lastCancelledId = reopened.id;
      await confirm(left.tab);
      const count = await prisma.accountSession.count({where: {accountId: actorId}});
      await change(ownerToken, actorId, 'OWNER');
      const directory = await json(management(school.id), {token: ownerToken});
      assert.ok(directory.accounts.find(row => row.id === actorId).awaitsManagementLogin);
      assert.equal(await prisma.accountSession.count({where: {accountId: actorId}}), count);
      held.delete(left.id);
    } else if (step === 'cancelled-unclaimed') {
      await waitFor(() => cancelled.has(lastCancelledId), 'Cancelled Console must close its original server request.');
      const audit = await prisma.$queryRaw`SELECT COUNT(*)::int AS count FROM "SchoolManagementLogin" WHERE "accountId"=${actorId}`;
      assert.equal(audit[0].count, 2, 'only original web login and initial Console claim count; cancelled browser completion does not');
      assert.ok(!claimed.has(lastCancelledId));
      const expired = await fetch(`${isolatedOrigin}${authorization}${lastCancelledId}/browser`);
      assert.equal(expired.status, 410);
      assert.equal((await roles(oldWebToken))[school.id], 'VIEWER');
    } else if (step === 'fresh-owner') {
      const request = await openBrowser(data.url, data.code);
      dropAcknowledgements.add(request.id);
      await confirm(request.tab);
      successorToken = await waitForClaims(request.id);
      assert.deepEqual(await roles(successorToken), {[school.id]: 'OWNER', [other.id]: 'ADMIN'});
      await photos(successorToken, 200);
      const directory = await json(management(school.id), {token: ownerToken});
      const proof = directory.accounts.find(row => row.id === actorId);
      assert.equal(proof.managementLoginMethod, 'OAUTH');
      assert.ok(proof.managementLoginAt && !proof.awaitsManagementLogin);
      const before = await prisma.$queryRaw`SELECT COUNT(*)::int count FROM "SchoolManagementLogin" WHERE "accountId"=${actorId}`;
      await json(`${authorization}${request.id}/acknowledge`, {method: 'POST', token: successorToken,
        body: acknowledgements.get(request.id), status: 410});
      const stable = (await json(management(school.id), {token: ownerToken})).accounts.find(row => row.id === actorId);
      const after = await prisma.$queryRaw`SELECT COUNT(*)::int count FROM "SchoolManagementLogin" WHERE "accountId"=${actorId}`;
      assert.equal(stable.managementLoginAt, proof.managementLoginAt);
      assert.equal(after[0].count, before[0].count);
      checks.push('ack-reply-lost-same-session-first-proof-unchanged');
      await json(`${management(other.id)}/preview`, {token: successorToken, method: 'POST', status: 403,
        body: {action: 'CREATE', role: 'ADMIN', username: 'forbidden', name: '不允许'}});
    } else if (step === 'owner-handoff-saved') {
      const owners = await prisma.schoolMember.findMany({where: {schoolId: school.id, role: 'OWNER'}, select: {accountId: true}});
      assert.deepEqual(owners, [{accountId: actorId}]);
      assert.equal((await roles(ownerToken))[school.id], 'ADMIN');
    } else if (step === 'claim-reply-lost') {
      const request = await openBrowser(data.url, data.code);
      dropClaims.add(request.id);
      const before = await prisma.accountSession.count({where: {accountId: actorId}});
      await confirm(request.tab);
      const undelivered = await waitForClaims(request.id, false);
      await profile(undelivered, 401);
      await json('/accounts/refresh', {method: 'POST', body: {refresh_token: claimed.get(request.id).refresh}, status: 401});
      await waitFor(() => cancelled.has(request.id), 'The window must release the lost claim request.');
      assert.equal(await prisma.accountSession.count({where: {accountId: actorId}}), before);
      assert.ok(!acknowledgements.has(request.id));
    } else if (step === 'confirming-state-captured') {
      captured.add(browserRequest(data.url));
    } else if (step === 'revoke-before-confirmation') {
      const request = await openBrowser(data.url, data.code);
      let release;
      const wait = new Promise(resolve => { release = resolve; });
      holdAcknowledgements.set(request.id, {wait, release});
      await confirm(request.tab);
      try {
        const issued = await waitForClaims(request.id, false);
        await profile(issued, 401);
        await change(successorToken, managerId, 'OWNER');
        const guardian = await login();
        assert.equal((await roles(guardian))[school.id], 'OWNER');
        await change(guardian, actorId, 'VIEWER');
        assert.deepEqual(await roles(successorToken), {[school.id]: 'VIEWER', [other.id]: 'ADMIN'});
        await photos(successorToken, 403);
        await json(management(school.id), {token: successorToken, status: 403});
        await json(management(other.id), {token: successorToken});
        await waitFor(() => captured.has(request.id), 'The Console must show the confirming stage before accepting the session.');
      } finally { holdAcknowledgements.delete(request.id); release(); }
      const after = await waitForClaims(request.id);
      assert.deepEqual(await roles(after), {[school.id]: 'VIEWER', [other.id]: 'ADMIN'});
      const {verifyAccessToken} = await fromKv('utils/tokenManager.js');
      const [proof] = await prisma.$queryRaw`SELECT grants FROM "SchoolManagementLogin" WHERE "sessionId"=${verifyAccessToken(after).sessionId}`;
      assert.ok(!Object.hasOwn(proof.grants, school.id), 'revoked school grant must be excluded from the committed desktop login evidence');
      await photos(after, 403);
    } else if (step === 'no-managed-school') {
      await setIdentity(teacherEmail);
      const request = await openBrowser(data.url, data.code);
      await confirm(request.tab);
      const issued = await waitForClaims(request.id);
      for (let pass = 0; pass < 50; pass++) {
        const active = await prisma.accountSession.count({where: {accountId: noScopeId, revokedAt: null}});
        if (active === 0) break;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      await profile(issued, 401);
      await setIdentity(actorEmail);
    } else if (step === 'changed-server' || step === 'closed-window') {
      await waitFor(() => cancelled.has(browserRequest(data.url)), 'Switching servers or closing the window must cancel the original request.');
      const request = await fetch(data.url);
      assert.equal(request.status, 410);
    } else throw new Error('Unexpected desktop checkpoint: ' + step);
    checks.push(step);
  }

  const legacy = createServer(async (req, res) => {
    if (req.url === authorization + 'providers') return res.writeHead(404, headers).end('{"success":false}');
    if (req.url === '/no-providers' + authorization + 'providers') return res.writeHead(200, headers).end('{"success":true,"data":[]}');
    if (req.url === '/provider-error' + authorization + 'providers') return res.writeHead(503, headers).end('{"success":false}');
    if (req.url === '/many-providers' + authorization + 'providers') return res.writeHead(200, headers)
      .end(JSON.stringify({success: true, data: [{id: 'hly', name: 'HLY'}, {id: 'stcn', name: 'STCN'}]}));
    try {
      const body = await read(req);
      const response = await fetch(origin + req.url, {method: req.method, body: body || undefined, headers: forwardedHeaders(req)});
      res.writeHead(response.status, headers).end(Buffer.from(await response.arrayBuffer()));
    } catch { res.writeHead(500).end(); }
  });
  try {
    isolatedOrigin = await listen(relay);
    const legacyOrigin = await listen(legacy);
    process.env.FRONTEND_URL = isolatedOrigin + '/__oauth-web-complete';
    provider = await isolatedIdentityProvider(fromKv, isolatedOrigin, {allowBrowserFixtureIdentity: true});
    const require = createRequire(join(web, 'package.json'));
    const {chromium} = require('@playwright/test');
    browser = await chromium.launch({channel: 'msedge', headless: false, args: ['--no-first-run']});
    context = await browser.newContext({viewport: {width: 1160, height: 780}, serviceWorkers: 'block'});
    await context.route('**/*', route => [isolatedOrigin, provider.issuer].includes(new URL(route.request().url()).origin) ? route.continue() : route.abort());
    await setIdentity(actorEmail);
    const oldPage = await context.newPage();
    const verifier = randomBytes(32).toString('base64url');
    const challenge = createHash('sha256').update(verifier).digest('base64url');
    await oldPage.goto(isolatedOrigin + '/accounts/oauth/hly?handoff_challenge=' + challenge);
    const webReceipt = new URL(oldPage.url());
    assert.equal(webReceipt.pathname, '/__oauth-web-complete');
    assert.ok(webReceipt.searchParams.has('oauth_code') && !webReceipt.searchParams.has('access_token') && !webReceipt.searchParams.has('refresh_token'));
    oldWebToken = (await json('/accounts/oauth/exchange', {method: 'POST', body: {code: webReceipt.searchParams.get('oauth_code'), verifier}})).access_token;
    actorId = (await profile(oldWebToken)).id;
    await prisma.schoolMember.createMany({data: [{schoolId: school.id, accountId: actorId, role: 'VIEWER'},
      {schoolId: other.id, accountId: actorId, role: 'ADMIN'}]});
    const noScope = await provider.begin(teacherEmail);
    const noScopeSession = await provider.exchange(noScope.code, noScope.verifier);
    noScopeId = (await profile(noScopeSession.access_token)).id;
    await fetch(origin + '/accounts/logout', {method: 'POST', headers: {Authorization: `Bearer ${noScopeSession.access_token}`}});
    ownerToken = await login();
    assert.equal((await roles(oldWebToken))[other.id], 'ADMIN');
    checks.push('old-web-edge-fixed-handoff');
    const config = join(directory, 'desktop-oauth.json');
    await writeFile(config, JSON.stringify({origin: isolatedOrigin, legacyOrigin, schoolId: school.id, otherId: other.id, schoolCode: school.code,
      ownerId: managerId, pin, isolated: true}), {mode: 0o600});
    await runDesktop(['--live-desktop-oauth', config, join(evidence, 'console')]);
    await writeFile(join(evidence, 'cross-end-result.json'), JSON.stringify({passed: true, checks, distinctClaims: claimed.size,
      oauth: 'isolated HLY identity provider, original KV callback and visible Edge', browserLaunch: 'fixture routes each Console browser URI into Edge'}, null, 2));
  } finally {
    for (const gate of holdAcknowledgements.values()) gate.release();
    if (browser) await browser.close();
    if (provider) await provider.close();
    if (relay.listening) await close(relay);
    if (legacy.listening) await close(legacy);
    if (previousFrontend === undefined) delete process.env.FRONTEND_URL; else process.env.FRONTEND_URL = previousFrontend;
  }
}
