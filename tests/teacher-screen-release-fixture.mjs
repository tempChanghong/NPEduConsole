import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {mkdir, readFile, rm, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyTeacherScreenRelease({prisma, fixture, origin, fromKv, bcrypt, directory, evidence, web, zip, manifestPath}) {
  await mkdir(evidence, {recursive: true});
  const {school, term, pin, managerId, classes: [workspaceId]} = fixture;
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const artifact = await readFile(zip);
  const hash = contents => createHash('sha256').update(contents).digest('hex');
  assert.equal(manifest.version, '0.1.11');
  assert.equal(manifest.sha256, hash(artifact));
  assert.equal(manifest.sizeBytes, artifact.length);
  const {localProviderId} = await fromKv('domain/localAccount.js');
  const teacherUsername = 'release-access';
  const created = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(school.code, teacherUsername),
    localUsername: teacherUsername, name: '发行验收教师', localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: school.code}}});
  await prisma.schoolMember.create({data: {schoolId: school.id, accountId: created.id, role: 'VIEWER'}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, name: '发行验收物理', code: 'RELEASE-PHY'}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: workspaceId, subjectId: subject.id, deliveryMode: 'ADMIN_CLASS'}});
  const teaching = await prisma.teachingAssignment.create({data: {workspaceId, subjectId: subject.id, accountId: created.id, position: 'CO_TEACHER'}});
  const activeClass = await prisma.workspace.findUniqueOrThrow({where: {id: workspaceId}});
  const archive = await prisma.academicTerm.create({data: {schoolId: school.id, name: '发行验收归档学期', academicYear: term.academicYear - 1,
    semester: term.semester, status: 'ARCHIVED'}});
  const oldClass = await prisma.workspace.create({data: {termId: archive.id, code: activeClass.code, name: activeClass.name, type: 'ADMIN_CLASS'}});
  const oldScreen = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: oldClass.id,
    name: '发行验收历史后屏', tokenHash: randomBytes(32).toString('hex'), deviceFingerprint: 'released-archived-browser', createdByAccountId: managerId}});
  const pendingScreen = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: oldClass.id,
    name: '发行验收待安排屏', tokenHash: randomBytes(32).toString('hex'), createdByAccountId: managerId}});
  const activationRequestId = randomUUID();
  const {saveTermOperationReceipt} = await fromKv('services/termActivationReceipts.js');
  await prisma.$transaction(tx => saveTermOperationReceipt(tx, {schoolId: school.id, accountId: managerId, termId: term.id,
    requestId: activationRequestId, digest: 'a'.repeat(64), activated: true, receipt: {operation: 'ACTIVATE', screens: [{
      bindingId: pendingScreen.id, name: pendingScreen.name, rebound: false, fromClass: {id: oldClass.id, name: oldClass.name,
        code: oldClass.code, termId: archive.id, termName: archive.name},
    }]}}));
  const setup = (await (await fetch(origin + '/api/v2/setup/status')).json()).data;
  assert.equal(setup.state, 'COMPLETED');
  assert.equal(setup.supportsDesktopTeacherAllocation, true);
  assert.equal(setup.supportsDesktopScreenDutyHistory, true);
  const retiredReads = [];
  let downloadedDigest;
  await verifyStudentBrowser({origin, directory, web, evidence, school, rooms: [], afterStudent: async (browser, site) => {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    const downloaded = join(directory, manifest.fileName);
    const access = join(directory, 'released-teacher-screen.json');
    try {
      await page.route(`${site}/.well-known/npclassworks-deployment.json`, route => route.fulfill({json: {
        schemaVersion: 1, adminLegacyManagementEnabled: true, apiBaseUrl: null, adminRetiredSections: ['teachers', 'screens']}}));
      await page.route(`${site}/downloads/npeduconsole/**`, route => route.request().url().endsWith('/console-release.json') ?
        route.fulfill({json: manifest}) : route.fulfill({contentType: 'application/zip', headers: {'Content-Length': String(artifact.length)},
          body: route.request().method() === 'HEAD' ? undefined : artifact}));
      page.on('request', req => {
        if (/\/(classroom-screens|teacher-allocation|homework-settings|workspace-memberships)(?:$|[/?])/.test(req.url()))
          retiredReads.push(new URL(req.url()).pathname);
      });
      const visit = query => page.goto(`${site}/classworks-admin?${new URLSearchParams({school: school.id, ...query})}`);
      const bookmark = {section: 'screens', term: archive.id, classId: oldClass.id, screen: oldScreen.id};
      await visit(bookmark);
      await page.getByText(school.name, {exact: true}).waitFor();
      await page.getByLabel('管理员短账号', {exact: true}).fill('admin');
      await page.getByLabel('个人 PIN', {exact: true}).fill(pin);
      await page.getByRole('button', {name: '登录', exact: true}).click();
      await page.getByText(`原大屏绑定编号：${oldScreen.id}`).waitFor();
      await visit({section: 'teachers', term: term.id});
      await page.getByRole('heading', {name: '网页版已停止办理教师分配'}).waitFor();
      await visit(bookmark);
      await page.getByRole('heading', {name: '网页版已停止办理大屏设备'}).waitFor();
      await page.getByText(`原大屏绑定编号：${oldScreen.id}`).waitFor();
      const pendingDownload = page.waitForEvent('download');
      await page.getByRole('button', {name: '获取 Windows 管理端'}).click();
      await (await pendingDownload).saveAs(downloaded);
      downloadedDigest = hash(await readFile(downloaded));
      assert.equal(downloadedDigest, manifest.sha256);
      assert.deepEqual(retiredReads, []);
      await page.screenshot({path: join(evidence, 'edge-published-and-original-binding.png')});
      await writeFile(access, JSON.stringify({isolated: true,
        login: {url: origin, website: site, code: school.code, username: 'admin', pin},
        teacherScreenRetirement: {taskId: `SchoolOverviewTask-TEACHER_ACCESS_MISSING-${workspaceId}-${subject.id}-${created.id}`,
          teacherUsername, bindingId: oldScreen.id, placementBindingId: pendingScreen.id}}), {mode: 0o600});
      await new Promise((resolve, reject) => {
        const child = spawn('pwsh.exe', ['-NoProfile', '-STA', '-File', fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url)),
          '-ManagementLogin', '-FixturePath', access, '-ZipPath', downloaded, '-ManifestPath', manifestPath,
          '-EvidenceDirectory', evidence], {windowsHide: true, stdio: 'inherit'});
        child.once('error', reject);
        child.once('exit', code => code === 0 ? resolve() : reject(Error('Published Console check exited ' + code)));
      });
    } finally {
      await page.close();
      await rm(access, {force: true});
      await rm(downloaded, {force: true});
    }
  }});
  const reloaded = await prisma.classroomScreenBinding.findUniqueOrThrow({where: {id: oldScreen.id}});
  assert.equal(reloaded.administrativeClassId, oldClass.id);
  assert.equal(reloaded.deviceFingerprint, null);
  assert.equal(reloaded.credentialVersion, oldScreen.credentialVersion + 1);
  assert.equal(reloaded.npepBindingRevision, oldScreen.npepBindingRevision);
  assert.equal(await prisma.workspaceMember.count({where: {workspaceId, accountId: created.id, role: 'ASSISTANT'}}), 1);
  assert.equal(await prisma.teachingAssignment.count({where: {id: teaching.id, isActive: true}}), 1);
  await writeFile(join(evidence, 'release-teacher-screen.json'), JSON.stringify({passed: true, version: manifest.version,
    sha256: downloadedDigest, sizeBytes: manifest.sizeBytes, retiredReads, teacher: {accountId: created.id, originalAssignmentId: teaching.id,
      workspaceId, role: 'ASSISTANT'}, screen: {bindingId: oldScreen.id, originalTermId: archive.id, originalClassId: oldClass.id,
      activeSameCodeClassId: activeClass.id, browserVersionBefore: oldScreen.credentialVersion, browserVersionAfter: reloaded.credentialVersion,
      nativeBindingVersion: reloaded.npepBindingRevision}, placement: {bindingId: pendingScreen.id, activationRequestId}}, null, 2));
  console.log('PASS: both retired guides used the live KV, Edge verified and downloaded the published ZIP, and that EXE applied both original targets.');
}
