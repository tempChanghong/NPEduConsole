import assert from 'node:assert/strict';
import {randomBytes, randomUUID, createHash} from 'node:crypto';
import {fork, spawn} from 'node:child_process';
import {mkdir, mkdtemp, readFile, readdir, realpath, rm, writeFile} from 'node:fs/promises';
import {createServer, request} from 'node:http';
import {dirname, join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {verifyFreshExportAuthentication, verifyLegacyTransferWeb} from './school-transfer-browser.mjs';

const setupPath = '/api/v2/setup';
const mime = 'application/vnd.npclassworks.transfer+json';
const makeDigest = bytes => createHash('sha256').update(bytes).digest('hex');
const credentials = () => randomBytes(20).toString('base64url');
const phrase = () => '迁入-' + credentials();

async function json(origin, path, {body, method = body === undefined ? 'GET' : 'POST', token, setupToken, status = 200} = {}) {
  const reply = await fetch(origin + path, {method, headers: {'Content-Type': 'application/json',
    ...(token ? {Authorization: 'Bearer ' + token} : {}), ...(setupToken ? {'X-Classworks-Setup-Token': setupToken} : {})},
    body: body === undefined ? undefined : JSON.stringify(body)});
  const message = await reply.json();
  if (status !== null) assert.equal(reply.status, status, path + ' ' + (message.code ?? message.error?.code ?? ''));
  return {status: reply.status, data: message.data ?? message};
}

async function login(origin, schoolCode, username, password) {
  return (await json(origin, '/accounts/local/login', {body: {schoolCode, username, password}})).data.access_token;
}

export async function verifyReleasedMigrationRetirement(context) {
  const {prisma, origin, fixture, directory, evidence, fromKv, bcrypt, kvRequire, zip, manifestPath} = context;
  const {school, other, term, managerId, pin, classes: [classId]} = fixture;
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const zipBytes = await readFile(zip);
  assert.equal(manifest.version, '0.1.8');
  assert.equal(manifest.sha256, makeDigest(zipBytes));
  assert.equal(manifest.sizeBytes, zipBytes.length);
  await mkdir(evidence, {recursive: true});
  await prisma.schoolMember.update({where: {schoolId_accountId: {schoolId: school.id, accountId: managerId}}, data: {role: 'OWNER'}});
  const {localProviderId} = await fromKv('domain/localAccount.js');
  const teacher = await prisma.account.findUniqueOrThrow({where: {provider_providerId: {
    provider: 'school-local', providerId: localProviderId(school.code, 'viewer')}}});
  await prisma.workspaceMember.create({data: {workspaceId: classId, accountId: teacher.id, role: 'TEACHER'}});
  for (const [memberSchool, username, role] of [[school, 'read-admin', 'ADMIN'], [other, 'empty-owner', 'OWNER']]) {
    const account = await prisma.account.create({data: {provider: 'school-local', providerId: localProviderId(memberSchool.code, username),
      localUsername: username, name: username, localPasswordHash: await bcrypt.hash(pin, 10), providerData: {schoolCode: memberSchool.code}}});
    await prisma.schoolMember.create({data: {schoolId: memberSchool.id, accountId: account.id, role}});
  }
  const file = join(await realpath(evidence), `released-owner-export-${randomUUID()}.npcw-transfer`);
  const passphrase = 'np-check-' + credentials();
  const migrationPath = id => `/api/v2/admin/schools/${id}/migration/`;
  const readonly = await login(origin, school.code, 'read-admin', pin);
  assert.equal((await json(origin, migrationPath(school.id) + 'readiness', {token: readonly})).data.role, 'ADMIN');
  await json(origin, migrationPath(school.id) + 'export', {token: readonly, body: {currentPin: pin, passphrase}, status: 403});
  const inactive = await login(origin, other.code, 'empty-owner', pin);
  assert.equal((await json(origin, migrationPath(other.id) + 'readiness', {token: inactive})).data.hasActiveTerm, false);
  const denied = await json(origin, migrationPath(other.id) + 'export', {token: inactive, body: {currentPin: pin, passphrase}, status: 422});
  assert.equal(denied.data.code, 'MIGRATION_ACTIVE_TERM_MISSING');
  const owner = await login(origin, school.code, 'admin', pin);
  const target = await startTarget(context);
  const {Client} = kvRequire('pg');
  const targetDb = new Client({connectionString: target.databaseUrl});
  let packageProof;
  try {
    await targetDb.connect();
    assert.equal((await targetDb.query('SELECT count(*)::int AS count FROM "School"')).rows[0].count, 0);
    const outcome = await verifyLegacyTransferWeb({...context, school, token: owner, pin, file, passphrase, target,
      pilot: {manifest, zip, classId, teacher: teacher.localUsername, async exportPackage() {
        const access = join(directory, 'release-transfer-fixture.json');
        await writeFile(access, JSON.stringify({isolated: true, login: {url: origin, website: origin, code: school.code,
          username: 'admin', pin}, transfer: {file, passphrase}}));
        try {
          await new Promise((complete, reject) => {
            const child = spawn('pwsh.exe', ['-NoProfile', '-STA', '-File', fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url)),
              '-ManagementLogin', '-FixturePath', access, '-ZipPath', zip, '-ManifestPath', manifestPath, '-EvidenceDirectory', evidence],
            {windowsHide: true, stdio: 'inherit'});
            child.once('error', reject);
            child.once('exit', code => code === 0 ? complete() : reject(Error(`The release export check exited (${code})`)));
          });
        } finally {
          await rm(access, {force: true});
        }
        const packageBytes = await readFile(file);
        const {decryptMigrationPackage} = await fromKv('services/schoolMigrationService.js');
        const {manifest: exported} = await decryptMigrationPackage(packageBytes, passphrase);
        assert.equal(exported.school.id, school.id);
        packageProof = {digest: makeDigest(packageBytes), size: packageBytes.length, format: exported.formatVersion,
          id: exported.migrationId};
        return packageProof.id;
      }}});
    assert.equal(outcome.migrationId, packageProof.id);
    assert.equal(outcome.schoolId, school.id);
    const receipt = await targetDb.query('SELECT "migrationId", "packageDigest" FROM "InstanceSchoolTransfer" WHERE "migrationId"=$1', [outcome.migrationId]);
    assert.equal(receipt.rows[0].packageDigest, packageProof.digest);
    const original = await targetDb.query('SELECT id FROM "Workspace" WHERE id=$1 AND "termId"=$2', [classId, term.id]);
    assert.equal(original.rowCount, 1);
    assert.equal((await targetDb.query('SELECT count(*)::int AS count FROM "School"')).rows[0].count, 1);
    const targetSetup = await json(target.origin, setupPath + '/status');
    assert.equal(targetSetup.data.state, 'COMPLETED');
    await writeFile(join(evidence, 'released-migration-result.json'), JSON.stringify({releaseSha256: manifest.sha256, releaseBytes: manifest.sizeBytes,
      packageSha256: packageProof.digest, packageBytes: packageProof.size, exportFormat: packageProof.format, sourceSchoolId: school.id,
      sourceTermId: term.id, targetWorkspaceId: classId, adminReadOnly: true, noActiveTermBlocked: true, ...outcome}, null, 2));
    await verifyFreshExportAuthentication({...context, school, passphrase});
    console.log('PASS: the released EXE exported; Edge restored the identical package and the original teacher entered the original class.');
  } finally {
    await rm(file, {force: true});
    await targetDb.end();
    await target.close();
  }
}

