import assert from 'node:assert/strict';
import {randomBytes, randomUUID} from 'node:crypto';
import {createServer} from 'node:http';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyDeviceFieldActions({prisma, origin, fixture, fromKv, directory, evidence, withReports = false, web, runDesktop, runNative}) {
  const {school, term, pin, managerId, identity} = fixture;
  await mkdir(evidence, {recursive: true});
  const {defaultStudySettings} = await fromKv('domain/npep/studySettings.js');
  const {studyHomeworkDate} = await fromKv('services/npepStudyHomework.js');
  const {secretHash} = await fromKv('domain/npep/wire.js');
  const date = studyHomeworkDate();
  const weekday = new Date(date + 'T00:00:00Z').getUTCDay() || 7;
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const room = await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, name: '高二（7）班', code: 'FIELD-7', type: 'ADMIN_CLASS'}});
  let unrelatedClassId;
  if (withReports) {
    unrelatedClassId = (await prisma.workspace.create({data: {termId:term.id, gradeId:grade.id, name:'高二（8）班', code:'REPORT-8', type:'ADMIN_CLASS'}})).id;
    await prisma.schoolMember.create({data:{schoolId:fixture.other.id, accountId:managerId, role:'ADMIN'}});
  }
  const settings = defaultStudySettings();
  settings.schedule = {...settings.schedule, effectiveFrom: date, rules: [
    {days: [weekday], start: '19:00', end: '20:00'}, {days: [weekday], start: '20:10', end: '21:00'}]};
  const policy = {mode: 'Override', settings};
  await prisma.$executeRaw`INSERT INTO "NpepStudySetting" ("schoolId","termId","targetType","targetId",revision,policy,"updatedBy","updatedAt")
    VALUES (${school.id},${term.id},'GRADE',${grade.id},1,${JSON.stringify(policy)}::jsonb,${managerId},NOW())`;

  async function general(path, {method = 'GET', body, token, screenToken, status = 200} = {}) {
    const reply = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token && {Authorization: 'Bearer ' + token}), ...(screenToken && {'X-Classworks-Screen-Token': screenToken})},
    body: body === undefined ? undefined : JSON.stringify(body)});
    const result = await reply.json();
    assert.equal(reply.status, status, path + ' ' + (result.code ?? ''));
    return result.data ?? result;
  }
  const login = username => general('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username, password: pin}});
  const [admin, second] = await Promise.all([login('admin'), login('admin2')]);
  const screenPath = '/api/v2/classroom-screens';
  const adminPath = '/api/v2/admin/schools/' + school.id;
  const rearLoginCode = 'FIELD-REAR';
  const rearBinding = await general(adminPath + '/classroom-screen-accounts', {method: 'POST', token: admin.access_token, status: 201,
    body: {administrativeClassId: room.id, name: '七班后屏', loginCode: rearLoginCode, pin}});
  const frontBinding = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: room.id,
    createdByAccountId: managerId, name: '七班前屏', tokenHash: randomBytes(32).toString('hex'), npepPrepared: true}});
  const links = await Promise.all([frontBinding.id, rearBinding.id].map(id => prisma.classroomScreenBinding.findUniqueOrThrow({where: {id}})));
  async function pair(binding, deviceName) {
    const secret = randomBytes(32).toString('base64url');
    const device = await prisma.npepDevice.create({data: {...identity, id: randomUUID(), installationId: randomUUID(), credentialId: randomUUID(),
      secretHash: secretHash(secret), schoolId: school.id, administrativeClassId: room.id, screenBindingId: binding.id,
      bindingRevision: binding.npepBindingRevision, deviceName, credentialExpiresAt: new Date(Date.now() + 86400000)}});
    return {binding, device, bearer: 'npep1.' + device.credentialId + '.' + secret};
  }
  const [front, rear] = await Promise.all([pair(links[0], '前屏原生设备'), pair(links[1], '后屏原生设备')]);
  const frontReportId = randomUUID();
  if (withReports) {
    const receivedAt = new Date().toISOString();
    const report = {sessionId: frontReportId, startedAt: new Date(Date.now() - 180000).toISOString(),
      endedAt: new Date(Date.now() - 120000).toISOString(), receivedAt, outcome: 'Interrupted',
      summary: {elapsedSeconds: 60, sampledSeconds: 45, coverage: .75, energyMeanDbfs: -36.3, peakDbfs: -21.8, clippedPercent: 0, frames: 45},
      estimate: {timeWeighting: 'FAST', state: 'Invalidated', reason: 'DEVICE_CHANGED', summary: {sampledSeconds: 15, coverage: .25, energyMeanDbA: 52.1, maximumDbA: 61.3}},
      timing: {observedEndedAt: new Date(Date.now() - 175000).toISOString(), clockDiscontinuity: true}};
    await prisma.npepNoiseDevice.create({data: {deviceId: front.device.id, data: {status: null, reports: [report], commands: []}}});
    const names = Array.from({length: 205}, (_, index) => ({...identity, id: randomUUID(), installationId: randomUUID(),
      credentialId: randomUUID(), secretHash: secretHash(randomBytes(32).toString('base64url')), schoolId: school.id,
      administrativeClassId: room.id, screenBindingId: front.binding.id, bindingRevision: front.binding.npepBindingRevision,
      deviceName: '诊断批次-sensitive-path-C-TEST-' + index, state: 'REVOKED', revokedAt: new Date(), credentialExpiresAt: new Date(Date.now() + 86400000)}));
    await prisma.npepDevice.createMany({data: names});
  }
  const webSession = await general(screenPath + '/login', {method: 'POST', body: {schoolCode: school.code,
    loginCode: rearLoginCode, pin, deviceFingerprint: randomUUID()}});
  await general(screenPath + '/feed', {screenToken: webSession.token});
  const scope = {termId: term.id, classId: room.id, bindingId: rear.binding.id};
  const query = '?' + new URLSearchParams(scope);
  const root = '/api/v2/npep/schools/' + school.id + '/devices/';
  const path = root + rear.device.id + '/field-actions';
  async function npep(url, {token = admin.access_token, body, version = '0.19', status = 200} = {}) {
    const id = body?.requestId ?? randomUUID();
    const reply = await fetch(origin + url, {method: body ? 'POST' : 'GET', headers: {Authorization: 'Bearer ' + token,
      'Content-Type': 'application/json', 'X-NPEP-Version': version, 'X-Request-Id': id}, body: body ? JSON.stringify(body) : undefined});
    const json = await reply.json();
    assert.equal(reply.status, status, url + ' ' + (json.error?.code ?? ''));
    return json.data ?? json.error;
  }
  const classSecret = randomBytes(12).toString('base64url');
  await npep('/api/v2/npep/schools/' + school.id + '/class-management/' + rear.binding.id, {version: '0.16', body: {
    requestId: randomUUID(), expectedRevision: 0, enabled: false, secret: classSecret,
  }});
  const marks = new Set(), steps = [];
  let native, nativeError, browser, resumeBody, resumeReceipt, revokeBody, revokeReceipt, legacyEvidence, legacyReports;
  let reportProof, exportProof;
  let revokePosts = 0, ownRevokeReads = 0;
  let legacyFront;
  async function wait(mark, timeout = 35000) {
    const until = Date.now() + timeout;
    while (!marks.has(mark) && Date.now() < until) { if (nativeError) throw nativeError; await delay(120); }
    assert.ok(marks.has(mark), 'missing ' + mark);
  }
  const get = (body, token) => npep(`${path}/requests/${body.requestId}` + query, {token});
  const reportsFor = (peer, status = 200) => npep(root + peer.device.id + '/noise', {version: '0.6', status});
  const classReport = (peer, sessionId, classroom = room.id, status = 200, schoolId = school.id, token = admin.access_token) => npep(
    `/api/v2/npep/schools/${schoolId}/study-sessions?` + new URLSearchParams({termId: term.id, classId: classroom, deviceId: peer.device.id, sessionId}),
    {version: '0.17', status, token});

  async function checkpoint(name) {
    if (name === 'arm') {
      await wait('host-ready', 100000);
      marks.add('armed');
    } else if (name === 'received') {
      assert.equal((await get(resumeBody)).outcome, 'QUEUED');
      assert.equal((await get(resumeBody)).sampleAt, null);
      const other = await npep(`${path}/requests/${resumeBody.requestId}` + query, {token: second.access_token, status: 404});
      assert.equal(other.code, 'FIELD_ACTION_NOT_FOUND');
      marks.add('allow-delivery');
    } else if (name === 'processed') {
      await wait('host-handled');
      const actual = await get(resumeBody);
      assert.equal(actual.outcome, 'ACCEPTED');
      assert.ok(actual.deviceProcessedAt);
      assert.equal(actual.sampleAt, null);
      marks.add('allow-sample');
    } else if (name === 'sample-shown') {
      await wait('host-sampled');
      resumeReceipt = await get(resumeBody);
      assert.ok(resumeReceipt.sampleAt);
      if (withReports) {
        assert.ok(resumeReceipt.sampleSessionId);
        const old = (await reportsFor(rear)).reports;
        assert.equal(old.length, 1);
        assert.notEqual(old[0].sessionId, resumeReceipt.sampleSessionId);
        assert.equal((await classReport(rear, resumeReceipt.sampleSessionId, room.id, 404)).code, 'NOT_FOUND');
      }
      await legacyEvidence();
      marks.add('allow-local-stop');
    } else if (name === 'second-stop') {
      await wait('host-restopped');
      const replay = await npep(path, {body: resumeBody});
      assert.deepEqual([replay.commandId, replay.acceptedAt, replay.outcome, replay.sampleAt, replay.sampleFresh],
        [resumeReceipt.commandId, resumeReceipt.acceptedAt, 'ACCEPTED', resumeReceipt.sampleAt, false]);
      const original = await npep(root + rear.device.id + '/study-settings', {version: '0.10'});
      assert.equal(original.execution.reason, 'WINDOW_SKIPPED');
      assert.equal(original.captureVerified, false);
      const [row] = await prisma.$queryRaw`SELECT policy, revision FROM "NpepStudySetting" WHERE "targetId"=${grade.id}`;
      assert.deepEqual(row, {policy, revision: 1});
      if (withReports) {
        const current = (await reportsFor(rear)).reports;
        assert.equal(current.length, 2);
        const matching = current.find(report => report.sessionId === resumeReceipt.sampleSessionId);
        assert.ok(matching.summary.sampledSeconds > 0);
        assert.ok(current.every(report => report.ownership.classId === room.id));
        const endedEvents = current.map(report => ({eventId:randomUUID(), deviceId:rear.device.id,
          ...report.ownership, digest:randomBytes(32).toString('hex'), metadata:{sessionId:report.sessionId,reason:'Random',photoEventId:null},
          createdAt:new Date(),expiresAt:new Date(Date.now()+86400000)}));
        await prisma.npepStudyExitEvent.createMany({data:endedEvents});
        const exact = await classReport(rear, matching.sessionId);
        assert.deepEqual(exact.items[0].fragments.map(item => item.sessionId), [matching.sessionId]);
        assert.deepEqual(exact.items[0].events.map(item => item.eventId),
          endedEvents.filter(item => item.metadata.sessionId === matching.sessionId).map(item => item.eventId));
        assert.equal((await classReport(front, matching.sessionId, room.id, 404)).code, 'NOT_FOUND');
        assert.equal((await classReport(front, frontReportId, room.id, 404)).code, 'NOT_FOUND');
        assert.equal((await classReport(rear, matching.sessionId, unrelatedClassId, 404)).code, 'NOT_FOUND');
        assert.equal((await classReport(rear, matching.sessionId, room.id, 403, fixture.other.id, second.access_token)).code, 'SCHOOL_ADMIN_REQUIRED');
        assert.deepEqual((await reportsFor(front)).reports.map(report => report.sessionId), [frontReportId]);
        await legacyReports(matching.sessionId);
        reportProof = {matchedSession: matching.sessionId, precedingSession: current.find(report => report.sessionId !== matching.sessionId).sessionId,
          originalClass: room.id, frontReportId, retainedOwnership: matching.ownership, replacementDeviceId: null};
      }
      await prisma.npepDevice.update({where: {id: rear.device.id}, data: {lastSeenAt: new Date(Date.now() - 120000), lastReportedAt: new Date(Date.now() - 120000)}});
    } else if (name === 'revoked') {
      assert.deepEqual([revokePosts, ownRevokeReads], [1, 1]);
      revokeReceipt = await get(revokeBody);
      assert.deepEqual([revokeReceipt.target.deviceId, revokeReceipt.outcome], [rear.device.id, 'REVOKED']);
      assert.equal((await general(screenPath + '/session', {screenToken: webSession.token})).binding.id, rear.binding.id);
      await general(screenPath + '/feed', {screenToken: webSession.token});
      const binding = await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: rear.binding.id}});
      assert.deepEqual([binding.npepBindingRevision, binding.credentialVersion, binding.loginCode],
        [rear.binding.npepBindingRevision, rear.binding.credentialVersion, rear.binding.loginCode]);
      const auditCount = await prisma.npepAudit.count({where: {objectId: rear.device.id, action: 'DEVICE_REVOKED'}});
      assert.equal(auditCount, 1);
      marks.add('rear-revoked');
      await wait('old-credential-rejected');
      legacyFront = await npep(root + front.device.id + '/study-settings', {version: '0.10'});
      assert.equal(legacyFront.captureVerified, true);
      if (withReports) {
        const replacement = await pair(rear.binding, '后屏新机');
        assert.equal((await reportsFor(rear, 401)).code, 'AUTH_INVALID');
        assert.deepEqual((await reportsFor(replacement)).reports, []);
        assert.equal((await classReport(rear, reportProof.matchedSession)).items[0].deviceId, rear.device.id);
        assert.equal((await classReport(replacement, reportProof.matchedSession, room.id, 404)).code, 'NOT_FOUND');
        reportProof.replacementDeviceId = replacement.device.id;
      }
    } else if (withReports && name === 'report-revoke') {
      await prisma.schoolMember.update({where:{schoolId_accountId:{schoolId:school.id,accountId:managerId}},data:{role:'VIEWER'}});
    } else if (withReports && name === 'report-restored') {
      await prisma.schoolMember.update({where:{schoolId_accountId:{schoolId:school.id,accountId:managerId}},data:{role:'ADMIN'}});
    } else if (withReports && name === 'export-revoke') {
      const [primary, capped] = await Promise.all(['diagnostics-partial.json', 'diagnostics-capped.json'].map(async file => {
        const text = await readFile(join(evidence, 'console', file), 'utf8');
        assert.doesNotMatch(text, /sensitive-path|诊断批次|deviceName|screenBindingName|credential|secretHash|console-log|pin|reportId/i);
        return JSON.parse(text);
      }));
      assert.deepEqual([primary.matchedTotal, primary.loadedCount, primary.exportedCount, primary.hasMore], [205, 100, 100, true]);
      assert.deepEqual([capped.matchedTotal, capped.loadedCount, capped.exportedCount, capped.omittedLoadedCount, capped.hasMore], [205, 205, 200, 5, false]);
      assert.deepEqual(capped.items.slice(0,100), primary.items);
      assert.ok(capped.items.every(item => item.state === 'REVOKED' && item.schoolId === school.id && item.screenBindingId === front.binding.id));
      exportProof = {partial:[primary.matchedTotal, primary.loadedCount, primary.exportedCount], capped:[capped.matchedTotal, capped.loadedCount, capped.exportedCount], deniedWrite: true};
      await prisma.schoolMember.update({where:{schoolId_accountId:{schoolId:school.id,accountId:managerId}},data:{role:'VIEWER'}});
    } else if (withReports && name === 'export-restored') {
      await prisma.schoolMember.update({where:{schoolId_accountId:{schoolId:school.id,accountId:managerId}},data:{role:'ADMIN'}});
    } else if (withReports && name === 'export-begin-switch') {
      await wait('delayed-diagnostic-response');
    } else if (withReports && name === 'export-finish-switch') {
      marks.add('release-diagnostic-response');
      const moved = await general(adminPath + '/classroom-screens/' + rear.binding.id, {method:'PATCH', token:second.access_token,
        body:{administrativeClassId:unrelatedClassId}});
      assert.equal(moved.administrativeClassId, unrelatedClassId);
      assert.equal((await classReport(rear, reportProof.matchedSession)).items[0].deviceId, rear.device.id);
      assert.equal((await classReport(rear, reportProof.matchedSession, unrelatedClassId, 404)).code, 'NOT_FOUND');
      reportProof.bindingAfterMove = {id:rear.binding.id,classId:moved.administrativeClassId};
    } else throw new Error('unexpected checkpoint ' + name);
    steps.push(name);
  }

  // A second independent connection lets HTTP drive exact report ordering. The main
  // path above still proves local stopping and sampling with the native Host.
  async function verifyAttribution() {
    const binding = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: room.id,
      createdByAccountId: managerId, name: '归因夹具屏', tokenHash: randomBytes(32).toString('hex')}});
    const peer = await pair(binding, '归因夹具');
    const deviceApi = (operation, version, fields, status = 200) => npep('/api/v2/npep/device/' + operation,
      {version, status, token: peer.bearer, body: fields && {requestId: randomUUID(), ...fields}});
    const session = await deviceApi('sessions', '0.1', {...identity, runId: randomUUID(), expectedStatusEpoch: 0}, 201);
    const current = await deviceApi('study-settings', '0.10');
    const context = {identity: current.identity, sessionId: session.sessionId, runId: session.runId, statusEpoch: session.statusEpoch};
    const window = {start: date + 'T19:00:00.000', end: date + 'T20:00:00.000'};
    const stamp = date + 'T19:10:00.000';
    const fieldScope = {...scope, bindingId: binding.id}, fieldQuery = '?' + new URLSearchParams(fieldScope);
    const endpoint = root + peer.device.id + '/field-actions';
    let observationSequence = 0, executionSequence = 0, exchangeSequence = 0;
    async function observe(execution, schoolNow = stamp, activeWindow = window) {
      await deviceApi('study-settings/receipt', '0.10', {context, sequence: ++observationSequence, status: {version: current.version,
        policyState: 'APPLIED', presentationState: 'NOT_INTEGRATED', captureState: 'NOT_INTEGRATED', phase: 'Active',
        reason: 'WINDOW_ACTIVE', clockReady: true, schoolNow, window: activeWindow}});
      await deviceApi('study-settings/execution', '0.10', {context, sequence: ++executionSequence, version: current.version, status: execution});
    }
    const stop = {owner: 'None', state: 'Stopped', reason: 'WINDOW_SKIPPED', sessionId: null, clockReady: true, schoolNow: stamp, window, signalFresh: false};
    const exchange = receipt => deviceApi('study-settings/resume-exchange', '0.11', {context,
      sequence: ++exchangeSequence, version: current.version, receipt});
    async function enqueue(token) {
      const state = await npep(endpoint + fieldQuery, {token});
      assert.equal(state.resume.reason, 'READY');
      const body = {...fieldScope, requestId: randomUUID(), action: 'RESUME', expectedBindingRevision: state.target.bindingRevision,
        reviewToken: state.resume.reviewToken};
      return {body, receipt: await npep(endpoint, {token, body})};
    }
    async function start(token) {
      const request = await enqueue(token);
      const {receipt} = request;
      const delivered = await exchange(null);
      assert.equal(delivered.command.commandId, receipt.commandId);
      assert.equal((await exchange({commandId: receipt.commandId, outcome: 'ACCEPTED'})).command, null);
      return request;
    }
    const receiptOf = (request, token) => npep(`${endpoint}/requests/${request.body.requestId}` + fieldQuery, {token});
    await observe(stop);
    await exchange(null);
    const a = await start(admin.access_token);
    assert.deepEqual([(await receiptOf(a, admin.access_token)).outcome, (await receiptOf(a, admin.access_token)).sampleAt], ['ACCEPTED', null]);
    await observe(stop);
    const replay = await npep(endpoint, {body: a.body});
    assert.equal(replay.commandId, a.receipt.commandId);
    assert.equal((await exchange(null)).command, null);
    const stopped = await npep(endpoint + fieldQuery);
    assert.equal(stopped.resume.executionReason, 'WINDOW_SKIPPED');
    assert.equal(stopped.resume.currentSamplingAt, null);
    const b = await start(second.access_token);
    await observe({...stop, owner: 'Schedule', state: 'Active', reason: 'WINDOW_ACTIVE', signalFresh: true, sessionId: randomUUID()});
    const receiptA = await receiptOf(a, admin.access_token), receiptB = await receiptOf(b, second.access_token);
    assert.deepEqual([receiptA.outcome, receiptA.sampleAt, receiptA.sampleFresh], ['ACCEPTED', null, false]);
    assert.ok(receiptB.sampleAt && receiptB.sampleFresh);
    assert.ok((await npep(endpoint + fieldQuery)).resume.currentSamplingAt);
    await observe(stop);
    const historyB = await receiptOf(b, second.access_token);
    assert.equal(historyB.sampleAt, receiptB.sampleAt);
    assert.equal(historyB.sampleFresh, false);

    const ready = (await npep(endpoint + fieldQuery)).resume;
    await observe({...stop, owner: 'Manual', state: 'Active', reason: 'MANUAL_ACTIVE'});
    const manual = (await npep(endpoint + fieldQuery)).resume;
    assert.deepEqual([manual.reason, manual.reviewToken], ['MANUAL_OCCUPIED', null]);
    const manualResponse = await npep(endpoint, {status: 409, body: {...a.body, requestId: randomUUID(), reviewToken: ready.reviewToken}});
    assert.equal(manualResponse.code, 'FIELD_ACTION_CHANGED');

    const closing = date + 'T19:59:57.000';
    await observe({...stop, schoolNow: closing}, closing);
    await exchange(null);
    const expires = await enqueue(admin.access_token);
    await delay(Math.max(0, Date.parse(expires.receipt.expiresAt) - Date.now()) + 120);
    const later = date + 'T20:12:00.000';
    const laterWindow = {start: date + 'T20:10:00.000', end: date + 'T21:00:00.000'};
    await observe({...stop, schoolNow: later, window: laterWindow}, later, laterWindow);
    assert.equal((await exchange(null)).command, null);
    const expired = await receiptOf(expires, admin.access_token);
    assert.deepEqual([expired.outcome, expired.deviceProcessedAt, expired.sampleAt], ['UNKNOWN', null, null]);
    assert.equal((await npep(endpoint + fieldQuery)).resume.reason, 'READY');
    return {receiptA, receiptB, historyB, earlierReplay: replay.commandId, manual: manual.reason, expired};
  }

  const relay = createServer(async (req, res) => {
    try {
      const url = new URL(req.url, origin);
      const name = url.pathname.split('/').at(-1);
      if (url.pathname.startsWith('/__field/native/')) {
        if (req.method === 'GET') return res.writeHead(marks.has(name) ? 204 : 404).end();
        marks.add(name);
        return res.writeHead(204).end();
      }
      if (url.pathname.startsWith('/__field/console/')) { await checkpoint(name); return res.writeHead(204).end(); }
      const chunks = [];
      for await (const part of req) chunks.push(part);
      const body = chunks.length ? Buffer.concat(chunks) : undefined;
      const headers = {...req.headers};
      for (const key of ['connection', 'host', 'transfer-encoding', 'content-length']) delete headers[key];
      const upstream = await fetch(url, {method: req.method, headers, body, redirect: 'manual'});
      const response = Buffer.from(await upstream.arrayBuffer());
      if (withReports && url.pathname.endsWith('/device-overview') && url.searchParams.get('search') === '延迟诊断') {
        marks.add('delayed-diagnostic-response');
        await wait('release-diagnostic-response');
      }
      if (url.pathname === path && req.method === 'POST' && upstream.ok) {
        const data = JSON.parse(body);
        if (data.action === 'RESUME') resumeBody = data;
        if (data.action === 'REVOKE') {
          revokeBody = data;
          revokePosts++;
          if (revokePosts === 1) return res.writeHead(502).end();
        }
      }
      if (revokeBody && url.pathname === `${path}/requests/${revokeBody.requestId}` && upstream.ok) ownRevokeReads++;
      const clean = Object.fromEntries(upstream.headers);
      for (const key of ['connection', 'transfer-encoding', 'content-encoding', 'content-length']) delete clean[key];
      res.writeHead(upstream.status, clean).end(response);
    } catch (error) { console.error(error.message); res.writeHead(500).end(error.message); }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  const relayOrigin = 'http://127.0.0.1:' + relay.address().port;
  const access = join(directory, 'field-action-console.json'), nativeAccess = join(directory, 'field-action-host.json');
  try {
    const registration = value => ({...identity, bearer: value.bearer, classSecret});
    await writeFile(nativeAccess, JSON.stringify({origin: relayOrigin, isolated: true, date, withReports, front: registration(front), rear: registration(rear)}), {mode: 0o600});
    await writeFile(access, JSON.stringify({origin: relayOrigin, schoolCode: school.code, schoolId: school.id, pin, roomId: room.id,
      frontId: front.binding.id, rearId: rear.binding.id, frontDeviceId: front.device.id, rearDeviceId: rear.device.id, frontReportId,
      otherSchoolId: fixture.other.id, withReports, allowWrites: true}), {mode: 0o600});
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (edge, site) => {
      browser = await edge.newContext({viewport: {width: 1280, height: 900}, serviceWorkers: 'block', storageState: {cookies: [], origins: [{origin: site,
        localStorage: [{name: 'classworks-v2-access-token', value: second.access_token}, {name: 'classworks-v2-refresh-token', value: second.refresh_token},
          {name: 'classworks-v2-oobe', value: JSON.stringify({version: 1, completed: true, roleHint: 'teacher'})}]}]}});
      const page = await browser.newPage();
      await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
      await page.goto(site + '/classworks-admin?section=npep-devices&school=' + school.id + '&term=' + term.id);
      if (withReports) {
        await page.getByRole('textbox', {name:'搜索设备、班级或大屏名称'}).fill('后屏原生设备');
        await page.getByRole('button', {name:'搜索', exact:true}).click();
      }
      const row = page.locator('.npep-device').filter({hasText: '后屏原生设备'});
      legacyEvidence = async () => {
        await row.getByRole('button', {name: '查看设备详情', exact: true}).click();
        await row.getByRole('button', {name: '刷新自习应用回执', exact: true}).click();
        await row.getByText(/设备已处理恢复指令/).waitFor();
        await page.screenshot({path: join(evidence, 'edge-legacy-receipt.png'), animations: 'disabled'});
      };
      legacyReports = async sessionId => {
        await row.getByRole('button', {name:'噪音报告', exact:true}).click();
        await page.getByRole('article', {name:'统计报告 ' + sessionId}).waitFor();
        await page.screenshot({path: join(evidence, 'edge-legacy-device-report.png'), animations:'disabled'});
      };
      await row.getByRole('heading', {name: '后屏原生设备'}).waitFor();
      native = runNative(nativeAccess, join(evidence, 'native-host.log'));
      native.catch(error => { nativeError = error; });
      await wait('host-ready', 100000);
      await runDesktop(['--live-device-field-actions', access, join(evidence, 'console')]);
      await native;
    }});
    assert.deepEqual(steps, ['arm', 'received', 'processed', 'sample-shown', 'second-stop', 'revoked', ...(withReports
      ? ['report-revoke','report-restored','export-revoke','export-restored','export-begin-switch','export-finish-switch'] : [])]);
    const attribution = await verifyAttribution();
    await writeFile(join(evidence, 'device-field-actions-result.json'), JSON.stringify({result: 'PASS', steps, marks: [...marks],
      front: {bindingId: front.binding.id, id: front.device.id, captureVerified: legacyFront.captureVerified},
      rear: {bindingId: rear.binding.id, id: rear.device.id}, resumeReceipt, revokeReceipt, revokePosts, ownRevokeReads, attribution,
      reportProof, exportProof, signals: 'synthetic audio and school clock; actual NoiseService, NoiseScheduleService and StudySettingsService'}, null, 2));
  } finally {
    for (const name of ['armed', 'allow-delivery', 'allow-sample', 'allow-local-stop', 'rear-revoked', 'release-diagnostic-response']) marks.add(name);
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
    await native?.catch(() => {});
    await browser?.close().catch(() => {});
  }
}
