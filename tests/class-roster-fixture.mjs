import assert from 'node:assert/strict';
import {createHash, randomBytes} from 'node:crypto';

export async function prepareClassRoster(prisma, origin, fixture) {
  const [classId, , emptyClassId] = fixture.classes;
  const screen = await prisma.classroomScreenBinding.findFirstOrThrow({where: {administrativeClassId: classId}});
  const screenToken = randomBytes(32).toString('hex');
  const screenHeaders = {'X-Classworks-Screen-Token': screenToken, 'Content-Type': 'application/json'};
  await prisma.classroomScreenBinding.update({where: {id: screen.id}, data: {tokenHash: createHash('sha256').update(screenToken).digest('hex')}});
  const past = await prisma.academicTerm.create({data: {schoolId: fixture.school.id, name: '往期', academicYear: 2025, semester: 1, status: 'ARCHIVED'}});
  const archived = await prisma.workspace.create({data: {termId: past.id, name: '往期班', code: 'PAST', type: 'ADMIN_CLASS'}});
  await prisma.administrativeClassStudent.createMany({data: [
    ['roster-a', '陈晨', '001'], ['roster-b', '陈晨', '002'], ['roster-history', '李明', null],
  ].map(([id, name, studentNumber], sortOrder) => ({administrativeClassId: classId, id, name, studentNumber, sortOrder}))});
  const attendance = origin + '/api/v2/classroom-screens/attendance/2026-04-15';
  const saved = await fetch(attendance, {method: 'PUT', headers: screenHeaders, body: JSON.stringify({absent: ['roster-history']})});
  assert.equal(saved.status, 200);
  return {
    access: {classId, emptyClassId, archivedClassId: archived.id, screenToken, allowRosterTestWrites: true},
    async verify() {
      const history = await fetch(attendance, {headers: screenHeaders});
      assert.deepEqual((await history.json()).data.absent, ['roster-history']);
      const removed = await prisma.administrativeClassStudent.findUniqueOrThrow({where: {id: 'roster-history'}});
      assert.equal(removed.isActive, false);
      assert.equal(removed.name, '李明');
      const audits = await prisma.auditLog.findMany({where: {schoolId: fixture.school.id, action: 'CLASS_ROSTER_SAVED', entityId: classId}});
      assert.equal(audits.length, 3, 'one record for each successful screen/Console write, including a lost reply');
      assert.ok(audits.some(row => row.actorType === 'CLASSROOM_SCREEN'));
      assert.ok(audits.some(row => row.metadata.before.some(s => s.id === 'roster-history') && !row.metadata.after.some(s => s.id === 'roster-history')));
      console.log('PASS: retained attendance and student history, screen and manager audits, exactly one forwarded write after a lost reply.');
    },
  };
}
