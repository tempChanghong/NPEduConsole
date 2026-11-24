import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyScopedTermTeaching({prisma, origin, fixture, directory, web, evidence, bcrypt, localProviderId, runDesktop}) {
  const {school, term: autumn, managerId, pin} = fixture;
  await mkdir(evidence, {recursive: true});
  const spring = await term(2026, 2, '春季师资草稿', 'DRAFT');
  const archived = await term(2025, 2, '归档师资学期', 'ARCHIVED');
  const autumnGrade = await prisma.grade.findFirstOrThrow({where: {termId: autumn.id, code: 'G2'}});
  const [grade, archiveGrade] = await Promise.all([spring, archived].map(row =>
    prisma.grade.create({data: {termId: row.id, name: '高二', code: 'G2'}})));
  const [fall7, seven, eight, nine, archive7] = await Promise.all([autumnGrade, grade, grade, grade, archiveGrade]
    .map((item, i) => classroom(item, i === 2 ? 8 : i === 3 ? 9 : 7)));
  const [physics, chinese] = await Promise.all([['PHY','物理'],['CHN','语文']].map(([code, name]) =>
    prisma.subject.create({data: {schoolId: school.id, code, name}})));
  await putRule(fall7.id, chinese.id, 'ADMIN_CLASS', true);
  await putRule(seven.id, chinese.id, 'ADMIN_CLASS', true);
  await putRule(nine.id, physics.id, 'COURSE_GROUP', false);
  await putRule(archive7.id, chinese.id, 'ADMIN_CLASS', true);
  await putRule(archive7.id, physics.id, 'COURSE_GROUP', false);
  const passwordHash = await bcrypt.hash(pin, 10);
  const [outgoing, incoming, primary, independent] = await Promise.all([
    ['oldhead', '卸任王老师'], ['newhead', '接任李老师'], ['physics', '物理主讲陈老师'], ['archiveonly', '归档独立成员']
  ].map(async ([username, name]) => {
    const account = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(school.code, username),
      name, localUsername: username, localPasswordHash: passwordHash, providerData: {schoolCode: school.code}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: account.id, role: 'VIEWER'}});
    return account;
  }));
  await prisma.administrativeClassLeadership.create({data: {
    administrativeClassId: eight.id, accountId: outgoing.id, position: 'HEAD_TEACHER'
  }});
  const history = await prisma.workspace.create({data: {type: 'COURSE_GROUP', termId: archived.id, gradeId: archiveGrade.id,
    subjectId: physics.id, name: '归档物理旧班', code: 'ARCH-PHY'}});
  await prisma.workspaceSourceClass.create({data: {workspaceId: history.id, administrativeClassId: archive7.id}});
  await prisma.workspaceMember.create({data: {workspaceId: history.id, accountId: independent.id, role: 'TEACHER'}});
  const publication = await prisma.publication.create({data: {type: 'ASSIGNMENT', title: '归档原作业', content: '样例',
    status: 'PUBLISHED', subjectId: physics.id, authorAccountId: managerId, targets: {create: {workspaceId: history.id}}}});
  const planPath = '/api/v2/admin/schools/' + school.id + '/academic-terms/' + spring.id + '/teaching/grade-plans';
  const staffPath = '/api/v2/admin/schools/' + school.id + '/academic-terms/' + spring.id + '/teaching/staff-responsibilities/classes/' + eight.id;
  const admin = await login('admin');
  const cases = [];
  let browser, site, planRequest, planReads = 0, staffDropped = false, staffReads = 0;

  function term(academicYear, semester, name, status) {
    return prisma.academicTerm.create({data: {schoolId: school.id, academicYear, semester, name, status}});
  }
  function classroom(item, number) {
    return prisma.workspace.create({data: {termId: item.termId, gradeId: item.id, name: `高二（${number}）班`,
      code: `SCOPED-${number}`, type: 'ADMIN_CLASS'}});
  }
  function putRule(administrativeClassId, subjectId, deliveryMode, isCompulsory) {
    return prisma.administrativeClassSubject.create({data: {administrativeClassId, subjectId, deliveryMode, isCompulsory}});
  }
  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const data = await response.json();
    assert.equal(response.status, status, `${path} ${data.code ?? ''}`);
    return data.data ?? data;
  }
  function login(username) {
    return api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username, password: pin}});
  }
  async function access(account, termId) {
    const data = await api('/api/v2/me/workspaces' + (termId ? '?termId=' + termId : ''), {token: account.access_token});
    return Object.fromEntries(data.map(row => [row.workspace.id, {role: row.role, responsibility: row.responsibilityDerived === true}]));
  }
  const group = () => prisma.workspace.findFirstOrThrow({where: {termId: spring.id, code: 'SPRING-A1'}});
  const sources = id => prisma.workspaceSourceClass.findMany({where: {workspaceId: id},
    select: {administrativeClassId: true}}).then(rows => rows.map(row => row.administrativeClassId).sort());
  const rule = (room, subject) => prisma.administrativeClassSubject.findUniqueOrThrow({
    where: {administrativeClassId_subjectId: {administrativeClassId: room.id, subjectId: subject.id}}});

  async function webPage(action) {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
    try { await action(page); }
    catch (error) {
      await page.screenshot({path: join(evidence, 'edge-failure.png'), animations: 'disabled'}).catch(() => {});
      throw error;
    } finally { await page.close(); }
  }
  async function student(expectedTerm, room, groupName, evidenceName) {
    await webPage(async page => {
      await page.goto(site);
      await page.getByRole('button', {name: /开始选班/}).click();
      const dialog = page.getByRole('dialog').filter({hasText: '选择我的班级'});
      const choose = async (label, option) => {
        await dialog.locator('.v-select').filter({hasText: label}).locator('.v-field').click();
        await page.getByRole('option', {name: option, exact: true}).click();
      };
      const selected = page.waitForResponse(res => res.url().includes('/catalog/terms/current?') && res.ok());
      await choose(/^学校/, school.name);
      assert.equal((await (await selected).json()).data.id, expectedTerm.id);
      await choose(/^行政班/, room.name);
      if (groupName) await choose(/物理/, groupName);
      await Promise.all([page.waitForResponse(res => res.url().includes('/administrative-classes/' + room.id + '/student-selection/validate') &&
        res.request().method() === 'POST' && res.ok()), dialog.getByRole('button', {name: '保存并查看作业'}).click()]);
      await dialog.waitFor({state: 'hidden'});
      await page.screenshot({path: join(evidence, evidenceName + '.png'), animations: 'disabled'});
    });
  }
  async function teacher(username, ids, evidenceName, readOnlyIds = []) {
    await webPage(async page => {
      await page.goto(site);
      await page.getByText('教师登录', {exact: true}).click();
      await page.getByText('登录教师工作台', {exact: true}).waitFor();
      await page.locator('.v-select').filter({hasText: '学校'}).locator('.v-field').click();
      await page.getByRole('option', {name: new RegExp(school.name)}).click();
      await page.getByLabel('教师短账号').fill(username);
      await page.getByLabel('个人 PIN').fill(pin);
      const observed = page.waitForResponse(res => new URL(res.url()).pathname === '/api/v2/me/workspaces' && res.ok());
      await page.getByRole('button', {name: '登录', exact: true}).click();
      const listed = (await (await observed).json()).data;
      assert.deepEqual(listed.map(row => row.workspace.id).sort(), ids.slice().sort());
      assert.deepEqual(listed.filter(row => row.role === 'VIEWER').map(row => row.workspace.id).sort(), readOnlyIds.slice().sort());
      await page.getByText(`已授权 ${ids.length - readOnlyIds.length} 个教学空间`, {exact: true}).waitFor();
      await page.screenshot({path: join(evidence, evidenceName + '.png'), animations: 'disabled'});
    });
  }
  async function legacySources() {
    const current = await group();
    await webPage(async page => {
      await page.addInitScript(({access, refresh}) => {
        localStorage.setItem('classworks-v2-access-token', access);
        localStorage.setItem('classworks-v2-refresh-token', refresh);
      }, {access: admin.access_token, refresh: admin.refresh_token});
      await page.goto(site + '/classworks-admin?section=structure&school=' + school.id + '&term=' + spring.id);
      await page.getByRole('tab', {name: '走班教学班与来源'}).click();
      const panel = page.locator('.v-expansion-panel').filter({hasText: current.code});
      await panel.getByRole('button', {name: new RegExp(current.name)}).click();
      await panel.getByRole('button', {name: '编辑', exact: true}).click();
      const editor = page.getByRole('dialog').filter({hasText: '编辑走班教学班'});
      await editor.locator('.v-select').filter({hasText: '来源行政班'}).locator('.v-field').click();
      await page.getByRole('option', {name: `${nine.name} · ${nine.code}`, exact: true}).click();
      await editor.getByText('编辑走班教学班', {exact: true}).click();
      await Promise.all([page.waitForResponse(res => res.url().endsWith('/course-groups/' + current.id)
        && res.request().method() === 'PATCH' && res.ok()), editor.getByRole('button', {name: '保存', exact: true}).click()]);
      await editor.waitFor({state: 'hidden'});
      await page.screenshot({path: join(evidence, 'edge-edited-spring-sources.png'), animations: 'disabled'});
    });
  }

  async function checkpoint(step) {
    switch (step) {
      case 'before': {
        await student(autumn, fall7, null, 'edge-autumn-student');
        await teacher(outgoing.localUsername, [], 'edge-autumn-teacher');
        const roles = await access(await login(outgoing.localUsername), spring.id);
        assert.equal(roles[eight.id]?.responsibility, true);
        break;
      }
      case 'prepared': {
        const current = await group();
        assert.deepEqual(await sources(current.id), [eight.id]);
        assert.equal((await rule(seven, physics)).deliveryMode, 'ADMIN_CLASS');
        assert.equal((await rule(seven, chinese)).isCompulsory, true);
        const roles = await prisma.teachingAssignment.findMany({where: {workspaceId: current.id, isActive: true}});
        assert.deepEqual(Object.fromEntries(roles.map(row => [row.accountId, row.position])),
          {[primary.id]: 'PRIMARY', [outgoing.id]: 'CO_TEACHER'});
        assert.ok(planRequest && planReads === 1);
        break;
      }
      case 'web-sources': await legacySources(); break;
      case 'zero-write': {
        const current = await group();
        assert.equal(current.name, '物理 A1');
        assert.deepEqual(await sources(current.id), [eight.id, nine.id].sort());
        break;
      }
      case 'handoff': {
        const only = await prisma.administrativeClassLeadership.findMany({where: {administrativeClassId: eight.id, isActive: true}});
        assert.deepEqual(only.map(row => [row.accountId, row.position]), [[incoming.id, 'HEAD_TEACHER']]);
        const current = await group();
        const left = await access(await login(outgoing.localUsername), spring.id);
        assert.deepEqual(left, {[current.id]: {role: 'ASSISTANT', responsibility: false}});
        const arrived = await access(await login(incoming.localUsername), spring.id);
        assert.equal(arrived[eight.id]?.responsibility, true);
        assert.equal(arrived[current.id]?.role, 'VIEWER');
        assert.ok(staffDropped && staffReads > 0);
        break;
      }
      case 'archive': {
        assert.equal((await rule(archive7, chinese)).isCompulsory, false);
        assert.equal((await rule(seven, chinese)).isCompulsory, true);
        assert.equal((await rule(fall7, chinese)).isCompulsory, true);
        assert.equal((await prisma.workspace.findUniqueOrThrow({where: {id: history.id}})).subjectId, physics.id);
        assert.equal(await prisma.publicationTarget.count({where: {publicationId: publication.id, workspaceId: history.id}}), 1);
        assert.equal((await access(await login(independent.localUsername), archived.id))[history.id]?.role, 'TEACHER');
        break;
      }
      case 'active-spring': {
        const current = await group();
        assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: spring.id}})).status, 'ACTIVE');
        await student(spring, seven, null, 'edge-spring-seven-student');
        await student(spring, eight, current.name, 'edge-spring-eight-student');
        await teacher(outgoing.localUsername, [current.id], 'edge-spring-outgoing');
        await teacher(incoming.localUsername, [eight.id, current.id], 'edge-spring-incoming', [current.id]);
        break;
      }
      default: throw new Error('Unexpected checkpoint');
    }
    cases.push(step);
  }

  const relay = createServer(async (request, response) => {
    try {
      const path = request.url;
      if (path.startsWith('/fixture/teaching/')) {
        await checkpoint(path.split('/').at(-1));
        return response.writeHead(204).end();
      }
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const headers = {...request.headers};
      for (const key of ['host','connection','transfer-encoding','content-length']) delete headers[key];
      const upstream = await fetch(origin + path, {method: request.method, headers, redirect: 'manual',
        body: chunks.length ? Buffer.concat(chunks) : undefined});
      const body = Buffer.from(await upstream.arrayBuffer());
      if (!planRequest && path === planPath && request.method === 'POST' && upstream.ok) {
        const result = JSON.parse(body).data;
        if (result.state === 'APPLIED') {
          planRequest = result.receipt.requestId;
          return response.writeHead(502).end();
        }
      }
      if (planRequest && path === planPath + '/receipts/' + planRequest && upstream.ok) planReads++;
      if (!staffDropped && path === staffPath && request.method === 'PUT' && upstream.ok && JSON.parse(body).data.state === 'APPLIED') {
        staffDropped = true;
        return response.writeHead(502).end();
      }
      if (staffDropped && path === staffPath && request.method === 'GET' && upstream.ok) staffReads++;
      const clean = Object.fromEntries(upstream.headers);
      for (const key of ['connection','transfer-encoding','content-encoding','content-length']) delete clean[key];
      response.writeHead(upstream.status, clean).end(body);
    } catch (error) {
      console.error(error.message);
      response.writeHead(500).end(error.message);
    }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  try {
    await verifyStudentBrowser({legacyWebManagement: true, origin, web, directory, evidence, school, rooms: [], afterStudent: async (edge, url) => {
      browser = edge;
      site = url;
      const config = join(directory, 'term-teaching-desktop.json');
      await writeFile(config, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
        schoolCode: school.code, password: pin, springId: spring.id, archiveId: archived.id, physicsId: physics.id, chineseId: chinese.id,
        gradeId: grade.id, sevenId: seven.id, eightId: eight.id, nineId: nine.id, archiveSevenId: archive7.id, archiveGroupId: history.id,
        outgoingId: outgoing.id, incomingId: incoming.id, primaryId: primary.id, allowTermTeachingWrites: true}), {mode: 0o600});
      await runDesktop(['--live-scoped-term-teaching', config, evidence]);
    }});
    assert.equal(cases.length, 7);
    await writeFile(join(evidence, 'scoped-term-teaching-result.json'), JSON.stringify({result: 'PASS', cases,
      planReceiptReads: planReads, staffResultReads: staffReads, sameNameClassIds: [fall7.id, seven.id, archive7.id],
      archivedPublicationId: publication.id}, null, 2));
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
