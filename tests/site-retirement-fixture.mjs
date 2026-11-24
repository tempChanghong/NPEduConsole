import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {copyFile, mkdir, open, readFile, writeFile} from 'node:fs/promises';
import {join} from 'node:path';

export const siteSections = ['audit', 'students', 'npep-seating', 'npep-study', 'organization', 'migration',
  'terms', 'accounts', 'structure', 'teachers', 'screens', 'npep-devices', 'overview'];

const declarationPath = '/.well-known/npclassworks-deployment.json';

export async function publishSiteRelease({directory, site, zip, manifestPath, manifest}) {
  const downloadDir = join(directory, 'walking-web-build', 'downloads', 'npeduconsole');
  await mkdir(downloadDir, {recursive: true});
  await copyFile(manifestPath, join(downloadDir, 'console-release.json'));
  await copyFile(zip, join(downloadDir, manifest.fileName));
  const response = await fetch(site + declarationPath);
  assert.equal(response.status, 200);
  const declaration = await response.json();
  assert.equal(declaration.adminManagementHandoffPilot, true);
  assert.deepEqual(new Set(declaration.adminRetiredSections), new Set(siteSections), 'The actual site build must emit all 13 switches.');
}

// A genuine server receipt and its matching browser session record represent a tab left open during cutover.
export async function prepareTermOperation({prisma, origin, school, token}) {
  const term = await prisma.academicTerm.create({data: {
    schoolId: school.id, academicYear: 2031, semester: 1, status: 'DRAFT', name: '切换前的原学期',
  }});
  const base = origin + '/api/v2/admin/schools/' + school.id + '/term-preparation/terms/' + term.id;
  async function post(path, body) {
    const response = await fetch(base + path, {method: 'POST', headers: {
      Authorization: 'Bearer ' + token, 'Content-Type': 'application/json',
    }, body: JSON.stringify(body)});
    assert.ok(response.ok, 'Source term operation returned ' + response.status);
    return (await response.json()).data;
  }
  const options = {action: 'ARCHIVE', rebindScreens: false};
  const preview = await post('/status-preview', options);
  const body = {...options, requestId: randomUUID(), proof: preview.proof, force: false};
  const result = await post('/status', body);
  assert.equal(result.receipt?.term?.id, term.id);
  return {kind: 'status', school: school.id, term: term.id, label: term.name, body};
}

async function verifyRejectedDownload({page, directory, evidence, manifest}) {
  const downloads = [];
  const onDownload = file => downloads.push(file.suggestedFilename());
  const path = join(directory, 'walking-web-build', 'downloads', 'npeduconsole', manifest.fileName);
  // Change only the fixture's served copy, preserving its size and advertised digest.
  const file = await open(path, 'r+');
  const byte = Buffer.alloc(1);
  let changed = false;
  try {
    assert.equal((await file.read(byte, 0, 1, 0)).bytesRead, 1);
    await file.write(Buffer.from([byte[0] ^ 1]), 0, 1, 0);
    changed = true;
    page.on('download', onDownload);
    const zipResponse = page.waitForResponse(response => response.url().endsWith(manifest.fileName) && response.request().method() === 'GET');
    await page.getByRole('button', {name: '获取 Windows 管理端'}).click();
    assert.equal((await zipResponse).status(), 200);
    await page.getByRole('status').filter({hasText: /均已停办/}).waitFor();
    await page.getByRole('button', {name: /重试下载/}).waitFor();
    assert.equal(await page.getByRole('link', {name: /继续使用网页版|查看网页版可办栏目/}).count(), 0);
    await page.getByRole('link', {name: '浏览器故障诊断'}).waitFor();
    await page.screenshot({path: join(evidence, 'edge-rejected-download.png')});
    assert.deepEqual(downloads, []);
  } finally {
    page.off('download', onDownload);
    try {
      if (changed) await file.write(byte, 0, 1, 0);
    } finally {
      await file.close();
    }
  }
}

