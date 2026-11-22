import assert from 'node:assert/strict';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';
import {mkdir, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {verifyStudentBrowser} from './course-groups-fixture.mjs';

export async function verifyTeacherAllocation({prisma, origin, fixture, fromKv, directory, evidence, web, bcrypt, runDesktop}) {
  await mkdir(evidence, {recursive: true});
  const {school, term, other, pin, managerId} = fixture;
  const teacher = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer', schoolMemberships: {some: {schoolId: school.id}}}});
  const grade = await prisma.grade.findFirstOrThrow({where: {termId: term.id, code: 'G2'}});
  const draft = await prisma.academicTerm.create({data: {schoolId: school.id, academicYear: 2027, semester: 1,
    name: '开学准备草稿', status: 'DRAFT'}});
  const draftGrade = await prisma.grade.create({data: {termId: draft.id, code: 'G2', name: '高二'}});
  const oauthSchool = await prisma.school.update({where: {id: other.id}, data: {name: '邮箱试验学校', teacherAuthMode: 'OAUTH_EMAIL'}});
  await prisma.schoolMember.create({data: {schoolId: oauthSchool.id, accountId: managerId, role: 'ADMIN'}});
  const oauthTerm = await prisma.academicTerm.create({data: {schoolId: oauthSchool.id, academicYear: 2026, semester: 1,
    name: '邮箱学校秋季学期', status: 'ACTIVE'}});
  const oauthGrade = await prisma.grade.create({data: {termId: oauthTerm.id, code: 'G2', name: '高二'}});
  const sharedPassword = randomBytes(12).toString('base64url');
  const shared = await prisma.school.create({data: {code: 'SHARED', name: '通用口令试验学校', teacherAuthMode: 'SHARED_PASSWORD',
    teacherSharedPasswordHash: await bcrypt.hash(sharedPassword, 10)}});
  await prisma.schoolMember.create({data: {schoolId: shared.id, accountId: managerId, role: 'ADMIN'}});
  const physics = await prisma.subject.create({data: {schoolId: school.id, code: 'PHY', name: '物理', category: 'CORE'}});
  const room = (targetTerm, targetGrade, code, name, type = 'ADMIN_CLASS', subjectId = null) =>
    prisma.workspace.create({data: {termId: targetTerm.id, gradeId: targetGrade.id, code, name, type, subjectId}});
  const [seven, eight, a1, draftSeven, draftEight, oauthSeven, oauthEight] = await Promise.all([
    room(term, grade, 'G2-C07', '高二（07）班'), room(term, grade, 'G2-C08', '高二（08）班'),
    room(term, grade, 'G2-PHY-A1', '物理 A1', 'COURSE_GROUP', physics.id),
    room(draft, draftGrade, 'G2-C07', '高二（07）班草稿'), room(draft, draftGrade, 'G2-C08', '高二（08）班草稿'),
    room(oauthTerm, oauthGrade, 'G2-C07', '邮箱高二七班'), room(oauthTerm, oauthGrade, 'G2-C08', '邮箱高二八班'),
  ]);
  await prisma.workspaceSourceClass.create({data: {workspaceId: a1.id, administrativeClassId: seven.id}});
  await prisma.administrativeClassSubject.createMany({data: [
    {administrativeClassId: seven.id, subjectId: physics.id, deliveryMode: 'COURSE_GROUP', isCompulsory: true},
    {administrativeClassId: eight.id, subjectId: physics.id, deliveryMode: 'ADMIN_CLASS', isCompulsory: true}
  ]});
  await prisma.administrativeClassLeadership.create({data: {administrativeClassId: seven.id, accountId: teacher.id, position: 'HEAD_TEACHER'}});
  await prisma.workspaceMember.createMany({data: [
    {workspaceId: seven.id, accountId: teacher.id, role: 'TEACHER'},
    {workspaceId: eight.id, accountId: teacher.id, role: 'VIEWER'},
    {workspaceId: a1.id, accountId: teacher.id, role: 'TEACHER'}
  ]});
  await prisma.teachingAssignment.create({data: {workspaceId: a1.id, subjectId: physics.id, accountId: teacher.id, position: 'PRIMARY'}});
  const teachingBefore = await prisma.teachingAssignment.findMany({where: {workspace: {termId: term.id}}});

  async function api(path, {token, method = 'GET', body, status = 200} = {}) {
    const response = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
      ...(token ? {Authorization: 'Bearer ' + token} : {})}, body: body === undefined ? undefined : JSON.stringify(body)});
    const payload = await response.json();
    assert.equal(response.status, status, path + ': ' + (payload.code ?? response.status));
    return payload.data ?? payload;
  }
  const localLogin = (username, password = pin, schoolCode = school.code, status = 200) =>
    api('/accounts/local/login', {method: 'POST', body: {schoolCode, username, password}, status});
  const [manager, browserManager, originalLogin] = await Promise.all(['admin', 'admin2', 'viewer'].map(username => localLogin(username)));
  const accountAfterLogin = await prisma.account.findUniqueOrThrow({where: {id: teacher.id}});
  const originalSession = originalLogin.access_token;
  const newPin = String(pin === '268240' ? 268241 : 268240);
  const ignoredPin = String(pin === '682492' ? 682493 : 682492);
  const path = schoolId => '/api/v2/admin/schools/' + schoolId + '/teacher-allocation';
  const primaryPath = path(school.id), oauthPath = path(oauthSchool.id);
  const inviteEmail = 'claim-teacher@example.invalid', revokeEmail = 'revoke-teacher@example.invalid';
  const state = {steps: [], stale: 0, lost: false, receiptReads: 0, oauthCodes: []};
  let browser, frontend;
  const probe = await isolatedIdentityProvider(fromKv, origin);
  const myWorkspaces = (token, targetTerm) => api('/api/v2/me/workspaces' + (targetTerm ? '?termId=' + targetTerm : ''), {token});
  const memberKey = (workspaceId, accountId) => ({workspaceId_accountId: {workspaceId, accountId}});
  const findMember = (board, account = teacher.id) => prisma.workspaceMember.findUnique({where: memberKey(board.id, account)});
  const findTeacher = username => prisma.account.findUnique({where: {provider_providerId: {provider: 'school-local', providerId: school.code + ':' + username}}});

  async function oldWebRemove() {
    const page = await browser.newPage({viewport: {width: 1360, height: 900}, serviceWorkers: 'block'});
    try {
      await page.route('**/*', route => new URL(route.request().url()).origin === frontend ? route.continue() : route.abort());
      await page.addInitScript(({access, refresh}) => {
        localStorage.setItem('classworks-v2-access-token', access);
        localStorage.setItem('classworks-v2-refresh-token', refresh);
      }, {access: browserManager.access_token, refresh: browserManager.refresh_token});
      await page.goto(frontend + '/classworks-admin?section=teachers&school=' + school.id + '&term=' + term.id);
      await page.locator('.v-text-field').filter({hasText: '搜索班级、科目或教师'}).locator('input').fill(eight.code);
      const row = page.locator('.v-list-item').filter({hasText: eight.code});
      const chip = row.locator('.v-chip').filter({hasText: /viewer.*只读/});
      await chip.locator('.v-chip__close').click();
      const confirm = page.getByRole('dialog').filter({hasText: '移除任课关系？'});
      const reply = page.waitForResponse(res => res.request().method() === 'DELETE' &&
        res.url().endsWith('/workspaces/' + eight.id + '/members/' + teacher.id) && res.ok());
      await confirm.getByRole('button', {name: '确认移除'}).click();
      await reply;
      await chip.waitFor({state: 'hidden'});
      await page.screenshot({path: join(evidence, 'edge-legacy-membership-removed.png'), animations: 'disabled'});
    } catch (error) {
      await page.screenshot({path: join(evidence, 'edge-legacy-failure.png')}).catch(() => {});
      throw error;
    } finally { await page.close(); }
  }

  async function independentSharedLogin() {
    const body = {termId: null, termStatus: null, gradeId: null, mode: 'SHARED_PASSWORD',
      teachers: [{username: 'shared-teacher', name: '通用教师', access: []}]};
    const token = manager.access_token;
    const preview = await api(path(shared.id) + '/preview', {method: 'POST', token, body});
    assert.ok(preview.canApply);
    const result = await api(path(shared.id), {method: 'POST', token, body: {...body, requestId: randomUUID(),
      expectedEvidence: preview.evidence, expectedVersion: preview.version}});
    const account = await prisma.account.findUniqueOrThrow({where: {id: result.receipt.teachers[0].accountId}});
    assert.equal(account.localPasswordHash, null);
    const login = await localLogin('shared-teacher', sharedPassword, shared.code);
    assert.equal(login.account.id, account.id);
    assert.deepEqual(await myWorkspaces(login.access_token), []);
  }

  async function checkpoint(step) {
    switch (step) {
    case 'draft-access': {
      assert.equal((await findMember(draftEight)).role, 'ASSISTANT');
      assert.equal((await findMember(eight)).role, 'VIEWER');
      const visible = await myWorkspaces(originalSession, draft.id);
      assert.ok(visible.some(row => row.workspace.id === draftEight.id));
      assert.equal((await prisma.academicTerm.findUniqueOrThrow({where: {id: draft.id}})).status, 'DRAFT');
      break;
    }
    case 'legacy-remove':
      await oldWebRemove();
      break;
    case 'document-scope-zero':
      assert.equal(await findTeacher('wrong-scope-probe'), null);
      assert.equal(await prisma.workspaceMember.count({where: {workspaceId: {in: [seven.id, draftSeven.id]}}}), 1);
      assert.equal((await findMember(eight)).role, 'VIEWER');
      assert.equal((await findMember(draftEight)).role, 'ASSISTANT');
      break;
    case 'stale-zero':
      assert.equal(state.stale, 1);
      assert.equal(await findTeacher('new-physics'), null);
      assert.equal(await findMember(eight), null);
      assert.equal((await findMember(seven)).role, 'TEACHER');
      break;
    case 'primary-applied': {
      assert.ok(state.lost && state.receiptReads === 1);
      const existing = await prisma.account.findUniqueOrThrow({where: {id: teacher.id}});
      for (const key of ['id', 'tokenVersion', 'localPasswordHash', 'localLoginFailures', 'localLockedUntil'])
        assert.deepEqual(existing[key], accountAfterLogin[key]);
      assert.equal((await findMember(eight)).role, 'ASSISTANT');
      assert.equal((await findMember(seven)).role, 'TEACHER');
      assert.equal((await findMember(a1)).role, 'TEACHER');
      assert.deepEqual(await prisma.teachingAssignment.findMany({where: {workspace: {termId: term.id}}}), teachingBefore);
      assert.ok((await myWorkspaces(originalSession)).some(row => row.workspace.id === eight.id && row.role === 'ASSISTANT'));
      const created = await localLogin('new-physics', newPin);
      assert.deepEqual((await myWorkspaces(created.access_token)).map(row => row.workspace.id).sort(), [seven.id, a1.id].sort());
      await localLogin('viewer', ignoredPin, school.code, 401);
      await localLogin('viewer', pin);
      break;
    }
    case 'duty-only':
      assert.equal(await findMember(seven), null);
      assert.ok((await myWorkspaces(originalSession)).some(row => row.workspace.id === seven.id &&
        row.role === 'TEACHER' && row.responsibilityDerived));
      assert.deepEqual(await prisma.teachingAssignment.findMany({where: {workspace: {termId: term.id}}}), teachingBefore);
      break;
    case 'invitation-pending':
      assert.equal(await prisma.workspaceMemberInvite.count({where: {workspaceId: {in: [oauthSeven.id, oauthEight.id]}, claimedAt: null}}), 2);
      break;
    case 'oauth-complete': {
      const claimed = await probe.begin(inviteEmail);
      assert.equal(await prisma.workspaceMemberInvite.count({where: {workspaceId: oauthSeven.id, claimedAt: null}}), 1,
        'provider callback alone cannot grant before proof of app handoff');
      const rejected = await probe.exchange(claimed.code, randomBytes(32).toString('base64url'), 401);
      assert.equal(rejected.code, 'OAUTH_HANDOFF_INVALID');
      assert.equal(await prisma.workspaceMemberInvite.count({where: {workspaceId: oauthSeven.id, claimedAt: null}}), 1);
      const accepted = await probe.exchange(claimed.code, claimed.verifier);
      assert.deepEqual((await myWorkspaces(accepted.access_token)).map(row => row.workspace.id), [oauthSeven.id]);
      const revoked = await probe.begin(revokeEmail);
      const reopened = await probe.exchange(revoked.code, revoked.verifier);
      assert.deepEqual(await myWorkspaces(reopened.access_token), []);
      assert.equal(await prisma.workspaceMemberInvite.count({where: {workspaceId: oauthEight.id}}), 0);
      state.oauthCodes.push('callback-pending', 'wrong-browser-denied', 'verified-claimed', 'revoked-no-access');
      break;
    }
    default: throw new Error('unknown teacher allocation checkpoint');
    }
    state.steps.push(step);
  }

  const upstreamOrigin = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__allocation/')) {
      try { await checkpoint(req.url.slice('/__allocation/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'GET' && req.url.startsWith(primaryPath + '/receipts/')) state.receiptReads++;
    const upstream = request(new URL(req.url, upstreamOrigin), {method: req.method, headers: {...req.headers, host: upstreamOrigin.host}}, reply => {
      if (req.method !== 'POST' || req.url !== primaryPath || reply.statusCode !== 200) {
        res.writeHead(reply.statusCode, reply.headers); reply.pipe(res);
        return;
      }
      const chunks = [];
      reply.on('data', chunk => chunks.push(chunk));
      reply.on('end', () => {
        const response = Buffer.concat(chunks), result = JSON.parse(response).data;
        if (result?.state === 'STALE') state.stale++;
        if (!state.lost && result?.state === 'APPLIED' && result.receipt.termStatus === 'ACTIVE') {
          state.lost = true;
          res.destroy();
          return;
        }
        res.writeHead(reply.statusCode, reply.headers);
        res.end(response);
      });
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  try {
    await independentSharedLogin();
    const desktopAccess = join(directory, 'allocation-desktop.json');
    const wrongTermFile = join(directory, 'different-term.json'), wrongSchoolFile = join(directory, 'different-school.json');
    const matchingFile = join(directory, 'matching-active-term.json');
    const wrongScopeRows = [{username: 'wrong-scope-probe', name: '范围核对教师', pin: newPin, role: 'TEACHER', workspaceCodes: ['G2-C07']}];
    await Promise.all([
      writeFile(wrongTermFile, JSON.stringify({schoolId: school.id, termId: draft.id, gradeId: draftGrade.id, assignments: wrongScopeRows})),
      writeFile(wrongSchoolFile, JSON.stringify({schoolId: oauthSchool.id, termId: oauthTerm.id, assignments: wrongScopeRows})),
      writeFile(matchingFile, JSON.stringify({schoolId: school.id, termId: term.id, assignments: [
        {username: 'new-physics', name: '陈老师', pin: 'abc', role: 'TEACHER', workspaceCodes: ['G2-C07', 'G2-PHY-A1']},
        {username: 'viewer', name: 'viewer', pin: ignoredPin, role: 'ASSISTANT', workspaceCodes: ['G2-C08']}
      ]}))
    ]);
    await writeFile(desktopAccess, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, password: pin, draftId: draft.id,
      oauthSchoolId: oauthSchool.id, newPin, ignoredPin, inviteEmail, revokeEmail, wrongTermFile, wrongSchoolFile, matchingFile,
      allowAllocationWrites: true}), {mode: 0o600});
    await verifyStudentBrowser({origin, web, directory, evidence, school, rooms: [], afterStudent: async (b, webOrigin) => {
      browser = b; frontend = webOrigin;
      await runDesktop(['--live-teacher-allocation', desktopAccess, evidence]);
    }});
    assert.deepEqual(state.steps, ['draft-access', 'document-scope-zero', 'legacy-remove', 'stale-zero', 'primary-applied', 'duty-only', 'invitation-pending', 'oauth-complete']);
    await writeFile(join(evidence, 'teacher-allocation-cross-end.json'), JSON.stringify({passed: true, ...state,
      sharedLocalLogin: true, originalLocalSession: 'valid', retainedDuty: 'HEAD_TEACHER', unchangedTeachingRows: teachingBefore.length,
      oauthProvider: 'loopback HLY fixture, real KV OAuth callback and browser-bound exchange routes'}, null, 2));
    console.log('PASS: Windows teacher allocation, Edge legacy mutation, actual local logins and OAuth callback/exchange.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
    await probe.close();
  }
}

export async function isolatedIdentityProvider(fromKv, origin, {allowBrowserFixtureIdentity = false} = {}) {
  const {oauthProviders} = await fromKv('config/oauth.js');
  const provider = oauthProviders.hly;
  const previous = {...provider}, previousBase = process.env.BASE_URL;
  const authorization = new Map(), accessTokens = new Map();
  const server = createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://localhost');
      if (url.pathname === '/authorize') {
        const code = randomUUID();
        const browserIdentity = allowBrowserFixtureIdentity ? /(?:^|;\s*)np_fixture_identity=([^;]+)/.exec(req.headers.cookie ?? '')?.[1] : null;
        const email = url.searchParams.get('test_identity') ?? (browserIdentity && decodeURIComponent(browserIdentity));
        assert.ok(email);
        assert.equal(url.searchParams.get('client_id'), 'isolated-teacher-oidc');
        assert.equal(url.searchParams.get('code_challenge_method'), 'S256');
        authorization.set(code, {email, challenge: url.searchParams.get('code_challenge')});
        const redirect = new URL(url.searchParams.get('redirect_uri'));
        assert.equal(redirect.origin, origin);
        redirect.searchParams.set('state', url.searchParams.get('state'));
        redirect.searchParams.set('code', code);
        res.writeHead(302, {location: redirect.href}).end();
      } else if (url.pathname === '/token') {
        let raw = '';
        for await (const chunk of req) raw += chunk;
        const body = new URLSearchParams(raw), entry = authorization.get(body.get('code'));
        assert.ok(entry);
        assert.equal(createHash('sha256').update(body.get('code_verifier')).digest('base64url'), entry.challenge);
        authorization.delete(body.get('code'));
        const token = randomUUID();
        accessTokens.set(token, entry.email);
        res.writeHead(200, {'content-type': 'application/json'}).end(JSON.stringify({access_token: token}));
      } else if (url.pathname === '/userinfo') {
        const email = accessTokens.get(req.headers.authorization?.slice('Bearer '.length));
        assert.ok(email);
        res.writeHead(200, {'content-type': 'application/json'}).end(JSON.stringify({sub: email, email, email_verified: true,
          name: email.startsWith('claim-') ? '已认领教师' : '撤回后登录教师'}));
      } else res.writeHead(404).end();
    } catch (error) { console.error(error); res.writeHead(400).end(); }
  });
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
  const issuer = 'http://127.0.0.1:' + server.address().port;
  Object.assign(provider, {clientId: 'isolated-teacher-oidc', clientSecret: 'synthetic-test-only', authorizationURL: issuer + '/authorize',
    tokenURL: issuer + '/token', userInfoURL: issuer + '/userinfo'});
  process.env.BASE_URL = origin;
  return {
    issuer,
    async begin(email) {
      const verifier = randomBytes(32).toString('base64url');
      const challenge = createHash('sha256').update(verifier).digest('base64url');
      const start = await fetch(origin + '/accounts/oauth/hly?handoff_challenge=' + challenge, {redirect: 'manual'});
      assert.equal(start.status, 302);
      const chooser = new URL(start.headers.get('location'));
      chooser.searchParams.set('test_identity', email);
      const step = await fetch(chooser, {redirect: 'manual'});
      assert.equal(step.status, 302);
      const finish = await fetch(step.headers.get('location'), {redirect: 'manual',
        headers: {Cookie: start.headers.getSetCookie()[0].split(';')[0]}});
      assert.equal(finish.status, 302);
      const handoff = new URL(finish.headers.get('location'));
      assert.equal(handoff.searchParams.get('success'), 'true');
      assert.ok(handoff.searchParams.get('oauth_code') && !handoff.searchParams.has('access_token'));
      return {code: handoff.searchParams.get('oauth_code'), verifier};
    },
    async exchange(code, verifier, status = 200) {
      const response = await fetch(origin + '/accounts/oauth/exchange', {method: 'POST',
        headers: {'Content-Type': 'application/json'}, body: JSON.stringify({code, verifier})});
      assert.equal(response.status, status);
      return response.json();
    },
    async close() {
      Object.assign(provider, previous);
      if (previousBase === undefined) delete process.env.BASE_URL; else process.env.BASE_URL = previousBase;
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
    }
  };
}
