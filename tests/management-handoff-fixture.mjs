import assert from 'node:assert/strict';
import {execFile} from 'node:child_process';
import {createHash} from 'node:crypto';
import {copyFile, mkdir, mkdtemp, readFile, rm, rmdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {promisify} from 'node:util';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

const run = promisify(execFile);
export async function verifyManagementHandoff({prisma, origin, directory, fixture, web, evidence, releaseDirectory, teacherBatch = false}) {
  const {school, pin, managerId} = fixture;
  const proof = {where: {accountId: managerId, schoolId: school.id, method: 'LOCAL_PIN'}};
  const release = JSON.parse(await readFile(join(releaseDirectory, 'console-release.json'), 'utf8'));
  assert.match(release.version, /^\d+\.\d+\.\d+$/);
  assert.equal(release.fileName, `NPEduConsole-${release.version}-win-x64.zip`);
  for (const separateApi of [false, true]) {
    const output = join(evidence, separateApi ? 'split' : 'same');
    await mkdir(output, {recursive: true});
    await verifyStudentBrowser({origin, web, directory, evidence: output, rooms: [], separateApi,
      afterStudent: async (browser, website) => {
        const api = separateApi ? origin : website;
        const build = join(directory, 'walking-web-build');
        const declarationFile = join(build, '.well-known', 'npclassworks-deployment.json');
        const declaration = JSON.parse(await readFile(declarationFile, 'utf8'));
        assert.equal(declaration.apiBaseUrl, api);
        await writeFile(declarationFile, JSON.stringify({...declaration, adminManagementHandoffPilot: true}));
        const downloads = join(build, 'downloads', 'npeduconsole');
        await mkdir(downloads, {recursive: true});
        for (const file of [release.fileName, 'console-release.json'])
          await copyFile(join(releaseDirectory, file), join(downloads, file));
        const priorOrigins = process.env.CORS_ALLOWED_ORIGINS;
        process.env.CORS_ALLOWED_ORIGINS = website;
        const page = await browser.newPage({viewport: {width: separateApi ? 390 : 1440, height: 900},
          acceptDownloads: true, serviceWorkers: 'block'});
        const input = join(directory, 'management-login.json');
        let scratch;
        try {
          await page.route('**/*', route => [website, api].includes(new URL(route.request().url()).origin) ? route.continue() : route.abort());
          await page.goto(website + '/?oobe=1');
          await Promise.all([
            page.waitForResponse(res => res.url() === api + '/api/v2/catalog/schools' && res.ok()),
            page.getByRole('button', {name: /学校管理员/}).click()
          ]);
          await page.locator('.management-entry__context .v-select .v-field').click();
          await page.getByRole('option', {name: `${school.name}（${school.code}）`, exact: true}).click();
          const code = await page.locator('.management-entry__context strong').innerText();
          const apiFromGuide = await page.locator('dd code').first().innerText();
          assert.equal(apiFromGuide, api);
          assert.equal(code, school.code);
          if (separateApi) assert.equal(await page.locator('dd code').nth(1).innerText(), website);
          await page.screenshot({path: join(output, 'edge-guide.png'), animations: 'disabled'});
          const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', {name: '获取 Windows 管理端'}).click()]);
          assert.equal(download.suggestedFilename(), release.fileName);
          const saved = join(directory, download.suggestedFilename());
          await download.saveAs(saved);
          const digest = createHash('sha256').update(await readFile(saved)).digest('hex');
          assert.equal(digest, release.sha256);
          const loginCount = await prisma.schoolManagementLogin.count(proof);
          let batch;
          if (teacherBatch) {
            scratch = await mkdtemp(join(output, 'temporary-delivery-'));
            const suffix = separateApi ? 'split' : 'same';
            batch = {input: join(scratch, 'teachers.json'), output: join(scratch, 'delivered.csv'),
              first: 'issued-a-' + suffix, second: 'issued-b-' + suffix,
              firstName: separateApi ? '陈,"老师"' : '顾,"老师"', secondName: separateApi ? '李老师' : '林老师'};
            await writeFile(batch.input, JSON.stringify({schoolId: school.id, termId: fixture.term.id, assignments: [
              {username: batch.first, name: batch.firstName, pin: '003047', role: 'VIEWER', workspaceCodes: ['G2-1']},
              {username: batch.second, name: batch.secondName, pin: '002209', role: 'VIEWER', workspaceCodes: ['G2-1']},
              {username: 'viewer', name: 'viewer', role: 'VIEWER', workspaceCodes: ['G2-1']}
            ]}));
          }
          await writeFile(input, JSON.stringify({isolated: true, login: {url: apiFromGuide, website, code, username: 'admin', pin}, batch}));
          await run('pwsh.exe', ['-NoProfile', '-STA', '-File', fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url)),
            '-ManagementLogin', '-FixturePath', input, '-ZipPath', saved, '-ManifestPath', join(downloads, 'console-release.json'),
            '-EvidenceDirectory', output], {windowsHide: true});
          if (batch) {
            const outcome = JSON.parse(await readFile(join(output, 'batch-result.json'), 'utf8'));
            assert.deepEqual(outcome.users.sort(), [batch.first, batch.second]);
            assert.equal(outcome.pinLogins, 2);
            assert.equal(outcome.originalViewerLogin, true);
          }
          assert.equal(await prisma.schoolManagementLogin.count(proof), loginCount + 1);
          await page.getByRole('link', {name: '继续使用网页版'}).click();
          await page.waitForURL(u => u.pathname === '/classworks-admin' && u.searchParams.get('school') === school.id);
          await page.getByRole('button', {name: /忘记 OWNER PIN/}).waitFor();
          if (batch) await verifyWebTeacherFallback(page, {website, api, code, pin, schoolId: school.id,
            termId: fixture.term.id, username: 'web-' + batch.first});
          await writeFile(join(output, 'handoff-result.json'), JSON.stringify({website, api, code, schoolId: school.id, sha256: digest,
            realPinLogin: true, continuedWebSchool: new URL(page.url()).searchParams.get('school')}, null, 2));
        } catch (error) {
          await page.screenshot({path: join(output, 'failed-step.png'), animations: 'disabled'});
          throw error;
        } finally {
          if (scratch) {
            for (const file of ['teachers.json', 'delivered.csv']) await rm(join(scratch, file), {force: true});
            await rmdir(scratch);
          }
          await rm(input, {force: true});
          await page.close();
          if (priorOrigins === undefined) delete process.env.CORS_ALLOWED_ORIGINS;
          else process.env.CORS_ALLOWED_ORIGINS = priorOrigins;
        }
      }});
  }
}

