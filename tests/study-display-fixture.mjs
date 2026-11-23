import assert from 'node:assert/strict';
import {randomBytes, randomUUID} from 'node:crypto';
import {createServer} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {prepareLegacyStudyWeb} from './school-study-fixture.mjs';

export async function verifyStudyDisplay({prisma, origin, fixture, fromKv, directory, evidence, web, runDesktop, runNative}) {
  const {school, term, other, managerId, pin, identity} = fixture;
  await mkdir(evidence, {recursive: true});
  const {defaultStudySettings} = await fromKv('domain/npep/studySettings.js');
  const {studyHomeworkDate} = await fromKv('services/npepStudyHomework.js');
  const {secretHash} = await fromKv('domain/npep/wire.js');
  const {png} = await fromKv('tests/helpers/npepSeatingFixture.js');
  const day = studyHomeworkDate();
  const weekday = new Date(day + 'T00:00:00Z').getUTCDay() || 7;
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const [seven, eight, nine, ten] = await Promise.all([7, 8, 9, 10].map(number => prisma.workspace.create({data: {
    type: 'ADMIN_CLASS', name: `高二（${number}）班`, code: 'DISPLAY-' + number, gradeId: grade.id, termId: term.id,
  }})));
  const physics = await prisma.subject.create({data: {schoolId: school.id, code: 'DSP-PHY', name: '物理'}});
  const walking = await prisma.workspace.create({data: {type: 'COURSE_GROUP', termId: term.id, gradeId: grade.id,
    subjectId: physics.id, name: '物理 A1', code: 'DSP-PHY-A1', sourceClasses: {create: {administrativeClassId: eight.id}}}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: eight.id, subjectId: physics.id,
    deliveryMode: 'COURSE_GROUP', isCompulsory: true}});
  const initial = defaultStudySettings();
  initial.schedule = {...initial.schedule, effectiveFrom: day, rules: [{days: [weekday], start: '19:00', end: '20:00'}]};
  initial.examSchedule = {...initial.examSchedule, effectiveFrom: day, rules: [{days: [weekday], start: '12:10', end: '12:30'}]};
  initial.noise = {...initial.noise, enabled: false};
  const independent = structuredClone(initial);
  independent.homework.rotationSeconds = 103;
  independent.presentation.noiseDisplay = 'StatusDbfs';
  delete independent.noise.estimated;
  for (const [type, target, policy] of [
    ['GRADE', grade, {mode: 'Override', settings: initial}], ['CLASS', eight, {mode: 'Override', settings: independent}],
    ['CLASS', nine, {mode: 'Disabled', settings: null}],
  ]) await prisma.$executeRaw`INSERT INTO "NpepStudySetting" ("schoolId","termId","targetType","targetId",revision,policy,"updatedBy","updatedAt")
    VALUES (${school.id},${term.id},${type},${target.id},1,${JSON.stringify(policy)}::jsonb,${managerId},NOW())`;

  const [front, back] = await Promise.all(['七班前屏', '七班后屏'].map(async name => {
    const binding = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: seven.id,
      name, tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId}});
    const secret = randomBytes(32).toString('base64url');
    const device = await prisma.npepDevice.create({data: {...identity, id: randomUUID(), installationId: randomUUID(), credentialId: randomUUID(),
      secretHash: secretHash(secret), schoolId: school.id, administrativeClassId: seven.id, screenBindingId: binding.id,
      bindingRevision: binding.npepBindingRevision, deviceName: name, credentialExpiresAt: new Date(Date.now() + 86400000)}});
    return {binding, device, bearer: 'npep1.' + device.credentialId + '.' + secret};
  }));
  const account = async username => {
    const response = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username, password: pin})});
    assert.equal(response.status, 200);
    return (await response.json()).data;
  };
  const [admin, legacyAdmin] = await Promise.all([account('admin'), account('admin2')]);
  const root = '/api/v2/npep/schools/' + school.id;
  const studyPath = root + '/study-settings';
  const query = room => '?termId=' + term.id + '&classId=' + room.id + '&date=' + day;
  async function call(path, {token = admin.access_token, method = 'GET', version = '0.10', status = 200, body} = {}) {
    const requestId = randomUUID();
    const response = await fetch(origin + path, {method, headers: {
      Authorization: 'Bearer ' + token, 'Content-Type': 'application/json', 'X-NPEP-Version': version, 'X-Request-Id': requestId,
    }, body: body === undefined ? undefined : JSON.stringify({...body, requestId})});
    const parsed = await response.json();
    assert.equal(response.status, status, path + ' ' + parsed.error?.code);
    return parsed.data ?? parsed.error;
  }
  function publish(title, targets, content = '本班实验记录', status = 'PUBLISHED', boardDate = day) {
    return prisma.publication.create({data: {type: 'ASSIGNMENT', status, title, content, authorAccountId: managerId,
      publishAt: new Date(Date.now() - 1000), boardDate: new Date(boardDate + 'T00:00:00Z'), subjectId: physics.id,
      targets: {create: targets.map(target => ({workspaceId: target.id}))}}});
  }
  const publication = await publish('同一份实验作业', [eight, walking],
    Array.from({length: 90}, (_, index) => '第 ' + (index + 1) + ' 段：记录不同材料中的声音变化，给出一个理由。\n').join(''));
  await publish('外班不显示', [ten]);
  const seating = await call(root + '/seating', {method: 'POST', version: '0.14', body: {
    termId: term.id, classId: seven.id, expectedRevision: 0, action: 'image', image: png(10, 8).toString('base64'),
  }});
  const otherSeat = await call(root + '/seating', {method: 'POST', version: '0.14', body: {
    termId: term.id, classId: eight.id, expectedRevision: 0, action: 'image', image: png(13, 7).toString('base64'),
  }});
  assert.notEqual(seating.hash, otherSeat.hash);
  assert.equal((await call(studyPath + '/display' + query(seven))).state, 'Empty');
  assert.equal((await call(studyPath + '/display' + query(nine))).state, 'NoPolicy');
  await call('/api/v2/npep/schools/' + other.id + '/study-settings/display' + query(seven), {status: 403});

  const legacy = prepareLegacyStudyWeb({web, directory, evidence, origin, token: legacyAdmin, school, term, room: eight});
  legacy.catch(() => {});
  const marks = new Set(), cases = [];
  let native, nativeFailure, drops = 0, receiptReads = 0, requestId, independentPreview, relayOrigin;
  let holdFirstDisplay = true, holdFirstReceipt = true;
  const holdDisplay = Promise.withResolvers(), cancelDisplay = Promise.withResolvers();
  const holdWrite = Promise.withResolvers(), loseWriteReply = Promise.withResolvers();
  const holdReceipt = Promise.withResolvers(), cancelReceipt = Promise.withResolvers();
  const receiptAttempts = [], forwardedDisplayWrites = [];
  const revisions = async () => prisma.$queryRaw`SELECT "targetId", revision, policy FROM "NpepStudySetting" WHERE "schoolId"=${school.id}`;
  async function updateGrade(change, token = admin.access_token) {
    const current = (await revisions()).find(row => row.targetId === grade.id);
    const policy = structuredClone(current.policy);
    change(policy.settings.homework);
    const body = {termId: term.id, targetType: 'GRADE', targetId: grade.id, expectedRevision: current.revision, policy};
    const preview = await call(studyPath + '/preview', {token, method: 'POST', body});
    const saved = await call(studyPath, {token, method: 'POST', body: {...body, expectedPreviewFingerprint: preview.previewFingerprint}});
    return {preview, saved};
  }
  async function waitFor(mark) {
    for (let i = 0; i < 240; i++) {
      if (nativeFailure) throw nativeFailure;
      if (marks.has(mark)) return;
      await delay(300);
    }
    throw new Error('No native proof of ' + mark);
  }
  async function legacyChange() {
    await (await legacy).withPage(async page => {
      const pane = page.locator('.study-settings');
      await pane.getByRole('heading', {name: '学校自习设置'}).waitFor();
      async function choose(label, labelValue) {
        await pane.locator('.v-select').filter({hasText: label}).locator('.v-field__append-inner').click();
        await page.getByRole('option', {name: labelValue, exact: true}).click();
      }
      await choose(/^自习配置范围/, '年级');
      await choose(/^自习配置对象/, grade.name);
      await choose(/^作业来源方式/, '指定来源行政班（自动包含其相关走班）');
      await choose(/^展示作业的来源行政班/, ten.name);
      await pane.getByRole('heading', {name: '3. 展示与当天作业'}).click();
      await pane.getByRole('button', {name: '预览自习影响'}).click();
      const save = pane.getByRole('button', {name: '保存自习设置'});
      await Promise.all([page.waitForResponse(reply => reply.url().endsWith(studyPath) && reply.request().method() === 'POST' && reply.ok()),
        save.click()]);
      await page.screenshot({path: join(evidence, 'edge-legacy-source-changed.png'), animations: 'disabled'});
    });
  }

  async function checkpoint(name) {
    if (name === 'display-read-held') await holdDisplay.promise;
    else if (name === 'display-read-cancelled') {
      await cancelDisplay.promise;
      assert.deepEqual(forwardedDisplayWrites, []);
      assert.equal((await revisions()).find(row => row.targetId === grade.id).revision, 1);
    } else if (name === 'legacy') await legacyChange();
    else if (name === 'stale') {
      const gradeNow = (await revisions()).find(row => row.targetId === grade.id);
      assert.deepEqual([gradeNow.revision, gradeNow.policy.settings.homework.sourceClassIds, drops], [2, [ten.id], 0]);
    } else if (name === 'display-write-held') {
      await holdWrite.promise;
      assert.equal((await revisions()).find(row => row.targetId === grade.id).revision, 3);
    } else if (name === 'display-lose-save-reply') loseWriteReply.resolve();
    else if (name === 'display-receipt-held') await holdReceipt.promise;
    else if (name === 'display-receipt-cancelled') await cancelReceipt.promise;
    else if (name === 'saved') {
      assert.deepEqual([drops, receiptReads], [1, 1]);
      assert.deepEqual(receiptAttempts, [requestId, requestId]);
      assert.equal(forwardedDisplayWrites.filter(id => id === requestId).length, 1);
      const planned = (await revisions()).find(row => row.targetId === grade.id);
      assert.equal(planned.revision, 3);
      assert.deepEqual(planned.policy.settings.homework.sourceClassIds, [eight.id]);
      assert.equal(planned.policy.settings.homework.rotationSeconds, 30);
      for (const key of ['schedule', 'examSchedule', 'noise', 'photos']) assert.deepEqual(planned.policy.settings[key], initial[key]);
      const state = await call(studyPath + '/display' + query(seven));
      assert.equal(state.state, 'Available');
      assert.deepEqual(state.items.map(item => item.id), [publication.id]);
      assert.deepEqual(state.items[0].sources.map(source => source.id).sort(), [walking.id, eight.id].sort());
      assert.equal((await call(studyPath + '/display' + query(eight))).homework.rotationSeconds, 103);
      assert.equal((await call(studyPath + '/display' + query(nine))).source, 'Disabled');
      const config = join(directory, 'native-display.json');
      await writeFile(config, JSON.stringify({isolated: true, origin: relayOrigin, deployment: identity, classId: seven.id,
        date: day, bearer: front.bearer, homeworkAcceptance: true, seatingHash: seating.hash}), {mode: 0o600});
      native = runNative([join(evidence, 'native'), '--live-school-study-seating', config]);
      native.catch(error => { nativeFailure = error; });
      await waitFor('presentation');
    } else if (name === 'checked') {
      marks.add('display-checked');
      assert.equal((await updateGrade(homework => { homework.rotationSeconds = 60; })).saved.revision, 4);
    } else if (name === 'independent-basis') {
      assert.equal((await updateGrade(homework => { homework.sourceClassIds = [ten.id]; }, legacyAdmin.access_token)).saved.revision, 5);
    } else if (name === 'independent-stale') {
      assert.ok(independentPreview, 'KV actually rejected the stale first preview');
      assert.ok(!(await revisions()).some(row => row.targetId === seven.id), 'the stale first preview did not write a class override');
      await call(`${studyPath}/requests/${independentPreview}`, {status: 404});
      const latest = await call(studyPath + '/display' + query(seven));
      assert.deepEqual([latest.source, latest.homework.sourceClassIds, latest.homework.rotationSeconds], ['Grade', [ten.id], 60]);
    }
    else if (name === 'independent') {
      const rows = await revisions();
      const current = rows.find(row => row.targetId === seven.id);
      assert.equal(current.policy.mode, 'Override');
      for (const key of ['schedule', 'examSchedule', 'noise', 'photos']) assert.deepEqual(current.policy.settings[key], initial[key]);
      assert.deepEqual([current.policy.settings.homework.sourceClassIds, current.policy.settings.homework.rotationSeconds], [[ten.id], 30]);
      const {preview, saved} = await updateGrade(homework => { homework.rotationSeconds = 52; });
      assert.equal(preview.changedClasses, 1);
      assert.equal(saved.revision, 6);
      assert.equal((await call(studyPath + '/display' + query(ten))).homework.rotationSeconds, 52);
      assert.equal((await call(studyPath + '/display' + query(eight))).homework.rotationSeconds, 103);
      const after = await call(studyPath + '/display' + query(seven));
      assert.deepEqual([after.source, after.homework.sourceClassIds, after.homework.rotationSeconds], ['Class', [ten.id], 30]);
    } else throw new Error('Unexpected fixture checkpoint ' + name);
    cases.push(name);
  }

  const relay = createServer(async (req, res) => {
    try {
      const url = new URL(req.url, origin);
      if (url.pathname.startsWith('/__display/console/')) {
        await checkpoint(url.pathname.split('/').at(-1));
        return res.writeHead(204).end();
      }
      if (url.pathname.startsWith('/__study/native/')) {
        const mark = url.pathname.split('/').at(-1);
        if (req.method === 'GET') return res.writeHead(marks.has(mark) ? 204 : 404).end();
        if (mark === 'append-homework') await publish('同来源新作业', [eight]);
        marks.add(mark);
        return res.writeHead(204).end();
      }
      if (holdFirstDisplay && req.method === 'GET' && url.pathname + url.search === studyPath + '/display' + query(seven)) {
        holdFirstDisplay = false;
        res.once('close', () => cancelDisplay.resolve());
        holdDisplay.resolve();
        return;
      }
      if (requestId && url.pathname === `${studyPath}/requests/${requestId}` && req.method === 'GET') {
        receiptAttempts.push(requestId);
        if (holdFirstReceipt) {
          holdFirstReceipt = false;
          res.once('close', () => cancelReceipt.resolve());
          holdReceipt.resolve();
          return;
        }
      }
      const chunks = [];
      for await (const part of req) chunks.push(part);
      const body = chunks.length ? Buffer.concat(chunks) : undefined;
      const headers = {...req.headers};
      for (const key of ['connection', 'host', 'transfer-encoding', 'content-length']) delete headers[key];
      const upstream = await fetch(url, {method: req.method, headers, body, redirect: 'manual'});
      const response = Buffer.from(await upstream.arrayBuffer());
      if (url.pathname === studyPath + '/preview' && req.method === 'POST' && upstream.status === 409 &&
          JSON.parse(response).error?.code === 'STUDY_DISPLAY_BASE_CHANGED') independentPreview = JSON.parse(body).requestId;
      if (url.pathname === studyPath && req.method === 'POST' && upstream.ok && JSON.parse(body).displayEdit) {
        const id = JSON.parse(body).requestId;
        forwardedDisplayWrites.push(id);
        if (!drops) {
          requestId = id;
          drops++;
          holdWrite.resolve();
          await loseWriteReply.promise;
          return res.writeHead(502).end();
        }
      }
      if (requestId && url.pathname === `${studyPath}/requests/${requestId}` && upstream.ok) receiptReads++;
      const clean = Object.fromEntries(upstream.headers);
      for (const key of ['connection', 'transfer-encoding', 'content-encoding', 'content-length']) delete clean[key];
      res.writeHead(upstream.status, clean).end(response);
    } catch (error) { console.error(error.message); res.writeHead(500).end(error.message); }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  relayOrigin = 'http://127.0.0.1:' + relay.address().port;
  const access = join(directory, 'display-desktop.json');
  try {
    await writeFile(access, JSON.stringify({origin: relayOrigin, schoolId: school.id, schoolCode: school.code, password: pin,
      date: day, gradeId: grade.id, sevenId: seven.id, eightId: eight.id, nineId: nine.id, allowDisplayWrites: true}), {mode: 0o600});
    await runDesktop(['--live-study-display', access, join(evidence, 'console')]);
    await native;
    assert.deepEqual(cases, ['display-read-held', 'display-read-cancelled', 'legacy', 'stale', 'display-write-held',
      'display-lose-save-reply', 'display-receipt-held', 'display-receipt-cancelled', 'saved', 'checked',
      'independent-basis', 'independent-stale', 'independent']);
    await writeFile(join(evidence, 'study-display-result.json'), JSON.stringify({result: 'PASS', cases, marks: [...marks],
      drops, receiptReads, receiptAttempts, forwardedDisplayWrites, schools: [school.id, other.id], front: front.binding.id, rear: back.binding.id,
      publicationId: publication.id}, null, 2));
  } finally {
    loseWriteReply.resolve();
    marks.add('display-checked');
    if (native) await native.catch(() => {});
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
    await (await legacy).close();
  }
}
