import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createServer, request} from 'node:http';
import {createRequire} from 'node:module';
import {mkdir, writeFile} from 'node:fs/promises';
import {dirname, join} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {pathToFileURL} from 'node:url';

export async function verifyCourseGroups({prisma, origin, fixture, directory, evidence, web, createTeacher, runDesktop, runDatabase}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, pin, managerId} = fixture;
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
  const makeRoom = n => prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS',
    name: '高二（' + n + '）班', code: 'G2-' + n}});
  const [seven, eight, nine, ten] = await Promise.all([7, 8, 9, 10].map(makeRoom));
  const physics = await prisma.subject.create({data: {schoolId: school.id, code: 'WALK-PHY', name: '物理'}});
  const math = await prisma.subject.create({data: {schoolId: school.id, code: 'WALK-MATH', name: '数学'}});
  await prisma.administrativeClassSubject.createMany({data: [
    ...[seven, eight, nine, ten].map(room => ({administrativeClassId: room.id, subjectId: physics.id,
      deliveryMode: room.id === nine.id ? 'ADMIN_CLASS' : 'COURSE_GROUP', isCompulsory: true})),
    {administrativeClassId: seven.id, subjectId: math.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true}
  ]});
  const [walkTeacher, fixedTeacher, head, member] = await Promise.all([
    createTeacher('walk-teacher', '走班教师'), createTeacher('fixed-teacher', '原任课教师'),
    createTeacher('walk-head', '八班班主任'), createTeacher('walk-member', '教研成员')
  ]);
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
  const [admin, teacherToken, headToken, memberToken] = await Promise.all(['admin', 'walk-teacher', 'walk-head', 'walk-member'].map(login));
  const schoolPath = '/api/v2/admin/schools/' + school.id;
  const oldPath = schoolPath + '/course-groups';
  const newPath = schoolPath + '/current-term/course-groups';
  const a2 = await api(oldPath, {method: 'POST', token: admin, status: 201, body: {
    termId: term.id, gradeId: grade.id, subjectId: physics.id, code: 'PHY-A2', name: '物理 A2', sourceClassIds: [seven.id], isStudentSelectable: true
  }});
  for (const workspaceId of [a2.id, nine.id]) await api(schoolPath + '/teaching-assignments', {
    method: 'PUT', token: admin, body: {workspaceId, subjectId: physics.id, accountId: fixedTeacher.id, position: 'PRIMARY'}
  });
  await prisma.administrativeClassLeadership.create({data: {administrativeClassId: eight.id, accountId: head.id, position: 'HEAD_TEACHER'}});
  const checks = [];
  const access = async token => new Map((await api('/api/v2/me/workspaces?termId=' + term.id, {token})).map(row => [row.workspace.id, row]));
  const task = async (code, group) => (await api(schoolPath + '/management-overview/current?courseGroupActions=1', {token: admin})).tasks
    .find(row => row.code === code && (row.courseGroupId === group || row.classId === group) && row.subjectId === physics.id);
  const student = room => '/api/v2/catalog/administrative-classes/' + room.id;
  const nextTerm = await prisma.academicTerm.create({data: {schoolId: school.id, name: '切换后的学期',
    academicYear: term.academicYear + 1, semester: term.semester, status: 'DRAFT'}});
  const nextGrade = await prisma.grade.create({data: {termId: nextTerm.id, code: 'G2', name: '高二'}});
  const nextRoom = await prisma.workspace.create({data: {termId: nextTerm.id, gradeId: nextGrade.id, type: 'ADMIN_CLASS',
    name: ten.name, code: ten.code}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: nextRoom.id,
    subjectId: physics.id, deliveryMode: 'COURSE_GROUP', isCompulsory: true}});
  let a1, b1, lost, releaseCommit, requestId, firstReceipt, firstReadCanceled = false, savePosts = 0, postsAtRecovery;
  let editRequestId, editReceipt, editReadCanceled = false;
  let switchTermOnCourseRead = false;
  const receiptReads = [];
  const courseReads = [];
  async function waitFor(condition, step) {
    for (let i = 0; i < 200 && !condition(); i++) await delay(20);
    assert.ok(condition(), step);
  }
  async function check(step) {
    switch (step) {
    case 'blocked':
      assert.equal(await prisma.workspace.count({where: {termId: term.id, type: 'COURSE_GROUP'}}), 1);
      assert.ok(await task('WALKING_SUBJECT_WITHOUT_GROUP', eight.id));
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: nine.id, accountId: fixedTeacher.id}}), 1);
      break;
    case 'commit-response-held':
      await waitFor(() => releaseCommit, 'the KV committed but its response is still in flight');
      assert.equal(savePosts, 1);
      assert.equal(receiptReads.length, 0);
      break;
    case 'drop-commit-response':
      assert.ok(releaseCommit);
      releaseCommit();
      break;
    case 'receipt-response-held':
      await waitFor(() => firstReceipt, 'the first read reached the real KV and its response is held');
      assert.equal(firstReceipt.requestId, requestId);
      assert.equal(firstReceipt.termId, term.id);
      assert.equal(receiptReads.length, 1);
      break;
    case 'receipt-read-canceled':
      await waitFor(() => firstReadCanceled, 'closing canceled the first receipt read');
      assert.equal(savePosts, 1);
      assert.equal(receiptReads.length, 1);
      break;
    case 'other-source-opened':
      assert.equal(savePosts, 1);
      assert.equal(receiptReads.length, 1);
      assert.ok(await task('WALKING_SUBJECT_WITHOUT_GROUP', ten.id));
      break;
    case 'other-source-saved':
      b1 = await prisma.workspace.findUniqueOrThrow({where: {termId_code: {termId: term.id, code: 'PHY-B1'}}});
      assert.deepEqual((await prisma.workspaceSourceClass.findMany({where: {workspaceId: b1.id}})).map(row => row.administrativeClassId), [ten.id]);
      assert.equal(savePosts, 2);
      assert.equal(receiptReads.length, 1);
      break;
    case 'other-group-opened':
      assert.equal(savePosts, 2);
      assert.equal(receiptReads.length, 1);
      assert.ok(await task('COURSE_GROUP_WITHOUT_TEACHER', b1.id));
      break;
    case 'switch-term-for-bookmark':
      switchTermOnCourseRead = true;
      break;
    case 'original-term-rejected':
      assert.deepEqual(courseReads.at(-1), {termId: term.id, status: 409});
      assert.equal(savePosts, 2);
      assert.equal(receiptReads.length, 1);
      break;
    case 'different-term-opened':
      assert.deepEqual(courseReads.at(-1), {termId: nextTerm.id, status: 200});
      assert.equal(savePosts, 2);
      assert.equal(receiptReads.length, 1);
      await prisma.$transaction([
        prisma.academicTerm.update({where: {id: nextTerm.id}, data: {status: 'DRAFT'}}),
        prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ACTIVE'}}),
      ]);
      break;
    case 'created': {
      a1 = await prisma.workspace.findUniqueOrThrow({where: {termId_code: {termId: term.id, code: 'PHY-A1'}}});
      assert.equal(a1.name, '物理 A1');
      postsAtRecovery = savePosts;
      assert.ok(lost && firstReadCanceled && postsAtRecovery === 2);
      assert.deepEqual(receiptReads, [requestId, requestId]);
      assert.equal(firstReceipt.groupId, a1.id);
      assert.deepEqual((await prisma.workspaceSourceClass.findMany({where: {workspaceId: a1.id}})).map(row => row.administrativeClassId).sort(), [seven.id, eight.id].sort());
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: a1.id}}), 0);
      assert.equal((await access(teacherToken)).has(a1.id), false);
      assert.equal((await access(headToken)).get(a1.id)?.role, 'VIEWER');
      assert.equal((await access(headToken)).get(a1.id)?.responsibilityDerived, true);
      assert.ok(await task('COURSE_GROUP_WITHOUT_TEACHER', a1.id));
      assert.equal(await task('WALKING_SUBJECT_WITHOUT_GROUP', eight.id), undefined);
      for (const [room, groups, mode] of [[seven, [a1.id, a2.id], 'COURSE_GROUP'], [eight, [a1.id], 'COURSE_GROUP'], [nine, [], 'ADMIN_CLASS']]) {
        const rule = (await api(student(room) + '/course-options')).subjects.find(row => row.subject.id === physics.id);
        assert.deepEqual(rule.courseGroups.map(row => row.id).sort(), groups.sort());
        assert.equal(rule.deliveryMode, mode);
        assert.equal(rule.isCompulsory, true);
      }
      for (const [room, group, status] of [[seven, a1, 200], [seven, a2, 200], [eight, a1, 200], [eight, a2, 422]]) {
        const observed = await api(student(room) + '/student-selection/validate', {method: 'POST', status,
          body: {courseGroupIds: {[physics.id]: group.id}}});
        assert.equal(observed.valid, status === 200);
      }
      await prisma.workspaceMember.create({data: {workspaceId: a1.id, accountId: member.id, role: 'ASSISTANT'}});
      break;
    }
    case 'assigned': {
      const teacher = await access(teacherToken), headAccess = await access(headToken), retained = await access(memberToken);
      assert.deepEqual([...teacher.keys()], [a1.id]);
      assert.equal(teacher.get(a1.id).role, 'TEACHER');
      assert.equal(headAccess.get(eight.id).responsibilityDerived, true);
      assert.equal(headAccess.get(a1.id).role, 'VIEWER');
      assert.equal(headAccess.has(a2.id), false);
      assert.equal(retained.get(a1.id).role, 'ASSISTANT');
      assert.equal(retained.get(a1.id).responsibilityDerived, undefined);
      assert.equal(await task('COURSE_GROUP_WITHOUT_TEACHER', a1.id), undefined);
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: {in: [a2.id, nine.id]}, accountId: fixedTeacher.id}}), 2);
      break;
    }
    case 'narrow-conflict-resolved':
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: nine.id, subjectId: physics.id, accountId: fixedTeacher.id}}), 0);
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: a2.id, subjectId: physics.id, accountId: fixedTeacher.id}}), 1);
      assert.equal(await prisma.administrativeClassSubject.count({where: {administrativeClassId: nine.id, subjectId: physics.id,
        deliveryMode: 'ADMIN_CLASS'}}), 1);
      break;
    case 'existing-edit-read-held':
      await waitFor(() => editReceipt, 'the actual existing group receipt reached the relay');
      assert.equal(editReceipt.requestId, editRequestId);
      assert.equal(editReceipt.groupId, a1.id);
      assert.equal(savePosts, 3);
      break;
    case 'existing-edit-read-canceled':
      await waitFor(() => editReadCanceled, 'closing canceled the existing group receipt read');
      break;
    case 'same-group-blocked':
      assert.equal(savePosts, 3);
      assert.deepEqual(receiptReads, [requestId, requestId, editRequestId]);
      assert.equal((await prisma.workspace.findUniqueOrThrow({where: {id: a1.id}})).name, '物理 A1');
      break;
    case 'supplemented': {
      assert.equal(savePosts, 3);
      assert.deepEqual(receiptReads, [requestId, requestId, editRequestId, editRequestId]);
      assert.equal(await prisma.workspace.count({where: {termId: term.id, type: 'COURSE_GROUP'}}), 3);
      assert.equal((await prisma.workspace.findUniqueOrThrow({where: {id: a1.id}})).code, 'PHY-A1-EDIT');
      const roomIds = (await prisma.workspaceSourceClass.findMany({where: {workspaceId: a1.id}})).map(row => row.administrativeClassId).sort();
      assert.deepEqual(roomIds, [seven.id, eight.id, ten.id].sort());
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: a1.id, accountId: walkTeacher.id}}), 1);
      assert.equal(await prisma.administrativeClassSubject.count({where: {administrativeClassId: seven.id, subjectId: math.id, deliveryMode: 'ADMIN_CLASS'}}), 1);
      break;
    }
    default: throw new Error('unknown isolated walking test checkpoint');
    }
    checks.push(step);
  }

  const backend = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__course/')) {
      try { await check(req.url.slice('/__course/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error.message); res.writeHead(500).end(); }
      return;
    }
    const target = new URL(req.url, backend);
    if (switchTermOnCourseRead && req.method === 'GET' && target.pathname === newPath) {
      switchTermOnCourseRead = false;
      try {
        await prisma.$transaction([
          prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}}),
          prisma.academicTerm.update({where: {id: nextTerm.id}, data: {status: 'ACTIVE'}}),
        ]);
      } catch (error) { console.error(error.message); res.writeHead(500).end(); return; }
    }
    const saveSequence = req.method === 'POST' && req.url === newPath ? ++savePosts : null;
    const readsReceipt = req.method === 'GET' && req.url.startsWith(newPath + '/receipts/');
    if (readsReceipt) receiptReads.push(req.url.split('/').at(-1));
    const holdFirstRead = readsReceipt && receiptReads.length === 1;
    const holdEditRead = readsReceipt && receiptReads.length === 3;
    const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
      if (req.method === 'GET' && target.pathname === newPath)
        courseReads.push({termId: target.searchParams.get('termId'), status: reply.statusCode});
      if ((saveSequence === 1 || saveSequence === 3) && reply.statusCode === 200) {
        const chunks = [];
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => {
          const body = Buffer.concat(chunks);
          const result = JSON.parse(body).data;
          if (result?.state === 'APPLIED' && saveSequence === 1) {
            lost = true;
            requestId = result.receipt.requestId;
            releaseCommit = () => res.destroy();
          } else if (result?.state === 'APPLIED' && saveSequence === 3) {
            editRequestId = result.receipt.requestId;
            res.destroy();
          }
          else { res.writeHead(reply.statusCode, reply.headers); res.end(body); }
        });
      } else if (holdFirstRead && reply.statusCode === 200) {
        const chunks = [];
        res.once('close', () => { firstReadCanceled = !res.writableEnded; });
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => { firstReceipt = JSON.parse(Buffer.concat(chunks)).data; });
      } else if (holdEditRead && reply.statusCode === 200) {
        const chunks = [];
        res.once('close', () => { editReadCanceled = !res.writableEnded; });
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => { editReceipt = JSON.parse(Buffer.concat(chunks)).data; });
      } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const accessPath = join(directory, 'course-groups-desktop.json');
    await writeFile(accessPath, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id, schoolCode: school.code,
      alternateSchoolId: other.id, password: pin, sevenId: seven.id, eightId: eight.id, nineId: nine.id, tenId: ten.id,
      subjectId: physics.id, teacherId: walkTeacher.id, a2Id: a2.id, nextRoomId: nextRoom.id, allowCourseTestWrites: true}), {mode: 0o600});
    await runDesktop(['--live-course-groups', accessPath, evidence]);
    assert.deepEqual(checks, ['blocked', 'commit-response-held', 'drop-commit-response', 'receipt-response-held',
      'receipt-read-canceled', 'other-source-opened', 'other-source-saved', 'other-group-opened', 'switch-term-for-bookmark',
      'original-term-rejected', 'different-term-opened', 'created', 'assigned', 'narrow-conflict-resolved', 'existing-edit-read-held',
      'existing-edit-read-canceled', 'same-group-blocked', 'supplemented']);
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [seven, eight, nine]});
    await runDatabase();
    await writeFile(join(evidence, 'server-result.json'), JSON.stringify({checks, savePosts, postsAtRecovery, requestId,
      receiptReads, firstReadCanceled, editRequestId, editReadCanceled, groupId: a1.id, otherGroupId: b1.id, courseReads,
      actualStudentBrowser: true, passed: true}, null, 2));
    console.log('PASS: Windows course-group setup, real Edge student selection, teacher / homeroom / direct member access, and transactional walking regressions.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}

export async function verifyStudentBrowser({origin, web, directory, evidence, school, rooms, expectedTermId, afterStudent, afterSelection, realtime = false, separateApi = false, legacyWebManagement = false}) {
  const require = createRequire(join(web, 'package.json'));
  const {chromium} = require('@playwright/test');
  const outDir = join(directory, 'walking-web-build');
  const viteRoot = dirname(require.resolve('vite/package.json'));
  const {preview} = await import(pathToFileURL(join(viteRoot, 'dist/node/index.js')).href);
  const proxy = {'/api': origin, '/accounts': origin};
  if (realtime) proxy['/socket.io'] = {target: origin, ws: true};
  const frontend = await preview({configFile: false, root: web, build: {outDir}, preview: {
    host: '127.0.0.1', port: 0, proxy: separateApi ? undefined : proxy
  }});
  const url = 'http://127.0.0.1:' + frontend.httpServer.address().port;
  const api = separateApi ? origin : url;
  let browser, page;
  try {
    await new Promise((resolve, reject) => {
      const build = spawn(process.execPath, ['node_modules/vite/bin/vite.js', 'build', '--logLevel', 'warn', '--outDir', outDir], {
        cwd: web, env: {...process.env, ...(legacyWebManagement && {VITE_ADMIN_LEGACY_MANAGEMENT_ENABLED: 'true'}),
          VITE_DEFAULT_KV_SERVER: api, VITE_DEFAULT_AUTH_SERVER: api,
          VITE_SERVER_URL: api, VITE_ENABLE_ANALYTICS: 'false'}, stdio: 'inherit', windowsHide: true
      });
      build.once('error', reject);
      build.once('exit', code => code === 0 ? resolve() : reject(new Error('Student web build failed')));
    });
    browser = await chromium.launch({channel: 'msedge', headless: true});
    for (let index = 0; index < rooms.length; index++) {
      const room = rooms[index];
      page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
      await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
      await page.goto(url, {waitUntil: 'domcontentloaded'});
      await page.getByRole('button', {name: /开始选班/}).click();
      const dialog = page.getByRole('dialog').filter({hasText: '选择我的班级'});
      await dialog.waitFor({state: 'visible', timeout: 20000});
      const choose = async (label, item) => {
        await dialog.locator('.v-select').filter({hasText: label}).locator('.v-field').click();
        await page.getByRole('option', {name: item, exact: true}).click();
      };
      const activeRequest = expectedTermId ? page.waitForResponse(res => res.url().includes('/catalog/terms/current?') && res.ok()) : null;
      await choose(/^学校/, school.name);
      if (activeRequest) assert.equal((await (await activeRequest).json()).data.id, expectedTermId);
      await choose(/^行政班/, room.name);
      if (index < 2) {
        await dialog.locator('.v-select').filter({hasText: '物理（必须确认）'}).locator('.v-field').click();
        await page.getByRole('option', {name: '物理 A1', exact: true}).waitFor();
        assert.equal(await page.getByRole('option', {name: '物理 A2', exact: true}).count(), index === 0 ? 1 : 0);
        await page.screenshot({path: join(evidence, 'browser-class-' + (index + 7) + '.png'), animations: 'disabled'});
        await page.getByRole('option', {name: '物理 A1', exact: true}).click();
      } else {
        await dialog.getByText('该班所有课程都随行政班，不需要选择走班教学班。', {exact: true}).waitFor();
        await page.screenshot({path: join(evidence, 'browser-class-9.png'), animations: 'disabled'});
      }
      await Promise.all([
        page.waitForResponse(res => res.url().includes('/student-selection/validate') && res.request().method() === 'POST' && res.ok()),
        dialog.getByRole('button', {name: '保存并查看作业'}).click()
      ]);
      await dialog.waitFor({state: 'hidden'});
      if (afterSelection) await afterSelection({page, dialog, room, browser, url});
      await page.close();
      page = null;
    }
    if (afterStudent) await afterStudent(browser, url);
  } catch (error) {
    if (page) await page.screenshot({path: join(evidence, 'browser-failure.png')});
    throw error;
  } finally {
    await browser?.close();
    frontend.httpServer.closeAllConnections();
    await frontend.close();
  }
}
