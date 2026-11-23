import assert from 'node:assert/strict';
import {randomBytes, randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {cp, mkdir, mkdtemp, rm, writeFile} from 'node:fs/promises';
import {basename, dirname, join, resolve} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyScreenDuty(options) {
  const evidence = resolve(options.evidence);
  await mkdir(evidence, {recursive: true});
  const stateRoot = await mkdtemp(join(evidence, 'native-state-'));
  try { await verify({...options, evidence, stateRoot}); }
  finally {
    if (dirname(resolve(stateRoot)) === evidence && basename(stateRoot).startsWith('native-state-'))
      await rm(stateRoot, {recursive: true, force: true});
  }
}

async function verify({prisma, origin, fixture, directory, evidence, web, runDesktop, runNative, stateRoot}) {
  const {school, term, pin, managerId} = fixture;
  const path = '/api/v2/admin/schools/' + school.id;
  const duty = path + '/screen-duty';
  const screenPath = '/api/v2/classroom-screens';
  async function api(url, {token, screenToken, method = 'GET', body, status = 200} = {}) {
    const reply = await fetch(origin + url, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {}), ...(screenToken ? {'X-Classworks-Screen-Token': screenToken} : {})},
      body: body === undefined ? undefined : JSON.stringify(body)});
    const payload = await reply.json().catch(() => null);
    assert.equal(reply.status, status, url + ': ' + (payload?.code ?? reply.status));
    return payload?.data ?? payload;
  }
  const login = async username => (await api('/accounts/local/login', {method: 'POST',
    body: {schoolCode: school.code, username, password: pin}})).access_token;
  async function npep(url, {data, token, screenToken, status = 200, version = '0.1'} = {}) {
    const requestId = randomUUID();
    const reply = await fetch(origin + '/api/v2/npep' + url, {method: data ? 'POST' : 'GET',
      headers: {'Content-Type': 'application/json', 'X-NPEP-Version': version, 'X-Request-Id': requestId,
        ...(token ? {Authorization: 'Bearer ' + token} : {}), ...(screenToken ? {'X-Classworks-Screen-Token': screenToken} : {})},
      body: data ? JSON.stringify({requestId, ...data}) : undefined});
    const payload = await reply.json();
    assert.equal(reply.status, status, url + ': ' + (payload.error?.code ?? reply.status));
    return payload.data ?? payload.error;
  }
  const [admin, legacy] = await Promise.all([login('admin'), login('admin2')]);
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const [seven, eight, nine] = await Promise.all([7, 8, 9].map(n => prisma.workspace.create({data: {
    termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS', code: 'DUTY-G2-' + n, name: '高二（' + n + '）班'
  }})));
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'DUTY-MATH', name: '数学', category: 'CORE'}});
  await prisma.administrativeClassSubject.createMany({data: [seven.id, eight.id].map(id =>
    ({administrativeClassId: id, subjectId: subject.id, deliveryMode: 'ADMIN_CLASS'}))});
  const frontInit = await api(path + '/classroom-screen-accounts', {method: 'POST', token: admin, status: 201,
    body: {administrativeClassId: seven.id, name: '七班前屏', loginCode: 'duty-front', pin}});
  const makeBinding = (room, name) => prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: room.id,
    name, tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId, npepPrepared: true}});
  const [rear, peer] = await Promise.all([makeBinding(seven, '七班后屏'), makeBinding(nine, '机房备用屏')]);
  const front = await prisma.classroomScreenBinding.update({where: {id: frontInit.id}, data: {npepPrepared: true}});
  const nativeFixture = join(directory, 'screen-duty-native.json'), nativeState = join(stateRoot, 'devices');
  const setScope = rearClass => writeFile(nativeFixture, JSON.stringify({kind: 'TERM_PREPARATION_DISPOSABLE_DATABASE',
    origin, adminToken: admin, schoolId: school.id, classId: seven.id, classIds: [seven.id, rearClass],
    bindingIds: [front.id, rear.id]}), {mode: 0o600});
  await setScope(seven.id);
  await runNative(['pair', nativeFixture, nativeState]);
  const priorRoot = join(stateRoot, 'rear-original');
  await mkdir(priorRoot);
  await cp(join(nativeState, 'device-2'), join(priorRoot, 'device-2'), {recursive: true});
  const device = async id => prisma.npepDevice.findFirstOrThrow({where: {screenBindingId: id, state: 'ACTIVE'}});
  const frontBefore = await device(front.id);
  const rearBefore = await device(rear.id);
  const desktopApi = id => api(duty + '?bindingId=' + id, {token: admin}).then(data => data.screens[0]);
  const binding = id => prisma.classroomScreenBinding.findUniqueOrThrow({where: {id}});
  const recent = async type => prisma.classroomScreenCommand.findFirstOrThrow({where: {screenBindingId: rear.id, type}, orderBy: {createdAt: 'desc'}});
  const screenToken = page => page.evaluate(() => localStorage.getItem('classworks-v2-screen-token'));
  const steps = [], commands = {};
  let browser, site, rearTab, frontTab, rearWebToken, frontWebToken, changedWebToken, allowMove = false, lost = null;
  let receiptReads = 0, movePosts = 0, navigations = 0, releaseReload, reloadGate;
  let createRequestId = null, guardCreate = false, createAttempts = 0;
  let rearFirstLoginAt;
  let movedWebToken, nextWebToken, beforeLoginEdit, beforePinReset, pairingTicket, pinResetReportAt;
  const rearCode = 'DUTY-REAR', newCode = 'DUTY-REAR-RENAMED';
  const resetPin = pin === '384167' ? '384168' : '384167';
  const event = (page, path, method = 'POST') => page.waitForResponse(res =>
    new URL(res.url()).pathname === path && res.request().method() === method, {timeout: 16000});
  const tick = page => page.evaluate(() => window.dispatchEvent(new Event('visibilitychange')));
  const heartbeat = screenPath + '/heartbeat';
  const loginAsExistingBrowser = async (row, code, password) => (await api(screenPath + '/login', {method: 'POST',
    body: {schoolCode: school.code, loginCode: code, pin: password, deviceFingerprint: row.deviceFingerprint}})).token;

  async function webLogin(page, code, pass) {
    await page.goto(site, {waitUntil: 'domcontentloaded'});
    await page.getByRole('button', {name: /激活大屏/}).click();
    const form = page.locator('.screen-login-card');
    await form.waitFor({state: 'visible'});
    if (!await form.locator('.v-select__selection-text').filter({hasText: school.name}).count()) {
      await form.locator('.v-select .v-field').click();
      await page.getByRole('option', {name: school.name, exact: true}).click();
    }
    await form.locator('.v-text-field').filter({hasText: '大屏账号'}).locator('input').fill(code);
    await form.locator('.v-text-field').filter({hasText: '大屏 PIN'}).locator('input').fill(pass);
    const logged = event(page, screenPath + '/login');
    const pulse = event(page, heartbeat);
    const feed = event(page, screenPath + '/feed', 'GET');
    await form.getByRole('button', {name: '登录并进入大屏'}).click();
    assert.ok((await logged).ok());
    await form.waitFor({state: 'hidden'});
    await page.getByRole('button', {name: '完成并进入大屏', exact: true}).click();
    assert.ok((await pulse).ok());
    assert.ok((await feed).ok());
    return screenToken(page);
  }

  async function pulse(page) {
    const result = event(page, heartbeat);
    await tick(page);
    assert.ok((await result).ok());
  }

  async function check(step) {
    switch (step) {
      case 'configured': {
        const row = await binding(rear.id);
        assert.equal(row.id, rear.id);
        assert.equal(row.administrativeClassId, seven.id);
        assert.equal(row.loginCode, rearCode);
        assert.equal(row.npepBindingRevision, rearBefore.bindingRevision);
        await runNative(['report-second', nativeFixture, nativeState]);
        rearWebToken = await webLogin(rearTab, rearCode, pin);
        rearTab.on('framenavigated', frame => { if (frame === rearTab.mainFrame()) navigations++; });
        const state = await desktopApi(rear.id);
        assert.equal(state.nativeState, 'REPORTED');
        assert.ok(state.heartbeatAt && state.firstLoginAt);
        rearFirstLoginAt = state.firstLoginAt;
        await rearTab.screenshot({path: join(evidence, 'edge-rear-live.png')});
        break;
      }
      case 'refresh': {
        const command = await recent('REFRESH_DATA');
        const seen = [];
        const listener = response => {
          const p = new URL(response.url()).pathname;
          if (p === screenPath + '/feed' || p.endsWith('/' + command.id + '/ack')) seen.push(p);
        };
        rearTab.on('response', listener);
        const ack = event(rearTab, screenPath + '/commands/' + command.id + '/ack');
        await pulse(rearTab);
        assert.ok((await ack).ok());
        rearTab.off('response', listener);
        assert.deepEqual(seen.slice(-2), [screenPath + '/feed', screenPath + '/commands/' + command.id + '/ack']);
        assert.equal((await desktopApi(rear.id)).commands.find(item => item.id === command.id)?.state, 'REFRESHED');
        assert.equal(await prisma.classroomScreenCommand.count({where: {screenBindingId: front.id}}), 0);
        commands.refreshed = command.id;
        await rearTab.context().setOffline(true);
        break;
      }
      case 'expired': {
        const command = await recent('REFRESH_DATA');
        assert.notEqual(command.id, commands.refreshed);
        assert.equal(command.status, 'PENDING');
        await prisma.classroomScreenCommand.update({where: {id: command.id}, data: {expiresAt: new Date(Date.now() - 1000)}});
        await rearTab.context().setOffline(false);
        await pulse(rearTab);
        assert.equal((await desktopApi(rear.id)).commands.find(item => item.id === command.id)?.state, 'EXPIRED');
        commands.expired = command.id;
        break;
      }
      case 'edit-begin': {
        await rearTab.getByRole('button', {name: '录入作业', exact: true}).click();
        const field = rearTab.locator('.screen-composer').locator('textarea:not([aria-hidden])').first();
        await field.fill('仅隔离校验的未提交作业');
        await rearTab.screenshot({path: join(evidence, 'edge-rear-editing.png')});
        break;
      }
      case 'editing-held': {
        const command = await recent('RELOAD_APP');
        const before = navigations;
        await pulse(rearTab);
        const field = rearTab.locator('.screen-composer').locator('textarea:not([aria-hidden])').first();
        assert.equal(await field.inputValue(), '仅隔离校验的未提交作业');
        assert.equal(navigations, before);
        assert.equal((await desktopApi(rear.id)).commands.find(item => item.id === command.id)?.state, 'DELIVERED');
        commands.reload = command.id;
        break;
      }
      case 'reload-accepted': {
        reloadGate = new Promise(resolve => { releaseReload = resolve; });
        const before = navigations;
        await rearTab.route('**/api/v2/classroom-screens/session', async route => {
          await reloadGate;
          await route.continue();
        });
        await rearTab.locator('.screen-composer').getByRole('button', {name: '取消', exact: true}).click();
        await rearTab.locator('.screen-composer').waitFor({state: 'hidden'});
        const ack = event(rearTab, screenPath + '/commands/' + commands.reload + '/ack');
        await pulse(rearTab);
        assert.ok((await ack).ok());
        assert.equal(navigations, before);
        assert.equal((await desktopApi(rear.id)).commands.find(item => item.id === commands.reload)?.state, 'RELOAD_ACCEPTED');
        break;
      }
      case 'reload-completed': {
        const after = rearTab.waitForEvent('framenavigated', {predicate: frame => frame === rearTab.mainFrame(), timeout: 16000});
        releaseReload();
        await after;
        await rearTab.unroute('**/api/v2/classroom-screens/session');
        assert.ok(navigations > 0);
        break;
      }
      case 'legacy-move': {
        await api(path + '/classroom-screens/' + peer.id, {method: 'PATCH', token: legacy, body: {administrativeClassId: eight.id}});
        break;
      }
      case 'stale-no-write': {
        assert.equal((await binding(rear.id)).administrativeClassId, seven.id);
        assert.equal((await binding(front.id)).administrativeClassId, seven.id);
        assert.equal((await binding(peer.id)).administrativeClassId, eight.id);
        allowMove = true;
        break;
      }
      case 'moved': {
        assert.ok(lost && movePosts === 1 && receiptReads === 1);
        assert.equal(lost.bindingId, rear.id);
        assert.equal((await binding(front.id)).administrativeClassId, seven.id);
        assert.equal((await binding(rear.id)).administrativeClassId, eight.id);
        assert.equal((await desktopApi(rear.id)).nativeState, 'REPAIR_REQUIRED');
        await api(screenPath + '/session', {screenToken: rearWebToken, status: 401});
        await api(screenPath + '/session', {screenToken: frontWebToken});
        await runNative(['invalidated-second', nativeFixture, priorRoot]);
        await runNative(['report-first', nativeFixture, nativeState]);
        const frontAfter = await device(front.id);
        assert.equal(frontAfter.credentialId, frontBefore.credentialId);
        assert.ok(frontAfter.lastReportedAt > frontBefore.lastReportedAt);
        await setScope(eight.id);
        beforeLoginEdit = await binding(rear.id);
        movedWebToken = await loginAsExistingBrowser(beforeLoginEdit, rearCode, pin);
        break;
      }
      case 'unchanged-login': {
        const current = await binding(rear.id);
        assert.equal(current.loginCode, beforeLoginEdit.loginCode);
        assert.equal(current.credentialVersion, beforeLoginEdit.credentialVersion);
        assert.equal(current.npepBindingRevision, beforeLoginEdit.npepBindingRevision);
        await api(screenPath + '/session', {screenToken: movedWebToken});
        assert.equal(await prisma.classroomScreenManagementReceipt.count({where: {schoolId: school.id,
          result: {path: ['action'], equals: 'CHANGE_LOGIN'}}}), 0);
        break;
      }
      case 'changed-login': {
        const current = await binding(rear.id);
        assert.equal(current.loginCode, newCode);
        assert.equal(current.credentialVersion, beforeLoginEdit.credentialVersion + 1);
        assert.equal(current.npepBindingRevision, beforeLoginEdit.npepBindingRevision);
        await api(screenPath + '/session', {screenToken: movedWebToken, status: 401});
        nextWebToken = await loginAsExistingBrowser(current, newCode, pin);
        const route = '/schools/' + school.id + '/class-management/' + rear.id;
        const scope = await npep(route, {token: admin, version: '0.16'});
        await npep(route, {token: admin, version: '0.16', data: {expectedClassId: scope.classId,
          expectedRevision: scope.revision, expectedBindingRevision: scope.bindingRevision, expectedPairingRevision: scope.pairingRevision,
          requireUnpaired: true, enabled: true, secret: 'isolated-screen-duty-class-management'}});
        pairingTicket = await npep('/screen/pairing', {screenToken: nextWebToken, data: {}, status: 201});
        await runNative(['repair-second', nativeFixture, nativeState]);
        await runNative(['report-second', nativeFixture, nativeState]);
        beforePinReset = await device(rear.id);
        break;
      }
      case 'inactive': {
        assert.equal((await binding(rear.id)).isActive, false);
        assert.equal((await binding(front.id)).isActive, true);
        await api(screenPath + '/session', {screenToken: changedWebToken, status: 401});
        break;
      }
      case 'restored': {
        const restored = await api(screenPath + '/session', {screenToken: changedWebToken});
        assert.equal(restored.binding.id, rear.id);
        await runNative(['repair-second', nativeFixture, nativeState]);
        assert.equal((await desktopApi(rear.id)).nativeState, 'AWAITING_REPORT');
        await runNative(['report-second', nativeFixture, nativeState]);
        break;
      }
      case 'updated-pin': {
        const updated = await binding(rear.id);
        assert.equal(updated.loginCode, newCode);
        assert.equal(updated.name, '八班后屏');
        await api(screenPath + '/session', {screenToken: nextWebToken, status: 401});
        changedWebToken = await loginAsExistingBrowser(updated, newCode, resetPin);
        await api(screenPath + '/session', {screenToken: changedWebToken});
        const {serverInstanceId, deploymentEpoch} = await npep('/info');
        const stalePairing = await npep('/pairings/claim', {status: 409, data: {serverInstanceId, deploymentEpoch,
          userCode: pairingTicket.userCode, installationId: randomUUID(), deviceName: '隔离验收候选机', appVersion: '1.1.10',
          pairingSecret: randomBytes(32).toString('base64url'), requestedCapabilities: ['device.status']}});
        assert.equal(stalePairing.code, 'PREAUTHORIZATION_CHANGED');
        await runNative(['report-second', nativeFixture, nativeState]);
        const afterPinReset = await device(rear.id);
        assert.equal(afterPinReset.credentialId, beforePinReset.credentialId);
        assert.ok(afterPinReset.lastReportedAt > beforePinReset.lastReportedAt);
        pinResetReportAt = afterPinReset.lastReportedAt;
        break;
      }
      case 'unbound': {
        const updated = await binding(rear.id);
        assert.equal(updated.deviceFingerprint, null);
        assert.equal((await desktopApi(rear.id)).firstLoginAt, rearFirstLoginAt);
        await api(screenPath + '/session', {screenToken: changedWebToken, status: 401});
        await runNative(['report-second', nativeFixture, nativeState]);
        await api(screenPath + '/session', {screenToken: frontWebToken});
        guardCreate = true;
        break;
      }
      case 'create-unsent': {
        assert.equal(createAttempts, 1);
        assert.equal(receiptReads, 2);
        assert.equal(await prisma.classroomScreenBinding.count({where: {schoolId: school.id, loginCode: 'DUTY-EXTRA'}}), 0);
        break;
      }
      case 'another-created': {
        assert.equal(createAttempts, 2);
        const records = await prisma.classroomScreenBinding.findMany({where: {administrativeClassId: eight.id}});
        assert.deepEqual(records.map(row => row.id).filter(id => [rear.id, peer.id].includes(id)).sort(), [rear.id, peer.id].sort());
        assert.equal(records.length, 3);
        assert.ok(records.some(row => row.name === '八班新增屏' && row.loginCode.toLowerCase() === 'duty-extra' && row.id !== rear.id));
        break;
      }
      case 'rotate-during-handoff': {
        const extra = await prisma.classroomScreenBinding.findFirstOrThrow({where: {schoolId: school.id, loginCode: 'DUTY-EXTRA'}});
        const body = {action: 'RESET_PIN', bindingId: extra.id};
        const preview = await api(duty + '/preview', {token: legacy, method: 'POST', body});
        await api(duty, {token: legacy, method: 'POST', body: {...body, expectedVersion: preview.version,
          requestId: randomUUID(), pin: resetPin}});
        assert.ok((await binding(extra.id)).credentialVersion > extra.credentialVersion);
        break;
      }
      default: throw new Error('Unknown screen-duty fixture checkpoint');
    }
    steps.push(step);
  }

  const target = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__duty/')) {
      try { await check(req.url.slice('/__duty/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'GET' && req.url.startsWith(duty + '/receipts/')) receiptReads++;
    let createBody;
    if (guardCreate && req.method === 'POST' && req.url === duty) {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      createBody = Buffer.concat(chunks);
      const {action, requestId} = JSON.parse(createBody);
      assert.equal(action, 'CREATE');
      if (++createAttempts === 1) { createRequestId = requestId; res.destroy(); return; }
      assert.equal(requestId, createRequestId);
      guardCreate = false;
    }
    const upstream = request(new URL(req.url, target), {method: req.method, headers: {...req.headers, host: target.host}}, reply => {
      if (!allowMove || lost || req.method !== 'POST' || req.url !== duty || reply.statusCode !== 200) {
        res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); return;
      }
      const chunks = [];
      reply.on('data', chunk => chunks.push(chunk));
      reply.on('end', () => {
        const body = Buffer.concat(chunks), result = JSON.parse(body).data;
        if (result?.action === 'MOVE') { lost = result; movePosts++; res.destroy(); return; }
        res.writeHead(reply.statusCode, reply.headers); res.end(body);
      });
    });
    upstream.on('error', () => res.destroy());
    if (createBody) upstream.end(createBody);
    else req.pipe(upstream);
  });
  await new Promise((ok, fail) => relay.once('error', fail).listen(0, '127.0.0.1', ok));
  try {
    const access = join(directory, 'screen-duty-desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolCode: school.code,
      schoolId: school.id, pin, resetPin, frontId: front.id, rearId: rear.id, sevenId: seven.id, eightId: eight.id, allowWrites: true}), {mode: 0o600});
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], realtime: true, afterStudent: async (edge, webOrigin) => {
      browser = edge; site = webOrigin;
      async function openPage() {
        const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
        await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
        return page;
      }
      try {
        frontTab = await openPage();
        frontWebToken = await webLogin(frontTab, 'duty-front', pin);
        rearTab = await openPage();
        await runDesktop(['--live-screen-duty', access, evidence]);
      } catch (error) {
        const failedPage = rearTab ?? frontTab;
        if (failedPage && !failedPage.isClosed()) await failedPage.screenshot({path: join(evidence, 'edge-failure.png')}).catch(() => {});
        throw error;
      } finally { releaseReload?.(); }
    }});
    assert.equal(steps.length, 19);
    assert.equal(steps.at(-1), 'rotate-during-handoff');
    const [finalFront, finalRear] = await Promise.all([device(front.id), device(rear.id)]);
    await writeFile(join(evidence, 'screen-duty-cross-end.json'), JSON.stringify({passed: true, steps, commands,
      screens: {
        front: {bindingId: front.id, originalClassId: seven.id, finalClassId: finalFront.administrativeClassId,
          originalReportAt: frontBefore.lastReportedAt, finalReportAt: finalFront.lastReportedAt},
        rear: {bindingId: rear.id, originalClassId: seven.id, finalClassId: finalRear.administrativeClassId,
          originalReportAt: rearBefore.lastReportedAt, finalReportAt: finalRear.lastReportedAt, firstWebLoginAt: rearFirstLoginAt}
      },
      nativeFrontOriginalCredential: true, nativeRearOldCredentialRejected: true, moveWrites: movePosts, receiptReads,
      afterPinReset: {oldPairingCodeRejected: true, originalNativeCredentialReportedAt: pinResetReportAt},
      createAttemptsWithSameId: createAttempts, observedPageReload: navigations > 0}, null, 2));
    console.log('PASS: Windows Console, real Edge screen login and commands, DPAPI native dual-screen credential checks.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
