import assert from 'node:assert/strict';
import {randomUUID, randomBytes} from 'node:crypto';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {join, resolve} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';

// Cross the actual KV / native-runtime boundary. Only the input device inventory
// is synthetic. No production account, capture, USB drive or Guard is involved.
export async function verifyNativeClassroomActions({prisma, origin, directory, fixture, runNative, runWebsiteDesktop}) {
  const {school, term, managerId, classes} = fixture;
  const inheritance = process.env.NP_CLASSROOM_ACTION_INHERITANCE === '1';
  const manualStudy = process.env.NP_MANUAL_STUDY_POLICY === '1';
  const manualRecording = process.env.NP_MANUAL_RECORDING_POLICY === '1';
  const classId = inheritance || manualStudy || manualRecording || runWebsiteDesktop ? classes[0] : classes[2];
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
      headers: {Authorization: 'Bearer ' + bearer, 'Content-Type': 'application/json', 'X-NPEP-Version': version, 'X-Request-Id': id,
        'X-NPEP-Classroom-Action-Policy': '2', 'X-NPEP-Classroom-Action-Inheritance': '1',
        'X-NPEP-Manual-Study-Policy': '1', 'X-NPEP-Manual-Recording-Policy': '1'},
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
  const paired = await prisma.npepDevice.findFirstOrThrow({where: {schoolId: school.id, screenBindingId: front.id, state: 'ACTIVE'}});
  assert.equal(paired.administrativeClassId, classId);
  const websiteDirectory = resolve(process.env.NP_CONSOLE_WEBSITE_POLICY_EVIDENCE || '.artifacts/website-policy/live');
  const websiteScope = `${root}/website-policy/scope/${term.id}/CLASS/${classId}`;
  let websiteEvidence = null;
  async function websiteDesktop(readReceipt) {
    const grade = await prisma.grade.findUniqueOrThrow({where: {id: room.gradeId}});
    const path = join(directory, 'website-access.json');
    await mkdir(websiteDirectory, {recursive: true});
    await writeFile(path, JSON.stringify({origin, schoolId: school.id, schoolCode: school.code, pin: fixture.pin,
      gradeCode: grade.code, classCode: room.code, readReceipt}));
    await runWebsiteDesktop(['--live-website-policy', path, websiteDirectory]);
  }
  if (runWebsiteDesktop) {
    await websiteDesktop(false);
    const desired = await call(websiteScope);
    assert.deepEqual(desired.rows[0].before.blockedHosts, ['douyin.com']);
    assert.equal(desired.rows[0].before.source, 'GRADE');
    assert.equal(desired.screens.find(screen => screen.bindingId === front.id).status, 'AWAITING_DEVICE');
  }
  const classSecret = randomBytes(24).toString('base64url');
  const classManagement = root + '/class-management/' + front.id;
  const old = await call(classManagement);
  await call(classManagement, {expectedRevision: old.revision, enabled: false, secret: classSecret});
  const records = [];
  const rule = mode => ({mode, factor: null});
  async function publishAndObserve(kind, actions, stopMode, exitMode,
      {switchMode = null, classActionOverrides, manualStudyMode = null, manualRecordingMode = null} = {}) {
    const scopeId = kind === 'GRADE' ? room.gradeId : classId;
    const change = {termId: term.id, scopeKind: kind, scopeId, operation: 'SET_POLICY', actions, usbKey: null, keyId: null,
      ...(classActionOverrides ? {classActionOverrides} : {})};
    const preview = await call(prefix + '/preview', {change});
    assert.equal(preview.canApply, true);
    const expected = preview.rooms.find(row => row.id === classId).after;
    const result = await call(prefix, {change, expectedVersion: preview.version});
    const own = await call(prefix + '/receipts/' + result.requestId);
    assert.equal(own.revision, result.revision);
    const observed = join(directory, 'seen-' + result.revision);
    const nativeRun = native('classroom-actions', {policyId: expected.policyId, stopMode, exitMode, observed,
      switchMode, manualStudyMode, manualRecordingMode,
      ...(['password', 'either', 'both'].includes(exitMode) || manualRecordingMode === 'password' ? {classSecret} : {})});
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
          if (runWebsiteDesktop) {
            const website = await call(websiteScope);
            const received = website.screens.find(screen => screen.bindingId === front.id);
            const unpaired = website.screens.find(screen => screen.bindingId === rear.id);
            if (received?.status !== 'RECEIVED_NOT_APPLIED') {
              await delay(350);
              continue;
            }
            assert.equal(received.deviceId, paired.id);
            assert.equal(received.bindingRevision, paired.bindingRevision);
            assert.equal(unpaired?.status, 'NOT_PAIRED');
            assert.deepEqual(website.rows[0].before.blockedHosts, ['douyin.com']);
            websiteEvidence = {classId, rule: website.rows[0].before, received, unpaired};
          }
          assert.equal(frontRow.observed.schemaVersion, 5);
          assert.deepEqual(current.rooms.find(row => row.id === classId).before.actions, expected.actions);
          assert.equal(frontRow.deviceId, paired.id);
          assert.equal(frontRow.bindingRevision, paired.bindingRevision);
          records.push({revision: result.revision, source: expected.source, policyId: expected.policyId,
            classId, front: frontRow.bindingId, frontDeviceId: frontRow.deviceId, frontReceiptVersion: frontRow.observed.schemaVersion,
            rear: rearRow.bindingId, rearReceipt: rearRow.observed, stopMode, exitMode, switchMode, manualStudyMode, manualRecordingMode,
            origins: expected.actionOrigins, requiredSchema: expected.minimumReceiptSchema});
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
  async function registerFixtureKey() {
    // A random synthetic hardware identity cannot match a drive on the workstation.
    const usbKey = {keyId: randomUUID(), label: '验收专用模拟盘', mediaHash: randomBytes(32).toString('hex'), secretHash: randomBytes(32).toString('hex')};
    const change = {termId: term.id, scopeKind: 'GRADE', scopeId: room.gradeId, operation: 'REGISTER_USB', actions: null, usbKey, keyId: null};
    const preview = await call(prefix + '/preview', {change});
    assert.equal(preview.canApply, true);
    await call(prefix, {change, expectedVersion: preview.version});
  }
  const gradeActions = {stopScheduledMonitoring: rule('ALLOW'), exitBackground: rule('DENY')};
  let neighborEvidence = null;
  if (runWebsiteDesktop) {
    await publishAndObserve('GRADE', gradeActions, 'allow', 'deny');
    assert.ok(websiteEvidence);
    await websiteDesktop(true);
    await writeFile(join(websiteDirectory, 'website-policy-live.json'), JSON.stringify({passed: true, ...websiteEvidence}, null, 2));
    console.log('PASS actual Console publication and receipt display with the bound native Host; Edge application was not claimed.');
    return;
  } else if (manualRecording) {
    const gradeAllow = {...gradeActions, switchClassroomMode: null, startManualStudy: null, startManualRecording: rule('ALLOW')};
    await publishAndObserve('GRADE', gradeAllow, 'allow', 'deny', {manualRecordingMode: 'allow'});
    const classActionOverrides = {stopScheduledMonitoring: null, exitBackground: null, switchClassroomMode: null,
      startManualStudy: null, startManualRecording: {rule: {mode: 'VERIFY', factor: 'CLASS_PASSWORD'}}};
    await publishAndObserve('CLASS', null, 'allow', 'deny', {classActionOverrides, manualRecordingMode: 'password'});
    assert.equal(records[1].origins.startManualRecording, 'CLASS');
    assert.equal(records[1].requiredSchema, 5);
    const neighbor = await prisma.workspace.findFirstOrThrow({where: {id: {in: classes.slice(1)}, gradeId: room.gradeId}, orderBy: {code: 'asc'}});
    const other = await call(prefix + '/scope/' + term.id + '/CLASS/' + neighbor.id);
    assert.equal(other.savedClassActionOverrides, null);
    assert.deepEqual(other.rooms[0].before.actions, gradeAllow);
    assert.equal(other.rooms[0].before.actionOrigins.startManualRecording, 'GRADE');
    neighborEvidence = {classId: neighbor.id, code: neighbor.code, policy: other.rooms[0].before};
  } else if (manualStudy) {
    await registerFixtureKey();
    const onlyManual = {stopScheduledMonitoring: null, exitBackground: null, switchClassroomMode: null,
      startManualStudy: {rule: {mode: 'VERIFY', factor: 'USB'}}};
    await publishAndObserve('CLASS', null, 'default', 'default', {classActionOverrides: onlyManual, manualStudyMode: 'usb'});
    assert.equal(records[0].requiredSchema, 4);
    assert.deepEqual(records[0].origins, {
      stopScheduledMonitoring: 'LEGACY', exitBackground: 'LEGACY', switchClassroomMode: 'LEGACY',
      startManualStudy: 'CLASS', startManualRecording: 'LEGACY'
    });
  } else if (inheritance) {
    await registerFixtureKey();
    const follow = {stopScheduledMonitoring: null, exitBackground: null, switchClassroomMode: null};
    await publishAndObserve('CLASS', null, 'default', 'default',
      {switchMode: 'usb', classActionOverrides: {...follow, switchClassroomMode: {rule: {mode: 'VERIFY', factor: 'USB'}}}});
    assert.equal(records[0].requiredSchema, 3);
    await publishAndObserve('GRADE', gradeActions, 'allow', 'deny', {switchMode: 'usb'});
    const neighbor = await prisma.workspace.findFirstOrThrow({where: {id: {in: classes.slice(1)}, gradeId: room.gradeId}, orderBy: {code: 'asc'}});
    const other = await call(prefix + '/scope/' + term.id + '/CLASS/' + neighbor.id);
    assert.equal(other.savedClassActionOverrides, null);
    assert.deepEqual(other.rooms[0].before.actions, {...gradeActions, switchClassroomMode: null, startManualStudy: null, startManualRecording: null});
    neighborEvidence = {classId: neighbor.id, code: neighbor.code, policy: other.rooms[0].before};
    await publishAndObserve('CLASS', null, 'allow', 'deny', {classActionOverrides: follow});
    assert.equal(records[1].origins.switchClassroomMode, 'CLASS');
    assert.equal(records[2].source, 'GRADE');
  } else {
    await publishAndObserve('GRADE', gradeActions, 'allow', 'deny');
    await publishAndObserve('CLASS', {stopScheduledMonitoring: rule('DENY'),
      exitBackground: {mode: 'VERIFY', factor: 'CLASS_PASSWORD'}}, 'deny', 'password');
    await publishAndObserve('CLASS', null, 'allow', 'deny');
    assert.equal(records[0].policyId, records[2].policyId);
    assert.notEqual(records[0].policyId, records[1].policyId);
    await registerFixtureKey();
    await publishAndObserve('GRADE', {stopScheduledMonitoring: rule('DENY'),
      exitBackground: {mode: 'VERIFY', factor: 'ANY'}}, 'deny', 'either');
    await publishAndObserve('CLASS', {stopScheduledMonitoring: rule('DENY'),
      exitBackground: {mode: 'VERIFY', factor: 'ALL'}}, 'deny', 'both');
    await publishAndObserve('CLASS', {stopScheduledMonitoring: rule('DENY'),
      exitBackground: {mode: 'VERIFY', factor: 'ALL'}, switchClassroomMode: rule('DENY')}, 'deny', 'both', {switchMode: 'deny'});
  }
  const evidence = resolve(process.env.NP_CLASSROOM_ACTION_NATIVE_EVIDENCE || '.artifacts/classroom-action-native');
  await mkdir(evidence, {recursive: true});
  await writeFile(join(evidence, 'classroom-action-native.json'), JSON.stringify({passed: true, neighbor: neighborEvidence, records}, null, 2));
  console.log(manualRecording ?
    'PASS current v5 receipts from the exact paired Host; grade permits manual recording, first class requires its password, and second class follows the grade.' :
    manualStudy ? 'PASS current v5 receipt from the exact paired Host; manual study alone requires a registered USB.' : inheritance ?
    'PASS bound native Host applied class-only USB verification, then inherited grade exit denial, and restored full grade inheritance; neighbor stayed on grade policy.' :
    'PASS previous classroom rules, grade inheritance restoration, ANY/ALL, and on-site mode denial on the bound native Host.');
}
