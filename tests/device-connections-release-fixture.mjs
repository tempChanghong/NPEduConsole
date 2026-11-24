import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {access, mkdir, mkdtemp, readFile, rm, writeFile} from 'node:fs/promises';
import {basename, dirname, join, resolve} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {fileURLToPath} from 'node:url';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyReleasedDeviceConnections(options) {
  const evidence = resolve(options.evidence);
  await mkdir(evidence, {recursive: true});
  const nativeRoot = await mkdtemp(join(evidence, 'device-connection-profiles-'));
  try { await verify({...options, evidence, nativeRoot}); }
  finally {
    if (dirname(resolve(nativeRoot)) === evidence && basename(nativeRoot).startsWith('device-connection-profiles-'))
      await rm(nativeRoot, {recursive: true, force: true});
  }
}

async function verify({prisma, fixture, origin, directory, evidence, web, zip, manifestPath, relay, review, runNative, nativeRoot}) {
  const {school, term, pin, managerId, classes: [, , classId]} = fixture;
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const artifact = await readFile(zip);
  const sha256 = contents => createHash('sha256').update(contents).digest('hex');
  assert.match(manifest.version, /^0\.1\.(1[2-9]|[2-9]\d|\d{3,})$/);
  assert.equal(manifest.sha256, sha256(artifact));
  assert.equal(manifest.sizeBytes, artifact.length);
  const checkedStatus = async () => (await (await fetch(origin + '/api/v2/setup/status')).json()).data;
  assert.equal((await checkedStatus()).supportsDesktopDeviceConnections, true);
  const gateFile = process.env.NPEP_DEPLOYMENT_FILE;
  const gate = await readFile(gateFile, 'utf8');
  try {
    await writeFile(gateFile, JSON.stringify({...JSON.parse(gate), enabled: false}));
    assert.equal((await checkedStatus()).supportsDesktopDeviceConnections, false);
  } finally { await writeFile(gateFile, gate); }
  const screen = async name => prisma.classroomScreenBinding.create({data: {schoolId: school.id, name,
    administrativeClassId: classId, createdByAccountId: managerId, tokenHash: randomBytes(32).toString('hex')}});
  const [front, rear, legacy] = await Promise.all([screen('试点前屏'), screen('试点后屏'), screen('试点侧屏')]);
  const exchange = join(nativeRoot, 'exchange');
  await mkdir(exchange);
  async function login(username) {
    const response = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username, password: pin})});
    assert.equal(response.status, 200, 'isolated school login');
    return (await response.json()).data.access_token;
  }
  const admin = await login('admin'), otherAdmin = await login('admin2');
  const root = `/api/v2/npep/schools/${school.id}`;
  async function adminRequest(path, token, value) {
    const requestId = randomUUID();
    const res = await fetch(origin + path, {method: value ? 'POST' : 'GET', signal: AbortSignal.timeout(12000),
      headers: {Authorization: `Bearer ${token}`, 'X-NPEP-Version': '0.1', 'X-Request-Id': requestId,
        'Content-Type': 'application/json'}, ...(value ? {body: JSON.stringify({requestId, ...value})} : {})});
    return {status: res.status, ...(await res.json())};
  }
  assert.equal((await adminRequest(root + '/management-connections', await login('viewer'))).status, 403);
  assert.equal((await adminRequest(`/api/v2/npep/schools/${fixture.other.id}/management-connections`, admin)).status, 403);
  async function native(phase, binding, name, extra = {}) {
    const profile = join(nativeRoot, name);
    const input = join(exchange, `${name}-${phase}.json`);
    const output = join(exchange, `${name}-code.json`);
    try {
      await writeFile(input, JSON.stringify({kind: 'DEVICE_CONNECTIONS_DISPOSABLE_DATABASE',
        origin, bindingId: binding.id, schoolId: school.id, classId, output,
        deviceName: binding.name + '原生组件', ...extra}));
      await runNative(['device-connections', phase, input, profile]);
      if (phase === 'begin') return JSON.parse(await readFile(output, 'utf8'));
    } finally {
      await rm(input, {force: true});
      if (phase === 'begin') await rm(output, {force: true});
    }
  }
  const old = await native('begin', front, 'front', {classApply: false});
  const oldApproval = await adminRequest(root + `/pairings/${old.pairingId}/approve`, admin,
    {screenBindingId: front.id, capabilities: ['device.status']});
  assert.equal(oldApproval.status, 200, oldApproval.error?.code);
  await native('confirm', front, 'front');
  await native('report', front, 'front');
  const previousFront = await prisma.npepPairing.findUniqueOrThrow({where: {id: old.pairingId}});
  assert.equal(previousFront.state, 'ACTIVATED');
  const frontBefore = await prisma.npepDevice.findUniqueOrThrow({where: {id: previousFront.deviceId}});
  const pendingRear = await native('begin', rear, 'rear', {classApply: true});
  const pendingLegacy = await native('begin', legacy, 'legacy', {classApply: false});
  const reviewApproval = review === 'approval' || review === 'all';
  const reviewEquipment = review === 'equipment' || review === 'all';
  const cancellation = reviewApproval ? await native('begin', legacy, 'cancel', {classApply: false}) : null;
  const observations = {}, retiredRequests = [], desktopRequests = [];
  let browserDigest;
  relay.server.on('request', (req, res) => {
    const path = new URL(req.url, origin).pathname;
    if (/\/(class-management|management-connections|pairing-access|pairings|equipment)(?:\/|$)/.test(path))
      res.once('finish', () => {
        desktopRequests.push({method: req.method, path, status: res.statusCode});
        console.log(`${req.method} ${path} ${res.statusCode}`);
      });
  });

  async function waitFor(read, seconds, label) {
    const limit = Date.now() + seconds * 1000;
    while (Date.now() < limit) {
      const result = await read();
      if (result) return result;
      await delay(130);
    }
    throw new Error(label);
  }
  const pair = id => prisma.npepPairing.findUniqueOrThrow({where: {id}});
  const rearDevice = async () => prisma.npepDevice.findUniqueOrThrow({where: {id: (await pair(pendingRear.pairingId)).deviceId}});
  async function checkpoint(name, work) {
    await waitFor(() => access(join(exchange, name + '.request')).then(() => true, () => false), 100, 'No checkpoint: ' + name);
    await work();
    await writeFile(join(exchange, name + '.ready'), '');
  }
  async function equipment(expectedVersion, locked) {
    const device = await rearDevice();
    const started = Date.now();
    const observed = join(exchange, `equipment-${expectedVersion}-${randomUUID()}.recorded`);
    const observe = async () => {
      const snapshot = await waitFor(async () => {
        const saved = await prisma.npepEquipmentInventory.findUnique({where: {deviceId: device.id}});
        return saved?.data?.appliedRevision === expectedVersion && saved.data.error === null &&
          Date.parse(saved.data.receivedAt) >= started ? saved.data : null;
      }, 48, 'Missing actual Host receipt');
      await writeFile(observed, '');
      return snapshot;
    };
    const [, snapshot] = await Promise.all([native('equipment', rear, 'rear', {expectedVersion, locked, observed}), observe()]);
    assert.equal(snapshot.error, null);
    const data = (await prisma.npepEquipmentPolicy.findUnique({where: {deviceId: device.id}}))?.data ??
      {revision: 0, locked: false, microphoneId: null, cameraId: null};
    assert.deepEqual([data.revision, data.locked, data.microphoneId, data.cameraId],
      [expectedVersion, locked, locked ? 'acceptance-mic' : null, locked ? 'acceptance-camera' : null]);
    if (locked) assert.deepEqual([snapshot.selectedMicrophoneId, snapshot.selectedCameraId], ['acceptance-mic', 'acceptance-camera']);
    observations[`equipment${expectedVersion}`] = {deviceId: device.id, revision: expectedVersion, locked,
      applied: snapshot.appliedRevision, receivedAt: snapshot.receivedAt};
  }

  await verifyStudentBrowser({origin, directory, web, evidence, school, rooms: [], afterStudent: async (browser, site) => {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    const downloaded = join(directory, manifest.fileName);
    const input = join(directory, 'released-device-connections.json');
    let child;
    try {
      await page.route(`${site}/.well-known/npclassworks-deployment.json`, route => route.fulfill({json: {
        schemaVersion: 1, adminLegacyManagementEnabled: true, apiBaseUrl: null, adminRetiredSections: ['npep-devices']}}));
      await page.route(`${site}/downloads/npeduconsole/**`, route => route.request().url().endsWith('/console-release.json')
        ? route.fulfill({json: manifest})
        : route.fulfill({contentType: 'application/zip', headers: {'Content-Length': String(artifact.length)},
          body: route.request().method() === 'HEAD' ? undefined : artifact}));
      page.on('request', req => {
        const path = new URL(req.url()).pathname;
        if (path.startsWith('/api/v2/npep/')) retiredRequests.push(path);
      });
      await page.goto(`${site}/classworks-admin?${new URLSearchParams({section: 'npep', school: school.id, term: term.id})}`);
      await page.getByLabel('管理员短账号', {exact: true}).fill('admin');
      await page.getByLabel('个人 PIN', {exact: true}).fill(pin);
      await page.getByRole('button', {name: '登录', exact: true}).click();
      await page.getByRole('heading', {name: '网页版已停止办理大屏互联'}).waitFor();
      assert.deepEqual(retiredRequests, []);
      const downloading = page.waitForEvent('download');
      await page.getByRole('button', {name: '获取 Windows 管理端'}).click();
      await (await downloading).saveAs(downloaded);
      browserDigest = sha256(await readFile(downloaded));
      assert.equal(browserDigest, manifest.sha256);
      await page.screenshot({path: join(evidence, 'edge-same-school-verified-download.png')});
      await writeFile(input, JSON.stringify({isolated: true,
        login: {url: relay.origin, website: site, code: school.code, username: 'admin', pin},
        deviceConnections: {exchange, frontBinding: front.id, rearBinding: rear.id, legacyBinding: legacy.id, rearPair: pendingRear.pairingId,
          rearCode: pendingRear.code, legacyCode: pendingLegacy.code,
          secret: randomBytes(18).toString('hex'), rotatedSecret: randomBytes(18).toString('hex'), reviewApproval, reviewEquipment,
          cancellationCode: cancellation?.code}}));
      child = spawn('pwsh.exe', ['-NoProfile', '-STA', '-File', fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url)),
        '-ManagementLogin', '-FixturePath', input, '-ZipPath', downloaded, '-ManifestPath', manifestPath,
        '-EvidenceDirectory', evidence], {windowsHide: true, stdio: 'inherit'});
      const desktop = new Promise((resolve, reject) => {
        child.once('error', reject);
        child.once('exit', code => code === 0 ? resolve() : reject(new Error('Published device connections exited ' + code)));
      });
      const drive = async () => {
        await checkpoint('change-batch', async () => {
          const credential = await prisma.npepClassManagement.findUniqueOrThrow({where: {schoolId_classId: {schoolId: school.id, classId}}});
          assert.equal(Number(credential.revision), 2);
          assert.equal((await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: front.id}})).npepPairingEnabled, false);
          assert.equal((await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: rear.id}})).npepPairingEnabled, true);
          const changed = await adminRequest(root + `/screen-bindings/${legacy.id}/pairing-access`, otherAdmin,
            {enabled: true, expectedRevision: legacy.npepPairingRevision});
          assert.equal(changed.status, 200, changed.error?.code);
        });
        await checkpoint('confirm-rear', async () => {
          const approved = await pair(pendingRear.pairingId);
          assert.equal(approved.approvalSource, 'CLASS_APPLY');
          assert.equal(approved.screenBindingId, rear.id);
          assert.equal(approved.state, 'APPROVED');
          await native('confirm', rear, 'rear');
          const activated = await rearDevice();
          assert.equal(activated.lastReportedAt, null);
          observations.approval = {requestId: approved.approvalRequestId, pairingId: approved.id, bindingId: rear.id,
            bindingRevision: activated.bindingRevision, deviceId: activated.id, beforeFirstReport: activated.lastReportedAt};
        });
        if (reviewApproval) await checkpoint('expire-legacy-approval', async () => {
          const prior = await pair(pendingLegacy.pairingId);
          assert.equal(prior.state, 'APPROVED');
          await prisma.npepPairing.update({where: {id: prior.id}, data: {expiresAt: new Date(Date.now() - 1000)}});
        });
        await checkpoint('report-rear', async () => {
          const former = await pair(pendingLegacy.pairingId);
          assert.equal(former.state, reviewApproval ? 'APPROVED' : 'CANCELLED');
          assert.equal(former.screenBindingId, legacy.id);
          if (cancellation) assert.equal((await pair(cancellation.pairingId)).state, 'CANCELLED');
          assert.equal((await pair(pendingRear.pairingId)).state, 'ACTIVATED');
          await native('report', rear, 'rear');
          await native('report', front, 'front');
          const after = await prisma.npepDevice.findUniqueOrThrow({where: {id: frontBefore.id}});
          assert.equal(after.credentialId, frontBefore.credentialId);
          assert.ok(after.lastReportedAt > frontBefore.lastReportedAt);
          observations.approval.reportedAt = (await rearDevice()).lastReportedAt;
          observations.front = {bindingId: front.id, deviceId: after.id, originalCredentialStillWorks: true};
          observations.otherApproval = {pairingId: former.id, bindingId: legacy.id, state: former.state,
            expired: former.expiresAt <= new Date()};
          await equipment(0, false);
        });
        await checkpoint('apply-equipment', () => equipment(1, true));
        if (reviewEquipment) {
          const device = await rearDevice();
          const inventory = () => prisma.npepEquipmentInventory.findUniqueOrThrow({where: {deviceId: device.id}});
          const save = async data => prisma.npepEquipmentInventory.update({where: {deviceId: device.id}, data: {data}});
          let original;
          await checkpoint('equipment-mismatch', async () => {
            original = (await inventory()).data;
            await save({...original, selectedCameraId: 'another-camera', selectedMicrophoneId: 'another-mic',
              unlocked: true, receivedAt: new Date().toISOString()});
          });
          await checkpoint('equipment-near-expiry', async () => {
            await save({...original, receivedAt: new Date(Date.now() - 52000).toISOString()});
          });
          await checkpoint('equipment-renew', () => equipment(1, true));
        }
        await checkpoint('clear-equipment', () => equipment(2, false));
        if (reviewApproval) await checkpoint('withdraw-rear-approval-device', async () => {
          const device = await rearDevice();
          const removed = await adminRequest(root + `/devices/${device.id}/revoke`, otherAdmin,
            {expectedBindingRevision: device.bindingRevision});
          assert.equal(removed.status, 200, removed.error?.code);
          assert.equal((await rearDevice()).state, 'REVOKED');
        });
      };
      await Promise.all([desktop, drive()]);
    } finally {
      if (child?.exitCode === null) child.kill();
      await page.close();
      await rm(input, {force: true});
      await rm(downloaded, {force: true});
    }
  }});
  const finalScreens = await prisma.classroomScreenBinding.findMany({where: {id: {in: [rear.id, legacy.id]}}});
  assert.ok(finalScreens.every(row => row.npepPairingEnabled === false));
  const batchWrites = desktopRequests.filter(row => row.method === 'POST' && row.path.endsWith('/pairing-access/batch'));
  assert.deepEqual(batchWrites.map(row => row.status), [409, 502], 'one stale write and one lost successful write without replay');
  if (reviewApproval) {
    const approvals = desktopRequests.filter(row => row.method === 'POST' && row.path.endsWith('/approve'));
    assert.deepEqual(approvals.map(row => row.status), [502, 502, 502], 'each successful approval reply was dropped once');
    assert.ok(desktopRequests.some(row => row.path === root + `/pairings/${pendingLegacy.pairingId}/cancel` && row.status === 410));
    assert.ok(desktopRequests.some(row => row.method === 'GET' && row.path.includes(`/pairings/${pendingRear.pairingId}/approvals/`) && row.status === 200));
    const cancellations = desktopRequests.filter(row => row.path === root + `/pairings/${cancellation.pairingId}/cancel`);
    assert.deepEqual(cancellations.map(row => row.status), [502], 'successful cancellation is never posted again');
  }
  assert.equal(await prisma.npepAudit.count({where: {schoolId: school.id, action: 'SCREEN_PAIRING_BATCH_DISABLED'}}), 1);
  await writeFile(join(evidence, 'release-device-connections.json'), JSON.stringify({
    passed: true, version: manifest.version, sha256: browserDigest, sizeBytes: manifest.sizeBytes, retiredRequests,
    batch: {attempts: batchWrites.length, successfulResponseLost: 1, bindingIds: finalScreens.map(row => row.id)},
    ...(review && {review, desktopRequests}),
    ...observations,
  }, null, 2));
}
