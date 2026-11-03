"""Deferred Windows API provider. No process is created by import."""
import ctypes as C
import ctypes.wintypes as W
import subprocess

K = C.WinDLL('kernel32', use_last_error=True)
P = C.WinDLL('psapi', use_last_error=True)
SIZE = C.c_size_t

class Failure(Exception):
    def __init__(self, operation):
        self.safe = {'operation': operation, 'win32Error': C.get_last_error()}
        super().__init__(operation)

def api(name, args, result=W.BOOL, library=K):
    fn = getattr(library, name); fn.argtypes = args; fn.restype = result
    return fn

close = api('CloseHandle', [W.HANDLE])
open_process = api('OpenProcess', [W.DWORD, W.BOOL, W.DWORD], W.HANDLE)
times = api('GetProcessTimes', [W.HANDLE] + [C.POINTER(W.FILETIME)] * 4)
wait = api('WaitForSingleObject', [W.HANDLE, W.DWORD], W.DWORD)
exit_code = api('GetExitCodeProcess', [W.HANDLE, C.POINTER(W.DWORD)])
precise = api('GetSystemTimePreciseAsFileTime', [C.POINTER(W.FILETIME)], None)
job_query = api('QueryInformationJobObject', [W.HANDLE, C.c_int, W.LPVOID, W.DWORD, C.POINTER(W.DWORD)])
in_job = api('IsProcessInJob', [W.HANDLE, W.HANDLE, C.POINTER(W.BOOL)])
terminate_job = api('TerminateJobObject', [W.HANDLE, W.UINT])

class Performance(C.Structure):
    _fields_ = [('cb', W.DWORD)] + [(n, SIZE) for n in ['CommitTotal', 'CommitLimit',
        'CommitPeak', 'PhysicalTotal', 'PhysicalAvailable', 'SystemCache',
        'KernelTotal', 'KernelPaged', 'KernelNonpaged', 'PageSize']] + [
        (n, W.DWORD) for n in ['HandleCount', 'ProcessCount', 'ThreadCount']]

class Memory(C.Structure):
    _fields_ = [('cb', W.DWORD), ('faults', W.DWORD)] + [(n, SIZE) for n in [
        'peakWS', 'workingSet', 'peakPaged', 'paged', 'peakNonpaged', 'nonpaged',
        'pagefile', 'peakPagefile', 'private']]

performance = api('GetPerformanceInfo', [C.POINTER(Performance), W.DWORD], library=P)
process_memory = api('GetProcessMemoryInfo', [W.HANDLE, C.POINTER(Memory), W.DWORD], library=P)

class Entry(C.Structure):
    _fields_ = [('size', W.DWORD), ('usage', W.DWORD), ('pid', W.DWORD),
        ('heap', SIZE), ('module', W.DWORD), ('threads', W.DWORD), ('parent', W.DWORD),
        ('priority', W.LONG), ('flags', W.DWORD), ('name', W.WCHAR * 260)]

snapshot = api('CreateToolhelp32Snapshot', [W.DWORD, W.DWORD], W.HANDLE)
first = api('Process32FirstW', [W.HANDLE, C.POINTER(Entry)])
next_entry = api('Process32NextW', [W.HANDLE, C.POINTER(Entry)])

def workers():
    h = snapshot(2, 0)
    if h == C.c_void_p(-1).value: raise Failure('CreateToolhelp32Snapshot')
    try:
        e = Entry(); e.size = C.sizeof(e); found = []
        C.set_last_error(0); ok = first(h, C.byref(e))
        while ok:
            if e.name.lower() == 'runner.worker.exe': found.append(int(e.pid))
            C.set_last_error(0); ok = next_entry(h, C.byref(e))
        if C.get_last_error() != 18: raise Failure('Process32Enumeration')
        return found
    finally: close(h)

def system_memory():
    p = Performance(); p.cb = C.sizeof(p)
    if not performance(C.byref(p), p.cb): raise Failure('GetPerformanceInfo')
    return {'availablePhysicalBytes': int(p.PhysicalAvailable * p.PageSize),
        'availableCommitBytes': int((p.CommitLimit - p.CommitTotal) * p.PageSize)}

def ticks():
    f = W.FILETIME(); precise(C.byref(f)); return (f.dwHighDateTime << 32) | f.dwLowDateTime

