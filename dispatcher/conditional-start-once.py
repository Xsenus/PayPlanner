"""One new finite root grant, one fresh readiness, at most one pure capture.

No retry loop; never rewrites an old grant, readiness, or execution marker.
"""
import contextlib
import datetime
import hashlib
import io
import json
import pathlib
import re
import runpy
import sys

sys.dont_write_bytecode = True
D = datetime.timezone.utc
ROOT = pathlib.Path('D:/PayPlannerDiagnostics/dependency-fingerprint-uv12-plan-20261103')
COLLECTOR = pathlib.Path('D:/PayPlannerDiagnostics/uv12-readiness-collector-20261103/collector.py')
SCOPE = 'one-retirement-dependency-fingerprint-uv12-5441ed9c'
HEAD = '5441ed9c68d6d9de13bf80e42eb106b2c0be4136'
MANIFEST = 'd4bf1cb6294f14b1afd6d7b1d4f240a454fe327ed7aee5c510b592b4920a2f79'
ADMISSION = 4831838208

def now():
    return datetime.datetime.now(D)

def date(value):
    result = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
    assert result.tzinfo is not None
    return result.astimezone(D)

def ordinary(path):
    path = pathlib.Path(path)
    assert path.is_file() and not path.is_symlink() and not path.stat().st_file_attributes & 1024
    return path

def sha(path):
    return hashlib.sha256(ordinary(path).read_bytes()).hexdigest()

def load(path):
    return json.loads(ordinary(path).read_bytes())

def save(name, value):
    with (ROOT / name).open('x', encoding='utf-8') as stream:
        stream.write(json.dumps(value, ensure_ascii=False, indent=2) + '\n')

