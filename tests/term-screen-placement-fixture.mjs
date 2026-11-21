import assert from 'node:assert/strict';
import {createServer, request} from 'node:http';
import {join} from 'node:path';
import {writeFile} from 'node:fs/promises';

export async function verifyTermScreenPlacement({prisma, origin, school, termId, oldClassId, targetClass, bindingIds,
  activation, managerToken, secondToken, browserLogin, oldBrowserToken, directory, evidence, nativeState, nativeFixture,
  beforeNativeCopy, setNativeScope, runDesktop, runNative, api, pin}) {
  const base = '/api/v2/admin/schools/' + school.id;
  const placementBase = base + '/term-preparation';
  const [frontId, rearId, movedId] = bindingIds;
  const placementPath = placementBase + '/screen-placements/' + movedId;
  const confirmation = () => api(placementBase + '/activation-receipts/' + activation.requestId, {token: managerToken});
  const overview = () => api(base + '/management-overview/current?termPreparation=1&screenPlacement=1', {token: secondToken});
  const current = () => api(placementPath + '?termId=' + termId, {token: secondToken});
  const activeDevice = screenBindingId => prisma.npepDevice.findFirstOrThrow({where: {screenBindingId, state: 'ACTIVE'}});
  const binding = id => prisma.classroomScreenBinding.findUniqueOrThrow({where: {id}});
  const peerBefore = await activeDevice(frontId);
  const frontBinding = await binding(frontId), rearBinding = await binding(rearId), oldBinding = await binding(movedId);
  assert.equal(oldBinding.administrativeClassId, oldClassId);
  const retained = (await overview()).tasks.find(row => row.screenId === movedId && row.code === 'TERM_SCREEN_RETAINED_ORIGINAL');
  assert.equal(retained?.action, 'OPEN_TERM_SCREEN_PLACEMENT');
  assert.deepEqual((await current()).candidates.find(row => row.id === targetClass.id).screens.map(row => row.id).sort(),
    [frontId, rearId].sort());

  const browserSession = async (token, expected) => {
    const reply = await fetch(origin + '/api/v2/classroom-screens/session', {headers: {'X-Classworks-Screen-Token': token}});
    assert.equal(reply.status, expected);
    return (await reply.json()).data;
  };
  await browserSession(oldBrowserToken, 409);
  let writes = 0, reads = 0, receipt;
  const verified = [];
  async function checkpoint(step) {
    switch (step) {
    case 'binding-arranged': {
      assert.ok(receipt && writes === 1 && reads === 2);
      assert.equal(receipt.targetClassId, targetClass.id);
      assert.equal(receipt.bindingId, movedId);
      assert.equal(receipt.fromClassId, oldClassId);
      await api(placementBase + '/screen-placement-receipts/' + receipt.requestId, {token: managerToken, status: 404});
      const moved = await binding(movedId);
      assert.equal(moved.id, oldBinding.id);
      assert.equal(moved.administrativeClassId, targetClass.id);
      assert.equal(moved.credentialVersion, oldBinding.credentialVersion + 1);
      assert.equal(moved.npepBindingRevision, oldBinding.npepBindingRevision + 1);
      for (const key of ['loginCode', 'pinHash', 'deviceFingerprint', 'createdByAccountId', 'isActive'])
        assert.equal(moved[key], oldBinding[key]);
      assert.notEqual(moved.tokenHash, oldBinding.tokenHash);
      for (const row of [frontBinding, rearBinding]) {
        const unchanged = await binding(row.id);
        for (const key of ['administrativeClassId', 'credentialVersion', 'npepBindingRevision', 'tokenHash'])
          assert.equal(unchanged[key], row[key]);
      }
      await browserSession(oldBrowserToken, 401);
      const restoredWeb = await api('/api/v2/classroom-screens/login', {method: 'POST', body: browserLogin});
      assert.equal(restoredWeb.binding.id, movedId);
      assert.equal(restoredWeb.binding.administrativeClassId, targetClass.id);
      await browserSession(restoredWeb.token, 200);

      await setNativeScope([targetClass.id, targetClass.id, targetClass.id]);
      await runNative(['invalidated-third', nativeFixture, beforeNativeCopy]);
      await runNative(['report-first', nativeFixture, nativeState]);
      const peerAfter = await activeDevice(frontId);
      for (const key of ['id', 'credentialId', 'bindingRevision', 'screenBindingId', 'administrativeClassId'])
        assert.equal(peerAfter[key], peerBefore[key]);
      assert.ok(peerAfter.lastReportedAt > peerBefore.lastReportedAt && peerAfter.statusEpoch > peerBefore.statusEpoch);
      const info = await current();
      assert.equal(info.placement.status, 'AWAITING_PAIRING');
      assert.equal(info.placement.lastReportedAt, null);
      assert.ok((await overview()).tasks.some(row => row.screenId === movedId && row.code === 'SCREEN_REPAIR_REQUIRED'));
      assert.ok((await overview()).tasks.every(row => row.screenId !== movedId || row.code !== 'TERM_SCREEN_RETAINED_ORIGINAL'));
      const history = await confirmation();
      assert.deepEqual(history.receipt, activation);
      assert.equal(history.screens.find(row => row.bindingId === movedId).placement.source, 'CONSOLE');
      break;
    }
    case 'paired':
      await runNative(['repair-third', nativeFixture, nativeState]);
      assert.equal((await current()).placement.status, 'AWAITING_REPORT');
      assert.ok((await overview()).tasks.some(row => row.screenId === movedId && row.code === 'SCREEN_AWAITING_REPORT'));
      break;
    case 'reported': {
      await runNative(['report-third', nativeFixture, nativeState]);
      assert.equal((await current()).placement.status, 'REPORTED');
      const view = await overview();
      for (const screenId of [frontId, movedId]) {
        assert.equal(view.screens.find(row => row.screenId === screenId)?.native.state, 'REPORTED');
        assert.ok(view.tasks.every(row => row.screenId !== screenId || ![
          'TERM_SCREEN_RETAINED_ORIGINAL', 'SCREEN_AWAITING_PAIRING', 'SCREEN_REPAIR_REQUIRED',
          'SCREEN_AWAITING_REPORT', 'SCREEN_NEEDS_VERIFICATION',
        ].includes(row.code)));
      }
      assert.ok(view.tasks.some(row => row.screenId === rearId && row.code === 'SCREEN_REPAIR_REQUIRED'));
      assert.ok(view.tasks.some(row => row.screenId === movedId && row.code === 'SCREEN_WEB_AWAITING_HEARTBEAT'));
      break;
    }
    default: throw new Error('Unknown placement checkpoint');
    }
    verified.push(step);
  }

  const backend = new URL(origin);
  const relay = createServer(async (req, res) => {
    if (req.method === 'POST' && req.url.startsWith('/__placement/')) {
      try { await checkpoint(req.url.slice('/__placement/'.length)); res.writeHead(204).end(); }
      catch (error) { console.error(error); res.writeHead(500).end(); }
      return;
    }
    if (req.method === 'POST' && req.url === placementPath) writes++;
    if (req.method === 'GET' && req.url.startsWith(placementBase + '/screen-placement-receipts/')) {
      reads++;
      if (reads === 1) { res.writeHead(503).end(); return; }
    }
    const upstream = request(new URL(req.url, backend), {method: req.method, headers: {...req.headers, host: backend.host}}, reply => {
      if (!receipt && req.method === 'POST' && req.url === placementPath && reply.statusCode === 200) {
        const chunks = [];
        reply.on('data', chunk => chunks.push(chunk));
        reply.on('end', () => {
          receipt = JSON.parse(Buffer.concat(chunks)).data.receipt;
          res.destroy();
        });
      } else { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); }
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise((yes, no) => relay.once('error', no).listen(0, '127.0.0.1', yes));
  try {
    const access = join(directory, 'screen-placement-desktop.json');
    await writeFile(access, JSON.stringify({origin: 'http://127.0.0.1:' + relay.address().port, schoolId: school.id,
      schoolCode: school.code, password: pin, termId, targetId: targetClass.id, frontId, rearId, bindingId: movedId,
      allowPlacement: true}), {mode: 0o600});
    await runDesktop(['--live-term-screen-placement', access, evidence]);
    assert.deepEqual(verified, ['binding-arranged', 'paired', 'reported']);
    await runDesktop(['--live-term-screen-placement-reopen', access, evidence]);
    const placed = await activeDevice(movedId), peerAfter = await activeDevice(frontId);
    const arrangedAt = new Date(receipt.arrangedAt);
    assert.ok(peerBefore.lastReportedAt < arrangedAt && arrangedAt < peerAfter.lastReportedAt &&
      arrangedAt < placed.lastReportedAt, 'the server receipt UTC time falls between the native reports');
    await writeFile(join(evidence, 'screen-placement-result.json'), JSON.stringify({
      verified, receipt, writes, reads, oldBrowserSessionRejected: true, sameWebAccountRelogged: true,
      existingFront: {bindingId: frontId, deviceId: peerAfter.id, revision: peerAfter.bindingRevision,
        before: peerBefore.lastReportedAt, after: peerAfter.lastReportedAt},
      moved: {bindingId: movedId, deviceId: placed.id, revision: placed.bindingRevision, firstReport: placed.lastReportedAt},
      rearStillPending: rearId, activationHistoryUnchanged: true, passed: true,
    }, null, 2));
    console.log('PASS: retained screen arranged from both desktop entries, original front credential preserved, recovered own receipt, browser session replaced, only the relocated screen paired, both histories reopen.');
  } finally {
    relay.closeAllConnections();
    await new Promise(resolve => relay.close(resolve));
  }
}
