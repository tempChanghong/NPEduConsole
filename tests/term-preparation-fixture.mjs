import assert from 'node:assert/strict';
import {createHash, randomBytes} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, mkdtemp, rm, writeFile} from 'node:fs/promises';
import {basename, dirname, join, resolve} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyTermPreparation(options) {
  const evidence = resolve(options.evidence);
  await mkdir(evidence, {recursive: true});
  const privateRoot = await mkdtemp(join(evidence, 'native-state-'));
  try { await verify({...options, nativeState: join(privateRoot, 'devices')}); }
  finally {
    if (dirname(resolve(privateRoot)) === evidence && basename(privateRoot).startsWith('native-state-'))
      await rm(privateRoot, {recursive: true, force: true});
  }
}

async function verify({prisma, origin, fixture, directory, evidence, web, nativeState, runDesktop, runNative, runDatabase}) {
  const {school, other, term, pin, managerId} = fixture;
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
  await prisma.academicTerm.update({where: {id: term.id}, data: {startsAt: new Date('2026-09-01'), endsAt: new Date('2027-01-15')}});
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const teacher = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer', schoolMemberships: {some: {schoolId: school.id}}}});
  const [seven, eight] = await Promise.all([7, 8].map(n => prisma.workspace.create({data: {
    termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS', name: '高二（' + n + '）班', code: 'G2-' + n
  }})));
  const subject = await prisma.subject.create({data: {schoolId: school.id, name: '物理', code: 'TERM-PHY'}});
  await prisma.administrativeClassSubject.createMany({data: [{administrativeClassId: seven.id, deliveryMode: 'COURSE_GROUP'},
    {administrativeClassId: eight.id, deliveryMode: 'ADMIN_CLASS'}].map(row => ({...row, subjectId: subject.id, isCompulsory: true}))});
  await prisma.administrativeClassStudent.create({data: {administrativeClassId: seven.id, name: '原学期学生', studentNumber: '0701'}});
  await prisma.administrativeClassLeadership.create({data: {administrativeClassId: seven.id, accountId: teacher.id, position: 'HEAD_TEACHER'}});
  await prisma.gradeLeadership.create({data: {gradeId: grade.id, accountId: teacher.id, position: 'PRIMARY'}});

  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const payload = await response.json();
    assert.equal(response.status, status, path + ': ' + (payload.code ?? response.status));
    return payload.data ?? payload;
  }
  const login = await api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username: 'admin', password: pin}});
  const token = login.access_token;
  const schoolPath = '/api/v2/admin/schools/' + school.id;
  const base = schoolPath + '/term-preparation';
  const assign = workspaceId => api(schoolPath + '/teaching-assignments', {method: 'PUT', token,
    body: {workspaceId, subjectId: subject.id, accountId: teacher.id, position: 'PRIMARY'}});
  const groups = [];
  for (const label of ['A1', 'A2']) {
    const group = await api(schoolPath + '/course-groups', {method: 'POST', token, status: 201, body: {
      termId: term.id, gradeId: grade.id, subjectId: subject.id, name: '物理 ' + label, code: 'PHY-' + label,
      sourceClassIds: [seven.id], isStudentSelectable: true
    }});
    groups.push(group);
    await assign(group.id);
  }
  await prisma.workspaceMemberInvite.create({data: {workspaceId: groups[0].id, email: 'future-teacher@example.invalid',
    normalizedEmail: 'future-teacher@example.invalid', role: 'TEACHER', invitedByAccountId: managerId}});
  const [first, second, extra] = await Promise.all([seven, seven, eight].map((room, index) => prisma.classroomScreenBinding.create({data: {
    schoolId: school.id, administrativeClassId: room.id, name: index === 2 ? '八班大屏' : '七班大屏',
    npepPrepared: true, createdByAccountId: managerId, tokenHash: createHash('sha256').update(randomBytes(32)).digest('hex')
  }})));
  const nativeFixture = join(directory, 'term-preparation-native.json');
  await writeFile(nativeFixture, JSON.stringify({kind: 'TERM_PREPARATION_DISPOSABLE_DATABASE', origin, adminToken: token, schoolId: school.id,
    classId: seven.id, bindingIds: [first.id, second.id]}), {mode: 0o600});
  await runNative(['pair', nativeFixture, nativeState]);
  const nativeBefore = await prisma.npepDevice.findMany({where: {screenBindingId: {in: [first.id, second.id]}}, orderBy: {id: 'asc'}});
  assert.ok(nativeBefore.length === 2 && nativeBefore.every(row => row.state === 'ACTIVE' && row.lastReportedAt));

  const studentTerm = async () => {
    const active = await api('/api/v2/catalog/terms/current?schoolId=' + school.id);
    assert.equal(active.id, term.id);
    const courses = await api('/api/v2/catalog/administrative-classes/' + seven.id + '/course-options');
    const physics = courses.subjects.find(row => row.subject.id === subject.id);
    assert.deepEqual(physics.courseGroups.map(row => row.id).sort(), groups.map(row => row.id).sort());
  };
  await studentTerm();
  const backend = new URL(origin);
  const checks = [];
  let lost = false, receiptReads = 0, commits = 0, confirmedReceipt, createdTerm;
  async function checkpoint(command) {
    switch (command) {
    case 'change-source':
      await assign(eight.id);
      break;
    case 'no-draft':
      assert.equal(await prisma.academicTerm.count({where: {schoolId: school.id, status: 'DRAFT'}}), 0);
      assert.equal(commits, 1);
      break;
    case 'verify-draft': {
      assert.ok(lost && receiptReads === 2 && commits === 2 && confirmedReceipt);
      createdTerm = await prisma.academicTerm.findUniqueOrThrow({where: {id: confirmedReceipt.termId}});
      assert.equal(createdTerm.status, 'DRAFT');
      assert.equal(createdTerm.name, '2027 春季准备');
      const rooms = await prisma.workspace.findMany({where: {termId: createdTerm.id}, include: {
        sourceClasses: true, subjectRules: true, _count: {select: {members: true, pendingInvitations: true, teachingAssignments: true, leaderships: true,
          students: true}}
      }});
      const clonedSeven = rooms.find(row => row.code === seven.code);
      assert.ok(clonedSeven && clonedSeven.id !== seven.id);
      const newGroups = rooms.filter(row => row.type === 'COURSE_GROUP');
      assert.deepEqual(newGroups.map(row => row.code).sort(), ['PHY-A1', 'PHY-A2']);
      for (const row of newGroups) assert.deepEqual(row.sourceClasses.map(link => link.administrativeClassId), [clonedSeven.id]);
      const total = key => rooms.reduce((sum, row) => sum + row._count[key], 0);
      assert.equal(total('students'), 0);
      const actual = {
        grades: await prisma.grade.count({where: {termId: createdTerm.id}}),
        administrativeClasses: rooms.filter(row => row.type === 'ADMIN_CLASS').length, courseGroups: newGroups.length,
        workspaces: rooms.length, workspaceMembers: total('members'), teachingAssignments: total('teachingAssignments'),
        gradeLeaderships: await prisma.gradeLeadership.count({where: {grade: {termId: createdTerm.id}}}),
        classLeaderships: total('leaderships'), pendingInvitations: total('pendingInvitations'),
        sourceLinks: rooms.reduce((sum, row) => sum + row.sourceClasses.length, 0)
      };
      assert.deepEqual(actual, confirmedReceipt.counts);
      assert.deepEqual(actual, {grades: 1, administrativeClasses: 2, courseGroups: 2, workspaces: 4,
        workspaceMembers: 3, teachingAssignments: 3, gradeLeaderships: 1, classLeaderships: 1, pendingInvitations: 0, sourceLinks: 2});
      await studentTerm();
      assert.equal(await prisma.administrativeClassStudent.count({where: {administrativeClassId: seven.id}}), 1);
      const readiness = await api(base + '/drafts/' + createdTerm.id + '/check', {token});
      assert.deepEqual(readiness.screens.filter(row => row.nativeImpact === 'REPAIR_REQUIRED').map(row => row.bindingId).sort(), [first.id, second.id].sort());
      assert.equal(readiness.screens.find(row => row.bindingId === extra.id)?.nativeImpact, 'VERIFY');
      break;
    }
    case 'change-draft': {
      const room = await prisma.workspace.findUniqueOrThrow({where: {termId_code: {termId: createdTerm.id, code: eight.code}}});
      await api(schoolPath + '/administrative-classes/' + room.id, {method: 'PATCH', token,
        body: {name: '高二（8）班新版', code: 'G2-8-NEW', expectedUpdatedAt: room.updatedAt}});
      // Inject a legacy incomplete rule so readiness can exercise a missing compulsory walking group.
      await prisma.administrativeClassSubject.update({
        where: {administrativeClassId_subjectId: {administrativeClassId: room.id, subjectId: subject.id}},
        data: {deliveryMode: 'COURSE_GROUP', isCompulsory: true},
      });
      break;
    }
    default: throw new Error('unknown term preparation checkpoint');
    }
    checks.push(command);
  }

  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__term/')) {
      try { await checkpoint(req.url.slice('/__term/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error.message); res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'POST' && req.url === base) commits++;
    if (req.method === 'GET' && req.url.startsWith(base + '/receipts/')) {
      receiptReads++;
      if (receiptReads === 1) { res.writeHead(503).end(); return; }
    }
    const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
      if (!lost && req.method === 'POST' && req.url === base && reply.statusCode === 200) {
        const chunks = [];
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => {
          const body = Buffer.concat(chunks), result = JSON.parse(body).data;
          if (result?.state === 'APPLIED') { confirmedReceipt = result.receipt; lost = true; res.destroy(); }
          else { res.writeHead(reply.statusCode, reply.headers); res.end(body); }
        });
      } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const accessPath = join(directory, 'term-desktop.json');
    await writeFile(accessPath, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id, otherId: other.id,
      schoolCode: school.code, password: pin, termName: term.name, firstId: first.id, secondId: second.id,
      unmatchedId: extra.id, allowTermWrites: true}), {mode: 0o600});
    await runDesktop(['--live-term-preparation', accessPath, evidence]);
    assert.deepEqual(checks, ['change-source', 'no-draft', 'verify-draft', 'change-draft']);
    await runNative(['report', nativeFixture, nativeState]);
    const updated = await prisma.npepDevice.findMany({where: {screenBindingId: {in: [first.id, second.id]}}, orderBy: {id: 'asc'}});
    for (const [index, before] of nativeBefore.entries()) {
      const after = updated[index];
      for (const field of ['id', 'credentialId', 'screenBindingId', 'administrativeClassId', 'bindingRevision', 'state'])
        assert.equal(after[field], before[field]);
      assert.ok(after.lastReportedAt > before.lastReportedAt && after.statusEpoch > before.statusEpoch);
    }
    const original = await prisma.classroomScreenBinding.findMany({where: {id: {in: [first.id, second.id]}}});
    assert.ok(original.length === 2 && original.every(row => row.administrativeClassId === seven.id && row.isActive));
    assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: createdTerm.id}})).status, 'DRAFT');
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [seven], expectedTermId: term.id});
    await runDatabase();
    await writeFile(join(evidence, 'server-result.json'), JSON.stringify({checks, commits, receiptReads, clonedCounts: confirmedReceipt.counts,
      studentActiveTermPreserved: true, deviceReceiptsBeforeAndAfter: 4, passed: true}, null, 2));
    console.log('PASS: term preparation, exact inheritance and readback, real native paired reports before/after, real Edge student active term, PostgreSQL regressions.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
