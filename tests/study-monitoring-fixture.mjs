import assert from 'node:assert/strict';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {nextStudyMonday} from './class-study-fixture.mjs';
import {prepareLegacyStudyWeb} from './school-study-fixture.mjs';

export async function verifyStudyMonitoring({prisma, origin, fixture, fromKv, kvRequire, directory, evidence, web, runDesktop, runNative}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, managerId, pin, identity} = fixture;
  const {defaultStudySettings} = await fromKv('domain/npep/studySettings.js');
  const {secretHash} = await fromKv('domain/npep/wire.js');
  const date = nextStudyMonday();
  const weekday = new Date(date + 'T00:00:00Z').getUTCDay() || 7;
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const rooms = [];
  for (const number of [7, 8, 9]) rooms.push(await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id,
    isActive: true, type: 'ADMIN_CLASS', name: '高二（' + number + '）班', code: 'MON-' + number}}));
  const [seven, eight, nine] = rooms;
  const settings = defaultStudySettings();
  settings.schedule = {...settings.schedule, effectiveFrom: date, rules: [{days: [weekday], start: '19:00', end: '20:00'}]};
  settings.examSchedule = {...settings.examSchedule, effectiveFrom: date, rules: [{days: [weekday], start: '13:30', end: '14:10'}]};
  settings.homework = {...settings.homework, sourceMode: 'SelectedClasses', sourceClassIds: [eight.id]};
  settings.noise = {...settings.noise, estimated: null, mediumLineDbfs: -40, redLineDbfs: -30};
  settings.photos.enabled = true;
  async function addPolicy(type, target, mode) {
    await prisma.npepStudySetting.create({data: {schoolId: school.id, termId: term.id, targetType: type, targetId: target,
      revision: 1, policy: {mode, settings: mode === 'Override' ? settings : null}, updatedBy: managerId}});
  }
  await addPolicy('GRADE', grade.id, 'Override');
  await addPolicy('CLASS', eight.id, 'Override');
  await addPolicy('CLASS', nine.id, 'Disabled');
  async function device(name) {
    const binding = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: seven.id,
      tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId, name}});
    const secret = randomBytes(32).toString('base64url');
    const unit = await prisma.npepDevice.create({data: {...identity, id: randomUUID(), installationId: randomUUID(),
      credentialId: randomUUID(), secretHash: secretHash(secret), schoolId: school.id, administrativeClassId: seven.id,
      screenBindingId: binding.id, bindingRevision: binding.npepBindingRevision, deviceName: name + '终端',
      credentialExpiresAt: new Date(Date.now() + 86400000)}});
    return {binding, unit, bearer: 'npep1.' + unit.credentialId + '.' + secret};
  }
  const front = await device('前屏'), rear = await device('后屏');
  async function login(username) {
    const response = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username, password: pin})});
    assert.equal(response.status, 200);
    return (await response.json()).data;
  }
  const [admin, owner, viewer, alternate] = await Promise.all(['admin','owner','viewer','admin2'].map(login));
  const teacher = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer'}});
  await prisma.administrativeClassLeadership.create({data: {administrativeClassId: seven.id, accountId: teacher.id}});
  await prisma.schoolMember.create({data: {accountId: teacher.id, schoolId: other.id, role: 'ADMIN'}});
  await prisma.schoolMember.create({data: {accountId: managerId, schoolId: other.id, role: 'ADMIN'}});
  const scope = {schoolId: school.id, termId: term.id, classId: seven.id};
  const resource = '/api/v2/npep/schools/' + school.id;
  const query = '?termId=' + term.id + '&classId=' + seven.id;
  const studyPath = resource + '/study-settings';
  async function api(path, {token = admin.access_token, method = 'GET', version = '0.15', body, status = 200} = {}) {
    const id = randomUUID();
    const response = await fetch(origin + path, {method, headers: {Authorization: 'Bearer ' + token, 'X-NPEP-Version': version,
      'X-Request-Id': id, 'Content-Type': 'application/json'}, body: body === undefined ? undefined : JSON.stringify({...body, requestId: id})});
    if (response.headers.get('content-type')?.startsWith('image/jpeg')) {
      assert.equal(response.status, status, path);
      return {bytes: Buffer.from(await response.arrayBuffer()), headers: response.headers};
    }
    const value = await response.json();
    assert.equal(response.status, status, path + ' ' + JSON.stringify(value.error));
    return value.data ?? value.error;
  }
  const image = join(directory, 'synthetic-photo.jpg');
  const pixels = Buffer.alloc(320 * 180 * 4);
  for (let y = 0; y < 180; y++) for (let x = 0; x < 320; x++) {
    const i = (y * 320 + x) * 4, seat = x % 43 > 6 && y % 35 > 6;
    pixels[i] = seat ? 28 : 216; pixels[i + 1] = seat ? 95 : 234; pixels[i + 2] = seat ? 160 : 248; pixels[i + 3] = 255;
  }
  await writeFile(image, kvRequire('jpeg-js').encode({width: 320, height: 180, data: pixels}, 75).data);
  const native = join(directory, 'native-monitoring.json'), nativeOutput = join(evidence, 'native');
  await writeFile(native, JSON.stringify({origin, deployment: identity, isolated: true, bearer: front.bearer, date, image, classId: seven.id}), {mode: 0o600});
  await runNative([nativeOutput, '--live-school-study-photo', native]);
  const capture = JSON.parse(await readFile(join(nativeOutput, 'photo-evidence.json'), 'utf8'));
  const observation = await api(resource + '/devices/' + front.unit.id + '/study-settings', {version: '0.10'});
  assert.ok(observation.configurationApplied && observation.captureVerified && observation.execution.signalFresh);
  const rearObservation = await api(resource + '/devices/' + rear.unit.id + '/study-settings', {version: '0.10'});
  assert.ok(!rearObservation.configurationApplied && !rearObservation.captureVerified && !rearObservation.observation);
  const photoPath = resource + '/study-photos/' + capture.photoId;
  // A finished report fixture tied to the actual capture IDs; no wait for a full simulated lesson.
  const receivedAt = new Date().toISOString();
  const report = {sessionId: capture.sessionId, startedAt: capture.capturedAt, endedAt: capture.capturedAt,
    receivedAt, outcome: 'Completed', ownership: scope, study: {mode: 'Daily', clockSource: 'ClassIsland', window: capture.window},
    summary: {coverage: .8, energyMeanDbfs: -20, peakDbfs: -18, clippedPercent: 0}};
  for (const [item, row] of [[front, report], [rear, {...report, sessionId: capture.sessionId}]]) {
    await prisma.npepNoiseDevice.create({data: {deviceId: item.unit.id, data: {reports: [row]}}});
  }
  const groups = await api(resource + '/study-sessions' + query, {version: '0.17'});
  const detail = id => api(resource + '/study-sessions' + query + '&groupId=' +
    groups.items.find(row => row.deviceId === id).groupId, {version: '0.17'}).then(x => x.items[0]);
  assert.deepEqual((await detail(front.unit.id)).photos.map(row => row.eventId), [capture.photoId]);
  assert.deepEqual((await detail(front.unit.id)).events.map(row => row.eventId), [capture.exitId]);
  assert.deepEqual((await detail(rear.unit.id)).photos, []);
  assert.deepEqual((await detail(rear.unit.id)).events, []);
  assert.deepEqual((await api(resource + '/study-photos' + query + '&date=' + date)).items.map(row => row.eventId), [capture.photoId]);
  assert.deepEqual((await api(resource + '/study-photos' + query + '&date=2020-01-01')).items, []);
  assert.equal((await api(resource + '/study-exit-events' + query + '&date=' + date)).items[0].photoState, 'Unavailable');
  await api(photoPath + '/image?termId=' + term.id + '&classId=' + eight.id, {status: 404});
  for (const [token, status] of [[owner.access_token, 200], [admin.access_token, 200], [viewer.access_token, 403]]) {
    await api(resource + '/study-photos' + query, {token, status});
    await api(resource + '/study-exit-events' + query, {token, status});
    await api(resource + '/study-sessions' + query + '&groupId=' + groups.items[0].groupId, {token, status, version: '0.17'});
    await api(photoPath + '/image' + query, {token, status});
    if (status === 403) await api(photoPath + '/delete' + query, {token, status, method: 'POST', body: {}});
  }
  const download = await api(photoPath + '/image' + query + '&download=1', {token: owner.access_token});
  assert.ok(download.bytes.length > 100 && download.headers.get('content-disposition').startsWith('attachment;'));
  assert.equal(await prisma.npepAudit.count({where: {objectId: capture.photoId, action: 'STUDY_PHOTO_DOWNLOAD'}}), 1);
  await api(resource + '/study-photos' + query, {token: front.bearer, status: 401});
  await api(photoPath + '/image' + query, {token: 'monitoring-credential-fixture', status: 401});
  const checks = ['real-host-stored', 'report-by-device-and-session', 'date-filter', 'owner-admin-allowed',
    'head-teacher-and-other-school-admin-denied', 'scoped-image', 'explicit-download-audit'];
  const legacy = prepareLegacyStudyWeb({web, directory, evidence, origin, token: alternate, school, term, room: eight});
  legacy.catch(() => {});
  let lost = false, receipts = 0, holdPhoto = false, stoppedImage;
  let savedExpiry;
  const existing = await prisma.npepStudyPhoto.findUniqueOrThrow({where: {eventId: capture.photoId}});
  const currentPolicy = () => prisma.npepStudySetting.findFirstOrThrow({where: {schoolId: school.id, targetId: grade.id}});
  const server = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__monitor/')) {
      const step = req.url.slice('/__monitor/'.length);
      try {
        if (step === 'web-change') await (await legacy).edit();
        else if (step === 'stale') {
          assert.equal((await currentPolicy()).revision, 1);
          assert.equal((await prisma.npepStudySetting.findFirstOrThrow({where: {targetId: eight.id}})).revision, 2);
        } else if (step === 'applied') {
          const row = await currentPolicy();
          assert.deepEqual(row.policy.settings, {...settings, photos: {...settings.photos, retentionDays: 14}});
          assert.equal(row.revision, 2);
          assert.ok(lost && receipts === 1);
          const stored = await api(resource + '/study-photos' + query);
          assert.equal(Date.parse(stored.items[0].expiresAt) - Date.parse(stored.items[0].capturedAt), 14 * 86400000);
        } else if (step === 'revoke') {
          await prisma.schoolMember.update({where: {schoolId_accountId: {schoolId: school.id, accountId: managerId}}, data: {role: 'VIEWER'}});
          await api(photoPath + '/image' + query, {status: 403});
        } else if (step === 'restore') {
          await prisma.schoolMember.update({where: {schoolId_accountId: {schoolId: school.id, accountId: managerId}}, data: {role: 'ADMIN'}});
        } else if (step === 'hold-photo') {
          holdPhoto = true;
        } else if (step === 'photo-awaiting-release') {
          for (let retry = 0; retry < 50 && !stoppedImage; retry++) await new Promise(resolve => setTimeout(resolve, 100));
          assert.ok(stoppedImage);
        } else if (step === 'release-photo') {
          assert.ok(stoppedImage);
          stoppedImage();
        } else if (step === 'extend') {
          const shortened = await prisma.npepStudyPhoto.findUniqueOrThrow({where: {eventId: capture.photoId}});
          const oldExit = await prisma.npepStudyExitEvent.findUniqueOrThrow({where: {eventId: capture.exitId}});
          const change = {termId: term.id, targetType: 'GRADE', targetId: grade.id, expectedRevision: 2,
            policy: {mode: 'Override', settings: {...settings, photos: {...settings.photos,
              retentionDays: 45, extendedRetentionAcknowledged: true}}}};
          const next = await api(studyPath + '/preview', {method: 'POST', version: '0.10', body: {...change, previewDate: date}});
          await api(studyPath, {method: 'POST', version: '0.10', body: {...change, expectedPreviewFingerprint: next.previewFingerprint}});
          assert.deepEqual((await prisma.npepStudyPhoto.findUniqueOrThrow({where: {eventId: capture.photoId}})).expiresAt, shortened.expiresAt);
          assert.deepEqual((await prisma.npepStudyExitEvent.findUniqueOrThrow({where: {eventId: capture.exitId}})).expiresAt, oldExit.expiresAt);
        } else if (step === 'photo-retained-after-selection-change') {
          assert.equal((await api(photoPath + '/image' + query)).bytes.length, download.bytes.length);
          assert.equal(await prisma.npepAudit.count({where: {objectId: capture.photoId, action: 'STUDY_PHOTO_DELETE'}}), 0);
        } else if (step === 'expire-image') {
          savedExpiry = (await prisma.npepStudyPhoto.findUniqueOrThrow({where: {eventId: capture.photoId}})).expiresAt;
          await prisma.npepStudyPhoto.update({where: {eventId: capture.photoId}, data: {expiresAt: new Date(Date.now() - 1000)}});
        } else if (step === 'unexpire-fixture-image') {
          assert.ok(savedExpiry);
          await prisma.npepStudyPhoto.update({where: {eventId: capture.photoId}, data: {expiresAt: savedExpiry}});
        } else throw new Error('Unknown monitoring checkpoint');
        checks.push(step); res.writeHead(204).end();
      } catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'GET' && req.url.startsWith(studyPath + '/requests/')) receipts++;
    const endpoint = new URL(req.url, origin);
    const forward = request(endpoint, {method: req.method, headers: {...req.headers, host: endpoint.host}}, response => {
      if (holdPhoto && req.method === 'GET' && endpoint.pathname === photoPath + '/image' && response.statusCode === 200) {
        holdPhoto = false;
        stoppedImage = () => {
          if (res.destroyed) response.resume();
          else { res.writeHead(response.statusCode, response.headers); response.pipe(res); }
        };
        return;
      }
      if (!lost && req.method === 'POST' && req.url === studyPath && response.statusCode === 200) {
        lost = true; response.resume(); response.on('end', () => res.destroy()); return;
      }
      res.writeHead(response.statusCode, response.headers); response.pipe(res);
    });
    forward.on('error', () => res.destroy());
    req.pipe(forward);
  });
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const desktop = join(directory, 'desktop-monitoring.json');
    await writeFile(desktop, JSON.stringify({origin: 'http://127.0.0.1:' + server.address().port, schoolCode: school.code,
      schoolId: school.id, otherSchoolId: other.id, password: pin, gradeId: grade.id, sevenId: seven.id, eightId: eight.id, nineId: nine.id,
      frontId: front.unit.id, photoId: capture.photoId, exitId: capture.exitId, date, isolated: true}), {mode: 0o600});
    const legacyFile = await (await legacy).photos(seven, capture.photoId);
    assert.equal(createHash('sha256').update(await readFile(legacyFile)).digest('hex'), capture.digest);
    checks.push('edge-photo-preview-and-explicit-download');
    await runDesktop(['--live-study-monitoring', desktop, join(evidence, 'console')]);
    assert.equal(await prisma.npepAudit.count({where: {objectId: capture.photoId, action: 'STUDY_PHOTO_VIEW'}}) >= 3, true);
    const retry = await fetch(origin + '/api/v2/npep/device/study-settings/photos/upload', {method: 'POST', headers: {
      Authorization: 'Bearer ' + front.bearer, 'X-NPEP-Version': '0.15', 'X-Request-Id': randomUUID(),
      'Content-Type': 'image/jpeg', 'X-NPEP-Photo': Buffer.from(JSON.stringify(existing.metadata)).toString('base64url')
    }, body: await readFile(image)});
    assert.equal(retry.status, 410);
    assert.equal((await retry.json()).error.code, 'PHOTO_DELETED');
    await api(photoPath + '/image' + query, {status: 404});
    assert.equal(await prisma.npepAudit.count({where: {objectId: capture.photoId, action: 'STUDY_PHOTO_DELETE'}}), 1);
    checks.push('explicit-deletion-audit-and-device-retry-cannot-restore');
    await writeFile(join(evidence, 'cross-end-result.json'), JSON.stringify({passed: true, checks, scope, capture,
      screens: {front: front.binding.id, rear: rear.binding.id},
      receipt: {front: observation.receivedAt, frontApplied: observation.configurationApplied, frontSignal: observation.captureVerified,
        rearApplied: rearObservation.configurationApplied, rearSignal: rearObservation.captureVerified},
      reportSeed: 'Finished fragment with actual Host session and synthetic report statistics'}, null, 2));
  } finally {
    stoppedImage?.();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    try { await (await legacy).close(); } catch {}
  }
}
