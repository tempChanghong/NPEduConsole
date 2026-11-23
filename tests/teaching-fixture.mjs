import assert from 'node:assert/strict';
import { createServer, request } from 'node:http';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function verifyCurrentTeaching({prisma, origin, fixture, directory, evidence, web, createTeacher, runDesktop, runDatabase}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, pin, managerId} = fixture;
  const grade = await prisma.grade.create({data: {termId: term.id, name: '高一', code: 'G1'}});
  const makeClass = number => prisma.workspace.create({data: {termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS',
    name: '高一（' + number + '）班', code: 'G1-' + number}});
  const [seven, eight, nine] = await Promise.all([7, 8, 9].map(makeClass));
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'MATH', name: '数学'}});
  await prisma.administrativeClassSubject.createMany({data: [seven, eight, nine].map(room =>
    ({administrativeClassId: room.id, subjectId: subject.id, deliveryMode: 'ADMIN_CLASS'}))});
  const historySubject = await prisma.subject.create({data: {schoolId: school.id, code: 'HISTORY', name: '历史'}});
  const historyRooms = await Promise.all(Array.from({length: 23}, (_, index) => makeClass(index + 10)));
  await prisma.administrativeClassSubject.createMany({data: [seven, ...historyRooms].map(room =>
    ({administrativeClassId: room.id, subjectId: historySubject.id, deliveryMode: 'ADMIN_CLASS'}))});
  const candidate = await createTeacher('mathteacher', '陈老师');
  await createTeacher('duplicateteacher', '陈老师');
  const primary = await createTeacher('originalteacher', '王老师');
  const admin2 = await createTeacher('teachingadmin2', '第二管理员', 'ADMIN');
  const otherManageable = await prisma.school.create({data: {name: '另一个可管理学校', code: 'OTHER-MANAGED'}});
  const next = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2026, semester: 2,
    name: '下一学期', status: 'DRAFT'}});
  const nextGrade = await prisma.grade.create({data: {termId: next.id, name: grade.name, code: grade.code}});
  const nextEight = await prisma.workspace.create({data: {termId: next.id, gradeId: nextGrade.id,
    name: eight.name, code: eight.code, type: 'ADMIN_CLASS'}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: nextEight.id, subjectId: subject.id,
    deliveryMode: 'ADMIN_CLASS'}});

  async function api(path, {method = 'GET', token, body, status = 200} = {}) {
    const headers = {'Content-Type': 'application/json'};
    if (token) headers.Authorization = 'Bearer ' + token;
    const response = await fetch(origin + path, {method, headers, body: body && JSON.stringify(body)});
    const result = await response.json();
    assert.equal(response.status, status, path + ': ' + result.code);
    return status >= 400 ? result : result.data;
  }
  const login = username => api('/accounts/local/login', {method: 'POST', body: {schoolCode: school.code, username, password: pin}});
  const admin = await login('admin');
  await prisma.schoolMember.create({data: {schoolId: otherManageable.id, accountId: managerId, role: 'ADMIN'}});
  const token = admin.access_token;
  const teacherToken = (await login('mathteacher')).access_token;
  const secondToken = (await login(admin2.localUsername)).access_token;
  const path = '/api/v2/admin/schools/' + school.id;
  const overview = () => api(path + '/management-overview/current', {token});
  const assignmentPath = id => path + '/current-term/administrative-classes/' + id + '/teaching-assignments';
  const original = await api(path + '/teaching-assignments', {method: 'PUT', token,
    body: {workspaceId: nine.id, subjectId: subject.id, accountId: primary.id, position: 'PRIMARY'}});
  const missing = async () => (await overview()).tasks.filter(item => item.action === 'OPEN_TEACHING' && item.subjectId === subject.id)
    .map(item => item.classId).sort();
  assert.deepEqual(await missing(), [seven.id, eight.id].sort());
  const teacherAccess = async () => await api('/api/v2/me/workspaces', {token: teacherToken});
  assert.ok(!(await teacherAccess()).some(row => row.workspace.id === eight.id));

  const dest = new URL(origin);
  let swallowed = false;
  let commits = 0;
  let readbackFailed = false;
  let changedByOtherAdmin = false;
  let releaseSavedReply;
  let releaseRecovery;
  let delayedRecovery = false;
  let holdClassRead = false;
  let releaseClassRead;
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && ['/__teaching/next-term', '/__teaching/original-term'].includes(req.url)) {
      const activatingNext = req.url.endsWith('/next-term');
      try {
        await prisma.$transaction([
          prisma.academicTerm.update({where: {id: activatingNext ? term.id : next.id}, data: {status: 'ARCHIVED'}}),
          prisma.academicTerm.update({where: {id: activatingNext ? next.id : term.id}, data: {status: 'ACTIVE'}}),
        ]);
        res.writeHead(204).end();
      } catch { res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'POST' && ['/__teaching/drop-saved-reply', '/__teaching/release-recovery'].includes(req.url)) {
      const release = req.url.endsWith('drop-saved-reply') ? releaseSavedReply : releaseRecovery;
      if (!release) { res.writeHead(409).end(); return; }
      release();
      res.writeHead(204).end();
      return;
    }
    if (req.url === '/__teaching/relay-state') {
      res.writeHead(200).end(JSON.stringify({saved: !!releaseSavedReply, reading: !!releaseRecovery}));
      return;
    }
    if (req.method === 'POST' && req.url.startsWith('/__teaching/') && req.url.endsWith('-class-read')) {
      try {
        if (req.url === '/__teaching/hold-class-read') holdClassRead = true;
        else if (req.url === '/__teaching/await-class-read') {
          const until = Date.now() + 4000;
          while (!releaseClassRead && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 30));
          assert.ok(releaseClassRead);
        } else if (req.url === '/__teaching/release-class-read') {
          assert.ok(releaseClassRead);
          releaseClassRead();
          releaseClassRead = null;
        }
        res.writeHead(204).end();
      } catch { res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'POST' && req.url === '/__teaching/change-history') {
      try {
        await api(path + '/teaching-assignments', {method: 'PUT', token: secondToken,
          body: {workspaceId: historyRooms.at(-1).id, subjectId: historySubject.id, accountId: primary.id, position: 'CO_TEACHER'}});
        changedByOtherAdmin = true;
        res.writeHead(204).end();
      } catch { res.writeHead(500).end(); }
      return;
    }
    if (delayedRecovery && !readbackFailed && req.method === 'POST' && req.url === assignmentPath(seven.id) + '/preview') {
      readbackFailed = true;
      req.resume();
      res.writeHead(502).end();
      return;
    }
    const upstream = request(new URL(req.url, dest), {method: req.method, headers: {...req.headers, host: dest.host}}, reply => {
      if (holdClassRead && req.method === 'GET' && [seven, eight].some(room => req.url.startsWith(assignmentPath(room.id) + '?')) &&
          reply.statusCode === 200) {
        holdClassRead = false;
        const chunks = [];
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => { releaseClassRead = () => { res.writeHead(reply.statusCode, reply.headers); res.end(Buffer.concat(chunks)); }; });
      } else if (req.method === 'POST' && req.url.startsWith(assignmentPath(seven.id)) && reply.statusCode === 200) {
        const data = [];
        reply.on('data', chunk => data.push(chunk));
        reply.on('end', () => {
          const body = Buffer.concat(data);
          const parsed = JSON.parse(body).data;
          console.log('Teaching response:', parsed.state ?? 'preview',
            parsed.rows.map(row => [row.className, row.state, row.version?.length, row.assignments?.length]));
          if (parsed.state === 'APPLIED') {
            commits++;
            if (!swallowed) {
              releaseSavedReply = () => { swallowed = true; res.destroy(); };
              return;
            }
          } else if (swallowed && !delayedRecovery && req.url === assignmentPath(seven.id) + '/preview') {
            delayedRecovery = true;
            releaseRecovery = () => { res.writeHead(reply.statusCode, reply.headers); res.end(body); };
            return;
          }
          res.writeHead(reply.statusCode, reply.headers); res.end(body);
        });
      } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    const access = join(directory, 'desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port,
      schoolId: school.id, schoolCode: school.code, password: pin, sevenId: seven.id, eightId: eight.id, nineId: nine.id,
      subjectId: subject.id, teacherId: candidate.id, historySubjectId: historySubject.id, historyPeerIds: historyRooms.map(room => room.id),
      otherManageableSchoolId: otherManageable.id, nextEightId: nextEight.id, allowTeachingTestWrites: true}), {mode: 0o600});
    await runDesktop(['--live-teaching', access, evidence]);
    assert.equal(commits, 1);
    assert.ok(swallowed && delayedRecovery && readbackFailed && changedByOtherAdmin);
    assert.equal(await prisma.teachingAssignment.count({where: {subjectId: historySubject.id}}), 1,
      "the stale desktop batch wrote no assignments in any history class");
    assert.deepEqual(await missing(), [eight.id]);
    assert.equal((await prisma.teachingAssignment.findUniqueOrThrow({where: {id: original.id}})).position, 'PRIMARY');
    assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: eight.id, subjectId: subject.id}}), 0);
    assert.equal(await prisma.teachingAssignment.count({where: {workspaceId: nextEight.id}}), 0,
      'no draft or stale submission changed the identically named class in the other term');
    assert.equal((await teacherAccess()).find(row => row.workspace.id === eight.id)?.role, 'TEACHER');
    console.log('PASS: teacher account still reads class 8 through real HTTP after removal; only class 8 was reopened as a task.');

    const draft = {termId: term.id, subjectId: subject.id, action: 'ASSIGN', classIds: [eight.id, nine.id],
      accountId: candidate.id, position: 'CO_TEACHER'};
    const preview = (classId, body) => api(assignmentPath(classId) + '/preview', {method: 'POST', token, body});
    const commit = (classId, body, baseline, status = 200) => api(assignmentPath(classId), {method: 'POST', token, status,
      body: {...body, expected: baseline.rows.map(({classId, version}) => ({classId, version}))}});
    const baseline = await preview(eight.id, draft);
    assert.deepEqual(baseline.rows.map(row => row.state), ['ADD', 'ADD']);
    await api(path + '/teaching-assignments', {method: 'PUT', token: secondToken,
      body: {workspaceId: eight.id, subjectId: subject.id, accountId: primary.id, position: 'PRIMARY'}});
    const stale = await commit(eight.id, draft, baseline);
    assert.equal(stale.state, 'STALE');
    assert.deepEqual(stale.rows.map(row => row.classId), [eight.id]);
    assert.equal(await prisma.teachingAssignment.count({where: {accountId: candidate.id, workspaceId: {in: [eight.id, nine.id]}}}), 0);
    assert.equal((await commit(eight.id, draft, await preview(eight.id, draft))).state, 'APPLIED');
    assert.equal(await prisma.teachingAssignment.count({where: {accountId: candidate.id, position: 'CO_TEACHER',
      workspaceId: {in: [eight.id, nine.id]}}}), 2);
    assert.equal((await prisma.teachingAssignment.findUniqueOrThrow({where: {id: original.id}})).position, 'PRIMARY');
    const forbidden = '/api/v2/admin/schools/' + other.id + '/current-term/administrative-classes/' + eight.id + '/teaching-assignments';
    await api(forbidden, {method: 'POST', token, status: 403, body: {...draft,
      expected: baseline.rows.map(({classId, version}) => ({classId, version}))}});
    const sevenAssignment = await prisma.teachingAssignment.findFirstOrThrow({where: {workspaceId: seven.id, accountId: candidate.id}});
    const change = {...draft, action: 'CHANGE', classIds: [seven.id], assignmentId: sevenAssignment.id};
    assert.equal((await commit(seven.id, change, await preview(seven.id, change))).state, 'APPLIED');
    assert.equal((await prisma.teachingAssignment.findUniqueOrThrow({where: {id: sevenAssignment.id}})).position, 'CO_TEACHER');
    assert.equal((await teacherAccess()).find(row => row.workspace.id === seven.id)?.role, 'TEACHER');
    console.log('PASS: concurrent legacy edit has zero writes, co-teaching preserves both primaries, explicit edit keeps class access, and cross-school write is denied.');

    await verifyBrowser({web, origin, school, term, admin, evidence});
    const one = {...draft, classIds: [seven.id]};
    const nextBaseline = await preview(seven.id, one);
    await prisma.$transaction([
      prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}}),
      prisma.academicTerm.update({where: {id: next.id}, data: {status: 'ACTIVE'}}),
    ]);
    assert.equal((await commit(seven.id, one, nextBaseline, 409)).code, 'TEACHING_TERM_CHANGED');
    await api(path + '/teaching-assignments', {method: 'PUT', token,
      body: {workspaceId: nine.id, subjectId: subject.id, accountId: primary.id, position: 'PRIMARY'}});
    console.log('PASS: stale Console term refused while the old webpage interface still edits an explicitly archived class.');
    await runDatabase();
  } finally {
    const closed = new Promise(resolve => relay.close(resolve));
    relay.closeAllConnections();
    await closed;
  }
}

