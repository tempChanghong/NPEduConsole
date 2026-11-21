import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {createServer, request} from 'node:http';

export async function prepareClassBatch(prisma, origin, fixture) {
  const {school, other, term, pin} = fixture;
  const grade = await prisma.grade.create({data: {termId: term.id, code: 'G1', name: '高一'}});
  const next = await prisma.academicTerm.create({data: {
    schoolId: school.id, academicYear: term.academicYear, semester: 2, name: '新启用学期', status: 'DRAFT',
  }});
  const base = `/api/v2/admin/schools/${school.id}`;
  const path = `${base}/current-term/administrative-classes/batch`;
  const target = new URL(origin);
  let posts = 0;
  let readbacks = 0;
  let rejectedBeforeAnyWrite = false;
  let dropped = false;
  let relayError;
  const requestIds = [];
  const checkedIds = [];
  const server = createServer(async (req, res) => {
    const submit = req.method === 'POST' && req.url === path;
    if (req.method === 'GET' && req.url.startsWith(path + '/')) {
      readbacks += 1;
      checkedIds.push(new URL(req.url, target).pathname.split('/').at(-1));
    }
    try {
      const attempt = submit ? ++posts : null;
      let submittedBody;
      if (submit) {
        const chunks = [];
        for await (const chunk of req) chunks.push(chunk);
        submittedBody = Buffer.concat(chunks);
        requestIds.push(JSON.parse(submittedBody).requestId);
      }
      if (attempt === 1) {
        // The first validation succeeds; another teaching space claims row 7
        // between that preview and the actual write.
        await prisma.workspace.create({data: {
          termId: term.id, gradeId: grade.id, code: 'G1-C07', name: '已占用代码的走班', type: 'COURSE_GROUP',
        }});
      }
      const forward = request({hostname: '127.0.0.1', port: target.port, path: req.url, method: req.method,
        headers: {...req.headers, host: target.host}}, async upstream => {
        try {
          if (attempt === 1) {
            assert.equal(upstream.statusCode, 409);
            assert.equal(await prisma.workspace.count({where: {termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS'}}), 0);
            const [rows] = await prisma.$queryRaw`SELECT count(*)::int AS count FROM "AdministrativeClassBatchCreation"
                WHERE "schoolId" = ${school.id}`;
            assert.equal(rows.count, 0);
            rejectedBeforeAnyWrite = true;
          }
          if (attempt === 2) {
            assert.equal(upstream.statusCode, 201);
            upstream.resume();
            upstream.on('end', () => {
              dropped = true;
              res.writeHead(502).end();
            });
            return;
          }
          res.writeHead(upstream.statusCode, upstream.headers);
          upstream.pipe(res);
        } catch (error) {
          relayError = error;
          upstream.resume();
          res.writeHead(500).end();
        }
      });
      forward.on('error', error => {
        relayError = error;
        if (!res.headersSent) res.writeHead(502);
        res.end();
      });
      if (submittedBody) forward.end(submittedBody);
      else req.pipe(forward);
    } catch (error) {
      relayError = error;
      res.writeHead(500).end();
    }
  });
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));

  async function manager(username) {
    const response = await fetch(origin + '/accounts/local/login', {
      method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({schoolCode: school.code, username, password: pin}),
    });
    assert.equal(response.status, 200);
    return {Authorization: 'Bearer ' + (await response.json()).data.access_token, 'Content-Type': 'application/json'};
  }
  const get = (resource, headers) => fetch(origin + resource, {headers});
  const post = (resource, headers, payload) => fetch(origin + resource, {method: 'POST', headers, body: JSON.stringify(payload)});
  const receiptPath = requestId => `${path}/${requestId}?termId=${term.id}&gradeId=${grade.id}`;
  const row = code => ({code, name: `回归 ${code}`, isStudentSelectable: false});

  return {
    server,
    access: {origin: `http://127.0.0.1:${server.address().port}`, directOrigin: origin, batchGradeId: grade.id,
      termId: term.id, allowClassBatchTestWrites: true},
    async checkDesktop() {
      if (relayError) throw relayError;
      assert.equal(posts, 2, 'only the rejected attempt and the corrected batch use POST');
      assert.ok(rejectedBeforeAnyWrite && dropped && readbacks >= 1);
      assert.notEqual(requestIds[0], requestIds[1], 'an edited payload gets a fresh request ID');
      assert.ok(checkedIds.every(id => id === requestIds[1]), 'reopening the dialog reads the original committed request ID');
      const receipts = await prisma.$queryRaw`SELECT "requestId", classes FROM "AdministrativeClassBatchCreation" WHERE "schoolId" = ${school.id}`;
      assert.equal(receipts.length, 1);
      assert.equal(receipts[0].requestId, requestIds[1]);
      const {classes} = receipts[0];
      assert.equal(classes.length, 12);
      assert.equal(classes[6].code, 'G1-C07-FIX');
      assert.equal(classes[1].isStudentSelectable, false);
      assert.equal(classes[0].isStudentSelectable, true);
      assert.equal(new Set(classes.map(item => item.id)).size, 12);
      assert.equal(await prisma.workspace.count({where: {termId: term.id, gradeId: grade.id, type: 'ADMIN_CLASS'}}), 12);
      const chosen = classes[2];
      assert.equal(chosen.code, 'G1-C03');
      const student = await prisma.administrativeClassStudent.findFirstOrThrow({where: {administrativeClassId: chosen.id, isActive: true}});
      assert.equal(student.name, '批量首份名单');
      assert.equal(await prisma.auditLog.count({where: {entityId: grade.id, action: 'ADMIN_CLASSES_BATCH_CREATED', success: true}}), 1);
      console.log('PASS: 12 rows, row 7 conflict with zero writes, one recovered receipt and one student on class 3.');
      return {selectedClassId: chosen.id, createdClassIds: classes.map(item => item.id), studentId: student.id};
    },
    async verifyOtherPaths() {
      const admin = await manager('admin');
      const second = await manager('admin2');
      const first = {termId: term.id, gradeId: grade.id, requestId: randomUUID(), classes: [row('RACE-01'), row('RACE-02')]};
      const another = {...first, requestId: randomUUID()};
      const attempts = await Promise.all([post(path, admin, first), post(path, second, another)]);
      assert.deepEqual(attempts.map(r => r.status).sort(), [201, 409]);
      const won = attempts[0].status === 201 ? 0 : 1;
      const [winner, winnerHeaders] = won === 0 ? [first, admin] : [another, second];
      const [loser, loserHeaders] = won === 0 ? [another, second] : [first, admin];
      const {data: wonReceipt} = await attempts[won].json();
      const {details} = await attempts[1 - won].json();
      assert.deepEqual(details.rows.map(item => item.row), [1, 2]);
      assert.equal((await (await get(receiptPath(loser.requestId), loserHeaders)).json()).data.classes, null);
      const stolen = await get(receiptPath(winner.requestId), loserHeaders);
      assert.equal(stolen.status, 409);
      assert.equal((await stolen.json()).code, 'CLASS_BATCH_REQUEST_CONFLICT');
      assert.deepEqual((await (await get(receiptPath(winner.requestId), winnerHeaders)).json()).data, wonReceipt);

      const forbidden = await post(path.replace(school.id, other.id), admin, first);
      assert.equal(forbidden.status, 403);
      assert.equal((await forbidden.json()).code, 'SCHOOL_ADMIN_REQUIRED');
      const stale = {...first, requestId: randomUUID(), classes: [row('STALE-01')]};
      assert.equal((await post(path + '/validate', admin, stale)).status, 200);
      await prisma.$transaction([
        prisma.academicTerm.update({where: {id: term.id}, data: {status: 'ARCHIVED'}}),
        prisma.academicTerm.update({where: {id: next.id}, data: {status: 'ACTIVE'}}),
      ]);
      const rejected = await post(path, admin, stale);
      assert.equal(rejected.status, 409);
      assert.equal((await rejected.json()).code, 'CLASS_BATCH_TERM_CHANGED');
      assert.equal(await prisma.workspace.count({where: {termId: term.id, code: 'STALE-01'}}), 0);
      assert.deepEqual((await (await get(receiptPath(winner.requestId), winnerHeaders)).json()).data, wonReceipt);

      const legacy = await post(base + '/administrative-classes/batch', admin, {termId: term.id, classes: [
        {...row('WEB-01'), gradeId: grade.id}, {...row('WEB-02'), gradeId: grade.id},
      ]});
      assert.equal(legacy.status, 201);
      const {data} = await legacy.json();
      assert.equal(data.count, 2);
      assert.ok(data.classes.every(item => item.termId === term.id));
      await post('/accounts/logout', admin);
      await post('/accounts/logout', second);
      console.log('PASS: two administrators raced; only the winning account owns the receipt; stale term and cross-school denied; historical web batch preserved.');
    },
  };
}
