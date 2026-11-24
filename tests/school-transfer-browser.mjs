import assert from 'node:assert/strict';
import {createHash, generateKeyPairSync, randomBytes, randomUUID} from 'node:crypto';
import {createServer} from 'node:http';
import {createRequire} from 'node:module';
import {createReadStream} from 'node:fs';
import {spawn} from 'node:child_process';
import {dirname, join} from 'node:path';
import {pathToFileURL} from 'node:url';
import {readFile, writeFile} from 'node:fs/promises';

const jsonHeaders = {'Content-Type': 'application/json'};

export async function verifyLegacyTransferWeb({web, directory, evidence, origin: source, school, token, pin, file, passphrase, target, pilot}) {
  const require = createRequire(join(web, 'package.json'));
  const {chromium} = require('@playwright/test');
  const {preview} = await import(pathToFileURL(join(dirname(require.resolve('vite/package.json')), 'dist/node/index.js')).href);
  const outDir = join(directory, 'school-transfer-web');
  await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ['node_modules/vite/bin/vite.js', 'build', '--logLevel', 'error', '--outDir', outDir],
      {cwd: web, windowsHide: true, stdio: 'inherit', env: {...process.env, VITE_DEFAULT_KV_SERVER: '',
        VITE_DEFAULT_AUTH_SERVER: '', VITE_SERVER_URL: '', VITE_ENABLE_ANALYTICS: 'false'}});
    child.once('error', reject);
    child.once('exit', code => code === 0 ? resolve() : reject(new Error('Legacy web build failed')));
  });
  const pilotRelease = {name: 'isolated-transfer-release', configurePreviewServer(server) {
    server.middlewares.use((req, res, next) => {
      if (req.url === '/.well-known/npclassworks-deployment.json') {
        return res.writeHead(200, jsonHeaders).end(JSON.stringify({schemaVersion: 1, apiBaseUrl: null, adminRetiredSections: ['migration']}));
      }
      const prefix = '/downloads/npeduconsole/';
      if (req.url === prefix + 'console-release.json') return res.writeHead(200, jsonHeaders).end(JSON.stringify(pilot.manifest));
      if (req.url !== prefix + pilot.manifest.fileName) return next();
      res.writeHead(200, {'Content-Type': 'application/zip', 'Content-Length': pilot.manifest.sizeBytes});
      if (req.method === 'HEAD') return res.end();
      createReadStream(pilot.zip).pipe(res);
    });
  }};
  const open = (backend, plugins = []) => preview({configFile: false, root: web, build: {outDir}, plugins,
    preview: {host: '127.0.0.1', port: 0, proxy: {'/api': backend, '/accounts': backend}}});
  const [from, to] = await Promise.all([open(source, pilot ? [pilotRelease] : []), open(target.origin)]);
  const browser = await chromium.launch({channel: 'msedge', headless: true});
  try {
    const localPage = async server => {
      const origin = 'http://127.0.0.1:' + server.httpServer.address().port;
      const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
      await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
      return {origin, page};
    };
    const sourceWeb = await localPage(from);
    const migrationRequests = [];
    sourceWeb.page.on('request', request => {
      if (/\/api\/v2\/admin\/schools\/[^/]+\/migration\//.test(new URL(request.url()).pathname))
        migrationRequests.push(request.method() + ' ' + new URL(request.url()).pathname);
    });
    await sourceWeb.page.addInitScript(accessToken => localStorage.setItem('classworks-v2-access-token', accessToken), token);
    await sourceWeb.page.goto(sourceWeb.origin + '/classworks-admin?section=migration&school=' + school.id);
    let migrationId;
    if (pilot) {
      const guide = sourceWeb.page.getByRole('region', {name: '学校管理办理去向'});
      await guide.getByRole('heading', {name: '网页版已停止办理服务器迁移'}).waitFor();
      await guide.getByText(`原学校：${school.name}（编号 ${school.id}）`).waitFor();
      assert.equal(await sourceWeb.page.getByRole('button', {name: '生成并下载迁移包'}).count(), 0);
      assert.deepEqual(migrationRequests, []);
      await sourceWeb.page.screenshot({path: join(evidence, 'edge-source-migration-retired.png')});
      migrationId = await pilot.exportPackage();
    } else {
      const primary = sourceWeb.page.getByRole('button', {name: '生成并下载迁移包'});
      await sourceWeb.page.getByText(/1 张在期自习照片/).waitFor();
      assert.ok(await primary.isDisabled(), 'legacy webpage requires owner confirmation');
      await sourceWeb.page.screenshot({path: join(evidence, '05-edge-legacy-download.png')});
      await sourceWeb.page.getByLabel('迁移密码', {exact: true}).fill(passphrase);
      await sourceWeb.page.getByLabel('当前管理员 PIN', {exact: true}).fill(pin);
      await sourceWeb.page.getByRole('checkbox', {name: /我明白该文件包含整校业务数据/}).check();
      const download = sourceWeb.page.waitForEvent('download');
      await primary.click();
      const downloaded = await download;
      assert.match(downloaded.suggestedFilename(), /\.npcw-transfer$/);
      await downloaded.delete();
    }

    const targetWeb = await localPage(to);
    const page = targetWeb.page;
    await page.goto(targetWeb.origin + '/setup');
    await page.getByLabel('BOOTSTRAP_SETUP_KEY', {exact: true}).fill(target.setupKey);
    await page.getByRole('button', {name: '验证并继续'}).click();
    await page.getByText('从旧服务器迁入整校数据', {exact: true}).click();
    await page.locator('input[type=file]').first().setInputFiles(file);
    await page.getByLabel('迁移密码', {exact: true}).fill(passphrase);
    const preview = page.waitForResponse(response => response.url().endsWith('/migration/preview'));
    await page.getByRole('button', {name: '预检迁移包'}).click();
    assert.equal((await preview).status(), 200);
    await page.getByText(/自习照片及退出记录留在源服务器/).waitFor();
    await page.getByLabel('迁移密码', {exact: true}).blur();
    await page.screenshot({path: join(evidence, '06-edge-legacy-preview.png')});
    const imported = page.waitForResponse(response => response.url().endsWith('/migration/import'));
    await page.getByRole('button', {name: '确认导入', exact: true}).click();
    const reply = await imported;
    assert.equal(reply.status(), 201);
    assert.ok(reply.request().headers()['x-npclassworks-migration-preview'], 'legacy setup sends the confirmed preview');
    let receiptSchoolId;
    if (pilot) {
      assert.ok(migrationId);
      const setupToken = reply.request().headers()['x-classworks-setup-token'];
      assert.ok(setupToken);
      const receipt = await page.request.get(targetWeb.origin + '/api/v2/setup/migration/receipts/' + migrationId,
        {headers: {'X-Classworks-Setup-Token': setupToken}});
      assert.equal(receipt.status(), 200);
      const recovered = (await receipt.json()).data;
      assert.equal(recovered.manifest.migrationId, migrationId);
      receiptSchoolId = recovered.school.id;
    }
    await page.getByLabel('短账号', {exact: true}).fill('admin');
    await page.getByLabel('PIN', {exact: true}).fill(pin);
    await page.getByRole('button', {name: '验证登录凭据', exact: true}).click();
    await page.getByText(/已在目标服务器以学校所有者身份登录/).waitFor();
    const completed = page.waitForResponse(response => response.url().endsWith('/setup/desktop-complete'));
    await page.getByRole('button', {name: '完成并锁定初始化', exact: true}).click();
    assert.equal((await completed).status(), 200);
    if (pilot) {
      const teacher = (await localPage(to)).page;
      await teacher.goto(targetWeb.origin);
      await teacher.getByText('教师登录', {exact: true}).click();
      await teacher.getByText('登录教师工作台', {exact: true}).waitFor();
      await teacher.locator('.v-select').filter({hasText: '学校'}).locator('.v-field').click();
      await teacher.getByRole('option', {name: school.name, exact: true}).click();
      await teacher.getByLabel('教师短账号').fill(pilot.teacher);
      await teacher.getByLabel('个人 PIN').fill(pin);
      const workspaces = teacher.waitForResponse(res => new URL(res.url()).pathname === '/api/v2/me/workspaces' && res.ok());
      await teacher.getByRole('button', {name: '登录', exact: true}).click();
      const workspaceIds = (await (await workspaces).json()).data.map(row => row.workspace.id);
      assert.deepEqual(workspaceIds, [pilot.classId]);
      await teacher.getByText(/已授权 .* 个教学空间/).first().waitFor();
      await teacher.screenshot({path: join(evidence, 'edge-target-original-teacher.png')});
      assert.deepEqual(migrationRequests, []);
      return {migrationId, schoolId: receiptSchoolId, workspaceIds, targetReceiptRead: true,
        retiredSourceRequests: migrationRequests, targetOwnerLoggedIn: true, targetSetupCompleted: true};
    }
    return {legacySourceReadiness: true, legacyOwnerExportedPackage: true, legacySetupImportedOriginalSchool: true, legacyOwnerRealLogin: true};
  } finally {
    await browser.close();
    for (const server of [from, to]) { server.httpServer.closeAllConnections(); await server.close(); }
  }
}

