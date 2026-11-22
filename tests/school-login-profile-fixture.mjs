import assert from 'node:assert/strict';
import {randomBytes, randomInt, randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import {createRequire} from 'node:module';
import {join, dirname} from 'node:path';
import {pathToFileURL} from 'node:url';
import {isolatedIdentityProvider} from './school-teacher-allocation-fixture.mjs';

const setup = '/api/v2/setup';
const asPin = () => String(randomInt(100000, 1000000));
const asPassword = () => randomBytes(18).toString('base64url');
const getProfilePath = schoolId => '/api/v2/admin/schools/' + schoolId + '/profile';

function client(origin) {
  return async (path, {method = 'GET', body, token, setupToken, status = 200} = {}) => {
    const response = await fetch(origin + path, {method, headers: {
      'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {}),
      ...(setupToken ? {'X-Classworks-Setup-Token': setupToken} : {}),
    }, body: body === undefined ? undefined : JSON.stringify(body)});
    const output = await response.json();
    if (status !== null) assert.equal(response.status, status, path + ' ' + (output.code ?? ''));
    return {status: response.status, value: output.data ?? output, code: output.code};
  };
}

function login(api, schoolCode, username, password, status = 200) {
  return api('/accounts/local/login', {method: 'POST', body: {schoolCode, username, password}, status});
}

function organization(school, term, classCode) {
  return {
    school,
    term: {name: term.name, academicYear: term.academicYear, semester: term.semester, status: term.status},
    grade: {code: 'PGFIX', name: '真实数据库导入验收年级'},
    subjects: [{code: 'PROFILE-SUBJECT', name: '教务导入学科', category: 'CORE'}],
    administrativeClasses: [{code: classCode, name: '导入验收班', subjectRules: {'PROFILE-SUBJECT': 'ADMIN_CLASS'}}],
    courseGroups: [],
  };
}

async function opensClass(api, token, classId) {
  const {value} = await api('/api/v2/me/workspaces', {token});
  assert.ok(JSON.stringify(value).includes(classId), 'original class remains accessible');
}

export async function verifyFirstSchoolSetup({prisma, origin, evidence}) {
  await mkdir(evidence, {recursive: true});
  const api = client(origin);
  const ticket = async () => (await api(setup + '/session', {method: 'POST',
    body: {setupKey: process.env.BOOTSTRAP_SETUP_KEY}})).value.token;
  const [firstTicket, secondTicket] = await Promise.all([ticket(), ticket()]);
  const credentials = {username: 'founder', pin: asPin(), sharedPassword: asPassword()};
  const input = {...credentials, schoolCode: 'SETUP-PROFILE', schoolName: '通用口令初次建校', administratorName: '首位管理员',
    termName: '2026 第一学期', academicYear: 2026, semester: 1, teacherAuthMode: 'SHARED_PASSWORD', allowOAuthTeacherLogin: true};
  const contender = {...input, username: 'contender', sharedPassword: asPassword(), pin: asPin()};
  const results = await Promise.all([firstTicket, secondTicket].map((setupToken, index) =>
    api(setup + '/initialize', {method: 'POST', setupToken, body: index === 0 ? input : contender, status: null})));
  assert.deepEqual(results.map(row => row.status).sort(), [201, 409]);
  const won = results[0].status === 201 ? input : contender;
  const lost = won === input ? contender : input;
  const setupToken = won === input ? firstTicket : secondTicket;
  const school = await prisma.school.findUniqueOrThrow({where: {code: input.schoolCode}});
  assert.equal(await prisma.school.count(), 1);
  assert.equal(await prisma.schoolMember.count({where: {schoolId: school.id, role: 'OWNER'}}), 1);
  const result = results.find(row => row.status === 201).value;
  assert.ok(!JSON.stringify(result).includes(won.sharedPassword));
  assert.ok(!JSON.stringify(result).includes(school.teacherSharedPasswordHash));
  const term = await prisma.academicTerm.findFirstOrThrow({where: {schoolId: school.id, status: 'ACTIVE'}});
  const document = organization({code: school.code, name: school.name,
    teacherAuth: {mode: 'SHARED_PASSWORD', allowOAuthFallback: true}}, term, 'SETUP-C1');
  await api(setup + '/organization/import', {method: 'POST', setupToken, status: 201, body: {organization: document}});
  await api(setup + '/teachers/import', {method: 'POST', setupToken, status: 201,
    body: {assignmentPlan: {assignments: [{username: 'newteacher', name: '首次设置教师', workspaceCodes: ['SETUP-C1'], role: 'TEACHER'}]}}});
  await api(setup + '/complete', {method: 'POST', setupToken});
  await login(api, school.code, won.username, won.pin);
  assert.ok((await login(api, school.code, lost.username, lost.pin, null)).status >= 400);
  const teacher = (await login(api, school.code, 'newteacher', won.sharedPassword)).value;
  const room = await prisma.workspace.findFirstOrThrow({where: {termId: term.id, code: 'SETUP-C1'}});
  await opensClass(api, teacher.access_token, room.id);
  await writeFile(join(evidence, 'first-setup.json'), JSON.stringify({concurrentStatuses: [201, 409], owners: 1,
    organizationImported: true, ownerLogin: true, sharedTeacherLoginAndClass: true, noCredentialInSetupResult: true}, null, 2));
}

export async function verifySchoolLoginProfile({prisma, origin, fixture, fromKv, bcrypt, directory, evidence, web, runDesktop}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, classes: [classId], pin, managerId} = fixture;
  const originalPassword = asPassword(), replacementPassword = asPassword(), newPin = asPin();
  const {localProviderId} = await fromKv('domain/localAccount.js');
  const oldHash = await bcrypt.hash(originalPassword, 10);
  await prisma.school.update({where: {id: school.id}, data: {teacherAuthMode: 'SHARED_PASSWORD',
    allowOAuthTeacherLogin: true, teacherSharedPasswordHash: oldHash}});
  await prisma.schoolMember.create({data: {schoolId: other.id, accountId: managerId, role: 'ADMIN'}});
  const hasPin = await prisma.account.findUniqueOrThrow({where: {provider_providerId: {
    provider: 'school-local', providerId: localProviderId(school.code, 'viewer')}}});
  await prisma.account.update({where: {id: hasPin.id}, data: {name: '已有个人 PIN 教师'}});
  const noPin = await prisma.account.create({data: {name: '仅通用口令教师', provider: 'school-local',
    providerId: localProviderId(school.code, 'sharedonly'), localUsername: 'sharedonly', providerData: {schoolCode: school.code}}});
  await prisma.schoolMember.create({data: {schoolId: school.id, accountId: noPin.id, role: 'VIEWER'}});
  await prisma.workspaceMember.createMany({data: [hasPin.id, noPin.id].map(accountId =>
    ({workspaceId: classId, accountId, role: 'TEACHER'}))});
  const api = client(origin);
  const owner = (await login(api, school.code, 'admin', pin)).value;
  const token = owner.access_token;
  const oldViewer = (await login(api, school.code, 'viewer', originalPassword)).value.access_token;
  await opensClass(api, oldViewer, classId);
  const importPath = '/api/v2/admin/organization/import';
  const pureSchool = {code: school.code, name: school.name};
  await api(importPath, {method: 'POST', token, status: 201, body: {organization: organization(pureSchool, term, 'ORG-OK')}});
  for (const [index, declaration] of [
    {teacherAuth: {allowOAuthFallback: false}},
    {teacherAuthMode: 'LOCAL_PIN'},
    {allowOAuthTeacherLogin: false},
  ].entries()) {
    const document = organization({...pureSchool, ...declaration}, term, 'ORG-BLOCK-' + index);
    for (const dryRun of [true, false]) {
      const response = await api(importPath + '?dryRun=' + dryRun,
        {method: 'POST', token, status: 428, body: {organization: document}});
      assert.match(response.value.message, /学校基础设置|登录设置/);
    }
    assert.equal(await prisma.workspace.count({where: {termId: term.id, code: 'ORG-BLOCK-' + index}}), 0);
  }
  const same = {...pureSchool, teacherAuthMode: 'SHARED_PASSWORD', allowOAuthTeacherLogin: true,
    teacherAuth: {mode: 'SHARED_PASSWORD', allowOAuthFallback: true}};
  await api(importPath, {method: 'POST', token, status: 201, body: {organization: organization(same, term, 'ORG-SAME')}});
  const current = await prisma.school.findUniqueOrThrow({where: {id: school.id}});
  assert.equal(current.teacherAuthMode, 'SHARED_PASSWORD');
  assert.equal(current.allowOAuthTeacherLogin, true);
  assert.equal(current.teacherSharedPasswordHash, oldHash);
  await login(api, school.code, 'sharedonly', originalPassword);

  const profile = getProfilePath(school.id);
  const countReceipts = async () => (await prisma.$queryRaw`SELECT count(*)::int AS count FROM "SchoolProfileReceipt" WHERE "schoolId" = ${school.id}`)[0].count;
  const checkpoints = new Set();
  let successfulReplyDropped = 0, receiptReads = 0;
  let timedPreview, previewHeld, releasePreview;
  const waitForPreview = new Promise(resolve => { previewHeld = resolve; });
  const previewMayReply = new Promise(resolve => { releasePreview = resolve; });
  const previewWrite = async pathname => {
    assert.ok(pathname.endsWith('/previewed'), 'only the known desktop checkpoint may start Edge');
    assert.equal(await countReceipts(), 0);
    await renameFromEdge({web, directory, evidence, origin, school, owner});
  };
  const relay = createServer(async (req, res) => {
    try {
      const pathname = new URL(req.url, origin).pathname;
      if (pathname.startsWith('/__profile-test/')) {
        if (pathname.endsWith('/hold-old-preview')) {
          timedPreview = true;
          res.writeHead(204).end();
          return;
        }
        if (pathname.endsWith('/old-preview-in-flight')) {
          await waitForPreview;
          res.writeHead(204).end();
          return;
        }
        if (pathname.endsWith('/release-old-preview')) {
          releasePreview();
          res.writeHead(204).end();
          return;
        }
        if (pathname.endsWith('/previewed')) await previewWrite(pathname);
        if (pathname.endsWith('/switched')) {
          const firstTeacher = (await login(api, school.code, 'viewer', pin)).value;
          await opensClass(api, firstTeacher.access_token, classId);
          await opensClass(api, oldViewer, classId);
          assert.ok((await login(api, school.code, 'sharedonly', originalPassword, null)).status >= 400);
        }
        if (pathname.endsWith('/pin-set')) {
          const secondTeacher = (await login(api, school.code, 'sharedonly', newPin)).value;
          await opensClass(api, secondTeacher.access_token, classId);
        }
        if (pathname.endsWith('/withdrew-other-school')) {
          await prisma.schoolMember.delete({where: {schoolId_accountId: {schoolId: other.id, accountId: managerId}}});
        }
        checkpoints.add(pathname.split('/').pop());
        res.writeHead(204).end();
        return;
      }
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = Buffer.concat(chunks);
      const upstream = request(origin + req.url, {method: req.method, headers: req.headers}, async reply => {
        if (req.method === 'PATCH' && pathname === profile && reply.statusCode === 200 &&
            'sharedPassword' in JSON.parse(body) && !successfulReplyDropped) {
          successfulReplyDropped++;
          reply.resume();
          res.destroy();
          return;
        }
        if (timedPreview && pathname === profile + '/preview') {
          timedPreview = false;
          previewHeld();
          await previewMayReply;
        }
        if (req.method === 'GET' && pathname.startsWith(profile + '/requests/') && reply.statusCode === 200) receiptReads++;
        res.writeHead(reply.statusCode, reply.headers);
        reply.pipe(res);
      });
      upstream.once('error', () => res.destroy());
      upstream.end(body);
    } catch (error) {
      console.error(error.message);
      res.writeHead(500).end();
    }
  });
  await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
  try {
    const fixturePath = join(directory, 'school-profile.json');
    await writeFile(fixturePath, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port,
      schoolId: school.id, schoolCode: school.code, otherSchoolId: other.id, pin, newPin, replacementPassword, accountId: noPin.id,
      isolated: true}), {mode: 0o600});
    await runDesktop(['--live-school-profile', fixturePath, evidence]);
  } finally {
    releasePreview();
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
  assert.deepEqual([...checkpoints], ['previewed', 'switched', 'pin-set', 'rotated', 'no-term', 'withdrew-other-school']);
  assert.equal(successfulReplyDropped, 1);
  assert.ok(receiptReads > 0);
  assert.equal(await countReceipts(), 3);
  const after = await prisma.school.findUniqueOrThrow({where: {id: school.id}});
  assert.equal(after.teacherAuthMode, 'SHARED_PASSWORD');
  assert.equal(after.allowOAuthTeacherLogin, true);
  assert.equal(after.name, '新版网页已更新校名');
  await opensClass(api, oldViewer, classId);
  const fresh = (await login(api, school.code, 'sharedonly', replacementPassword)).value;
  await opensClass(api, fresh.access_token, classId);
  assert.ok((await login(api, school.code, 'sharedonly', originalPassword, null)).status >= 400);
  await login(api, school.code, 'admin', pin);
  const oauth = await verifyOAuthPolicy({prisma, fromKv, origin, api, school, other, classId, managerId, token});
  await writeFile(join(evidence, 'http-pg-cross-client.json'), JSON.stringify({checkpoints: [...checkpoints],
    existingOrganization: 'implicit policy preserved; same explicit policy accepted; three mismatches rejected in preview and commit',
    oldSessionClassAccess: true, replacementPasswordClassAccess: true, oldSharedPasswordRejected: true,
    managementPin: true, successfulReplyDropped, receiptReads, priorSchoolPreviewHeldAcrossSwitch: true, oauth}, null, 2));
}

