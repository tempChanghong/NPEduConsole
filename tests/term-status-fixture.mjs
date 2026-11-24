import assert from 'node:assert/strict';
import {randomBytes, randomUUID} from 'node:crypto';
import {createServer} from 'node:http';
import {cp, mkdir, mkdtemp, rm, writeFile} from 'node:fs/promises';
import {basename, dirname, join, resolve} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyTermStatus(options) {
  const evidence = resolve(options.evidence);
  await mkdir(evidence, {recursive: true});
  const privateRoot = await mkdtemp(join(evidence, 'term-status-native-'));
  try { await verify({...options, evidence, native: join(privateRoot, 'profiles')}); }
  finally {
    if (dirname(resolve(privateRoot)) === evidence && basename(privateRoot).startsWith('term-status-native-'))
      await rm(privateRoot, {recursive: true, force: true});
  }
}

async function verify({prisma, origin, fixture, directory, evidence, web, native, runNative, runDesktop}) {
  const {school, other, term, pin, managerId} = fixture;
  const schoolPath = '/api/v2/admin/schools/' + school.id;
  const statusPath = schoolPath + '/term-preparation';
  const call = async (path, {method = 'GET', token, body, status = 200} = {}) => {
    const response = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const value = await response.json();
    assert.equal(response.status, status, `${method} ${path}: ${value.code ?? response.status}`);
    return value.data ?? value;
  };
  const login = async username => (await call('/accounts/local/login', {method: 'POST', body:
    {schoolCode: school.code, username, password: pin}})).access_token;
  const token = await login('admin');
  await prisma.schoolMember.upsert({where: {schoolId_accountId: {schoolId: other.id, accountId: managerId}},
    update: {role: 'ADMIN'}, create: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
  const forceDraft = await prisma.academicTerm.create({data: {schoolId: other.id, academicYear: 2028, semester: 1,
    name: '强制启用隔离检查学期', status: 'DRAFT'}});
  const sameNameHistory = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2024, semester: 1,
    name: term.name, status: 'ARCHIVED'}});
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const room = await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS',
    name: '高二（7）班', code: 'STATE-7'}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, name: '物理', code: 'STATE-PHY'}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: room.id, deliveryMode: 'COURSE_GROUP',
    subjectId: subject.id, isCompulsory: true}});
  const teacher = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer', schoolMemberships: {some: {schoolId: school.id}}}});
  for (const label of ['A1', 'A2']) {
    const group = await call(schoolPath + '/course-groups', {status: 201, method: 'POST', token, body: {termId: term.id, gradeId: grade.id,
      subjectId: subject.id, name: '物理 ' + label, code: 'STATE-' + label, sourceClassIds: [room.id], isStudentSelectable: true}});
    await call(schoolPath + '/teaching-assignments', {method: 'PUT', token, body: {workspaceId: group.id, subjectId: subject.id,
      accountId: teacher.id, position: 'PRIMARY'}});
  }
  await prisma.administrativeClassStudent.create({data: {administrativeClassId: room.id, name: '原学期名单样例'}});
  const bindings = await Promise.all(['七班前屏', '七班后屏'].map(name => prisma.classroomScreenBinding.create({data: {
    schoolId: school.id, administrativeClassId: room.id, name, npepPrepared: true, createdByAccountId: managerId,
    tokenHash: randomBytes(32).toString('hex'),
  }})));
  const [front, rear] = bindings;
  const nativeFixture = join(directory, 'term-status-native.json');
  const scopeNative = classId => writeFile(nativeFixture, JSON.stringify({kind: 'TERM_PREPARATION_DISPOSABLE_DATABASE', origin,
    adminToken: token, schoolId: school.id, classId, bindingIds: bindings.map(row => row.id)}), {mode: 0o600});
  await scopeNative(room.id);
  await runNative(['pair', nativeFixture, native]);
  const originalProfiles = join(dirname(native), 'old-id-probe');
  await cp(native, originalProfiles, {recursive: true});
  const steps = [];
  let browser, url, receiptCount = 0, dropped = null, newTerm, newRoom, newReceipt, firstNewReceipt, oldUnmatchedRoom, stalePlacement;
  const unmatchedScreenId = 'term-status-unmatched-' + randomUUID();
  const placementPath = statusPath + '/screen-placements/' + unmatchedScreenId;
  const placementReceipts = () => prisma.$queryRaw`SELECT "requestId", "activationRequestId" FROM "TermScreenPlacement"
    WHERE "schoolId"=${school.id} AND "bindingId"=${unmatchedScreenId}`;

  async function checkpoint(command) {
    if (command === 'rename-screen') {
      await call(schoolPath + '/classroom-screens/' + rear.id, {method: 'PATCH', token, body: {name: '七班后屏 · 网页并发改名'}});
    } else if (command === 'confirmed-stale') {
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: term.id}})).status, 'ACTIVE');
      assert.equal(await prisma.npepDevice.count({where: {screenBindingId: {in: [front.id, rear.id]}, state: 'ACTIVE'}}), 2);
    } else if (command === 'after-archive') {
      assert.ok(dropped && receiptCount >= 1, 'the Windows client obtained the dropped write via its original receipt');
      assert.equal(await prisma.academicTerm.count({where: {schoolId: school.id, status: 'ACTIVE'}}), 0);
      assert.equal((await call(schoolPath + '/migration/readiness', {token})).hasActiveTerm, false);
      const error = await call(schoolPath + '/migration/export', {method: 'POST', token, status: 422, body: {currentPin: pin,
        passphrase: 'isolated-test-passphrase-with-no-external-use'}});
      assert.equal(error.code, 'MIGRATION_ACTIVE_TERM_MISSING');
      await runNative(['invalidated', nativeFixture, native]);
      await studentAfterArchive(browser, url, school, evidence);
    } else if (command === 'old-id-enabled') {
      assert.equal((await call('/api/v2/catalog/terms/current?schoolId=' + school.id)).id, term.id);
      await runNative(['invalidated', nativeFixture, originalProfiles]);
    } else if (command === 'old-id-draft') {
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: term.id}})).status, 'DRAFT');
      assert.equal(await prisma.academicTerm.count({where: {schoolId: school.id, status: 'ACTIVE'}}), 0);
    } else if (command === 'archived-source-draft-created') {
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: term.id}})).status, 'ARCHIVED');
      newTerm = await prisma.academicTerm.findFirstOrThrow({where: {schoolId: school.id, name: '从归档准备的新学期', status: 'DRAFT'}});
      newRoom = await prisma.workspace.findFirstOrThrow({where: {termId: newTerm.id, code: room.code}});
      assert.equal(await prisma.academicTerm.count({where: {schoolId: school.id, status: 'ACTIVE'}}), 0);
      oldUnmatchedRoom = await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS',
        name: '高二（9）班', code: 'STATE-9'}});
      await prisma.classroomScreenBinding.create({data: {id: unmatchedScreenId, schoolId: school.id, administrativeClassId: oldUnmatchedRoom.id,
        name: '九班原屏', npepPrepared: true, createdByAccountId: managerId, tokenHash: randomBytes(32).toString('hex')}});
    } else if (command === 'front-reported') {
      assert.ok(newReceipt && newReceipt.term.id === newTerm.id);
      assert.equal((await call('/api/v2/catalog/terms/current?schoolId=' + school.id)).id, newTerm.id);
      await scopeNative(newRoom.id);
      await runNative(['repair-first', nativeFixture, native]);
      await runNative(['report-first', nativeFixture, native]);
      const observed = await call(statusPath + '/status-receipts/' + newReceipt.requestId, {token});
      assert.equal(observed.screens.find(row => row.bindingId === front.id).status, 'REPORTED');
      assert.equal(observed.screens.find(row => row.bindingId === rear.id).status, 'REPAIR_REQUIRED');
      const page = await call(`${placementPath}?termId=${newTerm.id}`, {token});
      const target = page.candidates.find(row => row.id === newRoom.id);
      assert.equal(page.activationRequestId, firstNewReceipt.requestId);
      stalePlacement = {termId: newTerm.id, targetClassId: target.id, version: page.version, targetVersion: target.version,
        requestId: randomUUID(), activationRequestId: page.activationRequestId};
    } else if (command === 'first-round-placement') {
      const [saved] = await placementReceipts();
      assert.equal(saved.activationRequestId, firstNewReceipt.requestId);
      assert.equal((await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: unmatchedScreenId}})).administrativeClassId, newRoom.id);
    } else if (command === 'return-unmatched-to-old-class') {
      assert.equal((await call('/api/v2/catalog/terms/current?schoolId=' + school.id)).id, term.id);
      await call(schoolPath + '/classroom-screens/' + unmatchedScreenId, {method: 'PATCH', token,
        body: {administrativeClassId: oldUnmatchedRoom.id}});
    } else if (command === 'second-round-pending') {
      const before = await placementReceipts();
      assert.equal(before.length, 1);
      const old = await call(placementPath, {method: 'POST', token, body: stalePlacement, status: 409});
      assert.equal(old.code, 'TERM_SCREEN_PLACEMENT_CHANGED');
      assert.deepEqual(await placementReceipts(), before);
      const current = await call(`${placementPath}?termId=${newTerm.id}`, {token});
      assert.equal(current.canArrange, true);
      assert.equal(current.activationRequestId, newReceipt.requestId);
      assert.notEqual(current.activationRequestId, firstNewReceipt.requestId);
    } else if (command === 'second-round-placement') {
      const saved = await placementReceipts();
      assert.deepEqual(saved.map(row => row.activationRequestId).sort(), [firstNewReceipt.requestId, newReceipt.requestId].sort());
      for (const row of saved) {
        const placement = await call(statusPath + '/screen-placement-receipts/' + row.requestId, {token});
        assert.equal(placement.receipt.activationRequestId, row.activationRequestId);
      }
      assert.equal(await prisma.classroomScreenBinding.count({where: {id: {in: [front.id, rear.id]}, administrativeClassId: newRoom.id}}), 2);
    } else if (command === 'force-applied') {
      assert.equal((await call('/api/v2/catalog/terms/current?schoolId=' + other.id)).id, forceDraft.id);
    } else throw new Error('unexpected term status checkpoint');
    steps.push(command);
  }

  const relay = createServer(async (incoming, outgoing) => {
    try {
      if (incoming.method === 'POST' && incoming.url.startsWith('/__term-status/')) {
        await checkpoint(incoming.url.slice('/__term-status/'.length));
        outgoing.writeHead(204).end();
        return;
      }
      const buffers = [];
      for await (const buffer of incoming) buffers.push(buffer);
      const headers = {...incoming.headers};
      delete headers.host;
      delete headers['transfer-encoding'];
      delete headers.connection;
      const payload = Buffer.concat(buffers);
      const response = await fetch(new URL(incoming.url, origin), {method: incoming.method, headers, redirect: 'manual',
        body: payload.length ? payload : undefined});
      const data = Buffer.from(await response.arrayBuffer());
      if (incoming.url.startsWith(statusPath + '/status-receipts/')) receiptCount += 1;
      if (incoming.method === 'POST' && incoming.url.startsWith(statusPath + '/terms/') && incoming.url.endsWith('/status') && response.ok) {
        const result = JSON.parse(data).data;
        if (result.state === 'STALE') await writeFile(join(evidence, 'stale-response.json'), JSON.stringify(result, null, 2));
        if (result.state === 'APPLIED' && result.receipt.operation === 'ARCHIVE' && !dropped) {
          dropped = result.receipt.requestId;
          outgoing.destroy();
          return;
        }
        if (result.state === 'APPLIED' && result.receipt.term.id === newTerm?.id && result.receipt.operation === 'ACTIVATE') {
          newReceipt = result.receipt;
          firstNewReceipt ??= newReceipt;
        }
      }
      const resultHeaders = Object.fromEntries(response.headers);
      delete resultHeaders['transfer-encoding'];
      delete resultHeaders['content-encoding'];
      outgoing.writeHead(response.status, {...resultHeaders, 'content-length': data.length}).end(data);
    } catch (error) {
      console.error(error);
      outgoing.writeHead(500).end();
    }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  try {
    const access = join(directory, 'term-status-desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id, schoolCode: school.code,
      termId: term.id, termName: term.name, otherSchoolId: other.id, sameNameId: sameNameHistory.id, forceTermId: forceDraft.id,
      frontId: front.id, rearId: rear.id, unmatchedId: unmatchedScreenId, username: 'admin', password: pin, allowTermStatusWrites: true}), {mode: 0o600});
    await verifyStudentBrowser({legacyWebManagement: true, origin, web, directory, evidence, school, rooms: [], afterStudent: async (edge, frontend) => {
      browser = edge;
      url = frontend;
      await runDesktop(['--live-term-status', access, evidence]);
      await studentInNewTerm(browser, url, school, newRoom, newTerm.id, evidence);
      await legacyWeb(browser, url, school, sameNameHistory, pin, evidence);
    }});
    assert.equal((await call(schoolPath + '/migration/readiness', {token})).hasActiveTerm, true);
    const exported = await fetch(origin + schoolPath + '/migration/export', {method: 'POST', headers: {'Content-Type': 'application/json',
      Authorization: 'Bearer ' + token}, body: JSON.stringify({currentPin: pin, passphrase: 'isolated-test-passphrase-with-no-external-use'})});
    assert.equal(exported.status, 200);
    assert.ok((await exported.arrayBuffer()).byteLength > 0);
    const accessAfter = await call('/api/v2/me/workspaces?termId=' + newTerm.id, {token: await login('viewer')});
    assert.deepEqual(accessAfter.map(row => row.workspace.code).sort(), ['STATE-A1', 'STATE-A2']);
    assert.equal(await prisma.administrativeClassStudent.count({where: {administrativeClassId: room.id}}), 1);
    await writeFile(join(evidence, 'term-status-cross-end.json'), JSON.stringify({steps, originalTerm: term.id, newTerm: newTerm.id,
      screens: {front: front.id, rear: rear.id}, dropped, receiptCount, teacherWorkspaces: accessAfter.map(row => row.workspace.id),
      studentEdge: {archivedWithoutActive: true, newTermSelected: newTerm.id}, actualLegacyWeb: true, exportRestored: true,
      rounds: {first: firstNewReceipt.requestId, second: newReceipt.requestId, binding: unmatchedScreenId}}, null, 2));
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}