export async function verifyConsoleFirstSchool({prisma, origin, directory, evidence, runDesktop}) {
  await mkdir(evidence, {recursive: true});
  const path = join(directory, 'first-school.json');
  const pin = '761298', schoolCode = 'NEW-CONSOLE';
  const gateways = await firstSchoolGateways(origin);
  try {
    await writeFile(path, JSON.stringify({sourceOrigin: gateways.same, splitWebsite: gateways.web, splitApi: gateways.api,
      setupKey: process.env.BOOTSTRAP_SETUP_KEY, pin, schoolCode, isolated: true}));
    await runDesktop(['--live-first-school-console', path, evidence]);
  } finally { await gateways.close(); }
  const checks = gateways.counts;
  assert.deepEqual(checks.sessions, {same: 2, api: 1}, 'only confirmed gateways receive a key');
  assert.equal(checks.expired, 1, 'one expired session response was injected');
  assert.equal(checks.initialized, 1, 'one real core initialization despite the lost reply');
  assert.equal(checks.completed, 1, 'one real owner completion despite the lost reply');
  assert.ok(checks.contextReads >= 1, 'reopening reads the original school from the server');
  assert.deepEqual(checks.protectedWrites, {initialize: true, complete: true});
  assert.deepEqual(checks.recoveryReads, {initialize: 'cancelled', complete: 'returned'});
  const school = await prisma.school.findUniqueOrThrow({where: {code: schoolCode}});
  const owner = await prisma.schoolMember.findFirstOrThrow({where: {schoolId: school.id, role: 'OWNER'}});
  const setup = await prisma.instanceSetup.findUniqueOrThrow({where: {id: 'default'}});
  assert.equal(setup.completedByAccountId, owner.accountId);
  const proof = await prisma.$queryRaw`SELECT method FROM "SchoolManagementLogin" WHERE "accountId"=${owner.accountId}
      AND "schoolId"=${school.id}`;
  assert.ok(proof.some(row => row.method === 'LOCAL_PIN'));
  const term = await prisma.academicTerm.findFirstOrThrow({where: {schoolId: school.id, status: 'ACTIVE'}});
  const room = await prisma.workspace.findFirstOrThrow({where: {termId: term.id, code: 'G1-1'}});
  assert.equal(room.type, 'ADMIN_CLASS');
  await login(origin, schoolCode, 'founder', pin);
  await writeFile(join(evidence, 'first-school-database.json'), JSON.stringify({schoolCreated: true, termCreated: true,
    ownerLoginProof: true, setupCompleted: true, schoolId: school.id, termId: term.id, firstClassId: room.id, gateways: checks}, null, 2));
}

