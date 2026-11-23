import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, mkdtemp, rm, writeFile} from 'node:fs/promises';
import {dirname, join, resolve} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyArchivedBatchCorrection(options) {
  const evidence = resolve(options.evidence);
  await mkdir(evidence, {recursive: true});
  const nativeRoot = await mkdtemp(join(evidence, 'archive-native-'));
  try {
    return await verify({...options, evidence, nativeState: join(nativeRoot, 'devices')});
  } finally {
    if (dirname(resolve(nativeRoot)) === evidence) await rm(nativeRoot, {recursive: true, force: true});
  }
}

async function verify({prisma, origin, fixture, directory, evidence, web, fromKv, runDesktop, runNative, nativeState}) {
  const {school, term: active, managerId, pin} = fixture;
  const path = '/api/v2/admin/schools/' + school.id;
  const organization = path + '/organization-import';
  const allocation = path + '/teacher-allocation';
  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body && JSON.stringify(body)});
    const reply = await response.json();
    assert.equal(response.status, status, path + ' ' + (reply.code ?? response.status));
    return reply.data ?? reply;
  }
  const login = username => api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username, password: pin}});
  const [admin, webAdmin, teacherSession] = await Promise.all(['admin', 'admin2', 'viewer'].map(login));
  const teacher = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer', schoolMemberships: {some: {schoolId: school.id}}}});
  await prisma.academicTerm.update({where: {id: active.id}, data: {name: '在用春季学期', semester: 2}});
  active.name = '在用春季学期';
  const activationRequest = crypto.randomUUID();
  const archive = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: active.academicYear,
    semester: 1, status: 'ARCHIVED', name: '已结秋季学期'}});
  const activeGrade = await prisma.grade.findFirstOrThrow({where: {termId: active.id, code: 'G2'}});
  const archiveGrade = await prisma.grade.create({data: {termId: archive.id, code: 'G2', name: '高二'}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'PHY', name: '物理', category: 'CORE'}});
  const room = (grade, code, name, type = 'ADMIN_CLASS') => prisma.workspace.create({data: {termId: grade.termId, gradeId: grade.id,
    code: 'G2-' + code, name, type, ...(type === 'COURSE_GROUP' ? {subjectId: subject.id} : {})}});
  const [historicSeven, historicEight, historicNine, historicGroup, currentEight, currentGroup] = await Promise.all([
    room(archiveGrade, 'C07', '高二（7）班'), room(archiveGrade, 'C08', '高二（8）班'),
    room(archiveGrade, 'C09', '高二（9）班'), room(archiveGrade, 'PHY-A1', '物理 A1', 'COURSE_GROUP'),
    room(activeGrade, 'C08', '高二（8）班'), room(activeGrade, 'PHY-A1', '物理 A1', 'COURSE_GROUP'),
  ]);
  await prisma.administrativeClassSubject.createMany({data: [historicSeven, historicEight, historicNine, currentEight].map(row =>
    ({administrativeClassId: row.id, subjectId: subject.id, deliveryMode: 'COURSE_GROUP', isCompulsory: true}))});
  await prisma.workspaceSourceClass.createMany({data: [[historicGroup, historicSeven], [currentGroup, currentEight]].map(([group, source]) =>
    ({workspaceId: group.id, administrativeClassId: source.id}))});
  const historicalStudent = await prisma.administrativeClassStudent.create({data: {administrativeClassId: historicEight.id,
    name: '历史学生', studentNumber: '001'}});
  const publication = await prisma.publication.create({data: {type: 'ASSIGNMENT', status: 'PUBLISHED', subjectId: subject.id,
    title: '历史物理作业', content: '历史作业合成内容', targets: {create: {workspaceId: historicGroup.id}}}});
  const historicTeacher = await prisma.teachingAssignment.create({data: {workspaceId: historicGroup.id, subjectId: subject.id,
    accountId: managerId, position: 'PRIMARY'}});
  const directCurrent = await prisma.workspaceMember.create({data: {workspaceId: currentEight.id, accountId: teacher.id, role: 'ASSISTANT'}});
  const spaces = termId => api('/api/v2/me/workspaces?termId=' + termId, {token: teacherSession.access_token});
  const spaceRole = (rows, id) => rows.find(row => (row.workspace?.id ?? row.id) === id)?.role;
  assert.equal(spaceRole(await spaces(archive.id), historicGroup.id), undefined);
  assert.equal(spaceRole(await spaces(active.id), currentEight.id), 'ASSISTANT');

  const bindings = await Promise.all(['春季前屏', '春季后屏'].map(name => prisma.classroomScreenBinding.create({data: {
    schoolId: school.id, administrativeClassId: currentEight.id, npepPrepared: true, name,
    tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId}})));
  const nativeFixture = join(directory, 'archive-native.json');
  await writeFile(nativeFixture, JSON.stringify({kind: 'TERM_PREPARATION_DISPOSABLE_DATABASE', origin, adminToken: admin.access_token,
    schoolId: school.id, classId: currentEight.id, bindingIds: bindings.map(row => row.id)}));
  await runNative(['pair', nativeFixture, nativeState]);
  const readDevices = () => prisma.npepDevice.findMany({where: {screenBindingId: {in: bindings.map(row => row.id)}}, orderBy: {id: 'asc'}});
  const devicesBefore = await readDevices();
  assert.equal(devicesBefore.length, 2);
  const bindingVersionsBefore = await prisma.classroomScreenBinding.findMany({where: {id: {in: bindings.map(row => row.id)}},
    select: {id: true, administrativeClassId: true, npepBindingRevision: true}, orderBy: {id: 'asc'}});

  const document = {
    school: {id: school.id, code: school.code, name: school.name, teacherAuth: {sharedPassword: 'fixture-ignore-this-field'}},
    term: {id: archive.id, name: archive.name, academicYear: archive.academicYear, semester: archive.semester, status: 'ACTIVE'},
    grade: {code: archiveGrade.code, name: archiveGrade.name, sortOrder: archiveGrade.sortOrder},
    subjects: [{code: subject.code, name: subject.name, category: subject.category}],
    administrativeClasses: [{code: historicEight.code, name: historicEight.name,
      subjectRules: {PHY: {deliveryMode: 'COURSE_GROUP', isCompulsory: true}}}],
    courseGroups: [{code: historicGroup.code, name: historicGroup.name, subject: subject.code, sourceClasses: [historicEight.code]}],
  };
  const file = join(directory, 'archived-correction.json');
  await writeFile(file, JSON.stringify(document));
  const webFile = join(directory, 'archived-web.json');
  await writeFile(webFile, JSON.stringify({...document, administrativeClasses: [],
    courseGroups: [{...document.courseGroups[0], sourceClasses: [historicNine.code]}]}));
  const activeWebFile = join(directory, 'active-web.json');
  await writeFile(activeWebFile, JSON.stringify({
    school: {code: school.code, name: school.name},
    term: {name: active.name, academicYear: active.academicYear, semester: 2, status: 'ACTIVE'},
    grade: {code: activeGrade.code, name: activeGrade.name, sortOrder: activeGrade.sortOrder},
    subjects: document.subjects,
    administrativeClasses: [{code: 'G2-C10', name: '高二（10）班', subjectRules: {PHY: 'ADMIN_CLASS'}}],
    courseGroups: [],
  }));
  // A cached whole-organization request declaring ACTIVE must still be refused based on the real term.
  const cached = await api('/api/v2/admin/organization/import?dryRun=false', {method: 'POST', status: 428, token: webAdmin.access_token,
    body: {organization: {...document, courseGroups: []}}});
  assert.equal(cached.code, 'ARCHIVED_ORGANIZATION_REVIEW_REQUIRED');
  assert.match(cached.message, /刷新.*批量更正/);
  const links = async group => (await prisma.workspaceSourceClass.findMany({where: {workspaceId: group.id}}))
    .map(row => row.administrativeClassId).sort();
  const receiptCount = async table => {
    const [row] = table === 'organization' ? await prisma.$queryRaw`SELECT count(*)::int AS n
      FROM "SchoolOrganizationImportReceipt" WHERE "schoolId"=${school.id} AND "actorId"=${managerId}` :
      await prisma.$queryRaw`SELECT count(*)::int AS n
      FROM "SchoolTeacherAllocationReceipt" WHERE "schoolId"=${school.id} AND "actorId"=${managerId}`;
    return row.n;
  };
  let browser, url, stale = 0, missingAllocationId;
  const results = {organization: {writes: 0, reads: 0}, allocation: {writes: 0, reads: 0}};
  const marks = [];
  async function manageInEdge(termId, callback) {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    try {
      await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
      await page.addInitScript(({access, refresh}) => {
        localStorage.setItem('classworks-v2-access-token', access);
        localStorage.setItem('classworks-v2-refresh-token', refresh);
      }, {access: webAdmin.access_token, refresh: webAdmin.refresh_token});
      await page.goto(url + '/classworks-admin?section=organization&school=' + school.id + '&term=' + termId);
      await callback(page);
    } catch (error) {
      await page.screenshot({path: join(evidence, 'edge-archive-failure.png')}).catch(() => {});
      throw error;
    } finally { await page.close(); }
  }
  async function webChange() {
    return manageInEdge(archive.id, async page => {
      await page.getByText('归档学期 · 批量更正', {exact: true}).waitFor();
      await page.locator('.v-select').filter({hasText: '目标年级'}).locator('.v-field').click();
      await page.getByRole('option', {name: '高二（G2）', exact: true}).click();
      await page.locator('input[type=file]').setInputFiles(webFile);
      await page.getByRole('button', {name: '预览更正', exact: true}).click();
      const apply = page.getByRole('button', {name: '确认更正 1 项', exact: true});
      await apply.waitFor();
      await page.getByText(/来源 高二（9）班/).waitFor();
      await page.screenshot({path: join(evidence, 'edge-reviewed-archive-source.png'), animations: 'disabled'});
      await apply.click();
      await page.getByText(/服务器已确认归档更正/).waitFor();
    });
  }
  async function activeWebImport() {
    return manageInEdge(active.id, async page => {
      await page.getByRole('button', {name: '载入七班制模板', exact: true}).waitFor();
      await page.locator('input[type=file]').setInputFiles(activeWebFile);
      await page.getByRole('button', {name: '预检', exact: true}).click();
      const apply = page.getByRole('button', {name: '正式导入', exact: true});
      await apply.click();
      const applied = page.waitForResponse(response => response.request().method() === 'POST' &&
        response.url().includes('/admin/organization/import?dryRun=false') && response.status() === 201);
      await page.getByRole('button', {name: '确认导入', exact: true}).click();
      await applied;
      await page.getByText('学校组织配置导入成功', {exact: true}).waitFor();
      await page.screenshot({path: join(evidence, 'edge-current-legacy-import.png'), animations: 'disabled'});
    });
  }
  async function checkpoint(mark) {
    if (mark === 'web-change') {
      await webChange();
      assert.deepEqual(await links(historicGroup), [historicSeven.id, historicNine.id].sort());
    } else if (mark === 'zero') {
      assert.equal(stale, 1);
      assert.equal(await receiptCount('organization'), 0);
      assert.deepEqual(await links(historicGroup), [historicSeven.id, historicNine.id].sort());
    } else if (mark === 'organization-only') {
      assert.deepEqual(await links(historicGroup), [historicEight.id]);
      assert.equal((await prisma.grade.findUniqueOrThrow({where: {id: archiveGrade.id}})).sortOrder, 2);
      assert.equal(results.organization.receipt.courseGroups[0].id, historicGroup.id);
      assert.equal(await receiptCount('organization'), 1);
      assert.equal(await receiptCount('allocation'), 0);
      assert.equal(spaceRole(await spaces(archive.id), historicGroup.id), undefined);
    } else if (mark === 'disable-teacher-for-review' || mark === 'enable-teacher-for-review') {
      await prisma.account.update({where: {id: teacher.id}, data: {localDisabled: mark === 'disable-teacher-for-review'}});
      assert.equal(await receiptCount('allocation'), 0);
    } else if (mark === 'both-complete') {
      assert.equal(results.allocation.receipt.teachers[0].access[0].workspaceId, historicGroup.id);
      assert.equal(await receiptCount('organization'), 1);
      assert.equal(await receiptCount('allocation'), 1);
      assert.equal(spaceRole(await spaces(archive.id), historicGroup.id), 'VIEWER');
      assert.equal(spaceRole(await spaces(active.id), currentEight.id), 'ASSISTANT');
    } else throw new Error('Unknown archive checkpoint.');
    marks.push(mark);
  }

  const upstream = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__archive/')) {
      try { await checkpoint(req.url.slice('/__archive/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    const kind = req.url.startsWith(organization) ? 'organization' : req.url.startsWith(allocation) ? 'allocation' : null;
    if (kind && req.method === 'GET' && req.url.includes('/receipts/')) results[kind].reads++;
    let allocationBody;
    if (req.method === 'POST' && req.url === allocation) {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      allocationBody = Buffer.concat(chunks);
      const {requestId} = JSON.parse(allocationBody);
      if (!missingAllocationId) {
        missingAllocationId = requestId;
        res.destroy();
        return;
      }
      if (missingAllocationId !== requestId) { res.writeHead(409).end(); return; }
    }
    const forwarded = request(new URL(req.url, upstream), {method: req.method, headers: {...req.headers, host: upstream.host}}, reply => {
      if (req.method !== 'POST' || ![organization, allocation].includes(req.url) || reply.statusCode !== 200) {
        res.writeHead(reply.statusCode, reply.headers); reply.pipe(res);
        return;
      }
      const buffers = [];
      reply.on('data', chunk => buffers.push(chunk));
      reply.on('end', () => {
        const body = Buffer.concat(buffers), result = JSON.parse(body).data;
        if (result?.state === 'STALE') stale++;
        if (result?.state === 'APPLIED') {
          results[kind].writes++;
          if (!results[kind].receipt) {
            results[kind].receipt = result.receipt;
            res.destroy();
            return;
          }
        }
        res.writeHead(reply.statusCode, reply.headers).end(body);
      });
    });
    forwarded.on('error', () => res.destroy());
    if (allocationBody) forwarded.end(allocationBody);
    else req.pipe(forwarded);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const desktopFile = join(directory, 'archived-desktop.json');
    await writeFile(desktopFile, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, password: pin, archivedTermId: archive.id, activeName: active.name, archiveName: archive.name,
      file, classId: historicEight.id, groupId: historicGroup.id, teacherId: teacher.id}));
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (edge, frontend) => {
      browser = edge;
      url = frontend;
      await runDesktop(['--live-archived-batch-correction', desktopFile, evidence]);
      await prisma.termActivationReceipt.create({data: {schoolId: school.id, termId: active.id, requestId: activationRequest, actorId: managerId,
        digest: 'a'.repeat(64), result: {operation: 'ACTIVATE', screens: []}}});
      await prisma.$executeRaw`INSERT INTO "SchoolTermActivation" ("schoolId", "termId", "requestId")
        VALUES (${school.id}, ${active.id}, ${activationRequest}::uuid)`;
      await activeWebImport();
      const page = await browser.newPage({viewport: {width: 1360, height: 900}});
      try {
        await page.route('**/*', request => new URL(request.request().url()).origin === url ? request.continue() : request.abort());
        await page.goto(url);
        await page.getByRole('button', {name: /开始选班/}).click();
        const dialog = page.getByRole('dialog').filter({hasText: '选择我的班级'});
        const choose = async (label, name) => {
          await dialog.locator('.v-select').filter({hasText: label}).locator('.v-field').click();
          await page.getByRole('option', {name, exact: true}).click();
        };
        const current = page.waitForResponse(response => response.url().includes('/catalog/terms/current?') && response.ok());
        await choose(/^学校/, school.name);
        assert.equal((await (await current).json()).data.id, active.id);
        await choose(/^行政班/, currentEight.name);
        await choose(/物理（必须确认）/, currentGroup.name);
        await Promise.all([page.waitForResponse(response => response.url().includes('/student-selection/validate') && response.ok()),
          dialog.getByRole('button', {name: '保存并查看作业'}).click()]);
        await dialog.waitFor({state: 'hidden'});
        await page.screenshot({path: join(evidence, 'edge-student-spring-selection.png'), animations: 'disabled'});
      } finally { await page.close(); }
    }});
    await runNative(['report', nativeFixture, nativeState]);
    const devices = await readDevices();
    for (const [index, after] of devices.entries()) {
      const before = devicesBefore[index];
      for (const key of ['id', 'credentialId', 'state', 'administrativeClassId', 'screenBindingId', 'bindingRevision'])
        assert.equal(after[key], before[key]);
      assert.ok(after.lastReportedAt > before.lastReportedAt);
    }
    assert.deepEqual(await prisma.classroomScreenBinding.findMany({where: {id: {in: bindings.map(row => row.id)}},
      select: {id: true, administrativeClassId: true, npepBindingRevision: true}, orderBy: {id: 'asc'}}), bindingVersionsBefore);
    const [roundAfter] = await prisma.$queryRaw`SELECT "requestId", "termId" FROM "SchoolTermActivation" WHERE "schoolId"=${school.id}`;
    assert.deepEqual(roundAfter, {requestId: activationRequest, termId: active.id});
    assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: active.id}})).status, 'ACTIVE');
    assert.equal(await prisma.workspace.count({where: {termId: active.id, code: 'G2-C10'}}), 1);
    marks.push('active-legacy-import-keeps-round-and-two-pairings');
    assert.deepEqual(await links(currentGroup), [currentEight.id]);
    assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: archive.id}})).status, 'ARCHIVED');
    assert.deepEqual(await prisma.administrativeClassStudent.findUniqueOrThrow({where: {id: historicalStudent.id}}), historicalStudent);
    assert.deepEqual(await prisma.teachingAssignment.findUniqueOrThrow({where: {id: historicTeacher.id}}), historicTeacher);
    assert.deepEqual(await prisma.workspaceMember.findUniqueOrThrow({where: {workspaceId_accountId: {
      workspaceId: currentEight.id, accountId: teacher.id}}}), directCurrent);
    assert.equal(await prisma.publicationTarget.count({where: {workspaceId: historicGroup.id, publicationId: publication.id}}), 1);
    assert.equal(results.organization.writes, 1);
    assert.equal(results.organization.reads, 1);
    assert.equal(results.allocation.writes, 1);
    assert.equal(results.allocation.reads, 2);
    assert.equal(results.allocation.receipt.requestId, missingAllocationId);
    const guardedDraft = {termId: archive.id, termStatus: 'ARCHIVED', archivedCorrection: true, targetGradeId: archiveGrade.id,
      grade: {code: archiveGrade.code, name: archiveGrade.name, sortOrder: 3}, administrativeClasses: [], courseGroups: [], subjects: []};
    async function prepare(token) {
      const preview = await api(organization + '/preview', {method: 'POST', token, body: guardedDraft});
      assert.equal(preview.canApply, true);
      return {...guardedDraft, expectedVersion: preview.version, expectedEvidence: preview.evidence, requestId: crypto.randomUUID()};
    }
    const guarded = await prepare(admin.access_token);
    await prisma.academicTerm.update({where: {id: archive.id}, data: {status: 'DRAFT'}});
    const outdated = await api(organization, {method: 'POST', status: 409, token: admin.access_token, body: guarded});
    assert.equal(outdated.code, 'ORG_IMPORT_TERM_CHANGED');
    await prisma.academicTerm.update({where: {id: archive.id}, data: {status: 'ARCHIVED'}});
    const restored = await api(organization, {method: 'POST', token: admin.access_token, body: guarded});
    assert.equal(restored.state, 'STALE');
    const other = await prisma.school.findUniqueOrThrow({where: {code: 'OTHER'}});
    await api(organization.replace(school.id, other.id), {method: 'POST', status: 403, token: admin.access_token, body: guarded});
    const revoked = await prepare(webAdmin.access_token);
    await prisma.schoolMember.updateMany({where: {schoolId: school.id, account: {localUsername: 'admin2'}}, data: {role: 'VIEWER'}});
    await api(organization, {method: 'POST', status: 403, token: webAdmin.access_token, body: revoked});
    assert.equal((await prisma.grade.findUniqueOrThrow({where: {id: archiveGrade.id}})).sortOrder, 2);
    assert.equal(await receiptCount('organization'), 1);
    marks.push('switch-archive-state-zero-write', 'restored-archive-old-version-stale', 'cross-school-denied', 'manager-revocation-zero-write');

    await prisma.school.update({where: {id: school.id}, data: {allowOAuthTeacherLogin: true}});
    const email = 'archived-invitation@example.test';
    const invitee = await prisma.account.create({data: {provider: 'hly', providerId: 'archive-invite-fixture',
      name: '待核对邮箱教师', email, providerData: {email, email_verified: false}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: invitee.id, role: 'VIEWER'}});
    await prisma.workspaceMember.create({data: {workspaceId: historicGroup.id, accountId: invitee.id, role: 'VIEWER'}});
    const invite = await prisma.workspaceMemberInvite.create({data: {workspaceId: historicGroup.id, role: 'ASSISTANT',
      email, normalizedEmail: email, invitedByAccountId: managerId}});
    const remove = {mode: 'LOCAL_PIN', termId: archive.id, termStatus: 'ARCHIVED', gradeId: archiveGrade.id, archivedCorrection: true,
      teachers: [{accountId: invitee.id, access: [{workspaceId: historicGroup.id, operation: 'REMOVE'}]}]};
    const change = await api(allocation + '/preview', {method: 'POST', token: admin.access_token, body: remove});
    assert.equal(change.canApply, true);
    assert.match(change.changes.find(row => row.action === 'REMOVE').message, /同邮箱.*归档期间不能认领.*转回草稿或启用.*身份核验/);
    const removed = await api(allocation, {method: 'POST', token: admin.access_token, body: {...remove,
      expectedVersion: change.version, expectedEvidence: change.evidence, requestId: crypto.randomUUID()}});
    assert.equal(removed.state, 'APPLIED');
    const inviteeMember = () => prisma.workspaceMember.findUnique({where: {workspaceId_accountId: {
      workspaceId: historicGroup.id, accountId: invitee.id}}});
    const {claimWorkspaceInvitations} = await fromKv('services/workspaceInvitationClaimService.js');
    const claim = () => claimWorkspaceInvitations({accountId: invitee.id, email});
    assert.equal(await inviteeMember(), null);
    assert.equal((await prisma.workspaceMemberInvite.findUniqueOrThrow({where: {id: invite.id}})).claimedAt, null);
    assert.equal((await claim()).claimed, 0);
    await prisma.academicTerm.update({where: {id: archive.id}, data: {status: 'DRAFT'}});
    assert.equal((await claim()).claimed, 0);
    assert.equal(await inviteeMember(), null);
    await prisma.account.update({where: {id: invitee.id}, data: {providerData: {email, email_verified: true}}});
    assert.equal((await claim()).claimed, 1);
    assert.equal((await inviteeMember()).role, 'ASSISTANT');
    assert.equal((await prisma.workspaceMemberInvite.findUniqueOrThrow({where: {id: invite.id}})).claimedByAccountId, invitee.id);
    await prisma.academicTerm.update({where: {id: archive.id}, data: {status: 'ARCHIVED'}});
    marks.push('archived-remove-preserves-pending-invitation', 'unverified-email-cannot-claim', 'draft-trusted-email-claims-original-invite');
    await writeFile(join(evidence, 'archive-cross-end-result.json'), JSON.stringify({
      passed: true, marks, stale, activeId: active.id, archiveId: archive.id,
      activeClassId: currentEight.id, archiveClassId: historicEight.id, activeGroupId: currentGroup.id, archiveGroupId: historicGroup.id,
      activationRequest, bindingVersionsBefore,
      teacherId: teacher.id, results, historicalStudentId: historicalStudent.id, publicationId: publication.id,
      native: devices.map((row, index) => ({bindingId: row.screenBindingId, deviceId: row.id,
        before: devicesBefore[index].lastReportedAt, after: row.lastReportedAt})),
    }, null, 2));
    console.log('PASS: Windows archive correction, Edge reviewed change and student selection, real teacher access, two receipts, native credential.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
