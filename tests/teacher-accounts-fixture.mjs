import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {randomInt} from 'node:crypto';
import {join} from 'node:path';

export async function verifyTeacherAccounts({prisma, origin, fixture, directory, evidence, bcrypt, localProviderId, runDesktop, runDatabase}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, pin, managerId} = fixture;
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id}});
  const rooms = await Promise.all([8, 9].map(n => prisma.workspace.create({data: {
    termId: term.id, gradeId: grade.id, code: 'TACC-' + n, name: '高二（' + n + '）班', type: 'ADMIN_CLASS'
  }})));
  const [eight] = rooms;
  const physics = await prisma.subject.create({data: {schoolId: school.id, name: '物理', code: 'TACC-PHY'}});
  await prisma.administrativeClassSubject.createMany({data: rooms.map(row => ({
    administrativeClassId: row.id, subjectId: physics.id, deliveryMode: 'ADMIN_CLASS'
  }))});
  const sameName = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(school.code, 'wang-a'),
    localUsername: 'wang-a', name: '王老师', localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: school.code}}});
  await prisma.schoolMember.create({data: {schoolId: school.id, accountId: sameName.id, role: 'VIEWER'}});

  const teacherPin = String(randomInt(100000, 1000000));
  const resetPin = String((Number(teacherPin) - 100000 + 563025) % 900000 + 100000);
  const teacherUsername = 'wang-b';
  const schoolPath = '/api/v2/admin/schools/' + school.id;
  const directoryPath = schoolPath + '/teacher-accounts';
  const backend = new URL(origin);
  let teacher, signedIn, afterRestore, createWrites = 0, readReceipts = 0, lost = false;
  const checks = [];
  async function api(path, {method = 'GET', token, body, expected = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {
      ...(token ? {Authorization: 'Bearer ' + token} : {}), 'Content-Type': 'application/json'
    }, body: body ? JSON.stringify(body) : undefined});
    const payload = await response.json();
    assert.equal(response.status, expected, path + ' / ' + (payload.code ?? response.status));
    return payload.data ?? payload;
  }
  const login = (username, password, expected) => api('/accounts/local/login', {method: 'POST', expected,
    body: {schoolCode: school.code, username, password}});
  const workspaces = (token, expected) => api('/api/v2/me/workspaces?termId=' + term.id, {token, expected});
  async function teacherRelations(count) {
    const rows = await prisma.teachingAssignment.findMany({where: {accountId: teacher.id}});
    assert.equal(rows.length, count);
    if (count) assert.ok(rows[0].workspaceId === eight.id && rows[0].subjectId === physics.id && rows[0].position === 'CO_TEACHER');
    assert.equal(await prisma.workspaceMember.count({where: {accountId: teacher.id}}), count);
    assert.equal(await prisma.administrativeClassLeadership.count({where: {accountId: teacher.id}}), 0);
  }
  async function check(step) {
    switch (step) {
    case 'before-assignment': {
      teacher = await prisma.account.findFirstOrThrow({where: {providerId: localProviderId(school.code, teacherUsername)}});
      assert.equal(teacher.name, sameName.name);
      assert.equal(teacher.lastLoginAt, null);
      assert.equal(await prisma.schoolMember.count({where: {schoolId: school.id, accountId: teacher.id, role: 'VIEWER'}}), 1);
      await teacherRelations(0);
      assert.equal(createWrites, 1);
      assert.ok(lost && readReceipts >= 1);
      break;
    }
    case 'sign-in': {
      await teacherRelations(1);
      signedIn = await login(teacherUsername, teacherPin);
      const pages = await workspaces(signedIn.access_token);
      assert.deepEqual(pages.map(row => row.workspace.id), [eight.id]);
      assert.equal(pages[0].role, 'ASSISTANT');
      assert.ok((await prisma.account.findUniqueOrThrow({where: {id: teacher.id}})).lastLoginAt);
      break;
    }
    case 'disabled':
      await teacherRelations(1);
      await workspaces(signedIn.access_token, 401);
      await login(teacherUsername, teacherPin, 403);
      break;
    case 'restored': {
      await teacherRelations(1);
      await workspaces(signedIn.access_token, 401);
      afterRestore = await login(teacherUsername, teacherPin);
      assert.deepEqual((await workspaces(afterRestore.access_token)).map(row => row.workspace.id), [eight.id]);
      assert.equal((await prisma.account.findUniqueOrThrow({where: {id: teacher.id}})).localDisabled, false);
      break;
    }
    case 'pin-reset': {
      await teacherRelations(1);
      await workspaces(afterRestore.access_token, 401);
      await login(teacherUsername, teacherPin, 401);
      const credentials = await login(teacherUsername, resetPin);
      assert.deepEqual((await workspaces(credentials.access_token)).map(row => row.workspace.id), [eight.id]);
      break;
    }
    case 'audit': {
      const row = await prisma.auditLog.findFirstOrThrow({where: {schoolId: school.id, entityId: teacher.id, action: 'TEACHER_ACCOUNT_CREATED'}});
      assert.ok(row.summary?.startsWith('创建教师账号'));
      assert.equal(row.metadata?.body?.pin, '[REDACTED]');
      assert.equal(JSON.stringify(row).includes(teacherPin), false);
      break;
    }
    default: throw new Error('Unknown teacher test step');
    }
    checks.push(step);
  }

  const server = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url?.startsWith('/__teacher/')) {
      try { await check(req.url.slice('/__teacher/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error.message); res.writeHead(500).end(); }
      return;
    }
    const drop = req.method === 'POST' && req.url === directoryPath && !lost;
    if (req.method === 'POST' && req.url === directoryPath) createWrites++;
    if (req.method === 'GET' && req.url?.startsWith(directoryPath + '/receipts/')) readReceipts++;
    const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
      if (drop && reply.statusCode === 201) {
        reply.resume();
        reply.on('end', () => { lost = true; res.destroy(); });
        return;
      }
      res.writeHead(reply.statusCode, reply.headers);
      reply.pipe(res);
    });
    upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
    req.pipe(upstream);
  });
  try {
    await new Promise((resolve, reject) => {server.once('error', reject); server.listen(0, '127.0.0.1', resolve);});
    const access = join(directory, 'teacher-desktop.json');
    await writeFile(access, JSON.stringify({
      origin: 'http://127.0.0.1:' + server.address().port, schoolId: school.id, schoolCode: school.code, noTermSchoolId: other.id,
      password: pin, eightId: eight.id, nineId: rooms[1].id, physicsId: physics.id, teacherUsername, teacherPin,
      resetPin, sameNameId: sameName.id, allowTeacherTestWrites: true
    }), {mode: 0o600});
    await runDesktop(['--live-teacher-accounts', access, evidence]);
    await check('audit');
    assert.deepEqual(checks, ['before-assignment', 'sign-in', 'disabled', 'restored', 'pin-reset', 'audit']);
    await runDatabase();
    await writeFile(join(evidence, 'server-result.json'), JSON.stringify({checks, createWrites, readReceipts, lost, passed: true}, null, 2));
    console.log('PASS: real teacher sign-in, exact classroom access, disable/restore and redacted account audit after a lost response.');
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
}
