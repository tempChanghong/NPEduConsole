import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';

export async function verifyTeachersRetirement({prisma, fixture, origin, fromKv, bcrypt, directory, evidence, runDesktop}) {
  await mkdir(evidence, {recursive: true});
  const {school, term, pin, managerId, classes: [first, second]} = fixture;
  const {localProviderId} = await fromKv('domain/localAccount.js');
  async function teacher(username, localDisabled) {
    const account = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(school.code, username),
      localUsername: username, name: '同名测试教师', localPasswordHash: await bcrypt.hash(pin, 10), localDisabled,
      providerData: {schoolCode: school.code}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: account.id, role: 'VIEWER'}});
    return account;
  }
  const [disabled, active] = await Promise.all([teacher('disabled-access', true), teacher('active-access', false)]);
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, name: '物理', code: 'TEACHERS-PHY'}});
  const identity = await prisma.account.create({data: {provider: 'hly', providerId: 'teachers-retirement', name: '旧邮箱教师',
    email: 'retirement.fixture@example.test', providerData: {email_verified: true, email: 'retirement.fixture@example.test'}}});
  const invitation = workspaceId => prisma.workspaceMemberInvite.create({data: {workspaceId, role: 'VIEWER',
    email: identity.email, normalizedEmail: identity.email, invitedByAccountId: managerId}});
  const [removed, peer] = await Promise.all([invitation(first), invitation(second)]);
  await prisma.workspaceMember.create({data: {workspaceId: first, accountId: disabled.id, role: 'TEACHER'}});
  await prisma.gradeLeadership.create({data: {gradeId: grade.id, accountId: disabled.id, position: 'PRIMARY'}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: first, subjectId: subject.id, deliveryMode: 'ADMIN_CLASS',
    isCompulsory: true}});
  const [held, repair] = await Promise.all([disabled, active].map(account => prisma.teachingAssignment.create({data: {
    workspaceId: first, accountId: account.id, subjectId: subject.id, position: 'CO_TEACHER',
  }})));
  const {claimWorkspaceInvitations} = await fromKv('services/workspaceInvitationClaimService.js');
  const target = `/api/v2/admin/schools/${school.id}/teacher-allocation/targeted-access`;
  const checks = [], writes = [], reads = [];
  let dropped = false, brokenRead = false;
  async function check(step) {
    if (step === 'reopen-after-disconnect') {
      assert.ok(dropped && brokenRead);
      assert.equal(await prisma.workspaceMemberInvite.count({where: {id: removed.id}}), 0);
      assert.equal(await prisma.workspaceMemberInvite.count({where: {id: peer.id}}), 1);
    } else if (step === 'members-and-original-receipt') {
      assert.deepEqual(reads, [writes[0].requestId, writes[0].requestId]);
      assert.deepEqual(writes.map(row => row.action), ['REVOKE', 'REMOVE', 'REPAIR']);
      assert.equal(await prisma.workspaceMember.count({where: {workspaceId: first, accountId: disabled.id}}), 0);
      assert.equal(await prisma.workspaceMember.count({where: {workspaceId: first, accountId: active.id, role: 'ASSISTANT'}}), 1);
      assert.equal(await prisma.gradeLeadership.count({where: {gradeId: grade.id, accountId: disabled.id, isActive: true}}), 1);
      assert.equal(await prisma.teachingAssignment.count({where: {id: {in: [held.id, repair.id]}, isActive: true}}), 2);
      await prisma.school.update({where: {id: school.id}, data: {allowOAuthTeacherLogin: true}});
      await claimWorkspaceInvitations({accountId: identity.id, email: identity.email});
      assert.equal(await prisma.workspaceMember.count({where: {workspaceId: first, accountId: identity.id}}), 0);
      assert.equal(await prisma.workspaceMember.count({where: {workspaceId: second, accountId: identity.id}}), 1);
    } else throw Error('Unknown teachers test step');
    checks.push(step);
  }
  const legacy = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.url.startsWith('/__teachers/')) {
      try { await check(req.url.split('/').at(-1)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    const commit = req.method === 'POST' && req.url === target;
    if (req.method === 'GET' && req.url.startsWith(target + '/receipts/')) {
      reads.push(req.url.split('/').at(-1));
      if (!brokenRead) { brokenRead = true; res.writeHead(503).end(); return; }
    }
    const body = [];
    for await (const chunk of req) body.push(chunk);
    const bytes = Buffer.concat(body);
    if (commit) {
      const plan = JSON.parse(bytes);
      writes.push({requestId: plan.requestId, action: plan.action});
    }
    const upstream = request(new URL(req.url, legacy), {method: req.method, headers: {...req.headers, host: legacy.host}}, reply => {
      if (commit && !dropped && reply.statusCode === 200) {
        reply.resume();
        reply.on('end', () => { dropped = true; res.destroy(); });
      } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
    });
    upstream.on('error', () => res.destroy());
    upstream.end(bytes);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const path = join(directory, 'teacher-retirement-desktop.json');
    await writeFile(path, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, password: pin, room: first, invitation: removed.id, disabledId: disabled.id, activeId: active.id,
      subjectId: subject.id, allowTeacherRetirementWrites: true}), {mode: 0o600});
    await runDesktop(['--live-teacher-retirement', path, evidence]);
    assert.equal(checks.length, 2);
    await writeFile(join(evidence, 'teachers-result.json'), JSON.stringify({checks, writes, reads, termId: term.id, schoolId: school.id,
      room: first, removedInvitation: removed.id, otherInvitation: peer.id, preservedAssignments: [held.id, repair.id], passed: true}, null, 2));
    console.log('PASS: Windows teacher access, lost acknowledgement and read, original request recovery, actual claim and exact repair.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