async function edgePage(browser, url) {
  const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
  await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
  return page;
}

async function studentSchool(page, url, school) {
  await page.goto(url);
  await page.getByRole('button', {name: /开始选班/}).click();
  const dialog = page.getByRole('dialog').filter({hasText: '选择我的班级'});
  await dialog.locator('.v-select').filter({hasText: /^学校/}).locator('.v-field').click();
  const term = page.waitForResponse(response => response.url().includes('/catalog/terms/current?'));
  await page.getByRole('option', {name: school.name, exact: true}).click();
  return {dialog, response: await term};
}

async function studentAfterArchive(browser, url, school, evidence) {
  const page = await edgePage(browser, url);
  try {
    const {dialog, response} = await studentSchool(page, url, school);
    assert.equal(response.status(), 404);
    assert.equal((await response.json()).code, 'ACTIVE_TERM_NOT_FOUND');
    await dialog.getByText('未找到当前启用的学期').waitFor();
    await page.screenshot({path: join(evidence, 'edge-student-no-active.png'), animations: 'disabled'});
  } finally { await page.close(); }
}

async function studentInNewTerm(browser, url, school, room, termId, evidence) {
  const page = await edgePage(browser, url);
  try {
    const {dialog, response} = await studentSchool(page, url, school);
    assert.equal((await response.json()).data.id, termId);
    await dialog.locator('.v-select').filter({hasText: /^行政班/}).locator('.v-field').click();
    await page.getByRole('option', {name: room.name, exact: true}).click();
    await dialog.locator('.v-select').filter({hasText: '物理（必须确认）'}).locator('.v-field').click();
    await page.getByRole('option', {name: '物理 A1', exact: true}).click();
    const validation = page.waitForResponse(res => res.url().includes('/student-selection/validate') && res.ok());
    await dialog.getByRole('button', {name: '保存并查看作业'}).click();
    assert.equal((await (await validation).json()).data.valid, true);
    await page.screenshot({path: join(evidence, 'edge-student-new-term.png'), animations: 'disabled'});
  } finally { await page.close(); }
}

