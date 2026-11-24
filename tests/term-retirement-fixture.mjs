import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {mkdir, readFile, rm, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyReleasedTermRetirement({prisma, origin, fixture, fromKv, bcrypt, directory, web, evidence, zip, manifestPath}) {
  const {school, other, term, managerId, classes: [classId], pin} = fixture;
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const zipBytes = await readFile(zip);
  assert.equal(manifest.version, '0.1.8');
  assert.equal(manifest.sha256, createHash('sha256').update(zipBytes).digest('hex'));
  assert.equal(manifest.sizeBytes, zipBytes.length);
  await mkdir(evidence, {recursive: true});
  const {localProviderId} = await fromKv('domain/localAccount.js');
  const blankOwner = 'blank-owner';
  const account = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(other.code, blankOwner),
    localUsername: blankOwner, name: '零学期所有者', localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: other.code}}});
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: account.id, role: 'OWNER'}});
  const original = await prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED', name: '指定归档原学期'}});
  const bindings = await prisma.classroomScreenBinding.findMany({where: {schoolId: school.id}, orderBy: {id: 'asc'}});
  assert.equal(bindings.length, 2);
  for (const [index, binding] of bindings.entries()) await prisma.classroomScreenBinding.update({where: {id: binding.id}, data: {
    name: index ? '同班后屏' : '同班前屏', administrativeClassId: classId}});
  assert.equal(await prisma.academicTerm.count({where: {schoolId: other.id}}), 0);
  const path = `/api/v2/admin/schools/${school.id}/term-preparation`;
  const postIds = [], receiptReads = [];
  let drop, compatibility = true;
  const relay = createServer(async (request, response) => {
    try {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const headers = {...request.headers};
      delete headers.host;
      delete headers.connection;
      delete headers['transfer-encoding'];
      const target = new URL(request.url, origin);
      if (compatibility && target.pathname.endsWith('/management-overview/current')) target.searchParams.delete('termPreparation');
      const upstream = await fetch(target, {method: request.method, headers, redirect: 'manual', body: chunks.length ? Buffer.concat(chunks) : undefined});
      let bytes = Buffer.from(await upstream.arrayBuffer());
      if (compatibility && target.pathname === '/api/v2/setup/status' && upstream.ok) {
        const data = JSON.parse(bytes);
        delete data.data.supportsDesktopTermManagement;
        bytes = Buffer.from(JSON.stringify(data));
      }
      if (request.method === 'POST' && target.pathname === `${path}/terms/${term.id}/status`) {
        postIds.push(JSON.parse(Buffer.concat(chunks)).requestId);
        const result = JSON.parse(bytes).data;
        if (!drop && result?.state === 'APPLIED' && result.receipt.operation === 'ACTIVATE') {
          drop = result.receipt;
          response.destroy();
          return;
        }
      }
      if (target.pathname.startsWith(`${path}/status-receipts/`)) receiptReads.push(target.pathname.split('/').at(-1));
      const replyHeaders = Object.fromEntries(upstream.headers);
      delete replyHeaders['transfer-encoding'];
      delete replyHeaders['content-encoding'];
      response.writeHead(upstream.status, {...replyHeaders, 'content-length': bytes.length}).end(bytes);
    } catch (error) {
      console.error(error);
      response.writeHead(502).end();
    }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  const api = `http://127.0.0.1:${relay.address().port}`;
  const legacyReads = [];
  const blankName = '零学期首份草稿';
  async function released(schoolCode, username, stage, name) {
    const access = join(directory, `term-retirement-${stage}.json`);
    const output = join(evidence, stage);
    await writeFile(access, JSON.stringify({isolated: true, login: {url: api, website: api, code: schoolCode, username, pin},
      termRetirement: {stage, name}}));
    try {
      await new Promise((done, reject) => {
        const child = spawn('pwsh.exe', ['-NoProfile', '-STA', '-File', fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url)),
          '-ManagementLogin', '-FixturePath', access, '-ZipPath', zip, '-ManifestPath', manifestPath, '-EvidenceDirectory', output],
        {windowsHide: true, stdio: 'inherit'});
        child.once('error', reject);
        child.once('exit', code => code === 0 ? done() : reject(new Error(`Released term ${stage} check failed (${code})`)));
      });
    } finally { await rm(access, {force: true}); }
  }
  try {
    const loggedIn = await fetch(api + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
    assert.equal(loggedIn.status, 200);
    const auth = (await loggedIn.json()).data.access_token;
    const legacyOverview = await fetch(api + `/api/v2/admin/schools/${school.id}/management-overview/current?termPreparation=1`,
      {headers: {Authorization: 'Bearer ' + auth}});
    const legacyTask = (await legacyOverview.json()).data.tasks.find(row => row.code === 'NO_ACTIVE_TERM');
    assert.equal(legacyTask.action, 'OPEN_WEB');
    assert.equal(legacyTask.targetTab, 'terms');
    await verifyStudentBrowser({origin: api, web, directory, evidence, school, rooms: [], afterStudent: async (browser, site) => {
      const page = await browser.newPage({viewport: {width: 1440, height: 900}, serviceWorkers: 'block'});
      try {
        await page.addInitScript(token => localStorage.setItem('classworks-v2-access-token', token), auth);
        await page.route(`${site}/.well-known/npclassworks-deployment.json`, route =>
          route.fulfill({json: {schemaVersion: 1, apiBaseUrl: site, adminRetiredSections: ['terms']}}));
        await page.route(`${site}/downloads/npeduconsole/**`, route => route.request().url().endsWith('/console-release.json') ?
          route.fulfill({json: manifest}) : route.fulfill({contentType: 'application/zip', headers: {'Content-Length': String(manifest.sizeBytes)}}));
        page.on('request', request => {
          if (request.url().includes('/transition/preview')) legacyReads.push(new URL(request.url()).pathname);
        });
        await page.goto(`${site}/classworks-admin?school=${school.id}&section=${legacyTask.targetTab}&term=${original.id}`);
        await page.getByRole('button', {name: '预检迁移内容'}).click();
        await page.getByText(/将复制 1 个年级/).waitFor();
        assert.deepEqual(legacyReads, [`/api/v2/admin/terms/${original.id}/transition/preview`]);
        await page.screenshot({path: join(evidence, 'edge-legacy-archived-source.png')});
        compatibility = false;
        await page.reload();
        await page.getByRole('heading', {name: '网页版已停止办理学期运维'}).waitFor();
        await page.getByText(`原学期：${original.name}（编号 ${original.id}）`).waitFor();
        await page.getByRole('button', {name: /获取 Windows 管理端/i}).waitFor();
        await page.screenshot({path: join(evidence, 'edge-retired-archived-link.png')});
        await page.setViewportSize({width: 390, height: 920});
        await page.locator('.admin-mobile-page-switcher .v-field').click();
        await page.getByRole('option', {name: /审计记录/}).waitFor();
        assert.equal(await page.getByRole('option', {name: '运维与安全 · 学期运维', exact: true}).count(), 0);
        await page.keyboard.press('Escape');
        await page.screenshot({path: join(evidence, 'edge-retired-archived-narrow.png'), animations: 'disabled'});
        assert.equal(legacyReads.length, 1);
      } finally { await page.close(); }
      await released(other.code, blankOwner, 'blank', blankName);
      await released(school.code, 'admin', 'activate', original.name);
    }});
    const blank = await prisma.academicTerm.findFirstOrThrow({where: {schoolId: other.id, name: blankName}});
    assert.equal(blank.status, 'DRAFT');
    assert.equal(await prisma.academicTerm.count({where: {schoolId: other.id}}), 1);
    assert.equal(await prisma.workspace.count({where: {termId: blank.id}}), 0);
    const after = await prisma.academicTerm.findFirstOrThrow({where: {schoolId: school.id, status: 'ACTIVE'}});
    assert.equal(after.id, original.id);
    assert.ok(drop);
    assert.deepEqual(postIds, [drop.requestId]);
    assert.deepEqual(receiptReads, [drop.requestId]);
    const [{requestId: round}] = await prisma.$queryRaw`SELECT "requestId" FROM "SchoolTermActivation" WHERE "schoolId"=${school.id}`;
    assert.equal(round, drop.requestId);
    assert.equal(await prisma.termActivationReceipt.count({where: {schoolId: school.id, termId: original.id}}), 1);
    const screenRows = await prisma.classroomScreenBinding.findMany({where: {schoolId: school.id}, orderBy: {id: 'asc'}});
    assert.deepEqual(screenRows.map(row => [row.id, row.administrativeClassId]), bindings.map(row => [row.id, classId]));
    assert.deepEqual(drop.screens.map(row => row.bindingId).sort(), bindings.map(row => row.id).sort());
    await writeFile(join(evidence, 'term-retirement-result.json'), JSON.stringify({releaseSha256: manifest.sha256,
      blank: {schoolId: other.id, termId: blank.id, status: blank.status}, archived: {schoolId: school.id, originalTermId: original.id,
        currentTermId: after.id, round, postIds, receiptReads, bindings: screenRows.map(row => row.id)}, legacyTask, legacyReads}, null, 2));
    console.log('PASS: released EXE created the first draft, restored the original term, recovered one round with two bindings, and retained exact old-KV Edge fallback.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
