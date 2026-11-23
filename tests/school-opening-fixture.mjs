import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {mkdir, mkdtemp, realpath, rm, writeFile} from 'node:fs/promises';
import {basename, dirname, join} from 'node:path';
import {randomBytes, randomUUID} from 'node:crypto';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifySchoolOpening({prisma, origin, fixture, directory, evidence, web, createTeacher, runDesktop, runNative}) {
  const {school, term, other, pin, managerId} = fixture;
  await mkdir(evidence, {recursive: true});
  const path = '/api/v2/admin/schools/' + school.id;
  async function api(url, {method = 'GET', token, body, status = 200} = {}) {
    const response = await fetch(origin + url, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const data = await response.json();
    assert.equal(response.status, status, method + ' ' + url + ' ' + (data.code ?? response.status));
    return data.data;
  }
  const login = async username => (await api('/accounts/local/login', {method: 'POST',
    body: {schoolCode: school.code, username, password: pin}})).access_token;
  const [manager, second, teacher, sameName] = await Promise.all([
    login('admin'), login('admin2'), createTeacher('opening-a', '周老师'), createTeacher('opening-b', '周老师'),
  ]);
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
  const history = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2025, semester: 2,
    status: 'ARCHIVED', name: '往届同码学期'}});
  const [grade, oldGrade, physics, chemistry] = await Promise.all([
    prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}}),
    prisma.grade.create({data: {termId: history.id, code: 'G2', name: '高二'}}),
    prisma.subject.create({data: {schoolId: school.id, code: 'OPEN-PHY', name: '物理'}}),
    prisma.subject.create({data: {schoolId: school.id, code: 'OPEN-CHEM', name: '化学'}}),
  ]);
  const createRoom = (targetGrade, n) => prisma.workspace.create({data: {termId: targetGrade.termId,
    gradeId: targetGrade.id, name: `高二（${n}）班`, code: 'OPEN-' + n, type: 'ADMIN_CLASS'}});
  const [seven, eight, formerEight] = await Promise.all([createRoom(grade, 7), createRoom(grade, 8), createRoom(oldGrade, 8)]);
  const group = await prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, name: '化学 A1', code: 'OPEN-A1',
    type: 'COURSE_GROUP', subjectId: chemistry.id, sourceClasses: {create: {administrativeClassId: eight.id}}}});
  await prisma.administrativeClassSubject.createMany({data: [
    {administrativeClassId: seven.id, subjectId: physics.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true},
    {administrativeClassId: eight.id, subjectId: physics.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true},
    {administrativeClassId: eight.id, subjectId: chemistry.id, deliveryMode: 'COURSE_GROUP', isCompulsory: false},
  ]});
  const assign = (room, subject, account, token) => api(path + '/teaching-assignments', {method: 'PUT', token,
    body: {workspaceId: room.id, subjectId: subject.id, accountId: account.id, position: 'PRIMARY'}});
  await assign(eight, physics, teacher, manager);
  await assign(group, chemistry, teacher, manager);
  await prisma.workspaceMember.create({data: {workspaceId: seven.id, accountId: sameName.id, role: 'TEACHER'}});
  await prisma.administrativeClassLeadership.create({data: {administrativeClassId: eight.id, accountId: teacher.id, position: 'HEAD_TEACHER'}});
  await prisma.gradeLeadership.create({data: {gradeId: grade.id, accountId: sameName.id, position: 'DEPUTY'}});
  const screenCode = 'opening-front';
  const front = await api(path + '/classroom-screen-accounts', {method: 'POST', token: manager, status: 201,
    body: {administrativeClassId: eight.id, name: '八班前屏', loginCode: screenCode, pin}});
  const rear = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: eight.id,
    name: '八班后屏', createdByAccountId: managerId, npepPrepared: true, tokenHash: randomBytes(32).toString('hex')}});
  await prisma.classroomScreenBinding.update({where: {id: front.id}, data: {npepPrepared: true}});
  const secret = randomBytes(22).toString('base64url');
  const nativePath = '/api/v2/npep/schools/' + school.id + '/class-management/' + front.id;
  async function native(method, body, version = '0.16') {
    const requestId = randomUUID();
    const res = await fetch(origin + nativePath, {method, headers: {'X-NPEP-Version': version,
      'X-Request-Id': requestId, Authorization: 'Bearer ' + manager, 'Content-Type': 'application/json'},
    body: body ? JSON.stringify({...body, requestId}) : undefined});
    assert.equal(res.status, 200, 'native management ' + method);
    return (await res.json()).data;
  }
  const admission = await native('GET');
  await native('POST', {enabled: true, secret, expectedRevision: admission.revision, expectedClassId: admission.classId,
    expectedBindingRevision: admission.bindingRevision, expectedPairingRevision: admission.pairingRevision, requireUnpaired: true});
  const nativeFixture = join(directory, 'school-opening-native.json');
  const signals = [];
  let browser, site, holdRead = false, releaseRead, browserProof, connectionProof;
  async function browserPage(run) {
    const page = await browser.newPage({viewport: {width: 1300, height: 870}, serviceWorkers: 'block'});
    await page.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
    try { await run(page); } finally { await page.close(); }
  }
  const event = (page, pathname, method = 'POST') => page.waitForResponse(response =>
    new URL(response.url()).pathname === pathname && response.request().method() === method && response.ok(), {timeout: 18000});
  const commands = {
    async staffed() {
      await assign(seven, physics, sameName, second);
      await browserPage(async page => {
        await page.goto(site);
        await page.getByText('教师登录', {exact: true}).click();
        await page.getByText('登录教师工作台', {exact: true}).waitFor();
        await page.locator('.v-select').filter({hasText: '学校'}).locator('.v-field').click();
        await page.getByRole('option', {name: school.name, exact: true}).click();
        await page.getByLabel('教师短账号').fill(sameName.localUsername);
        await page.getByLabel('个人 PIN').fill(pin);
        const list = event(page, '/api/v2/me/workspaces', 'GET');
        await page.getByRole('button', {name: '登录', exact: true}).click();
        browserProof = (await (await list).json()).data.map(row => row.workspace.id);
        assert.ok(browserProof.includes(seven.id) && !browserProof.includes(formerEight.id));
        await page.getByText(/已授权 .* 个教学空间/).first().waitFor();
        await page.screenshot({path: join(evidence, 'edge-teacher.png')});
      });
    },
    async connected() {
      const root = await realpath(evidence);
      const nativeDirectory = await mkdtemp(join(root, 'isolated-opening-host-'));
      try {
        await writeFile(nativeFixture, JSON.stringify({origin, schoolId: school.id, schoolCode: school.code, classId: eight.id,
          bindingId: front.id, rearId: rear.id, managementSecret: secret, password: pin, nativeDirectory, isolated: true}), {mode: 0o600});
        await runNative(['--native-school-transfer', nativeFixture]);
      } finally {
        const verifiedDirectory = await realpath(nativeDirectory);
        assert.equal(dirname(verifiedDirectory), root);
        assert.ok(basename(verifiedDirectory).startsWith('isolated-opening-host-'));
        await rm(verifiedDirectory, {recursive: true, force: true});
      }
      await browserPage(async page => {
        await page.goto(site);
        await page.getByRole('button', {name: /激活大屏/}).click();
        const form = page.locator('.screen-login-card');
        await form.waitFor({state: 'visible'});
        if (!await form.locator('.v-select__selection-text').filter({hasText: school.name}).count()) {
          await form.locator('.v-select .v-field').click();
          await page.getByRole('option', {name: school.name, exact: true}).click();
        }
        await form.locator('.v-text-field').filter({hasText: '大屏账号'}).locator('input').fill(screenCode);
        await form.locator('.v-text-field').filter({hasText: '大屏 PIN'}).locator('input').fill(pin);
        const logged = event(page, '/api/v2/classroom-screens/login');
        const pulse = event(page, '/api/v2/classroom-screens/heartbeat');
        await form.getByRole('button', {name: '登录并进入大屏'}).click();
        assert.equal((await (await logged).json()).data.binding.id, front.id);
        await page.getByRole('button', {name: '完成并进入大屏', exact: true}).click();
        await pulse;
        await page.screenshot({path: join(evidence, 'edge-front-screen.png')});
      });
      const observed = await api(path + '/screen-duty', {token: second});
      const rows = observed.screens.filter(row => [front.id, rear.id].includes(row.id));
      connectionProof = rows.map(row => ({id: row.id, browser: row.browserState, heartbeatAt: row.heartbeatAt,
        native: row.nativeState, nativeReportedAt: row.nativeReportedAt}));
      assert.equal(rows.find(row => row.id === front.id).browserState, 'ONLINE');
      assert.equal(rows.find(row => row.id === front.id).nativeState, 'REPORTED');
      assert.equal(rows.find(row => row.id === rear.id).nativeState, 'AWAITING_PAIRING');
      process.env.FRONTEND_URL = site;
    },
    hold() { holdRead = true; },
    async held() {
      for (let i = 0; i < 100 && !releaseRead; i++) await new Promise(resolve => setTimeout(resolve, 25));
      assert.ok(releaseRead, 'previous school read was captured');
    },
    release() { releaseRead?.(); },
  };
  const relay = createServer(async (req, res) => {
    const command = /^\/__school-opening\/([a-z]+)$/.exec(req.url)?.[1];
    if (req.method === 'POST' && command && Object.hasOwn(commands, command)) {
      try { await commands[command](); signals.push(command); res.writeHead(204).end(); }
      catch (error) { console.error('Opening fixture:', error.message); res.writeHead(500).end(); }
      return;
    }
    const hold = holdRead && req.method === 'GET' && req.url.startsWith(path + '/teacher-allocation?');
    if (hold) holdRead = false;
    const upstream = request(origin + req.url, {method: req.method, headers: req.headers}, response => {
      if (!hold) { res.writeHead(response.statusCode, response.headers); response.pipe(res); return; }
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => { releaseRead = () => {
        if (!res.destroyed) res.writeHead(response.statusCode, response.headers).end(Buffer.concat(chunks));
      }; });
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  const priorWeb = process.env.FRONTEND_URL;
  delete process.env.FRONTEND_URL;
  try {
    const files = join(directory, 'school-opening-desktop.json');
    await writeFile(files, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, pin, otherId: other.id, termId: term.id, gradeId: grade.id, sevenId: seven.id, eightId: eight.id,
      oldEightId: formerEight.id, physicsId: physics.id, chemistryId: chemistry.id, groupId: group.id,
      teacherId: teacher.id, sameNameId: sameName.id, frontId: front.id, rearId: rear.id, isolated: true}), {mode: 0o600});
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], realtime: true, afterStudent: async (edge, webOrigin) => {
      browser = edge;
      site = webOrigin;
      await runDesktop(['--live-school-opening', files, evidence]);
    }});
    assert.deepEqual(signals, ['staffed', 'connected', 'hold', 'held', 'release']);
    assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: formerEight.id}}), 0);
    await writeFile(join(evidence, 'school-opening-cross-end.json'), JSON.stringify({passed: true, signals, browserProof,
      screens: connectionProof, classId: eight.id, oldClassId: formerEight.id, termId: term.id}, null, 2));
  } finally {
    if (priorWeb === undefined) delete process.env.FRONTEND_URL;
    else process.env.FRONTEND_URL = priorWeb;
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
