import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';

export async function prepareGradeCreation(prisma, origin, fixture) {
  const {school, other, pin} = fixture;
  const {accountId} = await prisma.schoolMember.findFirstOrThrow({where: {schoolId: school.id, role: 'ADMIN'}});
  const zero = await prisma.school.create({data: {code: 'ZERO-GRADES', name: '零年级测试学校'}});
  await prisma.schoolMember.create({data: {schoolId: zero.id, accountId, role: 'ADMIN'}});
  const start = await prisma.academicTerm.create({data: {schoolId: zero.id, name: '原启用学期', academicYear: 2026, semester: 1, status: 'ACTIVE'}});
  const next = await prisma.academicTerm.create({data: {schoolId: zero.id, name: '新启用学期', academicYear: 2026, semester: 2, status: 'DRAFT'}});
  return {
    access: { zeroGradeSchoolId: zero.id, nextTermId: next.id, allowGradeCreationTestWrites: true },
    async verify() {
      const grades = await prisma.grade.findMany({where: {termId: start.id}});
      assert.equal(grades.length, 1, 'only the desktop grade is written to the original term');
      assert.equal(grades[0].code, 'G-FIRST');
      const receipt = await prisma.$queryRaw`SELECT "gradeId", "actorId" FROM "CurrentGradeCreation" WHERE "schoolId" = ${zero.id}`;
      assert.deepEqual(receipt, [{gradeId: grades[0].id, actorId: accountId}]);
      assert.equal(await prisma.auditLog.count({where: {entityId: grades[0].id, action: 'GRADE_CREATED', success: true}}), 1);
      const rows = await prisma.workspace.findMany({where: {termId: start.id, type: 'ADMIN_CLASS'}});
      assert.equal(rows.length, 1);
      assert.equal(rows[0].gradeId, grades[0].id);
      assert.equal(rows[0].code, 'G-FIRST-01');
      assert.equal(await prisma.administrativeClassStudent.count({where: {administrativeClassId: rows[0].id, isActive: true}}), 1);
      assert.equal(await prisma.grade.count({where: {termId: next.id}}), 0, 'a refreshed stale form must not submit into the new term');
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: next.id}})).status, 'ACTIVE');

      const login = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
      assert.equal(login.status, 200);
      const headers = {Authorization: 'Bearer ' + (await login.json()).data.access_token, 'Content-Type': 'application/json'};
      const base = `/api/v2/admin/schools/${zero.id}`;
      const legacy = await fetch(origin + base + '/grades', {method: 'POST', headers,
        body: JSON.stringify({termId: start.id, code: 'WEB-GRADE', name: '旧网页归档学期年级', sortOrder: 13})});
      assert.equal(legacy.status, 201, 'legacy web still supports explicit non-current terms');
      assert.equal((await legacy.json()).data.sortOrder, 13);
      const forbidden = await fetch(`${origin}/api/v2/admin/schools/${other.id}/current-term/grades/${randomUUID()}?termId=${next.id}`, {headers});
      assert.equal(forbidden.status, 403);
      assert.equal((await forbidden.json()).code, 'SCHOOL_ADMIN_REQUIRED');
      await fetch(origin + '/accounts/logout', {method: 'POST', headers});
      console.log('PASS: zero grades, one receipt-bound grade and its first class and roster, term switch, one creation audit, legacy term grade and cross-school denial.');
    },
  };
}
