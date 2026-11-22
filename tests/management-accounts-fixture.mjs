import assert from 'node:assert/strict';
import {createHash, randomUUID} from 'node:crypto';
import {spawn} from 'node:child_process';
import {createServer, request} from 'node:http';
import {createRequire} from 'node:module';
import {mkdir, writeFile} from 'node:fs/promises';
import {dirname, join} from 'node:path';
import {pathToFileURL} from 'node:url';
import {isolatedIdentityProvider} from './school-teacher-allocation-fixture.mjs';

export async function verifyManagementAccounts({prisma, origin, fixture, fromKv, kvRequire, bcrypt, directory, evidence, web, runDesktop}) {
  await mkdir(evidence, {recursive: true});
  const {school, other, term, managerId, pin, classes, devices} = fixture;
  const [classId] = classes;
  const [deviceId] = devices;
  const newPin = '739641', heirPin = '312568';
  const teacher = await prisma.account.findFirstOrThrow({where: {localUsername: 'viewer'}});
  await prisma.account.update({where: {id: teacher.id},data: {name: '教学组候选',localPasswordHash:null}});
  await prisma.school.update({where:{id:school.id},data:{teacherAuthMode:'SHARED_PASSWORD',teacherSharedPasswordHash:await bcrypt.hash(pin,10)}});
  const subject = await prisma.subject.create({data:{schoolId:school.id,code:'PHYS-MGMT',name:'物理'}});
  await prisma.workspaceMember.create({data:{workspaceId:classId,accountId:teacher.id,role:'TEACHER'}});
  await prisma.teachingAssignment.create({data:{workspaceId:classId,accountId:teacher.id,subjectId:subject.id,position:'PRIMARY'}});
  await prisma.academicTerm.update({where:{id:term.id},data:{status:'ARCHIVED'}});
  const layer = '/api/v2/admin/schools/' + school.id;
  const management = layer + '/management-accounts';
  const date = new Intl.DateTimeFormat('sv-SE',{timeZone:'Asia/Shanghai'}).format(new Date());
  const photoId = randomUUID();
  const jpeg = kvRequire('jpeg-js').encode({width:12,height:8,data:Buffer.alloc(12*8*4,240)},75).data;
  const photoHash = createHash('sha256').update(jpeg).digest('hex');
  const {NpepPhotoStore}=await fromKv('services/npepPhotoStore.js');
  await new NpepPhotoStore().put(photoId,jpeg);
  await prisma.npepStudyPhoto.create({data:{eventId:photoId,deviceId,schoolId:school.id,termId:term.id,classId,leaseId:randomUUID(),
    version:'a'.repeat(64),metadata:{schoolNow:date+'T19:00:00.000',reason:'Noise'},digest:photoHash,storedHash:photoHash,
    width:12,height:8,byteCount:jpeg.length,capturedAt:new Date(),expiresAt:new Date(Date.now()+86400000),state:'Stored'}});
  const photoPath='/api/v2/npep/schools/'+school.id+'/study-photos?termId='+term.id+'&classId='+classId;
  async function api(path, {token, method='GET', body, status=200, photo=false}={}) {
    const response = await fetch(origin + path,{method,headers:{...(token?{Authorization:'Bearer '+token}:{}),
      'Content-Type':'application/json',...(photo?{'X-NPEP-Version':'0.15','X-Request-Id':randomUUID()}:{})},
      body:body===undefined?undefined:JSON.stringify(body)});
    const output = await response.json();
    assert.equal(response.status,status,path+' '+(output.code??output.error?.code??''));
    return output.data??output;
  }
  const login = async (username,password=pin,schoolCode=school.code,status=200) => api('/accounts/local/login',
    {method:'POST',body:{schoolCode,username,password},status});
  const [owner, secondAdmin, oldTeacher]=await Promise.all([login('admin'),login('admin2'),login('viewer')]);
  const ownerToken=owner.access_token, teacherToken=oldTeacher.access_token;
  const statusOf = (token, status) => api(photoPath,{token,status,photo:true});
  const memberships = token => api('/api/v2/me/schools',{token});
  const scopes = token => api('/api/v2/me/workspaces',{token});
  const getDirectory = token => api(management,{token});
  async function change(command, token=ownerToken) {
    const preview = await api(management+'/preview',{token,method:'POST',body:command});
    assert.ok(preview.canConfirm);
    return api(management,{token,method:'POST',body:{...command,requestId:randomUUID(),expectedFingerprint:preview.fingerprint}});
  }
  const listing=await getDirectory(ownerToken);
  assert.equal(listing.actorRole,'OWNER');
  const ownerEvidence=listing.accounts.find(row=>row.id===managerId);
  assert.ok(Math.abs(Date.parse(ownerEvidence.lastLoginAt)-Date.parse(ownerEvidence.managementLoginAt))<30_000,
    'login and management evidence from the same request must have consistent UTC timestamps');
  assert.equal((await prisma.academicTerm.count({where:{schoolId:school.id,status:'ACTIVE'}})),0);
  await api(management+'/preview',{token:secondAdmin.access_token,method:'POST',body:{action:'ROLE',accountId:teacher.id,role:'ADMIN'},status:403});
  await api(layer+'/local-accounts/'+managerId,{token:secondAdmin.access_token,method:'PATCH',body:{disabled:true},status:403});
  await api(layer+'/local-admins',{token:secondAdmin.access_token,method:'POST',body:{username:'forbidden',name:'禁止',pin:newPin},status:403});
  const browser = prepareManagementBrowser({web,directory,evidence,origin,school,owner,secondAdmin});
  browser.catch(()=>{});
  const steps=['no-active-term','admin-cannot-manage-administrators'];
  const provider=await isolatedIdentityProvider(fromKv,origin);
  try {
    const email='claim-management@example.test', handoff=await provider.begin(email);
    const first=await provider.exchange(handoff.code,handoff.verifier);
    const federated=await prisma.account.findFirstOrThrow({where:{email}});
    await prisma.schoolMember.createMany({data:[
      {schoolId:school.id,accountId:federated.id,role:'VIEWER'},
      {schoolId:other.id,accountId:federated.id,role:'ADMIN'},
    ]});
    const preGrant=await provider.begin(email);
    await change({action:'ROLE',accountId:federated.id,role:'ADMIN'});
    const delayed=await provider.exchange(preGrant.code,preGrant.verifier);
    for(const session of [first,delayed]){
      assert.deepEqual((await memberships(session.access_token)).map(row=>[row.schoolId,row.role]).sort(),
        [[school.id,'VIEWER'],[other.id,'ADMIN']].sort());
      await statusOf(session.access_token,403);
    }
    const fresh=await provider.begin(email);
    const promoted=await provider.exchange(fresh.code,fresh.verifier);
    await statusOf(promoted.access_token,200);
    assert.equal((await getDirectory(ownerToken)).accounts.find(row=>row.id===federated.id).managementLoginMethod,'OAUTH');
    await change({action:'ROLE',accountId:federated.id,role:'VIEWER'});
    await statusOf(promoted.access_token,403);
    assert.equal((await memberships(promoted.access_token)).find(row=>row.schoolId===other.id).role,'ADMIN');
    steps.push('oauth-fresh-proof-pregrant-handoff-and-other-school-preserved');
  } finally { await provider.close(); }
  let savedTeacherToken, lost=false, receiptReads=0, heirId, droppedHeirRequest, delayedRequest;
  const relay = createServer(async (req,res)=>{
    if(req.method==='POST'&&req.url.startsWith('/__management/')){
      const step=req.url.slice('/__management/'.length);
      try {
        if(step==='teacher-granted'){
          await statusOf(teacherToken,403);
          await api(management,{token:teacherToken,status:403});
          assert.equal((await memberships(teacherToken)).find(row=>row.schoolId===school.id).role,'VIEWER');
          assert.equal((await getDirectory(ownerToken)).accounts.find(row=>row.id===teacher.id).awaitsManagementLogin,true);
          assert.ok(lost && receiptReads===1);
          await prisma.academicTerm.update({where:{id:term.id},data:{status:'ACTIVE'}});
          assert.deepEqual((await scopes(teacherToken)).map(row=>row.workspace.id),[classId]);
          savedTeacherToken=(await login('viewer',newPin)).access_token;
          assert.equal((await statusOf(savedTeacherToken,200)).items[0].eventId,photoId);
          await statusOf(teacherToken,403);
        } else if(step==='teacher-demoted'){
          await statusOf(savedTeacherToken,403);
          await api(management,{token:savedTeacherToken,status:403});
          assert.deepEqual((await scopes(savedTeacherToken)).map(row=>row.workspace.id),[classId]);
          assert.equal(await prisma.teachingAssignment.count({where:{workspaceId:classId,accountId:teacher.id,isActive:true}}),1);
        } else if(step==='missing-request-retried'){
          const row=await prisma.account.findFirstOrThrow({where:{localUsername:'successor'}});
          const saved=await prisma.$queryRaw`SELECT result FROM "SchoolManagementAccountReceipt"
            WHERE "schoolId"=${school.id} AND "requestId"=${droppedHeirRequest}::uuid`;
          assert.equal(saved.length,1);
          assert.equal(saved[0].result.accountId,row.id);
        } else if(step==='late-receipt-kept-original-pin'){
          const row=await prisma.account.findFirstOrThrow({where:{localUsername:'late-receipt'}});
          const saved=await prisma.$queryRaw`SELECT result FROM "SchoolManagementAccountReceipt"
            WHERE "schoolId"=${school.id} AND "requestId"=${delayedRequest.id}::uuid`;
          assert.equal(saved.length,1);
          assert.equal(saved[0].result.accountId,row.id);
          await login('late-receipt',heirPin);
          await login('late-receipt',newPin,school.code,401);
        } else if(step==='heir-created'){
          heirId=(await getDirectory(ownerToken)).accounts.find(row=>row.username==='successor').id;
          assert.ok(heirId);
          await api(layer+'/local-accounts/'+heirId,{token:ownerToken,method:'PATCH',body:{disabled:true}});
        } else if(step==='heir-disabled'){
          await login('successor',heirPin,school.code,403);
          await api(layer+'/local-accounts/'+heirId,{token:ownerToken,method:'PATCH',body:{disabled:false}});
        } else if(step==='heir-verified'){
          const heir=await login('successor',heirPin);
          assert.equal((await getDirectory(heir.access_token)).actorRole,'OWNER');
        } else if(step==='handoff-done'){
          assert.deepEqual(await prisma.schoolMember.findMany({where:{schoolId:school.id,role:'OWNER'},select:{accountId:true}}),[{accountId:heirId}]);
          assert.equal((await getDirectory(ownerToken)).actorRole,'ADMIN');
          await api(management+'/preview',{token:ownerToken,method:'POST',body:{action:'ROLE',accountId:heirId,role:'VIEWER'},status:403});
        } else throw new Error('Unexpected checkpoint '+step);
        steps.push(step);res.writeHead(204).end();
      } catch(error){console.error(error);res.writeHead(500).end();}
      return;
    }
    if(req.method==='GET'&&req.url.startsWith(management+'/requests/')) receiptReads++;
    let body;
    if(req.method==='POST'&&req.url===management){
      try {
        const chunks=[];
        for await(const chunk of req) chunks.push(chunk);
        body=Buffer.concat(chunks);
        const command=JSON.parse(body);
        if(command.username==='successor'){
          if(!droppedHeirRequest){droppedHeirRequest=command.requestId;res.destroy();return;}
          assert.equal(command.requestId,droppedHeirRequest);
        } else if(command.username==='late-receipt'){
          if(!delayedRequest){delayedRequest={id:command.requestId,body};res.destroy();return;}
          assert.equal(delayedRequest.id,command.requestId);
          // Make the original write arrive between Console's read-only check and its retry reaching KV.
          const original=await fetch(origin+management,{method:'POST',headers:{Authorization:req.headers.authorization,
            'Content-Type':'application/json'},body:delayedRequest.body});
          assert.equal(original.status,200);
          assert.equal((await original.json()).data.replayed,undefined);
          delayedRequest.body=null;
        }
      } catch(error){console.error(error);res.writeHead(502).end();return;}
    }
    const target=new URL(req.url,origin);
    const forward=request(target,{method:req.method,headers:{...req.headers,host:target.host}},upstream=>{
      if(!lost&&req.method==='POST'&&req.url===management&&upstream.statusCode===200){
        lost=true;upstream.resume();upstream.on('end',()=>res.destroy());return;
      }
      res.writeHead(upstream.statusCode,upstream.headers);upstream.pipe(res);
    });
    forward.on('error',()=>res.destroy());
    if(body) forward.end(body); else req.pipe(forward);
  });
  await new Promise((resolve,reject)=>relay.once('error',reject).listen(0,'127.0.0.1',resolve));
  try {
    await (await browser).verify();
    steps.push('edge-duplicate-and-owner-only-controls');
    const config=join(directory,'management-desktop.json');
    await writeFile(config,JSON.stringify({origin:'http://127.0.0.1:'+relay.address().port,schoolId:school.id,schoolCode:school.code,
      ownerId:managerId,teacherId:teacher.id,classId,photoId,pin,newPin,heirPin,isolated:true}),{mode:0o600});
    await runDesktop(['--live-management-accounts',config,join(evidence,'console')]);
    const {localProviderId}=await fromKv('domain/localAccount.js');
    const recovery=await prisma.account.create({data:{provider:'school-local',providerId:localProviderId(other.code,'recoverowner'),
      localUsername:'recoverowner',name:'恢复验证',localDisabled:true,localPasswordHash:await bcrypt.hash(pin,10)}});
    await prisma.schoolMember.create({data:{schoolId:other.id,accountId:recovery.id,role:'OWNER'}});
    await api('/accounts/local/recover-owner',{method:'POST',body:{setupKey:process.env.BOOTSTRAP_SETUP_KEY,
      schoolCode:other.code,username:'recoverowner',newPin:heirPin}});
    await login('recoverowner',heirPin,other.code);
    const before=await prisma.account.findUniqueOrThrow({where:{id:managerId},select:{tokenVersion:true}});
    await api('/accounts/local/recover-owner',{method:'POST',status:404,body:{setupKey:process.env.BOOTSTRAP_SETUP_KEY,
      schoolCode:school.code,username:'admin',newPin:heirPin}});
    assert.equal((await prisma.account.findUniqueOrThrow({where:{id:managerId}})).tokenVersion,before.tokenVersion);
    await login('admin',pin);
    steps.push('server-recovery-key-continues');
    const receiptRows=await prisma.$queryRaw`SELECT digest,result FROM "SchoolManagementAccountReceipt" WHERE "schoolId"=${school.id}`;
    const serialized=JSON.stringify(receiptRows);
    for(const secret of [pin,newPin,heirPin]) assert.ok(!serialized.includes('"'+secret+'"'));
    assert.equal(receiptReads,5);
    await writeFile(join(evidence,'cross-end-result.json'),JSON.stringify({passed:true,checks:steps,originalOwner:managerId,
      successor:heirId,teacher:teacher.id,originalClass:classId,photoId,receiptReads,oauth:'real KV handoff through isolated HLY'},null,2));
  } finally {
    relay.closeAllConnections();await new Promise(resolve=>relay.close(resolve));
    try{await (await browser).close();}catch{}
  }
}