async function verifyOAuthPolicy({prisma, fromKv, origin, api, school, other, classId, managerId, token}) {
  const provider = await isolatedIdentityProvider(fromKv, origin);
  try {
    const email = 'claim-profile-teacher@example.test', pendingEmail = 'claim-profile-pending@example.test';
    const path = getProfilePath(school.id);
    const invite = (address) => prisma.workspaceMemberInvite.create({data: {workspaceId: classId, email: address,
      normalizedEmail: address, role: 'TEACHER', invitedByAccountId: managerId}});
    await invite(email);
    const pending = await invite(pendingEmail);
    const signIn = async address => {
      const {code, verifier} = await provider.begin(address);
      return (await provider.exchange(code, verifier)).access_token;
    };
    const existing = await signIn(email);
    await opensClass(api, existing, classId);
    const directory = await api('/accounts/profile', {token: existing});
    const oauthAccount = directory.value.id ?? directory.value.account?.id;
    assert.ok(oauthAccount);
    await prisma.schoolMember.create({data: {schoolId: other.id, accountId: oauthAccount, role: 'ADMIN'}});
    const multiSchool = await signIn(email);
    await api(getProfilePath(other.id), {token: multiSchool});

    async function update(change) {
      const baseline = (await api(path, {token})).value;
      const fields = {...change, expectedUpdatedAt: baseline.updatedAt};
      const preview = (await api(path + '/preview', {token, method: 'POST', body: {...fields, rotateSharedPassword: false}})).value;
      await api(path, {token, method: 'PATCH', body: {...fields, requestId: randomUUID(), expectedFingerprint: preview.fingerprint}});
      return preview;
    }
    const off = await update({allowOAuthTeacherLogin: false});
    assert.ok(off.impact.rows.find(row => row.id === pending.id)?.needsAttention);
    assert.ok(!(await api('/api/v2/me/workspaces', {token: existing})).value.some(row => row.workspace.id === classId));
    await api(getProfilePath(other.id), {token: multiSchool});
    const stillPending = await signIn(pendingEmail);
    assert.ok(!(await api('/api/v2/me/workspaces', {token: stillPending})).value.some(row => row.workspace.id === classId));
    assert.equal((await prisma.workspaceMemberInvite.findUniqueOrThrow({where: {id: pending.id}})).claimedAt, null);
    await update({teacherAuthMode: 'OAUTH_EMAIL'});
    const claimed = await signIn(pendingEmail);
    await opensClass(api, claimed, classId);
    await opensClass(api, existing, classId);
    assert.ok((await prisma.workspaceMemberInvite.findUniqueOrThrow({where: {id: pending.id}})).claimedAt);
    return {isolatedHlyUsingRealCallback: true, disabledFallbackBlockedClaimAndDirectClassAccess: true,
      primaryOAuthAllowedClaimAndAccess: true, independentOtherSchoolManagement: true};
  } finally { await provider.close(); }
}

