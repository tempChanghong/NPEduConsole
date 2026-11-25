import assert from 'node:assert/strict';
import {randomBytes, randomUUID} from 'node:crypto';
import {mkdir, writeFile} from 'node:fs/promises';
import {createServer, request as forward} from 'node:http';
import {join} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';

export async function verifyClassroomActionPolicyConsole({prisma, origin, fixture, fromKv, directory, evidence, runDesktop}) {
  const {school, term, pin, identity, managerId, devices, classes, classroomBearer} = fixture;
  const room = await prisma.workspace.findUniqueOrThrow({where: {id: classes[0]}});
  const grade = await prisma.grade.findUniqueOrThrow({where: {id: room.gradeId}});
  const neighbor = await prisma.workspace.findFirstOrThrow({where: {id: {in: classes.slice(1)}, gradeId: grade.id}, orderBy: {code: 'asc'}});
  const front = await prisma.npepDevice.findUniqueOrThrow({where: {id: devices[0]}});
  const rear = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: room.id,
    name: '同班后屏', tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId}});
  const {secretHash} = await fromKv('domain/npep/wire.js');
  const rearSecret = randomBytes(32).toString('base64url');
  const rearDevice = await prisma.npepDevice.create({data: {...identity, id: randomUUID(), installationId: randomUUID(), credentialId: randomUUID(),
    schoolId: school.id, administrativeClassId: room.id, screenBindingId: rear.id, bindingRevision: rear.npepBindingRevision,
    deviceName: '同班后屏一体机', secretHash: secretHash(rearSecret), credentialExpiresAt: new Date(Date.now() + 3600000)}});
  const rearBearer = `npep1.${rearDevice.credentialId}.${rearSecret}`;
  const {schoolManagementRepository} = await fromKv('services/npepSchoolManagementService.js');
  const {managementVerifier} = await fromKv('domain/npep/schoolManagement.js');
  for (const classId of classes) await schoolManagementRepository.saveCredential(prisma, school.id, classId, {
    revision: 1, verifier: managementVerifier(randomBytes(32).toString('base64url')), updatedAt: new Date().toISOString(),
  });
  const login = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
  assert.equal(login.status, 200);
  const token = (await login.json()).data.access_token;
  async function call(path, body, bearer = token, version = '0.16', policySchema = 1) {
    const requestId = randomUUID();
    const response = await fetch(origin + '/api/v2/npep' + path, {method: body ? 'POST' : 'GET', signal: AbortSignal.timeout(10000),
      headers: {Authorization: 'Bearer ' + bearer, 'Content-Type': 'application/json', 'X-NPEP-Version': version, 'X-Request-Id': requestId,
        ...(policySchema > 1 ? {'X-NPEP-Classroom-Action-Policy': '2'} : {}),
        ...(policySchema >= 3 ? {'X-NPEP-Classroom-Action-Inheritance': '1'} : {}),
        ...(policySchema >= 4 ? {'X-NPEP-Manual-Study-Policy': '1'} : {}),
        ...(policySchema === 5 ? {'X-NPEP-Manual-Recording-Policy': '1'} : {})},
      ...(body ? {body: JSON.stringify({requestId, ...body})} : {})});
    const result = await response.json();
    assert.ok(response.ok, `${path}: ${result.error?.code}`);
    return result.data;
  }
  const path = `/schools/${school.id}/classroom-action-policy`;
  const keyId = randomUUID();
  const change = {termId: term.id, scopeKind: 'GRADE', scopeId: grade.id, operation: 'REGISTER_USB', actions: null, keyId: null,
    usbKey: {keyId, label: '验收值班 U 盘', mediaHash: randomBytes(32).toString('hex'), secretHash: randomBytes(32).toString('hex')}};
  const preview = await call(path + '/preview', {change});
  await call(path, {change, expectedVersion: preview.version});
  const oldKey = {...change.usbKey, keyId: randomUUID(), label: '跨学期挂失演示盘', mediaHash: randomBytes(32).toString('hex'), secretHash: randomBytes(32).toString('hex')};
  async function openDeviceSession(bearer, expectedStatusEpoch = 0) {
    const runId = randomUUID();
    const {sessionId, statusEpoch} = await call('/device/sessions', {...identity, runId, expectedStatusEpoch}, bearer, '0.1');
    return {runId, sessionId, statusEpoch};
  }
  function report(bearer, context, sequence, actionPolicyReceipt) {
    return call('/device/school-management/receipt', {context, sequence, microphones: [], cameras: [], unlocked: false,
      appliedRevision: 0, credentialRevision: 1, selectedMicrophoneId: null, selectedCameraId: null, error: null, actionPolicyReceipt}, bearer);
  }
  const frontSession = await openDeviceSession(classroomBearer);
  const scopePath = path + `/scope/${term.id}/CLASS/${room.id}`;
  const storageError = 'CLASSROOM_POLICY_STORE_UNAVAILABLE';
  let rearSession, faultEvidence;

  let dropped = false, writes = 0, ownReads = 0, activeFinal = null;
  const proxy = createServer((request, response) => {
    if (request.method === 'POST' && request.url === '/__classroom-policy-fixture/restart-rear') {
      request.resume();
      (async () => {
        assert.ok(rearSession && faultEvidence, 'rear first reported a storage fault');
        const next = await openDeviceSession(rearBearer, rearSession.statusEpoch);
        assert.notEqual(next.sessionId, rearSession.sessionId);
        const scope = await call(scopePath, null, token, '0.16', 5);
        const oldFault = scope.screens.find(screen => screen.bindingId === rear.id);
        assert.equal(oldFault.observed.error, storageError);
        assert.equal(oldFault.observationCurrent, false);
        faultEvidence.currentAfterRestart = oldFault.observationCurrent;
        response.writeHead(204).end();
      })().catch(error => {
        console.error(error);
        response.writeHead(500).end();
      });
      return;
    }
    if (request.method === 'POST' && request.url === '/__classroom-policy-fixture/archive') {
      request.resume();
      (async () => {
        assert.equal(activeFinal, null);
        activeFinal = await call(scopePath, null, token, '0.16', 5);
        const oldRegistration = {...change, usbKey: oldKey};
        const review = await call(path + '/preview', {change: oldRegistration}, token, '0.16', 5);
        await call(path, {change: oldRegistration, expectedVersion: review.version}, token, '0.16', 5);
        await prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}});
        response.writeHead(204).end();
      })().catch(error => {
        console.error(error);
        response.writeHead(500).end();
      });
      return;
    }
    const upstream = forward(new URL(request.url, origin), {method: request.method, headers: request.headers}, result => {
      const apply = request.method === 'POST' && request.url === '/api/v2/npep' + path;
      if (apply) writes++;
      if (request.method === 'GET' && request.url.startsWith('/api/v2/npep' + path + '/receipts/')) ownReads++;
      if (apply && result.statusCode === 200 && !dropped) {
        dropped = true;
        result.once('end', () => response.destroy());
        result.resume();
        return;
      }
      response.writeHead(result.statusCode, result.headers);
      result.pipe(response);
    });
    upstream.on('error', () => response.destroy());
    request.pipe(upstream);
  });
  await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
  const access = join(directory, 'classroom-action-console.json');
  await mkdir(evidence, {recursive: true});
  await writeFile(access, JSON.stringify({origin: `http://127.0.0.1:${proxy.address().port}`, schoolCode: school.code, schoolId: school.id,
    pin, gradeCode: grade.code, classCode: room.code, neighborCode: neighbor.code,
    frontId: front.screenBindingId, rearId: rear.id, historicalKeyId: oldKey.keyId}));
  let done = false;
  const applyObservation = async () => {
    let acknowledged = 0;
    while (!done) {
      const [state] = await prisma.$queryRaw`SELECT revision::int AS revision FROM "NpepClassroomActionState" WHERE "schoolId"=${school.id}`;
      if (state?.revision !== acknowledged + 2) {
        await delay(300);
        continue;
      }
      const current = await call('/device/school-management', null, classroomBearer, '0.16', 4);
      const policy = current.actionPolicy;
      if (policy.revision === acknowledged + 2) {
        assert.equal(policy.source, 'GRADE');
        const target = await call('/device/school-management', null, classroomBearer, '0.16', 5);
        assert.deepEqual(target.actionPolicy.actions.startManualRecording, acknowledged === 1 ?
          {mode: 'VERIFY', factor: 'CLASS_PASSWORD'} : {mode: 'ALLOW', factor: null});
        assert.equal(Object.hasOwn(policy.actions, 'startManualRecording'), false);
        assert.deepEqual(policy.actions, {stopScheduledMonitoring: {mode: 'VERIFY', factor: 'ALL'},
          exitBackground: {mode: 'DENY', factor: null}, switchClassroomMode: null, startManualStudy: null});
        assert.notEqual(target.actionPolicy.policyId, policy.policyId);
        await report(classroomBearer, {identity: current.identity, ...frontSession}, ++acknowledged,
          {schemaVersion: policy.schemaVersion, revision: policy.revision, policyId: policy.policyId, error: null});
        if (acknowledged === 2) {
          rearSession = await openDeviceSession(rearBearer);
          const rearPolicy = await call('/device/school-management', null, rearBearer, '0.16', 5);
          // A current Host with an unavailable policy store can report schema 1 and no installed policy.
          await report(rearBearer, {identity: rearPolicy.identity, ...rearSession}, 1,
            {schemaVersion: 1, revision: null, policyId: null, error: storageError});
          const scope = await call(scopePath, null, token, '0.16', 5);
          const byBinding = id => scope.screens.find(screen => screen.bindingId === id);
          const fault = byBinding(rear.id);
          assert.equal(fault.observationCurrent, true);
          assert.equal(fault.observed.schemaVersion, 1);
          assert.equal(fault.observed.error, storageError);
          assert.equal(byBinding(front.screenBindingId).observed.schemaVersion, 4);
          assert.equal(byBinding(front.screenBindingId).observed.error, null);
          faultEvidence = {reportedSchema: fault.observed.schemaVersion, serverPolicySchema: rearPolicy.actionPolicy.schemaVersion,
            error: fault.observed.error, currentInitially: fault.observationCurrent};
          return;
        }
      }
      await delay(300);
    }
  };
  const observed = applyObservation();
  const launched = runDesktop(['--live-classroom-action-policy', access, evidence]).finally(() => { done = true; });
  try {
    const outcomes = await Promise.allSettled([observed, launched]);
    for (const outcome of outcomes) if (outcome.status === 'rejected') throw outcome.reason;
    assert.equal(writes, 5);
    assert.equal(ownReads, 1);
    assert.equal(dropped, true);
    assert.equal(faultEvidence?.currentAfterRestart, false);
    assert.ok(activeFinal, 'desktop reached the original active-range result before archiving');
    const final = activeFinal;
    assert.equal(final.rooms[0].before.source, 'GRADE');
    assert.equal(final.rooms[0].before.usbCount, 0);
    assert.equal(final.rooms[0].before.actions.stopScheduledMonitoring.factor, 'ALL');
    assert.equal(final.rooms[0].before.actions.exitBackground.mode, 'DENY');
    assert.equal(final.rooms[0].before.actions.startManualRecording.mode, 'ALLOW');
    assert.deepEqual((await call(path + '/history-usb')).keys, []);
    assert.equal((await call(path)).term, null);
    assert.equal((await prisma.academicTerm.findUnique({where: {id: term.id}})).status, 'ARCHIVED');
    const [{data}] = await prisma.$queryRaw`SELECT data FROM "NpepClassroomActionState" WHERE "schoolId"=${school.id}`;
    assert.ok(data.keys.some(key => key.keyId === oldKey.keyId && key.termId === term.id && key.scopeId === grade.id && key.revoked));
    await writeFile(join(evidence, 'cross-check.json'), JSON.stringify({passed: true, writes, ownReads, dropped,
      originalGradeId: grade.id, originalTermId: term.id, originalClassId: room.id, front: front.screenBindingId, rear: rear.id,
      verifiedSource: final.rooms[0].before.source, lastKeyRevoked: final.rooms[0].before.usbCount === 0, physicalUsbWritten: false,
      historicalKeyId: oldKey.keyId, historicalKeyRevokedWithNoActiveTerm: true, faultEvidence}, null, 2));
    console.log('PASS real Windows Console, isolated KV/PostgreSQL, independent per-screen observations and historical USB revocation without an active term. No physical USB was written.');
  } finally {
    proxy.closeAllConnections();
    await new Promise(resolve => proxy.close(resolve));
  }
}
