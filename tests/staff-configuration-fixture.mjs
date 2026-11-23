import assert from 'node:assert/strict';
import {mkdir, mkdtemp, readdir, readFile, realpath, rm, rmdir, writeFile} from 'node:fs/promises';
import {createServer, request} from 'node:http';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyStaffConfiguration({prisma, origin, fixture, directory, evidence, web, bcrypt, runDesktop}) {
  directory = await realpath(directory);
  const {school, other, term, pin} = fixture;
  await mkdir(evidence, {recursive: true});
  const second = await prisma.account.findFirstOrThrow({where: {providerId: `${school.code}:admin2`, provider: 'school-local'}});
  await prisma.schoolMember.createMany({data: [fixture.managerId, second.id].map(accountId => ({schoolId: other.id, accountId, role: 'ADMIN'}))});
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const old = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: term.academicYear - 1,
    semester: term.semester, name: '归档同码学期', status: 'ARCHIVED'}});
  const oldGrade = await prisma.grade.create({data: {termId: old.id, code: grade.code, name: grade.name}});
  const room = (forTerm, forGrade) => prisma.workspace.create({data: {termId: forTerm.id, gradeId: forGrade.id, code: 'STAFF-C07',
    name: '高二（7）班 · 师资核对', type: 'ADMIN_CLASS'}});
  const [current, archived, subject] = await Promise.all([room(term, grade), room(old, oldGrade), prisma.subject.create({data: {
    schoolId: school.id, code: 'STAFF-PHY', name: '师资物理', category: 'CORE'}})]);
  const group = await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, code: 'STAFF-A1', name: '走班 A1',
    type: 'COURSE_GROUP', subjectId: subject.id}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: current.id, subjectId: subject.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true}});
  async function teacher(username, name) {
    const account = await prisma.account.create({data: {name, provider: 'school-local', providerId: `${school.code}:${username}`,
      localUsername: username, localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: school.code}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: account.id, role: 'VIEWER'}});
    return account;
  }
  const [existing, incumbent] = await Promise.all([teacher('staff-same-a', '周老师'), teacher('staff-incumbent', '现任老师')]);
  await prisma.workspaceMember.createMany({data: [{workspaceId: current.id, accountId: existing.id, role: 'VIEWER'},
    {workspaceId: archived.id, accountId: existing.id, role: 'VIEWER'}, {workspaceId: current.id, accountId: incumbent.id, role: 'TEACHER'}]});
  const assigned = await prisma.teachingAssignment.create({data: {workspaceId: current.id, subjectId: subject.id, accountId: incumbent.id,
    position: 'PRIMARY'}});
  const duty = await prisma.administrativeClassLeadership.create({data: {administrativeClassId: current.id,
    accountId: incumbent.id, position: 'HEAD_TEACHER'}});
  const newPin = pin === '472683' ? '472684' : '472683';
  const otherPin = pin === '659427' ? '659428' : '659427';
  const document = teachers => ({schemaVersion: 1, schoolCode: school.code, term: {academicYear: term.academicYear, semester: term.semester}, teachers});
  const file = (username, name, extra = {}) => ({username, name, credential: {mode: 'FIXED_PIN', pin: otherPin}, ...extra});
  const plan = document([file('staff-same-a', '周老师', {
    teachingAssignments: [{workspaceCode: current.code, subjectCode: subject.code, position: 'PRIMARY'}],
    responsibilities: {classLeaderships: [{classCode: current.code, position: 'HEAD_TEACHER'}]}
  }), file('staff-same-b', '周老师', {credential: {mode: 'FIXED_PIN', pin: newPin},
    teachingAssignments: [{workspaceCode: current.code, subjectCode: subject.code, position: 'CO_TEACHER'}]})]);
  const savedInput = join(directory, 'staff-plan.json');
  const wrongInput = join(directory, 'staff-plan-archived.json');
  const interruptedInput = join(directory, 'staff-plan-interrupted.json');
  const changedInput = join(directory, 'staff-plan-changed.json');
  const lostInput = join(directory, 'staff-plan-lost.json');
  // The restricted PostgreSQL directory is read-only to the desktop runner. Keep its mutable scratch separate.
  const clientDirectory = await mkdtemp(join(evidence, 'temporary-staff-client-'));
  const handoff = join(clientDirectory, 'temporary-handoff.txt');
  await Promise.all([
    writeFile(savedInput, JSON.stringify(plan)),
    writeFile(wrongInput, JSON.stringify({...plan, term: {...plan.term, academicYear: old.academicYear}})),
    writeFile(interruptedInput, JSON.stringify(document([file('staff-not-sent', '未到达示例')]))),
    writeFile(changedInput, JSON.stringify(document([file('staff-cancelled', '旧确认变更示例')]))),
    writeFile(lostInput, JSON.stringify(document([file('staff-lost', '查回示例', {
      teachingAssignments: [{workspaceCode: current.code, subjectCode: subject.code, position: 'CO_TEACHER'},
        {workspaceCode: group.code, subjectCode: subject.code, position: 'PRIMARY'}],
      responsibilities: {classLeaderships: [{classCode: current.code, position: 'CO_HEAD_TEACHER'}]}
    })]))),
    writeFile(handoff, '')
  ]);

  const path = `/api/v2/admin/schools/${school.id}/staff-configuration`;
  const counters = {commitCount: 0, previewCount: 0, dropped: [], received: [], readFromKV: [], stale: 0, commands: []};
  let lost, holdRead = false, heldRead, malformedRead = false, next = '';
  const byUsername = username => prisma.account.findFirst({where: {provider: 'school-local', providerId: `${school.code}:${username}`}});
  const receipts = () => prisma.$queryRaw`SELECT "requestId", "result" FROM "StaffConfigurationReceipt" WHERE "schoolId"=${school.id}`;
  const login = async (username, password) => {
    const response = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username, password})});
    assert.equal(response.status, 200);
    return (await response.json()).data;
  };
  const original = await login(existing.localUsername, pin);
  const before = await byUsername(existing.localUsername);
  const commands = {
    async teaching() { await prisma.teachingAssignment.update({where: {id: assigned.id}, data: {isActive: false}}); },
    async duty() { await prisma.administrativeClassLeadership.update({where: {administrativeClassId_accountId: {
      administrativeClassId: current.id, accountId: incumbent.id}}, data: {isActive: false}}); },
    async changed() { await prisma.teachingAssignment.update({where: {id: assigned.id}, data: {isActive: true}}); },
    async stale() {
      assert.equal(await byUsername('staff-same-b'), null);
      assert.equal((await receipts()).length, 0);
      assert.equal(await prisma.administrativeClassLeadership.count({where: {administrativeClassId: current.id, accountId: existing.id}}), 0);
      await commands.teaching();
    },
    async miss() { next = 'miss'; },
    async alterdate() { await prisma.academicTerm.update({where: {id: term.id}, data: {endsAt: new Date('2027-02-03T00:00:00Z')}}); },
    async revoke() { await prisma.schoolMember.delete({where: {schoolId_accountId: {schoolId: school.id, accountId: second.id}}}); },
    async lost() { next = 'lost'; holdRead = true; },
    async held() {
      for (let i = 0; i < 100 && !heldRead; i++) await new Promise(resolve => setTimeout(resolve, 25));
      assert.ok(heldRead, 'the desktop started a receipt read after the lost commit response');
    },
    async release() {
      assert.ok(heldRead);
      if (!heldRead.destroyed) heldRead.writeHead(502).end();
      malformedRead = true;
    }
  };
  const relay = createServer(async (req, res) => {
    try {
      const name = /^\/__staff-plan\/([a-z]+)$/.exec(req.url)?.[1];
      if (req.method === 'POST' && name && Object.hasOwn(commands, name)) {
        await commands[name]();
        counters.commands.push(name);
        res.writeHead(204).end();
        return;
      }
      const commit = req.method === 'POST' && req.url === path;
      const reading = req.method === 'GET' && req.url.startsWith(path + '/receipts/');
      if (req.method === 'POST' && req.url === path + '/preview') counters.previewCount++;
      if (reading && holdRead) {
        holdRead = false;
        heldRead = res;
        return;
      }
      if (reading && malformedRead) {
        malformedRead = false;
        res.writeHead(200, {'Content-Type': 'application/json'}).end(JSON.stringify({success: true, data: {state: 'APPLIED',
          receipt: {requestId: lost, schoolId: school.id, termId: term.id}}}));
        return;
      }
      if (reading) counters.readFromKV.push(req.url.split('/').at(-1));
      let disposition, body;
      if (commit) {
        counters.commitCount++;
        disposition = next;
        next = '';
        const buffers = [];
        for await (const part of req) buffers.push(part);
        body = Buffer.concat(buffers);
        const id = JSON.parse(body).requestId;
        if (disposition === 'miss') {
          counters.dropped.push(id);
          res.writeHead(502).end();
          return;
        }
        if (disposition === 'lost') lost = id;
        counters.received.push(id);
      }
      const upstream = request(origin + req.url, {method: req.method, headers: req.headers}, response => {
        if (commit && response.statusCode === 409) counters.stale++;
        if (disposition === 'lost' && response.statusCode === 200) {
          response.resume();
          response.once('end', () => res.writeHead(502).end());
          return;
        }
        res.writeHead(response.statusCode, response.headers);
        response.pipe(res);
      });
      upstream.on('error', () => res.destroy());
      if (body) upstream.end(body);
      else req.pipe(upstream);
    } catch (error) { console.error('Staff configuration fixture:', error.message); res.writeHead(500).end(); }
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const fixtureFile = join(directory, 'staff-plan-desktop.json');
    const localSettings = join(clientDirectory, 'last-server.txt');
    await writeFile(fixtureFile, JSON.stringify({origin: `http://127.0.0.1:${relay.address().port}`, schoolId: school.id, schoolCode: school.code,
      otherId: other.id, pin, termName: term.name, currentId: current.id, savedInput, wrongInput, interruptedInput, changedInput, lostInput, handoff, localSettings,
      isolated: true}));
    await runDesktop(['--live-staff-configuration', fixtureFile, evidence]);
    const requests = join(clientDirectory, 'staff-configuration-requests');
    const [pending] = await readdir(requests);
    assert.equal(pending, lost + '.json');
    const metadata = JSON.parse(await readFile(join(requests, pending), 'utf8'));
    assert.deepEqual(Object.keys(metadata).sort(), ['ActorId', 'Id', 'Proof', 'SchoolId', 'Server', 'TermId']);
    assert.equal(metadata.Id, lost);
    assert.match(metadata.Proof, /^[0-9a-f]{64}$/);
    const writes = await receipts();
    assert.equal(writes.filter(row => Array.isArray(row.result.teachers)).length, 4);
    await runDesktop(['--live-staff-configuration-reopen', fixtureFile, evidence]);
    assert.deepEqual(await readdir(requests), []);
    assert.equal(counters.commitCount, 7);
    assert.equal(counters.stale, 1);
    const [sameProof, changedProof] = counters.dropped;
    assert.ok(sameProof && changedProof && lost && counters.received.filter(id => id === sameProof).length === 1 &&
      !counters.received.includes(changedProof) && counters.received.filter(id => id === lost).length === 1);
    assert.ok(counters.readFromKV.includes(sameProof) && counters.readFromKV.includes(changedProof) && counters.readFromKV.includes(lost));
    assert.equal(counters.readFromKV.filter(id => id === changedProof).length, 1, 'normal cancellation response was accepted without another receipt GET');
    const after = await byUsername(existing.localUsername);
    assert.equal(after.localPasswordHash, before.localPasswordHash);
    assert.equal(after.tokenVersion, before.tokenVersion);
    assert.equal(await prisma.workspaceMember.count({where: {workspaceId: archived.id, role: 'VIEWER', accountId: existing.id}}), 1);
    assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: archived.id}}), 0);
    const established = await fetch(origin + '/api/v2/me/workspaces', {headers: {Authorization: `Bearer ${original.access_token}`}});
    assert.ok(established.ok);
    assert.deepEqual((await established.json()).data.map(row => row.workspace.id), [current.id]);
    assert.equal((await readFile(handoff, 'utf8')).split('\n').at(-1), 'PIN ' + newPin);
    await rm(handoff, {force: true});

    let browserIds;
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (browser, site) => {
      const page = await browser.newPage();
      try {
        await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
        await page.goto(site);
        await page.getByText('教师登录', {exact: true}).click();
        await page.getByText('登录教师工作台', {exact: true}).waitFor();
        await page.locator('.v-select').filter({hasText: '学校'}).locator('.v-field').click();
        await page.getByRole('option', {name: school.name, exact: true}).click();
        await page.getByLabel('教师短账号').fill('staff-same-b');
        await page.getByLabel('个人 PIN').fill(newPin);
        const list = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v2/me/workspaces' && response.ok());
        await page.getByRole('button', {name: '登录', exact: true}).click();
        browserIds = (await (await list).json()).data.map(row => row.workspace.id);
        assert.deepEqual(browserIds, [current.id]);
        await page.getByText(/已授权 .* 个教学空间/).first().waitFor();
        await page.screenshot({path: join(evidence, 'edge-new-teacher.png')});
      } finally { await page.close(); }
    }});
    await writeFile(join(evidence, 'staff-configuration-result.json'), JSON.stringify({passed: true, ...counters,
      current: current.id, archived: archived.id, browserIds}, null, 2));
  } finally {
    await rm(handoff, {force: true});
    await rm(join(clientDirectory, 'last-server.txt'), {force: true});
    // A failing run may leave non-sensitive pending metadata for inspection.
    await rmdir(join(clientDirectory, 'staff-configuration-requests')).catch(() => {});
    await rmdir(clientDirectory).catch(() => {});
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
