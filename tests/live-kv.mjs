// node tests/live-kv.mjs <NPClassworksKV checkout> <PostgreSQL bin> [test mode]
// With --serve, keep the isolated KV available for desktop acceptance until Enter.
import assert from 'node:assert/strict';
import { randomBytes, randomInt, randomUUID } from 'node:crypto';
import { closeSync, openSync } from 'node:fs';
import { mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { spawn, spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { createServer } from 'node:net';
import { createServer as createHttpServer, request as httpRequest } from 'node:http';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { cameras, microphones, startClassroomDevice } from './classroom-device.mjs';
import { nextStudyMonday, seedClassStudy } from './class-study-fixture.mjs';
import { prepareSchoolAudit } from './school-audit-fixture.mjs';
import { prepareClassRoster } from './class-roster-fixture.mjs';
import { prepareClassCreation } from './class-creation-fixture.mjs';
import { prepareGradeCreation } from './grade-creation-fixture.mjs';
import { prepareGradeRename } from './grade-rename-fixture.mjs';
import { prepareClassBatch } from './class-batch-fixture.mjs';

const [checkout, binaries, mode] = process.argv.slice(2);
assert.ok(checkout && binaries && (!mode || ['--serve', '--local-authorization', '--serve-local-authorization',
  '--software-update', '--serve-software-update', '--class-admission', '--serve-class-admission',
  '--class-study', '--serve-class-study', '--server-status', '--school-audit', '--serve-school-audit', '--class-roster', '--serve-class-roster',
  '--class-creation', '--serve-class-creation', '--grade-creation', '--grade-rename', '--class-batch',
  '--first-screen', '--serve-first-screen', '--screen-replacement'].includes(mode)),
  'Pass the KV checkout, PostgreSQL bin directory, and optionally a test mode.');
const serve = mode?.startsWith('--serve') ?? false;
const localAuthorization = mode === '--local-authorization' || mode === '--serve-local-authorization';
const softwareUpdate = mode === '--software-update' || mode === '--serve-software-update';
const classAdmission = mode === '--class-admission' || mode === '--serve-class-admission';
const classStudy = mode === '--class-study' || mode === '--serve-class-study';
const schoolAudit = mode === '--school-audit' || mode === '--serve-school-audit';
const classRoster = mode === '--class-roster' || mode === '--serve-class-roster';
const classCreation = mode === '--class-creation' || mode === '--serve-class-creation';
const gradeCreation = mode === '--grade-creation';
const gradeRename = mode === '--grade-rename';
const classBatch = mode === '--class-batch';
const replacement = mode === '--screen-replacement';
const firstScreen = mode === '--first-screen' || mode === '--serve-first-screen' || replacement;
const runDeviceFixture = !classAdmission && !classStudy && !schoolAudit && !classRoster && !classCreation && !gradeCreation && !gradeRename && !firstScreen;
const kv = resolve(checkout);
const fromKv = path => import(pathToFileURL(join(kv, path)).href);
const kvRequire = createRequire(join(kv, 'package.json'));
const { Client } = kvRequire('pg');
const bcrypt = kvRequire('bcrypt');
const temporaryRoot = resolve(tmpdir());
const directory = await mkdtemp(join(temporaryRoot, 'npeduconsole-kv-'));
const data = join(directory, 'data');
const passwordFile = join(directory, 'password');
const databasePassword = randomBytes(32).toString('hex');
const pg = command => join(resolve(binaries), command + (process.platform === 'win32' ? '.exe' : ''));
let prisma, server, stopClassroom, lostReply, rename, batch;

// Model a gateway losing an acknowledgement after KV has already committed.
// The destination is pinned to this run's loopback fixture for every request.
async function loseWriteReply(origin, matches, method = 'POST', dropStatus = 200) {
  const target = new URL(origin);
  let committedRequests = 0;
  let attempts = 0;
  const relay = createHttpServer((req, res) => {
    const watched = req.method === method && matches(req.url);
    if (watched) attempts++;
    const forward = httpRequest({ hostname: '127.0.0.1', port: target.port, method: req.method,
      path: req.url, headers: { ...req.headers, host: target.host } }, upstream => {
      if (watched && upstream.statusCode === dropStatus) {
        committedRequests++;
        upstream.resume();
        upstream.on('end', () => { res.writeHead(502); res.end(); });
        return;
      }
      res.writeHead(upstream.statusCode, upstream.headers);
      upstream.pipe(res);
    });
    forward.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
    req.pipe(forward);
  });
  await new Promise((resolve, reject) => relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  return { server: relay, origin: 'http://127.0.0.1:' + relay.address().port, count: () => committedRequests, attempts: () => attempts };
}

function run(command, args, env = process.env) {
  const logfile = join(directory, basename(command) + '.log');
  const log = openSync(logfile, 'a');
  let result;
  try {
    result = spawnSync(command, args, { cwd: kv, env, windowsHide: true, stdio: ['ignore', log, log] });
  } finally {
    closeSync(log);
  }
  if (result.error || result.status !== 0) throw new Error(`${basename(command)} failed. Log: ${logfile}`);
}

function startDatabase(port) {
  const options = `-h 127.0.0.1 -p ${port}` + (schoolAudit ? ' -c timezone=Asia/Shanghai' : '');
  run(pg('pg_ctl'), ['-D', data, '-l', join(directory, 'postgres.log'), '-o', options, '-w', 'start']);
}

async function createDatabase() {
  const probe = createServer();
  await new Promise((res, rej) => probe.once('error', rej).listen(0, '127.0.0.1', res));
  const port = probe.address().port;
  await new Promise(res => probe.close(res));
  const connection = { host: '127.0.0.1', port, user: 'npconsole', password: databasePassword };
  for (const key of ['AXIOM_TOKEN', 'AXIOM_DATASET', 'PGSERVICE', 'PGSERVICEFILE', 'PGOPTIONS', 'PGHOST', 'PGDATABASE', 'PGUSER', 'PGPORT', 'PGPASSWORD', 'TRUST_PROXY']) delete process.env[key];
  Object.assign(process.env, {
    DATABASE_URL: `postgresql://npconsole:${databasePassword}@127.0.0.1:${port}/npconsole_fixture`,
    DOTENV_CONFIG_PATH: join(directory, 'empty.env'), DOTENV_CONFIG_OVERRIDE: 'false',
    NODE_ENV: 'test', JWT_ALG: 'HS256', JWT_SECRET: randomBytes(48).toString('hex'),
    REFRESH_TOKEN_SECRET: randomBytes(48).toString('hex'), NPEP_ENABLED: 'true',
    NPEP_DEPLOYMENT_FILE: join(directory, 'deployment.json'),
  });
  delete process.env.NPEP_UPDATE_CATALOG_FILE;
  if (mode === '--server-status') process.env.ACCESS_TOKEN_EXPIRES_IN = '15s';
  if (softwareUpdate) {
    process.env.NPEP_UPDATE_CATALOG_FILE = join(directory, 'update-catalog.json');
    const releases = ['1.1.10.0', '1.1.11.0', '1.1.12.0'].map(version => ({
      version, url: 'https://example.invalid/NPEduTools-' + version + '.zip', sha256: 'a'.repeat(64), sizeBytes: 1,
    }));
    await writeFile(process.env.NPEP_UPDATE_CATALOG_FILE, JSON.stringify({ releases }));
  }
  await writeFile(process.env.DOTENV_CONFIG_PATH, '');
  await writeFile(passwordFile, databasePassword, { mode: 0o600 });
  try {
    run(pg('initdb'), ['-D', data, '-U', connection.user, '--pwfile', passwordFile, '--auth=scram-sha-256', '--encoding=UTF8', '--locale=C']);
  } finally {
    await rm(passwordFile, { force: true });
  }
  startDatabase(port);
  const db = new Client({ ...connection, database: 'postgres' });
  await db.connect();
  try {
    // Confirm ownership even if another listener raced the ephemeral port choice.
    assert.equal(resolve((await db.query('SHOW data_directory')).rows[0].data_directory), resolve(data));
    await db.query('CREATE DATABASE npconsole_fixture');
  } finally {
    await db.end();
  }
  run(process.execPath, ['node_modules/prisma/build/index.js', 'migrate', 'deploy']);
  return port;
}

async function seed() {
  const { localProviderId } = await fromKv('domain/localAccount.js');
  const { secretHash } = await fromKv('domain/npep/wire.js');
  const { schoolManagementRepository } = await fromKv('services/npepSchoolManagementService.js');
  const { managementVerifier } = await fromKv('domain/npep/schoolManagement.js');
  const identity = { serverInstanceId: randomUUID(), deploymentEpoch: randomUUID() };
  await writeFile(process.env.NPEP_DEPLOYMENT_FILE, JSON.stringify({ enabled: true, ...identity }));
  await prisma.npepDeployment.create({ data: { id: 'current', ...identity } });
  const school = await prisma.school.create({ data: { code: 'DEMO', name: '示范中学' } });
  const other = await prisma.school.create({ data: { code: 'OTHER', name: '另一所学校' } });
  const pin = String(randomInt(100000, 999999));
  async function account(username, role) {
    const entry = await prisma.account.create({ data: {
      provider: 'school-local', providerId: localProviderId(school.code, username), name: username,
      localUsername: username, localPasswordHash: await bcrypt.hash(pin, 10), providerData: { schoolCode: school.code },
    } });
    await prisma.schoolMember.create({ data: { schoolId: school.id, accountId: entry.id, role } });
    return entry;
  }
  const manager = await account('admin', 'ADMIN');
  await account('viewer', 'VIEWER');
  if (gradeRename || classBatch) await account('admin2', 'ADMIN');
  const term = await prisma.academicTerm.create({ data: {
    schoolId: school.id, academicYear: 2026, semester: 1, status: 'ACTIVE', name: '秋季学期',
  } });
  const grade = await prisma.grade.create({ data: { termId: term.id, name: '高二', code: 'G2', sortOrder: 1 } });
  const studyGrade = classStudy ? await prisma.grade.create({ data: { termId: term.id, name: '高一', code: 'G1', sortOrder: 2 } }) : null;
  const devices = [], classes = [], revokedDeviceId = randomUUID();
  let classroomBearer;
  for (let number = 1; number <= (classStudy || firstScreen ? 4 : 3); number++) {
    const roomGrade = studyGrade && number > 2 ? studyGrade : grade;
    const section = roomGrade === studyGrade ? number - 2 : number;
    const room = await prisma.workspace.create({ data: {
      termId: term.id, gradeId: roomGrade.id, name: `${roomGrade.name}（${section}）班`, code: `${roomGrade.code}-${section}`, type: 'ADMIN_CLASS',
    } });
    classes.push(room.id);
    if (firstScreen && number === 4) await prisma.classroomScreenBinding.create({data: {
      schoolId: school.id, administrativeClassId: room.id, name: '已停用大屏', isActive: false,
      tokenHash: randomBytes(32).toString('hex'), createdByAccountId: manager.id,
    }});
    if (classAdmission && number >= 2) {
      const names = number === 2 ? ['沿用口令大屏'] : ['主讲大屏', '后排大屏'];
      for (const name of names) await prisma.classroomScreenBinding.create({ data: {
        schoolId: school.id, administrativeClassId: room.id, name,
        tokenHash: randomBytes(32).toString('hex'), createdByAccountId: manager.id,
      } });
      if (number === 2) await schoolManagementRepository.saveCredential(prisma, school.id, room.id, {
        revision: 1, verifier: managementVerifier(randomBytes(24).toString('base64url')), updatedAt: new Date().toISOString(),
      });
    }
    if (number >= 3) continue;
    const binding = await prisma.classroomScreenBinding.create({ data: {
      schoolId: school.id, administrativeClassId: room.id, name: room.name + '大屏',
      tokenHash: randomBytes(32).toString('hex'), createdByAccountId: manager.id,
    } });
    const seen = new Date(Date.now() - (number === 1 ? 0 : 2 * 60 * 1000));
    const secret = randomBytes(32).toString('base64url');
    const device = await prisma.npepDevice.create({ data: {
      ...identity, id: randomUUID(), installationId: randomUUID(), credentialId: randomUUID(),
      secretHash: secretHash(secret), schoolId: school.id, administrativeClassId: room.id,
      screenBindingId: binding.id, bindingRevision: binding.npepBindingRevision, deviceName: room.name + '一体机',
      lastSeenAt: seen, credentialExpiresAt: new Date(Date.now() + 86400000),
    } });
    devices.push(device.id);
    if (number === 1) {
      classroomBearer = 'npep1.' + device.credentialId + '.' + secret;
      await prisma.npepDevice.create({ data: {
        ...identity, id: revokedDeviceId, installationId: randomUUID(), credentialId: randomUUID(),
        secretHash: randomBytes(32).toString('hex'), schoolId: school.id, administrativeClassId: room.id,
        screenBindingId: binding.id, bindingRevision: binding.npepBindingRevision, deviceName: '已退役的一体机',
        state: 'REVOKED', revokedAt: seen, credentialExpiresAt: new Date(Date.now() + 86400000),
      } });
    }
    await schoolManagementRepository.saveEquipment(prisma, device.id, {
      revision: 5, locked: true, microphoneId: microphones[0].id, cameraId: cameras[0].id, updatedAt: seen.toISOString(),
      ...(localAuthorization && number === 2 ? { allowLocalMicrophoneChange: true } : {}),
    });
    await schoolManagementRepository.saveInventory(prisma, device.id, {
      appliedRevision: number === 1 ? 5 : 4, selectedMicrophoneId: microphones[0].id, selectedCameraId: cameras[0].id,
      unlocked: false, error: null, microphones, cameras, receivedAt: seen.toISOString(),
    });
  }
  const studyDate = classStudy ? nextStudyMonday() : null;
  if (firstScreen) {
    // Fill the old global prefix with a different school's identically named class.
    const otherTerm = await prisma.academicTerm.create({data: {schoolId: other.id, academicYear: 2026, semester: 1, status: 'ACTIVE', name: term.name}});
    const otherGrade = await prisma.grade.create({data: {termId: otherTerm.id, name: grade.name, code: grade.code}});
    const duplicate = await prisma.workspace.create({data: {termId: otherTerm.id, gradeId: otherGrade.id, name: '高二（3）班', code: 'G2-3', type: 'ADMIN_CLASS'}});
    await prisma.classroomScreenBinding.createMany({data: Array.from({length: 205}, (_, i) => ({
      id: '000-screen-' + String(i).padStart(3, '0'), schoolId: other.id, administrativeClassId: duplicate.id,
      name: '教学屏' + (i + 1), tokenHash: randomBytes(32).toString('hex'), createdByAccountId: manager.id,
    }))});
  }
  if (studyDate) await seedClassStudy(prisma, { school, term, manager, grade, classes, date: studyDate });
  return { school, other, term, classes, devices, revokedDeviceId, pin, identity, classroomBearer, studyDate };
}

async function verify(origin, fixture) {
  async function login(username) {
    const response = await fetch(origin + '/accounts/local/login', { method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ schoolCode: fixture.school.code, username, password: fixture.pin }),
    });
    assert.equal(response.status, 200);
    return (await response.json()).data.access_token;
  }
  async function get(path, token, protocol, expected = 200) {
    const headers = { Authorization: 'Bearer ' + token };
    if (protocol) Object.assign(headers, { 'X-NPEP-Version': protocol, 'X-Request-Id': randomUUID() });
    const response = await fetch(origin + path, { headers });
    assert.equal(response.status, expected, path);
    return (await response.json()).data;
  }
  const admin = await login('admin'), viewer = await login('viewer');
  const memberships = await get('/api/v2/me/schools', admin);
  assert.deepEqual(memberships.map(x => x.schoolId), [fixture.school.id]);
  const prefix = '/api/v2/admin/schools/' + fixture.school.id;
  const structure = await get(prefix + '/academic-structure?termId=' + fixture.term.id, admin);
  assert.equal(structure.administrativeClasses.length, 3);
  const screens = await get(prefix + '/classroom-screens', admin);
  assert.deepEqual(screens.map(x => x.administrativeClassId).sort(), fixture.classes.slice(0, 2).sort());
  const schoolPath = id => '/api/v2/npep/schools/' + id;
  const overview = schoolPath(fixture.school.id) + '/device-overview';
  const first = await get(overview + '?limit=2', admin, '0.1');
  assert.ok(first.nextCursor);
  const second = await get(overview + '?limit=2&cursor=' + first.nextCursor, admin, '0.1');
  assert.equal(second.nextCursor, null);
  const overviewItems = [...first.items, ...second.items];
  assert.deepEqual(overviewItems.map(x => x.deviceId).sort(), [...fixture.devices, fixture.revokedDeviceId].sort());
  assert.equal(overviewItems.find(item => item.deviceId === fixture.revokedDeviceId).state, 'REVOKED');
  await get(schoolPath(fixture.school.id) + '/devices/' + fixture.revokedDeviceId + '/equipment', admin, '0.16', 401);
  for (const [index, id] of fixture.devices.entries()) {
    const path = '/devices/' + id + '/equipment';
    const snapshot = await get(schoolPath(fixture.school.id) + path, admin, '0.16');
    assert.equal(snapshot.policy.revision, 5);
    assert.equal(snapshot.inventory.appliedRevision, index === 0 ? 5 : 4);
    await get(schoolPath(fixture.other.id) + path, admin, '0.16', 403);
    await get(schoolPath(fixture.school.id) + path, viewer, '0.16', 403);
  }
  async function deniedChange(schoolId, deviceId, token, microphoneId, status, code) {
    const requestId = randomUUID();
    const response = await fetch(origin + schoolPath(schoolId) + '/devices/' + deviceId + '/equipment', {
      method: 'POST',
      headers: { Authorization: 'Bearer ' + token, 'X-NPEP-Version': '0.16', 'X-Request-Id': requestId,
        'Content-Type': 'application/json' },
      body: JSON.stringify({ requestId, microphoneId, cameraId: cameras[0].id, locked: true, expectedRevision: 5 }),
    });
    assert.equal(response.status, status);
    assert.equal((await response.json()).error.code, code);
  }
  const [activeId, offlineId] = fixture.devices;
  await deniedChange(fixture.other.id, activeId, admin, microphones[1].id, 403, 'SCHOOL_ADMIN_REQUIRED');
  await deniedChange(fixture.school.id, activeId, viewer, microphones[1].id, 403, 'SCHOOL_ADMIN_REQUIRED');
  await deniedChange(fixture.school.id, offlineId, admin, microphones[1].id, 409, 'EQUIPMENT_INVENTORY_STALE');
  await deniedChange(fixture.school.id, activeId, admin, 'disconnected-id', 409, 'EQUIPMENT_UNAVAILABLE');
  await get(schoolPath(fixture.other.id) + '/device-overview', admin, '0.1', 403);
  await get(overview, viewer, '0.1', 403);
  for (const endpoint of ['/academic-structure?termId=' + fixture.term.id, '/classroom-screens']) {
    await get('/api/v2/admin/schools/' + fixture.other.id + endpoint, admin, undefined, 403);
    await get(prefix + endpoint, viewer, undefined, 403);
  }
  console.log('PASS: real KV login, directory, paging, receipts and school scope. Unauthorized, offline-inventory and missing-microphone writes rejected.');
}

async function verifyServerStatus(origin, fixture, databasePort) {
  const project = fileURLToPath(new URL('./NPEduConsole.Smoke/NPEduConsole.Smoke.csproj', import.meta.url));
  run('dotnet', ['build', project, '-c', 'Release', '--nologo']);
  const monitoringToken = randomBytes(32).toString('hex');
  delete process.env.METRICS_TOKEN;
  async function loginAdmin() {
    const login = await fetch(origin + '/accounts/local/login', {method: 'POST',
      headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: fixture.school.code, username: 'admin', password: fixture.pin})});
    assert.equal(login.status, 200);
    const token = (await login.json()).data.access_token;
    const {exp, iat} = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString());
    assert.equal(exp - iat, 15, 'isolated KV issues short-lived access tokens for the renewal check');
    return token;
  }
  let schoolToken = await loginAdmin();
  async function schoolStatus(expectedStatus) {
    const response = await fetch(origin + '/api/v2/me/server-status', {headers: {Authorization: 'Bearer ' + schoolToken}});
    assert.equal(response.status, expectedStatus);
    assert.match(response.headers.get('cache-control'), /no-store/);
    assert.match(response.headers.get('vary'), /Authorization/i);
    const body = await response.json();
    if (response.ok) return body.data;
    assert.ok(!body.data, 'no diagnostics when the school grant is missing');
  }
  const summary = await schoolStatus(200);
  assert.equal(summary.database, 'AVAILABLE');
  assert.deepEqual(Object.keys(summary).sort(), ['backup', 'checkedAt', 'database', 'updateCatalog', 'version']);

  // The same logged-in account can administer multiple schools. A live grant in any one is enough.
  const manager = await prisma.schoolMember.findFirstOrThrow({where: {schoolId: fixture.school.id, role: 'ADMIN'}});
  const original = {schoolId_accountId: {schoolId: fixture.school.id, accountId: manager.accountId}};
  const otherMembership = {schoolId: fixture.other.id, accountId: manager.accountId};
  await prisma.schoolMember.create({data: {...otherMembership, role: 'OWNER'}});
  await prisma.schoolMember.update({where: original, data: {role: 'VIEWER'}});
  await schoolStatus(200);
  await prisma.schoolMember.delete({where: {schoolId_accountId: otherMembership}});
  await schoolStatus(403);
  await prisma.schoolMember.update({where: original, data: {role: 'ADMIN'}});
  await schoolStatus(200);

  process.env.METRICS_TOKEN = monitoringToken;
  for (const [path, token] of [['/api/v2/ops/status', schoolToken], ['/metrics', schoolToken],
    ['/api/v2/me/schools', monitoringToken], ['/api/v2/me/server-status', monitoringToken]]) {
    const response = await fetch(origin + path, {headers: {Authorization: 'Bearer ' + token}});
    await response.arrayBuffer();
    assert.equal(response.status, 401, path);
  }
  async function check(database, catalog, count, backupStatus, timeliness, result = 'null', schoolAccount = false) {
    const env = {...process.env, NP_OPS_ORIGIN: origin,
      NP_OPS_DATABASE: database, NP_OPS_CATALOG: catalog, NP_OPS_BACKUP_STATUS: backupStatus,
      NP_OPS_BACKUP_TIMELINESS: timeliness, NP_OPS_BACKUP_RESULT: result};
    const mode = schoolAccount ? '--school-server-status' : '--server-status';
    if (schoolAccount) {
      delete env.METRICS_TOKEN;
      delete env.NP_OPS_TOKEN;
      Object.assign(env, {NP_TEST_SCHOOL_CODE: fixture.school.code, NP_TEST_USERNAME: 'admin', NP_TEST_PASSWORD: fixture.pin,
        NP_TEST_RENEWAL_SECONDS: '18'});
    } else env.NP_OPS_TOKEN = monitoringToken;
    if (count === null) delete env.NP_OPS_RELEASE_COUNT;
    else env.NP_OPS_RELEASE_COUNT = String(count);
    await new Promise((resolve, reject) => {
      const child = spawn('dotnet', ['run', '--no-build', '--project', project, '-c', 'Release', '--', mode],
        {env, windowsHide: true, stdio: ['ignore', 'inherit', 'inherit']});
      child.once('error', reject);
      child.once('close', code => code === 0 ? resolve() : reject(new Error('Server status smoke failed: ' + code)));
    });
  }
  await check('AVAILABLE', 'NOT_CONFIGURED', null, 'NOT_CONFIGURED', 'UNKNOWN');
  const backupDirectory = join(directory, 'status-backup');
  await mkdir(backupDirectory);
  process.env.KV_BACKUP_STATUS_DIR = backupDirectory;
  const record = join(backupDirectory, 'scheduled.state');
  const path = join(directory, 'status-catalog.json');
  process.env.NPEP_UPDATE_CATALOG_FILE = path;
  await writeFile(path, '{');
  await check('AVAILABLE', 'UNAVAILABLE', null, 'NO_RECORD', 'UNKNOWN');
  await writeFile(path, '{"releases":[]}');
  await writeFile(record, 'invalid fixture record\n');
  await check('AVAILABLE', 'AVAILABLE', 0, 'UNAVAILABLE', 'UNKNOWN');
  const now = Math.floor(Date.now() / 1000);
  await writeFile(record, `1 ${now - 3600} 0 0 NONE\n`);
  await check('AVAILABLE', 'AVAILABLE', 0, 'AVAILABLE', 'AWAITING_FIRST_SUCCESS');
  await writeFile(path, JSON.stringify({releases: [{version: '1.1.12.0', url: 'https://example.invalid/update.zip',
    sha256: '0'.repeat(64), sizeBytes: 1}]}));
  await writeFile(record, `1 ${now - 7200} ${now - 3600} ${now - 120} FAILED\n`);
  schoolToken = await loginAdmin();
  run(pg('pg_ctl'), ['-D', data, '-w', '-m', 'fast', 'stop']);
  await check('UNAVAILABLE', 'AVAILABLE', 1, 'AVAILABLE', 'CURRENT', 'FAILED');
  const unavailable = await fetch(origin + '/api/v2/me/server-status', {
    headers: {Authorization: 'Bearer ' + schoolToken}, signal: AbortSignal.timeout(30000),
  });
  assert.ok(!unavailable.ok && !(await unavailable.json()).data, 'No account diagnostics without a live authorization check');
  startDatabase(databasePort);
  assert.equal((await schoolStatus(200)).database, 'AVAILABLE', 'Original school session works again after database recovery');
  await writeFile(record, `1 ${now - 30 * 3600} ${now - 27 * 3600} ${now - 120} FAILED\n`);
  await check('AVAILABLE', 'AVAILABLE', 1, 'AVAILABLE', 'OVERDUE', 'FAILED');
  const slightlyAhead = Math.floor(Date.now() / 1000) + 30;
  await writeFile(record, `1 ${now - 3600} ${slightlyAhead} ${slightlyAhead} SUCCEEDED\n`);
  await check('AVAILABLE', 'AVAILABLE', 1, 'AVAILABLE', 'CURRENT', 'SUCCEEDED');
  delete process.env.METRICS_TOKEN;
  await check('AVAILABLE', 'AVAILABLE', 1, 'AVAILABLE', 'CURRENT', 'SUCCEEDED', true);
  console.log('PASS: school account without a monitoring credential, live role revocation, independent deployment diagnostics, database outage and recovery.');
}

