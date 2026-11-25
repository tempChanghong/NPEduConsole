import assert from 'node:assert/strict';
import {randomUUID, randomBytes} from 'node:crypto';
import {spawn} from 'node:child_process';
import {mkdir, readFile, rename, rm, writeFile} from 'node:fs/promises';
import {join, resolve} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';

// The caller supplies the already-built Console UI probe and uses the actual
// guest App/Host to pair. Only this run's loopback database is accessed.
export async function verifyWebsitePolicyProduct({prisma, origin, directory, fixture}) {
  assert.ok(process.env.NP_WEBSITE_PRODUCT_EVIDENCE && process.env.NP_WEBSITE_PRODUCT_CONSOLE);
  const evidence = resolve(process.env.NP_WEBSITE_PRODUCT_EVIDENCE);
  const consoleDriver = resolve(process.env.NP_WEBSITE_PRODUCT_CONSOLE);
  const {school, term, managerId, classes, identity} = fixture;
  const name = ['网站策略验收一班', '网站策略验收二班'];
  const timed = process.env.NP_WEBSITE_PRODUCT_TIMING === '1';
  const screens = await Promise.all(classes.slice(0, 2).map((classId, index) => prisma.classroomScreenBinding.create({data: {
    schoolId: school.id, administrativeClassId: classId, name: name[index],
    tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId
  }})));
  await mkdir(evidence, {recursive: true});
  const access = join(directory, 'website-product-console.json');
  const signIn = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({schoolCode: school.code, username: 'admin', password: fixture.pin})});
  assert.equal(signIn.status, 200);
  const bearer = (await signIn.json()).data.access_token;
  const scopePath = `/api/v2/npep/schools/${school.id}/website-policy/scope/${term.id}/CLASS/`;
  const outcomes = [];
  const paired = [];
  async function call(path, body, version = '0.16') {
    const requestId = randomUUID();
    const response = await fetch(origin + path, {method: body ? 'POST' : 'GET', signal: AbortSignal.timeout(10000),
      headers: {Authorization: 'Bearer ' + bearer, 'Content-Type': 'application/json', 'X-NPEP-Version': version,
        'X-Request-Id': requestId, 'X-NPEP-Website-Policy-Automation': '1',
        ...(timed ? {'X-NPEP-Website-Policy-Timing': '1'} : {})}, ...(body ? {body: JSON.stringify({requestId, ...body})} : {})});
    const result = await response.json();
    assert.equal(response.status, 200, `${path} ${result.error?.code ?? ''}`);
    return result.data;
  }
  async function publish(extra = {}) {
    await writeFile(access, JSON.stringify({origin, schoolId: school.id, schoolCode: school.code, pin: fixture.pin,
      gradeCode: 'G2', classCode: 'G2-1', readReceipt: false, ...extra}));
    try {
      await new Promise((done, fail) => {
        const child = spawn(consoleDriver, ['--live-website-policy', access, evidence], {windowsHide: true, stdio: 'inherit'});
        child.once('error', fail);
        child.once('close', code => code === 0 ? done() : fail(new Error(`Console exit ${code}`)));
      });
    } finally { await rm(access, {force: true}); }
  }
  async function signal(stage, extra = {}) {
    const target = join(evidence, 'fixture.json');
    const temp = target + '.tmp';
    await writeFile(temp, JSON.stringify({stage, origin, ...identity, classes: classes.slice(0, 2), bindings: screens.map(row => row.id), names: name, ...extra}, null, 2));
    await rename(temp, target);
    console.log('PRODUCT WEBSITE POLICY:', stage);
  }
  async function until(label, read, timeout = 300000) {
    const end = Date.now() + timeout;
    do {
      const result = await read();
      if (result) return result;
      await delay(500);
    } while (Date.now() < end);
    throw new Error('Timed out: ' + label);
  }
  async function marker(stage, timeout = 300000) {
    const path = join(evidence, stage + '.json');
    return until(stage, async () => {
      try { return JSON.parse((await readFile(path, 'utf8')).replace(/^\uFEFF/, '')); }
      catch (error) { if (error.code !== 'ENOENT' && !(error instanceof SyntaxError)) throw error; return null; }
    }, timeout);
  }
  async function receipt(index, statuses, phase = null, timeout) {
    return until(statuses.join('/') + ' binding ' + index, async () => {
      const row = await call(scopePath + classes[index]);
      const screen = row.screens.find(item => item.bindingId === screens[index].id);
      if (!statuses.includes(screen.status) || phase && screen.schedulePhase !== phase) return null;
      const result = {screen, policy: row.rows[0].before};
      outcomes.push(result);
      await writeFile(join(evidence, 'receipts.json'), JSON.stringify(outcomes, null, 2));
      return result;
    }, timeout);
  }
  async function pair(index) {
    await signal('pair-' + index, {index});
    const {pairingId} = await marker('pair-request-' + index);
    assert.match(pairingId, /^[a-f0-9-]{36}$/);
    await call(`/api/v2/npep/schools/${school.id}/pairings/${pairingId}/approve`,
      {screenBindingId: screens[index].id, capabilities: ['device.status']}, '0.1');
    await signal('finish-pair-' + index, {index});
    paired[index] = await until('new binding', () => prisma.npepDevice.findFirst({where: {schoolId: school.id,
      screenBindingId: screens[index].id, administrativeClassId: classes[index], deviceName: name[index], state: 'ACTIVE'}}));
    assert.equal((await receipt(index, timed ? ['WRITTEN_PENDING', 'CLEARED_PENDING'] : ['WRITTEN_PENDING'])).screen.deviceId, paired[index].id);
  }
  if (timed) {
    const rule = {hostPeriods: {'douyin.com': 'LESSON|AFTER_SCHOOL'}, timed: true, unavailableBehavior: 'ALLOW'};
    await signal('publishing-timed-grade');
    await publish({...rule, imageName: 'timed-grade-1160'});
    const saved = (await receipt(0, ['NOT_PAIRED'])).policy;
    assert.deepEqual(saved.rules, [{host: 'douyin.com', period: 'LESSON|AFTER_SCHOOL'}]);
    assert.equal(saved.unavailableBehavior, 'ALLOW');
    if (process.env.NP_WEBSITE_PRODUCT_UI_ONLY === '1') {
      await publish({...rule, allowClass: true, draftHosts: ['douyin.com'], imageName: 'timed-class-840', windowWidth: 840, windowHeight: 480,
        unavailableBehavior: 'BLOCK', expectedBefore: '现在：年级默认', expectedAfter: '发布后：本班自定'});
      // An independent class copy must not change the effective rule of another class.
      assert.equal((await call(scopePath + classes[1])).rows[0].before.unavailableBehavior, 'ALLOW');
      await signal('complete');
      return;
    }
    const hold = process.env.NP_WEBSITE_PRODUCT_TIMING_HOLD === '1';
    if (hold) await writeFile(join(evidence, 'classroom-phase.txt'), 'Lesson');
    await pair(0);
    if (hold) {
      const lesson = ['WRITTEN_PENDING', 'EDGE_CONFIRMED'];
      await receipt(0, lesson, 'LESSON');
      await signal('timed-hold-short');
      await marker('timed-hold-short');
      await receipt(0, lesson, 'LESSON');
      await signal('timed-hold-long');
      await receipt(0, ['CLEARED_PENDING'], 'UNAVAILABLE');
      await signal('timed-hold-recover');
      await receipt(0, lesson, 'LESSON');
      await signal('timed-hold-recovered');
      await marker('timed-hold-recovered');
    }
    const natural = process.env.NP_WEBSITE_PRODUCT_NATURAL === '1';
    const phases = natural ? [
      ['LESSON', ['WRITTEN_PENDING', 'EDGE_CONFIRMED'], 'timed-native-lesson'],
      ['BREAK', ['CLEARED_PENDING'], 'timed-native-break'],
      ['LESSON', ['WRITTEN_PENDING', 'EDGE_CONFIRMED'], 'timed-native-return']
    ] : [
      ['LESSON', ['WRITTEN_PENDING', 'EDGE_CONFIRMED'], 'Lesson'],
      ['BREAK', ['CLEARED_PENDING'], 'Break'],
      ['AFTER_SCHOOL', ['WRITTEN_PENDING', 'EDGE_CONFIRMED'], 'AfterSchool'],
      ['UNAVAILABLE', ['CLEARED_PENDING'], 'Paused'],
      ['LESSON', ['WRITTEN_PENDING', 'EDGE_CONFIRMED'], 'Lesson']
    ];
    for (const [phase, statuses, stage] of hold ? [] : phases) {
      if (!natural) await writeFile(join(evidence, 'classroom-phase.txt'), stage);
      const result = await receipt(0, statuses, phase, natural ? 900000 : 300000);
      assert.equal(result.screen.deviceId, paired[0].id);
      assert.equal(result.screen.timingSupported, true);
      assert.equal(result.policy.policyId, saved.policyId);
      await signal(natural ? stage : 'timed-' + stage);
      // The independent browser observation can itself take five minutes.
      if (natural && stage === 'timed-native-return') await marker(stage, 420000);
    }
    await signal('unpair-0');
    await marker('unpair-0');
    await receipt(0, ['NOT_PAIRED']);
    await signal('complete');
    return;
  }
  await signal('publishing-grade');
  await publish();
  assert.equal((await receipt(0, ['NOT_PAIRED'])).policy.source, 'GRADE');

  const neighborOnly = process.env.NP_WEBSITE_PRODUCT_NEIGHBOR_ONLY === '1';
  if (!neighborOnly) {
    await pair(0);
    await signal('edge-blocked-0');
    await receipt(0, ['EDGE_CONFIRMED']);
    await marker('edge-blocked-0');
    await publish({readReceipt: true, expectedScreen: name[0], expectedStatus: 'Edge 前台已观察', imageName: '02-edge-confirmed-840'});
  }
  await publish({allowClass: true});
  assert.deepEqual((await receipt(0, [neighborOnly ? 'NOT_PAIRED' : 'CLEARED_PENDING'])).policy.blockedHosts, []);
  assert.deepEqual((await call(scopePath + classes[1])).rows[0].before.blockedHosts, ['douyin.com']);
  if (!neighborOnly) {
    await signal('edge-allowed-0');
    await marker('edge-allowed-0');
    await signal('unpair-0');
    await marker('unpair-0');
    await receipt(0, ['NOT_PAIRED']);
  }
  await pair(1);
  await signal('edge-blocked-1');
  await receipt(1, ['EDGE_CONFIRMED']);
  await marker('edge-blocked-1');
  assert.deepEqual((await call(scopePath + classes[0])).rows[0].before.blockedHosts, []);
  await signal('unpair-1');
  await marker('unpair-1');
  await receipt(1, ['NOT_PAIRED']);
  await signal('complete');
}