def main():
    assert sys.argv[1:] == ['--conditional-root-grant']
    grant_path = ROOT / 'conditional-root-grant.json'
    grant = load(grant_path)
    assert grant.get('enabled') is True
    assert grant['scope'] == SCOPE and grant['headSha'] == HEAD
    assert grant['runId'] == 41509039583 and grant['attempt'] == 1
    assert grant['rootReviewedCompleteSource'] is True and grant['maximumInvocations'] == 1
    assert grant['oneFreshReadinessWindowAllowed'] is True
    assert grant['planManifestSha256'] == MANIFEST == sha(ROOT / 'manifest.json')
    assert re.fullmatch('[a-f0-9]{32}', grant['nonce'])
    granted, expires = date(grant['rootGrantedUtc']), date(grant['expiresUtc'])
    assert granted <= now() < expires <= granted + datetime.timedelta(minutes=15)
    assert (now() - granted).total_seconds() <= 300, 'Root dispatch expired before preparation'
    assert sha(pathlib.Path(__file__)) == grant['conditionalWrapperSha256']
    assert sha(COLLECTOR) == grant['checkedCollectorSha256']
    assert sha(COLLECTOR.parent / 'manifest.json') == grant['checkedCollectorManifestSha256']
    for row in load(COLLECTOR.parent / 'manifest.json')['files']:
        assert pathlib.Path(row['file']).name == row['file']
        assert sha(COLLECTOR.parent / row['file']) == row['sha256']
    for name in ['authorization.json', 'execution-attempt.json', 'launch-committed.json',
                 'conditional-start-attempt.json', 'conditional-start-receipt.json']:
        assert not (ROOT / name).exists(), 'Preserve previous scope; no duplicate submission'
    save('conditional-start-attempt.json', {'scope': SCOPE, 'rootGrantSha256': sha(grant_path),
        'rootGrantedUtc': grant['rootGrantedUtc'], 'fixedExpiresUtc': grant['expiresUtc'],
        'startedUtc': now().isoformat(), 'maximumFreshWindows': 1, 'maximumCaptures': 1})
    captured = io.StringIO()
    with contextlib.redirect_stdout(captured):
        result = runpy.run_path(str(COLLECTOR), run_name='__main__')
    assert len(captured.getvalue().encode()) < 65536
    with (ROOT / 'conditional-readiness.stdout.jsonl').open('x', encoding='utf-8') as stream:
        stream.write(captured.getvalue())
    ready_path = ordinary(result['f']).resolve()
    assert ready_path.is_relative_to(ROOT.parent.resolve()) and ready_path.stat().st_size < 1048576
    ready = load(ready_path)
    assert ready == result['ready']
    if ready.get('qualified') is not True:
        save('conditional-start-receipt.json', {'status': 'NO_INVOKE_FRESH_READINESS',
            'scope': SCOPE, 'readinessPath': str(ready_path), 'readinessSha256': sha(ready_path),
            'captureInvocations': 0, 'authorizationCreated': False,
            'fixedExpiresUtc': grant['expiresUtc'], 'audit': result.get('audit')})
        print(json.dumps({'status': 'NO_INVOKE_FRESH_READINESS', 'captureInvocations': 0}))
        return 75
    assert ready['scope'] == SCOPE and ready['headSha'] == HEAD
    assert ready['planManifestSha256'] == MANIFEST and ready['pinsAllMatch'] is True
    assert ready['intervalSeconds'] == 10 and len(ready['samples']) == 5
    samples = ready['samples']
    times = [date(s['utc']) for s in samples]
    assert now() - datetime.timedelta(seconds=120) <= times[0] <= times[-1] <= now()
    assert all(9.5 <= (b - a).total_seconds() <= 10.5 for a, b in zip(times, times[1:]))
    assert all(min(s['availablePhysicalBytes'], s['availableCommitBytes']) >= ADMISSION
        and all(s[k] == 0 for k in ['workerCount', 'identityUnknownCount', 'oldMonitorCount']) for s in samples)
    assert ready['runnerBefore'] == ready['runnerAfter'] == {'id': 21, 'status': 'online', 'busy': False}
    assert now() < expires - datetime.timedelta(seconds=45)
    # This is the actual creation time of a new authorization. The root deadline
    # remains fixed; this never renews or edits any expired/previous grant.
    auth = {'enabled': True, 'scope': SCOPE, 'headSha': HEAD, 'runId': 41509039583,
        'attempt': 1, 'rootReviewedCompleteSource': True, 'maximumInvocations': 1,
        'manifestSha256': '996d89bac0cb5834a4515f8fb374e8ad19bcfad9e66b22b3280ab4f5a78acae5',
        'planManifestSha256': MANIFEST, 'nonce': grant['nonce'], 'issuedUtc': now().isoformat(),
        'expiresUtc': grant['expiresUtc'], 'rootGrantedUtc': grant['rootGrantedUtc'],
        'rootGrantSha256': sha(grant_path), 'readinessPath': str(ready_path),
        'readinessSha256': sha(ready_path)}
    save('authorization.json', auth)
    save('conditional-controller-dispatch.json', {'scope': SCOPE, 'authorizationSha256': sha(ROOT / 'authorization.json'),
        'readinessPath': str(ready_path), 'readinessSha256': sha(ready_path),
        'fixedExpiresUtc': grant['expiresUtc'], 'controllerInvocationsMaximum': 1})
    sys.path.insert(0, str(ROOT))
    sys.argv = [str(ROOT / 'controller.py'), '--execute']
    try:
        runpy.run_path(str(ROOT / 'controller.py'), run_name='__main__')
    except SystemExit as result:
        code = result.code if isinstance(result.code, int) else 1
        save('conditional-start-receipt.json', {'status': 'CONTROLLER_TERMINAL',
            'scope': SCOPE, 'controllerExitCode': code, 'finishedUtc': now().isoformat(),
            'fixedExpiresUtc': grant['expiresUtc'],
            'outcomeSha256': sha(ROOT / 'outcome.json') if (ROOT / 'outcome.json').is_file() else None,
            'cleanupReceiptSha256': sha(ROOT / 'cleanup-receipt.json') if (ROOT / 'cleanup-receipt.json').is_file() else None,
            'fullCiQualified': False, 'retries': 0})
        return code
    raise RuntimeError('Controller must terminate explicitly; no retry')

if __name__ == '__main__':
    raise SystemExit(main())
