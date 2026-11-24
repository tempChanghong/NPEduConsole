import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash, randomBytes} from 'node:crypto';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';
import {prepareTermOperation, publishSiteRelease, verifySiteDestinations} from './site-retirement-fixture.mjs';

export async function verifyReleasedOverviewRetirement({prisma, origin, fixture, directory, evidence, web, zip, manifestPath, siteCutover = false}) {
  const {school, term, classes: [classId], managerId, pin} = fixture;
  await mkdir(evidence, {recursive: true});
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const archiveBytes = await readFile(zip);
  const digest = bytes => createHash('sha256').update(bytes).digest('hex');
  assert.equal(manifest.version, siteCutover ? '0.1.14' : '0.1.13');
  assert.equal(manifest.sha256, digest(archiveBytes));
  assert.equal(manifest.sizeBytes, archiveBytes.length);

  const checkedStatus = async () => {
    const response = await fetch(origin + '/api/v2/setup/status');
    assert.equal(response.status, 200);
    return (await response.json()).data;
  };
  assert.equal((await checkedStatus()).supportsDesktopManagementOverview, true);
  // The runner owns this disposable database and gate file.
  const gateFile = process.env.NPEP_DEPLOYMENT_FILE;
  const originalGate = await readFile(gateFile, 'utf8');
  try {
    await writeFile(gateFile, JSON.stringify({...JSON.parse(originalGate), enabled: false}));
    const disabled = await checkedStatus();
    assert.equal(disabled.supportsDesktopManagementOverview, true);
    assert.equal(disabled.supportsDesktopDeviceConnections, false);
    await prisma.$executeRaw`ALTER TABLE "ClassroomScreenBinding" RENAME COLUMN "heartbeatCredentialVersion" TO "missingHeartbeatVersion"`;
    try {
      assert.equal((await checkedStatus()).supportsDesktopManagementOverview, false);
    } finally {
      await prisma.$executeRaw`ALTER TABLE "ClassroomScreenBinding" RENAME COLUMN "missingHeartbeatVersion" TO "heartbeatCredentialVersion"`;
    }
  } finally {
    await writeFile(gateFile, originalGate);
  }
  assert.equal((await checkedStatus()).supportsDesktopManagementOverview, true);
  console.log('PASS: real setup status requires the overview schema and stays available when the NPEP gate is closed.');

  const originalClass = await prisma.workspace.findUniqueOrThrow({where: {id: classId}});
  const archive = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2025,
    semester: 2, status: 'ARCHIVED', name: '同码班的原归档学期'}});
  const historicalGrade = await prisma.grade.create({data: {termId: archive.id, name: '高二', code: 'G2'}});
  const historicalClass = await prisma.workspace.create({data: {termId: archive.id, gradeId: historicalGrade.id,
    type: 'ADMIN_CLASS', code: originalClass.code, name: originalClass.name}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'PHY', name: '物理'}});
  const group = await prisma.workspace.create({data: {termId: term.id, gradeId: originalClass.gradeId,
    type: 'COURSE_GROUP', subjectId: subject.id, code: 'A1', name: '物理 A1'}});
  await prisma.teachingAssignment.createMany({data: [classId, group.id].map(workspaceId =>
    ({workspaceId, subjectId: subject.id, accountId: managerId}))});
  const online = await prisma.classroomScreenBinding.findFirstOrThrow({where: {administrativeClassId: classId}});
  const seen = new Date(Date.now() - 1000);
  await prisma.classroomScreenBinding.update({where: {id: online.id}, data: {
    loginCode: 'overview-front', activatedAt: seen, lastHeartbeatAt: seen, webLoginCredentialVersion: online.credentialVersion,
    heartbeatBindingRevision: online.npepBindingRevision, heartbeatCredentialVersion: online.credentialVersion,
    runtimeStatus: {online: true, realtimeConnected: true, pendingUploads: 0, route: '/screen'},
  }});
  await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: classId,
    name: '等待首次网页登录后屏', loginCode: 'overview-rear', createdByAccountId: managerId,
    tokenHash: randomBytes(32).toString('hex')}});

  const login = await fetch(origin + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({schoolCode: school.code, username: 'admin', password: pin})});
  assert.equal(login.status, 200);
  const token = (await login.json()).data.access_token;
  async function overview(suffix) {
    const response = await fetch(origin + `/api/v2/admin/schools/${school.id}/management-overview${suffix}`,
      {headers: {Authorization: `Bearer ${token}`}});
    assert.equal(response.status, 200);
    return (await response.json()).data;
  }
  const metricsFrom = ({courseGroups, teachers, onlineScreens, browserScreens, errors, warnings}) =>
    ({courseGroups, teachers, onlineScreens, browserScreens, errors, warnings});
  const active = await overview('/current?screenDuty=1&termPreparation=1');
  const scoped = await overview(`?desktop=1&termId=${archive.id}`);
  assert.deepEqual(active.metrics, metricsFrom((await overview('')).summary));
  assert.deepEqual(scoped.metrics, metricsFrom((await overview(`?termId=${archive.id}`)).summary));
  assert.deepEqual([active.metrics.courseGroups, active.metrics.teachers, active.metrics.onlineScreens, active.metrics.browserScreens], [1, 1, 1, 2]);
  assert.ok(active.tasks.some(task => task.code === 'SCREEN_WEB_ACCOUNT_REQUIRED'));
  assert.equal(scoped.term.id, archive.id);
  assert.equal(scoped.activeTerm.id, term.id);
  assert.ok(scoped.tasks.some(task => task.classId === historicalClass.id));

  let browserDigest;
  let pendingOperation;
  const legacyReads = [];
  if (siteCutover) {
    pendingOperation = await prepareTermOperation({prisma, origin, school, token});
    delete process.env.VITE_ADMIN_LEGACY_MANAGEMENT_ENABLED;
    delete process.env.VITE_ADMIN_MANAGEMENT_HANDOFF_PILOT;
    delete process.env.VITE_ADMIN_RETIRED_SECTIONS;
  }
  await verifyStudentBrowser({origin, directory, web, evidence, school, rooms: [], afterStudent: async (browser, site) => {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: siteCutover ? 'allow' : 'block'});
    const downloaded = join(directory, manifest.fileName);
    const input = join(directory, 'overview-retirement-windows.json');
    try {
      if (siteCutover) {
        await publishSiteRelease({directory, site, zip, manifestPath, manifest});
      } else {
        await page.route(`${site}/.well-known/npclassworks-deployment.json`, route => route.fulfill({json: {
          schemaVersion: 1, adminLegacyManagementEnabled: true, apiBaseUrl: null, adminManagementHandoffPilot: true, adminRetiredSections: ['overview'],
        }}));
        await page.route(`${site}/downloads/npeduconsole/**`, route => route.request().url().endsWith('/console-release.json')
          ? route.fulfill({json: manifest})
          : route.fulfill({contentType: 'application/zip', headers: {'Content-Length': String(archiveBytes.length)},
            body: route.request().method() === 'HEAD' ? undefined : archiveBytes}));
      }
      const observeRetiredOverview = request => {
        const path = new URL(request.url()).pathname;
        if (path.endsWith('/management-overview')) legacyReads.push(path);
      };
      page.on('request', observeRetiredOverview);
      if (siteCutover) {
        await page.goto(site + '/classworks-admin');
        const entry = page.getByRole('region', {name: '学校管理办理去向'});
        await entry.getByRole('button', {name: '获取 Windows 管理端'}).waitFor();
        assert.equal(await entry.getByText('查看原书签对象', {exact: true}).count(), 0);
        await page.screenshot({path: join(evidence, 'edge-fresh-upgrade-wide.png'), animations: 'disabled'});
        const parameters = new URLSearchParams({section: 'screens', school: school.id, term: term.id,
          screen: online.id, classId, extra: 'ignored'});
        await page.goto(site + '/classworks-admin?' + parameters);
        await entry.getByText('查看原书签对象', {exact: true}).click();
        await entry.getByText('大屏绑定编号：' + online.id, {exact: true}).waitFor();
        await entry.getByText('班级编号：' + classId, {exact: true}).waitFor();
        const recovery = new URL(await entry.getByRole('link', {name: '管理员登录与 OWNER 恢复'}).getAttribute('href'), site);
        parameters.delete('extra');
        assert.deepEqual(Object.fromEntries(recovery.searchParams), {...Object.fromEntries(parameters), access: '1'});
      }
      await page.goto(`${site}/classworks-admin?section=overview&school=${school.id}&term=${archive.id}`);
      if (siteCutover) {
        const username = page.getByLabel('管理员短账号', {exact: true});
        await username.or(page.getByRole('region', {name: '学校管理办理去向'})).first().waitFor();
        await page.screenshot({path: join(evidence, 'edge-default-bookmark.png'), animations: 'disabled'});
        assert.equal(await username.count(), 0, 'An ordinary pre-upgrade bookmark must open guidance, without mounting the web admin login.');
        await page.getByRole('link', {name: /管理员登录与 OWNER 恢复/}).click();
      }
      await page.getByLabel('管理员短账号', {exact: true}).fill('admin');
      await page.getByLabel('个人 PIN', {exact: true}).fill(pin);
      await page.getByRole('button', {name: '登录', exact: true}).click();
      if (siteCutover) {
        await page.getByLabel('管理员短账号', {exact: true}).waitFor({state: 'hidden'});
        await page.goto(`${site}/classworks-admin?section=overview&school=${school.id}&term=${archive.id}`);
      }
      const guide = page.getByRole('region', {name: '学校管理办理去向'});
      if (siteCutover) {
        await guide.getByRole('heading', {name: '学校管理', exact: true}).waitFor();
        await guide.getByText('原学期编号：' + archive.id, {exact: true}).waitFor();
      } else {
        await guide.getByRole('heading', {name: '网页版已停止办理管理总览'}).waitFor();
        await guide.getByText(`原学期：${archive.name}（编号 ${archive.id}）`, {exact: true}).waitFor();
      }
      await page.screenshot({path: join(evidence, 'edge-original-archive-wide.png'), animations: 'disabled'});
      await page.setViewportSize({width: 390, height: 900});
      await guide.getByRole('link', {name: '浏览器故障诊断'}).waitFor();
      await page.screenshot({path: join(evidence, 'edge-original-archive-narrow.png'), animations: 'disabled'});
      const download = page.waitForEvent('download');
      await guide.getByRole('button', {name: '获取 Windows 管理端'}).click();
      await (await download).saveAs(downloaded);
      browserDigest = digest(await readFile(downloaded));
      assert.equal(browserDigest, manifest.sha256);
      assert.deepEqual(legacyReads, []);
      page.off('request', observeRetiredOverview);
      if (siteCutover) {
        await verifySiteDestinations({page, site, directory, evidence, school, archive, managerId, operation: pendingOperation, manifest});
      }

      await writeFile(input, JSON.stringify({isolated: true, login: {url: origin, website: site, code: school.code, username: 'admin', pin},
        overviewRetirement: {activeName: term.name, archiveName: archive.name, active: active.metrics, archive: scoped.metrics}}));
      const child = spawn('pwsh.exe', ['-NoProfile', '-STA', '-File', fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url)),
        '-ManagementLogin', '-FixturePath', input, '-ZipPath', downloaded, '-ManifestPath', manifestPath, '-EvidenceDirectory', evidence],
      {windowsHide: true, stdio: 'inherit'});
      await new Promise((resolve, reject) => {
        child.once('error', reject);
        child.once('exit', code => code === 0 ? resolve() : reject(new Error('Published overview exited ' + code)));
      });
    } catch (error) {
      await page.screenshot({path: join(evidence, 'edge-failure.png')});
      throw error;
    } finally {
      await page.close();
    }
  }});
  await writeFile(join(evidence, 'overview-retirement-result.json'), JSON.stringify({
    schoolId: school.id, activeTermId: term.id, archivedTermId: archive.id, activeClassId: classId,
    archivedClassId: historicalClass.id, active: active.metrics, scoped: scoped.metrics,
    activeDesktopTasks: active.tasks.length, schemaMissing: false, npepDisabled: true,
    browserLegacyOverviewReads: legacyReads, release: manifest, browserDigest,
  }, null, 2));
  console.log('PASS: real KV/PG scope and metrics, exact retired booklet, verified Edge download and released EXE.');
}
