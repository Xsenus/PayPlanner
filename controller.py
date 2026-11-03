"""New scope; never imports/executes the old diagnostic v2. Default is plan-only."""
import datetime as D
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent
SCOPE = 'one-retirement-dependency-fingerprint-5441ed9c'
ADMISSION, FLOOR = 6442450944, 1073741824

class Guard(Exception): pass
def now(): return D.datetime.now(D.timezone.utc)
def load(path): return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))
def date(value):
    result = D.datetime.fromisoformat(value.replace('Z', '+00:00'))
    if result.tzinfo is None: raise Guard('AWARE_UTC_REQUIRED')
    return result.astimezone(D.timezone.utc)
def sha(path, deadline=None):
    digest = hashlib.sha256()
    with pathlib.Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1048576), b''):
            if deadline and time.monotonic() >= deadline: raise Guard('HASH_DEADLINE')
            digest.update(block)
    return digest.hexdigest()
def save(name, data):
    path = ROOT / name
    with path.open('x', encoding='utf8') as stream:
        stream.write(json.dumps(data, ensure_ascii=False, indent=2) + '\n')
def ordinary(path, directory=False):
    path = pathlib.Path(path)
    if path.is_symlink() or path.stat().st_file_attributes & 1024:
        raise Guard('REPARSE_BOUNDARY')
    if (directory and not path.is_dir()) or (not directory and not path.is_file()):
        raise Guard('ORDINARY_PATH_REQUIRED')
    return path
def static_plan():
    for item in load(ROOT / 'manifest.json')['files']:
        name = item['file']
        if pathlib.Path(name).name != name: raise Guard('MANIFEST_PATH')
        if sha(ordinary(ROOT / name)) != item['sha256']: raise Guard('PLAN_INPUT_CHANGED')
    plan, pins = load(ROOT / 'plan.json'), load(ROOT / 'pins.json')
    if plan['scope'] != SCOPE: raise Guard('SCOPE_MISMATCH')
    return plan, pins
def authorize(plan):
    path = ordinary(ROOT / 'authorization.json')
    auth = load(path)
    if not (auth.get('enabled') is True and auth.get('scope') == SCOPE
            and auth.get('rootReviewedCompleteSource') is True
            and auth.get('maximumInvocations') == 1
            and auth.get('headSha') == plan['headSha']
            and auth.get('runId') == plan['runId'] and auth.get('attempt') == 1
            and auth.get('manifestSha256') == plan['ciManifestSha256']
            and auth.get('planManifestSha256') == sha(ROOT / 'manifest.json')
            and re.fullmatch('[a-f0-9]{32}', auth.get('nonce', ''))):
        raise Guard('SEPARATE_ROOT_SCOPE_REQUIRED')
    issued, expires = date(auth['issuedUtc']), date(auth['expiresUtc'])
    if not (now() - D.timedelta(seconds=120) <= issued <= now() < expires
            <= issued + D.timedelta(minutes=15)):
        raise Guard('ROOT_GRANT_NOT_FRESH_OR_EXPIRED')
    ready_path = pathlib.Path(auth['readinessPath']).resolve()
    if not ready_path.is_relative_to(ROOT.parent) or ready_path.stat().st_size > 1048576:
        raise Guard('READINESS_PATH_OR_SIZE')
    if sha(ordinary(ready_path)) != auth['readinessSha256']: raise Guard('READINESS_HASH')
    ready = load(ready_path); samples = ready.get('samples', [])
    if not (ready.get('scope') == SCOPE and ready.get('headSha') == plan['headSha']
            and ready.get('planManifestSha256') == sha(ROOT / 'manifest.json')
            and ready.get('qualified') is True and ready.get('pinsAllMatch') is True
            and ready.get('intervalSeconds') == 10 and len(samples) == 5):
        raise Guard('FRESH_SCOPED_READINESS_REQUIRED')
    ts = [date(s['utc']) for s in samples]
    if not (now() - D.timedelta(seconds=120) <= ts[0] <= ts[-1] <= now()):
        raise Guard('READINESS_FRESHNESS')
    if any(not 9.5 <= (b - a).total_seconds() <= 10.5 for a, b in zip(ts, ts[1:])):
        raise Guard('READINESS_CHRONOLOGY')
    zero = ['workerCount', 'identityUnknownCount', 'oldMonitorCount']
    for sample in samples:
        if any(sample.get(k) != 0 for k in zero) or any(sample.get(k, -1) < ADMISSION
                for k in ['availablePhysicalBytes', 'availableCommitBytes']):
            raise Guard('READINESS_MEMORY_OR_IDENTITY')
    if any(ready.get(k) != {'id': 21, 'status': 'online', 'busy': False}
            for k in ['runnerBefore', 'runnerAfter']): raise Guard('READINESS_RUNNER')
    return auth
