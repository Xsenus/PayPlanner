import sys;sys.dont_write_bytecode=True
import pathlib,json,hashlib,datetime,time,subprocess,importlib.util,ctypes,ctypes.wintypes as W,re,os
B=pathlib.Path('D:/PayPlannerDiagnostics');P=B/'dependency-fingerprint-uv12-plan-20261103';pins=json.loads((P/'pins.json').read_text());H='5441ed9c68d6d9de13bf80e42eb106b2c0be4136';MH='d4bf1cb6294f14b1afd6d7b1d4f240a454fe327ed7aee5c510b592b4920a2f79';S='one-retirement-dependency-fingerprint-uv12-5441ed9c';A=4831838208;IDLE={'id':21,'status':'online','busy':False};install=pathlib.Path('D:/PayPlannerRunner/agent')
def utc():return datetime.datetime.now(datetime.timezone.utc).isoformat()
def sha(f):
 h=hashlib.sha256()
 with pathlib.Path(f).open('rb') as q:
  for z in iter(lambda:q.read(1048576),b''):h.update(z)
 return h.hexdigest()
def cmd(args):
 r=subprocess.run(args,cwd=pins['workspace'],capture_output=True,text=True,timeout=20)
 if r.returncode:raise RuntimeError('STANDARD_METADATA_FAILED')
 return r.stdout.strip()
def gh(path,jq):return json.loads(cmd(['gh','api','repos/Xsenus/PayPlanner'+path,'--jq',jq]))
native_path=pathlib.Path(__file__).resolve().parent/'native_win.py';assert sha(native_path)=='798af10a95e21fc0df532d95f4c0935d331935f1b95298a5b359800be5491159';spec=importlib.util.spec_from_file_location('pinned_os_metadata',native_path);n=importlib.util.module_from_spec(spec);spec.loader.exec_module(n);K=n.K;K.QueryFullProcessImageNameW.argtypes=[W.HANDLE,W.DWORD,W.LPWSTR,ctypes.POINTER(W.DWORD)];K.QueryFullProcessImageNameW.restype=W.BOOL;K.GetSystemTimePreciseAsFileTime.argtypes=[ctypes.POINTER(W.FILETIME)];K.GetSystemTimePreciseAsFileTime.restype=None
for f in [install,install/'.runner']:assert not f.lstat().st_file_attributes&1024
reg=json.loads((install/'.runner').read_text(encoding='utf-8-sig'));registered=reg.get('agentId')==21 and reg.get('agentName')=='PayPlanner-XSENUS';ranges=[tuple(int(datetime.datetime.fromisoformat(v).timestamp()*10000000)+116444736000000000 for v in x) for x in [('2026-10-31T16:58:40+00:00','2026-10-31T16:58:55+00:00'),('2026-10-31T18:26:25+00:00','2026-10-31T18:26:40+00:00')]]
def own_memory():
 # Kernel pseudo-handle -1 always denotes this current process; no real handle is opened.
 h=W.HANDLE(-1);c,x,k,u=(W.FILETIME() for _ in range(4));m=n.ProcessMemory();m.cb=ctypes.sizeof(m)
 if not K.GetProcessTimes(h,ctypes.byref(c),ctypes.byref(x),ctypes.byref(k),ctypes.byref(u)):raise RuntimeError('SELF_IDENTITY_QUERY_FAILED')
 if not n.P.GetProcessMemoryInfo(h,ctypes.byref(m),m.cb):raise RuntimeError('SELF_MEMORY_QUERY_FAILED')
 return {'pid':os.getpid(),'createdTicks':str((c.dwHighDateTime<<32)|c.dwLowDateTime),'workingSetBytes':int(m.WorkingSetSize),'privateBytes':int(m.PrivateUsage),'peakWorkingSetBytes':int(m.PeakWorkingSetSize),'peakPagefileUsageBytes':int(m.PeakPagefileUsage),'pseudoHandleNeedsClose':False}
