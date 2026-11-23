import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyEntryRetirementPilot({prisma, origin, fixture, directory, evidence, web, runDesktop}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term: active, managerId, pin} = fixture;
  await prisma.school.update({where: {id: school.id}, data: {teacherAuthMode: 'OAUTH_EMAIL'}});
  await prisma.academicTerm.update({where: {id: active.id}, data: {name: '现用春季'}});
  const archive = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: active.academicYear - 1,
    semester: 2, name: '历史秋季', status: 'ARCHIVED'}});
  const empty = await prisma.school.create({data: {code: 'NO-TERM', name: '示范无学期学校'}});
  await prisma.schoolMember.create({data: {schoolId: empty.id, accountId: managerId, role: 'ADMIN'}});
  const activeGrade = await prisma.grade.findFirstOrThrow({where: {termId: active.id, code: 'G2'}});
  const archivedGrade = await prisma.grade.create({data: {termId: archive.id, code: 'G2', name: '高二'}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'PILOT-PHY', name: '物理', category: 'CORE'}});
  const createRoom = (grade, code, name) => prisma.workspace.create({
    data: {termId: grade.termId, gradeId: grade.id, code, name, type: 'ADMIN_CLASS'}});
  const [current, historic, second, peer] = await Promise.all([
    createRoom(activeGrade, 'G2-C08', '高二（8）班'),
    createRoom(archivedGrade, 'G2-C08', '高二（8）班'),
    createRoom(archivedGrade, 'G2-C07', '高二（7）班'),
    createRoom(activeGrade, 'G2-C09', '高二（9）班'),
  ]);
  await prisma.administrativeClassSubject.createMany({data: [current, historic, peer].map(room => ({administrativeClassId: room.id,
    subjectId: subject.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true}))});
  const actor = await prisma.account.create({data: {name: '同邮箱已有教师', email: 'pilot-existing@example.test',
    provider: 'hly', providerId: 'retirement-pilot-teacher', providerData: {email: 'pilot-existing@example.test', email_verified: true}}});
  await prisma.schoolMember.create({data: {schoolId: school.id, accountId: actor.id, role: 'VIEWER'}});
  const direct = await prisma.workspaceMember.create({data: {workspaceId: historic.id, accountId: actor.id, role: 'TEACHER'}});
  const invite = (room, email, role = 'ASSISTANT') => prisma.workspaceMemberInvite.create({data: {workspaceId: room.id, email,
    normalizedEmail: email, role, invitedByAccountId: managerId}});
  const [target, sameEmail, race, statusInvite] = await Promise.all([
    invite(historic, actor.email), invite(second, actor.email),
    invite(historic, 'pilot-claim@example.test', 'VIEWER'), invite(second, 'pilot-status@example.test', 'VIEWER')
  ]);
  const auditDate = '2026-04-15';
  const auditId = 'pilot-audit-no-term-20260415', auditTitle = '试点跨端同一条记录';
  await prisma.auditLog.createMany({data: [
    {id: auditId, schoolId: empty.id, actorType: 'ACCOUNT', actorAccountId: managerId, action: 'GRADE_UPDATED',
      createdAt: new Date(auditDate + 'T12:00:00+08:00'), summary: auditTitle, success: false, statusCode: 409},
    {id: 'pilot-audit-other-result', schoolId: empty.id, actorType: 'ACCOUNT', actorAccountId: managerId,
      action: 'GRADE_UPDATED', createdAt: new Date(auditDate + 'T10:00:00+08:00'), summary: '对照 · 成功', success: true, statusCode: 200}
  ]});

  const marks = [];
  let legacyOverview = false;
  let holdOverview = false;
  let releaseOverview;
  let recordedWrite = 0;
  const upstream = new URL(origin);
  const base = '/api/v2/admin/schools/' + school.id;
  const teachingPath = `${base}/current-term/administrative-classes/${current.id}/teaching-assignments`;
  const scopePath = `${base}/teacher-allocation/archived/${archive.id}/grades/${archivedGrade.id}`;
  const revokePath = (workspace, invitation) => `${scopePath}/workspaces/${workspace.id}/invitations/${invitation.id}`;
  const snapshot = invitation => ({termUpdatedAt: archive.updatedAt.toISOString(), invitationUpdatedAt: invitation.updatedAt.toISOString(),
    normalizedEmail: invitation.normalizedEmail, role: invitation.role});
  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body && JSON.stringify(body)});
    const payload = await response.json();
    assert.equal(response.status, status, path + ' ' + (payload.code || response.status));
    return payload.data ?? payload;
  }
  const login = username => api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username, password: pin}});
  const {access_token: token} = await login('admin');
  const manager = await prisma.account.findUniqueOrThrow({where: {id: managerId}});
  const localAccount = async (username, name, role) => {
    const account = await prisma.account.create({data: {provider: 'school-local', providerId: `${school.code}:${username}`,
      name, localUsername: username, localPasswordHash: manager.localPasswordHash, providerData: {schoolCode: school.code}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: account.id, role}});
    return account;
  };
  const candidate = await localAccount('pilot-teacher-a', '林老师', 'VIEWER');
  const peerTeacher = await localAccount('pilot-teacher-b', '林老师', 'VIEWER');
  const manager2 = await localAccount('pilot-admin-2', '另一位管理员', 'ADMIN');
  const {access_token: token2} = await login(manager2.localUsername);
  const readInvite = id => prisma.workspaceMemberInvite.findUnique({where: {id}});
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__pilot/')) {
      const mark = req.url.slice('/__pilot/'.length);
      try {
        if (mark === 'legacy-overview-on') legacyOverview = true;
        else if (mark === 'legacy-overview-off') legacyOverview = false;
        else if (mark === 'only-target-revoked') {
          assert.equal(await readInvite(target.id), null);
          assert.deepEqual(await readInvite(sameEmail.id), sameEmail);
          assert.deepEqual(await prisma.workspaceMember.findUnique({where: {workspaceId_accountId: {workspaceId: historic.id, accountId: actor.id}}}), direct);
        } else if (mark === 'claim-before-confirm') {
          await prisma.workspaceMemberInvite.update({where: {id: race.id}, data: {claimedAt: new Date(), claimedByAccountId: actor.id}});
        } else if (mark === 'oauth-invitation-only') {
          assert.equal(await prisma.workspaceMemberInvite.count({where: {workspaceId: current.id, normalizedEmail: 'pilot-new@example.test'}}), 1);
          assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: current.id}}), 0);
        } else if (mark === 'add-peer-assignment') {
          await api(base + '/teaching-assignments', {method: 'PUT', token: token2,
            body: {workspaceId: peer.id, subjectId: subject.id, accountId: peerTeacher.id, position: 'PRIMARY'}});
        } else if (mark === 'stale-no-write') {
          assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: current.id}}), 0);
        } else if (mark === 'assignment-readback-confirmed') {
          assert.equal(recordedWrite, 1);
          const rows = await prisma.teachingAssignment.findMany({where: {subjectId: subject.id}, orderBy: {workspaceId: 'asc'}});
          assert.equal(rows.length, 2);
          assert.equal(rows.find(row => row.workspaceId === current.id)?.accountId, candidate.id);
          assert.equal(rows.find(row => row.workspaceId === peer.id)?.accountId, peerTeacher.id);
        } else if (mark === 'hold-overview') {
          holdOverview = true;
        } else if (mark === 'await-held-overview') {
          const until = Date.now() + 4000;
          while (!releaseOverview && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 30));
          assert.ok(releaseOverview, 'verified that original school produced the now-stale response');
        } else if (mark === 'release-overview') {
          assert.ok(releaseOverview);
          releaseOverview();
          releaseOverview = null;
        } else if (mark === 'revoke-teaching-scope' || mark === 'restore-teaching-scope') {
          await prisma.schoolMember.update({where: {schoolId_accountId: {schoolId: school.id, accountId: managerId}},
            data: {role: mark.startsWith('revoke') ? 'VIEWER' : 'ADMIN'}});
        } else throw new Error('Unknown pilot checkpoint');
        marks.push(mark);
        res.writeHead(204).end();
      } catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    const targetUrl = new URL(req.url, upstream);
    if (legacyOverview && targetUrl.pathname.endsWith('/management-overview')) targetUrl.searchParams.delete('desktop');
    const forwarded = request(targetUrl, {method: req.method, headers: {...req.headers, host: upstream.host}}, reply => {
      if (holdOverview && targetUrl.pathname === base + '/management-overview' && targetUrl.searchParams.get('termId') === active.id) {
        holdOverview = false;
        const chunks = [];
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => { releaseOverview = () => { res.writeHead(reply.statusCode, reply.headers); res.end(Buffer.concat(chunks)); }; });
        return;
      }
      if (req.method === 'POST' && targetUrl.pathname === teachingPath && reply.statusCode === 200) {
        const chunks = [];
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => {
          const body = Buffer.concat(chunks);
          if (JSON.parse(body).data.state === 'APPLIED') {
            recordedWrite++;
            res.destroy();
          } else { res.writeHead(reply.statusCode, reply.headers); res.end(body); }
        });
        return;
      }
      res.writeHead(reply.statusCode, reply.headers);
      reply.pipe(res);
    });
    forwarded.on('error', () => res.destroy());
    req.pipe(forwarded);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const desktopFixture = join(directory, 'entry-pilot-desktop.json');
    await writeFile(desktopFixture, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id, schoolCode: school.code,
      password: pin, archivedTermId: archive.id, activeTermId: active.id, archivedClassId: historic.id, activeClassId: current.id,
      subjectId: subject.id, peerId: peer.id, teacherId: candidate.id, inviteId: target.id, raceInviteId: race.id,
      emptySchoolId: empty.id, auditDate, auditId}));
    const exact = await api(base + '/management-overview?desktop=1&termId=' + archive.id, {token});
    assert.equal(exact.term.id, archive.id);
    assert.equal(exact.activeTerm.id, active.id);
    assert.ok(exact.tasks.some(row => row.classId === historic.id && row.subjectId === subject.id));
    assert.ok(exact.tasks.every(row => row.classId !== current.id));
    const main = await api(base + '/management-overview/current?staffReview=1&courseGroupActions=1', {token});
    assert.equal(main.term.id, active.id);
    assert.ok(main.tasks.every(row => row.classId !== historic.id));
    await runDesktop(['--live-entry-retirement-pilot', desktopFixture, evidence]);
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (browser, url) => {
      const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block', acceptDownloads: true,
        timezoneId: 'Asia/Shanghai'});
      try {
        await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
        const bookmark = url + '/classworks-admin?section=audit&school=' + empty.id;
        await page.addInitScript(schoolId => window.localStorage.setItem('npclassworks-admin-context:v1',
          JSON.stringify({schoolId, tab: 'overview'})), school.id);
        await page.goto(bookmark);
        await page.getByLabel('管理员短账号', {exact: true}).fill('admin');
        await page.getByLabel('个人 PIN', {exact: true}).fill(pin);
        const loginSchool = page.locator('.v-select').filter({hasText: '学校'}).locator('.v-field').first();
        await loginSchool.click();
        await page.getByRole('option', {name: school.name, exact: true}).click();
        await page.getByRole('button', {name: '登录', exact: true}).click();
        await page.getByText(auditTitle, {exact: true}).waitFor();
        assert.equal(new URL(page.url()).searchParams.get('school'), empty.id, 'explicit bookmark beats prior saved context');
        await page.getByRole('button', {name: '继续使用网页版', exact: true}).click();
        await page.getByLabel('开始日期', {exact: true}).fill(auditDate);
        await page.getByLabel('结束日期', {exact: true}).fill(auditDate);
        await page.locator('.v-select').filter({hasText: '执行结果'}).locator('.v-field').click();
        await page.getByRole('option', {name: '失败', exact: true}).click();
        await page.getByRole('button', {name: '查看详情'}).click();
        await page.getByText(auditId, {exact: true}).waitFor();
        await page.screenshot({path: join(evidence, 'edge-audit-same-id.png'), animations: 'disabled'});
        await page.reload();
        await page.getByRole('button', {name: '继续使用网页版'}).waitFor();
        await page.getByText(auditTitle, {exact: true}).waitFor();
        marks.push('edge-pin-bookmark-no-term-same-audit-id');
        await prisma.schoolMember.delete({where: {schoolId_accountId: {schoolId: empty.id, accountId: managerId}}});
        const auditRequests = [];
        page.on('request', req => { if (req.url().includes('/audit-logs')) auditRequests.push(req.url()); });
        await page.goto(bookmark);
        await page.getByText('原学校不可访问。请选择你有权限的学校以查看审计记录。', {exact: true}).waitFor();
        assert.deepEqual(auditRequests, []);
        await page.screenshot({path: join(evidence, 'edge-audit-unavailable-bookmark.png'), animations: 'disabled'});
        const picker = page.getByText('原学校不可访问。请选择你有权限的学校以查看审计记录。', {exact: true})
          .locator('xpath=ancestor::*[contains(@class,"v-window-item")][1]').locator('.v-select .v-field');
        await picker.click();
        const manualRead = page.waitForResponse(response => response.url().includes(`${base}/audit-logs`) && response.ok());
        await page.getByRole('option', {name: `${school.name} · 管理员`, exact: true}).click();
        await manualRead;
        await page.waitForURL(url => url.searchParams.get('school') === school.id);
        assert.ok(auditRequests.every(url => url.includes(`${base}/audit-logs`)));
        marks.push('denied-bookmark-does-not-open-other-school');

        await page.goto(url + '/settings');
        await page.getByText('故障诊断', {exact: true}).first().click();
        const download = page.waitForEvent('download');
        await page.getByRole('button', {name: '下载诊断包'}).click();
        const file = await download;
        await file.saveAs(join(evidence, 'edge-browser-diagnostics.json'));
        const diagnostic = JSON.parse(await readFile(join(evidence, 'edge-browser-diagnostics.json'), 'utf8'));
        assert.equal(diagnostic.context.page, 'settings-diagnostics');
        assert.ok(Array.isArray(diagnostic.local.events));
        assert.equal(diagnostic.managementOverview, null);
        await page.screenshot({path: join(evidence, 'edge-settings-diagnostics.png'), animations: 'disabled'});
        marks.push('edge-settings-diagnostic-download');
      } catch (error) {
        await page.screenshot({path: join(evidence, 'edge-pilot-failure.png')}).catch(() => {});
        throw error;
      } finally { await page.close(); }
    }});
    assert.ok((await readInvite(race.id)).claimedAt);
    const gone = await api(revokePath(historic, target), {method: 'DELETE', body: snapshot(target), token, status: 404});
    assert.equal(gone.code, 'WORKSPACE_INVITATION_NOT_FOUND');
    const wrongSchool = await api(revokePath(second, statusInvite).replace(school.id, other.id),
      {method: 'DELETE', body: snapshot(statusInvite), token, status: 403});
    assert.equal(wrongSchool.code, 'SCHOOL_ADMIN_REQUIRED');
    await prisma.workspaceMemberInvite.update({where: {id: statusInvite.id}, data: {role: 'ASSISTANT'}});
    const changed = await api(revokePath(second, statusInvite), {method: 'DELETE', body: snapshot(statusInvite), token, status: 409});
    assert.equal(changed.code, 'ARCHIVED_INVITATION_CHANGED');
    const updatedInvite = await readInvite(statusInvite.id);
    assert.equal(updatedInvite.role, 'ASSISTANT');
    await prisma.academicTerm.update({where: {id: archive.id}, data: {status: 'DRAFT'}});
    const stale = await api(revokePath(second, statusInvite), {method: 'DELETE', body: snapshot(updatedInvite), token, status: 409});
    assert.equal(stale.code, 'ARCHIVED_INVITATION_TERM_CHANGED');
    assert.deepEqual(await readInvite(statusInvite.id), updatedInvite);
    const managerMembership = {schoolId_accountId: {schoolId: school.id, accountId: managerId}};
    await prisma.schoolMember.update({where: managerMembership, data: {role: 'VIEWER'}});
    const revoked = await api(revokePath(second, statusInvite), {method: 'DELETE', body: snapshot(updatedInvite), token, status: 403});
    assert.equal(revoked.code, 'SCHOOL_ADMIN_REQUIRED');
    assert.deepEqual(await readInvite(statusInvite.id), updatedInvite);
    marks.push('archive-switch-cross-school-changed-role-withdrawn-authority-and-missing-rejected');
    await writeFile(join(evidence, 'entry-pilot-result.json'), JSON.stringify({marks, auditId, schoolId: school.id,
      emptySchoolId: empty.id, historicClassId: historic.id, currentClassId: current.id, withdrawnInvitationId: target.id,
      untouchedInvitationId: sameEmail.id, claimedInvitationId: race.id}, null, 2));
    console.log('PASS: Windows and Edge retirement pilot, selected-term diagnostics, independent archived invitation and matching school audit.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