function runDesktop(project, args) {
  return new Promise((res, rej) => {
    // The KV HTTP listener and database pool run on this event loop.
    const client = spawn('dotnet', ['run', '--project', project, '-c', 'Release', '--', ...args],
      { windowsHide: true, stdio: ['ignore', 'inherit', 'inherit'] });
    client.once('error', rej);
    client.once('close', code => code === 0 ? res() : rej(new Error('Desktop client smoke failed: ' + code)));
  });
}

async function verifyClassBatch(origin, fixture) {
  batch = await prepareClassBatch(prisma, origin, fixture);
  const access = join(directory, 'desktop.json');
  const login = {schoolId: fixture.school.id, schoolCode: fixture.school.code, username: 'admin', password: fixture.pin, ...batch.access};
  await writeFile(access, JSON.stringify(login), { mode: 0o600 });
  const project = fileURLToPath(new URL('./NPEduConsole.ClassCreationDesktop/NPEduConsole.ClassCreationDesktop.csproj', import.meta.url));
  const output = process.env.NP_CONSOLE_CLASS_BATCH_EVIDENCE
    || fileURLToPath(new URL('../.artifacts/class-batch/desktop-live', import.meta.url));
  await runDesktop(project, ['--live-class-batch', access, output]);
  const saved = await batch.checkDesktop();
  await writeFile(access, JSON.stringify({...login, ...saved, origin: login.directOrigin}), { mode: 0o600 });
  await runDesktop(project, ['--live-class-batch-reopen', access, join(output, 'reopen')]);
  await batch.verifyOtherPaths();
  run(process.execPath, ['--test', '--test-name-pattern=current class batch', 'tests/managementDatabase.integration.test.js'],
    {...process.env, RUN_DATABASE_TESTS: 'true'});
  console.log('PASS: real PostgreSQL batch receipt ownership and rollback after a racing legacy insert.');
}