async function legacyWeb(browser, url, school, term, pin, evidence) {
  const page = await edgePage(browser, url);
  try {
    await page.goto(url + '/classworks-admin?section=terms&school=' + school.id + '&term=' + term.id);
    await page.locator('.v-select').filter({hasText: /^学校/}).locator('.v-field').click();
    await page.getByRole('option', {name: new RegExp(school.name)}).click();
    await page.getByLabel('管理员短账号').fill('admin');
    await page.getByLabel('个人 PIN', {exact: true}).fill(pin);
    await page.getByRole('button', {name: '登录', exact: true}).click();
    const row = page.locator('.v-list-item').filter({hasText: term.name}).filter({hasText: `${term.academicYear} 学年`});
    const preview = page.waitForResponse(res => res.url().endsWith(`/terms/${term.id}/status-preview`) && res.ok());
    await row.getByRole('button', {name: '转为草稿', exact: true}).click();
    assert.equal((await (await preview).json()).data.term.id, term.id);
    const commit = page.waitForResponse(res => res.url().endsWith(`/terms/${term.id}/status`) && res.ok());
    await page.getByRole('button', {name: '设为草稿', exact: true}).click();
    assert.equal((await (await commit).json()).data.receipt.term.id, term.id);
    await page.getByText('学期已设为草稿', {exact: true}).waitFor();
    await page.screenshot({path: join(evidence, 'edge-legacy-draft.png'), animations: 'disabled'});
  } catch (error) {
    await page.screenshot({path: join(evidence, 'edge-legacy-failure.png'), animations: 'disabled'});
    throw error;
  } finally { await page.close(); }
}
