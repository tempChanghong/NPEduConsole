// node tests/live-kv.mjs <NPClassworksKV checkout> <PostgreSQL bin> [test mode]
// With --serve, keep the isolated KV available for desktop acceptance until Enter.
import assert from 'node:assert/strict';
import { randomBytes, randomInt, randomUUID } from 'node:crypto';
import { closeSync, openSync } from 'node:fs';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { spawn, spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { createServer } from 'node:net';
import { createServer as createHttpServer, request as httpRequest } from 'node:http';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { cameras, microphones, startClassroomDevice } from './classroom-device.mjs';

const [checkout, binaries, mode] = process.argv.slice(2);
assert.ok(checkout && binaries && (!mode || ['--serve', '--local-authorization', '--serve-local-authorization',
  '--software-update', '--serve-software-update'].includes(mode)),
  'Pass the KV checkout, PostgreSQL bin directory, and optionally a test mode.');
const serve = mode?.startsWith('--serve') ?? false;
const localAuthorization = mode === '--local-authorization' || mode === '--serve-local-authorization';
const softwareUpdate = mode === '--software-update' || mode === '--serve-software-update';
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
let prisma, server, stopClassroom, lostReply;

// Model a gateway losing an acknowledgement after KV has already committed.
// The destination is pinned to this run's loopback fixture for every request.
async function loseEquipmentReply(origin) {
  const target = new URL(origin);
  let committedRequests = 0;
  const relay = createHttpServer((req, res) => {
    const forward = httpRequest({ hostname: '127.0.0.1', port: target.port, method: req.method,
      path: req.url, headers: { ...req.headers, host: target.host } }, upstream => {
      if (req.method === 'POST' && req.url.endsWith('/equipment') && upstream.statusCode === 200) {
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
  return { server: relay, origin: 'http://127.0.0.1:' + relay.address().port, count: () => committedRequests };
}

function run(command, args) {
  const logfile = join(directory, basename(command) + '.log');
  const log = openSync(logfile, 'a');
  let result;
  try {
    result = spawnSync(command, args, { cwd: kv, windowsHide: true, stdio: ['ignore', log, log] });
  } finally {
    closeSync(log);
  }
  if (result.error || result.status !== 0) throw new Error(`${basename(command)} failed. Log: ${logfile}`);
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
  run(pg('pg_ctl'), ['-D', data, '-l', join(directory, 'postgres.log'), '-o', `-h 127.0.0.1 -p ${port}`, '-w', 'start']);
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
}

async function seed() {
  const { localProviderId } = await fromKv('domain/localAccount.js');
  const { secretHash } = await fromKv('domain/npep/wire.js');
  const { schoolManagementRepository } = await fromKv('services/npepSchoolManagementService.js');
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
  const term = await prisma.academicTerm.create({ data: {
    schoolId: school.id, academicYear: 2026, semester: 1, status: 'ACTIVE', name: '秋季学期',
  } });
  const grade = await prisma.grade.create({ data: { termId: term.id, name: '高二', code: 'G2', sortOrder: 1 } });
  const devices = [], classes = [], revokedDeviceId = randomUUID();
  let classroomBearer;
  for (let number = 1; number <= 3; number++) {
    const room = await prisma.workspace.create({ data: {
      termId: term.id, gradeId: grade.id, name: `高二（${number}）班`, code: `G2-${number}`, type: 'ADMIN_CLASS',
    } });
    classes.push(room.id);
    if (number === 3) continue;
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
  return { school, other, term, classes, devices, revokedDeviceId, pin, identity, classroomBearer };
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

let passed = false;
try {
  // Node's POSIX file modes have no ACL equivalent on Windows. Restrict the
  // new directory before any credentials, database files, or logs are written.
  if (process.platform === 'win32') {
    const account = spawnSync('whoami', [], { windowsHide: true, encoding: 'utf8' });
    assert.equal(account.status, 0);
    const restricted = spawnSync('icacls', [directory, '/inheritance:r', '/grant:r',
      account.stdout.trim() + ':(OI)(CI)F', '*S-1-5-18:(OI)(CI)F'], { windowsHide: true });
    assert.equal(restricted.status, 0, 'Could not restrict temporary fixture access');
  }
  await createDatabase();
  ({ prisma } = await fromKv('utils/prisma.js'));
  const fixture = await seed();
  const { default: app } = await fromKv('app.js');
  server = await new Promise((res, rej) => {
    const instance = app.listen(0, '127.0.0.1', () => res(instance));
    instance.once('error', rej);
  });
  const origin = 'http://127.0.0.1:' + server.address().port;
  stopClassroom = await startClassroomDevice(origin, fixture.identity, fixture.classroomBearer,
    { checkIncompleteReceipts: !serve && !localAuthorization, simulateLocalAuthorization: localAuthorization,
      lagAfterLocalChange: localAuthorization && !serve });
  await verify(origin, fixture);
  if (softwareUpdate) {
    await stopClassroom();
    stopClassroom = null;
  }
  // Only local, short-lived, synthetic login data is made available to the desktop process.
  const access = join(directory, 'desktop.json');
  if (!serve && !softwareUpdate) lostReply = await loseEquipmentReply(origin);
  await writeFile(access, JSON.stringify({
    origin, schoolCode: fixture.school.code, username: 'admin', viewer: 'viewer', password: fixture.pin,
    schoolId: fixture.school.id, otherSchoolId: fixture.other.id, revokedDeviceId: fixture.revokedDeviceId,
    lostReplyOrigin: lostReply?.origin,
    ...(softwareUpdate ? { classroomBearer: fixture.classroomBearer, deviceIdentity: fixture.identity } : {}),
  }), { mode: 0o600 });
  const smoke = fileURLToPath(new URL('./NPEduConsole.Smoke/NPEduConsole.Smoke.csproj', import.meta.url));
  if (!(serve && softwareUpdate)) await new Promise((res, rej) => {
    // The KV HTTP listener and database pool run on this event loop.
    const options = softwareUpdate ? ['--software-update'] : localAuthorization ? [serve ? '--view-local-authorization' : '--local-authorization'] :
      serve ? ['--view-only'] : [];
    const client = spawn('dotnet', ['run', '--project', smoke, '-c', 'Release', '--', access, ...options],
      { windowsHide: true, stdio: ['ignore', 'inherit', 'inherit'] });
    client.once('error', rej);
    client.once('close', code => code === 0 ? res() : rej(new Error('Desktop client smoke failed: ' + code)));
  });
  if (!serve && !softwareUpdate) {
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
  passed = true;
} finally {
  // Finish outstanding device traffic before closing the local server and data store.
  if (stopClassroom) {
    try { await stopClassroom(); }
    catch (error) { passed = false; process.exitCode = 1; console.error('Fixture device stopped:', error.message); }
  }
  for (const listener of [lostReply?.server, server].filter(Boolean)) {
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
