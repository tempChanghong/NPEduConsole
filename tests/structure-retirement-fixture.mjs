import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash, randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, readFile, rm, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyStructureRetirement({prisma, origin, fixture, directory, evidence, createTeacher, runDesktop, web, zip, manifestPath}) {
  await mkdir(evidence, {recursive: true});
  const {school, term, pin} = fixture;
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const archived = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2025,
    semester: 1, status: 'ARCHIVED', name: '秋季学期'}});
  const archiveGrade = await prisma.grade.create({data: {termId: archived.id, name: '高二', code: 'G2'}});
  const otherGrade = await prisma.grade.create({data: {termId: term.id, name: '高三', code: 'G3', sortOrder: 20}});
  const room = (termId, gradeId, code, name) => prisma.workspace.create({
    data: {termId, gradeId, type: 'ADMIN_CLASS', code, name},
  });
  const [seven, eight, nine, sameCode, otherRoom] = await Promise.all([
    room(term.id, grade.id, 'G2-7', '高二（7）班'), room(term.id, grade.id, 'G2-8', '高二（8）班'),
    room(term.id, grade.id, 'G2-9', '高二（9）班'), room(archived.id, archiveGrade.id, 'G2-7', '高二（7）班'),
    room(term.id, otherGrade.id, 'G3-7', '高三（7）班'),
  ]);
  const physics = await prisma.subject.create({data: {schoolId: school.id, code: 'PHY', name: '物理'}});
  await prisma.administrativeClassSubject.createMany({data: [seven, eight, nine, sameCode, otherRoom].map(row => ({
    administrativeClassId: row.id, subjectId: physics.id, deliveryMode: row.id === eight.id ? 'COURSE_GROUP' : 'ADMIN_CLASS',
    isCompulsory: true,
  }))});
  const [mixed, first, second, disabled] = await Promise.all([
    createTeacher('mixed-teacher', '混合分配老师'), createTeacher('first-teacher', '周老师'),
    createTeacher('second-teacher', '周老师'), createTeacher('disabled-teacher', '周老师'),
  ]);
  const schoolPath = '/api/v2/admin/schools/' + school.id;
  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const result = await response.json();
    assert.equal(response.status, status, path + ': ' + (result.code ?? response.status));
    return result.data ?? result;
  }
  const login = async username => (await api('/accounts/local/login', {method: 'POST', body: {
    schoolCode: school.code, username, password: pin,
  }})).access_token;
  const [actor, otherActor, teacherToken] = await Promise.all(['admin', 'admin2', 'mixed-teacher'].map(login));
  const group = code => api(schoolPath + '/course-groups', {method: 'POST', token: actor, status: 201,
    body: {termId: term.id, gradeId: grade.id, subjectId: physics.id, name: '物理 A1', code,
      sourceClassIds: [eight.id], isStudentSelectable: true}});
  const [a1, conflict] = await Promise.all([group('PHY-MIXED'), group('PHY-CONFLICT')]);
  const assign = (workspaceId, accountId, position) => api(schoolPath + '/teaching-assignments', {method: 'PUT',
    token: actor, body: {workspaceId, accountId, position, subjectId: physics.id}});
  await assign(conflict.id, first.id, 'PRIMARY');
  await assign(conflict.id, second.id, 'PRIMARY');
  const disabledAssignment = await assign(conflict.id, disabled.id, 'CO_TEACHER');
  await prisma.account.update({where: {id: disabled.id}, data: {localDisabled: true}});
  const assignment = (workspaceId, accountId) => prisma.teachingAssignment.findFirst({
    where: {workspaceId, accountId, subjectId: physics.id, isActive: true},
  });
  const changeAccess = (role, token = actor) => api(schoolPath.split('/schools/')[0] + '/workspaces/' + a1.id + '/members', {
    method: 'PUT', token, body: {accountId: mixed.id, role},
  });
  await changeAccess('VIEWER');
  const planPath = schoolPath + '/academic-terms/' + term.id + '/teaching/grade-plans';
  const overview = (parameters = '') => api(schoolPath + '/management-overview/current?courseGroupActions=1' + parameters, {token: actor});
  const duplicate = row => row.code === 'MULTIPLE_PRIMARY_TEACHERS' && row.courseGroupId === conflict.id;
  const old = (await overview()).tasks.find(duplicate);
  const exact = (await overview('&structureActions=1')).tasks.find(duplicate);
  assert.notEqual(old.action, 'OPEN_TERM_TEACHING');
  assert.deepEqual([exact.action, exact.gradeId, exact.subjectId], ['OPEN_TERM_TEACHING', grade.id, physics.id]);
  const edgeReads = [];
  let release;
  if (zip && manifestPath) {
    const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
    const bytes = await readFile(zip);
    assert.equal(manifest.version, '0.1.10');
    assert.equal(manifest.sizeBytes, bytes.length);
    assert.equal(manifest.sha256, createHash('sha256').update(bytes).digest('hex'));
    assert.equal((await api('/api/v2/setup/status')).supportsDesktopStructure, true);
    const access = join(directory, 'released-structure.json');
    const output = join(evidence, 'released');
    await writeFile(access, JSON.stringify({isolated: true,
      login: {url: origin, website: origin, code: school.code, username: 'admin', pin},
      structureRetirement: {taskId: 'SchoolOverviewTask-MULTIPLE_PRIMARY_TEACHERS-' + conflict.id + '-' + physics.id}}), {mode: 0o600});
    try {
      await new Promise((resolve, reject) => {
        const console = spawn('pwsh.exe', ['-NoProfile', '-STA', '-File',
          fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url)), '-ManagementLogin',
          '-FixturePath', access, '-ZipPath', zip, '-ManifestPath', manifestPath, '-EvidenceDirectory', output],
        {windowsHide: true, stdio: 'inherit'});
        console.once('error', reject);
        console.once('exit', code => code === 0 ? resolve() : reject(new Error('Released structure check failed: ' + code)));
      });
    } finally {
      await rm(access, {force: true});
    }
    await verifyStudentBrowser({origin, directory, web, evidence, school, rooms: [], afterStudent: async (browser, site) => {
      const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
      try {
        await page.addInitScript(token => localStorage.setItem('classworks-v2-access-token', token), actor);
        let sections = ['structure', 'accounts'], oldKv = true;
        await page.route(`${site}/.well-known/npclassworks-deployment.json`, route => route.fulfill({
          json: {schemaVersion: 1, adminLegacyManagementEnabled: true, apiBaseUrl: site, adminRetiredSections: sections},
        }));
        await page.route(`${site}/downloads/npeduconsole/**`, route => route.request().url().endsWith('/console-release.json') ?
          route.fulfill({json: manifest}) : route.fulfill({contentType: 'application/zip',
            headers: {'Content-Length': String(manifest.sizeBytes)}}));
        await page.route(`${site}/api/v2/setup/status`, async route => {
          if (!oldKv) return route.continue();
          const response = await route.fetch();
          const body = await response.json();
          delete body.data.supportsDesktopStructure;
          return route.fulfill({response, json: body});
        });
        page.on('request', req => {
          const url = new URL(req.url());
          if (/\/(?:academic-structure|teaching-relationships)$/.test(url.pathname))
            edgeReads.push({method: req.method(), term: url.searchParams.get('termId'), resource: url.pathname.split('/').at(-1)});
        });
        const link = site + '/classworks-admin?' + new URLSearchParams({section: 'structure', school: school.id,
          term: term.id, space: conflict.id, subject: physics.id, teacher: first.id});
        await page.goto(link);
        await page.getByText('物理 A1（PHY-CONFLICT） · 物理', {exact: true}).waitFor();
        await page.getByRole('button', {name: '核对原任课'}).click();
        await page.getByRole('dialog').getByText('周老师（first-teacher） · 主讲教师').waitFor();
        await page.screenshot({path: join(evidence, 'edge-old-kv-original-teacher.png')});
        oldKv = false;
        await page.reload();
        await page.getByText('原教学空间编号：' + conflict.id).waitFor();
        await page.getByRole('button', {name: /获取 Windows 管理端/i}).waitFor();
        await page.screenshot({path: join(evidence, 'edge-retired-original-scope.png')});
        await page.setViewportSize({width: 390, height: 920});
        await page.screenshot({path: join(evidence, 'edge-retired-original-scope-narrow.png')});
        assert.deepEqual(edgeReads, [{method: 'GET', term: term.id, resource: 'teaching-relationships'}]);
        sections = ['accounts'];
        await page.reload();
        await page.getByRole('button', {name: '核对原任课'}).waitFor();
        assert.deepEqual(edgeReads, Array.from({length: 2}, () => ({method: 'GET', term: term.id, resource: 'teaching-relationships'})));
        await page.goto(site + '/classworks-admin?' + new URLSearchParams({section: 'accounts', school: school.id}));
        await page.getByRole('heading', {name: '网页版已停止办理账号与管理员'}).waitFor();
      } finally { await page.close(); }
    }});
    release = {version: manifest.version, sha256: manifest.sha256, sizeBytes: manifest.sizeBytes};
  }
  const checks = [], writes = [], reads = [];
  let suppressNextWrite = false;
  async function checkpoint(step) {
    if (step === 'other-admin-changes-access') await changeAccess('ASSISTANT', otherActor);
    else if (step === 'stale') {
      assert.deepEqual(writes.map(row => row.state), ['STALE']);
      assert.ok(writes[0].changedKeys.includes(a1.id));
      assert.equal(await prisma.gradeTeachingPlanReceipt.count({where: {schoolId: school.id}}), 0);
      assert.equal(await assignment(seven.id, mixed.id), null);
      assert.equal(await assignment(a1.id, mixed.id), null);
      assert.equal((await prisma.workspaceMember.findUniqueOrThrow({
        where: {workspaceId_accountId: {workspaceId: a1.id, accountId: mixed.id}},
      })).role, 'ASSISTANT');
    } else if (step === 'receipt') {
      assert.deepEqual(writes.map(row => row.state), ['STALE', 'APPLIED']);
      assert.deepEqual(reads, [writes[1].id]);
      const spaces = await api('/api/v2/me/workspaces?termId=' + term.id, {token: teacherToken});
      assert.deepEqual(spaces.map(row => [row.workspace.id, row.role]).sort(), [[seven.id, 'TEACHER'], [a1.id, 'TEACHER']].sort());
      for (const target of [seven, a1]) assert.equal((await assignment(target.id, mixed.id)).position, 'PRIMARY');
      for (const target of [eight, nine, sameCode, otherRoom, conflict]) assert.equal(await assignment(target.id, mixed.id), null);
    } else if (step === 'resolved-exact-group') {
      assert.ok(await assignment(conflict.id, first.id));
      assert.equal(await assignment(conflict.id, second.id), null);
      assert.ok(await assignment(conflict.id, disabled.id));
      assert.ok(await assignment(a1.id, mixed.id));
      assert.equal((await overview('&structureActions=1')).tasks.some(duplicate), false);
    } else if (step === 'miss-next-write') {
      suppressNextWrite = true;
    } else if (step === 'separate-pending') {
      assert.ok(await assignment(conflict.id, first.id));
      assert.ok(await assignment(conflict.id, disabled.id));
      assert.equal(writes.length, 4);
      assert.deepEqual(writes.map(row => row.state), ['STALE', 'APPLIED', 'APPLIED', 'NOT_FORWARDED']);
      assert.deepEqual(reads, [writes[1].id, writes[3].id]);
    } else if (step === 'other-admin-restores-teacher') {
      const {scopeVersion: expectedScope} = await api(planPath, {token: actor});
      const draft = {termId: term.id, gradeId: grade.id, subjectId: physics.id, expectedScope,
        expectedIssue: {code: 'DISABLED_TEACHER_ASSIGNED', workspaceId: conflict.id, subjectId: physics.id, accountId: disabled.id},
        assignments: [{workspaceKey: conflict.id, subjectId: physics.id, action: 'REMOVE', assignmentId: disabledAssignment.id}]};
      const preview = body => api(planPath + '/preview', {method: 'POST', token: actor, body});
      const before = await preview(draft);
      assert.equal(before.canApply, true);
      await api(schoolPath + '/local-accounts/' + disabled.id, {method: 'PATCH', token: otherActor, body: {disabled: false}});
      const stale = await api(planPath, {method: 'POST', token: actor, body: {...draft,
        requestId: randomUUID(), expectedVersion: before.version, expectedEvidence: before.evidence}});
      assert.equal(stale.state, 'STALE');
      const after = await preview(draft);
      assert.equal(after.canApply, false);
      assert.ok(after.issues.some(row => row.code === 'GRADE_PLAN_ORIGINAL_ISSUE_RESOLVED' && row.key === conflict.id));
    } else if (step === 'healthy-assignment-preserved') {
      assert.equal((await assignment(conflict.id, disabled.id)).id, disabledAssignment.id);
      assert.equal((await prisma.account.findUniqueOrThrow({where: {id: disabled.id}})).localDisabled, false);
      assert.equal(await prisma.gradeTeachingPlanReceipt.count({where: {schoolId: school.id}}), 2);
      assert.equal(writes.length, 4);
      assert.deepEqual(reads, [writes[1].id, writes[3].id]);
    } else throw new Error('Unknown check: ' + step);
    checks.push(step);
  }
  const backend = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.url.startsWith('/__structure/')) {
      try { await checkpoint(req.url.split('/').at(-1)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    const save = req.method === 'POST' && req.url === planPath;
    if (req.method === 'GET' && req.url.startsWith(planPath + '/receipts/')) reads.push(req.url.split('/').at(-1));
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const body = Buffer.concat(chunks);
    const id = save ? JSON.parse(body).requestId : null;
    if (save && suppressNextWrite) {
      suppressNextWrite = false;
      writes.push({id, state: 'NOT_FORWARDED'});
      res.destroy();
      return;
    }
    const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
      if (!save) {
        res.writeHead(reply.statusCode, reply.headers);
        reply.pipe(res);
        return;
      }
      const response = [];
      reply.on('data', chunk => response.push(chunk));
      reply.on('end', () => {
        const raw = Buffer.concat(response);
        const data = JSON.parse(raw).data;
        writes.push({id, state: data?.state, changedKeys: data?.changedKeys});
        if (writes.length === 2 && data?.state === 'APPLIED') res.destroy();
        else res.writeHead(reply.statusCode, reply.headers).end(raw);
      });
    });
    upstream.on('error', () => res.destroy());
    upstream.end(body);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const access = join(directory, 'structure-desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, password: pin, termId: term.id, gradeId: grade.id, subjectId: physics.id,
      roomId: seven.id, groupId: a1.id, conflictId: conflict.id, firstId: first.id, secondId: second.id,
      disabledId: disabled.id, mixedId: mixed.id, allowStructureTestWrites: true}), {mode: 0o600});
    await runDesktop(['--live-structure-retirement', access, evidence]);
    assert.equal(checks.length, 8);
    await writeFile(join(evidence, 'structure-result.json'), JSON.stringify({checks, schoolId: school.id, termId: term.id,
      preserved: [eight.id, nine.id, sameCode.id, otherRoom.id], actualTeachingSpaces: [seven.id, a1.id],
      exactGroup: conflict.id, subjectId: physics.id, writes, reads, edgeReads, release, passed: true}, null, 2));
    console.log('PASS: structure mixed plan, membership-only stale, real teacher access, original receipt, exact conflict and distinct pending teacher.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