async function firstSchoolGateways(origin) {
  const counts = {sessions: {same: 0, api: 0}, expired: 0, initialized: 0, completed: 0, contextReads: 0, recoveryReads: {}, protectedWrites: {}};
  const held = new Map();
  let holdNextStatus;
  let splitApi;
  let delayNextSplit = false;
  let apiResponse = 'live';
  let sameDeclaration = 'same';
  const gates = [];
  async function open(kind) {
    const server = createServer((req, res) => {
      if (kind === 'api' && req.url === '/__oobe/delay-next-declaration') {
        delayNextSplit = true;
        res.writeHead(204).end();
        return;
      }
      if (kind === 'api' && req.url?.startsWith('/__oobe/api-response/')) {
        apiResponse = req.url.slice('/__oobe/api-response/'.length);
        res.writeHead(204).end();
        return;
      }
      if (kind === 'api' && req.url?.startsWith('/__oobe/same-declaration/')) {
        sameDeclaration = req.url.slice('/__oobe/same-declaration/'.length);
        res.writeHead(204).end();
        return;
      }
      if (kind === 'api' && req.url?.startsWith('/__oobe/recovery/')) {
        const [action, phase] = req.url.slice('/__oobe/recovery/'.length).split('/');
        const answer = held.get(phase);
        if (action === 'release' && answer) {
          answer();
          held.delete(phase);
        }
        res.writeHead(answer ? 204 : 404).end();
        return;
      }
      if (kind === 'api' && req.url === setupPath + '/status' && apiResponse !== 'live') {
        if (apiResponse === 'closed') res.destroy();
        else res.writeHead(apiResponse === 'missing' ? 404 : 200, {'Content-Type': 'application/json'})
          .end(JSON.stringify({success: true, data: {state: 'UNRECOGNIZED'}}));
        return;
      }
      if (req.url === '/.well-known/npclassworks-deployment.json' && kind !== 'api') {
        if (kind === 'same' && sameDeclaration === 'missing') {
          res.writeHead(404).end();
          return;
        }
        const send = () => res.writeHead(200, {'Content-Type': 'application/json', 'Cache-Control': 'no-store'})
          .end(JSON.stringify({schemaVersion: 1, apiBaseUrl: kind === 'web' || sameDeclaration === 'changed' ? splitApi : null}));
        if (kind === 'web' && delayNextSplit) {
          delayNextSplit = false;
          setTimeout(send, 1100);
        } else send();
        return;
      }
      if (kind === 'web' || !/^\/(api|accounts|socket\.io)(\/|$)/.test(req.url ?? '')) {
        res.writeHead(200, {'Content-Type': 'text/html'}).end('<!doctype html><title>Isolated frontend gateway</title>');
        return;
      }
      if (req.method === 'POST' && req.url === setupPath + '/session') counts.sessions[kind]++;
      if (req.method === 'POST' && req.url === setupPath + '/initialize' && !counts.expired) {
        counts.expired++;
        req.resume();
        res.writeHead(401, {'Content-Type': 'application/json'})
          .end(JSON.stringify({success: false, code: 'SETUP_TOKEN_EXPIRED', message: '初始化会话已过期'}));
        return;
      }
      const recovery = req.method === 'GET' && req.url === setupPath + '/status' ? holdNextStatus : null;
      if (recovery) holdNextStatus = null;
      const upstream = request(origin + req.url, {method: req.method, headers: req.headers}, reply => {
        if (req.url === setupPath + '/initialize' && reply.statusCode === 201 ||
            req.url === setupPath + '/desktop-complete' && reply.statusCode === 200) {
          const phase = req.url.endsWith('/initialize') ? 'initialize' : 'complete';
          if (phase === 'initialize') counts.initialized++;
          else counts.completed++;
          reply.resume();
          held.set(phase + '-write', () => {
            counts.protectedWrites[phase] = !res.destroyed;
            holdNextStatus = phase;
            res.destroy();
          });
          return;
        }
        if (recovery) {
          deferReply(reply, res, send => held.set(recovery, () => { counts.recoveryReads[recovery] = send(); }));
          return;
        }
        if (req.url === setupPath + '/context' && reply.statusCode === 200) counts.contextReads++;
        res.writeHead(reply.statusCode, reply.headers);
        reply.pipe(res);
      });
      upstream.on('error', () => res.destroy());
      req.pipe(upstream);
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    gates.push(server);
    return 'http://127.0.0.1:' + server.address().port;
  }
  splitApi = await open('api');
  const same = await open('same'), web = await open('web');
  return {api: splitApi, same, web, counts, close: () => Promise.all(gates.map(server => {
    server.closeAllConnections();
    return new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  }))};
}

function deferReply(upstream, response, ready) {
  const chunks = [];
  upstream.on('data', chunk => chunks.push(chunk));
  upstream.on('end', () => ready(() => {
    if (response.destroyed) return 'cancelled';
    response.writeHead(upstream.statusCode, upstream.headers).end(Buffer.concat(chunks));
    return 'returned';
  }));
}

export async function startTarget({fixtureRunner, kv, binaries}) {
  const tail = [];
  const child = fork(fixtureRunner, [kv, binaries, '--school-transfer-target'], {silent: true, windowsHide: true});
  for (const stream of [child.stdout, child.stderr]) stream.on('data', bytes => {
    tail.push(String(bytes));
    if (tail.length > 20) tail.shift();
  });
  const data = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { child.send('stop', () => {}); reject(new Error('Target KV startup timed out')); }, 60_000);
    child.once('message', value => { clearTimeout(timer); resolve(value); });
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`Target KV exited (${code}).\n${tail.join('')}`)); });
  });
  const close = async () => {
    if (child.exitCode !== null) return;
    const exited = new Promise((resolve, reject) => child.once('exit', code => code === 0 ? resolve() :
      reject(new Error(`Target KV exit ${code}.\n${tail.join('')}`))));
    child.send('stop');
    await exited;
  };
  return {...data, close};
}

