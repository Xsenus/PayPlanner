import ctypes,ctypes.wintypes as W,datetime,pathlib
K=ctypes.WinDLL('kernel32',use_last_error=True)
P=ctypes.WinDLL('psapi',use_last_error=True)
N=ctypes.WinDLL('ntdll',use_last_error=True)
K.OpenProcess.argtypes=[W.DWORD,W.BOOL,W.DWORD];K.OpenProcess.restype=W.HANDLE
K.CloseHandle.argtypes=[W.HANDLE];K.CloseHandle.restype=W.BOOL
K.ReadProcessMemory.argtypes=[W.HANDLE,W.LPCVOID,W.LPVOID,ctypes.c_size_t,ctypes.POINTER(ctypes.c_size_t)];K.ReadProcessMemory.restype=W.BOOL
K.GetProcessTimes.argtypes=[W.HANDLE,ctypes.POINTER(W.FILETIME),ctypes.POINTER(W.FILETIME),ctypes.POINTER(W.FILETIME),ctypes.POINTER(W.FILETIME)];K.GetProcessTimes.restype=W.BOOL
class MemoryInfo(ctypes.Structure):
 _fields_=[('cb',W.DWORD)]+[(k,ctypes.c_size_t) for k in ['CommitTotal','CommitLimit','CommitPeak','PhysicalTotal','PhysicalAvailable','SystemCache','KernelTotal','KernelPaged','KernelNonpaged','PageSize']]+[(k,W.DWORD) for k in ['HandleCount','ProcessCount','ThreadCount']]
class ProcessMemory(ctypes.Structure):
 _fields_=[('cb',W.DWORD),('PageFaultCount',W.DWORD)]+[(k,ctypes.c_size_t) for k in ['PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage','QuotaPagedPoolUsage','QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage','PagefileUsage','PeakPagefileUsage','PrivateUsage']]
class ProcessEntry(ctypes.Structure):
 _fields_=[('dwSize',W.DWORD),('cntUsage',W.DWORD),('th32ProcessID',W.DWORD),('th32DefaultHeapID',ctypes.c_size_t),('th32ModuleID',W.DWORD),('cntThreads',W.DWORD),('th32ParentProcessID',W.DWORD),('pcPriClassBase',W.LONG),('dwFlags',W.DWORD),('szExeFile',W.WCHAR*260)]
class BasicInfo(ctypes.Structure):
 _fields_=[('Reserved1',ctypes.c_void_p),('PebBaseAddress',ctypes.c_void_p),('Reserved2',ctypes.c_void_p*2),('UniqueProcessId',ctypes.c_void_p),('Reserved3',ctypes.c_void_p)]
K.CreateToolhelp32Snapshot.argtypes=[W.DWORD,W.DWORD];K.CreateToolhelp32Snapshot.restype=W.HANDLE
K.Process32FirstW.argtypes=[W.HANDLE,ctypes.POINTER(ProcessEntry)];K.Process32FirstW.restype=W.BOOL
K.Process32NextW.argtypes=[W.HANDLE,ctypes.POINTER(ProcessEntry)];K.Process32NextW.restype=W.BOOL
P.GetPerformanceInfo.argtypes=[ctypes.POINTER(MemoryInfo),W.DWORD];P.GetPerformanceInfo.restype=W.BOOL
P.GetProcessMemoryInfo.argtypes=[W.HANDLE,ctypes.POINTER(ProcessMemory),W.DWORD];P.GetProcessMemoryInfo.restype=W.BOOL
N.NtQueryInformationProcess.argtypes=[W.HANDLE,W.ULONG,W.LPVOID,W.ULONG,ctypes.POINTER(W.ULONG)];N.NtQueryInformationProcess.restype=W.LONG
def utc():return datetime.datetime.now(datetime.timezone.utc).isoformat()
def memory():
 d=MemoryInfo();d.cb=ctypes.sizeof(d)
 if not P.GetPerformanceInfo(ctypes.byref(d),d.cb):raise RuntimeError('MEMORY_QUERY_FAILED')
 return dict(utc=utc(),availableBytes=d.PhysicalAvailable*d.PageSize,totalBytes=d.PhysicalTotal*d.PageSize,commitUsedBytes=d.CommitTotal*d.PageSize,commitLimitBytes=d.CommitLimit*d.PageSize)
def processes():
 h=K.CreateToolhelp32Snapshot(2,0)
 if h==ctypes.c_void_p(-1).value:raise RuntimeError('PROCESS_SNAPSHOT_FAILED')
 try:
  e=ProcessEntry();e.dwSize=ctypes.sizeof(e);ok=K.Process32FirstW(h,ctypes.byref(e));rows={}
  while ok:
   rows[int(e.th32ProcessID)]=dict(pid=int(e.th32ProcessID),parentPid=int(e.th32ParentProcessID),name=e.szExeFile)
   ok=K.Process32NextW(h,ctypes.byref(e))
  return rows
 finally:K.CloseHandle(h)
def _read(h,address,count):
 b=ctypes.create_string_buffer(count);done=ctypes.c_size_t()
 if not K.ReadProcessMemory(h,address,b,count,ctypes.byref(done)) or done.value!=count:raise RuntimeError('OWNED_PROCESS_READ_UNAVAILABLE')
 return b.raw
def info(pid,with_cwd=False):
 h=K.OpenProcess(0x0400|0x0010,False,pid)
 if not h:return None
 try:
  c,x,k,u=(W.FILETIME() for _ in range(4))
  if not K.GetProcessTimes(h,ctypes.byref(c),ctypes.byref(x),ctypes.byref(k),ctypes.byref(u)):return None
  ticks=(c.dwHighDateTime<<32)|c.dwLowDateTime
  m=ProcessMemory();m.cb=ctypes.sizeof(m)
  ok=P.GetProcessMemoryInfo(h,ctypes.byref(m),m.cb)
  d=dict(createdTicks=ticks,startUtc=datetime.datetime.fromtimestamp((ticks-116444736000000000)/10000000,datetime.timezone.utc).isoformat(),privateBytes=int(m.PrivateUsage) if ok else None,workingSetBytes=int(m.WorkingSetSize) if ok else None)
  if with_cwd:
   try:
    b=BasicInfo();ret=W.ULONG()
    if N.NtQueryInformationProcess(h,0,ctypes.byref(b),ctypes.sizeof(b),ctypes.byref(ret))!=0:raise RuntimeError('PEB_QUERY_FAILED')
    params=int.from_bytes(_read(h,b.PebBaseAddress+0x20,8),'little');s=_read(h,params+0x38,16);size=int.from_bytes(s[:2],'little');address=int.from_bytes(s[8:16],'little')
    if size>8192:raise RuntimeError('CWD_LENGTH_UNSAFE')
    d['cwd']=_read(h,address,size).decode('utf-16-le') if size else ''
   except Exception:d['cwd']=None
  return d
 finally:K.CloseHandle(h)
def descendants(root,root_ticks):
 rows=processes();owned={};root_info=info(root)
 if root_info and root_info['createdTicks']==root_ticks and root in rows:owned[root]=dict(rows[root],**root_info)
 changed=True
 while changed:
  changed=False
  for pid,row in rows.items():
   if pid in owned or row['parentPid'] not in owned:continue
   detail=info(pid)
   if detail and detail['createdTicks']>=owned[row['parentPid']]['createdTicks']:
    owned[pid]=dict(row,**detail);changed=True
 return owned,rows
