import assert from 'node:assert/strict';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {writeFile} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import {join} from 'node:path';

export async function verifyClassMaintenance({prisma, origin, fixture, directory, evidence, runDesktop}) {
  const {school, other, term, classes, devices, pin} = fixture;
  const [classId, , staleId] = classes;
  const [deviceId] = devices;
  const base = '/api/v2/admin/schools/' + school.id;
  const maintenance = base + '/current-term/administrative-classes/maintenance';
  const path = maintenance + '/' + classId;
  const impactPath = path + '/deactivation-impact?termId=' + term.id;
  const original = await prisma.workspace.findUniqueOrThrow({where: {id: classId}});
  const student = await prisma.administrativeClassStudent.create({data: {
    administrativeClassId: classId, name: '原班模拟学生', studentNumber: '01',
  }});
  const historicalStudent = await prisma.administrativeClassStudent.create({data: {
    administrativeClassId: classId, name: '模拟已移出学生', studentNumber: '02', isActive: false,
  }});
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'MATH', name: '数学'}});
  const teacher = await prisma.account.create({data: {provider: 'maintenance-fixture', providerId: randomUUID(), name: '模拟任课教师'}});
  const teaching = await prisma.teachingAssignment.create({data: {workspaceId: classId, accountId: teacher.id, subjectId: subject.id}});
  const notice = await prisma.publication.create({data: {
    type: 'NOTICE', content: '原班通知', title: '原班通知', status: 'PUBLISHED',
    targets: {create: {workspaceId: classId}},
  }});
  const screen = await prisma.classroomScreenBinding.findFirstOrThrow({where: {administrativeClassId: classId}});
  const browserSecret = randomBytes(32).toString('base64url');
  await prisma.classroomScreenBinding.update({where: {id: screen.id}, data: {tokenHash: createHash('sha256').update(browserSecret).digest('hex')}});
  const browser = () => fetch(origin + '/api/v2/classroom-screens/session', {headers: {'X-Classworks-Screen-Token': browserSecret}});
  const native = () => fetch(origin + '/api/v2/npep/device/school-management', {headers: {
    Authorization: 'Bearer ' + fixture.classroomBearer, 'X-NPEP-Version': '0.16', 'X-Request-Id': randomUUID(),
  }});
  const nativeProbe = process.env.NP_CONSOLE_CLASS_MAINTENANCE_NATIVE_PROBE;
  async function nativeClient(phase) {
    if (!nativeProbe) return;
    await new Promise((resolve, reject) => {
      const child = spawn('dotnet', [nativeProbe, phase, origin, join(directory, 'native-device-probe')],
        {stdio: ['pipe', 'inherit', 'inherit'], windowsHide: true, timeout: 20000});
      child.once('error', reject);
      child.once('close', code => code === 0 ? resolve() : reject(new Error('Native client probe failed: ' + phase)));
      child.stdin.end(phase === 'active' ? JSON.stringify({bearer: fixture.classroomBearer, identity: fixture.identity}) : '');
    });
  }
  const login = async username => {
    const reply = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username, password: pin})});
    assert.equal(reply.status, 200);
    return (await reply.json()).data.access_token;
  };
  const admin = await login('admin');
  const second = await login('admin2');
  const viewer = await login('viewer');
  async function call(route, token = admin, method = 'GET', body, status = 200) {
    const reply = await fetch(origin + route, {method, headers: {Authorization: 'Bearer ' + token, 'Content-Type': 'application/json'},
      body: body && JSON.stringify(body)});
    const result = await reply.json();
    assert.equal(reply.status, status, route + ' ' + (result.code ?? result.message ?? ''));
    return reply.ok ? result.data : result;
  }
  const edit = (row, code, name, selectable = false) => ({
    termId: term.id, action: 'EDIT', expectedUpdatedAt: row.updatedAt, code, name, isStudentSelectable: selectable,
  });
  let previewReads = 0, deactivationWrites = 0, addedScreen, oldImpact, concurrentEdit = false;
  const target = new URL(origin);
  const relay = createServer(async (req, res) => {
    try {
      if (req.method === 'GET' && req.url === impactPath && previewReads++ === 0) {
        res.writeHead(503).end();
        return;
      }
      if (req.method !== 'PATCH' || req.url !== path) {
        forward(req, res);
        return;
      }
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const bytes = Buffer.concat(chunks);
      const body = JSON.parse(bytes);
      if (body.action === 'EDIT' && body.code === 'G2.DESK' && !concurrentEdit) {
        concurrentEdit = true;
        const latest = await call(path + '?termId=' + term.id, second);
        await call(path, second, 'PATCH', edit(latest.administrativeClass, original.code, '另一管理员最新名称'));
      }
      if (body.action === 'DEACTIVATE' && ++deactivationWrites === 1) {
        oldImpact = body.impactToken;
        assert.equal((await native()).status, 200, 'editing name and code leaves the existing native authorization valid');
        addedScreen = await call(base + '/classroom-screen-accounts', second, 'POST', {
          administrativeClassId: classId, name: '审阅中新增的大屏', loginCode: 'SECOND.SCREEN', pin: '451237',
        }, 201);
      } else if (body.action === 'DEACTIVATE') assert.notEqual(body.impactToken, oldImpact);
      forward(req, res, bytes);
    } catch (error) {
      console.error('Class maintenance relay:', error.message);
      res.writeHead(502).end();
    }
  });
  function forward(req, res, bytes) {
    const upstream = request({hostname: '127.0.0.1', port: target.port, path: req.url, method: req.method,
      headers: {...req.headers, host: target.host}}, response => {
      res.writeHead(response.statusCode, response.headers);
      response.pipe(res);
    });
    upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
    if (bytes) upstream.end(bytes);
    else req.pipe(upstream);
  }
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const file = join(directory, 'desktop.json');
    const desktopOrigin = 'http://127.0.0.1:' + relay.address().port;
    const access = {origin: desktopOrigin, schoolId: school.id, schoolCode: school.code, username: 'admin', password: pin,
      classId, screenId: screen.id, allowClassMaintenanceTestWrites: true};
    await writeFile(file, JSON.stringify(access), {mode: 0o600});
    assert.equal((await browser()).status, 200);
    assert.equal((await native()).status, 200);
    await nativeClient('active');
    await runDesktop(['--live-class-maintenance', file, evidence]);
    assert.equal(deactivationWrites, 2);
    assert.ok(addedScreen.id && previewReads >= 3 && concurrentEdit);
    const inactive = await prisma.workspace.findUniqueOrThrow({where: {id: classId}});
    assert.equal(inactive.isActive, false);
    assert.equal(inactive.code, 'G2.DESK');
    assert.equal(inactive.isStudentSelectable, false);
    assert.equal(inactive.name, '高二（1）班 · 已修正');
    assert.equal((await call(maintenance)).classes[0].id, classId);
    for (const [get, status, code] of [[browser, 409, 'SCREEN_BINDING_INACTIVE'], [native, 401, 'AUTH_INVALID']]) {
      const rejected = await get();
      assert.equal(rejected.status, status);
      const problem = await rejected.json();
      assert.equal(problem.error?.code ?? problem.code, code);
    }
    console.log('PASS: known preview error, draft conflict, added screen invalidates the old preview; browser and native screen requests are refused while the class is inactive.');
    await nativeClient('inactive');
    await runDesktop(['--live-class-maintenance-reopen', file, join(evidence, 'reopen')]);
    assert.equal((await browser()).status, 200);
    const previousNative = await native();
    assert.equal(previousNative.status, 401);
    assert.equal((await previousNative.json()).error.code, 'AUTH_INVALID');
    await nativeClient('restored');
    const restored = await prisma.workspace.findUniqueOrThrow({where: {id: classId}});
    assert.deepEqual({...restored, name: original.name, code: original.code, isStudentSelectable: original.isStudentSelectable,
      updatedAt: original.updatedAt}, original);
    assert.deepEqual(await prisma.administrativeClassStudent.findUniqueOrThrow({where: {id: student.id}}), student);
    assert.deepEqual(await prisma.administrativeClassStudent.findUniqueOrThrow({where: {id: historicalStudent.id}}), historicalStudent);
    assert.deepEqual(await prisma.teachingAssignment.findUniqueOrThrow({where: {id: teaching.id}}), teaching);
    assert.equal(await prisma.publicationTarget.count({where: {workspaceId: classId, publicationId: notice.id}}), 1);
    assert.equal((await prisma.publication.findUniqueOrThrow({where: {id: notice.id}})).status, 'PUBLISHED');
    assert.equal(await prisma.classroomScreenBinding.count({where: {administrativeClassId: classId, id: {in: [screen.id, addedScreen.id]}}}), 2);
    const device = await prisma.npepDevice.findUniqueOrThrow({where: {id: deviceId}});
    assert.equal(device.screenBindingId, screen.id);
    assert.equal(device.administrativeClassId, classId);
    assert.equal(device.state, 'INVALIDATED');
    assert.equal(device.bindingRevision, screen.npepBindingRevision);
    assert.equal((await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: screen.id}})).npepBindingRevision,
      screen.npepBindingRevision + 1);
    const writes = await prisma.auditLog.findMany({where: {entityId: classId, action: 'ADMIN_CLASS_UPDATED', success: true},
      orderBy: {createdAt: 'asc'}});
    assert.deepEqual(writes.map(log => log.metadata.operation), ['EDIT', 'EDIT', 'DEACTIVATE', 'REACTIVATE']);
    assert.equal(writes[2].metadata.impact.screenBindings, 2);
    console.log('PASS: reactivation kept the class, roster, screen, device-history and notice IDs; the old web token resumed, and native pairing remained invalidated by the existing trigger.');

    const stalePath = maintenance + '/' + staleId;
    const baseline = (await call(stalePath + '?termId=' + term.id)).administrativeClass;
    const validPreview = await call(stalePath + '/deactivation-impact?termId=' + term.id);
    assert.ok(Object.values(validPreview.counts).every(count => count === 0), 'empty relations are an actual successful preview');
    const deactivate = {termId: term.id, action: 'DEACTIVATE', expectedUpdatedAt: baseline.updatedAt};
    assert.equal((await call(stalePath, admin, 'PATCH', {...deactivate, confirmImpact: true}, 409)).code,
      'CLASS_DEACTIVATION_PREVIEW_REQUIRED');
    assert.equal((await call(stalePath, second, 'PATCH', {...deactivate, impactToken: validPreview.impactToken}, 409)).code,
      'CLASS_DEACTIVATION_IMPACT_CHANGED');
    const addScreen = loginCode => call(base + '/classroom-screen-accounts', second, 'POST', {
      administrativeClassId: staleId, name: loginCode, loginCode, pin: '451237',
    }, 201);
    const firstBinding = await addScreen('CHECK.SCREEN.1');
    const firstPreview = await call(stalePath + '/deactivation-impact?termId=' + term.id);
    await call(base + '/classroom-screens/' + firstBinding.id, second, 'PATCH', {isActive: false});
    await addScreen('CHECK.SCREEN.2');
    const secondPreview = await call(stalePath + '/deactivation-impact?termId=' + term.id);
    assert.deepEqual(secondPreview.counts, firstPreview.counts);
    assert.notEqual(secondPreview.impactToken, firstPreview.impactToken);
    assert.equal((await call(stalePath, admin, 'PATCH', {...deactivate, impactToken: firstPreview.impactToken}, 409)).code,
      'CLASS_DEACTIVATION_IMPACT_CHANGED', 'equal counts with different screen IDs require a new review');
    assert.equal((await call(stalePath, viewer, 'PATCH', edit(baseline, 'FORBID.A', '不应写入'), 403)).code, 'SCHOOL_ADMIN_REQUIRED');
    const crossSchool = maintenance.replace(school.id, other.id) + '/' + staleId;
    assert.equal((await call(crossSchool, admin, 'PATCH', edit(baseline, 'FORBID.B', '不应写入'), 403)).code, 'SCHOOL_ADMIN_REQUIRED');
    const next = await prisma.academicTerm.create({data: {
      schoolId: school.id, semester: 2, academicYear: 2026, status: 'DRAFT', name: '下一学期',
    }});
    await prisma.$transaction([
      prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}}),
      prisma.academicTerm.update({where: {id: next.id}, data: {status: 'ACTIVE'}}),
    ]);
    assert.equal((await call(stalePath, admin, 'PATCH', edit(baseline, 'STALE.A', '旧表单'), 409)).code, 'CLASS_MAINTENANCE_TERM_CHANGED');
    assert.equal((await call(stalePath, admin, 'PATCH', {...deactivate, impactToken: validPreview.impactToken}, 409)).code,
      'CLASS_MAINTENANCE_TERM_CHANGED');
    assert.equal((await call(maintenance)).classes.length, 0);
    const legacy = base + '/administrative-classes/' + classId;
    const legacyPreview = await call(base + '/workspaces/' + classId + '/change-impact');
    assert.equal(legacyPreview.counts.students, 2); // Legacy impact includes historical student records.
    const legacyWrite = await call(legacy, admin, 'PATCH', {name: '旧网页修改归档班', expectedUpdatedAt: restored.updatedAt});
    assert.equal(legacyWrite.id, classId);
    assert.equal(legacyWrite.termId, term.id);
    assert.equal((await call(legacy, admin, 'PATCH', {isActive: false, expectedUpdatedAt: legacyWrite.updatedAt}, 409)).code,
      'ORGANIZATION_CHANGE_CONFIRMATION_REQUIRED');
    const legacyDisabled = await call(legacy, admin, 'PATCH', {isActive: false, expectedUpdatedAt: legacyWrite.updatedAt, confirmImpact: true});
    assert.equal(legacyDisabled.isActive, false);
    const legacyRestored = await call(legacy, admin, 'PATCH', {isActive: true, expectedUpdatedAt: legacyDisabled.updatedAt});
    assert.equal(legacyRestored.id, classId);
    assert.equal(legacyRestored.isActive, true);
    console.log('PASS: empty impact, account-bound preview, equal-count screen replacement, viewer/cross-school refusal, stale term rejection, and legacy editing/reversible deactivation in an archived term.');
  } finally {
    const closed = new Promise(resolve => relay.close(resolve));
    relay.closeAllConnections();
    await closed;
  }
}
