import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyScopedTermClasses({prisma, origin, fixture, directory, evidence, web, runDesktop}) {
  const {school, term: autumn, pin, other, managerId} = fixture;
  await mkdir(evidence, {recursive: true});
  const spring = await makeTerm(2026, 2, '春季学期 · 备课', 'DRAFT');
  const archive = await makeTerm(2025, 1, '归档学期 · 原始记录', 'ARCHIVED');
  const fallGrade = await prisma.grade.findFirstOrThrow({where: {termId: autumn.id, code: 'G2'}});
  const springGrade = await makeGrade(spring.id);
  const archiveGrade = await makeGrade(archive.id);
  const stopped = await prisma.workspace.create({data: {termId: spring.id, gradeId: springGrade.id,
    type: 'ADMIN_CLASS', name: '高二停用示例班', code: 'STOPPED-8', isActive: false}});
  const [fallRoom, springRoom, archiveRoom] = await Promise.all([
    makeRoom(autumn.id, fallGrade.id), makeRoom(spring.id, springGrade.id), makeRoom(archive.id, archiveGrade.id)
  ]);
  const archivedStudent = await student(archiveRoom, '归档原姓名');
  await student(fallRoom, '秋季原同学');
  const day = new Date('2025-09-01T00:00:00Z');
  const attendance = {absent: [archivedStudent.id], late: [], excluded: []};
  await prisma.classAttendanceDay.create({data: {administrativeClassId: archiveRoom.id, attendanceDate: day, attendance}});
  await prisma.schoolMember.upsert({where: {schoolId_accountId: {schoolId: other.id, accountId: managerId}},
    create: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}, update: {role: 'ADMIN'}});
  const login = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
  assert.equal(login.status, 200);
  const loginResult = await login.json();
  const auth = loginResult.data ?? loginResult;
  const steps = [];
  let lost, receiptReads = 0, gradeReplyLost = false, gradeReadLost = false, browser, site;
  const sourcePath = '/api/v2/admin/schools/' + school.id + '/academic-terms/' + spring.id + '/structure';

  function makeTerm(academicYear, semester, name, status) {
    return prisma.academicTerm.create({data: {schoolId: school.id, academicYear, semester, name, status}});
  }
  function makeGrade(termId) {
    return prisma.grade.create({data: {termId, name: '高二', code: 'G2', sortOrder: 1}});
  }
  function makeRoom(termId, gradeId) {
    return prisma.workspace.create({data: {termId, gradeId, name: '高二（7）班', code: 'SCOPED-7', type: 'ADMIN_CLASS'}});
  }
  function student(room, name) {
    return prisma.administrativeClassStudent.create({data: {administrativeClassId: room.id, name, studentNumber: '001'}});
  }
  function roster(room) {
    return prisma.administrativeClassStudent.findMany({where: {administrativeClassId: room.id, isActive: true},
      orderBy: {sortOrder: 'asc'}});
  }

  async function activeStudent(expectedTerm, name) {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    try {
      await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
      await page.goto(site);
      await page.getByRole('button', {name: /开始选班/}).click();
      const dialog = page.getByRole('dialog').filter({hasText: '选择我的班级'});
      const choose = async (label, item) => {
        await dialog.locator('.v-select').filter({hasText: label}).locator('.v-field').click();
        await page.getByRole('option', {name: item, exact: true}).click();
      };
      const current = page.waitForResponse(response => response.url().includes('/catalog/terms/current?') && response.ok());
      await choose(/^学校/, school.name);
      assert.equal((await (await current).json()).data.id, expectedTerm.id);
      await choose(/^行政班/, '高二（7）班');
      await Promise.all([page.waitForResponse(response => response.url().includes('/student-selection/validate') && response.ok()),
        dialog.getByRole('button', {name: '保存并查看作业'}).click()]);
      await dialog.waitFor({state: 'hidden'});
      await page.screenshot({path: join(evidence, name + '.png'), animations: 'disabled'});
    } finally { await page.close(); }
  }

  async function legacyAdmin(section, termId, verify) {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
    await page.addInitScript(({access, refresh}) => {
      localStorage.setItem('classworks-v2-access-token', access);
      localStorage.setItem('classworks-v2-refresh-token', refresh);
    }, {access: auth.access_token, refresh: auth.refresh_token});
    const url = site + '/classworks-admin?section=' + section + '&school=' + school.id + '&term=' + termId;
    try { await verify(page, url); }
    catch (error) {
      await page.screenshot({path: join(evidence, 'edge-legacy-' + section + '-failure.png'), animations: 'disabled'}).catch(() => {});
      throw error;
    } finally { await page.close(); }
  }

  async function legacyWrite() {
    await legacyAdmin('students', spring.id, async (page, url) => {
      const isRoster = (response, method) => response.url().endsWith('/administrative-classes/' + springRoom.id + '/students')
        && response.request().method() === method && response.ok();
      const loaded = page.waitForResponse(response => isRoster(response, 'GET'));
      await page.goto(url);
      await loaded;
      const rosterCard = page.locator('.class-roster-manager');
      const student = rosterCard.getByLabel('姓名 1', {exact: true});
      assert.equal(await student.inputValue(), '春季原同学');
      await student.fill('旧网页先保存');
      await rosterCard.getByRole('button', {name: '预览并保存', exact: true}).click();
      await Promise.all([page.waitForResponse(response => isRoster(response, 'PUT')),
        page.getByRole('dialog').filter({hasText: '核对名单变更'})
          .getByRole('button', {name: '确认保存名单', exact: true}).click()]);
      await rosterCard.getByText('名单已保存，在线大屏将同步更新。').waitFor();
      await page.screenshot({path: join(evidence, 'edge-legacy-spring-write.png'), animations: 'disabled'});
    });
  }

  async function legacyClassName(before, after) {
    await legacyAdmin('structure', archive.id, async (page, url) => {
      await page.goto(url);
      await page.getByRole('tab', {name: '年级与行政班'}).click();
      const row = page.locator('.v-list-item').filter({hasText: before}).filter({hasText: archiveRoom.code});
      await row.getByRole('button').click();
      const editor = page.getByRole('dialog').filter({hasText: '编辑行政班'});
      await editor.getByLabel('班级名称').fill(after);
      await Promise.all([page.waitForResponse(response => response.url().endsWith('/administrative-classes/' + archiveRoom.id)
        && response.request().method() === 'PATCH' && response.ok()),
      editor.getByRole('button', {name: '保存', exact: true}).click()]);
      await page.getByText(after, {exact: true}).waitFor();
    });
  }

  async function checkpoint(step) {
    switch (step) {
      case 'before':
        await activeStudent(autumn, 'edge-student-autumn');
        break;
      case 'legacy-writes': await legacyWrite(); break;
      case 'legacy-rename-before-roster': await legacyClassName('高二（7）班', '高二（7）班 · 归档一版'); break;
      case 'legacy-rename-after-preview': await legacyClassName('高二（7）班 · 归档一版', '高二（7）班 · 归档二版'); break;
      case 'conflict-zero-overwrite':
        assert.deepEqual((await roster(springRoom)).map(row => [row.studentNumber, row.name]),
          [['001', '旧网页先保存'], ['002', '春季新增同学']]);
        break;
      case 'archive-and-draft':
        assert.deepEqual((await roster(archiveRoom)).map(row => [row.id, row.studentNumber, row.name]),
          [[archivedStudent.id, '001', '归档已更正']]);
        assert.equal((await prisma.workspace.findUniqueOrThrow({where: {id: archiveRoom.id}})).name, '高二（7）班 · 归档二版');
        assert.deepEqual((await prisma.classAttendanceDay.findFirstOrThrow({where: {administrativeClassId: archiveRoom.id}})).attendance, attendance);
        assert.deepEqual((await roster(fallRoom)).map(row => row.name), ['秋季原同学']);
        assert.equal((await prisma.grade.findUniqueOrThrow({where: {id: springGrade.id}})).sortOrder, 25);
        assert.equal(await prisma.workspace.count({where: {termId: spring.id, code: {in: ['SG2-C01', 'SG2-C02', 'SPRING-ONE']}}}), 3);
        assert.ok(lost && receiptReads > 0, 'named class recovered by the same request');
        assert.equal(await prisma.administrativeClassCreation.count({where: {schoolId: school.id, requestId: lost}}), 1);
        break;
      case 'active-spring':
        assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: spring.id}})).status, 'ACTIVE');
        assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: autumn.id}})).status, 'ARCHIVED');
        assert.deepEqual((await roster(fallRoom)).map(row => row.name), ['秋季原同学']);
        await activeStudent(spring, 'edge-student-spring');
        break;
      default: throw new Error('Unknown checkpoint');
    }
    steps.push(step);
  }

  const relay = createServer(async (request, response) => {
    try {
      const path = request.url;
      if (path.startsWith('/fixture/scoped/')) {
        await checkpoint(path.split('/').at(-1));
        response.writeHead(204).end();
        return;
      }
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const headers = {...request.headers};
      for (const header of ['host', 'connection', 'transfer-encoding', 'content-length']) delete headers[header];
      const upstream = await fetch(origin + path, {method: request.method, headers, redirect: 'manual',
        body: chunks.length ? Buffer.concat(chunks) : undefined});
      const body = Buffer.from(await upstream.arrayBuffer());
      if (path === sourcePath + '/grades/' + springGrade.id && request.method === 'PATCH' && upstream.ok && !gradeReplyLost) {
        gradeReplyLost = true;
        response.writeHead(502).end();
        return;
      }
      if (path === sourcePath && request.method === 'GET' && gradeReplyLost && !gradeReadLost) {
        gradeReadLost = true;
        response.writeHead(502).end();
        return;
      }
      if (path === sourcePath + '/classes' && request.method === 'POST' && upstream.status === 201 && !lost) {
        lost = JSON.parse(body).data.requestId;
        request.socket.destroy();
        return;
      }
      if (lost && path.startsWith(sourcePath + '/classes/receipts/' + lost) && upstream.ok) ++receiptReads;
      const clean = Object.fromEntries(upstream.headers);
      for (const header of ['connection', 'transfer-encoding', 'content-encoding', 'content-length']) delete clean[header];
      response.writeHead(upstream.status, clean).end(body);
    } catch (error) {
      console.error(error.message);
      response.writeHead(500).end(error.message);
    }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  try {
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [],
      afterStudent: async (edge, base) => {
        browser = edge;
        site = base;
        const access = join(directory, 'scoped-term-desktop.json');
        assert.equal(new Set([fallRoom.id, springRoom.id, archiveRoom.id]).size, 3);
        await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
          schoolCode: school.code, username: 'admin', password: pin, autumnId: autumn.id, springId: spring.id, archiveId: archive.id,
          springRoomId: springRoom.id, archiveRoomId: archiveRoom.id, stoppedId: stopped.id, springGradeId: springGrade.id,
          otherSchoolId: other.id, allowScopedTermWrites: true}), {mode: 0o600});
        await runDesktop(['--live-scoped-term-classes', access, evidence]);
      }});
    assert.equal(steps.length, 7);
    assert.ok(gradeReplyLost && gradeReadLost);
    await writeFile(join(evidence, 'scoped-term-result.json'), JSON.stringify({result: 'PASS', steps, receiptReads, gradeReplyLost, gradeReadLost,
      roomIds: {autumn: fallRoom.id, spring: springRoom.id, archive: archiveRoom.id},
      archiveStudentId: archivedStudent.id, archiveAttendanceRetained: true}, null, 2));
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