async function prepareManagementBrowser({web,directory,evidence,origin,school,owner,secondAdmin}) {
  const require=createRequire(join(web,'package.json'));
  const {chromium}=require('@playwright/test');
  const {preview}=await import(pathToFileURL(join(dirname(require.resolve('vite/package.json')),'dist/node/index.js')).href);
  const outDir=join(directory,'management-web');
  const frontend=await preview({configFile:false,root:web,build:{outDir},preview:{host:'127.0.0.1',port:0,proxy:{'/api':origin,'/accounts':origin}}});
  const url='http://127.0.0.1:'+frontend.httpServer.address().port;
  const close=async()=>{frontend.httpServer.closeAllConnections();await frontend.close();};
  try {
    await new Promise((resolve,reject)=>{
      const child=spawn(process.execPath,['node_modules/vite/bin/vite.js','build','--logLevel','error','--outDir',outDir],
        {cwd:web,env:{...process.env,VITE_DEFAULT_KV_SERVER:url,VITE_DEFAULT_AUTH_SERVER:url,VITE_SERVER_URL:url,VITE_ENABLE_ANALYTICS:'false'},
          windowsHide:true,stdio:'inherit'});
      child.once('error',reject);child.once('exit',code=>code===0?resolve():reject(new Error('Legacy web build failed')));
    });
    return {close,verify:async()=>{
      const browser=await chromium.launch({channel:'msedge',headless:true});
      async function pageFor(token){
        const page=await browser.newPage({viewport:{width:1360,height:900},serviceWorkers:'block'});
        await page.route('**/*',route=>new URL(route.request().url()).origin===url?route.continue():route.abort());
        await page.addInitScript(({access_token,refresh_token})=>{
          localStorage.setItem('classworks-v2-access-token',access_token);
          localStorage.setItem('classworks-v2-refresh-token',refresh_token);
        },token);
        await page.goto(url+'/classworks-admin?section=accounts&school='+school.id);
        await page.getByText('本地账号',{exact:true}).waitFor();
        return page;
      }
      try {
        const ownerPage=await pageFor(owner);
        await ownerPage.getByLabel('管理员短账号').fill('VIEWER');
        await ownerPage.getByLabel('管理员姓名').fill('重复目标');
        await ownerPage.getByLabel('初始 PIN').fill('572891');
        const pending=ownerPage.waitForResponse(response=>response.url().endsWith('/local-admins')&&response.status()===409);
        await ownerPage.getByRole('button',{name:'创建管理员',exact:true}).click();
        await pending;
        await ownerPage.getByText(/短账号已存在/).waitFor();
        await ownerPage.getByLabel('初始 PIN').fill('');
        await ownerPage.screenshot({path:join(evidence,'edge-duplicate.png')});
        const admin=await pageFor(secondAdmin);
        assert.equal(await admin.getByText('新建管理账号',{exact:true}).count(),0);
        const ownerRow=admin.getByText(/admin · @admin$/).locator('..').locator('..');
        assert.equal(await ownerRow.getByRole('button',{name:'重置 PIN'}).count(),0);
        await admin.screenshot({path:join(evidence,'edge-admin-readonly-managers.png')});
      } finally {await browser.close();}
    }};
  } catch(error){await close();throw error;}
}