export async function verifySiteDestinations({page, site, directory, evidence, school, archive, managerId, operation, manifest}) {
  const destination = site + '/school-management?school=' + school.id;
  const switchFile = join(directory, 'walking-web-build', '.well-known', 'npclassworks-deployment.json');
  const original = await readFile(switchFile, 'utf8');
  const requests = [];
  const onRequest = request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/v2/admin/')) requests.push({method: request.method(), path: url.pathname, term: url.searchParams.get('termId')});
  };
  page.on('request', onRequest);
  try {
    await page.goto(destination);
    await page.waitForFunction(() => navigator.serviceWorker.controller !== null);
    const diagnostics = page.getByRole('link', {name: '浏览器故障诊断'});
    await diagnostics.waitFor();
    assert.equal(await page.getByRole('link', {name: /继续使用网页版|查看网页版可办栏目/}).count(), 0);
    await page.getByRole('button', {name: '获取 Windows 管理端'}).waitFor();
    await page.screenshot({path: join(evidence, 'edge-entire-site.png')});
    await verifyRejectedDownload({page, directory, evidence, manifest});
    assert.deepEqual(requests, []);

    const key = 'npclassworks:web-term-operation:' + JSON.stringify([site, managerId, school.id]);
    await page.evaluate(({key, operation}) => sessionStorage.setItem(key, JSON.stringify(operation)), {key, operation});
    await page.reload();
    const pendingLink = page.getByRole('link', {name: '核对原网页学期操作'});
    await pendingLink.waitFor();
    const target = new URL(await pendingLink.getAttribute('href'), site);
    assert.deepEqual(Object.fromEntries(target.searchParams), {school: school.id, section: 'terms', term: operation.term});
    await pendingLink.click();
    const receiptPath = '/api/v2/admin/schools/' + school.id + '/term-preparation/status-receipts/' + operation.body.requestId;
    const receiptResponse = page.waitForResponse(response => new URL(response.url()).pathname === receiptPath);
    await page.getByRole('button', {name: '只读核对原操作'}).click();
    assert.equal((await receiptResponse).status(), 200);
    await page.waitForFunction(key => sessionStorage.getItem(key) === null, key);
    assert.deepEqual(requests.filter(({path}) => path.includes('/term-preparation/')), [{method: 'GET', path: receiptPath, term: null}]);
    assert.equal(requests.filter(({method}) => method !== 'GET').length, 0);
    const declaration = JSON.parse(original);
    declaration.adminRetiredSections = declaration.adminRetiredSections.filter(section => section !== 'overview');
    await writeFile(switchFile, JSON.stringify(declaration));
    await page.goto(destination);
    const webLink = page.getByRole('link', {name: '查看网页版可办栏目'});
    await webLink.waitFor();
    const entry = new URL(await webLink.getAttribute('href'), site);
    assert.deepEqual(Object.fromEntries(entry.searchParams), {school: school.id, section: 'entry'});

    const archiveUrl = site + '/classworks-admin?section=overview&school=' + school.id + '&term=' + archive.id;
    const overviewResponse = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/management-overview'));
    await page.goto(archiveUrl);
    const restored = await overviewResponse;
    const data = (await restored.json()).data;
    assert.equal(restored.status(), 200);
    assert.equal(data.term.id, archive.id);
    assert.equal(new URL(restored.url()).pathname, '/api/v2/admin/schools/' + school.id + '/management-overview');
    await page.getByText(archive.name, {exact: false}).first().waitFor();
    await page.screenshot({path: join(evidence, 'edge-one-section-restored.png')});
    const beforeAccounts = requests.length;
    await page.goto(site + '/classworks-admin?section=accounts&school=' + school.id);
    await page.getByRole('region', {name: '学校管理办理去向'}).getByRole('heading', {name: /已停止办理/}).waitFor();
    assert.deepEqual(requests.slice(beforeAccounts).filter(({path}) => /accounts|management-overview/.test(path)), []);
    await writeFile(join(evidence, 'site-retirement-result.json'), JSON.stringify({
      school: school.id, sections: siteSections, archivedTerm: archive.id, recoveredReceipt: operation.body.requestId,
      readOnlyTerm: operation.term, requests, releasedSection: 'overview', accountsStillRetired: true,
      rejectedDownload: {http: 200, bytes: manifest.sizeBytes, filesSaved: 0},
      activeServiceWorker: await page.evaluate(() => navigator.serviceWorker.controller !== null),
    }, null, 2));
  } finally {
    page.off('request', onRequest);
    await writeFile(switchFile, original);
  }
}
