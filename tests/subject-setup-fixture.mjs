import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';

export async function verifySubjectSetup({prisma, origin, fixture, directory, evidence, createTeacher, runDesktop, runDatabase}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, pin} = fixture;
  const grade = await prisma.grade.create({data: {termId: term.id, code: 'G1', name: '高一'}});
  const [seven, eight, nine] = await Promise.all([7, 8, 9].map(number => prisma.workspace.create({data: {
    termId: term.id, gradeId: grade.id, code: 'G1-' + number, name: '高一（' + number + '）班', type: 'ADMIN_CLASS',
  }})));
  const [math, english, physics] = await Promise.all([['MATH', '数学'], ['ENG', '英语'], ['PHY', '物理']]
    .map(([code, name]) => prisma.subject.create({data: {schoolId: school.id, code, name}})));
  const teacher = await createTeacher('setupteacher', '陈老师');
  const originalTeacher = await createTeacher('originalmath', '王老师');
  const admin2 = await createTeacher('subjectadmin2', '第二管理员', 'ADMIN');
  const emptySchool = await prisma.school.create({data: {code: 'NO-SUBJECTS', name: '待维护学科的学校'}});
  await prisma.schoolMember.create({data: {schoolId: emptySchool.id, accountId: fixture.managerId, role: 'ADMIN'}});
  const emptyTerm = await prisma.academicTerm.create({data: {schoolId: emptySchool.id, academicYear: 2026, semester: 1,
    status: 'ACTIVE', name: '秋季学期'}});
  const emptyClass = await prisma.workspace.create({data: {termId: emptyTerm.id, type: 'ADMIN_CLASS', code: 'G1-1', name: '高一（1）班'}});
  const originalRule = await prisma.administrativeClassSubject.create({data: {administrativeClassId: eight.id,
    subjectId: math.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true}});
  const walkingRule = await prisma.administrativeClassSubject.create({data: {administrativeClassId: nine.id,
    subjectId: math.id, deliveryMode: 'COURSE_GROUP', isCompulsory: true}});
  const group = await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, code: 'MATH-GROUP',
    name: '数学走班', subjectId: math.id, type: 'COURSE_GROUP'}});
  const source = await prisma.workspaceSourceClass.create({data: {workspaceId: group.id, administrativeClassId: nine.id}});
  const originalAssignment = await prisma.teachingAssignment.create({data: {workspaceId: eight.id,
    subjectId: math.id, accountId: originalTeacher.id, position: 'PRIMARY'}});
  const originalMember = await prisma.workspaceMember.create({data: {workspaceId: eight.id, accountId: originalTeacher.id, role: 'TEACHER'}});
  const originalRoster = await prisma.administrativeClassStudent.create({data: {administrativeClassId: seven.id, name: '原名单学生', studentNumber: '01'}});

  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const headers = {'Content-Type': 'application/json'};
    if (token) headers.Authorization = 'Bearer ' + token;
    const response = await fetch(origin + path, {method, headers, body: body && JSON.stringify(body)});
    const payload = await response.json();
    assert.equal(response.status, status, path + ': ' + (payload.code ?? response.status));
    return status >= 400 ? payload : payload.data;
  }
  const login = async username => (await api('/accounts/local/login', {method: 'POST',
    body: {schoolCode: school.code, username, password: pin}})).access_token;
  const token = await login('admin');
  const second = await login(admin2.localUsername);
  const prefix = '/api/v2/admin/schools/' + school.id;
  const setup = id => prefix + '/current-term/administrative-classes/' + id + '/subject-setup';
  const legacy = id => prefix + '/administrative-classes/' + id + '/subject-rules';
  const preview = (id, targets) => api(setup(id) + '/preview', {method: 'POST', token, body: {termId: term.id, targets}});
  const body = (targets, receipt) => ({termId: term.id, targets,
    expected: receipt.rows.map(({classId, subjectId, version}) => ({classId, subjectId, version}))});
  const commit = (id, targets, receipt, status = 200) => api(setup(id), {method: 'POST', token, status, body: body(targets, receipt)});
  const missing = async () => (await api(prefix + '/management-overview/current', {token})).tasks
    .filter(task => task.code === 'ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER' && task.subjectId === math.id)
    .map(task => task.classId).sort();
  const mathSetup = [seven, eight, nine].map(room => ({classId: room.id, subjectId: math.id}));
  assert.deepEqual(await missing(), []);
  const review = await preview(seven.id, mathSetup);
  assert.deepEqual(review.rows.map(row => row.state), ['ADD', 'PRESENT', 'WALKING_CONFLICT']);
  assert.equal((await commit(seven.id, mathSetup, review)).state, 'BLOCKED');
  assert.equal(await prisma.administrativeClassSubject.count({where: {administrativeClassId: seven.id}}), 0);

  const backend = new URL(origin);
  let lost = false, checkPending = false;
  const relay = createServer(async (req, res) => {
    if (req.url === '/__subject-setup/check-pending' && req.method === 'POST') {
      try {
        assert.deepEqual(await missing(), [seven.id]);
        checkPending = true;
        res.writeHead(204).end();
      } catch (error) {
        res.writeHead(500).end(error.message);
      }
      return;
    }
    const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
      if (!lost && req.method === 'POST' && req.url === setup(seven.id) && reply.statusCode === 200) {
        const data = [];
        reply.on('data', chunk => data.push(chunk));
        reply.on('end', () => {
          const wire = Buffer.concat(data);
          const payload = JSON.parse(wire).data;
          if (payload.state === 'APPLIED') { lost = true; res.destroy(); return; }
          res.writeHead(reply.statusCode, reply.headers); res.end(wire);
        });
      } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const access = join(directory, 'desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, password: pin, gradeId: grade.id, sevenId: seven.id, eightId: eight.id, nineId: nine.id,
      mathId: math.id, teacherId: teacher.id, emptySchoolId: emptySchool.id, emptyClassId: emptyClass.id,
      allowSubjectSetupTestWrites: true}), {mode: 0o600});
    await runDesktop(['--live-subject-setup', access, evidence]);
    assert.ok(lost && checkPending);
    assert.deepEqual(await missing(), []);
    assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: seven.id, subjectId: math.id, accountId: teacher.id}}), 1);
    const ruleKey = administrativeClassId => ({administrativeClassId_subjectId: {administrativeClassId, subjectId: math.id}});
    assert.deepEqual(await prisma.administrativeClassSubject.findUniqueOrThrow({where: ruleKey(eight.id)}), originalRule);
    assert.deepEqual(await prisma.administrativeClassSubject.findUniqueOrThrow({where: ruleKey(nine.id)}), walkingRule);
    assert.deepEqual(await prisma.workspaceSourceClass.findUniqueOrThrow({
      where: {workspaceId_administrativeClassId: {workspaceId: group.id, administrativeClassId: nine.id}},
    }), source);
    assert.deepEqual(await prisma.teachingAssignment.findUniqueOrThrow({where: {id: originalAssignment.id}}), originalAssignment);
    assert.deepEqual(await prisma.workspaceMember.findUniqueOrThrow({
      where: {workspaceId_accountId: {workspaceId: eight.id, accountId: originalTeacher.id}},
    }), originalMember);
    assert.deepEqual(await prisma.administrativeClassStudent.findUniqueOrThrow({where: {id: originalRoster.id}}), originalRoster);
    console.log('PASS: real KV verified only class 7 received the math task and preserved roster, assignments, source links and permissions.');

    await api(legacy(seven.id), {method: 'PUT', token: second, status: 409,
      body: {expectedUpdatedAt: seven.updatedAt, subjectRules: [{subjectId: english.id, deliveryMode: 'ADMIN_CLASS'}]}});
    const ten = await prisma.workspace.findUniqueOrThrow({where: {termId_code: {termId: term.id, code: 'G1-C10'}}});
    const eleven = await prisma.workspace.findUniqueOrThrow({where: {termId_code: {termId: term.id, code: 'G1-C11'}}});
    const more = [
      {classId: ten.id, subjectId: english.id},
      {classId: eleven.id, subjectId: physics.id},
      {classId: eleven.id, subjectId: english.id},
    ];
    const beforeLegacy = await preview(ten.id, more);
    assert.deepEqual(beforeLegacy.rows.map(row => row.state), ['ADD', 'ADD', 'ADD']);
    await api(legacy(ten.id), {method: 'PUT', token: second, body: {
      expectedUpdatedAt: ten.updatedAt, subjectRules: [{subjectId: english.id, deliveryMode: 'COURSE_GROUP'}],
    }});
    const stale = await commit(ten.id, more, beforeLegacy);
    assert.equal(stale.state, 'STALE');
    assert.deepEqual(stale.rows.map(row => [row.classId, row.subjectId, row.state]), [[ten.id, english.id, 'WALKING_CONFLICT']]);
    assert.equal(await prisma.administrativeClassSubject.count({where: {administrativeClassId: eleven.id}}), 0);
    const remaining = more.slice(1);
    const cleanPreview = await preview(ten.id, more);
    const filtered = {rows: cleanPreview.rows.filter(row => row.classId === eleven.id)};
    assert.deepEqual((await commit(ten.id, remaining, filtered)).rows.map(row => row.state), ['ADDED', 'ADDED']);
    assert.deepEqual((await commit(ten.id, remaining, filtered)).rows.map(row => row.state), ['PRESENT', 'PRESENT']);
    const forbidden = '/api/v2/admin/schools/' + other.id + '/current-term/administrative-classes/' + ten.id + '/subject-setup';
    await api(forbidden, {method: 'POST', token, status: 403, body: body(remaining, filtered)});
    const old = await preview(ten.id, remaining);
    const next = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2026, semester: 2, status: 'DRAFT', name: '下一学期'}});
    await prisma.$transaction([
      prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}}),
      prisma.academicTerm.update({where: {id: next.id}, data: {status: 'ACTIVE'}}),
    ]);
    assert.equal((await commit(ten.id, remaining, old, 409)).code, 'SUBJECT_SETUP_TERM_CHANGED');
    await api(legacy(ten.id), {method: 'PUT', token: second,
      body: {subjectRules: [{subjectId: english.id, deliveryMode: 'COURSE_GROUP'}, {subjectId: physics.id, deliveryMode: 'ADMIN_CLASS'}]}});
    console.log('PASS: multi-subject cell exclusion, stale web/editor and Console writes, idempotency, term change, cross-school rejection, and archived legacy compatibility.');
    await runDatabase();
  } finally {
    const closed = new Promise(resolve => relay.close(resolve));
    relay.closeAllConnections();
    await closed;
  }
}