async function renameFromEdge({web, directory, evidence, origin, school, owner}) {
  const require = createRequire(join(web, 'package.json'));
  const {chromium} = require('@playwright/test');
  const {preview} = await import(pathToFileURL(join(dirname(require.resolve('vite/package.json')), 'dist/node/index.js')).href);
  const outDir = join(directory, 'profile-web');
  const frontend = await preview({configFile: false, root: web, build: {outDir},
    preview: {host: '127.0.0.1', port: 0, proxy: {'/api': origin, '/accounts': origin}}});
  const url = 'http://127.0.0.1:' + frontend.httpServer.address().port;
  try {
    await new Promise((resolve, reject) => {
      const child = spawn(process.execPath, ['node_modules/vite/bin/vite.js', 'build', '--logLevel', 'error', '--outDir', outDir],
        {cwd: web, env: {...process.env, VITE_DEFAULT_KV_SERVER: url, VITE_DEFAULT_AUTH_SERVER: url, VITE_SERVER_URL: url,
          VITE_ENABLE_ANALYTICS: 'false'}, windowsHide: true, stdio: 'inherit'});
      child.once('error', reject);
      child.once('exit', code => code === 0 ? resolve() : reject(new Error('Legacy web build failed')));
    });
    const browser = await chromium.launch({channel: 'msedge', headless: true});
    try {
      const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
      await page.route('**/*', route => new URL(route.request().url()).origin === url ? route.continue() : route.abort());
      await page.addInitScript(({access_token, refresh_token}) => {
        localStorage.setItem('classworks-v2-access-token', access_token);
        localStorage.setItem('classworks-v2-refresh-token', refresh_token);
      }, owner);
      await page.goto(url + '/classworks-admin?section=structure&school=' + school.id);
      await page.getByLabel('学校名称').fill('新版网页已更新校名');
      const saved = page.waitForResponse(response => response.url().endsWith('/profile') && response.request().method() === 'PATCH');
      await page.getByRole('button', {name: '保存学校设置', exact: true}).click();
      assert.equal((await saved).status(), 200);
      await page.getByText('学校基础设置已保存。').waitFor();
      await page.screenshot({path: join(evidence, 'edge-school-profile.png')});
    } finally { await browser.close(); }
  } finally {
    frontend.httpServer.closeAllConnections();
    await frontend.close();
  }
}
