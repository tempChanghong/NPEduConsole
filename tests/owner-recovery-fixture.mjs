import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomInt, randomUUID} from 'node:crypto';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {createServer, request} from 'node:http';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

const recovery = '/accounts/local/recover-owner';
const releaseDriver = fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url));

async function releaseWindow({directory, output, login, zip, setupKey, pin}) {
  await mkdir(output, {recursive: true});
  const file = join(directory, 'recovery-target.json');
  await writeFile(file, JSON.stringify({isolated: true, login}));
  const child = spawn('pwsh.exe', ['-NoProfile', '-STA', '-File', releaseDriver, '-OwnerRecovery',
    '-FixturePath', file, '-ZipPath', zip, '-EvidenceDirectory', output], {windowsHide: true, stdio: ['pipe', 'inherit', 'inherit']});
  const finished = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', code => code === 0 ? resolve() : reject(new Error('Released recovery UI failed: ' + code)));
  });
  child.stdin.end(JSON.stringify({setupKey, pin}));
  await finished;
}

export async function verifyOwnerRecovery({prisma, origin, directory, fixture, bcrypt, web, evidence, releaseDirectory}) {
  const {school, other, classes} = fixture;
  const setupKey = process.env.BOOTSTRAP_SETUP_KEY;
  const manifest = JSON.parse(await readFile(join(releaseDirectory, 'console-release.json'), 'utf8'));
  const zip = join(releaseDirectory, manifest.fileName);
  const username = 'school-owner';
  const pins = new Set();
  function nextPin() {
    let pin;
    do { pin = '0' + String(randomInt(10000, 100000)); } while (pins.has(pin));
    pins.add(pin);
    return pin;
  }
  async function local(targetSchool, short, pin) {
    const account = await prisma.account.create({data: {provider: 'school-local', providerId: targetSchool.code + ':' + short,
      localUsername: short, name: '校务验收员', localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: targetSchool.code}}});
    await prisma.schoolMember.create({data: {schoolId: targetSchool.id, accountId: account.id, role: 'OWNER'}});
    return account;
  }
  async function call(path, {body, token, expected = 200} = {}) {
    const headers = {};
    if (token) headers.Authorization = 'Bearer ' + token;
    if (body) headers['Content-Type'] = 'application/json';
    const res = await fetch(origin + path, {method: body ? 'POST' : 'GET', headers, body: body ? JSON.stringify(body) : undefined});
    assert.equal(res.status, expected, path);
    return res.json();
  }
  const login = async (target, short, pin, expected) => (await call('/accounts/local/login',
    {body: {schoolCode: target.code, username: short, password: pin}, expected})).data;
  const firstPin = nextPin(), secondPin = nextPin(), peerPin = nextPin(), webPin = nextPin();
  const first = await local(other, username, firstPin);
  const second = await local(school, username, secondPin);
  const peer = await local(other, 'peer-owner', peerPin);
  const webOwner = await local(other, 'web-owner', webPin);
  const federated = await prisma.account.create({data: {provider: 'isolated-provider', providerId: 'external-owner', name: '外部所有者'}});
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: federated.id, role: 'OWNER'}});
  await prisma.schoolMember.create({data: {schoolId: school.id, accountId: first.id, role: 'VIEWER'}});
  await prisma.workspaceMember.create({data: {workspaceId: classes[0], accountId: first.id, role: 'ASSISTANT'}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, name: '数学', code: 'MATH-RECOVERY'}});
  await prisma.administrativeClassSubject.create({data: {administrativeClassId: classes[0], subjectId: subject.id, deliveryMode: 'ADMIN_CLASS'}});
  await prisma.teachingAssignment.create({data: {workspaceId: classes[0], subjectId: subject.id, accountId: first.id}});
  await prisma.administrativeClassLeadership.create({data: {administrativeClassId: classes[0], accountId: first.id}});
  assert.equal(await prisma.academicTerm.count({where: {schoolId: other.id, status: 'ACTIVE'}}), 0);
  const readRelations = () => Promise.all([
    prisma.schoolMember.findMany({where: {accountId: first.id}, orderBy: {schoolId: 'asc'}}),
    prisma.workspaceMember.findMany({where: {accountId: first.id}, orderBy: {workspaceId: 'asc'}}),
    prisma.teachingAssignment.findMany({where: {accountId: first.id}}),
    prisma.administrativeClassLeadership.findMany({where: {accountId: first.id}}),
  ]);
  const relations = await readRelations();
  const peerSession = await login(other, peer.localUsername, peerPin);
  const firstSessions = [await login(other, username, firstPin), await login(other, username, firstPin)];
  const secondSession = await login(school, username, secondPin);
  const state = id => prisma.account.findUniqueOrThrow({where: {id}, select: {localDisabled: true, localLoginFailures: true,
    localLockedUntil: true, tokenVersion: true, localPasswordHash: true}});
  const secondBefore = await state(second.id);
  await prisma.account.update({where: {id: first.id}, data: {localDisabled: true, localLoginFailures: 5, localLockedUntil: new Date(Date.now() + 3600000)}});
  const firstBefore = await state(first.id);
  const pin = nextPin();
  for (const [code, short, key, expected] of [[other.code, username, 'wrong-fixture-key', 401],
    [school.code, 'admin', setupKey, 404], [school.code, 'viewer', setupKey, 404], [other.code, 'external-owner', setupKey, 404]]) {
    await call(recovery, {body: {schoolCode: code, username: short, setupKey: key, newPin: pin}, expected});
  }
  assert.ok(JSON.stringify(await state(first.id)) === JSON.stringify(firstBefore), 'rejected recoveries changed the account');

  // Count only requests issued by the released window. The upstream KV always remains pinned to this fixture.
  let fault = 'normal';
  let attempts = 0, applied = 0, logins = 0, redirected = 0;
  const relay = createServer((req, res) => {
    if (req.url === '/moved') { redirected++; req.resume(); res.writeHead(404).end(); return; }
    const watched = req.method === 'POST' && req.url === recovery;
    if (req.method === 'POST' && req.url === '/accounts/local/login') logins++;
    if (watched) {
      attempts++;
      if (req.headers.authorization || req.headers.cookie) { res.writeHead(400).end(); return; }
      if (fault === 'before') { req.resume(); res.destroy(); return; }
      if (fault === 'redirect') { req.resume(); res.writeHead(307, {Location: '/moved'}).end(); return; }
      if (fault === 'wrong-type') { req.resume(); res.writeHead(200, {'Content-Type': 'text/html'}).end('{"success":true}'); return; }
    }
    const target = new URL(origin);
    const upstream = request({hostname: target.hostname, port: target.port, path: req.url, method: req.method,
      headers: {...req.headers, host: target.host}}, result => {
      if (watched && result.statusCode === 200) {
        applied++;
        if (fault === 'after') { result.resume(); result.on('end', () => res.destroy()); return; }
      }
      res.writeHead(result.statusCode, result.headers);
      result.pipe(res);
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  const relayUrl = 'http://127.0.0.1:' + relay.address().port;
  const results = [];
  try {
    for (const separateApi of [false, true]) {
      const root = join(evidence, separateApi ? 'split' : 'same');
      await mkdir(root, {recursive: true});
      await verifyStudentBrowser({origin: relayUrl, web, directory, evidence: root, rooms: [], separateApi,
        afterStudent: async (browser, website) => {
          const api = separateApi ? relayUrl : website;
          const oldCors = process.env.CORS_ALLOWED_ORIGINS;
          process.env.CORS_ALLOWED_ORIGINS = website;
          try {
            for (const scenario of (separateApi ? ['before', 'redirect', 'wrong-type', 'after'] : ['normal'])) {
              fault = scenario;
              const target = separateApi ? school : other;
              const account = separateApi ? second : first;
              const start = await state(account.id);
              const oldPin = separateApi ? secondPin : firstPin;
              const submittedPin = nextPin();
              const counts = [attempts, applied, logins];
              const written = Number(scenario === 'normal' || scenario === 'after');
              await releaseWindow({directory, output: join(root, scenario), zip, pin: submittedPin, setupKey, login: {
                url: api, website, code: target.code, username, width: separateApi ? 840 : 1160, height: separateApi ? 480 : 780,
                confirmed: scenario === 'normal', expectLogin: !!written}});
              assert.deepEqual([attempts - counts[0], applied - counts[1], logins - counts[2]], [1, written, 1]);
              assert.equal(redirected, 0, 'the recovery endpoint must never follow an HTTP redirect');
              const changed = await state(account.id);
              assert.equal(changed.tokenVersion, start.tokenVersion + written);
              assert.equal(await bcrypt.compare(submittedPin, changed.localPasswordHash), !!written);
              if (written) {
                assert.equal(changed.localDisabled, false);
                assert.equal(changed.localLockedUntil, null);
                assert.equal(changed.localLoginFailures, 0);
                await login(target, username, oldPin, 401);
                for (const old of separateApi ? [secondSession] : firstSessions) {
                  await call('/accounts/profile', {token: old.access_token, expected: 401});
                  await call('/accounts/refresh', {body: {refresh_token: old.refresh_token}, expected: 401});
                }
              } else {
                await login(target, username, oldPin);
                await call('/accounts/profile', {token: secondSession.access_token});
              }
              if (!separateApi) {
                assert.ok(JSON.stringify(await state(second.id)) === JSON.stringify(secondBefore), 'other school account changed');
                await call('/accounts/profile', {token: secondSession.access_token});
              }
              await call('/accounts/profile', {token: peerSession.access_token});
              assert.deepEqual(await readRelations(), relations);
              results.push({scenario, website, api, target: target.code, ownerId: account.id, posts: 1, successfulWrites: written,
                explicitLogins: 1, previousSessionsVerified: written ? (separateApi ? 1 : 2) : 0});
              console.log('PASS released recovery:', scenario, target.code);
            }
            if (separateApi) {
              fault = 'normal';
              const page = await browser.newPage({serviceWorkers: 'block'});
              try {
                await page.route('**/*', route => [website, api].includes(new URL(route.request().url()).origin) ? route.continue() : route.abort());
                await page.goto(website + '/classworks-admin?school=' + other.id);
                await page.getByRole('link', {name: '管理员登录与 OWNER 恢复'}).click();
                await page.getByRole('button', {name: /忘记 OWNER PIN/}).click();
                await page.getByLabel('OWNER 短账号').fill('web-owner');
                const replacement = nextPin();
                await page.getByLabel('新 PIN', {exact: true}).fill(replacement);
                await page.getByLabel('服务器 BOOTSTRAP_SETUP_KEY').fill(setupKey);
                await page.getByRole('button', {name: '恢复 OWNER PIN', exact: true}).click();
                await page.getByText('OWNER PIN 已恢复，请使用新 PIN 登录。', {exact: true}).waitFor();
                const webLogin = await login(other, 'web-owner', replacement);
                await page.screenshot({path: join(root, 'edge-recovered.png')});
                const personalPin = nextPin();
                await call('/accounts/local/change-pin', {token: webLogin.access_token, body: {currentPin: replacement, newPin: personalPin}});
                const ownLogin = await login(other, 'web-owner', personalPin);
                const path = '/api/v2/admin/schools/' + other.id + '/management-accounts';
                const operation = {action: 'RESET_PIN', accountId: webOwner.id};
                const preview = (await call(path + '/preview', {token: peerSession.access_token, body: operation})).data;
                assert.ok(preview.canConfirm);
                const managedPin = nextPin();
                await call(path, {token: peerSession.access_token, body: {...operation, expectedFingerprint: preview.fingerprint,
                  requestId: randomUUID(), pin: managedPin}});
                await call('/accounts/profile', {token: ownLogin.access_token, expected: 401});
                await login(other, 'web-owner', managedPin);
              } finally { await page.close(); }
            }
          } finally {
            if (oldCors === undefined) delete process.env.CORS_ALLOWED_ORIGINS;
            else process.env.CORS_ALLOWED_ORIGINS = oldCors;
          }
        }});
    }
    await writeFile(join(evidence, 'recovery-result.json'), JSON.stringify({passed: true, hash: manifest.sha256, results,
      legacyEdgeRecovery: true, signedInChangeOwnPin: true, signedInPeerOwnerReset: true,
      unchangedGrantsAndClass: true, sameSchoolPeerSessionValid: true}, null, 2));
  } finally { relay.closeAllConnections(); await new Promise(resolve => relay.close(resolve)); }
}
