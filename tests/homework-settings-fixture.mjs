import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

const label = '推导例题';
const text = '完成课本物理例题推导';
const deadlineLabel = '下周一 07:30';
const monday = '2026-11-23T09:00:00+08:00';

export async function verifyHomeworkSettings({prisma, origin, fixture, directory, evidence, web, runDesktop}) {
  const {school, other, term, pin, managerId, classes} = fixture;
  await mkdir(evidence, {recursive: true});
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
  const roomId = classes[0];
  const [physics, chinese] = await Promise.all([
    prisma.subject.create({data: {schoolId: school.id, name: '物理', code: 'PHYS'}}),
    prisma.subject.create({data: {schoolId: school.id, name: '语文', code: 'CHN'}})
  ]);
  const teacher = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer', schoolMemberships: {some: {schoolId: school.id}}}});
  await prisma.workspaceMember.create({data: {workspaceId: roomId, accountId: teacher.id, role: 'TEACHER'}});
  for (const subject of [physics, chinese]) {
    await prisma.administrativeClassSubject.create({data: {administrativeClassId: roomId, subjectId: subject.id, deliveryMode: 'ADMIN_CLASS'}});
    await prisma.teachingAssignment.create({data: {workspaceId: roomId, subjectId: subject.id, accountId: teacher.id}});
  }
  const published = await prisma.publication.create({data: {type: 'ASSIGNMENT', status: 'PUBLISHED',
    authorAccountId: teacher.id, subjectId: physics.id, title: '原来已发布的物理作业', content: '原正文不变',
    boardDate: new Date('2026-11-23T00:00:00Z'), dueAt: new Date('2026-11-24T00:00:00Z'),
    targets: {create: {workspaceId: roomId}}}});
  const path = id => '/api/v2/admin/schools/' + id + '/homework-settings';
  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {...(token ? {Authorization: 'Bearer ' + token} : {}),
      ...(body ? {'Content-Type': 'application/json'} : {})}, ...(body ? {body: JSON.stringify(body)} : {})});
    const result = await response.json();
    assert.equal(response.status, status, path + ' ' + (result.code ?? result.message ?? ''));
    return response.ok ? result.data ?? result : result;
  }
  const login = username => api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username, password: pin}});
  const [admin, owner] = await Promise.all([login('admin2'), login('admin')]);
  const snapshot = id => api(path(id), {token: (id === other.id ? owner : admin).access_token});
  const initial = await snapshot(school.id);
  await api('/api/v2/admin/schools/' + school.id + '/classroom-screen-accounts', {method: 'POST', token: admin.access_token, status: 201,
    body: {administrativeClassId: roomId, name: '作业验收大屏', loginCode: 'homework-screen', pin}});
  const checks = [];
  let webPage, screenPage, teacherPage, edgeOrigin, releaseRead, readDelayed;
  let dropped = null, saveRequests = 0, receiptReads = 0, delayRead = false, allowReceipt = false;
  const receiptPaths = new Set();
  let conflictAttempt;
  const check = name => { checks.push(name); console.log('PASS homework settings: ' + name); };

  async function checkpoint(name) {
    switch (name) {
      case 'web-deadline': {
        const saved = webPage.waitForResponse(res => res.request().method() === 'PUT' && new URL(res.url()).pathname === path(school.id));
        const fields = webPage.locator('.quick-deadline-row__label input');
        await fields.last().fill(deadlineLabel);
        await webPage.getByRole('button', {name: '保存截止时间', exact: true}).click();
        const response = await saved;
        assert.ok(response.ok());
        const sent = response.request().postDataJSON();
        assert.deepEqual(sent.expectedVersions, {quickDeadlines: 0});
        assert.equal(Object.hasOwn(sent, 'quickInputs'), false);
        await webPage.screenshot({path: join(evidence, 'edge-admin-deadline.png')});
        check('Edge independently saved the deadline partition');
        break;
      }
      case 'allow-receipt':
        assert.ok(dropped && receiptReads === 1 && saveRequests === 1);
        allowReceipt = true;
        break;
      case 'receipt': {
        const latest = await snapshot(school.id);
        assert.deepEqual(latest.versions, {quickInputs: 1, quickDeadlines: 1});
        assert.deepEqual(latest.quickInputs.find(row => row.label === label)?.subjectIds, [physics.id]);
        assert.equal(latest.quickDeadlines.at(-1).label, deadlineLabel);
        assert.ok(dropped && receiptReads === 2 && saveRequests === 1);
        assert.deepEqual([...receiptPaths], [path(school.id) + '/receipts/' + dropped]);
        const failure = await webPage.evaluate(async ({path, initial}) => {
          const token = localStorage.getItem('classworks-v2-access-token');
          const response = await fetch(path, {method: 'PUT', headers: {'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'},
            body: JSON.stringify({quickInputs: initial.quickInputs, quickDeadlines: initial.quickDeadlines})});
          return {status: response.status, result: (await response.json()).code};
        }, {path: path(school.id), initial});
        assert.deepEqual(failure, {status: 428, result: 'HOMEWORK_SETTINGS_BASELINE_REQUIRED'});
        assert.deepEqual(await snapshot(school.id), latest);
        check('lost Console reply found the exact request and a cached full PUT was rejected');
        break;
      }
      case 'same-zone': {
        const latest = await snapshot(school.id);
        const quickInputs = latest.quickInputs.map((row, index) => index === 0 ? {...row, text: '学校另一次编辑'} : row);
        await api(path(school.id), {method: 'PUT', token: admin.access_token,
          body: {requestId: randomUUID(), expectedVersions: {quickInputs: latest.versions.quickInputs}, quickInputs}});
        break;
      }
      case 'zero-write': {
        const latest = await snapshot(school.id);
        assert.deepEqual(conflictAttempt.expectedVersions, {quickInputs: 1, quickDeadlines: 1});
        assert.equal(conflictAttempt.quickInputs[0].text, '本地尚未保存的正文');
        assert.equal(conflictAttempt.quickDeadlines[0].label, '本地未提交的时间');
        assert.deepEqual(latest.versions, {quickInputs: 2, quickDeadlines: 1});
        assert.equal(latest.quickInputs[0].text, '学校另一次编辑');
        assert.equal(latest.quickDeadlines[0].label, initial.quickDeadlines[0].label);
        check('same-section conflict blocked both changed sections atomically');
        break;
      }
      case 'reviewed-no-write': {
        const latest = await snapshot(school.id);
        assert.equal(saveRequests, 2);
        assert.deepEqual(latest.versions, {quickInputs: 2, quickDeadlines: 1});
        check('review and refused school switch both preserved drafts without writing');
        break;
      }
      case 'saved-after-review': {
        const latest = await snapshot(school.id);
        assert.equal(saveRequests, 3);
        assert.deepEqual(latest.versions, {quickInputs: 3, quickDeadlines: 2});
        assert.equal(latest.quickInputs[0].text, '本地尚未保存的正文');
        assert.equal(latest.quickDeadlines[0].label, '本地未提交的时间');
        check('explicit save committed both drafts after reviewing the changed partition');
        delayRead = true;
        break;
      }
      case 'wait-old-read':
        await readDelayed;
        break;
      case 'allow-old-read': {
        releaseRead();
        check('a pending school read was released only after the Console switched schools');
        break;
      }
      case 'no-term': {
        assert.equal(await prisma.academicTerm.count({where: {schoolId: other.id}}), 0);
        assert.equal((await snapshot(other.id)).quickDeadlines[0].label, '无学期也可设置');
        assert.equal((await snapshot(school.id)).quickDeadlines[0].label, '本地未提交的时间');
        check('school without an active term saved its own settings only');
        break;
      }
      default: assert.fail('Unknown homework settings checkpoint');
    }
  }

  let confirmReadDelayed;
  readDelayed = new Promise(resolve => { confirmReadDelayed = resolve; });
  const relay = createServer((req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__homework/')) {
      checkpoint(req.url.slice('/__homework/'.length)).then(() => res.writeHead(204).end(), error => {
        console.error(error); res.writeHead(500).end();
      });
      return;
    }
    if (req.method === 'GET' && req.url.startsWith(path(school.id) + '/receipts/')) {
      receiptReads++;
      receiptPaths.add(req.url);
      if (!allowReceipt) { res.writeHead(503).end(); return; }
    }
    const target = new URL(req.url, origin);
    if (req.method === 'PUT' && req.url === path(school.id) && saveRequests === 1) {
      const parts = [];
      req.on('data', data => parts.push(data));
      req.on('end', () => { conflictAttempt = JSON.parse(Buffer.concat(parts)); });
    }
    const upstream = request(target, {method: req.method, headers: {...req.headers, host: target.host}}, reply => {
      if (req.method === 'PUT' && req.url === path(school.id) && ++saveRequests === 1 && reply.statusCode === 200) {
        const parts = [];
        reply.on('data', data => parts.push(data));
        reply.on('end', () => {
          dropped = JSON.parse(Buffer.concat(parts)).data.requestId;
          res.destroy();
        });
        return;
      }
      const forward = () => { if (!res.destroyed) { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); } else reply.resume(); };
      if (delayRead && req.method === 'GET' && req.url === path(school.id)) {
        delayRead = false;
        releaseRead = forward;
        confirmReadDelayed();
      } else forward();
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((ok, fail) => relay.once('error', fail).listen(0, '127.0.0.1', ok));

  try {
    const access = join(directory, 'homework-settings-desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      otherSchoolId: other.id, schoolCode: school.code, pin, physicsId: physics.id, allowWrites: true}), {mode: 0o600});
    await verifyStudentBrowser({legacyWebManagement: true, origin, web, directory, evidence, school, rooms: [], afterStudent: async (edge, site) => {
      edgeOrigin = site;
      const authenticated = await edge.newContext({viewport: {width: 1360, height: 920}, serviceWorkers: 'block',
        storageState: {cookies: [], origins: [{origin: site, localStorage: [
          {name: 'classworks-v2-access-token', value: admin.access_token},
          {name: 'classworks-v2-refresh-token', value: admin.refresh_token},
          {name: 'classworks-v2-oobe', value: JSON.stringify({version: 1, completed: true, roleHint: 'teacher'})}
        ]}]}});
      webPage = await localPage(authenticated, site);
      const screen = await edge.newContext({viewport: {width: 1360, height: 920}, serviceWorkers: 'block', timezoneId: 'Asia/Shanghai'});
      screenPage = await localPage(screen, site);
      await screenPage.clock.setFixedTime(new Date(monday));
      const teacherContext = await edge.newContext({viewport: {width: 1360, height: 920}, serviceWorkers: 'block'});
      teacherPage = await localPage(teacherContext, site);
      try {
        await webPage.goto(site + '/classworks-admin?section=screens&school=' + school.id + '&term=' + term.id);
        await webPage.getByRole('button', {name: '保存截止时间', exact: true}).waitFor();
        await loginScreen();
        await runDesktop(['--live-homework-settings', access, evidence]);
        await assertScreen();
        await assertTeacher();
      } catch (error) {
        for (const [page, name] of [[webPage, 'admin'], [screenPage, 'screen'], [teacherPage, 'teacher']])
          await page.screenshot({path: join(evidence, 'edge-failure-' + name + '.png')}).catch(() => {});
        throw error;
      }
    }});
    assert.deepEqual(await prisma.publication.findUniqueOrThrow({where: {id: published.id}}), published);
    check('published homework retains its original body, deadline and revision');
    await writeFile(join(evidence, 'homework-settings-cross-end.json'), JSON.stringify({passed: true, checks, lostRequest: dropped,
      receiptReads, screenshotViewport: [[1160, 780], [840, 480]], monday, resolvedDeadline: '2026-11-30T07:30'}, null, 2));
  } finally {
    releaseRead?.();
    relay.closeAllConnections();
    await new Promise(ok => relay.close(ok));
  }

  async function localPage(context, site) {
    const page = await context.newPage();
    await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
    return page;
  }

  async function loginScreen() {
    await screenPage.goto(edgeOrigin, {waitUntil: 'domcontentloaded'});
    await screenPage.getByRole('button', {name: /激活大屏/}).click();
    const form = screenPage.locator('.screen-login-card');
    await form.waitFor();
    if (!await form.locator('.v-select__selection-text').filter({hasText: school.name}).count()) {
      await form.locator('.v-select .v-field').click();
      await screenPage.getByRole('option', {name: school.name, exact: true}).click();
    }
    await form.getByLabel('大屏账号').fill('homework-screen');
    await form.getByLabel('大屏 PIN').fill(pin);
    await form.getByRole('button', {name: '登录并进入大屏'}).click();
    await screenPage.getByRole('button', {name: '完成并进入大屏', exact: true}).click();
    await screenPage.getByRole('button', {name: '录入作业', exact: true}).waitFor();
  }

  async function assertScreen() {
    await screenPage.reload();
    await screenPage.getByRole('button', {name: '录入作业', exact: true}).click();
    const panel = screenPage.locator('.screen-composer');
    await panel.getByRole('button', {name: '物理', exact: true}).click();
    await panel.getByRole('button', {name: label, exact: true}).click();
    assert.equal(await panel.locator('.screen-content-input textarea:not([aria-hidden])').first().inputValue(), text);
    await panel.getByRole('button', {name: deadlineLabel, exact: true}).click();
    await panel.getByText(/更多设置/).click();
    assert.equal(await panel.locator('input[type="datetime-local"]').inputValue(), '2026-11-30T07:30');
    await screenPage.screenshot({path: join(evidence, 'edge-screen-monday-deadline.png')});
    check('real Edge screen inserted the scoped word and resolved a Monday deadline to the next Monday');
  }

  async function assertTeacher() {
    await teacherPage.goto(edgeOrigin, {waitUntil: 'domcontentloaded'});
    await teacherPage.getByRole('button', {name: /教师/}).first().click();
    const schoolChoice = teacherPage.locator('.v-select').filter({hasText: /^学校/});
    if (!await schoolChoice.locator('.v-select__selection-text').filter({hasText: school.name}).count()) {
      await schoolChoice.locator('.v-field').first().click();
      await teacherPage.getByRole('option', {name: school.name, exact: true}).click();
    }
    await teacherPage.getByLabel('教师短账号').fill('viewer');
    await teacherPage.getByLabel('个人 PIN').fill(pin);
    await teacherPage.getByRole('button', {name: '登录', exact: true}).click();
    const subject = teacherPage.locator('.v-select').filter({hasText: /^科目/}).first();
    await subject.locator('.v-field').click();
    await teacherPage.getByRole('option', {name: '物理', exact: true}).click();
    const quick = teacherPage.locator('.homework-quick-input').first();
    await quick.getByRole('button', {name: label, exact: true}).click();
    assert.equal(await teacherPage.getByLabel('正文', {exact: true}).inputValue(), text);
    await teacherPage.screenshot({path: join(evidence, 'edge-teacher-physics-word.png')});
    await subject.locator('.v-field').click();
    await teacherPage.getByRole('option', {name: '语文', exact: true}).click();
    assert.equal(await quick.getByRole('button', {name: label, exact: true}).count(), 0);
    check('real teacher composer inserted the word for physics and hid it for Chinese');
  }
}
