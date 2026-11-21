import assert from 'node:assert/strict';
import {randomBytes, randomUUID} from 'node:crypto';
import {spawn} from 'node:child_process';
import {createServer, request} from 'node:http';
import {createRequire} from 'node:module';
import {mkdir, writeFile} from 'node:fs/promises';
import {dirname, join} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {deflateSync} from 'node:zlib';
import {pathToFileURL} from 'node:url';
import {nextStudyMonday} from './class-study-fixture.mjs';

export async function verifySchoolStudy({prisma, origin, fixture, fromKv, directory, evidence, web, runDesktop, runNative}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, managerId, pin, identity} = fixture;
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const from = nextStudyMonday(), date = add(from, 5);
  function add(value, days) { return new Date(Date.parse(value + 'T00:00:00Z') + days * 86400000).toISOString().slice(0, 10); }
  const rooms = await prisma.workspace.createManyAndReturn({data: Array.from({length: 53}, (_, index) => ({
    termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS', name: '高二（' + String(index + 7).padStart(2, '0') + '）班',
    code: 'STUDY-' + (index + 7)
  }))});
  const [seven, eight, nine] = ['STUDY-7', 'STUDY-8', 'STUDY-9'].map(code => rooms.find(row => row.code === code));
  const tail = rooms.find(row => row.code === 'STUDY-57');
  const {defaultStudySettings} = await fromKv('domain/npep/studySettings.js');
  const {secretHash} = await fromKv('domain/npep/wire.js');
  const gradeSettings = defaultStudySettings();
  gradeSettings.schedule = {...gradeSettings.schedule, effectiveFrom: from, rules: [{days: [4], start: '19:00', end: '20:00'}]};
  gradeSettings.examSchedule = {...gradeSettings.examSchedule, effectiveFrom: from, rules: [{days: [6], start: '13:30', end: '14:10'}]};
  gradeSettings.homework = {...gradeSettings.homework, sourceMode: 'SelectedClasses', sourceClassIds: [eight.id], rotationSeconds: 72};
  gradeSettings.noise = {...gradeSettings.noise, enabled: false, redLineDbfs: -36};
  const independent = structuredClone(gradeSettings);
  independent.schedule.rules = [{days: [6], start: '20:00', end: '20:40'}];
  const override = settings => ({mode: 'Override', settings});
  for (const [type, id, policy] of [['GRADE', grade.id, override(gradeSettings)], ['CLASS', eight.id, override(independent)],
    ['CLASS', nine.id, {mode: 'Disabled', settings: null}], ['CLASS', tail.id, override(independent)]]) {
    await prisma.$executeRaw`INSERT INTO "NpepStudySetting" ("schoolId","termId","targetType","targetId",revision,policy,"updatedBy","updatedAt")
      VALUES (${school.id},${term.id},${type},${id},1,${JSON.stringify(policy)}::jsonb,${managerId},NOW())`;
  }
  async function createScreen(name) {
    const binding = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: seven.id,
      name, tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId}});
    const secret = randomBytes(32).toString('base64url');
    const device = await prisma.npepDevice.create({data: {...identity, id: randomUUID(), installationId: randomUUID(),
      credentialId: randomUUID(), secretHash: secretHash(secret), schoolId: school.id, administrativeClassId: seven.id,
      screenBindingId: binding.id, bindingRevision: binding.npepBindingRevision, deviceName: name + '终端',
      credentialExpiresAt: new Date(Date.now() + 86400000)}});
    return {binding, device, bearer: 'npep1.' + device.credentialId + '.' + secret};
  }
  const [front, back] = await Promise.all(['前屏', '后屏'].map(createScreen));
  async function login(username) {
    const res = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username, password: pin})});
    assert.equal(res.status, 200);
    return (await res.json()).data;
  }
  const [admin, browserAdmin] = await Promise.all(['admin', 'admin2'].map(login));
  const schoolPath = '/api/v2/npep/schools/' + school.id;
  const studyPath = schoolPath + '/study-settings', seatingPath = schoolPath + '/seating';
  async function api(path, {token = admin.access_token, method = 'GET', body, version = '0.10', status = 200} = {}) {
    const requestId = randomUUID();
    const res = await fetch(origin + path, {method, headers: {Authorization: 'Bearer ' + token, 'X-Request-Id': requestId,
      'X-NPEP-Version': version, 'Content-Type': 'application/json'}, body: body === undefined ? undefined : JSON.stringify({...body, requestId})});
    const reply = await res.json();
    assert.equal(res.status, status, path + ' ' + reply.error?.code);
    return reply.data ?? reply.error;
  }
  const {png, chunk} = await fromKv('tests/helpers/npepSeatingFixture.js');
  const otherSeat = await api(seatingPath, {method: 'POST', version: '0.14',
    body: {termId: term.id, classId: eight.id, expectedRevision: 0, action: 'image', image: png(3, 3).toString('base64')}});
  await api('/api/v2/npep/schools/' + other.id + '/study-settings', {status: 403});
  const marks = new Set(), checkpoints = [];
  let native, nativeFailure, allowedClose = false, lostSchedule = false, lostSeat = false, studyReceiptReads = 0, seatingReceiptReads = 0;
  const pgRows = () => prisma.$queryRaw`SELECT "targetType", "targetId", revision, policy FROM "NpepStudySetting" WHERE "schoolId"=${school.id}`;
  const gradeRow = async () => (await pgRows()).find(row => row.targetType === 'GRADE' && row.targetId === grade.id);
  const seatQuery = '?termId=' + term.id + '&classId=' + seven.id;
  async function checkNative(mark) {
    for (let attempt = 0; attempt < 170; attempt++) {
      if (nativeFailure) throw nativeFailure;
      if (marks.has(mark)) return;
      await delay(250);
    }
    throw new Error('Native did not reach ' + mark);
  }
  let relayUrl, nativePath;
  const legacy = prepareLegacyStudyWeb({web, directory, evidence, origin, token: browserAdmin, school, term, room: eight});
  legacy.catch(() => {});
  async function checkpoint(step) {
    if (step === 'web-change') await (await legacy).edit();
    else if (step === 'stale') {
      const rows = await pgRows();
      assert.equal((await gradeRow()).revision, 1);
      assert.equal(rows.find(row => row.targetId === eight.id).revision, 2);
      assert.equal(rows.find(row => row.targetId === eight.id).policy.settings.schedule.rules[0].end, '20:55');
      assert.equal(lostSchedule, false);
    } else if (step === 'schedule-applied') {
      const row = await gradeRow();
      assert.equal(row.revision, 2);
      const old = structuredClone(gradeSettings), saved = structuredClone(row.policy.settings);
      delete old.schedule; delete saved.schedule;
      assert.deepEqual(saved, old, 'other mode and all non-edited settings survive');
      assert.deepEqual(row.policy.settings.schedule.exceptions, [{date, kind: 'UseWeekday', weekday: 4}]);
      const days = await api(studyPath + '/calendar?termId=' + term.id + '&gradeId=' + grade.id + '&from=' + date + '&days=1');
      assert.equal(days.classes.length, 53);
      const day = room => days.classes.find(row => row.classId === room.id);
      assert.equal(day(seven).source, 'Grade');
      assert.equal(day(seven).dates[0].daily.windows[0].start, date + 'T19:00:00');
      assert.equal(day(seven).dates[0].exam.windows[0].start, date + 'T13:30:00');
      assert.equal(day(eight).source, 'Class');
      assert.equal(day(eight).dates[0].daily.windows[0].end, date + 'T20:55:00');
      assert.equal(day(nine).source, 'Disabled');
      assert.equal(day(nine).dates[0].daily.windows.length, 0);
      assert.ok(lostSchedule && studyReceiptReads === 1);
    } else if (step === 'start-native') {
      native = runNative([join(evidence, 'native'), '--live-school-study-seating', nativePath]);
      native.catch(error => { nativeFailure = error; });
      await checkNative('old-open');
    } else if (step === 'old-in-use') {
      await checkNative('old-while-new-downloaded');
      const current = await api(seatingPath + seatQuery, {version: '0.14'});
      assert.equal(current.current.revision, 2);
      assert.notEqual(current.current.hash, otherSeat.hash);
      const live = current.receipts.find(row => row.bindingId === front.binding.id);
      assert.ok(live.downloaded && live.fresh && live.visible && live.revision === 1 && !live.current);
      assert.ok(current.bindings.some(row => row.bindingId === back.binding.id && row.deviceId === back.device.id));
      assert.ok(!current.receipts.some(row => row.bindingId === back.binding.id));
      assert.ok(lostSeat && seatingReceiptReads === 1);
    } else if (step === 'allow-close') allowedClose = true;
    else if (step === 'new-in-use') await checkNative('new-open');
    else if (step === 'withdrawn') await checkNative('withdrawn');
    else if (step === 'restored') {
      await checkNative('restored');
      const current = await api(seatingPath + seatQuery, {version: '0.14'});
      assert.equal(current.current.revision, 4);
      assert.equal(current.current.hash, current.versions.find(row => row.revision === 1).hash);
      assert.ok(current.receipts.some(row => row.bindingId === front.binding.id && row.fresh && row.visible && row.current));
    } else if (step === 'restored-inheritance') {
      const policy = (await pgRows()).find(row => row.targetId === tail.id);
      assert.equal(policy.revision, 2);
      assert.deepEqual(policy.policy, {mode: 'Inherit', settings: null});
      const calendar = await api(studyPath + '/calendar?termId=' + term.id + '&gradeId=' + grade.id + '&from=' + date + '&days=1');
      assert.equal(calendar.classes.find(row => row.classId === tail.id).dates[0].daily.windows[0].start, date + 'T19:00:00');
    } else throw new Error('Unknown school study checkpoint.');
    checkpoints.push(step);
  }
  const server = createServer(async (req, res) => {
    if (req.url.startsWith('/__study/native/')) {
      const flag = req.url.slice('/__study/native/'.length);
      if (req.method === 'POST') { marks.add(flag); res.writeHead(204).end(); }
      else res.writeHead(flag === 'allow-close' && allowedClose ? 204 : 409).end();
      return;
    }
    if (req.method === 'POST' && req.url.startsWith('/__study/')) {
      try { await checkpoint(req.url.slice('/__study/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    const studySave = req.method === 'POST' && req.url === studyPath;
    const seatSave = req.method === 'POST' && req.url === seatingPath;
    if (req.method === 'GET' && req.url.startsWith(studyPath + '/requests/')) studyReceiptReads++;
    if (req.method === 'GET' && req.url.startsWith(seatingPath + '/requests/')) seatingReceiptReads++;
    const endpoint = new URL(req.url, origin);
    const forwarded = request(endpoint, {method: req.method, headers: {...req.headers, host: endpoint.host}}, response => {
      if (response.statusCode !== 200 || !studySave && !seatSave) {
        res.writeHead(response.statusCode, response.headers); response.pipe(res); return;
      }
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const body = Buffer.concat(chunks), value = JSON.parse(body).data;
        if (studySave && !lostSchedule) { lostSchedule = true; res.destroy(); }
        else if (seatSave && value.revision === 2 && !lostSeat) { lostSeat = true; res.destroy(); }
        else { res.writeHead(response.statusCode, response.headers); res.end(body); }
      });
    });
    forwarded.on('error', () => res.destroy());
    req.pipe(forwarded);
  });
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    relayUrl = 'http://127.0.0.1:' + server.address().port;
    nativePath = join(directory, 'native-school-study.json');
    await writeFile(nativePath, JSON.stringify({origin: relayUrl, deployment: identity, isolated: true,
      bearer: front.bearer, classId: seven.id, date}), {mode: 0o600});
    const desktopPath = join(directory, 'desktop-school-study.json');
    const [image1, image2] = ['seat-old.png', 'seat-new.png'].map(file => join(directory, file));
    function sample(columns, ink, paper) {
      const width = 750, height = 475, line = width * 3 + 1;
      const header = Buffer.alloc(13), raw = Buffer.alloc(line * height);
      header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2;
      for (let y = 0; y < height; y++) {
        for (let x = 0; x < width; x++) {
          const column = (x - 42) % Math.floor(670 / columns), row = (y - 100) % 86;
          const seat = x >= 42 && x < 710 && y >= 100 && y < 420 && column < 670 / columns - 24 && row < 59;
          const podium = y >= 26 && y < 64 && x >= 240 && x < 510;
          const pixel = (seat || podium) ? ink : paper;
          const index = y * line + 1 + x * 3;
          raw[index] = pixel[0]; raw[index + 1] = pixel[1]; raw[index + 2] = pixel[2];
        }
      }
      return Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header),
        chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
    }
    await Promise.all([writeFile(image1, sample(5, [49, 105, 170], [226, 237, 248])),
      writeFile(image2, sample(6, [36, 126, 94], [226, 244, 234]))]);
    await writeFile(desktopPath, JSON.stringify({origin: relayUrl, schoolCode: school.code, schoolId: school.id, password: pin,
      gradeId: grade.id, sevenId: seven.id, eightId: eight.id, nineId: nine.id, lastId: tail.id, date, image1, image2, allowStudyTestWrites: true}),
    {mode: 0o600});
    await runDesktop(['--live-school-study', desktopPath, join(evidence, 'console')]);
    await native;
    assert.deepEqual(checkpoints, ['web-change', 'stale', 'schedule-applied', 'start-native', 'old-in-use', 'allow-close',
      'new-in-use', 'withdrawn', 'restored', 'restored-inheritance']);
    await writeFile(join(evidence, 'cross-end-result.json'), JSON.stringify({passed: true, checkpoints, native: [...marks],
      date, room: seven.id, front: front.binding.id, rear: back.binding.id, studyReceiptReads, seatingReceiptReads}, null, 2));
    console.log('PASS: actual Windows Console + Edge legacy editing + PostgreSQL + NPEduTools Host and WPF seating observation.');
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    if (native) await native.catch(() => {});
    try { await (await legacy).close(); } catch {}
  }
}

