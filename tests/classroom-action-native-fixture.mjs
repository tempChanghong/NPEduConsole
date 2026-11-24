import assert from 'node:assert/strict';
import {randomUUID, randomBytes} from 'node:crypto';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {join, resolve} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';

// Cross the actual KV / native-runtime boundary. Only the input device inventory
// is synthetic. No production account, capture, USB drive or Guard is involved.
export async function verifyNativeClassroomActions({prisma, origin, directory, fixture, runNative}) {
  const {school, term, managerId, classes: [, , classId]} = fixture;
  const room = await prisma.workspace.findUniqueOrThrow({where: {id: classId}});
  const makeScreen = name => prisma.classroomScreenBinding.create({data: {name, schoolId: school.id,
    administrativeClassId: classId, tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId}});
  const front = await makeScreen('真实策略前屏');
  const rear = await makeScreen('同班待接入后屏');
  const signIn = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({schoolCode: school.code, username: 'admin', password: fixture.pin})});
  assert.equal(signIn.status, 200);
  const bearer = (await signIn.json()).data.access_token;
  const root = '/api/v2/npep/schools/' + school.id;
  const prefix = root + '/classroom-action-policy';
  async function call(path, value, version = '0.16') {
    const id = randomUUID();
    const response = await fetch(origin + path, {method: value ? 'POST' : 'GET', signal: AbortSignal.timeout(15000),
      headers: {Authorization: 'Bearer ' + bearer, 'Content-Type': 'application/json', 'X-NPEP-Version': version, 'X-Request-Id': id},
      ...(value ? {body: JSON.stringify({requestId: id, ...value})} : {})});
    const envelope = await response.json();
    assert.equal(response.status, 200, path + ' ' + (envelope.error?.code ?? ''));
    return envelope.data;
  }
  const output = join(directory, 'pair-result.json'), file = join(directory, 'native.json');
  async function native(phase, extra = {}) {
    await writeFile(file, JSON.stringify({kind: 'DEVICE_CONNECTIONS_DISPOSABLE_DATABASE', origin, schoolId: school.id,
      classId, bindingId: front.id, deviceName: '验收教室前屏', classApply: false, output, ...extra}));
    await runNative(['device-connections', phase, file, join(directory, 'native-profile')]);
  }
  await native('begin');
  const {pairingId} = JSON.parse(await readFile(output, 'utf8'));
  await call(root + '/pairings/' + pairingId + '/approve', {screenBindingId: front.id, capabilities: ['device.status']}, '0.1');
  await native('confirm');
  const classSecret = randomBytes(24).toString('base64url');
  const classManagement = root + '/class-management/' + front.id;
  const old = await call(classManagement);
  await call(classManagement, {expectedRevision: old.revision, enabled: false, secret: classSecret});
  const records = [];
  const rule = mode => ({mode, factor: null});
  async function publishAndObserve(kind, actions, stopMode, exitMode) {
    const scopeId = kind === 'GRADE' ? room.gradeId : classId;
    const change = {termId: term.id, scopeKind: kind, scopeId, operation: 'SET_POLICY', actions, usbKey: null, keyId: null};
    const preview = await call(prefix + '/preview', {change});
    assert.equal(preview.canApply, true);
    const expected = preview.rooms.find(row => row.id === classId).after;
    const result = await call(prefix, {change, expectedVersion: preview.version});
    const own = await call(prefix + '/receipts/' + result.requestId);
    assert.equal(own.revision, result.revision);
    const observed = join(directory, 'seen-' + result.revision);
    const nativeRun = native('classroom-actions', {policyId: expected.policyId, stopMode, exitMode, observed,
      ...(exitMode === 'password' ? {classSecret} : {})});
    const scopePath = prefix + '/scope/' + term.id + '/CLASS/' + classId;
    const check = async () => {
      const until = Date.now() + 65000;
      while (Date.now() < until) {
        const current = await call(scopePath);
        const frontRow = current.screens.find(screen => screen.bindingId === front.id);
        const rearRow = current.screens.find(screen => screen.bindingId === rear.id);
        assert.ok(frontRow && rearRow);
        assert.equal(rearRow.observed, null);
        if (frontRow.observationCurrent && frontRow.observed?.policyId === expected.policyId &&
            frontRow.observed?.revision === result.revision && frontRow.observed.error === null) {
          records.push({revision: result.revision, source: expected.source, policyId: expected.policyId,
            front: frontRow.bindingId, rear: rearRow.bindingId, rearReceipt: rearRow.observed, stopMode, exitMode});
          await writeFile(observed, 'verified');
          return;
        }
        await delay(350);
      }
      throw new Error('Native receipt missing from current classroom scope.');
    };
    // Always wait for the child before cleaning the disposable database and pairing.
    const results = await Promise.allSettled([nativeRun, check()]);
    for (const part of results) if (part.status === 'rejected') throw part.reason;
  }
  await publishAndObserve('GRADE', {stopScheduledMonitoring: rule('ALLOW'), exitBackground: rule('DENY')}, 'allow', 'deny');
  await publishAndObserve('CLASS', {stopScheduledMonitoring: rule('DENY'),
    exitBackground: {mode: 'VERIFY', factor: 'CLASS_PASSWORD'}}, 'deny', 'password');
  await publishAndObserve('CLASS', null, 'allow', 'deny');
  assert.equal(records[0].policyId, records[2].policyId);
  assert.notEqual(records[0].policyId, records[1].policyId);
  const evidence = resolve(process.env.NP_CLASSROOM_ACTION_NATIVE_EVIDENCE || '.artifacts/classroom-action-native');
  await mkdir(evidence, {recursive: true});
  await writeFile(join(evidence, 'classroom-action-native.json'), JSON.stringify({passed: true, records}, null, 2));
  console.log('PASS grade, classroom override and restored inheritance on the real paired native runtime; separate same-class screen stays pending.');
}