async function dropImportReply(origin) {
  let dropped = 0, reads = 0;
  let sendReceipt, released;
  const server = createServer((req, res) => {
    if (req.url === '/__migration-recovery/held') { res.writeHead(sendReceipt ? 204 : 404).end(); return; }
    if (req.url === '/__migration-recovery/release') {
      released = sendReceipt?.();
      sendReceipt = null;
      res.writeHead(released ? 204 : 404).end();
      return;
    }
    const upstream = request(origin + req.url, {method: req.method, headers: req.headers}, reply => {
      if (req.url?.endsWith('/migration/import') && reply.statusCode === 201 && !dropped) {
        dropped++;
        reply.resume();
        res.destroy();
        return;
      }
      if (req.url?.includes('/migration/receipts/') && reply.statusCode === 200 && ++reads === 1) {
        deferReply(reply, res, send => { sendReceipt = send; });
        return;
      }
      res.writeHead(reply.statusCode, reply.headers);
      reply.pipe(res);
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return {origin: 'http://127.0.0.1:' + server.address().port, counts: () => ({dropped, reads, released}),
    close() { server.closeAllConnections(); return new Promise(resolve => server.close(resolve)); }};
}

export async function verifyConsoleSchoolTransfer(context) {
  const {prisma, origin, fixture, fromKv, bcrypt, kvRequire, directory, evidence, runDesktop, runNative} = context;
  await mkdir(evidence, {recursive: true});
  const {school, other, term, managerId, pin, identity, classroomBearer, classes: [classId]} = fixture;
  await prisma.schoolMember.update({where: {schoolId_accountId: {schoolId: school.id, accountId: managerId}}, data: {role: 'OWNER'}});
  const ownerEpoch = randomUUID(), adminEpoch = randomUUID();
  await prisma.$executeRaw`INSERT INTO "SchoolManagementAuthority" ("schoolId","accountId","adminEpoch","ownerEpoch")
      VALUES (${school.id},${managerId},${adminEpoch}::uuid,${ownerEpoch}::uuid)`;
  const {localProviderId} = await fromKv('domain/localAccount.js');
  const teacher = await prisma.account.findUniqueOrThrow({where: {provider_providerId: {
    provider: 'school-local', providerId: localProviderId(school.code, 'viewer')}}});
  const historicalAdmin = await prisma.account.create({data: {provider: 'school-local',
    providerId: localProviderId(school.code, 'oldadmin'), localUsername: 'oldadmin', name: '原学校管理员',
    localPasswordHash: await bcrypt.hash(pin, 10)}});
  await prisma.schoolMember.create({data: {schoolId: school.id, accountId: historicalAdmin.id, role: 'ADMIN'}});
  const previousAdminEpoch = randomUUID();
  await prisma.$executeRaw`INSERT INTO "SchoolManagementAuthority" ("schoolId","accountId","adminEpoch")
      VALUES (${school.id},${historicalAdmin.id},${previousAdminEpoch}::uuid)`;
  await prisma.workspaceMember.create({data: {workspaceId: classId, accountId: teacher.id, role: 'TEACHER'}});
  const student = await prisma.administrativeClassStudent.create({data: {administrativeClassId: classId, name: '迁入名单样本'}});
  const subject = await prisma.subject.create({data: {schoolId: school.id, code: 'PHYS', name: '物理', category: 'CORE'}});
  const relation = await prisma.teachingAssignment.create({data: {workspaceId: classId, subjectId: subject.id, accountId: teacher.id}});
  const leadership = await prisma.administrativeClassLeadership.create({data: {administrativeClassId: classId, accountId: teacher.id}});
  const publication = await prisma.publication.create({data: {authorAccountId: teacher.id, type: 'NOTICE', title: '同校通知',
    content: '跨库对照记录', status: 'PUBLISHED', targets: {create: {workspaceId: classId}}}});
  const history = await prisma.publicationRevision.create({data: {publicationId: publication.id, revision: 1,
    snapshot: {title: publication.title, targetWorkspaceIds: [classId]}, action: 'CREATED', actorType: 'ACCOUNT',
    editorAccountId: teacher.id}});
  const audit = await prisma.auditLog.create({data: {schoolId: school.id, actorType: 'ACCOUNT', actorAccountId: teacher.id,
    action: 'TEST_ORIGINAL_SCOPE', summary: '迁移审计对照'}});
  const front = await prisma.classroomScreenBinding.findFirstOrThrow({where: {administrativeClassId: classId}});
  await prisma.classroomScreenBinding.update({where: {id: front.id}, data: {name: '前屏', loginCode: 'FRONT',
    pinHash: await bcrypt.hash(pin, 10), npepPairingEnabled: true}});
  const webScreenPath = '/api/v2/classroom-screens';
  const loginScreen = async targetOrigin => (await json(targetOrigin, webScreenPath + '/login', {body: {
    schoolCode: school.code, loginCode: 'front', pin, deviceFingerprint: 'transfer-' + randomUUID(),
  }})).data.token;
  const beforeWeb = await loginScreen(origin);
  const rear = await prisma.classroomScreenBinding.create({data: {schoolId: school.id, administrativeClassId: classId, name: '后屏',
    tokenHash: makeDigest(credentials()), createdByAccountId: managerId, npepPairingEnabled: true, npepPrepared: true}});
  const command = await prisma.classroomScreenCommand.create({data: {screenBindingId: front.id, issuedByAccountId: managerId, type: 'REFRESH_DATA',
    status: 'PENDING', expiresAt: new Date(Date.now() + 10 * 60_000), bindingRevision: front.npepBindingRevision,
    credentialVersion: front.credentialVersion}});
  const classSecret = credentials();
  const {managementVerifier} = await fromKv('domain/npep/schoolManagement.js');
  await prisma.$executeRaw`INSERT INTO "NpepClassManagement" ("schoolId","classId",revision,verifier,"updatedAt")
      VALUES (${school.id},${classId},7,${JSON.stringify(managementVerifier(classSecret))}::jsonb,now())`;
  const policy = {mode: 'OFF'};
  await prisma.$executeRaw`INSERT INTO "NpepStudySetting" ("schoolId","termId","targetType","targetId",revision,policy,"updatedBy")
      VALUES (${school.id},${term.id},'CLASS',${classId},3,${JSON.stringify(policy)}::jsonb,${managerId})`;
  const {png} = await fromKv('tests/helpers/npepSeatingFixture.js');
  const image = png();
  const imageDigest = makeDigest(image);
  await prisma.$executeRaw`INSERT INTO "NpepSeatingVersion" ("schoolId","termId","classId",revision,hash,width,height,"imageBytes","requestId",digest,"actorId")
      VALUES (${school.id},${term.id},${classId},2,${imageDigest},2,2,${image},${randomUUID()}::uuid,${imageDigest},${managerId})`;
  const event = randomUUID(), exit = randomUUID(), device = fixture.devices[0], expires = new Date(Date.now() + 86400000);
  const {NpepPhotoStore} = await fromKv('services/npepPhotoStore.js');
  const samplePhoto = kvRequire('jpeg-js').encode({width: 12, height: 8, data: Buffer.alloc(12 * 8 * 4, 210)}, 75).data;
  const photoStore = new NpepPhotoStore();
  await photoStore.put(event, samplePhoto);
  const digest = makeDigest(samplePhoto);
  await prisma.$executeRaw`INSERT INTO "NpepStudyPhoto" ("eventId","deviceId","schoolId","termId","classId","leaseId",version,metadata,
      digest,"storedHash",width,height,"byteCount","capturedAt","expiresAt",state)
      VALUES (${event}::uuid,${device}::uuid,${school.id},${term.id},${classId},${randomUUID()}::uuid,${digest},'{}'::jsonb,
      ${digest},${digest},12,8,${samplePhoto.length},now(),${expires},'Stored')`;
  await prisma.$executeRaw`INSERT INTO "NpepStudyExitEvent" ("eventId","deviceId","schoolId","termId","classId",digest,metadata,"createdAt","expiresAt")
      VALUES (${exit}::uuid,${device}::uuid,${school.id},${term.id},${classId},${digest},'{}'::jsonb,now(),${expires})`;
  const oldSession = await login(origin, school.code, 'viewer', pin);
  const owner = await login(origin, school.code, 'admin', pin);
  const target = await startTarget(context);
  const relay = await dropImportReply(target.origin);
  const {Client} = kvRequire('pg');
  const record = new Client({connectionString: target.databaseUrl});
  const file = join(evidence, `desktop-school-${randomUUID()}.npcw-transfer`);
  const evidenceRoot = await realpath(evidence);
  const nativeDirectory = await mkdtemp(join(evidenceRoot, 'isolated-native-'));
  try {
    await record.connect();
    const access = join(directory, 'transfer-desktop.json'), passphrase = phrase();
    await writeFile(access, JSON.stringify({sourceOrigin: origin, targetOrigin: relay.origin, file, pin, schoolCode: school.code,
      schoolId: school.id, setupKey: target.setupKey, passphrase, frontId: front.id, rearId: rear.id, isolated: true}));
    await runDesktop(['--live-school-transfer', access, evidence]);
    const packageBuffer = await readFile(file);
    const {decryptMigrationPackage} = await fromKv('services/schoolMigrationService.js');
    const payload = await decryptMigrationPackage(packageBuffer, passphrase);
    const serialized = JSON.stringify(payload);
    assert.ok(![event, exit, other.id, beforeWeb, classroomBearer, oldSession, digest, command.id, samplePhoto.toString('base64')]
      .some(value => serialized.includes(value)));
    assert.equal(payload.manifest.retained.photos, 1);
    assert.equal(payload.manifest.retained.exitEvents, 1);
    assert.equal(payload.data.seatVersions[0].imageBase64, image.toString('base64'));
    assert.deepEqual(relay.counts(), {dropped: 1, reads: 1, released: 'returned'});
    const exists = (name, id) => record.query(`SELECT id FROM "${name}" WHERE id=$1`, [id]).then(result => result.rowCount);
    for (const [table, id] of [['School', school.id], ['AcademicTerm', term.id], ['Workspace', classId], ['Account', teacher.id],
      ['AdministrativeClassStudent', student.id], ['TeachingAssignment', relation.id], ['Publication', publication.id],
      ['AdministrativeClassLeadership', leadership.id], ['PublicationRevision', history.id], ['AuditLog', audit.id],
      ['ClassroomScreenBinding', front.id], ['ClassroomScreenBinding', rear.id]]) assert.equal(await exists(table, id), 1, table);
    assert.equal(await exists('School', other.id), 0);
    const check = (sql, params) => record.query(sql, params).then(result => result.rows[0]);
    assert.deepEqual(await check('SELECT "adminEpoch","ownerEpoch" FROM "SchoolManagementAuthority" WHERE "schoolId"=$1 AND "accountId"=$2', [school.id, managerId]),
      {adminEpoch, ownerEpoch});
    assert.deepEqual(await check('SELECT "adminEpoch","ownerEpoch" FROM "SchoolManagementAuthority" WHERE "schoolId"=$1 AND "accountId"=$2', [school.id, historicalAdmin.id]),
      {adminEpoch: previousAdminEpoch, ownerEpoch: null});
    const targetAdmin = await login(target.origin, school.code, 'oldadmin', pin);
    const migrationPath = '/api/v2/admin/schools/' + school.id + '/migration/';
    assert.equal((await json(target.origin, migrationPath + 'readiness', {token: targetAdmin})).data.role, 'ADMIN');
    assert.equal((await json(target.origin, migrationPath + 'export', {token: targetAdmin, body: {currentPin: pin, passphrase}, status: null})).status, 403);
    assert.equal((await check('SELECT policy FROM "NpepStudySetting" WHERE "schoolId"=$1', [school.id])).policy.mode, policy.mode);
    assert.ok((await check('SELECT "imageBytes" FROM "NpepSeatingVersion" WHERE "schoolId"=$1', [school.id])).imageBytes.equals(image));
    const empty = await check('SELECT (SELECT count(*)::int FROM "NpepStudyPhoto") AS photos, (SELECT count(*)::int FROM "NpepStudyExitEvent") AS exits, (SELECT count(*)::int FROM "ClassroomScreenCommand") AS commands');
    assert.deepEqual(empty, {photos: 0, exits: 0, commands: 0});
    const entries = await readdir(target.photoDirectory).catch(error => { if (error.code === 'ENOENT') return []; throw error; });
    assert.equal(entries.length, 0);
    assert.deepEqual((await prisma.$queryRaw`SELECT "expiresAt" FROM "NpepStudyPhoto" WHERE "eventId"=${event}::uuid`)[0].expiresAt, expires);
    assert.deepEqual((await prisma.$queryRaw`SELECT "expiresAt" FROM "NpepStudyExitEvent" WHERE "eventId"=${exit}::uuid`)[0].expiresAt, expires);
    assert.ok((await photoStore.read(event, digest)).equals(samplePhoto));
    const sourcePhoto = await fetch(origin + `/api/v2/npep/schools/${school.id}/study-photos/${event}/image?termId=${term.id}&classId=${classId}`,
      {headers: {Authorization: 'Bearer ' + owner, 'X-NPEP-Version': '0.15', 'X-Request-Id': randomUUID()}});
    assert.equal(sourcePhoto.status, 200);
    assert.ok(Buffer.from(await sourcePhoto.arrayBuffer()).equals(samplePhoto));
    assert.equal((await prisma.classroomScreenCommand.findUniqueOrThrow({where: {id: command.id}})).status, 'PENDING');
    const targetTeacher = await login(target.origin, school.code, 'viewer', pin);
    assert.ok((await json(target.origin, '/api/v2/me/workspaces', {token: targetTeacher})).data.some(row => row.workspace.id === classId));
    assert.equal((await json(target.origin, '/api/v2/me/workspaces', {token: oldSession, status: null})).status, 401);
    const screenSession = (endpoint, token) => fetch(endpoint + webScreenPath + '/session', {headers: {'X-Classworks-Screen-Token': token}});
    assert.equal((await screenSession(origin, beforeWeb)).status, 200);
    assert.equal((await screenSession(target.origin, beforeWeb)).status, 401);
    assert.equal((await screenSession(target.origin, await loginScreen(target.origin))).status, 200);
    const reqId = randomUUID();
    const obsolete = await fetch(target.origin + '/api/v2/npep/device/sessions', {method: 'POST', headers: {
      Authorization: 'Bearer ' + classroomBearer, 'X-NPEP-Version': '0.1', 'X-Request-Id': reqId, 'Content-Type': 'application/json'},
    body: JSON.stringify({...identity, requestId: reqId, runId: randomUUID(), expectedStatusEpoch: 0})});
    assert.ok(!obsolete.ok, 'old native bearer must not open or report on target');
    const native = join(directory, 'transfer-native.json');
    await writeFile(native, JSON.stringify({origin: target.origin, schoolId: school.id, schoolCode: school.code, classId,
      bindingId: front.id, rearId: rear.id, managementSecret: classSecret, password: pin, nativeDirectory, isolated: true}));
    await runNative(['--native-school-transfer', native]);
    assert.equal((await check('SELECT count(*)::int AS count FROM "NpepDevice" WHERE "screenBindingId"=$1 AND "lastSeenAt" IS NOT NULL', [front.id])).count, 1);
    assert.equal((await check('SELECT count(*)::int AS count FROM "NpepDevice" WHERE "screenBindingId"=$1', [rear.id])).count, 0);
    await json(origin, '/api/v2/admin/schools/' + school.id + '/migration/readiness', {token: owner});
    const legacyTarget = await startTarget(context);
    let legacy;
    try {
      legacy = await verifyLegacyTransferWeb({...context, school, token: owner, file, passphrase, pin, target: legacyTarget});
      const legacyOwner = await login(legacyTarget.origin, school.code, 'admin', pin);
      assert.ok((await json(legacyTarget.origin, '/api/v2/me/schools', {token: legacyOwner})).data
        .some(row => row.school.id === school.id && row.role === 'OWNER'));
    } finally { await legacyTarget.close(); }
    await verifyFreshExportAuthentication({...context, school, passphrase});
    await writeFile(join(evidence, 'dual-kv-school-transfer.json'), JSON.stringify({sourceAndTargetIndependent: true,
      restored: {school: school.id, class: classId, front: front.id, rear: rear.id}, durableReceipt: relay.counts(), originalTeacherLogin: true,
      oldAccountAndDeviceCredentialsRejected: true, photosAndExitsRetainedOnSource: [1, 1], target: empty,
      sourcePhotoReadableAndPendingCommandPreserved: true, historicalOwnerAndAdminAuthoritiesPreserved: true,
      oldBrowserTokenRejectedAndOriginalScreenPinCanLogIn: true,
      nativePairedUsingOriginalClassSecret: true, frontReported: true, rearStillNeedsPairing: true, legacy,
      freshExportReauthenticationTest: 'oauth-export-evidence.json'}, null, 2));
  } finally {
    await rm(file, {force: true});
    const nativeRoot = await realpath(nativeDirectory);
    assert.equal(dirname(nativeRoot), evidenceRoot);
    assert.equal(nativeRoot.toLowerCase(), nativeDirectory.toLowerCase());
    await rm(nativeRoot, {recursive: true, force: true});
    await record.end();
    await relay.close();
    await target.close();
  }
}