export async function verifyFreshExportAuthentication({origin, school, prisma, fromKv, kvRequire, evidence, web, passphrase}) {
  const {oauthProviders} = await fromKv('config/oauth.js');
  const provider = oauthProviders.hly;
  const saved = {...provider};
  const env = ['BASE_URL', 'FRONTEND_URL', 'MIGRATION_REAUTH_HLY_ISSUER', 'MIGRATION_REAUTH_HLY_JWKS_URL'];
  const before = Object.fromEntries(env.map(key => [key, process.env[key]]));
  const clientId = 'isolated-school-transfer', subject = 'school-transfer-owner';
  const {sign} = kvRequire('jsonwebtoken');
  const {publicKey, privateKey} = generateKeyPairSync('rsa', {modulusLength: 2048});
  const keyId = 'school-transfer-fixture';
  let variant = 'valid';
  const grants = new Map();
  const providerServer = createServer(async (req, res) => {
    const url = new URL(req.url, issuer);
    const reply = body => res.writeHead(200, jsonHeaders).end(JSON.stringify(body));
    if (url.pathname === '/authorize') {
      const id = randomUUID();
      const nonce = url.searchParams.get('nonce');
      if (nonce) {
        assert.equal(url.searchParams.get('prompt'), 'login');
        assert.equal(url.searchParams.get('max_age'), '0');
      }
      const redirect = new URL(url.searchParams.get('redirect_uri'));
      assert.equal(redirect.origin, origin);
      grants.set(id, {nonce, variant, challenge: url.searchParams.get('code_challenge')});
      redirect.searchParams.set('state', url.searchParams.get('state'));
      redirect.searchParams.set('code', id);
      res.writeHead(302, {location: redirect.href}).end();
    } else if (url.pathname === '/token') {
      let raw = '';
      for await (const part of req) raw += part;
      const body = new URLSearchParams(raw), code = body.get('code');
      const grant = grants.get(code);
      if (!grant || grant.challenge !== createHash('sha256').update(body.get('code_verifier') ?? '').digest('base64url'))
        return res.writeHead(400).end();
      grants.delete(code);
      const authTime = Math.floor(Date.now() / 1000) - (grant.variant === 'old' ? 120 : 0);
      const idToken = sign({nonce: grant.variant === 'wrong-nonce' ? 'other' : grant.nonce, auth_time: authTime}, privateKey, {
        algorithm: 'RS256', keyid: keyId, issuer, audience: clientId,
        subject: grant.variant === 'wrong-subject' ? 'another-identity' : subject, expiresIn: 90,
      });
      reply({access_token: 'isolated-test-userinfo', id_token: idToken});
    } else if (url.pathname === '/userinfo') {
      reply({sub: subject, email: 'transfer-owner@example.test', email_verified: true, name: 'OAuth 迁移所有者'});
    } else if (url.pathname === '/jwks') {
      reply({keys: [{...publicKey.export({format: 'jwk'}), kid: keyId, use: 'sig', alg: 'RS256'}]});
    } else res.writeHead(404).end();
  });
  await new Promise(resolve => providerServer.listen(0, '127.0.0.1', resolve));
  const issuer = 'http://127.0.0.1:' + providerServer.address().port;
  const require = createRequire(join(web, 'package.json'));
  const {chromium} = require('@playwright/test');
  const browser = await chromium.launch({channel: 'msedge', headless: true});
  try {
    Object.assign(process.env, {BASE_URL: origin, FRONTEND_URL: origin});
    Object.assign(provider, {clientId, clientSecret: 'isolated-fixture', authorizationURL: issuer + '/authorize',
      tokenURL: issuer + '/token', userInfoURL: issuer + '/userinfo'});
    const account = await prisma.account.create({data: {provider: 'hly', providerId: subject, name: 'OAuth 迁移所有者'}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: account.id, role: 'OWNER'}});
    const login = async () => {
      const verifier = randomBytes(32).toString('base64url');
      const challenge = createHash('sha256').update(verifier).digest('base64url');
      const begin = await fetch(origin + '/accounts/oauth/hly?handoff_challenge=' + challenge, {redirect: 'manual'});
      const redirect = await fetch(begin.headers.get('location'), {redirect: 'manual'});
      const complete = await fetch(redirect.headers.get('location'), {redirect: 'manual', headers: {Cookie: begin.headers.getSetCookie()[0].split(';')[0]}});
      const code = new URL(complete.headers.get('location')).searchParams.get('oauth_code');
      assert.ok(code);
      const exchange = await fetch(origin + '/accounts/oauth/exchange', {method: 'POST', headers: jsonHeaders,
        body: JSON.stringify({code, verifier})});
      assert.equal(exchange.status, 200);
      return (await exchange.json()).access_token;
    };
    const [token, otherSession] = await Promise.all([login(), login()]);
    const scope = '/api/v2/admin/schools/' + school.id + '/migration/';
    const send = (path, actor, body) => fetch(origin + scope + path, {method: body ? 'POST' : 'GET',
      headers: {...jsonHeaders, Authorization: 'Bearer ' + actor}, body: body ? JSON.stringify(body) : undefined});
    delete process.env.MIGRATION_REAUTH_HLY_ISSUER;
    const blocked = await send('export', token, {passphrase, confirmationSchoolCode: school.code});
    assert.equal((await blocked.json()).code, 'MIGRATION_REAUTH_UNSUPPORTED');
    process.env.MIGRATION_REAUTH_HLY_ISSUER = issuer;
    process.env.MIGRATION_REAUTH_HLY_JWKS_URL = issuer + '/jwks';
    const codeOnly = await send('export', token, {passphrase, confirmationSchoolCode: school.code});
    assert.equal((await codeOnly.json()).code, 'MIGRATION_REAUTH_REQUIRED');
    async function verify(mode, verified) {
      variant = mode;
      const requestId = randomUUID();
      const begin = await send('reauth', token, {requestId});
      assert.equal(begin.status, 200);
      const {reauthId, browserPath} = (await begin.json()).data;
      const page = await browser.newPage({serviceWorkers: 'block'});
      try {
        await page.route('**/*', route => [origin, issuer].includes(new URL(route.request().url()).origin) ? route.continue() : route.abort());
        const reply = await page.goto(origin + '/' + browserPath);
        assert.equal(reply.status(), verified ? 200 : 400);
        if (mode === 'valid' && verified) await page.screenshot({path: join(evidence, '07-edge-fresh-owner.png')});
      } finally { await page.close(); }
      assert.equal((await (await send('reauth/' + reauthId, token)).json()).data.state, verified ? 'VERIFIED' : 'DECLINED');
      return {requestId, reauthId, passphrase};
    }
    for (const mode of ['old', 'wrong-nonce', 'wrong-subject']) {
      const denied = await verify(mode, false);
      assert.equal((await send('export', token, denied)).status, 428);
    }
    const proof = await verify('valid', true);
    assert.equal((await send('export', otherSession, proof)).status, 428);
    assert.equal((await send('export', token, {...proof, requestId: randomUUID()})).status, 428);
    const success = await send('export', token, proof);
    assert.equal(success.status, 200);
    await success.arrayBuffer();
    assert.equal((await send('export', token, proof)).status, 428);
    const beforeRevocation = await verify('valid', true);
    await prisma.schoolMember.update({where: {schoolId_accountId: {schoolId: school.id, accountId: account.id}}, data: {role: 'ADMIN'}});
    assert.equal((await send('export', token, beforeRevocation)).status, 403);
    await writeFile(join(evidence, 'oauth-export-evidence.json'), JSON.stringify({isolation: 'signed loopback OIDC provider, real KV callbacks and Edge',
      unsupportedProviderBlocked: true, schoolCodeRejected: true, staleAuthTimeRejected: true, wrongNonceRejected: true,
      wrongSubjectRejected: true, mismatchedSessionAndRequestRejected: true, singleUse: true, revokedOwnerRejected: true}, null, 2));
  } finally {
    Object.assign(provider, saved);
    for (const key of env) if (before[key] === undefined) delete process.env[key]; else process.env[key] = before[key];
    await browser.close();
    providerServer.closeAllConnections();
    await new Promise(resolve => providerServer.close(resolve));
  }
}
