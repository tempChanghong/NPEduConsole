import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyClassSubjectRules({prisma, origin, fixture, directory, web, evidence, runDesktop}) {
  const {school, term: fall, managerId, pin} = fixture;
  await mkdir(evidence, {recursive: true});
  const spring = await makeTerm(2026, 2, '春季授课草稿', 'DRAFT');
  const archive = await makeTerm(2025, 2, '归档授课学期', 'ARCHIVED');
  const fallGrade = await prisma.grade.findFirstOrThrow({where: {termId: fall.id, code: 'G2'}});
  const [springGrade, archiveGrade] = await Promise.all([spring, archive].map(term =>
    prisma.grade.create({data: {termId: term.id, name: '高二', code: 'G2'}})));
  const [fall8, seven, eight, archive8] = await Promise.all([
    makeRoom(fallGrade, 8), makeRoom(springGrade, 7), makeRoom(springGrade, 8), makeRoom(archiveGrade, 8)]);
  const [physics, chinese, chemistry, history, biology, geography] = await Promise.all([
    ['RULE-PHY', '物理', 'ELECTIVE'], ['RULE-CHN', '语文', 'CORE'], ['RULE-CHEM', '化学', 'ELECTIVE'], ['RULE-HIS', '历史', 'CORE'],
    ['RULE-BIO', '生物', 'ELECTIVE'],
    ['RULE-GEO', '地理', 'ELECTIVE'],
  ].map(([code, name, category]) => prisma.subject.create({data: {schoolId: school.id, code, name, category}})));
  const [a1, a2, inactive] = await Promise.all([
    group('RULE-A1', '物理 A1', true, [seven, eight]),
    group('RULE-A2', '物理 A2', true, [seven]),
    group('RULE-OLD', '物理旧组', false, [seven, eight]),
  ]);
  const biologyGroup = await group('RULE-B1', '生物 B1', true, [eight], biology);
  await Promise.all([
    addRule(fall8, chinese, 'ADMIN_CLASS', true), addRule(archive8, chinese, 'ADMIN_CLASS', false),
    addRule(seven, physics, 'COURSE_GROUP', true), addRule(eight, physics, 'COURSE_GROUP', true),
    addRule(eight, chinese, 'ADMIN_CLASS', false), addRule(eight, history, 'ADMIN_CLASS', true), addRule(eight, biology, 'COURSE_GROUP', true),
  ]);
  await prisma.workspaceMember.create({data: {workspaceId: eight.id, accountId: managerId, role: 'TEACHER'}});
  const teacher = await prisma.teachingAssignment.create({data: {
    workspaceId: eight.id, subjectId: history.id, accountId: managerId, position: 'PRIMARY'}});
  await prisma.workspaceMember.create({data: {workspaceId: a1.id, accountId: managerId, role: 'VIEWER'}});
  const publication = await prisma.publication.create({data: {
    type: 'ASSIGNMENT', title: '物理 A1 原作业', content: '隔离验收', status: 'PUBLISHED',
    subjectId: physics.id, authorAccountId: managerId, targets: {create: {workspaceId: a1.id}},
  }});
  const login = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
  assert.equal(login.status, 200);
  const loginBody = await login.json();
  const access = loginBody.data ?? loginBody;
  const endpoint = '/api/v2/admin/schools/' + school.id + '/administrative-classes/' + eight.id + '/subject-rules';
  let browser, site, savedId, ownReads = 0, applied = 0;
  const cases = [];

  function makeTerm(academicYear, semester, name, status) {
    return prisma.academicTerm.create({data: {schoolId: school.id, academicYear, semester, name, status}});
  }
  function makeRoom(grade, number) {
    return prisma.workspace.create({data: {termId: grade.termId, gradeId: grade.id, code: 'RULE-' + number,
      name: `高二（${number}）班`, type: 'ADMIN_CLASS'}});
  }
  function group(code, name, isActive, rooms, subject = physics) {
    return prisma.workspace.create({data: {termId: spring.id, gradeId: springGrade.id, subjectId: subject.id, code, name,
      isActive, type: 'COURSE_GROUP', isStudentSelectable: true,
      sourceClasses: {create: rooms.map(row => ({administrativeClassId: row.id}))}}});
  }
  function addRule(room, subject, deliveryMode, isCompulsory) {
    return prisma.administrativeClassSubject.create({data: {administrativeClassId: room.id, subjectId: subject.id, deliveryMode, isCompulsory}});
  }
  function rule(room, subject) {
    return prisma.administrativeClassSubject.findUnique({
      where: {administrativeClassId_subjectId: {administrativeClassId: room.id, subjectId: subject.id}}});
  }
  const sources = async group => (await prisma.workspaceSourceClass.findMany({where: {workspaceId: group.id}}))
    .map(row => row.administrativeClassId).sort();

  async function page(action) {
    const window = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    await window.route('**/*', route => new URL(route.request().url()).origin === site ? route.continue() : route.abort());
    try { await action(window); }
    catch (error) {
      await window.screenshot({path: join(evidence, 'edge-failure.png'), animations: 'disabled'}).catch(() => {});
      throw error;
    } finally { await window.close(); }
  }

  async function management(window, term) {
    await window.addInitScript(({accessToken, refreshToken}) => {
      localStorage.setItem('classworks-v2-access-token', accessToken);
      localStorage.setItem('classworks-v2-refresh-token', refreshToken);
    }, {accessToken: access.access_token, refreshToken: access.refresh_token});
    await window.goto(site + '/classworks-admin?section=structure&school=' + school.id + '&term=' + term.id);
  }

  async function legacySwap() {
    await page(async window => {
      await management(window, spring);
      await window.getByRole('tab', {name: '走班教学班与来源'}).click();
      for (const target of [a1, a2]) {
        const panel = window.locator('.v-expansion-panel').filter({hasText: target.code});
        await panel.getByRole('button', {name: new RegExp(target.name)}).click();
        await panel.getByRole('button', {name: '编辑', exact: true}).click();
        const editor = window.getByRole('dialog').filter({hasText: '编辑走班教学班'});
        const sourceField = editor.locator('.v-select').filter({hasText: '来源行政班'});
        if (target.id === a1.id) {
          await sourceField.locator('.v-chip').filter({hasText: eight.code}).locator('.v-chip__close').click();
        } else {
          await sourceField.locator('.v-field__append-inner').click();
          await window.getByRole('option', {name: `${eight.name} · ${eight.code}`, exact: true}).click();
        }
        await editor.getByText('编辑走班教学班', {exact: true}).click();
        await Promise.all([window.waitForResponse(res => res.url().endsWith('/course-groups/' + target.id) &&
          res.request().method() === 'PATCH' && res.ok()), editor.getByRole('button', {name: '保存', exact: true}).click()]);
        await editor.waitFor({state: 'hidden'});
      }
      await window.screenshot({path: join(evidence, 'edge-web-swapped-sources.png'), animations: 'disabled'});
    });
    assert.deepEqual(await sources(a1), [seven.id]);
    assert.deepEqual(await sources(a2), [seven.id, eight.id].sort());
  }

  async function legacySave() {
    await page(async window => {
      await management(window, fall);
      await window.getByRole('tab', {name: '行政班授课规则'}).click();
      const picker = window.locator('.v-select').filter({hasText: /^行政班/}).locator('.v-field');
      await picker.locator('.v-field__append-inner').click();
      await window.getByRole('option', {name: `${fall8.name} · ${fall8.code}`, exact: true}).click();
      const chineseRow = window.locator('[data-subject-id="' + chinese.id + '"]');
      await chineseRow.getByText('语文', {exact: true}).waitFor();
      await chineseRow.getByRole('checkbox', {name: '语文必修'}).uncheck();
      await window.getByRole('button', {name: '预览 1 科调整'}).click();
      await window.getByText(/语文：随行政班 · 必修 → 随行政班 · 选修/).waitFor();
      await window.screenshot({path: join(evidence, 'edge-web-confirmation.png'), animations: 'disabled'});
      await window.getByRole('button', {name: '确认保存整班规则', exact: true}).click();
      await window.getByText(/已确认本账号的整班规则保存回执/).waitFor();
      assert.equal((await rule(fall8, chinese)).isCompulsory, false);
    });
  }

  async function student(term, room, walking) {
    await page(async window => {
      await window.goto(site);
      await window.getByRole('button', {name: /开始选班/}).click();
      const dialog = window.getByRole('dialog').filter({hasText: '选择我的班级'});
      const choose = async (label, option) => {
        await dialog.locator('.v-select').filter({hasText: label}).locator('.v-field').click();
        await window.getByRole('option', {name: option, exact: true}).click();
      };
      const selected = window.waitForResponse(res => res.url().includes('/catalog/terms/current?') && res.ok());
      await choose(/^学校/, school.name);
      assert.equal((await (await selected).json()).data.id, term.id);
      await choose(/^行政班/, room.name);
      if (walking) {
        await dialog.locator('.v-select').filter({hasText: /物理/}).locator('.v-field').click();
        await window.getByRole('option', {name: '物理 A1', exact: true}).waitFor();
        assert.equal(await window.getByRole('option', {name: '物理 A2', exact: true}).count(), 1);
        assert.equal(await window.getByRole('option', {name: '物理旧组', exact: true}).count(), 0);
        await window.getByRole('option', {name: '物理 A2', exact: true}).click();
      } else {
        assert.equal(await dialog.locator('.v-select').filter({hasText: /物理/}).count(), 0);
        if (term.id === spring.id) await choose(/生物/, '生物 B1');
      }
      await window.screenshot({path: join(evidence, `edge-student-${term.id === fall.id ? 'fall' : walking ? 'spring-seven' : 'spring-eight'}.png`),
        animations: 'disabled'});
      await Promise.all([window.waitForResponse(res => res.url().includes('/administrative-classes/' + room.id + '/student-selection/validate') &&
        res.request().method() === 'POST' && res.ok()), dialog.getByRole('button', {name: '保存并查看作业'}).click()]);
      await dialog.waitFor({state: 'hidden'});
    });
  }

  async function checkpoint(name) {
    if (name === 'start') await student(fall, fall8, false);
    else if (name === 'presets') {
      assert.equal((await rule(eight, physics)).isCompulsory, true);
      assert.equal((await rule(eight, chinese)).isCompulsory, false);
      assert.equal(await rule(eight, geography), null);
      assert.equal(await rule(eight, chemistry), null);
      assert.equal(applied, 0);
    }
    else if (name === 'swap') await legacySwap();
    else if (name === 'stale') {
      assert.equal((await rule(eight, physics)).isCompulsory, true);
      assert.equal(await rule(eight, chemistry), null);
      assert.deepEqual(await sources(a2), [seven.id, eight.id].sort());
      assert.equal(applied, 0);
    } else if (name === 'saved') {
      assert.equal(await rule(eight, physics), null);
      assert.equal((await rule(eight, chemistry)).deliveryMode, 'ADMIN_CLASS');
      assert.equal((await rule(eight, chinese)).isCompulsory, false);
      assert.equal((await rule(eight, biology)).isCompulsory, true);
      assert.deepEqual(await sources(biologyGroup), [eight.id]);
      assert.equal((await rule(fall8, chinese)).isCompulsory, true);
      assert.deepEqual(await sources(a2), [seven.id]);
      assert.deepEqual(await sources(inactive), [seven.id]);
      assert.equal((await rule(seven, physics)).isCompulsory, true);
      assert.equal((await prisma.teachingAssignment.findUniqueOrThrow({where: {id: teacher.id}})).isActive, false);
      assert.equal((await prisma.workspaceMember.findUniqueOrThrow({where: {workspaceId_accountId: {workspaceId: eight.id, accountId: managerId}}})).role, 'TEACHER');
      assert.ok(await prisma.publicationTarget.findFirst({where: {publicationId: publication.id, workspaceId: a1.id}}));
      assert.deepEqual([applied, ownReads], [1, 1]);
    } else if (name === 'archive') {
      assert.equal((await rule(archive8, chinese)).isCompulsory, true);
      await legacySave();
    } else if (name === 'active') {
      await student(spring, seven, true);
      await student(spring, eight, false);
    } else throw new Error('Unexpected checkpoint ' + name);
    cases.push(name);
  }

  const relay = createServer(async (req, res) => {
    try {
      const url = new URL(req.url, origin);
      if (url.pathname.startsWith('/fixture/class-rules/')) {
        await checkpoint(url.pathname.split('/').at(-1));
        return res.writeHead(204).end();
      }
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const headers = {...req.headers};
      for (const key of ['host', 'connection', 'transfer-encoding', 'content-length']) delete headers[key];
      const requestBody = chunks.length ? Buffer.concat(chunks) : undefined;
      const upstream = await fetch(url, {method: req.method, headers, body: requestBody, redirect: 'manual'});
      const responseBody = Buffer.from(await upstream.arrayBuffer());
      if (url.pathname === endpoint && req.method === 'PUT' && upstream.ok && JSON.parse(responseBody).data.state === 'APPLIED') {
        applied++;
        if (applied === 1) {
          savedId = JSON.parse(requestBody).requestId;
          return res.writeHead(502).end();
        }
      }
      if (savedId && url.pathname === endpoint + '/receipts/' + savedId && upstream.ok) ownReads++;
      const responseHeaders = Object.fromEntries(upstream.headers);
      for (const key of ['connection', 'transfer-encoding', 'content-encoding', 'content-length']) delete responseHeaders[key];
      res.writeHead(upstream.status, responseHeaders).end(responseBody);
    } catch (error) {
      console.error(error.message);
      res.writeHead(500).end(error.message);
    }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  try {
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (edge, address) => {
      browser = edge;
      site = address;
      const config = join(directory, 'class-rules-desktop.json');
      await writeFile(config, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id, schoolCode: school.code,
        password: pin, springId: spring.id, archiveId: archive.id, physicsId: physics.id, chineseId: chinese.id, chemistryId: chemistry.id,
        historyId: history.id, biologyId: biology.id, geographyId: geography.id, teacherId: teacher.id, eightId: eight.id,
        archiveEightId: archive8.id, fallEightId: fall8.id,
        allowClassRuleWrites: true}), {mode: 0o600});
      await runDesktop(['--live-class-rules', config, evidence]);
    }});
    assert.deepEqual(cases, ['start', 'presets', 'swap', 'stale', 'saved', 'archive', 'active']);
    await writeFile(join(evidence, 'class-rules-result.json'), JSON.stringify({result: 'PASS', cases, applied, ownReads,
      receiptId: savedId, sameNameClassIds: [fall8.id, eight.id, archive8.id], sourceIds: [a1.id, a2.id, inactive.id],
      protectedPublication: publication.id}, null, 2));
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
