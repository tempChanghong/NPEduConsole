import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyGradeTeachingPlan({prisma, origin, fixture, directory, evidence, web, createTeacher, runDesktop}) {
  await mkdir(evidence, {recursive: true});
  const {school, term, pin} = fixture;
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const [seven, eight, nine] = await Promise.all([7, 8, 9].map(n => prisma.workspace.create({data: {
    termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS', name: '高二（' + n + '）班', code: 'G2-' + n
  }})));
  const historyGrade = await prisma.grade.create({data: {termId: term.id, name: '高三回归年级', code: 'G3-TEST', sortOrder: 50}});
  const historyRoom = await prisma.workspace.create({data: {termId: term.id, gradeId: historyGrade.id, type: 'ADMIN_CLASS',
    name: '高三回归班', code: 'G3-TEST-7'}});
  const [physics, math] = await Promise.all([['PHY', '物理'], ['MATH', '数学']].map(([code, name]) =>
    prisma.subject.create({data: {schoolId: school.id, code, name}})));
  const [teacher, fixed, member] = await Promise.all([
    createTeacher('plan-teacher', '八班物理老师'), createTeacher('plan-fixed', '原物理老师'),
    createTeacher('plan-member', '独立教研成员')
  ]);
  await prisma.administrativeClassSubject.createMany({data: [
    ...[seven, eight, nine].map(room => ({administrativeClassId: room.id, subjectId: physics.id,
      deliveryMode: room.id === nine.id ? 'ADMIN_CLASS' : 'COURSE_GROUP', isCompulsory: true})),
    {administrativeClassId: seven.id, subjectId: math.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true},
    {administrativeClassId: historyRoom.id, subjectId: physics.id, deliveryMode: 'COURSE_GROUP', isCompulsory: false}
  ]});
  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const headers = {'Content-Type': 'application/json', ...(token ? {Authorization: 'Bearer ' + token} : {})};
    const response = await fetch(origin + path, {method, headers, body: body === undefined ? undefined : JSON.stringify(body)});
    const result = await response.json();
    assert.equal(response.status, status, path + ': ' + (result.code ?? response.status));
    return result.data ?? result;
  }
  const login = async username => (await api('/accounts/local/login', {method: 'POST', body: {
    schoolCode: school.code, username, password: pin
  }})).access_token;
  const adminSession = await api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username: 'admin', password: pin}});
  const admin = adminSession.access_token;
  const [teacherToken, memberToken, fixedToken] = await Promise.all(['plan-teacher', 'plan-member', 'plan-fixed'].map(login));
  const schoolPath = '/api/v2/admin/schools/' + school.id;
  const planPath = schoolPath + '/current-term/grade-teaching-plans';
  const group = (code, name, rooms, isStudentSelectable = true, gradeId = grade.id) => api(schoolPath + '/course-groups', {method: 'POST', token: admin, status: 201,
    body: {termId: term.id, gradeId, subjectId: physics.id, code, name, sourceClassIds: rooms.map(room => room.id),
      isStudentSelectable}});
  const a1 = await group('PHY-A1', '物理 A1', [seven, eight]);
  const a2 = await group('PHY-A2', '物理 A2', [seven]);
  const consoleHistory = await group('PHY-HC', '桌面移除检验班', [historyRoom], false, historyGrade.id);
  const webHistory = await group('PHY-HW', '网页移除检验班', [historyRoom], false, historyGrade.id);
  for (const workspaceId of [a1.id, a2.id, nine.id, consoleHistory.id, webHistory.id]) await api(schoolPath + '/teaching-assignments', {
    method: 'PUT', token: admin, body: {workspaceId, subjectId: physics.id, accountId: fixed.id, position: 'PRIMARY'}
  });
  await prisma.workspaceMember.create({data: {workspaceId: a1.id, accountId: member.id, role: 'ASSISTANT'}});
  await prisma.workspaceMember.create({data: {workspaceId: nine.id, accountId: member.id, role: 'ASSISTANT'}});

  const checks = [];
  const originalAssignments = new Map((await prisma.teachingAssignment.findMany({where: {workspaceId: {in: [consoleHistory.id, webHistory.id]}}}))
    .map(row => [row.workspaceId, row.id]));
  const access = async token => new Map((await api('/api/v2/me/workspaces?termId=' + term.id, {token})).map(row => [row.workspace.id, row.role]));
  let postCount = 0, receiptReads = 0, dropped = false, appliedRequest, postState;
  async function checkpoint(step) {
    const oldGroup = await prisma.workspace.findUniqueOrThrow({where: {id: a1.id}});
    const eighthRule = await prisma.administrativeClassSubject.findUniqueOrThrow({
      where: {administrativeClassId_subjectId: {administrativeClassId: eight.id, subjectId: physics.id}}
    });
    if (step === 'web-change') {
      await api(schoolPath + '/teaching-assignments', {method: 'PUT', token: admin,
        body: {workspaceId: a1.id, subjectId: physics.id, accountId: member.id, position: 'CO_TEACHER'}});
    } else if (step === 'stale') {
      assert.equal(postState, 'STALE');
      assert.equal(oldGroup.isActive, true);
      assert.equal(eighthRule.deliveryMode, 'COURSE_GROUP');
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: eight.id}}), 0);
    } else if (step === 'applied') {
      assert.equal(oldGroup.isActive, false);
      assert.equal(oldGroup.subjectId, physics.id);
      assert.deepEqual((await prisma.workspaceSourceClass.findMany({where: {workspaceId: a1.id}}))
        .map(row => row.administrativeClassId).sort(), [seven.id, eight.id].sort());
      assert.equal(eighthRule.deliveryMode, 'ADMIN_CLASS');
      assert.equal(eighthRule.isCompulsory, true);
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: eight.id, accountId: teacher.id, isActive: true}}), 1);
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: {in: [a2.id, nine.id]}, accountId: fixed.id}}), 2);
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: a1.id}}), 2);
      assert.equal(await prisma.administrativeClassSubject.count({where: {administrativeClassId: seven.id, subjectId: math.id}}), 1);
      assert.deepEqual([...(await access(teacherToken))], [[eight.id, 'TEACHER']]);
      const independent = await access(memberToken);
      assert.equal(independent.get(nine.id), 'ASSISTANT');
      assert.equal(independent.has(a1.id), false);
      assert.equal(await prisma.workspaceMember.count({where: {workspaceId: a1.id, accountId: member.id}}), 1);
      const overview = await api(schoolPath + '/management-overview/current?courseGroupActions=1', {token: admin});
      assert.equal(overview.tasks.some(row => row.subjectId === physics.id &&
        (row.classId === eight.id || row.courseGroupId === a1.id)), false);
      assert.ok(dropped);
      assert.equal(postCount, 2);
      assert.equal(receiptReads, 1);
      assert.equal((await api(planPath + '/receipts/' + appliedRequest, {token: admin})).requestId, appliedRequest);
    } else if (step === 'desktop-assignment-removed') await checkAssignmentHistory(consoleHistory);
    else throw new Error('Unknown teaching-plan checkpoint');
    checks.push(step);
  }

  async function checkAssignmentHistory(group) {
    const assignment = await prisma.teachingAssignment.findUniqueOrThrow({where: {id: originalAssignments.get(group.id)}});
    assert.equal(assignment.isActive, false);
    assert.equal(assignment.workspaceId, group.id);
    assert.equal((await access(fixedToken)).get(group.id), 'TEACHER', 'independent member permission remains');
    const directory = await api(schoolPath + '/current-term/course-groups?termId=' + term.id, {token: admin});
    assert.deepEqual(directory.groups.find(row => row.id === group.id).assignments, []);
    const overview = await api(schoolPath + '/management-overview/current?courseGroupActions=1', {token: admin});
    assert.ok(overview.tasks.some(row => row.code === 'COURSE_GROUP_WITHOUT_TEACHER' && row.courseGroupId === group.id));
    const options = await api('/api/v2/catalog/administrative-classes/' + seven.id + '/course-options');
    assert.deepEqual(options.subjects.find(row => row.subject.id === physics.id).courseGroups.map(row => row.id), [a2.id]);
    const draft = {termId: term.id, gradeId: historyGrade.id, subjectId: physics.id, rules: [{classId: historyRoom.id,
      subjectId: math.id, mode: 'COURSE_GROUP', compulsory: true}], assignments: [], groups: [{key: group.id, groupId: group.id,
      operation: 'RECLASSIFY', subjectId: math.id, name: group.name, code: group.code, isStudentSelectable: true,
      sourceClassIds: [historyRoom.id]}]};
    let review = await api(planPath + '/preview', {method: 'POST', token: admin, body: draft});
    assert.ok(review.issues.some(row => row.key === group.id && row.code === 'COURSE_GROUP_SUBJECT_LOCKED'));
    draft.groups[0] = {...draft.groups[0], key: 'replacement', groupId: null, operation: 'CREATE', code: group.code + '-NEW'};
    review = await api(planPath + '/preview', {method: 'POST', token: admin, body: draft});
    assert.equal(review.canApply, true, 'new course ID may replace a protected group');
  }

  async function verifyWebRemoval(browser, url) {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    try {
      await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
      await page.addInitScript(({accessToken, refreshToken}) => {
        localStorage.setItem('classworks-v2-access-token', accessToken);
        localStorage.setItem('classworks-v2-refresh-token', refreshToken);
      }, {accessToken: adminSession.access_token, refreshToken: adminSession.refresh_token});
      await page.goto(url + '/classworks-admin?section=structure&school=' + school.id + '&term=' + term.id);
      await page.locator('.grade-select .v-field').click();
      await page.getByRole('option', {name: historyGrade.name, exact: true}).click();
      const row = page.locator('.relationship-matrix tr').filter({hasText: historyRoom.name});
      await row.getByRole('button', {name: /网页移除检验班/}).click();
      const assignment = page.getByRole('dialog').filter({hasText: webHistory.name});
      await assignment.locator('.v-chip').filter({hasText: fixed.name}).locator('.v-chip__close').click();
      const confirmation = page.getByRole('dialog').filter({hasText: '移除任课关系？'});
      const verified = page.waitForResponse(res => res.request().method() === 'DELETE' &&
        res.url().endsWith('/teaching-assignments/' + originalAssignments.get(webHistory.id)) && res.ok());
      await confirmation.getByRole('button', {name: '确认移除'}).click();
      await verified;
      await row.getByRole('button', {name: /网页移除检验班.*未明确教师/}).waitFor();
      await page.screenshot({path: join(evidence, 'browser-legacy-assignment-removed.png'), animations: 'disabled'});
      await checkAssignmentHistory(webHistory);
      checks.push('legacy-assignment-removed');
    } finally { await page.close(); }
  }

  const backend = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__plan/')) {
      try { await checkpoint(req.url.slice('/__plan/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    const save = req.method === 'POST' && req.url === planPath;
    if (save) postCount++;
    if (req.method === 'GET' && req.url.startsWith(planPath + '/receipts/')) receiptReads++;
    const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
      if (!save || reply.statusCode !== 200) {
        res.writeHead(reply.statusCode, reply.headers);
        reply.pipe(res);
        return;
      }
      const chunks = [];
      reply.on('data', chunk => chunks.push(chunk));
      reply.on('end', () => {
        const body = Buffer.concat(chunks);
        const result = JSON.parse(body).data;
        postState = result?.state;
        if (!dropped && postState === 'APPLIED') {
          appliedRequest = result.receipt.requestId;
          dropped = true;
          res.destroy();
        } else {
          res.writeHead(reply.statusCode, reply.headers);
          res.end(body);
        }
      });
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const accessPath = join(directory, 'grade-teaching-plan-desktop.json');
    await writeFile(accessPath, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id, schoolCode: school.code,
      password: pin, gradeId: grade.id, sevenId: seven.id, eightId: eight.id, nineId: nine.id, subjectId: physics.id,
      teacherId: teacher.id, a1Id: a1.id, consoleHistoryGroupId: consoleHistory.id, allowGradePlanTestWrites: true}), {mode: 0o600});
    await verifyStudentBrowser({legacyWebManagement: true, origin, web, directory, evidence, school, rooms: [seven], expectedTermId: term.id,
      afterSelection: async ({page, dialog, browser, url}) => {
        const saved = () => page.evaluate(() => JSON.parse(localStorage.getItem('classworks-v2-student-selection')));
        assert.equal((await saved()).courseGroupIds[physics.id], a1.id);
        await runDesktop(['--live-grade-teaching-plan', accessPath, evidence]);
        assert.deepEqual(checks, ['web-change', 'stale', 'applied', 'desktop-assignment-removed']);
        await page.reload({waitUntil: 'domcontentloaded'});
        await page.getByText(/请重新确认走班选择/).waitFor({state: 'visible', timeout: 20000});
        await page.getByRole('button', {name: '修改选班', exact: true}).click();
        await dialog.waitFor({state: 'visible'});
        await dialog.locator('.v-select').filter({hasText: '物理（必须确认）'}).locator('.v-field').click();
        await page.getByRole('option', {name: '物理 A2', exact: true}).waitFor();
        assert.equal(await page.getByRole('option', {name: '物理 A1', exact: true}).count(), 0);
        await page.screenshot({path: join(evidence, 'browser-reselect-a2.png'), animations: 'disabled'});
        await page.getByRole('option', {name: '物理 A2', exact: true}).click();
        const validation = page.waitForResponse(res => res.url().includes('/student-selection/validate') &&
          res.request().method() === 'POST' && res.ok() && res.request().postDataJSON().courseGroupIds[physics.id] === a2.id);
        await dialog.getByRole('button', {name: '保存并查看作业'}).click();
        await validation;
        await dialog.waitFor({state: 'hidden'});
        assert.equal((await saved()).courseGroupIds[physics.id], a2.id);
        checks.push('student-reselected-actual-a2');
        await verifyWebRemoval(browser, url);
      }
    });
    await writeFile(join(evidence, 'cross-end-result.json'), JSON.stringify({checks, termId: term.id, a1Id: a1.id, a2Id: a2.id,
      eightId: eight.id, observedPlanRequestId: appliedRequest, postCount, receiptReads, passed: true}, null, 2));
    console.log('PASS: grade-level plan through Windows, actual Edge saved/changed selection, PostgreSQL, teacher access and verified receipt.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