def sample():
 m=n.memory();ft=W.FILETIME();K.GetSystemTimePreciseAsFileTime(ctypes.byref(ft));cut=(ft.dwHighDateTime<<32)|ft.dwLowDateTime;r={'utc':m['utc'],'availablePhysicalBytes':m['availableBytes'],'availableCommitBytes':m['commitLimitBytes']-m['commitUsedBytes'],'workerCount':0,'identityUnknownCount':0,'oldMonitorCount':0,'verifiedListeners':[],'unknown':[]};h=K.CreateToolhelp32Snapshot(2,0)
 if h==ctypes.c_void_p(-1).value:raise RuntimeError('SNAPSHOT_FAILED')
 try:
  e=n.ProcessEntry();e.dwSize=ctypes.sizeof(e);ctypes.set_last_error(0);ok=K.Process32FirstW(h,ctypes.byref(e))
  while ok:
   name=e.szExeFile.lower();pid=int(e.th32ProcessID);runner=name in ['runner.worker.exe','runner.listener.exe'];py=name in ['python.exe','pythonw.exe','python3.exe','py.exe']
   if name=='runner.worker.exe':r['workerCount']+=1
   if runner or py:
    error=None;ph=K.OpenProcess(0x1000,False,pid)
    if not ph:error={'pid':pid,'operation':'OpenProcess0x1000','win32Error':ctypes.get_last_error()}
    else:
     try:
      c,x,k,u=(W.FILETIME() for _ in range(4))
      if not K.GetProcessTimes(ph,ctypes.byref(c),ctypes.byref(x),ctypes.byref(k),ctypes.byref(u)):error={'pid':pid,'operation':'GetProcessTimes','win32Error':ctypes.get_last_error()}
      else:
       t=(c.dwHighDateTime<<32)|c.dwLowDateTime
       if t>cut:error={'pid':pid,'operation':'CreationAfterPreSnapshotCutoff','win32Error':None}
       elif py:
        if any(a<=t<=b for a,b in ranges):r['oldMonitorCount']+=1
       elif runner:
        buf=ctypes.create_unicode_buffer(32768);length=W.DWORD(len(buf))
        if not K.QueryFullProcessImageNameW(ph,0,buf,ctypes.byref(length)):error={'pid':pid,'operation':'QueryFullProcessImageNameW','win32Error':ctypes.get_last_error()}
        else:
         image=pathlib.Path(buf.value);parent=image.parent;trusted=registered and image.name.lower()==name and parent.parent==install and bool(re.fullmatch(r'bin(?:\.\d+\.\d+\.\d+)?',parent.name,re.I))
         if trusted:
          try:trusted=not any(z.lstat().st_file_attributes&1024 for z in [install,parent,image])
          except OSError:trusted=False
         if not trusted:error={'pid':pid,'operation':'RunnerInstallationBindingUnproven','win32Error':None}
         elif name=='runner.listener.exe':r['verifiedListeners'].append({'pid':pid,'createdTicks':str(t),'bin':parent.name,'installationRootBound':True})
     finally:
      if not K.CloseHandle(ph):raise RuntimeError('PROCESS_HANDLE_CLOSE_FAILED')
    if error:r['unknown'].append(error)
   ctypes.set_last_error(0);ok=K.Process32NextW(h,ctypes.byref(e))
  if ctypes.get_last_error()!=18:raise RuntimeError('ENUMERATION_FAILED')
 finally:
  if not K.CloseHandle(h):raise RuntimeError('SNAPSHOT_HANDLE_CLOSE_FAILED')
 r['identityUnknownCount']=len({x['pid'] for x in r['unknown']});r['collectorOwn']=own_memory();return r
def clean(r):return min(r['availablePhysicalBytes'],r['availableCommitBytes'])>=A and all(r[k]==0 for k in ['workerCount','identityUnknownCount','oldMonitorCount']) and len(r['verifiedListeners'])==1
ready={'scope':S,'headSha':H,'planManifestSha256':MH,'qualified':False,'pinsAllMatch':False,'intervalSeconds':10,'samples':[],'runnerBefore':None,'runnerAfter':None};audit={'collectorSourceSha256':sha(__file__),'nativeHelperSha256':sha(native_path),'nativeHandleCloseResultsChecked':True,'ownMemoryScope':'Current process only, never foreign memory; transitive child peaks UNKNOWN','readiness':ready,'thresholdPhysicalAndCommitBytes':A,'mandatoryCiProfileChanged':False,'executionAuthorized':False,'authorizationCreated':False,'captureInvocations':0,'applicationModuleImports':0,'nodeModulesScans':0,'historicalCiChildAbsenceProven':False,'identityScope':'Current runner and Python candidates against two saved Oct31 monitor creation windows; no broad historical PID audit'}
if datetime.datetime.now(datetime.timezone.utc)<datetime.datetime.fromisoformat('2026-11-03T04:22:08.165057+00:00'):
 print(json.dumps({'status':'NO_INVOKE_TOO_EARLY','osSamples':0,'captureInvocations':0}));raise SystemExit(75)
