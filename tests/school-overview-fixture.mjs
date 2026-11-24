import assert from 'node:assert/strict';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {writeFile} from 'node:fs/promises';
import {join} from 'node:path';

export async function verifySchoolOverview({prisma, origin, fixture, directory, evidence, secretHash, runDesktop, runDatabase}) {
  const {school, other, term, pin, identity, managerId} = fixture;
  const prefix = schoolId => '/api/v2/admin/schools/' + schoolId + '/management-overview';
  const overviewPath = prefix(school.id) + '/current';
  const oldTerm = await prisma.academicTerm.create({data: {
    schoolId: school.id, academicYear: 2025, semester: 2, name: '归档学期', status: 'ARCHIVED',
  }});
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id}});
  const oldGrade = await prisma.grade.create({data: {termId: oldTerm.id, code: grade.code, name: grade.name}});
  const classRow = (classTerm, classGrade, number) => prisma.workspace.create({data: {
    termId: classTerm.id, gradeId: classGrade.id, code: 'G2-' + number,
    name: (classTerm.id === term.id ? '高二' : '往届高二') + '（' + number + '）班', type: 'ADMIN_CLASS',
  }});
  const oldSeven = await classRow(oldTerm, oldGrade, 7);
  const seven = await classRow(term, grade, 7);
  const eight = await classRow(term, grade, 8);
  const restored = await classRow(term, grade, 9);
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});

  const hash = text => createHash('sha256').update(text).digest('hex');
  const randomSecret = () => randomBytes(32).toString('base64url');
  const newScreen = (room, name, extra = {}) => prisma.classroomScreenBinding.create({data: {
    schoolId: school.id, administrativeClassId: room.id, name, createdByAccountId: managerId,
    tokenHash: hash(randomSecret()), ...extra,
  }});
  const oldScreen = await newScreen(oldSeven, '往届大屏', {loginCode: 'OLD.SCREEN', activatedAt: new Date()});
  const ready = await newScreen(eight, '前排已接入');
  const waiting = await newScreen(eight, '后排待接入', {npepPrepared: true});
  const repaired = await newScreen(restored, '原班大屏');
  async function seedDevice(binding) {
    const secret = randomSecret();
    const device = await prisma.npepDevice.create({data: {
      ...identity, id: randomUUID(), installationId: randomUUID(), credentialId: randomUUID(), secretHash: secretHash(secret),
      schoolId: school.id, administrativeClassId: binding.administrativeClassId, screenBindingId: binding.id,
      bindingRevision: binding.npepBindingRevision, deviceName: '验收教室终端', credentialExpiresAt: new Date(Date.now() + 86400000),
    }});
    return {device, bearer: 'npep1.' + device.credentialId + '.' + secret};
  }

  async function native(path, bearer, fields, expected = 200, version = '0.1') {
    const requestId = randomUUID();
    const headers = {'X-NPEP-Version': version, 'X-Request-Id': requestId, 'Content-Type': 'application/json'};
    if (bearer) headers.Authorization = 'Bearer ' + bearer;
    const reply = await fetch(origin + '/api/v2/npep/' + path, {method: fields ? 'POST' : 'GET', headers,
      body: fields && JSON.stringify({...fields, requestId})});
    const result = await reply.json();
    assert.equal(reply.status, expected, path + ': ' + (result.error?.code ?? reply.status));
    assert.equal(result.requestId, requestId);
    return result.data;
  }
  const open = (bearer, expectedStatusEpoch) =>
    native('device/sessions', bearer, {...identity, runId: randomUUID(), expectedStatusEpoch}, 201);
  const report = (bearer, session) => native('device/status', bearer, {...identity,
    sessionId: session.sessionId, statusEpoch: session.statusEpoch, sequence: 1, sampleAgeMs: 0,
    status: {appVersion: '1.1.10', mode: 'DAILY', modePhase: 'RUNNING', modeRevision: 1,
      automaticRecording: 'DISABLED', recording: 'IDLE', classIsland: {connection: 'READY', bridgeVersion: null},
      examAware: {connection: 'READY', bridgeVersion: null}},
  });
  const first = await seedDevice(ready);
  await report(first.bearer, await open(first.bearer, 0));
  const previous = await seedDevice(repaired);
  await report(previous.bearer, await open(previous.bearer, 0));
  await prisma.workspace.update({where: {id: restored.id}, data: {isActive: false}});
  await prisma.workspace.update({where: {id: restored.id}, data: {isActive: true}});
  assert.equal((await prisma.npepDevice.findUniqueOrThrow({where: {id: previous.device.id}})).state, 'INVALIDATED');

  const login = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
  assert.equal(login.status, 200);
  const admin = (await login.json()).data.access_token;
  const schoolHeader = {Authorization: 'Bearer ' + admin};
  let newcomer, newBinding, nativeSession, accepted, overviewReads = 0, failNext = false, holdNext = false, released = false;
  let heldResponse;
  const membership = {schoolId_accountId: {schoolId: school.id, accountId: managerId}};
  let gatewayDenied = false, deniedReads = 0;
  let directoryFault;
  const directoryFaultsSeen = [];
  const commands = {
    devicepagefailed() { directoryFault = 'second-device-page'; },
    devicedenied() { directoryFault = 'device-denied'; },
    structurefailed() { directoryFault = 'structure'; },
    gatewaydenied() { gatewayDenied = true; },
    revoke() { return prisma.schoolMember.update({where: membership, data: {role: 'VIEWER'}}); },
    restore() { return prisma.schoolMember.update({where: membership, data: {role: 'ADMIN'}}); },
    revokeother() { return prisma.schoolMember.update({where: {schoolId_accountId: {schoolId: other.id, accountId: managerId}},
      data: {role: 'VIEWER'}}); },
    async pair() {
      newBinding = await prisma.classroomScreenBinding.findFirstOrThrow({where: {schoolId: school.id, administrativeClassId: seven.id}});
      const management = 'schools/' + school.id + '/class-management/' + newBinding.id;
      const prev = await native(management, admin, undefined, 200, '0.16');
      const password = randomSecret();
      await native(management, admin, {enabled: true, secret: password, expectedRevision: prev.revision,
        expectedClassId: prev.classId, expectedBindingRevision: prev.bindingRevision, expectedPairingRevision: prev.pairingRevision,
        requireUnpaired: true}, 200, '0.16');
      const pairingSecret = randomSecret();
      const pairing = await native('pairings/password', null, {...identity, installationId: randomUUID(),
        deviceName: '七班现场终端', appVersion: '1.1.10', pairingSecret, requestedCapabilities: ['device.status'],
        screenBindingId: newBinding.id, secret: password}, 201);
      const pairBearer = 'npepp1.' + pairing.pairingId + '.' + pairingSecret;
      const pair = await native('pairings/' + pairing.pairingId, pairBearer);
      assert.equal(pair.screenBindingId, newBinding.id);
      const credentialId = randomUUID(), deviceSecret = randomSecret();
      const configured = await native('pairings/' + pairing.pairingId + '/confirm', pairBearer,
        {...identity, approvalId: pair.approvalId, credentialId, deviceSecret}, 201);
      newcomer = {id: configured.deviceId, bearer: 'npep1.' + credentialId + '.' + deviceSecret};
      nativeSession = await open(newcomer.bearer, 0);
    },
    async report() { accepted = await report(newcomer.bearer, nativeSession); },
    async renew() { nativeSession = await open(newcomer.bearer, nativeSession.statusEpoch); },
    async restart() { await open(newcomer.bearer, nativeSession.statusEpoch); },
    fail() { failNext = true; },
    hold() { holdNext = 'overview'; heldResponse = undefined; },
    holdregistration() { holdNext = 'registration'; heldResponse = undefined; },
    async held() { for (let i = 0; !heldResponse && i < 100; i++) await new Promise(resolve => setTimeout(resolve, 25));
      assert.ok(heldResponse, 'the previous school response is waiting'); },
    release() { released = heldResponse !== undefined; heldResponse?.(); },
    async activate() {
      await prisma.academicTerm.create({data: {schoolId: other.id, academicYear: 2026, semester: 1, name: '起始学期', status: 'ACTIVE'}});
    },
    async scale() {
      const data = Array.from({length: 200}, (_, i) => ({
        schoolId: school.id, administrativeClassId: i < 100 ? eight.id : restored.id,
        name: i >= 198 ? '同步教室综合展示大屏' : '巡检屏 ' + String(i + 1).padStart(3, '0'),
        createdByAccountId: managerId, tokenHash: hash(randomSecret()),
      }));
      await prisma.classroomScreenBinding.createMany({data});
    },
    async classlist() {
      const thirdGrade = await prisma.grade.create({data: {termId: term.id, name: '高三', code: 'G3'}});
      await prisma.workspace.createMany({data: [
        ...Array.from({length: 100}, (_, i) => ({termId: term.id, gradeId: thirdGrade.id,
          name: '新班 ' + (i + 1), code: 'G3-N' + (i + 1), type: 'ADMIN_CLASS'})),
        ...[[grade.id, 'G2-EAST'], [thirdGrade.id, 'G3-EAST'], [thirdGrade.id, 'G3-PAUSE']].map(([gradeId, code]) =>
          ({termId: term.id, gradeId, name: '东区实验班', code, type: 'ADMIN_CLASS', isActive: code !== 'G3-PAUSE'})),
      ]});
    },
  };
  const destination = new URL(origin);
  const relay = createServer(async (req, res) => {
    const command = /^\/__school-overview\/([a-z]+)$/.exec(req.url)?.[1];
    if (command) {
      try {
        assert.ok(req.method === 'POST' && Object.hasOwn(commands, command));
        await commands[command]();
        res.writeHead(204).end();
      } catch (error) {
        console.error('Fixture command failed:', command, error.message);
        res.writeHead(500).end();
      }
      return;
    }
    const target = new URL(req.url, destination);
    const deviceDirectory = req.method === 'GET' && target.pathname === `/api/v2/npep/schools/${school.id}/device-overview`;
    if (deviceDirectory && directoryFault === 'second-device-page') target.searchParams.set('limit', '1');
    const failedDevicePage = deviceDirectory && directoryFault === 'second-device-page' && target.searchParams.has('cursor');
    const deniedDevicePage = deviceDirectory && directoryFault === 'device-denied';
    const failedStructure = directoryFault === 'structure' && target.pathname === `/api/v2/admin/schools/${school.id}/academic-structure`;
    if (failedDevicePage || deniedDevicePage || failedStructure) {
      directoryFaultsSeen.push(directoryFault);
      directoryFault = undefined;
      res.writeHead(deniedDevicePage ? 403 : 502).end();
      return;
    }
    const isOverview = req.method === 'GET' && target.pathname === overviewPath;
    if (isOverview) {
      overviewReads++;
      if (gatewayDenied) {
        gatewayDenied = false;
        res.writeHead(403, {'Content-Type': 'text/html'}).end('<html>Gateway unavailable</html>');
        return;
      }
      if (failNext) { failNext = false; res.writeHead(503).end(); return; }
    }
    const hold = (isOverview && holdNext === 'overview') ||
      (req.method === 'POST' && req.url.endsWith('/first-screen') && holdNext === 'registration');
    if (hold) holdNext = false;
    const upstream = request({hostname: '127.0.0.1', port: destination.port, path: target.pathname + target.search, method: req.method,
      headers: {...req.headers, host: destination.host}}, response => {
      if (isOverview && response.statusCode === 403) deniedReads++;
      if (hold) {
        const chunks = [];
        response.on('data', data => chunks.push(data));
        response.on('end', () => { heldResponse = () => {
          if (!res.destroyed) res.writeHead(response.statusCode, response.headers).end(Buffer.concat(chunks));
        }; });
        return;
      }
      res.writeHead(response.statusCode, response.headers);
      response.pipe(res);
    });
    upstream.on('error', () => { if (!res.headersSent) res.writeHead(502).end(); });
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    async function get(path) {
      const reply = await fetch(origin + path, {headers: schoolHeader});
      assert.equal(reply.status, 200);
      return (await reply.json()).data;
    }
    const historical = await get(prefix(school.id) + '?termId=' + oldTerm.id);
    assert.equal(historical.summary.screens, 1);
    assert.deepEqual(historical.screens.map(row => [row.classId, row.screenId]), [[oldSeven.id, oldScreen.id]]);
    assert.ok(historical.diagnostics.some(task => task.screenId === oldScreen.id && task.targetTab === 'screens'));
    const currentForWeb = await get(prefix(school.id) + '?termId=' + term.id);
    assert.ok(currentForWeb.diagnostics.some(task => task.code === 'ADMIN_CLASS_WITHOUT_SCREEN' && task.workspaceId === seven.id));
    assert.deepEqual(currentForWeb.screens.filter(row => row.classId === eight.id).map(row => row.screenId).sort(),
      [ready.id, waiting.id].sort());
    async function browserRequest(path, method, headers, body) {
      const reply = await fetch(origin + path, {method, headers: {...headers, 'Content-Type': 'application/json'},
        body: JSON.stringify(body)});
      assert.equal(reply.status, 200, path);
      return (await reply.json()).data;
    }
    const webPin = String(100000 + (randomBytes(4).readUInt32BE() % 900000));
    const loginCode = 'WEB.EIGHT';
    const observedWaitingScreen = async () => (await get(overviewPath)).screens.find(row => row.screenId === waiting.id);
    await browserRequest(`/api/v2/admin/schools/${school.id}/classroom-screens/${waiting.id}/account`,
      'PATCH', schoolHeader, {loginCode, pin: webPin});
    assert.equal((await observedWaitingScreen()).browser.state, 'AWAITING_LOGIN');
    const webSession = await browserRequest('/api/v2/classroom-screens/login', 'POST', {},
      {schoolCode: school.code, loginCode, pin: webPin, deviceFingerprint: randomUUID()});
    assert.equal(webSession.binding.id, waiting.id);
    assert.equal((await observedWaitingScreen()).browser.state, 'AWAITING_HEARTBEAT');
    const heartbeat = await browserRequest('/api/v2/classroom-screens/heartbeat', 'POST',
      {'X-Classworks-Screen-Token': webSession.token}, {online: true, realtimeConnected: true});
    const browserReady = await observedWaitingScreen();
    assert.deepEqual(browserReady.browser, {state: 'ONLINE', lastReportedAt: heartbeat.receivedAt});
    assert.deepEqual(browserReady.native, {state: 'AWAITING_PAIRING', lastReportedAt: null});
    console.log('PASS: legacy browser login and first duty heartbeat leave NPEduTools pending on the same screen.');
    const access = join(directory, 'desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, username: 'admin', password: pin, otherSchoolId: other.id,
      classSevenId: seven.id, classEightId: eight.id, waitingScreenId: waiting.id, readyScreenId: ready.id,
      restoredScreenId: repaired.id, allowOverviewTestWrites: true}), {mode: 0o600});
    await runDesktop(['--live-school-overview', access, evidence]);
    assert.deepEqual(directoryFaultsSeen, ['second-device-page', 'device-denied', 'structure']);
    assert.equal(deniedReads, 2, 'both revoked overview reads reached the real service');
    await commands.restore();
    assert.ok(overviewReads >= 7 && released && accepted.receivedAt && newcomer.id, 'all live stages ran');
    const summary = await get(overviewPath);
    assert.equal(summary.term.id, term.id);
    assert.equal(summary.screens.find(row => row.screenId === newBinding.id).native.state, 'REPORTED');
    const saved = await prisma.npepDevice.findUniqueOrThrow({where: {id: newcomer.id}});
    assert.equal(saved.lastSeenAt, null);
    assert.equal(saved.lastReportedAt.toISOString(), accepted.receivedAt);
    assert.equal(await prisma.classroomScreenBinding.count({where: {administrativeClassId: seven.id}}), 1);
    console.log('PASS: real NPEP password pairing, accepted report and Host restart preserve one registered screen and the durable receipt. Historical web overview keeps original scope and navigation.');
    await runDatabase();
    console.log('PASS: focused school management overview PostgreSQL regressions.');
  } finally {
    const closed = new Promise(resolve => relay.close(resolve));
    relay.closeAllConnections();
    await closed;
  }
}