class Held:
    def __init__(self, pid, handle=None):
        self.pid = pid; self.h = handle or open_process(0x101000, False, pid)
        if not self.h: raise Failure('OpenProcessOwnedCandidate')
        try:
            c, x, k, u = (W.FILETIME() for _ in range(4))
            if not times(self.h, C.byref(c), C.byref(x), C.byref(k), C.byref(u)):
                raise Failure('GetProcessTimes')
            self.created = (c.dwHighDateTime << 32) | c.dwLowDateTime
        except BaseException:
            if handle is None: close(self.h)
            self.h = None; raise
    def identity(self): return {'pid': self.pid, 'createdTicks': str(self.created)}
    def state(self):
        result = wait(self.h, 0)
        if result not in (0, 258): raise Failure('WaitForSingleObject')
        code = W.DWORD()
        if not exit_code(self.h, C.byref(code)): raise Failure('GetExitCodeProcess')
        return {'alive': result == 258, 'exitCode': None if result == 258 else int(code.value)}
    def memory(self):
        p = Memory(); p.cb = C.sizeof(p)
        if not process_memory(self.h, C.byref(p), p.cb): raise Failure('GetProcessMemoryInfo')
        return {'privateBytes': int(p.private), 'workingSetBytes': int(p.workingSet)}
    def close(self):
        if self.h:
            ok = bool(close(self.h)); self.h = None; return ok
        return True

class BasicLimit(C.Structure):
    _fields_ = [('processTime', C.c_longlong), ('jobTime', C.c_longlong),
        ('flags', W.DWORD), ('minWS', SIZE), ('maxWS', SIZE),
        ('active', W.DWORD), ('affinity', SIZE), ('priority', W.DWORD), ('schedule', W.DWORD)]

class Io(C.Structure): _fields_ = [(n, C.c_ulonglong) for n in ['ro', 'wo', 'oo', 'rb', 'wb', 'ob']]
class Limits(C.Structure):
    _fields_ = [('basic', BasicLimit), ('io', Io), ('processMemory', SIZE),
        ('jobMemory', SIZE), ('peakProcessMemory', SIZE), ('peakJobMemory', SIZE)]
class Startup(C.Structure):
    _fields_ = [('cb', W.DWORD), ('reserved', W.LPWSTR), ('desktop', W.LPWSTR),
        ('title', W.LPWSTR)] + [(n, W.DWORD) for n in ['x', 'y', 'xs', 'ys',
        'xc', 'yc', 'fill', 'flags']] + [('show', W.WORD), ('reservedSize', W.WORD),
        ('reservedBytes', W.LPVOID), ('stdin', W.HANDLE), ('stdout', W.HANDLE), ('stderr', W.HANDLE)]
class StartupEx(C.Structure): _fields_ = [('startup', Startup), ('attributes', W.LPVOID)]
class ProcessInfo(C.Structure):
    _fields_ = [('process', W.HANDLE), ('thread', W.HANDLE), ('pid', W.DWORD), ('tid', W.DWORD)]
class Security(C.Structure): _fields_ = [('size', W.DWORD), ('descriptor', W.LPVOID), ('inherit', W.BOOL)]
class Members(C.Structure):
    _fields_ = [('assigned', W.DWORD), ('count', W.DWORD), ('pids', SIZE * 64)]

create_job = api('CreateJobObjectW', [W.LPVOID, W.LPCWSTR], W.HANDLE)
set_job = api('SetInformationJobObject', [W.HANDLE, C.c_int, W.LPVOID, W.DWORD])
assign = api('AssignProcessToJobObject', [W.HANDLE, W.HANDLE])
terminate_process = api('TerminateProcess', [W.HANDLE, W.UINT])
resume_thread = api('ResumeThread', [W.HANDLE], W.DWORD)
create_file = api('CreateFileW', [W.LPCWSTR, W.DWORD, W.DWORD, C.POINTER(Security), W.DWORD, W.DWORD, W.HANDLE], W.HANDLE)
init_attrs = api('InitializeProcThreadAttributeList', [W.LPVOID, W.DWORD, W.DWORD, C.POINTER(SIZE)])
update_attrs = api('UpdateProcThreadAttribute', [W.LPVOID, W.DWORD, SIZE, W.LPVOID, SIZE, W.LPVOID, W.LPVOID])
delete_attrs = api('DeleteProcThreadAttributeList', [W.LPVOID], None)
create_process = api('CreateProcessW', [W.LPCWSTR, W.LPWSTR, W.LPVOID, W.LPVOID,
    W.BOOL, W.DWORD, W.LPVOID, W.LPCWSTR, C.POINTER(StartupEx), C.POINTER(ProcessInfo)])

