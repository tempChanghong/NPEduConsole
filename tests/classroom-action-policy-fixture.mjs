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
  async function call(path, body, bearer = token, version = '0.16') {
    const requestId = randomUUID();
    const response = await fetch(origin + '/api/v2/npep' + path, {method: body ? 'POST' : 'GET', signal: AbortSignal.timeout(10000),
      headers: {Authorization: 'Bearer ' + bearer, 'Content-Type': 'application/json', 'X-NPEP-Version': version, 'X-Request-Id': requestId},
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
  const runId = randomUUID();
  const session = await call('/device/sessions', {...identity, runId, expectedStatusEpoch: 0}, classroomBearer, '0.1');

  let dropped = false, writes = 0, ownReads = 0;
  const proxy = createServer((request, response) => {
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
    pin, gradeCode: grade.code, classCode: room.code, frontId: front.screenBindingId, rearId: rear.id}));
  let done = false;
  const applyObservation = async () => {
    while (!done) {
      const current = await call('/device/school-management', null, classroomBearer);
      const policy = current.actionPolicy;
      if (policy.source === 'GRADE' && policy.revision === 2) {
        await call('/device/school-management/receipt', {context: {identity: current.identity, runId, sessionId: session.sessionId,
          statusEpoch: session.statusEpoch}, sequence: 1, microphones: [], cameras: [], unlocked: false, appliedRevision: 0,
          credentialRevision: 1, selectedMicrophoneId: null, selectedCameraId: null, error: null,
          actionPolicyReceipt: {schemaVersion: 1, revision: policy.revision, policyId: policy.policyId, error: null}}, classroomBearer);
        return;
      }
      await delay(300);
    }
  };
  const observed = applyObservation();
  const launched = runDesktop(['--live-classroom-action-policy', access, evidence]).finally(() => { done = true; });
  try {
    const outcomes = await Promise.allSettled([observed, launched]);
    for (const outcome of outcomes) if (outcome.status === 'rejected') throw outcome.reason;
    assert.equal(writes, 4);
    assert.equal(ownReads, 1);
    assert.equal(dropped, true);
    const final = await call(path + `/scope/${term.id}/CLASS/${room.id}`);
    assert.equal(final.rooms[0].before.source, 'GRADE');
    assert.equal(final.rooms[0].before.usbCount, 0);
    assert.equal(final.rooms[0].before.actions.exitBackground.factor, 'ALL');
    await writeFile(join(evidence, 'cross-check.json'), JSON.stringify({passed: true, writes, ownReads, dropped,
      originalGradeId: grade.id, originalTermId: term.id, originalClassId: room.id, front: front.screenBindingId, rear: rear.id,
      verifiedSource: final.rooms[0].before.source, lastKeyRevoked: final.rooms[0].before.usbCount === 0, physicalUsbWritten: false}, null, 2));
    console.log('PASS real Windows Console, isolated KV/PostgreSQL, original receipt recovery and independent screen observations. No physical USB was written.');
  } finally {
    proxy.closeAllConnections();
    await new Promise(resolve => proxy.close(resolve));
  }
}