async function prepareLegacyStudyWeb({web, directory, evidence, origin, token, school, term, room}) {
  const require = createRequire(join(web, 'package.json'));
  const {chromium} = require('@playwright/test');
  const {preview} = await import(pathToFileURL(join(dirname(require.resolve('vite/package.json')), 'dist/node/index.js')).href);
  const outDir = join(directory, 'school-study-web-build');
  const frontend = await preview({configFile: false, root: web, build: {outDir}, preview: {
    host: '127.0.0.1', port: 0, proxy: {'/api': origin, '/accounts': origin}
  }});
  const url = 'http://127.0.0.1:' + frontend.httpServer.address().port;
  const close = async () => { frontend.httpServer.closeAllConnections(); await frontend.close(); };
  try {
    await new Promise((resolve, reject) => {
      const child = spawn(process.execPath, ['node_modules/vite/bin/vite.js', 'build', '--logLevel', 'error', '--outDir', outDir], {
        cwd: web, env: {...process.env, VITE_DEFAULT_KV_SERVER: url, VITE_DEFAULT_AUTH_SERVER: url, VITE_SERVER_URL: url,
          VITE_ENABLE_ANALYTICS: 'false'}, windowsHide: true, stdio: 'inherit'
      });
      child.once('error', reject);
      child.once('exit', code => code === 0 ? resolve() : reject(new Error('Legacy web build failed.')));
    });
    return {close, edit: async () => {
      const browser = await chromium.launch({channel: 'msedge', headless: true});
      const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
      try {
        await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
        await page.addInitScript(({access_token, refresh_token}) => {
          localStorage.setItem('classworks-v2-access-token', access_token);
          localStorage.setItem('classworks-v2-refresh-token', refresh_token);
        }, token);
        await page.goto(url + '/classworks-admin?section=npep-study&school=' + school.id + '&term=' + term.id);
        const pane = page.locator('.study-settings');
        await pane.getByRole('heading', {name: '学校自习设置'}).waitFor();
        async function select(label, name) {
          await pane.locator('.v-select').filter({hasText: label}).locator('.v-field').click();
          await page.getByRole('option', {name, exact: true}).click();
        }
        await select(/^自习配置范围/, '行政班');
        await select(/^自习配置对象/, room.name);
        await pane.getByLabel('自习结束 1', {exact: true}).fill('20:55');
        await pane.getByRole('button', {name: '预览自习影响'}).click();
        const saved = page.waitForResponse(res => res.url().endsWith('/study-settings') &&
          res.request().method() === 'POST' && res.ok());
        await pane.getByRole('button', {name: '保存自习设置'}).click();
        await saved;
        await page.screenshot({path: join(evidence, 'legacy-web-eight-saved.png'), animations: 'disabled'});
      } catch (error) {
        await page.screenshot({path: join(evidence, 'legacy-web-failure.png')});
        throw error;
      } finally { await browser.close(); }
    }};
  } catch (error) { await close(); throw error; }
}