class Job:
    def __init__(self):
        self.h = create_job(None, None); self.held = {}; self.resumed = False
        self.process_created = False; self.root = None; self.assigned = False
        self.raw_created = None; self.created_pid = None
        if not self.h: raise Failure('CreateJobObject')
        limit = Limits(); limit.basic.flags = 0x2000 | 0x200
        limit.jobMemory = 536870912  # 512 MiB; admission preserves the separate 4 GiB reserve.
        if not set_job(self.h, 9, C.byref(limit), C.sizeof(limit)):
            err = Failure('SetInformationJobObject'); close(self.h); self.h = None; raise err
    def launch(self, executable, arguments, cwd, stdout, stderr):
        streams = []; attrs = None; thread = None; process = None
        try:
            security = Security(C.sizeof(Security), None, True)
            for name, access, mode in [('NUL', 0x80000000, 3), (str(stdout), 0x40000000, 1), (str(stderr), 0x40000000, 1)]:
                h = create_file(name, access, 0, C.byref(security), mode, 128, None)
                if h == C.c_void_p(-1).value: raise Failure('CreateOwnStream')
                streams.append(h)
            size = SIZE(); init_attrs(None, 1, 0, C.byref(size))
            if not size.value: raise Failure('InitializeAttributeSize')
            buf = C.create_string_buffer(size.value); attrs = C.cast(buf, W.LPVOID)
            if not init_attrs(attrs, 1, 0, C.byref(size)): attrs = None; raise Failure('InitializeAttributes')
            handles = (W.HANDLE * 3)(*streams)
            if not update_attrs(attrs, 0, 0x20002, C.cast(handles, W.LPVOID), C.sizeof(handles), None, None):
                raise Failure('UpdateHandleList')
            start = StartupEx(); start.startup.cb = C.sizeof(start); start.startup.flags = 0x100
            start.startup.stdin, start.startup.stdout, start.startup.stderr = streams
            start.attributes = attrs; pi = ProcessInfo()
            env = C.create_unicode_buffer('BackgroundServices__Enabled=false\0Email__Enabled=false\0NODE_OPTIONS=\0'
                'SystemRoot=C:\\Windows\0UV_THREADPOOL_SIZE=12\0WINDIR=C:\\Windows\0\0')
            cmd = C.create_unicode_buffer(subprocess.list2cmdline([executable] + arguments))
            if not create_process(executable, cmd, None, None, True, 0x80000 | 0x400 | 0x4 | 0x8000000,
                    C.cast(env, W.LPVOID), str(cwd), C.byref(start), C.byref(pi)):
                raise Failure('CreateSuspendedOwnedNode')
            self.raw_created, thread = pi.process, pi.thread; self.process_created = True
            self.created_pid = int(pi.pid)
            self.root = Held(int(pi.pid), self.raw_created); self.raw_created = None
            self.held[self.root.pid] = self.root
            if not assign(self.h, self.root.h): raise Failure('AssignOwnedNodeToJob')
            self.assigned = True
            if resume_thread(thread) == 0xFFFFFFFF: raise Failure('ResumeOwnedNode')
            self.resumed = True
        except BaseException:
            handle = self.root.h if self.root else self.raw_created
            if handle: terminate_process(handle, 126)  # Only the actual retained CreateProcess handle.
            raise
        finally:
            if thread: close(thread)
            if process: close(process)
            if attrs: delete_attrs(attrs)
            for handle in streams: close(handle)
    def sample(self):
        cutoff = ticks(); listed = Members(); length = W.DWORD()
        if not job_query(self.h, 3, C.byref(listed), C.sizeof(listed), C.byref(length)):
            raise Failure('QueryOwnedJobMembers')
        if listed.assigned > 64 or listed.count > 64: raise Failure('OwnedJobMemberCap')
        unknown = []
        for pid in listed.pids[:listed.count]:
            if pid in self.held:
                if self.held[pid].state()['alive']: continue
                # A reused PID must not inherit the previous identity's ownership.
                unknown.append({'pid': int(pid), 'operation': 'ExitedHeldPidReappeared'}); continue
            candidate = None
            try:
                candidate = Held(int(pid)); belongs = W.BOOL()
                if not in_job(candidate.h, self.h, C.byref(belongs)): raise Failure('IsProcessInOwnedJob')
                if not belongs.value or candidate.created > cutoff: raise Failure('OwnedJobCreationOrMembership')
                self.held[int(pid)] = candidate; candidate = None
            except Failure as error: unknown.append({'pid': int(pid), **error.safe})
            finally:
                if candidate: candidate.close()
        rows = []
        for process in self.held.values():
            state = process.state(); item = {**process.identity(), **state}
            if state['alive']: item.update(process.memory())
            rows.append(item)
        return {'processes': rows, 'unknown': unknown}
    def stop(self):
        if self.h and self.assigned and not terminate_job(self.h, 125): raise Failure('TerminateOnlyOwnedJob')
        if self.root and not self.assigned and self.root.state()['alive']:
            if not terminate_process(self.root.h, 125): raise Failure('TerminateOnlyCreatedRoot')
        elif self.raw_created:
            if not terminate_process(self.raw_created, 125): raise Failure('TerminateOnlyCreateProcessHandle')
    def close(self):
        failures = []
        for process in self.held.values():
            if not process.close(): failures.append({'pid': process.pid, 'operation': 'CloseHeldProcessHandle'})
        if self.raw_created:
            if not close(self.raw_created): failures.append({'pid': self.created_pid, 'operation': 'CloseCreatedProcessHandle'})
            self.raw_created = None
        if self.h:
            if not close(self.h): failures.append({'operation': 'CloseOwnedJobHandle'})
            self.h = None
        return failures
