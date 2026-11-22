import assert from 'node:assert/strict';
import {randomBytes, randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {spawn} from 'node:child_process';
import {createRequire} from 'node:module';
import {cp, mkdir, mkdtemp, readFile, rm, unlink, writeFile} from 'node:fs/promises';
import {basename, dirname, join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {setTimeout as delay} from 'node:timers/promises';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

const artifactRoot = fileURLToPath(new URL('../.artifacts/exam-workbench/', import.meta.url));

export async function verifyExamWorkbench(options) {
  await mkdir(artifactRoot, {recursive: true});
  const stateRoot = await mkdtemp(join(artifactRoot, 'native-fixture-'));
  try { await verify({...options, stateRoot}); }
  finally {
    if (dirname(resolve(stateRoot)) === resolve(artifactRoot) && basename(stateRoot).startsWith('native-fixture-'))
      await rm(stateRoot, {recursive: true, force: true});
  }
}

async function verify({prisma, origin, fixture, directory, evidence, web, runDesktop, runNative, stateRoot}) {
  const {school, term, pin, managerId} = fixture;
  await mkdir(evidence, {recursive: true});
  const tools = resolve(web, '../NPEduTools');
  const coord = join(stateRoot, 'coord');
  await mkdir(coord);
  const path = '/api/v2/npep/schools/' + school.id + '/devices/';
  const checkpoints = [];
  let playerProc;
  let sawPlayerExit;
  let nativeRun;
  let relay;
  let legacyPlan;
  let legacyContext;
  let interceptedStarts = 0, startReceiptReads = 0;
  let lostStart;
  let observedReady, observedClosed;

  async function until(read, message, timeout = 35000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      const value = await read();
      if (value) return value;
      await delay(150);
    }
    throw new Error(message);
  }

  const readJSON = async file => {
    try { return JSON.parse(await readFile(file, 'utf8')); }
    catch (error) { if (error.code === 'ENOENT' || error instanceof SyntaxError) return null; throw error; }
  };

  async function login(username) {
    const response = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username, password: pin})});
    assert.ok(response.ok);
    return (await response.json()).data;
  }

  const [administrator, second] = await Promise.all([login('admin'), login('admin2')]);
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const room = await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS', code: 'EXAM-7', name: '高二（7）班'}});
  const binding = name => prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: room.id,
    name, tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId, npepPrepared: true}});
  const front = await binding('七班前屏');
  const rear = await binding('七班后屏');
  const modeOnly = await binding('环境切换验收屏');
  const pairFixture = join(directory, 'exam-pair-fixture.json');
  await writeFile(pairFixture, JSON.stringify({kind: 'TERM_PREPARATION_DISPOSABLE_DATABASE', origin, adminToken: administrator.access_token,
    schoolId: school.id, classId: room.id, bindingIds: [front.id, rear.id, modeOnly.id]}));
  const profile = join(stateRoot, 'devices');
  await runNative(['pair', pairFixture, profile]);
  const find = id => prisma.npepDevice.findFirstOrThrow({where: {screenBindingId: id, state: 'ACTIVE'}});
  const [frontDevice, rearDevice, modeOnlyDevice] = await Promise.all([find(front.id), find(rear.id), find(modeOnly.id)]);

  async function query(deviceId, suffix, version = '0.5') {
    const r = await fetch(origin + path + deviceId + '/' + suffix, {headers: {Authorization: 'Bearer ' + second.access_token,
      'X-NPEP-Version': version, 'X-Request-Id': randomUUID()}});
    assert.equal(r.status, 200);
    return (await r.json()).data;
  }
  const rearPlans = () => query(rearDevice.id, 'exam-plans');
  const runtime = id => query(id, 'runtime-status', '0.4');
  const ledger = id => query(id, 'runtime-operations', '0.4');
  const matching = op => op.items.find(item => item.operationId === lostStart?.operationId);
  let blockModeOnlyPlanRead = false;
  const hidePlayerObservation = join(coord, 'hide-player-observation');

  async function checkpoint(name) {
    switch (name) {
      case 'rear-selected':
        assert.equal((await runtime(frontDevice.id)).status.runtimeMode, 'DAILY');
        assert.equal((await runtime(rearDevice.id)).status.runtimeMode, 'DAILY');
        break;
      case 'exam-environment': {
        const mode = await runtime(rearDevice.id);
        const history = await ledger(rearDevice.id);
        assert.equal(mode.status.runtimeMode, 'EXAM');
        assert.equal(history.items[0].state, 'SUCCEEDED');
        assert.equal(history.items[0].target, 'EXAM');
        assert.equal(mode.status.remoteExamPause, true);
        assert.equal((await runtime(frontDevice.id)).status.runtimeMode, 'DAILY');
        break;
      }
      case 'prepared': {
        const state = await rearPlans();
        assert.equal(state.items[0].state, 'PREPARED');
        assert.equal(state.items[0].summary.exams.length, 2);
        assert.equal(state.status.player.sessions.length, 0);
        break;
      }
      case 'playing': {
        const actual = await until(() => readJSON(join(coord, 'ready.json')), 'Real player window was never observed');
        const current = await rearPlans();
        const item = matching(current);
        assert.equal(item?.state, 'STARTED');
        assert.equal(item.sessionId, actual.sessionId);
        assert.deepEqual(current.status.player.sessions.map(s => [s.id, s.state]), [[actual.sessionId, 'ready']]);
        assert.equal(interceptedStarts, 1);
        assert.ok(startReceiptReads >= 1);
        assert.equal(current.items.length, 1);
        assert.equal((await query(frontDevice.id, 'exam-plans')).items.length, 0);
        observedReady = {sessionId: item.sessionId, kvReceivedAt: current.receivedAt, windowObservedAt: actual.observedAt};
        await legacyPlan.getByText('放映已就绪', {exact: true}).waitFor({timeout: 25000});
        await legacyPlan.screenshot({path: join(evidence, 'legacy-edge-playing.png')});
        break;
      }
      case 'hide-playing-observation':
        await writeFile(hidePlayerObservation, '1');
        await until(async () => (await rearPlans()).status.player === null, 'Unknown playback observation was not reported');
        assert.ok(await readJSON(join(coord, 'ready.json')));
        break;
      case 'allow-local-close':
        await unlink(hidePlayerObservation);
        await until(async () => (await rearPlans()).status.player?.sessions.some(item =>
          item.id === observedReady.sessionId && item.state === 'ready'), 'Active playback observation did not resume');
        await writeFile(join(coord, 'close'), '1');
        break;
      case 'closed': {
        const actual = await until(() => readJSON(join(coord, 'closed.json')), 'Real player window did not close');
        const current = await rearPlans();
        const item = matching(current);
        assert.equal(item.sessionId, actual.sessionId);
        assert.equal(current.status.player.lastSession?.state, 'closed');
        assert.equal(current.status.player.lastSession.id, actual.sessionId);
        assert.equal(current.status.player.sessions.length, 0);
        observedClosed = {sessionId: item.sessionId, kvReceivedAt: current.receivedAt, windowObservedAt: actual.observedAt};
        await legacyPlan.getByText('放映已结束', {exact: true}).waitFor({timeout: 25000});
        await legacyPlan.screenshot({path: join(evidence, 'legacy-edge-closed.png')});
        break;
      }
      case 'returned': {
        const mode = await runtime(rearDevice.id);
        const operations = await ledger(rearDevice.id);
        assert.equal(mode.status.runtimeMode, 'DAILY');
        assert.equal(mode.status.remoteExamPause, false);
        assert.deepEqual(operations.items.map(op => [op.target, op.state]), [['DAILY', 'SUCCEEDED'], ['EXAM', 'SUCCEEDED']]);
        assert.equal((await runtime(frontDevice.id)).status.runtimeMode, 'DAILY');
        assert.deepEqual((await ledger(frontDevice.id)).items, []);
        break;
      }
      case 'new-cycle':
        assert.equal((await runtime(rearDevice.id)).status.runtimeMode, 'EXAM');
        await until(async () => (await rearPlans()).status.player.lastSession === null, 'Previous player observation was not cleared');
        assert.equal(matching(await rearPlans()).sessionId, observedClosed.sessionId);
        break;
      case 'returned-again':
        assert.equal((await runtime(rearDevice.id)).status.runtimeMode, 'DAILY');
        assert.equal((await runtime(frontDevice.id)).status.runtimeMode, 'DAILY');
        assert.equal((await rearPlans()).items.length, 1);
        break;
      case 'fail-mode-only-plan-read':
        assert.equal((await runtime(modeOnlyDevice.id)).status.runtimeMode, 'EXAM');
        assert.deepEqual(await query(modeOnlyDevice.id, 'exam-plans'), {context: null, status: null, receivedAt: null, online: false, items: []});
        blockModeOnlyPlanRead = true;
        break;
      case 'restore-mode-only-plan-read':
        blockModeOnlyPlanRead = false;
        break;
      case 'mode-only-returned':
        assert.equal((await runtime(modeOnlyDevice.id)).status.runtimeMode, 'DAILY');
        assert.deepEqual((await ledger(modeOnlyDevice.id)).items.map(op => [op.target, op.state]),
          [['DAILY', 'SUCCEEDED'], ['EXAM', 'SUCCEEDED']]);
        assert.equal((await runtime(frontDevice.id)).status.runtimeMode, 'DAILY');
        break;
      default: assert.fail('Unrecognized isolated checkpoint');
    }
    checkpoints.push(name);
    console.log('PASS exam workbench checkpoint: ' + name);
  }

  try {
    const source = resolve(process.env.NP_EXAM_AWARE_SOURCE || resolve(web, '../../ExamAware2'));
    const require = createRequire(join(web, 'package.json'));
    const playwright = createRequire(require.resolve('@playwright/test/package.json')).resolve('playwright/package.json');
    const args = [join(tools, 'scripts/test-examaware-host.mjs'), '--source', source, '--playwright', playwright, '--remote-workbench', coord];
    if (process.env.NP_EXAM_AWARE_ELECTRON) args.push('--electron', resolve(process.env.NP_EXAM_AWARE_ELECTRON));
    if (process.env.NP_EXAM_AWARE_ARCHIVE) args.push('--app-archive', resolve(process.env.NP_EXAM_AWARE_ARCHIVE));
    playerProc = spawn(process.execPath, args, {cwd: tools, windowsHide: true, stdio: 'inherit'});
    sawPlayerExit = new Promise((ok, fail) => {
      playerProc.once('error', fail);
      playerProc.once('exit', code => code === 0 ? ok() : fail(new Error('Real ExamAware fixture exited with ' + code)));
    });
    sawPlayerExit.catch(() => {});
    const bridge = await Promise.race([sawPlayerExit.then(() => assert.fail('Player exited before bridge readiness')),
      until(() => readJSON(join(coord, 'bridge.json')), 'No real ExamAware bridge was available', 50000)]);
    const nativeFixture = join(directory, 'exam-native-fixture.json');
    const nativeFinish = join(coord, 'native-finish');
    await writeFile(nativeFixture, JSON.stringify({kind: 'EXAM_WORKBENCH_DISPOSABLE_DATABASE', origin, port: bridge.port, key: bridge.key, finish: nativeFinish}));
    nativeRun = runNative(['exam-workbench', nativeFixture, profile]);
    nativeRun.catch(() => {});
    await until(async () => (await runtime(rearDevice.id)).connectivity === 'ONLINE' && (await rearPlans()).status?.enabled,
      'Isolated mode and plan observations never started', 45000);

    const target = new URL(origin);
    relay = createServer(async (req, res) => {
      if (req.method === 'POST' && req.url.startsWith('/__exam/')) {
        try { await checkpoint(req.url.slice('/__exam/'.length)); res.writeHead(204).end(); }
        catch (error) { console.error(error); res.writeHead(500).end(); }
        return;
      }
      if (req.method === 'GET' && req.url.startsWith(path + rearDevice.id + '/exam-plans/') && req.url.includes('/start/receipts/'))
        startReceiptReads++;
      if (blockModeOnlyPlanRead && req.method === 'GET' && req.url === path + modeOnlyDevice.id + '/exam-plans') {
        res.writeHead(503).end();
        return;
      }
      const outgoing = request(new URL(req.url, target), {method: req.method, headers: {...req.headers, host: target.host}}, upstream => {
        if (req.method === 'POST' && req.url.startsWith(path + rearDevice.id + '/exam-plans/') && req.url.endsWith('/start')) {
          interceptedStarts++;
          if (upstream.statusCode === 200 && !lostStart) {
            const parts = [];
            upstream.on('data', data => parts.push(data));
            upstream.on('end', () => {
              const result = JSON.parse(Buffer.concat(parts)).data;
              lostStart = {operationId: result.operationId, state: result.state};
              res.destroy();
            });
            return;
          }
        }
        res.writeHead(upstream.statusCode, upstream.headers); upstream.pipe(res);
      });
      outgoing.on('error', () => res.destroy());
      req.pipe(outgoing);
    });
    await new Promise((ok, fail) => relay.once('error', fail).listen(0, '127.0.0.1', ok));
    const access = join(directory, 'exam-desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port,
      schoolCode: school.code, schoolId: school.id, pin, roomId: room.id, frontId: front.id, rearId: rear.id,
      frontDeviceId: frontDevice.id, rearDeviceId: rearDevice.id, modeOnlyId: modeOnly.id, modeOnlyDeviceId: modeOnlyDevice.id, allowWrites: true}));

    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (browser, site) => {
      legacyContext = await browser.newContext({viewport: {width: 1280, height: 900}, serviceWorkers: 'block',
        storageState: {cookies: [], origins: [{origin: site, localStorage: [
          {name: 'classworks-v2-access-token', value: second.access_token},
          {name: 'classworks-v2-refresh-token', value: second.refresh_token},
          {name: 'classworks-v2-oobe', value: JSON.stringify({version: 1, completed: true, roleHint: 'teacher'})}
        ]}]}});
      legacyPlan = await legacyContext.newPage();
      await legacyPlan.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
      await legacyPlan.goto(site + '/classworks-admin?section=npep-devices&school=' + school.id + '&term=' + term.id);
      const row = legacyPlan.locator('.npep-device').filter({hasText: '七班后屏'});
      await row.getByRole('button', {name: '考试方案', exact: true}).click({timeout: 25000});
      await legacyPlan.getByRole('heading', {name: '考试方案', exact: true}).waitFor();
      await runDesktop(['--live-exam-workbench', access, evidence]);
    }});
    assert.deepEqual(checkpoints, ['rear-selected', 'exam-environment', 'prepared', 'playing', 'hide-playing-observation',
      'allow-local-close', 'closed', 'returned', 'new-cycle', 'returned-again', 'fail-mode-only-plan-read',
      'restore-mode-only-plan-read', 'mode-only-returned']);
    await writeFile(nativeFinish, '1');
    await nativeRun;
    await writeFile(join(coord, 'finish'), '1');
    await sawPlayerExit;
    await cp(join(stateRoot, 'player-evidence'), join(evidence, 'real-player'), {recursive: true});
    await writeFile(join(evidence, 'exam-workbench-cross-end.json'), JSON.stringify({passed: true, checkpoints,
      front: {bindingId: front.id, deviceId: frontDevice.id, remainedDaily: true}, rear: {bindingId: rear.id, deviceId: rearDevice.id,
        operationId: lostStart.operationId, lostAcknowledgementState: lostStart.state, startPosts: interceptedStarts, startReceiptReads,
        ready: observedReady, closed: observedClosed}, modeOnly: {bindingId: modeOnly.id, deviceId: modeOnlyDevice.id,
        returnedWithoutPlanReport: true, blockedOnFailedPlanRead: true},
      modeEffects: 'simulated OS switch; real NPEP requests, events and player bridge/window'}, null, 2));
    console.log('PASS Windows Console, legacy Edge, the actual ExamAware player, native NPEP, and PostgreSQL.');
  } finally {
    await legacyContext?.close().catch(() => {});
    for (const signal of ['native-finish', 'close', 'finish']) await writeFile(join(coord, signal), '1');
    if (nativeRun) await nativeRun.catch(() => {});
    if (playerProc && playerProc.exitCode === null && playerProc.signalCode === null) {
      await Promise.race([sawPlayerExit.catch(() => {}), delay(6000)]);
      if (playerProc.exitCode === null && playerProc.signalCode === null) playerProc.kill();
    }
    await cp(join(stateRoot, 'player-evidence'), join(evidence, 'real-player'), {recursive: true}).catch(() => {});
    if (relay) {
      relay.closeAllConnections();
      await new Promise(ok => relay.close(ok));
    }
  }
}
