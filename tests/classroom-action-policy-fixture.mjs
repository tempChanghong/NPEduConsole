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
  await prisma.npepDevice.create({data: {...identity, id: randomUUID(), installationId: randomUUID(), credentialId: randomUUID(),
    schoolId: school.id, administrativeClassId: room.id, screenBindingId: rear.id, bindingRevision: rear.npepBindingRevision,
    deviceName: '同班后屏一体机', secretHash: randomBytes(32).toString('hex'), credentialExpiresAt: new Date(Date.now() + 3600000)}});
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
        ...(policySchema === 4 ? {'X-NPEP-Manual-Study-Policy': '1'} : {})},
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
  const runId = randomUUID();
  const session = await call('/device/sessions', {...identity, runId, expectedStatusEpoch: 0}, classroomBearer, '0.1');

  let dropped = false, writes = 0, ownReads = 0, activeFinal = null;
  const proxy = createServer((request, response) => {
    if (request.method === 'POST' && request.url === '/__classroom-policy-fixture/archive') {
      request.resume();
      (async () => {
        assert.equal(activeFinal, null);
        activeFinal = await call(path + `/scope/${term.id}/CLASS/${room.id}`);
        const oldRegistration = {...change, usbKey: oldKey};
        const review = await call(path + '/preview', {change: oldRegistration});
        await call(path, {change: oldRegistration, expectedVersion: review.version});
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
      const current = await call('/device/school-management', null, classroomBearer, '0.16', 3);
      const policy = current.actionPolicy;
      if (policy.revision === acknowledged + 2) {
        assert.equal(policy.source, 'GRADE');
        if (acknowledged === 1) {
          const target = await call('/device/school-management', null, classroomBearer, '0.16', 4);
          assert.deepEqual(target.actionPolicy.actions.startManualStudy, {mode: 'VERIFY', factor: 'USB'});
          assert.equal(target.actionPolicy.actions.switchClassroomMode, null);
          assert.equal(Object.hasOwn(policy.actions, 'startManualStudy'), false);
          assert.equal(target.actionPolicy.actions.exitBackground.mode, 'DENY');
          assert.notEqual(target.actionPolicy.policyId, policy.policyId);
        }
        await call('/device/school-management/receipt', {context: {identity: current.identity, runId, sessionId: session.sessionId,
          statusEpoch: session.statusEpoch}, sequence: ++acknowledged, microphones: [], cameras: [], unlocked: false, appliedRevision: 0,
          credentialRevision: 1, selectedMicrophoneId: null, selectedCameraId: null, error: null,
          actionPolicyReceipt: {schemaVersion: policy.schemaVersion, revision: policy.revision, policyId: policy.policyId, error: null}}, classroomBearer);
        if (acknowledged === 2) return;
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
    assert.ok(activeFinal, 'desktop reached the original active-range result before archiving');
    const final = activeFinal;
    assert.equal(final.rooms[0].before.source, 'GRADE');
    assert.equal(final.rooms[0].before.usbCount, 0);
    assert.equal(final.rooms[0].before.actions.stopScheduledMonitoring.factor, 'ALL');
    assert.equal(final.rooms[0].before.actions.exitBackground.mode, 'DENY');
    assert.deepEqual((await call(path + '/history-usb')).keys, []);
    assert.equal((await call(path)).term, null);
    assert.equal((await prisma.academicTerm.findUnique({where: {id: term.id}})).status, 'ARCHIVED');
    const [{data}] = await prisma.$queryRaw`SELECT data FROM "NpepClassroomActionState" WHERE "schoolId"=${school.id}`;
    assert.ok(data.keys.some(key => key.keyId === oldKey.keyId && key.termId === term.id && key.scopeId === grade.id && key.revoked));
    await writeFile(join(evidence, 'cross-check.json'), JSON.stringify({passed: true, writes, ownReads, dropped,
      originalGradeId: grade.id, originalTermId: term.id, originalClassId: room.id, front: front.screenBindingId, rear: rear.id,
      verifiedSource: final.rooms[0].before.source, lastKeyRevoked: final.rooms[0].before.usbCount === 0, physicalUsbWritten: false,
      historicalKeyId: oldKey.keyId, historicalKeyRevokedWithNoActiveTerm: true}, null, 2));
    console.log('PASS real Windows Console, isolated KV/PostgreSQL, independent per-screen observations and historical USB revocation without an active term. No physical USB was written.');
  } finally {
    proxy.closeAllConnections();
    await new Promise(resolve => proxy.close(resolve));
  }
}