async function verifyWebTeacherFallback(page, {website, api, code, pin, schoolId, termId, username}) {
  await page.getByLabel('管理员短账号', {exact: true}).fill('admin');
  await page.getByLabel('个人 PIN', {exact: true}).fill(pin);
  await Promise.all([page.waitForResponse(res => res.url().includes('/api/v2/me/schools') && res.ok()),
    page.getByRole('button', {name: '登录', exact: true}).click()]);
  await page.goto(website + '/classworks-admin?section=teachers&school=' + schoolId + '&term=' + termId);
  await page.getByLabel('批量 JSON 模式').check();
  await page.getByLabel('教师分配 JSON').fill(JSON.stringify({assignments: [
    {name: '兼容教师', username, pin: '000517', role: 'TEACHER', workspaceCodes: ['G2-1']}
  ]}));
  const section = page.locator('.v-window-item--active').filter({has: page.getByLabel('教师分配 JSON')});
  await section.getByRole('button', {name: '预检', exact: true}).click();
  const saved = page.waitForResponse(res => res.url().includes('/local-teachers/import?dryRun=false'));
  await section.getByRole('button', {name: '确认分配', exact: true}).click();
  assert.equal((await saved).status(), 201);
  const file = page.waitForEvent('download');
  await section.getByRole('button', {name: '另存本次教师分配凭据（CSV）'}).click();
  const csv = await readFile(await (await file).path(), 'utf8');
  assert.ok(csv.includes(`"${username}","000517","G2-1"`), 'Browser CSV contains the confirmed account and original PIN');
  const response = await fetch(api + '/accounts/local/login', {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({schoolCode: code, username, password: '000517'})});
  assert.equal(response.status, 200);
}
