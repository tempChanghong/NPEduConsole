import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';

export async function verifyStaffResponsibilities({prisma, origin, fixture, directory, evidence, bcrypt, localProviderId, runDesktop, runDatabase}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, pin} = fixture;
  const secondSchool = await prisma.school.create({data: {code: 'STAFF-SECOND', name: '隔离校验学校'}});
  await prisma.schoolMember.create({data: {schoolId: secondSchool.id, accountId: fixture.managerId, role: 'ADMIN'}});
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const anotherGrade = await prisma.grade.create({data: {termId: term.id, code: 'G1', name: '高一'}});
  const [eight, nine, unrelated] = await Promise.all([8, 9, 7].map(number => {
    const target = number === 7 ? anotherGrade : grade;
    return prisma.workspace.create({data: {termId: term.id, gradeId: target.id, code: `${target.code}-${number}`,
      name: `${target.name}（${number}）班`, type: 'ADMIN_CLASS'}});
  }));
  const physics = await prisma.subject.create({data: {schoolId: school.id, code: 'STAFF-PHY', name: '物理'}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: eight.id, subjectId: physics.id, deliveryMode: 'ADMIN_CLASS'}});
  const walk = await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, code: 'STAFF-WALK', name: '物理走班',
    type: 'COURSE_GROUP', subjectId: physics.id}});
  await prisma.workspaceSourceClass.create({data: {workspaceId: walk.id, administrativeClassId: eight.id}});
  async function account(username, name, role = 'VIEWER') {
    const result = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(school.code, username),
      localUsername: username, name, localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: school.code}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: result.id, role}});
    return result;
  }
  const first = await account('staff-t1', '张老师');
  const second = await account('staff-t2', '张老师');
  const third = await account('staff-t3', '陈老师');
  const fourth = await account('staff-t4', '林老师');
  const fifth = await account('staff-t5', '周老师');
  await account('staff-admin', '另一位学校管理员', 'ADMIN');
  const gradeRows = [[grade.id, third.id], [grade.id, fourth.id], [anotherGrade.id, third.id]];
  await prisma.gradeLeadership.createMany({data: gradeRows.map(([gradeId, accountId]) => ({gradeId, accountId, position: 'PRIMARY'}))});

  const schoolPath = `/api/v2/admin/schools/${school.id}`;
  const overviewPath = schoolPath + '/management-overview/current';
  const currentPath = (kind, id) => schoolPath + `/current-term/staff-responsibilities/${kind}/${id}`;
  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const headers = {'Content-Type': 'application/json'};
    if (token) headers.Authorization = `Bearer ${token}`;
    const response = await fetch(origin + path, {method, headers, body: body && JSON.stringify(body)});
    const payload = await response.json();
    assert.equal(response.status, status, `${path}: ${payload.code ?? response.status}`);
    return status >= 400 ? payload : payload.data ?? payload;
  }
  const login = async username => (await api('/accounts/local/login', {method: 'POST',
    body: {schoolCode: school.code, username, password: pin}})).access_token;
  const [admin, otherAdmin, firstToken, secondToken] = await Promise.all(['admin', 'staff-admin', 'staff-t1', 'staff-t2'].map(login));
  const originalTeaching = await api(schoolPath + '/teaching-assignments', {method: 'PUT', token: admin,
    body: {workspaceId: eight.id, subjectId: physics.id, accountId: first.id, position: 'PRIMARY'}});
  const access = async token => new Map((await api(`/api/v2/me/workspaces?termId=${term.id}`, {token})).map(row => [row.workspace.id, row]));
  const oldOverview = await api(overviewPath, {token: admin});
  const legacyTask = oldOverview.tasks.find(task => task.code === 'CLASS_WITHOUT_HOMEROOM' && task.classId === eight.id);
  assert.equal(legacyTask?.action, 'OPEN_WEB');
  const newOverview = await api(overviewPath + '?staffActions=1', {token: admin});
  assert.equal(newOverview.tasks.find(task => task.code === 'CLASS_WITHOUT_HOMEROOM' && task.classId === eight.id)?.action, 'OPEN_CLASS_RESPONSIBILITY');
  assert.equal(newOverview.tasks.find(task => task.code === 'MULTIPLE_PRIMARY_GRADE_LEADERS' && task.gradeId === grade.id)?.action, 'OPEN_GRADE_RESPONSIBILITY');

  const backend = new URL(origin);
  let classWrites = 0, lost = false, firstChecked = false, handoffChecked = false, race = false;
  const server = createServer(async (req, res) => {
    try {
      if (req.method === 'POST' && req.url?.startsWith('/__staff/')) {
        switch (req.url) {
        case '/__staff/check-first': {
          const a = await access(firstToken), b = await access(secondToken);
          assert.equal(a.get(eight.id)?.responsibilityDerived, true);
          assert.equal(a.get(walk.id)?.role, 'VIEWER');
          assert.ok(!b.has(eight.id));
          firstChecked = true;
          break;
        }
        case '/__staff/check-handoff': {
          const a = await access(firstToken), b = await access(secondToken);
          assert.equal(a.get(eight.id)?.role, 'TEACHER');
          assert.equal(a.get(eight.id)?.responsibilityDerived, undefined);
          assert.ok(!a.has(walk.id));
          assert.equal(b.get(eight.id)?.responsibilityDerived, true);
          assert.equal(b.get(walk.id)?.role, 'VIEWER');
          assert.ok(!b.has(nine.id) && !b.has(unrelated.id));
          assert.equal(await prisma.workspaceMember.count({where: {workspaceId: eight.id, accountId: second.id}}), 0);
          handoffChecked = true;
          break;
        }
        case '/__staff/race-grade':
          await api(schoolPath + '/grade-leaderships', {method: 'PUT', token: otherAdmin,
            body: {gradeId: grade.id, accountId: fifth.id, position: 'DEPUTY'}});
          race = true;
          break;
        case '/__staff/switch-term':
          await prisma.$transaction(async tx => {
            await tx.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}});
            await tx.academicTerm.create({data: {schoolId: school.id, academicYear: 2026, semester: 2, name: '下一学期', status: 'ACTIVE'}});
          });
          break;
        default: res.writeHead(404).end(); return;
        }
        res.writeHead(204).end();
        return;
      }

      const classSave = req.method === 'PUT' && req.url === currentPath('classes', eight.id);
      if (classSave) classWrites++;
      const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
        if (classSave && classWrites === 2 && reply.statusCode === 200) {
          const chunks = [];
          reply.on('data', chunk => chunks.push(chunk));
          reply.on('end', () => {
            const body = Buffer.concat(chunks);
            if (JSON.parse(body).data.state === 'APPLIED') { lost = true; res.destroy(); }
            else { res.writeHead(reply.statusCode, reply.headers); res.end(body); }
          });
        } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
      });
      upstream.on('error', () => res.destroy());
      req.pipe(upstream);
    } catch (error) { console.error(error.message); res.writeHead(500).end(error.message); }
  });
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const path = join(directory, 'desktop.json');
    await writeFile(path, JSON.stringify({origin: `http://127.0.0.1:${server.address().port}`, schoolId: school.id, schoolCode: school.code,
      password: pin, eightId: eight.id, nineId: nine.id, gradeId: grade.id, firstId: first.id, secondId: second.id, thirdId: third.id,
      fourthId: fourth.id, fifthId: fifth.id, allowStaffTestWrites: true}), {mode: 0o600});
    await runDesktop(['--live-staff-responsibilities', path, evidence]);
    assert.ok(lost && firstChecked && handoffChecked && race);
    assert.equal(classWrites, 2, 'Lost write acknowledgement only triggered a read');
    assert.equal((await prisma.teachingAssignment.findUniqueOrThrow({where: {id: originalTeaching.id}})).accountId, first.id);
    assert.equal(await prisma.administrativeClassLeadership.count({where: {administrativeClassId: nine.id}}), 0);
    assert.deepEqual((await prisma.gradeLeadership.findMany({where: {gradeId: grade.id}})).map(row => [row.accountId, row.position]).sort(),
      [[second.id, 'PRIMARY'], [third.id, 'DEPUTY'], [fifth.id, 'DEPUTY']].sort());
    assert.equal((await prisma.gradeLeadership.findUniqueOrThrow({where: {gradeId_accountId: {
      gradeId: anotherGrade.id, accountId: third.id}}})).position, 'PRIMARY');
    assert.equal((await api(`/api/v2/admin/schools/${other.id}/current-term/staff-responsibilities/classes/${eight.id}?termId=${term.id}`,
      {token: admin, status: 403})).code, 'SCHOOL_ADMIN_REQUIRED');
    const historical = await api(schoolPath + `/staff-responsibilities?termId=${term.id}`, {token: admin});
    assert.equal(historical.term.status, 'ARCHIVED');
    assert.ok(historical.administrativeClasses.find(room => room.id === eight.id).leaderships.some(row => row.accountId === second.id));
    console.log('PASS: both real teacher sessions verified independent access; original physics, unrelated grade and archived web responsibilities remain linked.');
    await runDatabase();
  } finally {
    const closed = new Promise(resolve => server.close(resolve));
    server.closeAllConnections();
    await closed;
  }
}
