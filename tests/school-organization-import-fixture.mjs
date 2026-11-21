import assert from 'node:assert/strict';
import {createHash, randomBytes} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, mkdtemp, rm, writeFile} from 'node:fs/promises';
import {dirname, basename, join, resolve} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyOrganizationImport(options) {
  const evidence = resolve(options.evidence);
  await mkdir(evidence, {recursive: true});
  const nativeState = await mkdtemp(join(evidence, 'native-state-'));
  try { await verify({...options, evidence, nativeRoot: join(nativeState, 'devices')}); }
  finally {
    if (dirname(resolve(nativeState)) === evidence && basename(nativeState).startsWith('native-state-'))
      await rm(nativeState, {recursive: true, force: true});
  }
}

async function verify({prisma, origin, fixture, directory, evidence, web, runDesktop, runNative, nativeRoot}) {
  const {school, term, pin, managerId} = fixture;
  const path = '/api/v2/admin/schools/' + school.id;
  const importPath = path + '/organization-import';
  const marks = [];
  async function api(relative, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + relative, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const payload = await response.json();
    assert.equal(response.status, status, relative + ': ' + (payload.code ?? response.status));
    return payload.data ?? payload;
  }
  const login = username => api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username, password: pin}});
  const [admin, browserAdmin, teacherSession] = await Promise.all(['admin', 'admin2', 'viewer'].map(login));
  const teacher = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer', schoolMemberships: {some: {schoolId: school.id}}}});
  const activeGrade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const draft = await prisma.academicTerm.create({data: {schoolId: school.id, name: '2027 春季筹备',
    academicYear: 2027, semester: 1, status: 'DRAFT', startsAt: new Date('2027-02-15'), endsAt: new Date('2027-07-15')}});
  const grade = await prisma.grade.create({data: {termId: draft.id, code: 'G2', name: '高二'}});
  const otherGrade = await prisma.grade.create({data: {termId: draft.id, code: 'G1', name: '高一'}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'PHY', name: '物理', category: 'CORE'}});
  const arts = await prisma.subject.create({data: {schoolId: school.id, code: 'ART', name: '美术', category: 'ELECTIVE'}});
  const room = (termId, gradeId, code, name) => prisma.workspace.create({data: {termId, gradeId, code, name, type: 'ADMIN_CLASS'}});
  const [oldSeven, oldNine, draftEight] = await Promise.all([
    room(term.id, activeGrade.id, 'G2-C07', '高二（07）班'), room(term.id, activeGrade.id, 'G2-C09', '高二（09）班'),
    room(draft.id, grade.id, 'G2-C08', '高二（08）班')
  ]);
  await prisma.administrativeClassSubject.createMany({data: [
    {administrativeClassId: oldSeven.id, subjectId: subject.id, deliveryMode: 'COURSE_GROUP', isCompulsory: true},
    {administrativeClassId: oldNine.id, subjectId: subject.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true},
    {administrativeClassId: draftEight.id, subjectId: arts.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: false}
  ]});
  await prisma.administrativeClassStudent.create({data: {administrativeClassId: oldSeven.id, name: '原学期学生', studentNumber: '0701'}});
  const groups = [];
  for (const name of ['A1', 'A2']) {
    const group = await api(path + '/course-groups', {method: 'POST', token: admin.access_token, status: 201,
      body: {termId: term.id, gradeId: activeGrade.id, subjectId: subject.id, name: '物理 ' + name,
        code: 'G2-PHY-' + name, sourceClassIds: [oldSeven.id], isStudentSelectable: true}});
    groups.push(group);
    await api(path + '/teaching-assignments', {method: 'PUT', token: admin.access_token,
      body: {workspaceId: group.id, subjectId: subject.id, accountId: teacher.id, position: 'PRIMARY'}});
  }
  const originalAccess = async token => (await api('/api/v2/me/workspaces?termId=' + term.id, {token}))
    .map(row => row.id ?? row.workspace?.id).filter(Boolean).sort();
  const teacherBefore = await originalAccess(teacherSession.access_token);
  for (const group of groups) assert.ok(teacherBefore.includes(group.id), 'original teacher has both real assignments');
  const screens = await Promise.all(['前屏', '后屏'].map(name => prisma.classroomScreenBinding.create({data: {
    schoolId: school.id, administrativeClassId: oldSeven.id, npepPrepared: true, name,
    tokenHash: createHash('sha256').update(randomBytes(32)).digest('hex'), createdByAccountId: managerId
  }})));
  const nativeFixture = join(directory, 'organization-native.json');
  await writeFile(nativeFixture, JSON.stringify({kind: 'TERM_PREPARATION_DISPOSABLE_DATABASE', origin, adminToken: admin.access_token,
    schoolId: school.id, classId: oldSeven.id, bindingIds: screens.map(row => row.id)}), {mode: 0o600});
  await runNative(['pair', nativeFixture, nativeRoot]);
  const getDevices = () => prisma.npepDevice.findMany({where: {screenBindingId: {in: screens.map(row => row.id)}}, orderBy: {id: 'asc'}});
  const devicesBefore = await getDevices();
  assert.equal(devicesBefore.length, 2);
  assert.ok(devicesBefore.every(row => row.state === 'ACTIVE' && row.lastReportedAt));

  const draftFile = join(directory, 'draft-organization.json');
  const classCode = n => 'G2-C' + String(n).padStart(2, '0');
  const className = n => '高二（' + String(n).padStart(2, '0') + '）班';
  const file = {school: {code: school.code, name: school.name, teacherAuth: {sharedPassword: 'fixture-not-an-import-field'}},
    term: {name: draft.name, academicYear: draft.academicYear, semester: draft.semester, status: 'ACTIVE'},
    grade: {code: grade.code, name: grade.name, sortOrder: grade.sortOrder},
    subjects: [{code: subject.code, name: subject.name, category: 'CORE'}],
    administrativeClasses: Array.from({length: 7}, (_, i) => ({code: classCode(i + 7), name: className(i + 7),
      subjectRules: {PHY: {deliveryMode: i < 2 ? 'COURSE_GROUP' : 'ADMIN_CLASS', isCompulsory: true}}})),
    courseGroups: [{code: 'G2-PHY-A1', name: '物理 A1', subject: subject.code, isStudentSelectable: true,
      sourceClasses: [7, 8, 9].map(classCode)}]};
  await writeFile(draftFile, JSON.stringify(file), {mode: 0o600});
  const wrongGradeFile = join(directory, 'wrong-grade-organization.json');
  await writeFile(wrongGradeFile, JSON.stringify({...file, grade: {code: otherGrade.code, name: otherGrade.name},
    administrativeClasses: [{code: 'G1-C01', name: '高一（01）班', subjectRules: {PHY: 'ADMIN_CLASS'}}], courseGroups: []}), {mode: 0o600});

  let browser, url, firstReceipt, receiptReads = 0, staleCommits = 0, currentReceipt;
  async function legacyChange() {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    try {
      await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
      await page.addInitScript(({access, refresh}) => {
        localStorage.setItem('classworks-v2-access-token', access);
        localStorage.setItem('classworks-v2-refresh-token', refresh);
      }, {access: browserAdmin.access_token, refresh: browserAdmin.refresh_token});
      await page.goto(url + '/classworks-admin?section=structure&school=' + school.id + '&term=' + draft.id);
      await page.getByRole('tab', {name: '年级与行政班'}).click();
      const row = page.locator('.v-list-item').filter({hasText: draftEight.name}).filter({hasText: draftEight.code});
      await row.getByRole('button').click();
      const editor = page.getByRole('dialog').filter({hasText: '编辑行政班'});
      await editor.getByLabel('班级名称').fill('高二（08）班 · 网页已改');
      const saved = page.waitForResponse(res => res.request().method() === 'PATCH' && res.url().endsWith('/administrative-classes/' + draftEight.id) && res.ok());
      await editor.getByRole('button', {name: '保存', exact: true}).click();
      await saved;
      await page.getByText('高二（08）班 · 网页已改', {exact: true}).waitFor();
      await page.screenshot({path: join(evidence, 'edge-edited-draft-eighth.png'), animations: 'disabled'});
    } catch (error) {
      await page.screenshot({path: join(evidence, 'edge-organization-failure.png')}).catch(() => {});
      throw error;
    } finally { await page.close(); }
  }

  const apply = async draftBody => {
    const preview = await api(importPath + '/preview', {method: 'POST', token: admin.access_token, body: draftBody});
    return {preview, save: () => api(importPath, {method: 'POST', token: admin.access_token, body: {...draftBody,
      requestId: crypto.randomUUID(), expectedVersion: preview.version, expectedEvidence: preview.evidence}})};
  };
  async function checkpoint(step) {
    if (step === 'wrong-grade-zero-write') {
      assert.equal(await prisma.workspace.count({where: {termId: draft.id, gradeId: otherGrade.id}}), 0);
      assert.deepEqual((await prisma.workspace.findMany({where: {termId: draft.id, gradeId: grade.id}})).map(row => row.id), [draftEight.id]);
      assert.equal((await prisma.$queryRaw`SELECT count(*)::int AS n FROM "SchoolOrganizationImportReceipt" WHERE "schoolId"=${school.id}`)[0].n, 0);
    } else if (step === 'legacy-change') await legacyChange();
    else if (step === 'stale-zero-write') {
      assert.equal(staleCommits, 1);
      assert.equal(await prisma.workspace.count({where: {termId: draft.id}}), 1);
      assert.equal((await prisma.workspace.findUniqueOrThrow({where: {id: draftEight.id}})).name, '高二（08）班 · 网页已改');
      assert.equal((await prisma.$queryRaw`SELECT count(*)::int AS n FROM "SchoolOrganizationImportReceipt" WHERE "schoolId"=${school.id}`)[0].n, 0);
    } else if (step === 'draft-applied') {
      assert.equal(receiptReads, 1);
      assert.equal(firstReceipt.administrativeClasses.length, 7);
      assert.equal(firstReceipt.courseGroups.length, 1);
      assert.equal(firstReceipt.gradeId, grade.id);
      assert.equal(firstReceipt.administrativeClasses.find(row => row.code === draftEight.code).id, draftEight.id);
      const actualGroup = await prisma.workspace.findUniqueOrThrow({where: {id: firstReceipt.courseGroups[0].id}, include: {sourceClasses: true}});
      assert.deepEqual(actualGroup.sourceClasses.map(row => row.administrativeClassId).sort(),
        firstReceipt.administrativeClasses.filter(row => [7, 8].map(classCode).includes(row.code)).map(row => row.id).sort());
      assert.equal(await prisma.administrativeClassSubject.count({where: {administrativeClassId: draftEight.id, subjectId: arts.id}}), 1);
      const nineId = firstReceipt.administrativeClasses.find(row => row.code === classCode(9)).id;
      assert.equal((await prisma.administrativeClassSubject.findUniqueOrThrow({where: {administrativeClassId_subjectId:
        {administrativeClassId: nineId, subjectId: subject.id}}})).deliveryMode, 'ADMIN_CLASS');
      assert.equal(await prisma.workspace.count({where: {termId: draft.id}}), 8);
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: draft.id}})).status, 'DRAFT');
    } else if (step === 'repeat-kept') {
      const patch = {termId: draft.id, termStatus: 'DRAFT', grade: file.grade, subjects: [],
        administrativeClasses: [{code: draftEight.code, name: className(8), subjectRules: [{subjectCode: 'PHY',
          deliveryMode: 'COURSE_GROUP', isCompulsory: true}]}],
        courseGroups: [{code: 'G2-PHY-A1', name: '物理 A1', subjectCode: 'PHY', sourceClassCodes: [classCode(7)]}]};
      const kept = await apply(patch);
      assert.ok(kept.preview.canApply && kept.preview.changes.every(row => row.action === 'KEEP'));
      const replace = structuredClone(patch);
      replace.administrativeClasses[0].replaceSubjectRules = true;
      replace.courseGroups[0].replaceSources = true;
      const explicit = await apply(replace);
      assert.deepEqual(explicit.preview.changes.filter(row => row.action === 'REMOVE').map(row => row.path).sort(),
        ['administrativeClasses[0].replaceSubjectRules', 'courseGroups[0].sourceClassCodes']);
      assert.ok(explicit.preview.canApply);
      assert.equal((await explicit.save()).state, 'APPLIED');
      assert.equal(await prisma.administrativeClassSubject.count({where: {administrativeClassId: draftEight.id, subjectId: arts.id}}), 0);
      // Put the deliberately removed relations back for the subsequent term check.
      patch.administrativeClasses[0].subjectRules.push({subjectCode: 'ART', deliveryMode: 'ADMIN_CLASS', isCompulsory: false});
      patch.courseGroups[0].sourceClassCodes.push(draftEight.code);
      assert.equal((await (await apply(patch)).save()).state, 'APPLIED');
    } else if (step === 'current-applied') {
      assert.equal(currentReceipt.administrativeClasses.length, 1);
      const row = await prisma.workspace.findUniqueOrThrow({where: {id: currentReceipt.administrativeClasses[0].id}, include: {subjectRules: true}});
      assert.equal(row.code, 'G2-C15');
      assert.equal(row.termId, term.id);
      assert.equal(row.subjectRules[0].subjectId, subject.id);
      assert.equal(row.subjectRules[0].deliveryMode, 'ADMIN_CLASS');
      assert.equal((await prisma.workspace.findUniqueOrThrow({where: {id: oldNine.id}})).name, oldNine.name);
    } else throw new Error('Unknown organization import checkpoint.');
    marks.push(step);
  }

  const upstreamOrigin = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__org/')) {
      try { await checkpoint(req.url.slice('/__org/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'GET' && req.url.startsWith(importPath + '/receipts/')) receiptReads++;
    const upstream = request(new URL(req.url, upstreamOrigin), {method: req.method, headers: {...req.headers, host: upstreamOrigin.host}}, reply => {
      if (req.method !== 'POST' || req.url !== importPath || reply.statusCode !== 200) {
        res.writeHead(reply.statusCode, reply.headers); reply.pipe(res);
        return;
      }
      const chunks = [];
      reply.on('data', chunk => chunks.push(chunk));
      reply.on('end', () => {
        const body = Buffer.concat(chunks), result = JSON.parse(body).data;
        if (result?.state === 'STALE') staleCommits++;
        if (result?.state === 'APPLIED' && result.receipt.termStatus === 'DRAFT' && !firstReceipt) {
          firstReceipt = result.receipt;
          res.destroy();
          return;
        }
        if (result?.state === 'APPLIED' && result.receipt.termStatus === 'ACTIVE') currentReceipt = result.receipt;
        res.writeHead(reply.statusCode, reply.headers);
        res.end(body);
      });
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const desktopAccess = join(directory, 'organization-desktop.json');
    await writeFile(desktopAccess, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, password: pin, draftId: draft.id, draftName: draft.name, activeId: term.id, draftFile, wrongGradeFile,
      allowOrganizationTestWrites: true}), {mode: 0o600});
    // Use the same real Edge instance for old administration and post-import student selection.
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (b, frontend) => {
      browser = b;
      url = frontend;
      await runDesktop(['--live-organization-import', desktopAccess, evidence]);
      await verifyOriginalStudent(browser, url, evidence, school, term, oldSeven);
    }});
    const teacherAfter = await login('viewer');
    assert.deepEqual(await originalAccess(teacherAfter.access_token), teacherBefore);
    await runNative(['report', nativeFixture, nativeRoot]);
    const devicesAfter = await getDevices();
    for (const [index, before] of devicesBefore.entries()) {
      const after = devicesAfter[index];
      for (const key of ['id', 'credentialId', 'state', 'screenBindingId', 'administrativeClassId', 'bindingRevision'])
        assert.equal(after[key], before[key]);
      assert.ok(after.lastReportedAt > before.lastReportedAt && after.statusEpoch > before.statusEpoch);
    }
    assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: draft.id}})).status, 'DRAFT');
    assert.equal(await prisma.administrativeClassStudent.count({where: {administrativeClassId: oldSeven.id}}), 1);
    await writeFile(join(evidence, 'organization-cross-end-result.json'), JSON.stringify({passed: true, marks, staleCommits, receiptReads,
      draftId: draft.id, draftRooms: firstReceipt.administrativeClasses.length, draftGroups: firstReceipt.courseGroups.length,
      currentRoomId: currentReceipt.administrativeClasses[0].id, originalTermId: term.id, teacherWorkspaces: teacherBefore,
      nativeBindings: devicesAfter.map(row => ({bindingId: row.screenBindingId, classId: row.administrativeClassId,
        state: row.state, reportedAt: row.lastReportedAt}))}, null, 2));
    console.log('PASS: Windows organization import, Edge legacy edit and student selection, PostgreSQL, teacher and original native pairings.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}

async function verifyOriginalStudent(browser, url, evidence, school, term, room) {
  const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
  try {
    await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
    await page.goto(url, {waitUntil: 'domcontentloaded'});
    await page.getByRole('button', {name: /开始选班/}).click();
    const dialog = page.getByRole('dialog').filter({hasText: '选择我的班级'});
    const choose = async (label, item) => {
      await dialog.locator('.v-select').filter({hasText: label}).locator('.v-field').click();
      await page.getByRole('option', {name: item, exact: true}).click();
    };
    const current = page.waitForResponse(res => res.url().includes('/catalog/terms/current?') && res.ok());
    await choose(/^学校/, school.name);
    assert.equal((await (await current).json()).data.id, term.id);
    await choose(/^行政班/, room.name);
    await dialog.locator('.v-select').filter({hasText: '物理（必须确认）'}).locator('.v-field').click();
    await page.getByRole('option', {name: '物理 A2', exact: true}).waitFor();
    await page.screenshot({path: join(evidence, 'edge-original-student-options.png'), animations: 'disabled'});
    await page.getByRole('option', {name: '物理 A1', exact: true}).click();
    await Promise.all([page.waitForResponse(res => res.url().includes('/student-selection/validate') && res.ok()),
      dialog.getByRole('button', {name: '保存并查看作业'}).click()]);
    await dialog.waitFor({state: 'hidden'});
  } finally { await page.close(); }
}
