import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';

export async function prepareGradeRename(prisma, origin, fixture) {
  const {school, other, term, pin} = fixture;
  const [classId] = fixture.classes;
  const room = await prisma.workspace.findUniqueOrThrow({where: {id: classId}});
  const grade = await prisma.grade.findUniqueOrThrow({where: {id: room.gradeId}});
  const next = await prisma.academicTerm.create({data: {
    schoolId: school.id, academicYear: term.academicYear, semester: 2, name: '新启用学期', status: 'DRAFT',
  }});
  const nextGrade = await prisma.grade.create({data: {termId: next.id, code: grade.code, name: '新学期年级'}});
  const legacyGrade = await prisma.grade.create({data: {termId: term.id, code: 'LEGACY', name: '旧网页编辑年级'}});
  const learner = await prisma.administrativeClassStudent.create({data: {
    administrativeClassId: classId, name: '模拟学生甲', studentNumber: '0101', sortOrder: 1,
  }});
  const base = `/api/v2/admin/schools/${school.id}`;
  const writePath = `${base}/current-term/grades/${grade.id}/name`;
  const target = new URL(origin);
  let switched = 0;
  let shiftStatus;
  const relay = createServer(async (req, res) => {
    try {
      const isRename = req.method === 'PATCH' && req.url === writePath;
      if (isRename) switched += 1;
      if (isRename && switched === 1) {
        await prisma.$transaction([
          prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}}),
          prisma.academicTerm.update({where: {id: next.id}, data: {status: 'ACTIVE'}}),
        ]);
      }
      const forward = request({hostname: '127.0.0.1', port: target.port, path: req.url, method: req.method,
        headers: {...req.headers, host: target.host}}, upstream => {
        if (isRename) shiftStatus = upstream.statusCode;
        res.writeHead(upstream.statusCode, upstream.headers);
        upstream.pipe(res);
      });
      forward.on('error', () => res.writeHead(502).end());
      req.pipe(forward);
    } catch {
      res.writeHead(502).end();
    }
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  return {
    server: relay,
    access: {
      termId: term.id, gradeId: grade.id, gradeName: grade.name, classId, studentId: learner.id,
      secondUsername: 'admin2', secondPassword: pin, shiftTermOrigin: `http://127.0.0.1:${relay.address().port}`,
      allowGradeRenameTestWrites: true,
    },
    matches: path => path === writePath,
    async verify() {
      assert.equal(switched, 1, 'one explicit desktop attempt after switching the term');
      assert.equal(shiftStatus, 409);
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: next.id}})).status, 'ACTIVE');
      assert.deepEqual(await prisma.grade.findUniqueOrThrow({where: {id: nextGrade.id}}), nextGrade);
      const updated = await prisma.grade.findUniqueOrThrow({where: {id: grade.id}});
      assert.deepEqual({...updated, name: grade.name, updatedAt: grade.updatedAt}, grade, 'only name and version change');
      assert.notEqual(updated.name, grade.name);
      assert.deepEqual(await prisma.workspace.findUniqueOrThrow({where: {id: classId}}), room);
      assert.deepEqual(await prisma.administrativeClassStudent.findUniqueOrThrow({where: {id: learner.id}}), learner);
      const audits = await prisma.auditLog.findMany({where: {
        schoolId: school.id, entityId: grade.id, action: 'GRADE_UPDATED', success: true,
      }, orderBy: {createdAt: 'asc'}});
      assert.equal(audits.length, 2, 'only the initial rename and the rename with the lost reply commit');
      assert.equal(audits[0].metadata.previousName, grade.name);
      assert.equal(audits[1].metadata.previousName, audits[0].metadata.name);
      assert.equal(audits[1].metadata.name, updated.name);

      const login = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
      assert.equal(login.status, 200);
      const headers = {Authorization: 'Bearer ' + (await login.json()).data.access_token, 'Content-Type': 'application/json'};
      const patch = (path, body) => fetch(origin + path, {method: 'PATCH', headers, body: JSON.stringify(body)});
      const forbidden = await patch(`/api/v2/admin/schools/${other.id}/current-term/grades/${grade.id}/name`,
        {termId: term.id, name: '跨校误写', expectedUpdatedAt: updated.updatedAt});
      assert.equal(forbidden.status, 403);
      assert.equal((await forbidden.json()).code, 'SCHOOL_ADMIN_REQUIRED');
      const legacy = await patch(`${base}/grades/${legacyGrade.id}`, {
        name: '网页修改往期年级', code: 'LEGACY-WEB', sortOrder: 23, expectedUpdatedAt: legacyGrade.updatedAt,
      });
      assert.equal(legacy.status, 200);
      const {data} = await legacy.json();
      assert.equal(data.termId, term.id);
      assert.equal(data.name, '网页修改往期年级');
      assert.equal(data.code, 'LEGACY-WEB');
      assert.equal(data.sortOrder, 23);
      await fetch(origin + '/accounts/logout', {method: 'POST', headers});
      console.log('PASS: only two renames committed, original grade/class/student IDs and metadata retained, term change refused, cross-school denied, historical web edit retained.');
    },
  };
}