async function runFixture() {
  // Node's POSIX file modes have no ACL equivalent on Windows. Restrict the
  // new directory before any credentials, database files, or logs are written.
  if (process.platform === 'win32') {
    const account = spawnSync('whoami', [], { windowsHide: true, encoding: 'utf8' });
    assert.equal(account.status, 0);
    const restricted = spawnSync('icacls', [directory, '/inheritance:r', '/grant:r',
      account.stdout.trim() + ':(OI)(CI)F', '*S-1-5-18:(OI)(CI)F'], { windowsHide: true });
    assert.equal(restricted.status, 0, 'Could not restrict temporary fixture access');
  }
  const databasePort = await createDatabase();
  ({ prisma } = await fromKv('utils/prisma.js'));
  const fixture = await seed();
  if (classRoster || classCreation || gradeCreation || gradeRename || classBatch) process.env.NPEP_ENABLED = 'false';
  if (mode === '--server-status') delete process.env.KV_BACKUP_STATUS_DIR;
  const { default: app } = await fromKv('app.js');
  server = await new Promise((res, rej) => {
    const instance = app.listen(0, '127.0.0.1', () => res(instance));
    instance.once('error', rej);
  });
  const origin = 'http://127.0.0.1:' + server.address().port;
  if (mode === '--server-status') return verifyServerStatus(origin, fixture, databasePort);
  if (classBatch) return verifyClassBatch(origin, fixture);
  const audit = schoolAudit ? await prepareSchoolAudit(prisma, origin, fixture) : null;
  const roster = classRoster ? await prepareClassRoster(prisma, origin, fixture) : null;
  const creation = classCreation ? await prepareClassCreation(prisma, origin, fixture) : null;
  const grades = gradeCreation ? await prepareGradeCreation(prisma, origin, fixture) : null;
  if (gradeRename) rename = await prepareGradeRename(prisma, origin, fixture);
  if (runDeviceFixture) {
    stopClassroom = await startClassroomDevice(origin, fixture.identity, fixture.classroomBearer,
      { checkIncompleteReceipts: !serve && !localAuthorization, simulateLocalAuthorization: localAuthorization,
        lagAfterLocalChange: localAuthorization && !serve });
    await verify(origin, fixture);
  }
  if (softwareUpdate) {
    await stopClassroom();
    stopClassroom = null;
  }
  // Only local, short-lived, synthetic login data is made available to the desktop process.
  const access = join(directory, 'desktop.json');
  if (classAdmission) lostReply = await loseWriteReply(origin, path => path.includes('/class-management/'));
  else if (firstScreen) lostReply = await loseWriteReply(origin, path =>
    path.endsWith('/first-screen') || (replacement && path === '/api/v2/npep/pairings/replacement'), 'POST', 201);
  else if (classStudy) lostReply = await loseWriteReply(origin, path => path.endsWith('/study-settings'));
  else if (!serve && classRoster) lostReply = await loseWriteReply(origin, path => path.endsWith('/students'), 'PUT');
  else if (classCreation) lostReply = await loseWriteReply(origin, path => path.endsWith('/current-term/administrative-classes'), 'POST', 201);
  else if (gradeCreation) lostReply = await loseWriteReply(origin, path => path.endsWith('/current-term/grades'), 'POST', 201);
  else if (rename) lostReply = await loseWriteReply(origin, rename.matches, 'PATCH');
  else if (!serve && !softwareUpdate && runDeviceFixture) lostReply = await loseWriteReply(origin, path => path.endsWith('/equipment'));
  await writeFile(access, JSON.stringify({
    origin, schoolCode: fixture.school.code, username: 'admin', viewer: 'viewer', password: fixture.pin,
    schoolId: fixture.school.id, otherSchoolId: fixture.other.id, revokedDeviceId: fixture.revokedDeviceId,
    lostReplyOrigin: lostReply?.origin,
    ...audit?.access,
    ...roster?.access,
    ...creation?.access,
    ...grades?.access,
    ...rename?.access,
    ...(firstScreen ? {emptyClassId: fixture.classes[2], inactiveClassId: fixture.classes[3], termId: fixture.term.id,
      allowFirstScreenTestWrites: true, allowReplacementTestWrites: replacement} : {}),
    ...(softwareUpdate || classStudy ? { classroomBearer: fixture.classroomBearer, deviceIdentity: fixture.identity } : {}),
    ...(classStudy ? { termId: fixture.term.id, studyDate: fixture.studyDate } : {}),
  }), { mode: 0o600 });
  const project = gradeCreation || gradeRename ? './NPEduConsole.ClassCreationDesktop/NPEduConsole.ClassCreationDesktop.csproj' :
    firstScreen ? './NPEduConsole.FirstScreenSmoke/NPEduConsole.FirstScreenSmoke.csproj' :
    classCreation ? './NPEduConsole.ClassCreationSmoke/NPEduConsole.ClassCreationSmoke.csproj' :
    classRoster ? './NPEduConsole.RosterSmoke/NPEduConsole.RosterSmoke.csproj' :
    schoolAudit ? './NPEduConsole.AuditSmoke/NPEduConsole.AuditSmoke.csproj' :
    classStudy ? './NPEduConsole.StudySmoke/NPEduConsole.StudySmoke.csproj' : './NPEduConsole.Smoke/NPEduConsole.Smoke.csproj';
  const smoke = fileURLToPath(new URL(project, import.meta.url));
  if (!serve || (runDeviceFixture && !softwareUpdate)) {
    const options = classAdmission ? ['--class-admission'] : softwareUpdate ? ['--software-update'] : localAuthorization ? [serve ? '--view-local-authorization' : '--local-authorization'] :
      serve ? ['--view-only'] : [];
    const args = gradeRename ? ['--live-grade-rename', access, process.env.NP_CONSOLE_GRADE_RENAME_EVIDENCE
      || fileURLToPath(new URL('../.artifacts/grade-rename/desktop-live', import.meta.url))]
      : gradeCreation ? ['--live-grade', access, fileURLToPath(new URL('../.artifacts/grade-creation/desktop-live', import.meta.url))]
      : [access, ...options];
    await runDesktop(smoke, args);
  }
  if (!serve && (classAdmission || classStudy)) {
    assert.equal(lostReply.count(), 1, 'a lost acknowledgement caused no automatic write retry');
    console.log('PASS: KV committed exactly one write through the lost-reply relay.');
  }
  if (!serve && audit) await audit.verifyRevocation();
  if (!serve && firstScreen) {
    assert.equal(lostReply.count(), replacement ? 2 : 1);
    assert.equal(lostReply.attempts(), replacement ? 4 : 2);
    const id = fixture.classes[2];
    const [binding] = await prisma.classroomScreenBinding.findMany({where: {administrativeClassId: id}});
    assert.equal(await prisma.classroomScreenBinding.count({where: {administrativeClassId: id}}), 1);
    assert.equal(binding.loginCode, null);
    assert.equal(binding.pinHash, null);
    assert.equal(await prisma.auditLog.count({where: {schoolId: fixture.school.id, entityId: binding.id, action: 'FIRST_CLASSROOM_SCREEN_PREPARED'}}), 1);
    if (replacement) {
      const where = {schoolId: fixture.school.id, screenBindingId: binding.id};
      const candidate = await prisma.npepPairing.findFirstOrThrow({where: {...where, approvalSource: 'REPLACEMENT'}});
      assert.equal(await prisma.npepPairing.count({where: {...where, approvalSource: 'REPLACEMENT'}}), 1);
      assert.equal(candidate.state, 'ACTIVATED');
      const incumbent = await prisma.npepDevice.findUniqueOrThrow({where: {id: candidate.approvalSnapshot.expectedDeviceId}});
      assert.equal(incumbent.state, 'REVOKED');
      assert.equal(incumbent.sessionId, null);
      assert.deepEqual(await prisma.npepDevice.findMany({where: {...where, state: 'ACTIVE'}, select: {id: true}}), [{id: candidate.deviceId}]);
      for (const action of ['REPLACEMENT_REQUESTED', 'REPLACEMENT_APPROVED', 'REPLACEMENT_ACTIVATED', 'REPLACEMENT_FIRST_REPORT'])
        assert.equal(await prisma.npepAudit.count({where: {schoolId: fixture.school.id, objectId: candidate.id, action}}), 1, action);
      console.log('PASS: one handover despite a lost write reply, one successor, old credential revoked, one first-report audit.');
    }
    run(process.execPath, ['--test', '--test-name-pattern=first-screen', 'tests/managementDatabase.integration.test.js'], {...process.env, RUN_DATABASE_TESTS: 'true'});
    console.log('PASS: one screen, one audit, no web login or PIN, and one explicit retry after a lost reply.');
  }
  if (!serve && creation) {
    assert.equal(lostReply.count(), 1, 'only the original creation returned 201');
    assert.equal(lostReply.attempts(), 2, 'the original and the explicit confirmation retry');
    await creation.verify();
    run(process.execPath, ['--test', 'tests/managementDatabase.integration.test.js'], {...process.env, RUN_DATABASE_TESTS: 'true'});
    console.log('PASS: live KV database management tests, including actor-bound retries and desktop/web creation races.');
  }
  if (grades) {
    assert.equal(lostReply.count(), 1, 'the intermediary lost the only successful grade write response');
    assert.equal(lostReply.attempts(), 3, 'recovery uses GET, with POST only for the creation, explicit duplicate, and stale term checks');
    await grades.verify();
    run(process.execPath, ['--test', '--test-name-pattern=grade creation', 'tests/managementDatabase.integration.test.js'], {...process.env, RUN_DATABASE_TESTS: 'true'});
    console.log('PASS: PostgreSQL request ownership, concurrent retries and locked-term refusal.');
  }
  if (rename) {
    assert.equal(lostReply.attempts(), 1, 'no automatic second PATCH after a lost acknowledgement');
    assert.equal(lostReply.count(), 1, 'the lost acknowledgement follows a committed PATCH');
    await rename.verify();
    run(process.execPath, ['--test', '--test-name-pattern=grade rename', 'tests/managementDatabase.integration.test.js'],
      {...process.env, RUN_DATABASE_TESTS: 'true'});
    console.log('PASS: grade rename PostgreSQL contention and stale form regression.');
  }
  if (!serve && roster) {
    assert.equal(lostReply.count(), 1);
    assert.equal(lostReply.attempts(), 1, 'no automatic resubmission after a lost roster reply');
    await roster.verify();
    run(process.execPath, ['--test', 'tests/classRosterDatabase.integration.test.js'], {...process.env, RUN_DATABASE_TESTS: 'true'});
    console.log('PASS: KV database integration for simultaneous screen/admin saves and a term switch after opening the roster.');
  }
  if (!serve && !softwareUpdate && runDeviceFixture) {
    const { schoolManagementRepository } = await fromKv('services/npepSchoolManagementService.js');
    const policy = await schoolManagementRepository.equipment(prisma, fixture.devices[0]);
    assert.deepEqual({ revision: policy.revision, mic: policy.microphoneId, camera: policy.cameraId, locked: policy.locked },
      { revision: localAuthorization ? 8 : 7, mic: microphones[0].id, camera: cameras[0].id, locked: true });
    assert.equal(await prisma.npepAudit.count({ where: { schoolId: fixture.school.id, objectId: fixture.devices[0],
      action: 'EQUIPMENT_POLICY_UPDATED' } }), localAuthorization ? 3 : 2, 'one audit per accepted logical change');
    assert.equal(lostReply.count(), 1, 'no automatic resubmission after a lost acknowledgement');
    console.log('PASS: one equipment audit per write, one forwarded write after the lost acknowledgement, camera and lock unchanged.');
  }
  if (serve) {
    console.log('Desktop test access: ' + access + ' (press Enter to stop)');
    await new Promise(done => {
      process.stdin.resume();
      process.stdin.once('data', done);
      process.stdin.once('end', done);
      process.once('SIGINT', done);
      process.once('SIGTERM', done);
    });
    process.stdin.pause();
  }
}

let passed = false;
try {
  await runFixture();
  passed = true;
} finally {
  // Finish outstanding device traffic before closing the local server and data store.
  if (stopClassroom) {
    try { await stopClassroom(); }
    catch (error) { passed = false; process.exitCode = 1; console.error('Fixture device stopped:', error.message); }
  }
  for (const listener of [lostReply?.server, rename?.server, batch?.server, server].filter(Boolean)) {
    const closed = new Promise(res => listener.close(res));
    listener.closeAllConnections();
    await closed;
  }
  if (prisma) await prisma.$disconnect();
  await rm(join(directory, 'desktop.json'), { force: true });
  const status = spawnSync(pg('pg_ctl'), ['-D', data, 'status'], { windowsHide: true, stdio: 'ignore' });
  if (status.status === 0) run(pg('pg_ctl'), ['-D', data, '-w', '-m', 'fast', 'stop']);
  if (passed && dirname(directory) === temporaryRoot && basename(directory).startsWith('npeduconsole-kv-')) {
    await rm(directory, { recursive: true });
  } else console.error('Fixture logs retained in ' + directory);
}
