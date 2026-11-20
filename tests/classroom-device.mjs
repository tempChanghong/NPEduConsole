import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

export const microphones = [{ id: 'microphone-usb', name: '讲台 USB 麦克风' }, { id: 'microphone-array', name: '教室阵列麦克风' }];
export const cameras = [{ id: 'camera-board', name: '板书摄像头' }];

// Simulate only the classroom's capture timing. Read policy and send receipts and
// presence through the same authenticated NPEP routes used by a real terminal.
export async function startClassroomDevice(origin, identity, bearer, { checkIncompleteReceipts = false } = {}) {
  const base = origin + '/api/v2/npep/device/';
  async function request(path, version, body) {
    const requestId = randomUUID();
    const headers = { Authorization: 'Bearer ' + bearer, 'X-NPEP-Version': version, 'X-Request-Id': requestId };
    const options = { headers };
    if (body) {
      headers['Content-Type'] = 'application/json';
      Object.assign(options, { method: 'POST', body: JSON.stringify({ ...body, requestId }) });
    }
    const response = await fetch(base + path, options);
    const message = await response.json();
    assert.ok(response.ok, path + ': ' + (message.error?.code ?? response.status));
    assert.equal(message.requestId, requestId);
    return message.data;
  }
  const session = await request('sessions', '0.1', { ...identity, runId: randomUUID(), expectedStatusEpoch: 0 });
  const { sessionId, runId, statusEpoch } = session;
  let sequence = 0, heartbeatSequence = 0, lastHeartbeat = 0;
  let applied = 5, selected = microphones[0].id, pendingRevision, pendingSince;
  const incomplete = checkIncompleteReceipts ? ['previous-input', 'missing-endpoint'] : [];
  async function tick() {
    const policy = await request('school-management', '0.16');
    const now = Date.now();
    if (now - lastHeartbeat >= 15000) {
      const bridge = { connection: 'READY', bridgeVersion: null };
      await request('status', '0.1', { ...identity, sessionId, statusEpoch,
        sequence: ++heartbeatSequence, sampleAgeMs: 0, status: {
          appVersion: 'fixture', mode: 'DAILY', modePhase: 'RUNNING', modeRevision: 1,
          automaticRecording: 'DISABLED', recording: 'IDLE', classIsland: bridge, examAware: bridge,
        } });
      lastHeartbeat = now;
    }
    let reportedRevision = applied;
    let reportedInput = selected, reportedMicrophones = microphones;
    if (policy.equipment.revision !== applied) {
      if (pendingRevision !== policy.equipment.revision) {
        pendingRevision = policy.equipment.revision;
        pendingSince = now;
      }
      if (now - pendingSince >= 6500) {
        reportedRevision = policy.equipment.revision;
        const scenario = incomplete.shift();
        if (scenario === 'missing-endpoint') {
          // A terminal can retain its selected ID while the USB input is disconnected.
          reportedInput = policy.equipment.microphoneId;
          reportedMicrophones = microphones.filter(m => m.id !== reportedInput);
        } else if (scenario !== 'previous-input') {
          applied = reportedRevision;
          selected = policy.equipment.microphoneId;
          reportedInput = selected;
        }
      }
    }
    await request('school-management/receipt', '0.16', {
      context: { sessionId, runId, statusEpoch, identity: policy.identity }, sequence: ++sequence, microphones: reportedMicrophones, cameras,
      unlocked: false, appliedRevision: reportedRevision, credentialRevision: 0, selectedMicrophoneId: reportedInput, selectedCameraId: cameras[0].id,
      error: reportedRevision === policy.equipment.revision ? null : 'EQUIPMENT_PENDING',
    });
  }
  await tick();
  const controller = new AbortController();
  const loop = (async () => {
    while (!controller.signal.aborted) {
      try { await delay(3000, null, { signal: controller.signal }); }
      catch (error) { if (controller.signal.aborted) break; throw error; }
      await tick();
    }
  })();
  loop.catch(() => {}); // Propagate failures when the fixture is closed.
  return async () => { controller.abort(); await loop; };
}
