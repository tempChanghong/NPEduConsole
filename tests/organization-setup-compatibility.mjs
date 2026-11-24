import assert from 'node:assert/strict';
import {Buffer} from 'node:buffer';
import {randomInt} from 'node:crypto';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';
import {startTarget} from './school-transfer-fixture.mjs';

export async function verifyOrganizationSetupCompatibility(context) {
  const {origin, web, directory, evidence, prisma, kvRequire} = context;
  await mkdir(evidence, {recursive: true});
  const target = await startTarget(context);
  const {Client} = kvRequire('pg');
  const db = new Client({connectionString: target.databaseUrl});
  const evidenceByMode = {};
  try {
    await db.connect();
    await verifyStudentBrowser({origin, web, directory, evidence, rooms: [], afterStudent: async (browser, frontend) => {
      for (const [full, backend, key] of [[false, origin, process.env.BOOTSTRAP_SETUP_KEY], [true, target.origin, target.setupKey]]) {
        const mode = full ? 'FULL' : 'QUICK';
        const code = `ORGANIZATION-${mode}`;
        const pin = String(randomInt(100000, 999999));
        const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
        const requests = [];
        try {
          await page.route('**/*', async route => {
            const request = route.request(), url = new URL(request.url());
            if (url.origin !== frontend) return route.abort();
            if (url.pathname === '/.well-known/npclassworks-deployment.json')
              return route.fulfill({json: {schemaVersion: 1, apiBaseUrl: null, adminRetiredSections: ['organization']}});
            if (!/^\/(api|accounts)(\/|$)/.test(url.pathname)) return route.continue();
            const response = await route.fetch({url: backend + url.pathname + url.search});
            if (url.pathname.startsWith('/api/v2/setup/')) requests.push({method: request.method(), path: url.pathname, status: response.status(),
              hasSetupToken: Boolean(await request.headerValue('x-classworks-setup-token'))});
            return route.fulfill({response});
          });
          await page.goto(frontend + '/setup');
          await page.getByLabel('BOOTSTRAP_SETUP_KEY', {exact: true}).fill(key);
          await page.getByRole('button', {name: '验证并继续'}).click();
          if (full) {
            await page.getByText(/^完整配置：继续导入/).click();
            assert.equal(await page.getByRole('radio', {name: /完整配置/}).isChecked(), true);
          }
          for (const [label, value] of [['学校名称', `隔离 ${mode} 学校`], ['学校代码', code], ['管理员姓名', '验收所有者'],
            ['管理员 PIN（4—8位数字）', pin], ['再次输入管理员 PIN', pin]]) await page.getByLabel(label, {exact: true}).fill(value);
          await page.getByRole('button', {name: '创建核心数据'}).click();
          if (full) {
            const org = page.locator('.v-window-item--active textarea').first();
            await org.waitFor({state: 'visible'});
            const expected = JSON.parse(await org.inputValue());
            assert.equal(expected.school.code, code);
            expected.grade.sortOrder = 17;
            expected.school.teacherAuth.sharedPassword = 'older-file-secret-fixture';
            await page.locator('.v-window-item--active input[type=file]').setInputFiles({name: 'old-organization.json',
              mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(expected))});
            await page.waitForFunction(() => JSON.parse(document.querySelector('.v-window-item--active textarea').value).grade.sortOrder === 17);
            assert.ok(!(await org.inputValue()).includes('older-file-secret-fixture'));
            await page.getByRole('button', {name: '预检配置', exact: true}).click();
            await page.getByRole('button', {name: '确认导入', exact: true}).click();
            await page.getByRole('button', {name: '导入教师配置 JSON', exact: true}).click();
            const staff = page.locator('.v-window-item--active textarea').first();
            assert.equal(JSON.parse(await staff.inputValue()).schoolCode, code);
            await page.getByRole('button', {name: '预检配置', exact: true}).click();
            await page.getByRole('button', {name: '确认导入', exact: true}).click();
            const screen = page.locator('.v-window-item--active').filter({hasText: '首个班级大屏账号'});
            await screen.locator('.v-select .v-field').click();
            await page.getByRole('option').first().click();
            const screenPin = String(randomInt(100000, 999999));
            for (const [label, value] of [[/^大屏名称/, '测试教室屏'], ['大屏登录账号', 'setup-screen-a'],
              ['大屏 PIN（4—8位数字）', screenPin], ['再次输入大屏 PIN', screenPin]]) await screen.getByLabel(label, {exact: true}).fill(value);
            await screen.getByRole('button', {name: '创建大屏账号'}).click();
            await screen.getByRole('button', {name: '完成大屏配置'}).click();
            for (const path of ['/organization/template', '/organization/import', '/staff-configuration/template', '/staff-configuration/import', '/screens']) {
              const matches = requests.filter(row => row.path.endsWith(path));
              assert.ok(matches.length && matches.every(row => row.hasSetupToken && row.status < 300), path);
            }
          } else assert.ok(requests.every(row => !row.path.includes('/organization/') && !row.path.includes('/staff-configuration/')));
          await page.getByLabel('PIN', {exact: true}).fill(pin);
          await page.getByRole('button', {name: '验证登录凭据'}).click();
          await page.getByText('管理员已验证', {exact: true}).waitFor();
          await page.getByLabel(/我已下载、复制或打印/).check();
          await page.screenshot({path: join(evidence, `${mode.toLowerCase()}-owner-verified.png`)});
          await page.getByText(/请在 NPEduConsole 打开「学校总览 → 开学配置」/).waitFor();
          await page.getByRole('button', {name: '完成并锁定初始化'}).click();
          await page.waitForURL('**/classworks-admin');
          await page.getByRole('region', {name: '学校管理办理去向'}).getByText('学校管理请使用 Windows 管理端 NPEduConsole。').waitFor();
          const status = await fetch(backend + '/api/v2/setup/status').then(response => response.json());
          assert.equal(status.data.state, 'COMPLETED');
          assert.equal(status.data.supportsOrganizationRetirement, true);
          evidenceByMode[mode] = requests;
        } catch (error) {
          await page.screenshot({path: join(evidence, `${mode.toLowerCase()}-failure.png`)});
          await writeFile(join(evidence, `${mode.toLowerCase()}-failure-requests.json`), JSON.stringify(requests, null, 2));
          throw error;
        } finally { await page.close(); }
      }
    }});
    const quick = await prisma.school.findUniqueOrThrow({where: {code: 'ORGANIZATION-QUICK'}, include: {terms: {include: {workspaces: true}}}});
    assert.equal(quick.terms[0].workspaces.length, 0);
    const {rows: [full]} = await db.query(`SELECT (SELECT count(*)::int FROM "Workspace") AS workspaces,
      (SELECT count(*)::int FROM "TeachingAssignment") AS assignments, (SELECT count(*)::int FROM "ClassroomScreenBinding") AS screens,
      (SELECT count(*)::int FROM "InstanceSetup" WHERE "completedAt" IS NOT NULL) AS completed`);
    assert.ok(full.workspaces > 0 && full.assignments > 0 && full.screens === 1 && full.completed === 1);
    await writeFile(join(evidence, 'setup-requests.json'), JSON.stringify({quickSchool: quick.id, full, modes: evidenceByMode}, null, 2));
    console.log('PASS: real Edge QUICK and FULL on two isolated KV databases, setup-token organization/staff routes, and completion.');
  } finally { await db.end(); await target.close(); }
}
