import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomInt} from 'node:crypto';
import {mkdir, realpath, rm, writeFile} from 'node:fs/promises';
import {createServer, request} from 'node:http';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {startTarget} from './school-transfer-fixture.mjs';

const setup = '/api/v2/setup';

async function listen(listener) {
  const server = createServer(listener);
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
  return {
    url: 'http://127.0.0.1:' + server.address().port,
    close: () => { server.closeAllConnections(); return new Promise(resolve => server.close(resolve)); }
  };
}

function declaration(res, api) {
  res.writeHead(200, {'Content-Type': 'application/json'}).end(JSON.stringify({schemaVersion: 1, apiBaseUrl: api}));
}

async function apiGateway(origin, sameOrigin, counts) {
  return listen((req, res) => {
    if (sameOrigin && req.url === '/.well-known/npclassworks-deployment.json') return declaration(res, null);
    if (!/^\/(api|accounts)(\/|$)/.test(req.url)) return res.writeHead(404).end();
    if (req.method === 'POST') {
      if (req.url === setup + '/session') counts.sessions++;
      if (req.url === setup + '/initialize') counts.initializations++;
      if (req.url === setup + '/desktop-complete') counts.completions++;
      if (req.url === setup + '/migration/import') counts.imports++;
    }
    const upstream = request(origin + req.url, {method: req.method, headers: req.headers}, reply => {
      res.writeHead(reply.statusCode, reply.headers);
      reply.pipe(res);
    });
    upstream.once('error', () => res.destroy());
    req.pipe(upstream);
  });
}

export async function verifyWindowsReleaseOobe(context) {
  const {prisma, origin, directory, evidence, kvRequire, zip, manifest} = context;
  assert.equal(process.platform, 'win32', 'The release OOBE test requires an interactive Windows host.');
  await mkdir(evidence, {recursive: true});
  const target = await startTarget(context);
  const {Client} = kvRequire('pg');
  const targetDb = new Client({connectionString: target.databaseUrl});
  const zero = () => ({sessions: 0, initializations: 0, completions: 0, imports: 0});
  const first = zero(), split = zero();
  const webHits = [];
  const listeners = [];
  let input;
  try {
    await targetDb.connect();
    const sameOrigin = await apiGateway(origin, true, first);
    listeners.push(sameOrigin);
    const splitApi = await apiGateway(target.origin, false, split);
    listeners.push(splitApi);
    const splitWeb = await listen((req, res) => {
      webHits.push([req.method, req.url]);
      if (req.url === '/.well-known/npclassworks-deployment.json') return declaration(res, splitApi.url);
      res.writeHead(404).end();
    });
    listeners.push(splitWeb);
    input = join(await realpath(directory), 'release-oobe.json');
    const pin = () => String(randomInt(100000, 999999));
    await writeFile(input, JSON.stringify({same: {url: sameOrigin.url, code: 'RELEASE-SAME', setupKey: process.env.BOOTSTRAP_SETUP_KEY, pin: pin()},
      split: {url: splitApi.url, website: splitWeb.url, code: 'RELEASE-SPLIT', setupKey: target.setupKey, pin: pin()}, isolated: true}));
    const script = fileURLToPath(new URL('./WindowsReleaseOobe.ps1', import.meta.url));
    await new Promise((resolve, reject) => {
      const args = ['-NoProfile', '-STA', '-File', script, '-FixturePath', input, '-ZipPath', zip, '-EvidenceDirectory', evidence];
      if (manifest) args.push('-ManifestPath', manifest);
      const process = spawn('pwsh.exe', args, {stdio: 'inherit', windowsHide: true});
      process.once('error', reject);
      process.once('exit', code => code === 0 ? resolve() : reject(new Error('Published Windows EXE acceptance failed: ' + code)));
    });
    assert.deepEqual(first, {sessions: 1, initializations: 1, completions: 1, imports: 0});
    assert.deepEqual(split, {sessions: 3, initializations: 1, completions: 1, imports: 0});
    assert.ok(webHits.length > 0 && webHits.every(([method, path]) => method === 'GET' && path === '/.well-known/npclassworks-deployment.json'));
    const original = await prisma.$queryRaw`SELECT s.id, s.code, t.name AS term, l.method
      FROM "School" s JOIN "AcademicTerm" t ON t."schoolId"=s.id AND t.status='ACTIVE'
      JOIN "InstanceSetup" i ON i."completedAt" IS NOT NULL
      JOIN "SchoolManagementLogin" l ON l."schoolId"=s.id AND l."accountId"=i."completedByAccountId"`;
    const {rows: separate} = await targetDb.query(`SELECT s.id, s.code, t.name AS term, l.method
      FROM "School" s JOIN "AcademicTerm" t ON t."schoolId"=s.id AND t.status='ACTIVE'
      JOIN "InstanceSetup" i ON i."completedAt" IS NOT NULL
      JOIN "SchoolManagementLogin" l ON l."schoolId"=s.id AND l."accountId"=i."completedByAccountId"`);
    for (const [rows, code, term, count] of [[original, 'RELEASE-SAME', '同源发行验收学期', 1], [separate, 'RELEASE-SPLIT', '分域发行验收学期', 2]]) {
      assert.equal(rows.length, count, 'actual owner sign-ins for initialization and later ordinary login');
      assert.ok(rows.every(row => row.code === code && row.term === term && row.method === 'LOCAL_PIN'));
      assert.equal(new Set(rows.map(row => row.id)).size, 1, 'the restart did not create a second school');
    }
    assert.notEqual(original[0].id, separate[0].id);
    await writeFile(join(evidence, 'release-oobe-database.json'), JSON.stringify({same: {schoolId: original[0].id, ...first},
      split: {schoolId: separate[0].id, ...split}, distinctDatabases: true, ownerLoginProof: true,
      splitWebReads: webHits.length, newSchoolWritesPerDatabase: 1}, null, 2));
  } finally {
    const closed = await Promise.allSettled([targetDb.end(), ...listeners.map(gateway => gateway.close()),
      ...(input ? [rm(input, {force: true})] : [])]);
    await target.close();
    for (const result of closed) if (result.status === 'rejected') throw result.reason;
  }
}
