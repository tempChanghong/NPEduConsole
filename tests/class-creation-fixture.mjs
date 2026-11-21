import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {setTimeout as delay} from 'node:timers/promises';

export async function prepareClassCreation(prisma, origin, fixture) {
  const {school, other, term, pin} = fixture;
  const {accountId} = await prisma.schoolMember.findFirstOrThrow({where: {schoolId: school.id, role: 'ADMIN'}});
  const emptyGrade = await prisma.grade.create({data: {termId: term.id, code: 'G3', name: '高三'}});
  const emptySchool = await prisma.school.create({data: {code: 'FIRST', name: '新建校区'}});
  await prisma.schoolMember.create({data: {schoolId: emptySchool.id, accountId, role: 'ADMIN'}});
  const emptyTerm = await prisma.academicTerm.create({data: {schoolId: emptySchool.id, name: '秋季', academicYear: 2026, semester: 1, status: 'ACTIVE'}});
  const firstGrade = await prisma.grade.create({data: {termId: emptyTerm.id, code: 'G1', name: '高一'}});
  return {
    access: {termId: term.id, emptyGradeId: emptyGrade.id, emptySchoolId: emptySchool.id, allowClassCreationTestWrites: true},
    async verify() {
      const receipts = await prisma.$queryRaw`SELECT "classId" FROM "AdministrativeClassCreation" WHERE "schoolId" = ${school.id}`;
      assert.equal(receipts.length, 1);
      const [receipt] = receipts;
      assert.equal(await prisma.auditLog.count({where: {schoolId: school.id, action: 'ADMIN_CLASS_CREATED', success: true}}), 1);
      assert.equal(await prisma.auditLog.count({where: {entityId: receipt.classId, action: 'ADMIN_CLASS_CREATED', success: true}}), 1);
      assert.equal(await prisma.administrativeClassStudent.count({where: {administrativeClassId: receipt.classId, isActive: true}}), 2);
      const created = await prisma.workspace.findUniqueOrThrow({where: {id: receipt.classId}});
      assert.equal(created.code, 'G3-01');
      assert.equal(created.isStudentSelectable, false);
      const login = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
      assert.equal(login.status, 200);
      const headers = {Authorization: 'Bearer ' + (await login.json()).data.access_token, 'Content-Type': 'application/json'};
      const path = id => origin + '/api/v2/admin/schools/' + id + '/current-term/administrative-classes';
      const input = {termId: emptyTerm.id, gradeId: firstGrade.id, requestId: randomUUID(), code: 'PARALLEL', name: '新一班', isStudentSelectable: true};
      const post = (schoolId, body) => fetch(path(schoolId), {method: 'POST', headers, body: JSON.stringify(body)});
      const rejects = async (response, status, code) => {
        assert.equal(response.status, status);
        assert.equal((await response.json()).code, code);
      };
      const pair = await Promise.all([post(emptySchool.id, input), post(emptySchool.id, input)]);
      assert.deepEqual(pair.map(r => r.status).sort(), [200, 201]);
      const ids = await Promise.all(pair.map(async r => (await r.json()).data.id));
      assert.equal(ids[0], ids[1]);
      const concurrentCode = {...input, code: 'SAME-CODE'};
      const competitors = await Promise.all([randomUUID(), randomUUID()].map(requestId => post(emptySchool.id, {...concurrentCode, requestId})));
      assert.deepEqual(competitors.map(r => r.status).sort(), [201, 409]);
      await rejects(competitors.find(r => r.status === 409), 409, 'WORKSPACE_CODE_EXISTS');
      await rejects(await post(emptySchool.id, {...input, gradeId: emptyGrade.id, requestId: randomUUID()}), 404, 'GRADE_INVALID');
      await rejects(await post(other.id, input), 403, 'SCHOOL_ADMIN_REQUIRED');
      await prisma.workspace.update({where: {id: ids[0]}, data: {isActive: false}});
      await rejects(await post(emptySchool.id, input), 409, 'CLASS_CREATION_TARGET_CHANGED');
      await prisma.workspace.delete({where: {id: ids[0]}});
      await rejects(await post(emptySchool.id, input), 409, 'CLASS_CREATION_TARGET_CHANGED');
      assert.equal(await prisma.workspace.count({where: {id: ids[0]}}), 0);

      // Once the active term switch starts, a create based on the open dialog must wait for it.
      const attempted = {...input, code: 'AFTER', requestId: randomUUID()};
      let pending;
      await prisma.$transaction(async tx => {
        await tx.academicTerm.update({where: {id: emptyTerm.id}, data: {status: 'ARCHIVED'}});
        pending = post(emptySchool.id, attempted);
        // Prove the HTTP writer actually reached the locked term before allowing the switch to commit.
        for (let i = 0; ; i++) {
          const [waiting] = await prisma.$queryRaw`SELECT EXISTS(SELECT 1 FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%"AcademicTerm"%FOR SHARE%') AS blocked`;
          if (waiting.blocked) break;
          assert.ok(i < 100, 'class creation should wait on the active term row');
          await delay(20);
        }
      }, {timeout: 10000});
      await rejects(await pending, 409, 'CLASS_CREATION_TERM_CHANGED');
      await rejects(await post(emptySchool.id, input), 409, 'CLASS_CREATION_TARGET_CHANGED');
      const noTerm = await fetch(path(emptySchool.id), {headers});
      assert.deepEqual((await noTerm.json()).data, {term: null, grades: []});
      assert.equal(await prisma.workspace.count({where: {termId: emptyTerm.id, code: 'AFTER'}}), 0);
      // The legacy administrative endpoint still supports preparing a non-current term.
      const legacy = await fetch(path(emptySchool.id).replace('/current-term', ''), {method: 'POST', headers, body: JSON.stringify(attempted)});
      assert.equal(legacy.status, 201);
      await prisma.schoolMember.delete({where: {schoolId_accountId: {schoolId: emptySchool.id, accountId}}});
      await rejects(await post(emptySchool.id, input), 403, 'SCHOOL_ADMIN_REQUIRED');
      console.log('PASS: concurrent create/retry, scoped grade, inactive class, term switch, legacy term creation, revoked role, one creation audit and first roster.');
    },
  };
}
