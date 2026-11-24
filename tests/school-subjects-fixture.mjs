import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';

export async function verifySchoolSubjects({prisma, origin, fixture, directory, evidence, bcrypt, localProviderId, runDesktop, runDatabase}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, pin} = fixture;
  const noTerm = await prisma.school.create({data: {code: 'CATALOG-NO-TERM', name: '未启用学期的学校'}});
  await prisma.schoolMember.create({data: {schoolId: noTerm.id, accountId: fixture.managerId, role: 'ADMIN'}});
  const grade = await prisma.grade.create({data: {termId: term.id, code: 'G1', name: '高一'}});
  const [seven, eight] = await Promise.all([7, 8].map(number => prisma.workspace.create({data: {
    termId: term.id, gradeId: grade.id, code: `G1-${number}`, name: `高一（${number}）班`, type: 'ADMIN_CLASS',
  }})));
  await prisma.subject.createMany({data: Array.from({length: 52}, (_, index) => ({schoolId: school.id,
    code: `SAMPLE-${String(index + 1).padStart(2, '0')}`, name: `目录样例学科 ${index + 1}`, sortOrder: 30 + index}))});
  const math = await prisma.subject.create({data: {schoolId: school.id, code: 'CAT-MATH', name: '数学', category: 'CORE'}});
  await prisma.subject.create({data: {schoolId: other.id, code: 'CAT-PHYSICS', name: '外校物理'}});
  async function account(username, name, role) {
    const entry = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(school.code, username),
      localUsername: username, name, localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: school.code}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: entry.id, role}});
    return entry;
  }
  const teacher = await account('catalogteacher', '李老师', 'VIEWER');
  await account('catalogadmin2', '另一位管理员', 'ADMIN');
  const schoolPath = `/api/v2/admin/schools/${school.id}`;
  const subjectsPath = `${schoolPath}/subjects`;
  const emptyPath = `/api/v2/admin/schools/${noTerm.id}/subjects`;
  async function call(path, {method = 'GET', token, body, status = 200} = {}) {
    const headers = {'Content-Type': 'application/json'};
    if (token) headers.Authorization = `Bearer ${token}`;
    const response = await fetch(origin + path, {method, headers, body: body && JSON.stringify(body)});
    const result = await response.json();
    assert.equal(response.status, status, `${path}: ${result.code ?? response.status}`);
    return status >= 400 ? result : result.data ?? result;
  }
  const login = async username => (await call('/accounts/local/login', {method: 'POST',
    body: {schoolCode: school.code, username, password: pin}})).access_token;
  const admin = await login('admin');
  const otherAdmin = await login('catalogadmin2');
  let physics, attempts = 0, receiptReads = 0, competitorCreated = false, codeAttempts = 0, previewBeforeSetup = false;
  let physicsRequestId, dropPhysicsReply;
  const physicsWriteHeld = Promise.withResolvers();
  const physicsReadHeld = Promise.withResolvers();
  const physicsReadCanceled = Promise.withResolvers();
  const physicsReceiptReads = [];
  let delayNextCatalog = false;
  let delayedCatalogArrived, delayedCatalogSent, catalogArrived, catalogSent;
  const writeRequests = new Map();
  const backend = new URL(origin);
  const server = createServer(async (req, res) => {
    try {
      let input;
      if (req.method === 'POST' && req.url === '/__school-subjects/wait-physics-write') {
        await physicsWriteHeld.promise;
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'POST' && req.url === '/__school-subjects/drop-physics-reply') {
        dropPhysicsReply();
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'POST' && req.url === '/__school-subjects/wait-physics-read') {
        await physicsReadHeld.promise;
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'POST' && req.url === '/__school-subjects/wait-physics-read-canceled') {
        await physicsReadCanceled.promise;
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'POST' && req.url === '/__school-subjects/arm-catalog-delay') {
        delayedCatalogArrived = new Promise(resolve => { catalogArrived = resolve; });
        delayedCatalogSent = new Promise(resolve => { catalogSent = resolve; });
        delayNextCatalog = true;
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'POST' && req.url === '/__school-subjects/wait-catalog-arrival') {
        await delayedCatalogArrived;
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'POST' && req.url === '/__school-subjects/wait-catalog-sent') {
        await delayedCatalogSent;
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'POST' && req.url === '/__school-subjects/race-edit') {
        const current = (await call(subjectsPath, {token: otherAdmin})).subjects.find(item => item.id === math.id);
        await call(`${subjectsPath}/${math.id}`, {method: 'PATCH', token: otherAdmin,
          body: {name: '学校数学新名称', expectedUpdatedAt: current.updatedAt}});
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'GET' && req.url === '/__school-subjects/confirmed-physics') {
        assert.ok(physics);
        assert.equal(writeRequests.get('CAT-PHYSICS'), 1);
        assert.deepEqual(physicsReceiptReads, [physicsRequestId, physicsRequestId]);
        assert.equal(await prisma.administrativeClassSubject.count({where: {subjectId: physics.id}}), 0);
        previewBeforeSetup = true;
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({physicsId: physics.id}));
        return;
      }
      if (req.method === 'POST' && req.url === '/__school-subjects/switch-term') {
        await prisma.$transaction(async tx => {
          await tx.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}});
          await tx.academicTerm.create({data: {schoolId: school.id, name: '春季学期', academicYear: 2026, semester: 2, status: 'ACTIVE'}});
        });
        res.writeHead(204).end();
        return;
      }
      if (req.method === 'GET' && req.url.startsWith(subjectsPath + '/creation-receipts/')) receiptReads++;
      const physicsReceipt = req.method === 'GET' && physicsRequestId &&
        req.url === `${subjectsPath}/creation-receipts/${physicsRequestId}`;
      if (physicsReceipt) physicsReceiptReads.push(req.url.split('/').at(-1));
      const holdPhysicsRead = physicsReceipt && physicsReceiptReads.length === 1;
      if (delayNextCatalog && req.method === 'GET' && req.url === subjectsPath) {
        delayNextCatalog = false;
        catalogArrived();
        setTimeout(() => { forward(); catalogSent(); }, 750);
        return;
      }
      if (req.method === 'POST' && req.url === subjectsPath) {
        const chunks = [];
        for await (const chunk of req) chunks.push(chunk);
        const wire = Buffer.concat(chunks);
        input = JSON.parse(wire);
        writeRequests.set(input.code, (writeRequests.get(input.code) || 0) + 1);
        if (input.code === 'LOST-OTHER' && !competitorCreated) {
          await call(subjectsPath, {method: 'POST', token: otherAdmin, status: 201,
            body: {code: input.code, name: '另一位管理员的学科'}});
          competitorCreated = true;
          res.destroy();
          return;
        }
        if (input.code === 'LOST-OTHER') codeAttempts++;
        if (input.code === 'CAT-PHYSICS') attempts++;
        forward(wire);
      } else forward();

      function forward(wire) {
        const upstream = request(new URL(req.url, backend), {method: req.method,
          headers: {...req.headers, host: backend.host}}, reply => {
          if (input?.code === 'CAT-PHYSICS' && reply.statusCode === 201 && !physics) {
            const data = [];
            reply.on('data', chunk => data.push(chunk));
            reply.on('end', () => {
              physics = JSON.parse(Buffer.concat(data)).data;
              physicsRequestId = input.requestId;
              dropPhysicsReply = () => res.destroy();
              physicsWriteHeld.resolve();
            });
          } else if (holdPhysicsRead && reply.statusCode === 200) {
            res.once('close', () => { if (!res.writableEnded) physicsReadCanceled.resolve(); });
            reply.on('end', () => physicsReadHeld.resolve());
            reply.resume();
          } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
        });
        upstream.on('error', () => res.destroy());
        if (wire || req.method === 'GET') upstream.end(wire);
        else req.pipe(upstream);
      }
    } catch (error) { res.writeHead(500).end(error.message); }
  });
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const access = join(directory, 'desktop.json');
    const config = {origin: `http://127.0.0.1:${server.address().port}`, schoolId: school.id, schoolCode: school.code, password: pin,
      noTermId: noTerm.id, sevenId: seven.id, eightId: eight.id, mathId: math.id, teacherId: teacher.id, allowSchoolSubjectWrites: true};
    await writeFile(access, JSON.stringify(config), {mode: 0o600});
    await runDesktop(['--live-school-subjects', access, evidence]);
    assert.ok(physics && attempts === 1 && receiptReads >= 2 && codeAttempts === 1 && previewBeforeSetup);
    const rules = await prisma.administrativeClassSubject.findMany({where: {subjectId: physics.id}});
    assert.deepEqual(rules.map(rule => rule.administrativeClassId).sort(), [seven.id, eight.id].sort());
    const assignments = await prisma.teachingAssignment.findMany({where: {subjectId: physics.id}});
    assert.deepEqual(assignments.map(item => [item.workspaceId, item.accountId]), [[seven.id, teacher.id]]);
    const tasks = (await call(`${schoolPath}/management-overview/current`, {token: admin})).tasks;
    assert.deepEqual(tasks.filter(task => task.code === 'ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER' && task.subjectId === physics.id)
      .map(task => task.classId), [eight.id]);
    assert.equal((await call(emptyPath, {token: admin})).subjects[0].code, 'NO-TERM');
    assert.equal(await prisma.academicTerm.count({where: {schoolId: noTerm.id}}), 0);
    assert.equal((await call(subjectsPath, {token: admin})).subjects.find(item => item.id === math.id).name, '数学（核对后更新）');
    assert.equal(await prisma.subject.count({where: {schoolId: school.id, code: 'CAT-PHYSICS'}}), 1);
    assert.equal(writeRequests.get('CAT-PHYSICS'), 1);
    await writeFile(join(evidence, 'creation-recovery.json'), JSON.stringify({schoolId: school.id, subjectId: physics.id,
      creationPosts: writeRequests.get('CAT-PHYSICS'), receiptReads: physicsReceiptReads, firstReadCanceled: true}, null, 2));
    console.log('PASS: PostgreSQL confirms one owned catalog creation, no rules until explicit setup, physics on 7 and 8, teacher and task only on the intended IDs.');
    await writeFile(access, JSON.stringify({...config, physicsId: physics.id}), {mode: 0o600});
    await runDesktop(['--live-school-subjects-reopen', access, evidence]);
    const lateSubject = await prisma.subject.findUniqueOrThrow({where: {schoolId_code: {schoolId: school.id, code: 'CAT-CHEM'}}});
    assert.equal(await prisma.administrativeClassSubject.count({where: {subjectId: lateSubject.id}}), 0);
    const legacy = await call(`${schoolPath}/academic-structure?termId=${term.id}`, {token: admin});
    assert.equal(legacy.term.status, 'ARCHIVED');
    assert.equal(legacy.subjects.find(subject => subject.id === physics.id).name, '物理');
    assert.ok(legacy.administrativeClasses.find(room => room.id === seven.id).subjectRules.some(rule => rule.subjectId === physics.id));
    assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: seven.id, subjectId: physics.id, accountId: teacher.id}}), 1);
    assert.equal((await call(`/api/v2/admin/schools/${other.id}/subjects`, {token: admin, status: 403})).code, 'SCHOOL_ADMIN_REQUIRED');
    console.log('PASS: restarted desktop found the original physics ID; term switch blocked old writes and the archived web structure still references the same subject and class.');
    await runDatabase();
  } finally {
    const closed = new Promise(resolve => server.close(resolve));
    server.closeAllConnections();
    await closed;
  }
}