try:
 initial=sample();audit['initialSample']=initial;ready['runnerBefore']=gh('/actions/runners/21','{id,status,busy}');main=gh('/git/ref/heads/main','{sha:.object.sha}');run=gh('/actions/runs/41509039583','{id,run_attempt,head_sha,status}');jobs=gh('/actions/runs/41509039583/attempts/1/jobs?per_page=100','{total_count,jobs:[.jobs[]|{id,status,head_sha,run_attempt}]}');bad=[];local=cmd(['git','--no-optional-locks','rev-parse','HEAD']);dirty=cmd(['git','--no-optional-locks','status','--porcelain=v1','--untracked-files=no'])
 if sha(P/'manifest.json')!=MH:bad.append('Manifest')
 for x in json.loads((P/'manifest.json').read_text())['files']:
  if sha(P/x['file'])!=x['sha256']:bad.append('Prepared:'+x['file'])
 for name,x in pins['files'].items():
  if sha(pathlib.Path(pins['workspace'])/name)!=x['sha256']:bad.append('Source:'+name)
 if sha(pins['node']['path'])!=pins['node']['sha256']:bad.append('Node')
 ready['pinsAllMatch']=not bad and local==H and not dirty;terminal=main.get('sha')==H and run=={'id':41509039583,'run_attempt':1,'head_sha':H,'status':'completed'} and jobs['total_count']==len(jobs['jobs'])==11 and all(j['head_sha']==H and j['run_attempt']==1 and j['status']=='completed' for j in jobs['jobs']);audit['ci']={'main':main.get('sha'),'runId':41509039583,'attempt':1,'all11JobsTerminal':terminal};audit['pinMismatches']=bad
 if clean(initial) and ready['runnerBefore']==IDLE and terminal and ready['pinsAllMatch']:
  print(json.dumps({'initialGuards':'PASSED','fiveSamplesStartingUtc':utc()}),flush=True);start=time.monotonic()
  for i in range(5):
   if i:time.sleep(max(0,start+i*10-time.monotonic()))
   ready['samples'].append(sample())
  ready['runnerAfter']=gh('/actions/runners/21','{id,status,busy}');ts=[datetime.datetime.fromisoformat(s['utc']) for s in ready['samples']];gaps=[(b-a).total_seconds() for a,b in zip(ts,ts[1:])];audit['gapsSeconds']=gaps;ready['qualified']=all(clean(s) for s in ready['samples']) and all(9.5<=v<=10.5 for v in gaps) and ready['runnerAfter']==IDLE;audit['status']='READINESS_QUALIFIED' if ready['qualified'] else 'NO_INVOKE_FIVE_SAMPLE_GUARD';audit['minPhysicalBytes']=min(s['availablePhysicalBytes'] for s in ready['samples']);audit['minCommitBytes']=min(s['availableCommitBytes'] for s in ready['samples'])
 else:audit['status']='NO_INVOKE_INITIAL_GUARD'
except Exception as e:audit['status']='NO_INVOKE_METADATA_OR_IDENTITY';audit['errorClass']=str(e) if isinstance(e,RuntimeError) else type(e).__name__
audit['finishedUtc']=utc()
if not ready['qualified'] and 'initialSample' in audit:audit['nextOsReadNotBeforeUtc']=(datetime.datetime.fromisoformat(audit['initialSample']['utc'])+datetime.timedelta(minutes=15)).isoformat()
d=B/('dependency-fingerprint-uv12-readiness-checked-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S'));d.mkdir(exist_ok=False);f=d/'proof.json';f.write_text(json.dumps(ready,indent=2)+'\n',encoding='utf8');o=d/'observation.json';o.write_text(json.dumps(audit,indent=2)+'\n',encoding='utf8');m=d/'manifest.json';m.write_text(json.dumps({'files':[{'file':z.name,'sha256':sha(z),'bytes':z.stat().st_size} for z in [f,o]]},indent=2)+'\n',encoding='utf8');print(json.dumps({'readiness':ready,'status':audit['status'],'initialSample':audit.get('initialSample'),'ci':audit.get('ci'),'minPhysicalBytes':audit.get('minPhysicalBytes'),'minCommitBytes':audit.get('minCommitBytes'),'nextOsReadNotBeforeUtc':audit.get('nextOsReadNotBeforeUtc'),'proofPath':str(f),'proofSha256':sha(f),'observationSha256':sha(o),'manifestPath':str(m),'manifestSha256':sha(m),'executionAuthorized':False,'captureInvocations':0}))
