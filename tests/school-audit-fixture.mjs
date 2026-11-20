import assert from 'node:assert/strict';
import {setTimeout as wait} from 'node:timers/promises';

export async function prepareSchoolAudit(prisma, origin, fixture) {
  const schoolId = fixture.school.id;
  const admin = await prisma.schoolMember.findFirstOrThrow({where: {schoolId, role: 'ADMIN'}});
  const screen = await prisma.classroomScreenBinding.findFirstOrThrow({where: {schoolId}});
  const empty = await prisma.school.create({data: {code: 'EMPTY', name: '尚无班级的学校'}});
  const membership = {schoolId: empty.id, accountId: admin.accountId};
  await prisma.schoolMember.create({data: {...membership, role: 'OWNER'}});
  const start = new Date(2026, 3, 15), end = new Date(2026, 3, 16);
  const makeRow = (id, changes = {}) => ({
    id, schoolId, actorType: 'ACCOUNT', actorAccountId: admin.accountId, action: 'GRADE_UPDATED',
    success: true, createdAt: start, ...changes,
  });
  // Tied timestamps cross a page boundary. A past roster snapshot intentionally predates redaction.
  await prisma.auditLog.createMany({data: [
    ...Array.from({length: 52}, (_, i) => makeRow('audit-tie-' + String(i).padStart(2, '0'))),
    makeRow('audit-last-microsecond', {actorType: 'CLASSROOM_SCREEN', actorAccountId: null,
      actorScreenBindingId: screen.id, action: 'CLASS_ROSTER_SAVED', success: false, statusCode: 409,
      requestMethod: 'PUT', requestPath: '/historic/students?display=full', entityType: 'WORKSPACE',
      entityId: fixture.classes[0], clientIp: '127.0.0.1', summary: '测试名单更新',
      metadata: {after: Array.from({length: 65}, (_, i) => ({name: '学生' + i})), nested: {pin: 'fixture-only'}}}),
    makeRow('audit-next-day', {createdAt: end}),
    makeRow('audit-migration', {createdAt: new Date(end.getTime() + 1), actorType: 'SETUP',
      actorAccountId: null, action: 'SCHOOL_MIGRATION_IMPORT_COMPLETED'}),
    makeRow('audit-staff', {createdAt: new Date(end.getTime() + 2), action: 'STAFF_CONFIGURATION_IMPORT'}),
    makeRow('audit-secondary', {schoolId: empty.id, action: 'FUTURE_ACTION', actorAccountId: null}),
    makeRow('audit-foreign', {schoolId: fixture.other.id}),
  ]});
  // Prisma dates have millisecond precision. Anchor the sub-millisecond case to the
  // next midnight written by the same client used to create real school records.
  await prisma.$executeRaw`UPDATE "AuditLog" SET "createdAt" =
    (SELECT "createdAt" FROM "AuditLog" WHERE id = 'audit-next-day') - interval '1 microsecond'
    WHERE id = 'audit-last-microsecond'`;

  async function tokenFor(username) {
    const response = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: fixture.school.code, username, password: fixture.pin})});
    assert.equal(response.status, 200);
    return (await response.json()).data.access_token;
  }
  const token = await tokenFor('admin');
  const headers = {Authorization: 'Bearer ' + token, 'Content-Type': 'application/json'};
  const prefix = '/api/v2/admin/schools/' + schoolId;
  const profile = await fetch(origin + prefix + '/profile', {headers}).then(response => response.json());
  const url = origin + prefix + '/profile?lang=zh';
  const started = Date.now() - 1000;
  for (const [name, expected] of [['审计测试学校', 200], ['', 422]]) {
    const res = await fetch(url, {method: 'PATCH', headers, body: JSON.stringify({name, expectedUpdatedAt: profile.data.updatedAt})});
    assert.equal(res.status, expected);
  }
  let writes;
  for (let attempt = 0; attempt < 100; attempt++) {
    writes = await prisma.auditLog.findMany({where: {schoolId, action: 'SCHOOL_PROFILE_UPDATED'}});
    if (writes.length === 2) break;
    await wait(25);
  }
  assert.deepEqual(writes.map(x => x.statusCode).sort(), [200, 422]);
  assert.ok(writes.every(row => row.requestPath === prefix + '/profile' && row.metadata.query.lang === 'zh'));
  const audits = id => origin + '/api/v2/admin/schools/' + id + '/audit-logs';
  const recent = await fetch(audits(schoolId) + '?action=SCHOOL_PROFILE_UPDATED', {headers}).then(r => r.json());
  assert.ok(recent.data.items.length === 2 && recent.data.items.every(row => Date.parse(row.createdAt) >= started &&
    Date.parse(row.createdAt) <= Date.now() + 1000), 'Live audit timestamps match the time of each operation');
  const raw = await fetch(audits(schoolId) + '?actorType=CLASSROOM_SCREEN', {headers});
  assert.match(raw.headers.get('cache-control'), /no-store/);
  const history = (await raw.json()).data.items;
  assert.equal(history.length, 1);
  assert.equal(history[0].requestPath, '/historic/students');
  assert.equal(history[0].metadata.after.length, 65);
  assert.equal(history[0].metadata.nested.pin, '[REDACTED]');
  const legacyQuery = new URLSearchParams({actorType: 'CLASSROOM_SCREEN', to: new Date(end.getTime() - 1).toISOString()});
  const legacy = await fetch(audits(schoolId) + '?' + legacyQuery, {headers}).then(r => r.json());
  assert.equal(legacy.data.items.length, 0, 'Legacy callers retain their inclusive millisecond upper bound');
  const midnight = new URLSearchParams({from: end.toISOString(), to: end.toISOString()});
  const inclusive = await fetch(audits(schoolId) + '?' + midnight, {headers}).then(r => r.json());
  assert.deepEqual(inclusive.data.items.map(row => row.id), ['audit-next-day']);
  assert.equal((await fetch(audits(schoolId), {headers: {Authorization: 'Bearer ' + await tokenFor('viewer')}})).status, 403);
  assert.equal((await fetch(audits(fixture.other.id), {headers})).status, 403);
  return {
    access: {secondarySchoolId: empty.id, auditDate: '2026-04-15'},
    async verifyRevocation() {
      assert.equal((await fetch(audits(empty.id), {headers})).status, 200);
      await prisma.schoolMember.delete({where: {schoolId_accountId: membership}});
      assert.equal((await fetch(audits(empty.id), {headers})).status, 403);
      assert.equal((await fetch(audits(schoolId), {headers})).status, 200);
      console.log('PASS: generated school writes, retained roster details, scope isolation and live permission revocation.');
    },
  };
}
