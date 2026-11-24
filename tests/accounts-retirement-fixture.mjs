import assert from 'node:assert/strict';
import {createHash, randomUUID} from 'node:crypto';
import {spawn} from 'node:child_process';
import {mkdir, readFile, rm, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyReleasedAccountsRetirement({prisma, origin, fixture, fromKv, bcrypt, directory, web, evidence, zip, manifestPath}) {
  const {school, other, pin} = fixture;
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const bytes = await readFile(zip);
  assert.equal(manifest.version, '0.1.9');
  assert.equal(manifest.sha256, createHash('sha256').update(bytes).digest('hex'));
  assert.equal(manifest.sizeBytes, bytes.length);
  await mkdir(evidence, {recursive: true});
  const {localProviderId} = await fromKv('domain/localAccount.js');
  const subjectName = '同名管理员', username = 'native-handoff';
  async function addLocal(school, username, name, role) {
    const account = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(school.code, username),
      localUsername: username, name, localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: school.code}}});
    await prisma.schoolMember.create({data: {schoolId: school.id, accountId: account.id, role}});
    return account;
  }
  const homonym = await addLocal(school, username, subjectName, 'ADMIN');
  await addLocal(other, 'handoff-owner', '发放所有者', 'OWNER');
  await addLocal(other, 'peer-owner', '核对所有者', 'OWNER');
  assert.equal(await prisma.academicTerm.count({where: {schoolId: other.id}}), 0);

  async function api(path, {token, body, status = 200} = {}) {
    const response = await fetch(origin + path, {method: body ? 'POST' : 'GET', headers: {'Content-Type': 'application/json',
      ...(token && {Authorization: 'Bearer ' + token})}, body: body && JSON.stringify(body)});
    const envelope = await response.json();
    assert.equal(response.status, status, path + ' ' + (envelope.code ?? ''));
    return envelope.data ?? envelope;
  }
  async function login(code, user, password = pin, status = 200) {
    return api('/accounts/local/login', {body: {schoolCode: code, username: user, password}, status});
  }
  const base = `/api/v2/admin/schools/${other.id}/management-accounts`;
  const owner = (await login(other.code, 'handoff-owner')).access_token;
  const peer = (await login(other.code, 'peer-owner')).access_token;
  const auth = {token: owner};
  const checks = [];

  // The file is genuinely saved and used for a login, then removed from the test data directory.
  const access = join(directory, 'released-accounts-access.json');
  const output = join(evidence, `handoff-${randomUUID()}.txt`);
  const initial = '003571';
  await writeFile(access, JSON.stringify({isolated: true, login: {url: origin, website: origin, code: other.code, username: 'handoff-owner', pin},
    accountsRetirement: {name: subjectName, user: username, pin: initial, output}}));
  let admin;
  try {
    await new Promise((resolve, reject) => {
      const child = spawn('pwsh.exe', ['-NoProfile', '-STA', '-File', fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url)),
        '-ManagementLogin', '-FixturePath', access, '-ZipPath', zip, '-ManifestPath', manifestPath, '-EvidenceDirectory', join(evidence, 'console')],
      {windowsHide: true, stdio: 'inherit'});
      child.once('error', reject);
      child.once('exit', code => code === 0 ? resolve() : reject(new Error(`Released accounts check failed (${code})`)));
    });
    const saved = (await readFile(output, 'utf8')).split(/\r?\n/);
    assert.deepEqual(saved.slice(0, 3), [other.name, `学校代码 ${other.code}`, `短账号 ${username}`]);
    if (saved[3] !== `PIN ${initial}`) throw new Error('The delivered PIN did not match the applied operation.');
    admin = (await login(other.code, username, saved[3].slice(4))).access_token;
    checks.push('released-exe-native-picker-pin-login-without-term');
  } finally { await Promise.all([rm(access, {force: true}), rm(output, {force: true})]); }

  const directoryReply = await api(base, {token: admin});
  assert.equal(directoryReply.actorRole, 'ADMIN');
  const target = directoryReply.accounts.find(row => row.username === username);
  assert.ok(target?.managementLoginAt);
  assert.notEqual(target.id, homonym.id);
  await api(`/api/v2/admin/schools/${school.id}/management-accounts`, {token: admin, status: 403});
  await api(base + '/preview', {token: admin, body: {action: 'CREATE', username: 'forbidden', name: '禁止', role: 'ADMIN'}, status: 403});
  const [saved] = await prisma.$queryRaw`SELECT result FROM "SchoolManagementAccountReceipt" WHERE "schoolId"=${other.id} AND result->>'accountId'=${target.id}`;
  const first = saved.result;
  const proof = base + `/requests/${first.requestId}/pin-delivery`;
  const receipt = await api(base + `/requests/${first.requestId}`, auth);
  assert.equal(receipt.accountId, target.id);
  assert.ok(!('credentialDelivery' in receipt));
  assert.equal((await api(proof, auth)).accountId, target.id);
  await api(proof, {token: peer, status: 409});

  async function change(token, action, options = {}) {
    const pending = await api(base + '/preview', {token, body: action});
    assert.equal(pending.canConfirm, true);
    const body = {...action, expectedFingerprint: pending.fingerprint, requestId: randomUUID(),
      ...(options.pin && {requestCredentialDelivery: true}), ...options};
    return {result: await api(base, {token, body}), body};
  }
  const replacement = '529408';
  const reset = await change(peer, {action: 'RESET_PIN', accountId: target.id}, {pin: replacement});
  assert.ok(reset.result.credentialDelivery.credentialVersion > first.credentialDelivery.credentialVersion);
  await api(proof, {...auth, status: 409});
  const replay = await api(base, {token: peer, body: {...reset.body, pin: initial}});
  assert.equal(replay.replayed, true);
  assert.ok(!('credentialDelivery' in replay));
  await login(other.code, username, initial, 401);
  await login(other.code, username, replacement);
  checks.push('different-owner-rotation-invalidates-only-original-proof-and-replay-has-no-proof');

  const fresh = await change(owner, {action: 'RESET_PIN', accountId: target.id}, {pin: initial});
  const freshProof = base + `/requests/${fresh.result.requestId}/pin-delivery`;
  await api(freshProof, auth);
  const actor = directoryReply.accounts.find(row => row.username === 'handoff-owner');
  await change(peer, {action: 'ROLE', accountId: actor.id, role: 'VIEWER'});
  await api(freshProof, {...auth, status: 403});
  const rows = await prisma.$queryRaw`SELECT result FROM "SchoolManagementAccountReceipt" WHERE "schoolId"=${other.id}`;
  for (const sample of [pin, initial, replacement]) assert.ok(!JSON.stringify(rows).includes('"' + sample + '"'));
  checks.push('lost-school-permission-refuses-delivery-and-receipts-contain-no-pin');

  const webOwner = (await login(school.code, 'admin')).access_token;
  await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (browser, site) => {
    const page = await browser.newPage({viewport: {width: 1440, height: 900}, serviceWorkers: 'block'});
    try {
      await page.addInitScript(token => localStorage.setItem('classworks-v2-access-token', token), webOwner);
      await page.route(`${site}/.well-known/npclassworks-deployment.json`, route =>
        route.fulfill({json: {schemaVersion: 1, adminLegacyManagementEnabled: true, apiBaseUrl: site, adminRetiredSections: ['accounts']}}));
      await page.route(`${site}/downloads/npeduconsole/**`, route => route.request().url().endsWith('/console-release.json') ?
        route.fulfill({json: manifest}) : route.fulfill({contentType: 'application/zip', headers: {'Content-Length': String(manifest.sizeBytes)}}));
      const setupUrl = `${site}/api/v2/setup/status`;
      await page.route(setupUrl, async route => {
        const response = await route.fetch();
        const envelope = await response.json();
        assert.equal(envelope.data.supportsDesktopManagementAccounts, true);
        delete envelope.data.supportsDesktopManagementAccounts;
        await route.fulfill({response, json: envelope});
      });
      const reads = [];
      page.on('request', request => {
        if (new URL(request.url()).pathname.endsWith('/local-accounts')) reads.push(new URL(request.url()).pathname);
      });
      await page.goto(`${site}/classworks-admin?section=accounts&school=${school.id}&account=${homonym.id}`);
      await page.getByText(`${subjectName} · @${username}`, {exact:true}).waitFor();
      await page.getByText(`原账号编号：${homonym.id}`, {exact:true}).waitFor();
      await page.screenshot({path: join(evidence, 'edge-legacy-original-account.png')});
      assert.deepEqual(reads, [`/api/v2/admin/schools/${school.id}/local-accounts`]);
      await page.unroute(setupUrl);
      await page.reload();
      await page.getByRole('heading', {name: '网页版已停止办理账号与管理员'}).waitFor();
      await page.getByText(`原账号编号：${homonym.id}`, {exact:true}).waitFor();
      await page.getByRole('button', {name: /获取 Windows 管理端/i}).waitFor();
      await page.screenshot({path: join(evidence, 'edge-retired-original-account.png')});
      assert.equal(reads.length, 1);
    } finally { await page.close(); }
  }});
  checks.push('real-edge-kv-legacy-reads-original-account-retired-does-not-read');
  await writeFile(join(evidence, 'accounts-retirement-result.json'), JSON.stringify({releaseSha256: manifest.sha256,
    school: other.id, target: target.id, otherSchool: school.id, homonym: homonym.id, requestId: first.requestId, checks}, null, 2));
  console.log('PASS: released account handoff, credential rotation, loss of school access and exact legacy Edge lookup.');
}
