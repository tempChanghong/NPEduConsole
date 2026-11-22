import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {createServer} from 'node:http';
import {rm, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyStaffReview({prisma, origin, fixture, directory, evidence, web, bcrypt, localProviderId, runDesktop}) {
  const {school, other, term: fall, pin, managerId} = fixture;
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
  assert.equal(await prisma.academicTerm.count({where: {schoolId: other.id}}), 0);
  const spring = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2026, semester: 2,
    name: '春季师资核对', status: 'DRAFT'}});
  const archived = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2025, semester: 2,
    name: '归档师资核对', status: 'ARCHIVED'}});
  const fallGrade = await prisma.grade.findFirstOrThrow({where: {termId: fall.id, code: 'G2'}});
  const grade = await prisma.grade.create({data: {termId: spring.id, code: 'G2', name: '高二'}});
  const archivedGrade = await prisma.grade.create({data: {termId: archived.id, code: 'G2', name: '高二'}});
  const room = (target, number) => prisma.workspace.create({data: {termId: target.termId, gradeId: target.id,
    type: 'ADMIN_CLASS', code: 'STAFF-' + number, name: '高二（' + number + '）班'}});
  const [fallEight, seven, eight, archivedEight] = await Promise.all([room(fallGrade, 8), room(grade, 7), room(grade, 8), room(archivedGrade, 8)]);
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'STAFF-PHY', name: '物理'}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: seven.id,
    subjectId: subject.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true}});
  const hash = await bcrypt.hash(pin, 10);
  const account = async (username, name, role, localDisabled = false) => {
    const result = await prisma.account.create({data: {name, provider: 'school-local', providerId: localProviderId(school.code, username),
      localUsername: username, localPasswordHash: hash, localDisabled, providerData: {schoolCode: school.code}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: result.id, role}});
    return result;
  };
  const [teacher, second, disabled, former, replacement] = await Promise.all([
    account('headreview', '春季班主任周老师', 'VIEWER'), account('coreview', '副管理员', 'ADMIN'),
    account('retiredreview', '已停用郑老师', 'VIEWER', true),
    account('formerreview', '原协同余老师', 'VIEWER'), account('replacereview', '新协同谢老师', 'VIEWER')
  ]);
  await prisma.administrativeClassLeadership.createMany({data: [
    {administrativeClassId: fallEight.id, accountId: teacher.id, position: 'HEAD_TEACHER'},
    {administrativeClassId: eight.id, accountId: teacher.id, position: 'HEAD_TEACHER'},
    {administrativeClassId: archivedEight.id, accountId: former.id, position: 'HEAD_TEACHER'},
    {administrativeClassId: eight.id, accountId: disabled.id, position: 'CO_HEAD_TEACHER'},
    {administrativeClassId: eight.id, accountId: former.id, position: 'CO_HEAD_TEACHER'}
  ]});
  await prisma.gradeLeadership.create({data: {gradeId: grade.id, accountId: teacher.id, position: 'DEPUTY'}});
  const retainedSchool = await prisma.school.create({data: {code: 'REVIEW-RETAINED', name: '继续授权的学校'}});
  await prisma.schoolMember.create({data: {schoolId: retainedSchool.id, accountId: second.id, role: 'ADMIN'}});
  const retainedTerm = await prisma.academicTerm.create({data: {schoolId: retainedSchool.id, academicYear: 2026, semester: 1,
    name: '仍可管理学期', status: 'ACTIVE'}});
  const endpoint = '/api/v2/admin/schools/' + school.id + '/staff-responsibility-policy';
  const otherEndpoint = '/api/v2/admin/schools/' + other.id + '/staff-responsibility-policy';
  const policyFields = ['gradeLeaderMustBeHomeroom', 'gradeLeaderMustTeach', 'homeroomMustTeach'];
  const baseLabels = {'gradeLeaderMustBeHomeroom': '年级组长需兼任本年级班主任', 'gradeLeaderMustTeach': '年级组长需在本年级任课',
    'homeroomMustTeach': '班主任需在本年级任课'};
  const checks = [], recoveries = {};
  let browser, site, fault, staleId, originalAccess, expectedSpring, legacyBody, webChangeCount = 0;
  let holdRead = false, releaseRead, requestHeld;

  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const res = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const data = await res.json();
    assert.equal(res.status, status, method + ' ' + path + ' ' + JSON.stringify({code: data.code, message: data.message}));
    return data.data ?? data;
  }
  const login = username => api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username, password: pin}});
  const admin = await login('admin'), webAdmin = await login(second.localUsername);
  const fresh = () => api(endpoint + '?termId=' + spring.id, {token: webAdmin.access_token});
  const access = async (token, termId) => (await api('/api/v2/me/workspaces' + (termId ? '?termId=' + termId : ''), {token}))
    .map(row => ({id: row.workspace.id, role: row.role, derived: row.responsibilityDerived === true, direct: Boolean(row.joinedAt)}))
    .sort((a, b) => a.id.localeCompare(b.id));
  const assertReceiptMissing = async requestId => {
    const rejected = await api(endpoint + '/receipts/' + requestId, {token: admin.access_token, status: 404});
    assert.equal(rejected.code, 'STAFF_POLICY_RECEIPT_NOT_FOUND');
  };
  async function setField(field, value) {
    const current = await fresh();
    if (current.policies[field] === value) return;
    const body = {termId: spring.id, scopeVersion: current.scopeVersion, changes: {[field]: value}, expected: {[field]: current.policies[field]}};
    const preview = await api(endpoint + '/preview', {method: 'POST', token: webAdmin.access_token, body});
    assert.equal(preview.state, 'READY');
    const result = await api(endpoint, {method: 'PUT', token: webAdmin.access_token,
      body: {...body, previewVersion: preview.version, requestId: randomUUID()}});
    assert.equal(result.state, 'APPLIED');
  }

  async function pageRun(name, work) {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
    try {
      await work(page);
      await page.screenshot({path: join(evidence, 'edge-' + name + '.png'), animations: 'disabled'});
    } catch (error) {
      await page.screenshot({path: join(evidence, 'edge-failure-' + name + '.png'), animations: 'disabled'}).catch(() => {});
      throw error;
    } finally { await page.close(); }
  }
  async function webWrite(field) {
    await pageRun('rules-different-field', async page => {
      await page.addInitScript(({access, refresh}) => {
        localStorage.setItem('classworks-v2-access-token', access);
        localStorage.setItem('classworks-v2-refresh-token', refresh);
      }, {access: webAdmin.access_token, refresh: webAdmin.refresh_token});
      await page.goto(site + '/classworks-admin?section=teachers&school=' + school.id + '&term=' + spring.id);
      await page.getByRole('tab', {name: '联动规则', exact: true}).click();
      const activeRule = page.getByLabel(baseLabels[field], {exact: true});
      await activeRule.uncheck();
      await page.route('**' + endpoint + '/preview', route => route.fulfill({status: 503, contentType: 'application/json',
        body: JSON.stringify({message: '测试网关暂不可用'})}), {times: 1});
      await page.getByRole('button', {name: '预览规则影响'}).click();
      await page.getByText(/测试网关暂不可用/).waitFor();
      assert.equal(await activeRule.isChecked(), false);
      await page.getByRole('button', {name: '预览规则影响'}).click();
      await page.getByTestId('staff-policy-impact').getByText(/消失.*周老师/).waitFor();
      const saved = page.waitForResponse(res => res.url().endsWith(endpoint) && res.request().method() === 'PUT' && res.ok());
      await page.getByRole('button', {name: '确认保存联动规则'}).click();
      const response = await saved;
      assert.deepEqual(response.request().postDataJSON().changes, {[field]: false});
      assert.equal((await response.json()).data.state, 'APPLIED');
      await page.getByText(/已确认本账号的保存回执/).waitFor();
      webChangeCount++;
    });
  }
  async function fromTeacher(name) {
    let result;
    await pageRun(name, async page => {
      await page.goto(site);
      await page.getByText('教师登录', {exact: true}).click();
      await page.getByText('登录教师工作台', {exact: true}).waitFor();
      await page.locator('.v-select').filter({hasText: '学校'}).locator('.v-field').click();
      await page.getByRole('option', {name: new RegExp(school.name)}).click();
      await page.getByLabel('教师短账号').fill(teacher.localUsername);
      await page.getByLabel('个人 PIN').fill(pin);
      const visit = page.waitForResponse(res => new URL(res.url()).pathname === '/api/v2/me/workspaces' && res.ok());
      await page.getByRole('button', {name: '登录', exact: true}).click();
      result = (await (await visit).json()).data.map(row => ({id: row.workspace.id, role: row.role,
        derived: row.responsibilityDerived === true, direct: Boolean(row.joinedAt)}));
      await page.getByText('已授权 1 个教学空间', {exact: true}).waitFor();
    });
    return result;
  }

  async function revokeDuringPreview() {
    await pageRun('revoked-school-cleared', async page => {
      await page.addInitScript(({access, refresh}) => {
        localStorage.setItem('classworks-v2-access-token', access);
        localStorage.setItem('classworks-v2-refresh-token', refresh);
      }, {access: webAdmin.access_token, refresh: webAdmin.refresh_token});
      await page.goto(site + '/classworks-admin?section=teachers&school=' + school.id + '&term=' + spring.id);
      await page.getByRole('tab', {name: '联动规则', exact: true}).click();
      await page.getByLabel(baseLabels.homeroomMustTeach, {exact: true}).uncheck();
      await page.getByRole('button', {name: '预览规则影响'}).click();
      await page.getByTestId('staff-policy-impact').getByText(/消失.*新协同谢老师/).waitFor();
      await prisma.schoolMember.delete({where: {schoolId_accountId: {schoolId: school.id, accountId: second.id}}});
      const refused = page.waitForResponse(res => res.url().endsWith(endpoint) && res.request().method() === 'PUT' && res.status() === 403);
      await page.getByRole('button', {name: '确认保存联动规则'}).click();
      const response = await refused;
      const {requestId} = response.request().postDataJSON();
      const [row] = await prisma.$queryRaw`SELECT count(*)::int AS count FROM "SchoolStaffPolicyReceipt" WHERE "schoolId" = ${school.id} AND "requestId" = ${requestId}::uuid`;
      assert.equal(row.count, 0);
      assert.equal((await prisma.school.findUniqueOrThrow({where: {id: school.id}})).homeroomMustTeach, true);
      const notice = page.getByRole('alert').filter({hasText: '当前账号已无法管理本校'});
      await notice.waitFor();
      const element = await notice.elementHandle();
      await page.waitForFunction(alert => {
        const bounds = alert.getBoundingClientRect();
        return (alert === document.activeElement || alert.contains(document.activeElement)) &&
          bounds.top >= 0 && bounds.bottom <= window.innerHeight;
      }, element);
      assert.equal(await page.getByTestId('staff-policy-impact').count(), 0);
      assert.equal(await page.getByRole('button', {name: '确认保存联动规则'}).count(), 0);
      assert.equal(await page.getByText(/消失.*新协同谢老师/).count(), 0);
      await page.screenshot({path: join(evidence, 'edge-revoked-no-preview.png'), animations: 'disabled'});
      await page.goto(site + '/classworks-admin?section=teachers&school=' + retainedSchool.id + '&term=' + retainedTerm.id);
      await page.getByRole('tab', {name: '联动规则', exact: true}).click();
      await page.getByLabel(baseLabels.homeroomMustTeach, {exact: true}).uncheck();
      await page.getByRole('button', {name: '预览规则影响'}).click();
      await page.getByTestId('staff-policy-impact').getByText(/仍可管理学期/).waitFor();
      await page.getByRole('button', {name: '确认保存联动规则'}).click();
      await page.getByText(/已确认本账号的保存回执/).waitFor();
      assert.equal((await prisma.school.findUniqueOrThrow({where: {id: retainedSchool.id}})).homeroomMustTeach, false);
    });
  }

  async function checkpoint(step) {
    if (step === 'before') {
      originalAccess = await fromTeacher('teacher-before-rules');
      assert.deepEqual(originalAccess, [{id: fallEight.id, role: 'TEACHER', derived: true, direct: false}]);
      const session = await login(teacher.localUsername);
      expectedSpring = await access(session.access_token, spring.id);
      assert.ok(expectedSpring.some(row => row.id === seven.id && row.role === 'TEACHER' && row.derived && !row.direct));
      assert.deepEqual((await fresh()).issues.filter(row => row.accountId === teacher.id).map(row => row.code).sort(),
        ['GRADE_LEADER_NOT_TEACHING', 'HOMEROOM_NOT_TEACHING']);
    } else if (step === 'web-write') {
      await webWrite('gradeLeaderMustTeach');
      legacyBody = await api(endpoint, {method: 'PUT', token: webAdmin.access_token,
        body: Object.fromEntries(policyFields.map(field => [field, true])), status: 409});
      assert.equal(legacyBody.code, 'STAFF_POLICY_REVIEW_REQUIRED');
      fault = 'reply-then-revert';
    } else if (step === 'rules-only') {
      assert.ok(recoveries.applied?.receiptReads >= 1);
      assert.deepEqual(await fromTeacher('teacher-after-rules'), originalAccess);
      const session = await login(teacher.localUsername);
      assert.deepEqual(await access(session.access_token, spring.id), expectedSpring);
      const current = await fresh();
      assert.equal(current.policies.gradeLeaderMustTeach, false);
      assert.equal(current.policies.homeroomMustTeach, true);
      assert.ok(current.issues.some(row => row.accountId === disabled.id && row.code === 'DISABLED_HOMEROOM'));
    } else if (step === 'same-field') {
      await setField('homeroomMustTeach', false);
    } else if (step === 'stale-checked') {
      await assertReceiptMissing(staleId);
      assert.equal((await fresh()).policies.gradeLeaderMustBeHomeroom, true);
      await setField('homeroomMustTeach', true);
    } else if (step === 'drop-before-write') {
      fault = 'missing-then-identical';
    } else if (step === 'no-own-receipt') {
      await assertReceiptMissing(recoveries.missing.requestId);
      assert.ok(recoveries.missing.receiptReads >= 1);
      await setField('homeroomMustTeach', true);
    } else if (step === 'teaching-checked') {
      const current = await fresh();
      assert.ok(!current.issues.some(row => row.code === 'HOMEROOM_NOT_TEACHING' && row.accountId === teacher.id));
      const fallPage = await api(endpoint + '?termId=' + fall.id, {token: admin.access_token});
      assert.ok(fallPage.issues.some(row => row.classId === fallEight.id && row.code === 'HOMEROOM_NOT_TEACHING'));
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: seven.id, accountId: teacher.id, subjectId: subject.id, isActive: true}}), 1);
      assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: {in: [eight.id, fallEight.id]}, accountId: teacher.id, isActive: true}}), 0);
      const session = await login(teacher.localUsername);
      assert.deepEqual(await access(session.access_token), originalAccess);
      const after = await access(session.access_token, spring.id);
      assert.deepEqual(after, expectedSpring.map(row => row.id === seven.id ? {...row, direct: true} : row));
      assert.equal((await prisma.workspaceMember.findUniqueOrThrow({where: {workspaceId_accountId: {workspaceId: seven.id,
        accountId: teacher.id}}})).role, 'TEACHER');
    } else if (step === 'handoff-only') {
      const leaders = await prisma.administrativeClassLeadership.findMany({where: {administrativeClassId: eight.id, isActive: true}});
      assert.ok(leaders.some(row => row.accountId === replacement.id && row.position === 'CO_HEAD_TEACHER'));
      assert.ok(leaders.some(row => row.accountId === teacher.id && row.position === 'HEAD_TEACHER'));
      assert.ok(!leaders.some(row => row.accountId === former.id));
      const fallRoles = await prisma.administrativeClassLeadership.findMany({where: {administrativeClassId: fallEight.id, isActive: true}});
      assert.deepEqual(fallRoles.map(row => [row.accountId, row.position]), [[teacher.id, 'HEAD_TEACHER']]);
      assert.equal(await prisma.teachingAssignment.count({where: {accountId: {in: [former.id, replacement.id]}}}), 0);
      const state = await fresh();
      assert.ok(state.issues.some(row => row.accountId === replacement.id && row.code === 'HOMEROOM_NOT_TEACHING'));
      assert.ok(!state.issues.some(row => row.accountId === former.id && row.code === 'HOMEROOM_NOT_TEACHING'));
    } else if (step === 'archive-explicit') {
      const leaders = await prisma.administrativeClassLeadership.findMany({where: {administrativeClassId: archivedEight.id, isActive: true}});
      assert.deepEqual(leaders.map(row => [row.accountId, row.position]), [[former.id, 'HEAD_TEACHER']]);
    } else if (step === 'hold-old-school') {
      holdRead = true;
      requestHeld = Promise.withResolvers();
      releaseRead = Promise.withResolvers();
    } else if (step === 'await-old-school') {
      await requestHeld.promise;
    } else if (step === 'release-old-school') {
      releaseRead.resolve();
    } else if (step === 'no-term-school') {
      const otherPolicy = await api(otherEndpoint, {token: admin.access_token});
      assert.equal(otherPolicy.term, null);
      assert.deepEqual(otherPolicy.terms, []);
      assert.equal(otherPolicy.policies.homeroomMustTeach, false);
      assert.equal((await fresh()).policies.homeroomMustTeach, true);
    } else if (step === 'web-revocation') {
      await revokeDuringPreview();
    } else throw new Error('Unexpected checkpoint ' + step);
    checks.push(step);
  }

  const relay = createServer(async (req, res) => {
    try {
      const path = req.url;
      if (path.startsWith('/fixture/staff/')) {
        await checkpoint(path.split('/').at(-1));
        return res.writeHead(204).end();
      }
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const request = Buffer.concat(chunks);
      const headers = {...req.headers};
      for (const key of ['host', 'connection', 'transfer-encoding', 'content-length']) delete headers[key];
      const policyWrite = path === endpoint && req.method === 'PUT';
      if (policyWrite && fault === 'missing-then-identical') {
        fault = null;
        const submitted = JSON.parse(request);
        recoveries.missing = {requestId: submitted.requestId, receiptReads: 0};
        await setField('homeroomMustTeach', false);
        return res.writeHead(502).end();
      }
      const upstream = await fetch(origin + path, {method: req.method, headers,
        body: request.length ? request : undefined, redirect: 'manual'});
      const body = Buffer.from(await upstream.arrayBuffer());
      if (holdRead && path === endpoint && req.method === 'GET') {
        holdRead = false;
        requestHeld.resolve();
        await releaseRead.promise;
      }
      if (policyWrite && upstream.ok) {
        const result = JSON.parse(body).data, submitted = JSON.parse(request);
        if (result.state === 'STALE') staleId = submitted.requestId;
        if (fault === 'reply-then-revert' && result.state === 'APPLIED') {
          fault = null;
          recoveries.applied = {requestId: result.receipt.requestId, receiptReads: 0};
          assert.equal(result.receipt.policies.homeroomMustTeach, false);
          assert.equal(result.receipt.policies.gradeLeaderMustTeach, false);
          await setField('homeroomMustTeach', true);
          return res.writeHead(502).end();
        }
      }
      for (const record of Object.values(recoveries))
        if (path === endpoint + '/receipts/' + record.requestId) record.receiptReads++;
      const outgoing = Object.fromEntries(upstream.headers);
      for (const key of ['connection', 'transfer-encoding', 'content-encoding', 'content-length']) delete outgoing[key];
      res.writeHead(upstream.status, outgoing).end(body);
    } catch (error) {
      console.error(error.message);
      res.writeHead(500).end(error.message);
    }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  const config = join(directory, 'staff-review-desktop.json');
  try {
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (edge, address) => {
      browser = edge;
      site = address;
      await writeFile(config, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
        schoolCode: school.code, otherSchoolId: other.id, otherSchoolName: other.name, password: pin, fallId: fall.id, springId: spring.id, gradeId: grade.id,
        fallEightId: fallEight.id, sevenId: seven.id, eightId: eight.id, subjectId: subject.id,
        teacherId: teacher.id, formerId: former.id, replacementId: replacement.id, archivedId: archived.id, archivedEightId: archivedEight.id,
        archivedGradeId: archivedGrade.id, allowStaffReviewWrites: true}), {mode: 0o600});
      await runDesktop(['--live-staff-review', config, evidence]);
    }});
    assert.equal(checks.length, 15);
    await writeFile(join(evidence, 'staff-review-result.json'), JSON.stringify({result: 'PASS', checks, recoveries,
      webChangeCount, cachedWebStatus: legacyBody.code, scopes: {fallEight: fallEight.id, springEight: eight.id, springSeven: seven.id},
      teacherAccess: {beforeAndAfterRulesActive: originalAccess, beforeAndAfterRulesSpring: expectedSpring}}, null, 2));
  } finally {
    releaseRead?.resolve();
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
    await rm(config, {force: true});
  }
}