def metadata(args, deadline, cwd):
    remaining = deadline - time.monotonic()
    if remaining <= 2: raise Guard('METADATA_DEADLINE')
    result = subprocess.run(args, cwd=cwd, capture_output=True, text=True,
        timeout=min(15, remaining - 1), creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode: raise Guard('STANDARD_METADATA_FAILED')
    return result.stdout.strip()
def pins_match(pins, deadline):
    work = ordinary(pins['workspace'], True)
    if metadata(['git', '--no-optional-locks', 'rev-parse', 'HEAD'], deadline, work) != pins['headSha']:
        raise Guard('WORKSPACE_HEAD_CHANGED')
    if metadata(['git', '--no-optional-locks', 'status', '--porcelain=v1', '--untracked-files=no'], deadline, work):
        raise Guard('TRACKED_CHECKOUT_CHANGED')
    ordinary(work / 'node_modules', True)  # Metadata only, no pre-capture dependency scan.
    for name, pin in pins['files'].items():
        if sha(ordinary(work / name), deadline) != pin['sha256']: raise Guard('SOURCE_PIN_CHANGED')
    if sha(ordinary(pins['node']['path']), deadline) != pins['node']['sha256']:
        raise Guard('NODE_BINARY_CHANGED')
def idle_ci(plan, pins, deadline):
    work = pins['workspace']; base = 'repos/Xsenus/PayPlanner'
    def gh(endpoint, query):
        return json.loads(metadata(['gh', 'api', base + endpoint, '--jq', query], deadline, work))
    current = gh('/git/ref/heads/main', '{sha:.object.sha}')
    if current['sha'] != plan['headSha']: raise Guard('REMOTE_MAIN_CHANGED')
    run = gh('/actions/runs/' + str(plan['runId']), '{id,run_attempt,head_sha,status,conclusion}')
    if not (run['id'] == plan['runId'] and run['run_attempt'] == 1
            and run['head_sha'] == plan['headSha'] and run['status'] == 'completed'):
        raise Guard('CURRENT_CI_NOT_EXACT_TERMINAL_ATTEMPT')
    jobs = gh('/actions/runs/' + str(plan['runId']) + '/attempts/1/jobs?per_page=100',
        '{total_count,jobs:[.jobs[]|{id,status,head_sha,run_attempt}]}')
    if not (0 < jobs['total_count'] == len(jobs['jobs']) <= 100
            and any(j['id'] == plan['failedJobId'] for j in jobs['jobs'])
            and all(j['status'] == 'completed' and j['head_sha'] == plan['headSha']
                and j['run_attempt'] == 1 for j in jobs['jobs'])):
        raise Guard('ALL_CI_JOBS_MUST_BE_TERMINAL')
    runner = gh('/actions/runners/21', '{id,status,busy}')
    if runner != {'id': 21, 'status': 'online', 'busy': False}: raise Guard('RUNNER_NOT_IDLE')
    return {'run': run, 'jobs': jobs, 'runner': runner,
        'ciManifestBinding': 'ROOT_ATTESTED_EXPECTED_HASH_NO_PROTECTED_STORE_READ'}
def execute(plan, pins):
    auth = authorize(plan)  # No OS provider import or workload before scoped grant/readiness.
    begin = time.monotonic(); deadline = min(begin + 885,
        begin + (date(auth['expiresUtc']) - now()).total_seconds() - 5)
    if deadline - begin < 30: raise Guard('INSUFFICIENT_BOUNDED_GRANT')
    if (ROOT / 'execution-attempt.json').exists(): raise Guard('ATTEMPT_ALREADY_CONSUMED')
    save('execution-attempt.json', {'utc': now().isoformat(), 'maximumInvocations': 1})
    pins_match(pins, deadline); ci = idle_ci(plan, pins, deadline)
    sys.dont_write_bytecode = True
    import win_owned as native
    own_check = native.Held(os.getpid())
    try:
        if not own_check.state()['alive']: raise Guard('OWN_API_SELF_CHECK')
        own_check.memory()
    finally:
        if not own_check.close(): raise Guard('SELF_CHECK_HANDLE_CLOSE')
    initial = native.system_memory(); worker_pids = native.workers()
    if worker_pids or any(initial[k] < ADMISSION for k in initial):
        raise Guard('IMMEDIATE_ADMISSION_OR_WORKER_GUARD')
    # Revalidate freshness/expiry immediately before the one controlled launch.
    auth = authorize(plan)
    if deadline - time.monotonic() < 20: raise Guard('LAUNCH_BUDGET_EXHAUSTED')
    save('launch-ticket.json', {'scope': SCOPE, 'nonce': auth['nonce'],
        'authorizationSha256': sha(ROOT / 'authorization.json'),
        'manifestSha256': sha(ROOT / 'manifest.json'), 'expiresUtc': auth['expiresUtc'],
        'workerBudgetMs': min(870000, int((deadline - time.monotonic() - 6) * 1000))})
    job = native.Job(); samples = []; abort = None; cleanup = {}; actual_exit = None
    def record_abort(reason):
        nonlocal abort
        if abort is None: abort = {'reason': reason, 'utc': now().isoformat(), 'deliberateOwnedStop': False}
    try:
        save('launch-committed.json', {'maximumInvocations': 1, 'processCreated': False})
        job.launch(pins['node']['path'], ['--max-old-space-size=512', str(ROOT / 'worker.mjs')],
            ROOT, ROOT / 'worker.stdout.jsonl', ROOT / 'worker.stderr.log')
        while True:
            resource = native.system_memory(); owned = job.sample()
            runner_worker_count = len(native.workers())
            sample = {'utc': now().isoformat(), 'elapsedSeconds': time.monotonic() - begin,
                'runnerWorkerCandidateCount': runner_worker_count, **resource, **owned}; samples.append(sample)
            with (ROOT / 'resource-samples.jsonl').open('a', encoding='utf8') as stream:
                stream.write(json.dumps(sample) + '\n')
            state = job.root.state()
            if owned['unknown']: record_abort('OWNED_IDENTITY_UNKNOWN')
            if runner_worker_count: record_abort('NEW_CI_WORKER_OBSERVED')
            if any(value < FLOOR for value in resource.values()): record_abort('PHYSICAL_OR_COMMIT_FLOOR')
            if time.monotonic() >= deadline - 6 or len(samples) >= 880: record_abort('OWN_DEADLINE')
            if any((ROOT / name).stat().st_size > 2097152 for name in ['worker.stdout.jsonl', 'worker.stderr.log']):
                record_abort('OWN_PROGRESS_LOG_CAP')
            if not state['alive']: actual_exit = state['exitCode']; break
            if abort:
                job.stop(); abort['deliberateOwnedStop'] = True; break
            time.sleep(min(1, max(0, deadline - 6 - time.monotonic())))
    except BaseException as error:
        record_abort(error.safe if isinstance(error, native.Failure) else
            str(error) if isinstance(error, Guard) else type(error).__name__)
    finally:
        final_membership = {'unknown': []}
        try: final_membership = job.sample()
        except BaseException as error:
            final_membership['unknown'].append(error.safe if isinstance(error, native.Failure)
                else type(error).__name__)
            record_abort('FINAL_MEMBERSHIP_UNKNOWN')
        try:
            before = [{**h.identity(), **h.state()} for h in job.held.values()]
            if any(x['alive'] for x in before) or job.raw_created:
                job.stop(); cleanup['ownedJobStopPerformed'] = True
                if abort is not None: abort['deliberateOwnedStop'] = True
            until = min(time.monotonic() + 5, deadline)
            while time.monotonic() < until and (any(h.state()['alive'] for h in job.held.values())
                    or job.raw_created and native.wait(job.raw_created, 0) == 258):
                time.sleep(0.1)
            final = [{**h.identity(), **h.state()} for h in job.held.values()]
            if job.root and not job.root.state()['alive']: actual_exit = job.root.state()['exitCode']
            cleanup.update(before=before, final=final,
                knownResidualCount=sum(x['alive'] for x in final), unknown=final_membership['unknown'])
            if job.raw_created:
                cleanup['unknown'].append({'pid': job.created_pid,
                    'operation': 'CreatedRootCreationIdentityUnavailable',
                    'terminalHandleObserved': native.wait(job.raw_created, 0) == 0})
        except BaseException as error:
            cleanup['unknown'] = final_membership['unknown'] + [
                error.safe if isinstance(error, native.Failure) else type(error).__name__]
            record_abort('FINAL_CLEANUP_UNKNOWN')
        finally:
            if cleanup.get('unknown'):
                try:
                    job.stop(); cleanup['independentFinalOwnedStopAttempted'] = True
                    if abort is not None: abort['deliberateOwnedStop'] = True
                except BaseException as error:
                    cleanup.setdefault('unknown', []).append(error.safe if isinstance(error, native.Failure)
                        else type(error).__name__)
            process_created, resumed = job.process_created, job.resumed
            handle_count = len(job.held) + bool(job.h) + bool(job.raw_created)
            close_failures = job.close()
            cleanup['retainedProcessAndJobHandleCloseAttempts'] = handle_count
            cleanup['handleCloseFailures'] = close_failures
            cleanup['allRetainedHandlesClosed'] = not close_failures
            cleanup['kernelKillOnClosePolicy'] = True
            cleanup['shortLivedDescendantsMayBeMissedBySampling'] = True
        save('cleanup-receipt.json', cleanup)
    if actual_exit == 125 and (ROOT / 'own-deadline.json').is_file():
        record_abort('WORKER_OWN_DEADLINE'); abort['deliberateOwnedStop'] = True
    result = load(ROOT / 'capture-result.json') if (ROOT / 'capture-result.json').is_file() else None
    post_pins_match = False
    if abort is None:
        try: pins_match(pins, deadline); post_pins_match = True
        except BaseException as error: record_abort(str(error) if isinstance(error, Guard) else type(error).__name__)
    valid_result = False
    try:
        valid_result = bool(result and result.get('completed') is True and
            result.get('nodeSha256') == pins['node']['sha256'] and result.get('nodeVersion') == pins['node']['expectedVersion']
            and sha(ROOT / 'dependency-fingerprint.json', deadline) == result.get('manifestFileSha256'))
    except BaseException as error: record_abort(str(error) if isinstance(error, Guard) else type(error).__name__)
    summary = {'scope': SCOPE, 'headSha': pins['headSha'], 'ci': ci, 'initial': initial,
        'processCreated': process_created, 'resumed': resumed, 'maximumCaptureInvocations': 1,
        'actualNativeExitCode': actual_exit, 'diagnosticAbort': abort,
        'ownedStop': abort if abort and abort['deliberateOwnedStop'] else None,
        'deliberateOwnedStop': bool(abort and abort['deliberateOwnedStop']),
        'captureResult': result, 'postPinsMatch': post_pins_match, 'cleanup': cleanup,
        'measurementCompleted': valid_result and actual_exit == 0 and abort is None and post_pins_match
            and cleanup.get('knownResidualCount') == 0 and not cleanup.get('unknown')
            and cleanup.get('allRetainedHandlesClosed') is True,
        'elapsedSeconds': time.monotonic() - begin, 'samples': len(samples),
        'fullCiOrTestRequalification': False}
    save('outcome.json', summary)
    try:
        save('execution-manifest.json', {'files': [{'file': p.name, 'sha256': sha(p, begin + 895)} for p in ROOT.iterdir()
            if p.is_file() and p.name in ['outcome.json', 'cleanup-receipt.json', 'capture-result.json',
            'capture-error.json', 'capture-attempt.json', 'dependency-fingerprint.json', 'resource-samples.jsonl',
            'worker.stdout.jsonl', 'worker.stderr.log', 'execution-attempt.json', 'launch-committed.json',
            'own-deadline.json']]})
    except Guard:
        save('execution-manifest-incomplete.json', {'reason': 'SEALING_DEADLINE', 'measurementAccepted': False})
        return 2
    print(json.dumps({'measurementCompleted': summary['measurementCompleted'], 'outcomeSha256': sha(ROOT / 'outcome.json')}))
    return 0 if summary['measurementCompleted'] else 2

if __name__ == '__main__':
    try:
        plan, pins = static_plan()
        if sys.argv[1:] == ['--execute']: raise SystemExit(execute(plan, pins))
        if sys.argv[1:] not in [[], ['--validate-plan']]: raise Guard('UNKNOWN_MODE')
        print(json.dumps({'scope': SCOPE, 'planOnly': True, 'captureInvocations': 0}))
    except Guard as error:
        print(json.dumps({'noInvokeOrGuard': str(error)})); raise SystemExit(2)
    except Exception as error:
        print(json.dumps({'noInvokeOrDiagnosticFailure': type(error).__name__})); raise SystemExit(2)
