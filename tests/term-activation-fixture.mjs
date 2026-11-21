import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, mkdtemp, rm, writeFile} from 'node:fs/promises';
import {basename, dirname, join, resolve} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyTermActivation(options) {
  const evidence = resolve(options.evidence);
  await mkdir(evidence, {recursive: true});
  const privateRoot = await mkdtemp(join(evidence, 'native-state-'));
  try { await verify({...options, nativeState: join(privateRoot, 'devices')}); }
  finally {
    if (dirname(resolve(privateRoot)) === evidence && basename(privateRoot).startsWith('native-state-'))
      await rm(privateRoot, {recursive: true, force: true});
  }
}

async function verify({prisma, origin, fixture, directory, evidence, web, nativeState, runDesktop, runNative, createTeacher}) {
  const {school, other, term, pin, managerId} = fixture;
  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const payload = await response.json();
    assert.equal(response.status, status, path + ': ' + (payload.code ?? response.status));
    return payload.data ?? payload;
  }
  const login = async username => (await api('/accounts/local/login', {method: 'POST', body:
    {schoolCode: school.code, username, password: pin}})).access_token;
  const token = await login('admin');
  const assigned = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer', schoolMemberships: {some: {schoolId: school.id}}}});
  const member = await createTeacher('independent');
  const schoolPath = '/api/v2/admin/schools/' + school.id, base = schoolPath + '/term-preparation';
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const [seven, eight] = await Promise.all([7, 8].map(n => prisma.workspace.create({data: {
    termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS', name: '高二（' + n + '）班', code: 'G2-' + n,
  }})));
  const subject = await prisma.subject.create({data: {schoolId: school.id, name: '物理', code: 'ACT-PHY'}});
  await prisma.administrativeClassSubject.createMany({data: [{administrativeClassId: seven.id, deliveryMode: 'COURSE_GROUP'},
    {administrativeClassId: eight.id, deliveryMode: 'ADMIN_CLASS'}].map(row => ({...row, subjectId: subject.id, isCompulsory: true}))});
  const groups = [];
  for (const label of ['A1', 'A2']) {
    const group = await api(schoolPath + '/course-groups', {method: 'POST', token, status: 201, body: {
      termId: term.id, gradeId: grade.id, subjectId: subject.id, name: '物理 ' + label, code: 'PHY-' + label,
      sourceClassIds: [seven.id], isStudentSelectable: true,
    }});
    groups.push(group);
    await api(schoolPath + '/teaching-assignments', {method: 'PUT', token, body: {
      workspaceId: group.id, subjectId: subject.id, accountId: assigned.id, position: 'PRIMARY',
    }});
  }
  await prisma.workspaceMember.create({data: {workspaceId: groups[0].id, accountId: member.id, role: 'ASSISTANT'}});
  await prisma.administrativeClassStudent.create({data: {administrativeClassId: seven.id, name: '原名单学生', studentNumber: '0701'}});

  const screenRooms = [seven, seven, eight], names = ['七班前屏', '七班后屏', '八班无匹配屏'];
  const bindings = await Promise.all(screenRooms.map((room, index) => prisma.classroomScreenBinding.create({data: {
    schoolId: school.id, administrativeClassId: room.id, name: names[index], npepPrepared: true,
    createdByAccountId: managerId, tokenHash: randomBytes(32).toString('hex'),
  }})));
  const bindingIds = bindings.map(row => row.id);
  const nativeFixture = join(directory, 'term-activation-native.json');
  const setNativeScope = classIds => writeFile(nativeFixture, JSON.stringify({kind: 'TERM_PREPARATION_DISPOSABLE_DATABASE',
    origin, adminToken: token, schoolId: school.id, classId: classIds[0], classIds, bindingIds}), {mode: 0o600});
  await setNativeScope(screenRooms.map(row => row.id));
  await runNative(['pair', nativeFixture, nativeState]);
  const devices = await prisma.npepDevice.findMany({where: {screenBindingId: {in: bindingIds}}});
  assert.equal(devices.length, 3);
  assert.ok(devices.every(row => row.state === 'ACTIVE' && row.lastReportedAt));

  const target = {academicYear: 2027, semester: 1, name: '2027 春季教学正式学期', startsAt: '2027-02-15', endsAt: '2027-07-15'};
  const proposal = {target, sourceTermId: term.id};
  const preview = await api(base + '/preview', {method: 'POST', token, body: proposal});
  const created = await api(base, {method: 'POST', token, body: {...proposal, expectedVersion: preview.version,
    requestId: 'ea23d26b-c07f-4535-849b-0c6e96ebcb4f'}});
  assert.equal(created.state, 'APPLIED');
  const draftId = created.receipt.termId;
  const newRoom = code => prisma.workspace.findUniqueOrThrow({where: {termId_code: {termId: draftId, code}}});
  const newSeven = await newRoom(seven.code), newEight = await newRoom(eight.code);
  const newGroups = await Promise.all(['PHY-A1', 'PHY-A2'].map(newRoom));
  await api(schoolPath + '/administrative-classes/' + newEight.id, {method: 'PATCH', token,
    body: {name: newEight.name, code: 'G2-8-NEW', expectedUpdatedAt: newEight.updatedAt}});

  const checkPath = base + '/drafts/' + draftId + '/check';
  const activatePath = base + '/drafts/' + draftId + '/activate';
  const oldEdit = schoolPath + '/current-term/administrative-classes/' + eight.id + '/teaching-assignments';
  const staleDraft = {termId: term.id, subjectId: subject.id, accountId: assigned.id, action: 'ASSIGN',
    position: 'PRIMARY', classIds: [eight.id]};
  const staleTeachingPreview = await api(oldEdit + '/preview', {method: 'POST', token, body: staleDraft});
  assert.equal(staleTeachingPreview.rows[0].state, 'ADD');
  const oldTeaching = {...staleDraft, expected: staleTeachingPreview.rows.map(({classId, version}) => ({classId, version}))};
  const overview = () => api(schoolPath + '/management-overview/current?termPreparation=1', {token});
  const proof = await api(checkPath, {token});
  assert.deepEqual(proof.blockingDiagnostics, []);
  assert.equal(proof.screens.find(row => row.bindingId === bindingIds[2]).archiveInvalidatesNative, true);
  const stepLog = [];
  let posts = 0, reads = 0, lostReceipt;
  async function checkpoint(step) {
    switch (step) {
    case 'change-binding':
      await prisma.classroomScreenBinding.update({where: {id: bindingIds[1]}, data: {name: '七班后屏（已改显示名）'}});
      break;
    case 'confirm-no-write':
      assert.equal(posts, 1);
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: term.id}})).status, 'ACTIVE');
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: draftId}})).status, 'DRAFT');
      assert.deepEqual(await Promise.all(bindingIds.map(async id => (await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id}})).administrativeClassId)),
        [seven.id, seven.id, eight.id]);
      break;
    case 'before-repair': {
      assert.ok(posts === 2 && reads === 2 && lostReceipt);
      assert.equal(lostReceipt.term.id, draftId);
      assert.equal(lostReceipt.archivedTerm.id, term.id);
      assert.equal(lostReceipt.reboundScreens, 2);
      const after = await overview();
      assert.equal(after.term.id, draftId);
      for (const id of bindingIds.slice(0, 2)) assert.equal(after.tasks.some(row => row.code === 'SCREEN_REPAIR_REQUIRED' && row.screenId === id), true);
      assert.equal(after.tasks.filter(row => row.code === 'TERM_SCREEN_RETAINED_ORIGINAL').length, 1);
      assert.ok(after.tasks.some(row => row.code === 'TERM_SCREEN_RETAINED_ORIGINAL' && row.screenId === bindingIds[2] && row.sourceTermId === term.id));
      await runNative(['invalidated', nativeFixture, nativeState]);
      const invalidated = await prisma.npepDevice.findMany({where: {id: {in: devices.map(row => row.id)}}});
      assert.ok(invalidated.length === 3 && invalidated.every(row => row.state === 'INVALIDATED'));
      assert.equal((await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: bindingIds[2]}})).administrativeClassId, eight.id);
      break;
    }
    case 'paired':
      await setNativeScope([newSeven.id, newSeven.id, eight.id]);
      await runNative(['repair-first', nativeFixture, nativeState]);
      assert.ok((await overview()).tasks.some(row => row.code === 'SCREEN_AWAITING_REPORT' && row.screenId === bindingIds[0]));
      break;
    case 'reported': {
      await runNative(['report-first', nativeFixture, nativeState]);
      const after = await overview();
      assert.equal(after.screens.find(row => row.screenId === bindingIds[0]).native.state, 'REPORTED');
      assert.equal(after.screens.find(row => row.screenId === bindingIds[1]).native.state, 'REPAIR_REQUIRED');
      assert.equal(after.tasks.some(row => row.screenId === bindingIds[0]), false);
      assert.ok(after.tasks.some(row => row.screenId === bindingIds[1] && row.code === 'SCREEN_REPAIR_REQUIRED'));
      assert.ok(after.tasks.some(row => row.screenId === bindingIds[2] && row.code === 'TERM_SCREEN_RETAINED_ORIGINAL'));
      break;
    }
    default: throw new Error('Unknown activation checkpoint');
    }
    stepLog.push(step);
  }

  async function verifyLegacyWebScope() {
    const [current, candidate] = await Promise.all(['ACTIVE', 'DRAFT'].map((status, i) => prisma.academicTerm.create({data: {
      schoolId: other.id, academicYear: 2026, semester: i + 1, name: i ? '网页兼容目标学期' : '网页原学期', status,
    }})));
    const rooms = [];
    for (const item of [current, candidate]) {
      const grade = await prisma.grade.create({data: {termId: item.id, code: 'G1', name: '高一'}});
      rooms.push(await prisma.workspace.create({data: {termId: item.id, gradeId: grade.id,
        type: 'ADMIN_CLASS', name: '高一（1）班', code: 'G1-1'}}));
    }
    const binding = await prisma.classroomScreenBinding.create({data: {schoolId: other.id, administrativeClassId: rooms[0].id,
      name: '网页兼容大屏', tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId}});
    return {current, candidate, binding, rooms};
  }

  async function verifyLegacyWeb(browser, url, {current, candidate, binding, rooms}) {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    try {
      await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
      await page.goto(url + '/classworks-admin?section=terms&school=' + other.id + '&term=' + candidate.id);
      await page.locator('.v-select').filter({hasText: /^学校/}).locator('.v-field').click();
      await page.getByRole('option', {name: new RegExp(school.name)}).click();
      await page.getByLabel('管理员短账号').fill('admin');
      await page.getByLabel('个人 PIN', {exact: true}).fill(pin);
      await page.getByRole('button', {name: '登录', exact: true}).click();
      const row = page.locator('.v-list-item').filter({hasText: candidate.name, has: page.getByRole('button', {name: '启用', exact: true})});
      const matches = path => response => new URL(response.url()).pathname.endsWith('/terms/' + candidate.id + path);
      async function review() {
        const pending = page.waitForResponse(matches('/transition-readiness'));
        await row.getByRole('button', {name: '启用', exact: true}).click();
        const response = await pending;
        assert.ok(response.ok());
        return (await response.json()).data.activationVersion;
      }
      const dialog = page.getByRole('dialog').filter({hasText: '确认切换学期'});
      async function submit(expected, status) {
        const pending = page.waitForResponse(matches('/activate'));
        await dialog.getByRole('button', {name: '确认切换学期'}).click();
        const response = await pending;
        assert.equal(response.status(), status);
        assert.equal(response.request().postDataJSON().expectedVersion, expected);
      }
      const stale = await review();
      await api('/api/v2/admin/schools/' + other.id + '/classroom-screens/' + binding.id, {method: 'PATCH', token,
        body: {name: '网页并发改名屏'}});
      await submit(stale, 409);
      await page.getByText(/网页并发改名屏/).first().waitFor({state: 'visible'});
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: current.id}})).status, 'ACTIVE');
      assert.equal((await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: binding.id}})).administrativeClassId, rooms[0].id);
      await page.screenshot({path: join(evidence, 'browser-legacy-stale.png'), animations: 'disabled'});
      const fresh = await review();
      assert.notEqual(fresh, stale);
      await submit(fresh, 200);
      await page.getByText(/已切换到网页兼容目标学期/).waitFor({state: 'visible'});
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: current.id}})).status, 'ARCHIVED');
      assert.equal((await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: binding.id}})).administrativeClassId, rooms[1].id);
      await page.screenshot({path: join(evidence, 'browser-legacy-success.png'), animations: 'disabled'});
    } catch (error) {
      await page.screenshot({path: join(evidence, 'browser-legacy-failure.png'), animations: 'disabled'});
      throw error;
    } finally { await page.close(); }
  }

  const backend = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__activation/')) {
      try { await checkpoint(req.url.slice('/__activation/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'POST' && req.url === activatePath) posts++;
    if (req.method === 'GET' && req.url.startsWith(base + '/activation-receipts/')) {
      reads++;
      if (reads === 1) { res.writeHead(503).end(); return; }
    }
    const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
      if (!lostReceipt && req.method === 'POST' && req.url === activatePath && reply.statusCode === 200) {
        const chunks = [];
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => {
          const body = Buffer.concat(chunks), result = JSON.parse(body).data;
          if (result?.state === 'APPLIED') { lostReceipt = result.receipt; res.destroy(); }
          else { res.writeHead(reply.statusCode, reply.headers); res.end(body); }
        });
      } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((yes, no) => relay.once('error', no).listen(0, '127.0.0.1', yes));
  try {
    const access = join(directory, 'activation-desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, password: pin, currentName: term.name, draftName: target.name, draftId,
      firstId: bindingIds[0], secondId: bindingIds[1], unmatchedId: bindingIds[2], allowActivation: true}), {mode: 0o600});
    await runDesktop(['--live-term-activation', access, evidence]);
    assert.deepEqual(stepLog, ['change-binding', 'confirm-no-write', 'before-repair', 'paired', 'reported']);

    const readAccess = async username => (await api('/api/v2/me/workspaces?termId=' + draftId, {token: await login(username)}))
      .map(row => [row.workspace.id, row.role]);
    const assignedAccess = await readAccess('viewer'), independentAccess = await readAccess('independent');
    assert.deepEqual(assignedAccess.sort(), newGroups.map(row => [row.id, 'TEACHER']).sort());
    assert.deepEqual(independentAccess, [[newGroups[0].id, 'ASSISTANT']]);
    assert.equal(await prisma.teachingAssignment.count({where: {accountId: member.id, workspace: {termId: draftId}}}), 0);
    const oldWrite = await api(oldEdit, {method: 'POST', token, status: 409, body: oldTeaching});
    assert.match(oldWrite.code, /TERM/);
    assert.equal(await prisma.administrativeClassStudent.count({where: {administrativeClassId: seven.id}}), 1);
    await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
    const legacy = await verifyLegacyWebScope();
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [newSeven], expectedTermId: draftId,
      afterStudent: (browser, url) => verifyLegacyWeb(browser, url, legacy)});
    const confirmation = await api(base + '/activation-receipts/' + lostReceipt.requestId, {token});
    await writeFile(join(evidence, 'server-result.json'), JSON.stringify({checks: stepLog, activeTerm: confirmation.activeTerm,
      archivedTerm: confirmation.receipt.archivedTerm, screens: confirmation.screens, requestId: lostReceipt.requestId,
      posts, reads, assignedAccess, independentAccess, oldFormRejection: oldWrite.code, studentNewTerm: draftId, legacyWebPassed: true}, null, 2));
    console.log('PASS: Windows activation and readback, independent screen recovery, teacher permissions, stale old form and actual student and school-admin Edge.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