async function verifyBrowser({web, origin, school, term, admin, evidence}) {
  const require = createRequire(join(web, 'package.json'));
  const {chromium} = require('@playwright/test');
  const outDir = join(evidence, 'web-build');
  const viteRoot = dirname(require.resolve('vite/package.json'));
  const {preview} = await import(pathToFileURL(join(viteRoot, 'dist/node/index.js')).href);
  const frontend = await preview({configFile: false, root: web, build: {outDir}, preview: {host: '127.0.0.1', port: 0,
    proxy: {'/api': origin, '/accounts': origin}}});
  const url = 'http://127.0.0.1:' + frontend.httpServer.address().port;
  let browser;
  let page;
  try {
    const response = await fetch(url + '/api/v2/me/schools', {headers: {Authorization: 'Bearer ' + admin.access_token}});
    assert.equal(response.status, 200, 'the browser proxy can reach the authorized KV');
    assert.ok((await response.json()).data.some(row => row.schoolId === school.id));
    await new Promise((resolve, reject) => {
      const build = spawn(process.execPath, ['node_modules/vite/bin/vite.js', 'build', '--logLevel', 'warn', '--outDir', outDir],
        {cwd: web, env: {...process.env, VITE_DEFAULT_KV_SERVER: url, VITE_DEFAULT_AUTH_SERVER: url,
          VITE_SERVER_URL: url, VITE_ENABLE_ANALYTICS: 'false'},
          stdio: 'inherit', windowsHide: true});
      build.once('error', reject);
      build.once('exit', code => code === 0 ? resolve() : reject(new Error('Web build failed')));
    });
    browser = await chromium.launch({channel: 'msedge', headless: true});
    page = await browser.newPage({viewport: {width: 1360, height: 950}, serviceWorkers: 'block'});
    await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
    page.on('requestfailed', req => {
      const failed = new URL(req.url());
      if (/^\/(api|accounts)\//.test(failed.pathname)) console.log('Browser API request failed:', failed.origin + failed.pathname);
    });
    await page.addInitScript(({accessToken, refreshToken}) => {
      window.localStorage.setItem('classworks-v2-access-token', accessToken);
      window.localStorage.setItem('classworks-v2-refresh-token', refreshToken);
    }, {accessToken: admin.access_token, refreshToken: admin.refresh_token});
    await page.goto(url + '/classworks-admin?section=overview&school=' + school.id + '&term=' + term.id);
    const responsibilities = page.locator('.v-expansion-panel').filter({
      has: page.getByRole('button', {name: /岗位职责/}),
    });
    await responsibilities.getByRole('button', {name: /岗位职责/}).click();
    const row = responsibilities.locator('.v-list-item').filter({hasText: '高一尚未设置年级组长'}).first();
    await row.getByRole('button', {name: '去处理'}).click({timeout: 20000});
    await page.waitForURL(next => next.searchParams.get('section') === 'teachers' &&
      next.searchParams.get('school') === school.id && next.searchParams.get('term') === term.id);
    await page.getByText('年级组长、班主任和任课关系分别记录，最终权限按职责叠加').waitFor({state: 'visible'});
    await page.screenshot({path: join(evidence, '08-web-responsibility.png')});
    console.log('PASS: Edge clicked the legacy grade-leader task and opened the original responsibility page for the same school and term.');
  } catch (error) {
    if (page) {
      console.log('Legacy overview rendered:', (await page.locator('body').innerText()).slice(0, 1600));
      await page.screenshot({path: join(evidence, 'web-failure.png')});
    }
    throw error;
  } finally {
    await browser?.close();
    frontend.httpServer.closeAllConnections();
    await frontend.close();
  }
}
