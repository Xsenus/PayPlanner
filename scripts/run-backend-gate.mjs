import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { closeSync, cpSync, existsSync, mkdirSync, openSync, readFileSync, renameSync, rmSync, writeFileSync, statfsSync } from 'node:fs';
import { freemem, availableParallelism } from 'node:os';
import { basename, dirname, isAbsolute, relative, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { snapshotVerificationInputs, assertUnchanged } from './verification-inputs.mjs';
import { verifyPostgres, postgresSettings } from './verify-xsenus-runner.mjs';
import { testedDocumentation } from './verification-scope.mjs';

const gib = 1024 ** 3;
const script = fileURLToPath(import.meta.url);
const hashFile = path => createHash('sha256').update(readFileSync(path)).digest('hex').toUpperCase();
const readJson = path => JSON.parse(readFileSync(path, 'utf8'));
const alive = pid => { try { process.kill(pid, 0); return true; } catch (e) { return e.code !== 'ESRCH'; } };

// Bind discovery's build SDK entry point before preparation can be reused.
export function captureVstestEntryPoint(summaryPath) {
  const summary = readFileSync(summaryPath);
  const path = JSON.parse(summary.toString('utf8')).VstestConsolePath;
  if (typeof path !== 'string' || !isAbsolute(path)
    || basename(path) !== 'vstest.console.dll' || !existsSync(path))
    throw new Error('Discovery must record the build SDK VSTest entry point; preserve old preparation and create a new one');
  return { path, sha256: hashFile(path), summarySha256: createHash('sha256').update(summary).digest('hex').toUpperCase() };
}

export function assertVstestEntryPoint(summaryPath, expected) {
  if (expected?.invalidated) throw new Error('This preparation was invalidated by SDK input drift; preserve it and create a new one');
  if (!expected || ['path', 'sha256', 'summarySha256'].some(key => typeof expected[key] !== 'string'))
    throw new Error('Prepared results lack the bound SDK VSTest input; preserve them and create a new preparation');
  try {
    const actual = captureVstestEntryPoint(summaryPath);
    if (actual.path !== expected.path || actual.sha256 !== expected.sha256 || actual.summarySha256 !== expected.summarySha256)
      throw new Error('Discovery summary or SDK VSTest input changed; preserve the original results');
    return actual.path;
  } catch (error) { expected.invalidated = true; throw error; }
}

export function workerCount(requested = 'auto', free = freemem(), cores = availableParallelism(), allowMemoryPressure = false) {
  requested = String(requested);
  if (!['auto', '1', '2', '4'].includes(requested)) throw new Error('Workers must be auto, 1, 2 or 4');
  if (!Number.isFinite(free) || !Number.isFinite(cores) || free < 0 || cores < 1) throw new Error('Available memory and processor count must be known');
  if (allowMemoryPressure && !['1', 'auto'].includes(requested)) throw new Error('Memory pressure override permits one worker or resource-controlled auto');
  if (free < 4 * gib && !allowMemoryPressure) throw new Error('At least 4 GiB of available RAM is required before starting tests');
  const safe = free >= 12 * gib && cores >= 8 ? 4 : free >= 8 * gib && cores >= 4 ? 2 : 1;
  if (requested === '2' && safe < 2) throw new Error('Two workers require 8 GiB available RAM and four processors');
  if (requested === '4' && safe < 4) throw new Error('Four workers require 12 GiB available RAM and eight processors');
  return requested === 'auto' ? safe : Number(requested);
}

export function selectPendingShards(states, count = 4) {
  if (count !== 4 || !Array.isArray(states) || states.length !== count) throw new Error('Exactly four shard states required');
  const pending = [];
  for (let index = 0; index < count; index++) {
    const item = states[index];
    if (item.shard !== index || !['pending', 'running', 'passed', 'failed'].includes(item.status)) throw new Error('Invalid shard state');
    if (item.launchPending) throw new Error(`Shard ${index} has an unrecorded launch outcome; resolve the owned process before resuming`);
    if (item.status === 'running' && item.pid && alive(item.pid)) throw new Error(`Shard ${index} is still running; preserve its process and logs`);
    if (item.status !== 'passed') pending.push(index);
    else if (item.exitCode !== 0) throw new Error('An unsuccessful invocation cannot be reused');
  }
  return pending;
}

export function assertStageCanResume(state) {
  if (state.vstestEntryPoint?.invalidated) throw new Error('This preparation was invalidated by SDK input drift; preserve it and create a new one');
  if (state.activeLaunchPending) throw new Error('An owned stage has an unrecorded launch outcome; resolve its process before resuming');
  if (state.activePid && alive(state.activePid)) throw new Error('A preserved child process is still alive; wait for it before resuming');
}

function save(path, value) {
  const temp = `${path}.${process.pid}.tmp`;
  writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`);
  renameSync(temp, path);
}

export async function pool(items, count, action) {
  if (!Number.isInteger(count) || count < 1 || count > 2) throw new Error('Pool requires one or two workers');
  let next = 0;
  // A failed worker does not discard already running siblings. Drain and preserve
  // all outcomes, then fail the gate; never turn a partial run into success.
  const errors = [];
  await Promise.all(Array.from({ length: count }, async () => {
    while (next < items.length) {
      const item = items[next++];
      try { await action(item); } catch (error) { errors.push(error); }
    }
  }));
  if (errors.length) throw new AggregateError(errors, `${errors.length} shard invocation(s) failed; inspect preserved logs`);
}

export function parseArguments(args) {
  const result = { configuration: 'Release', workers: 'auto', suite: 'all', resume: false, detach: false, untestedDocs: [] };
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--resume' || arg === '--detach') result[arg.slice(2)] = true;
    else if (arg === '--prepare-only') result.prepareOnly = true;
    else if (arg === '--allow-memory-pressure') result.allowMemoryPressure = true;
    else if (arg === '--untested-doc' && args[i + 1] && !args[i + 1].startsWith('--')) result.untestedDocs.push(args[++i]);
    else if (['--results', '--configuration', '--workers', '--suite', '--timings'].includes(arg) && args[i + 1] && !args[i + 1].startsWith('--')) result[arg.slice(2)] = args[++i];
    else throw new Error(`Unsupported argument: ${arg}`);
  }
  if (!result.results || !['Debug', 'Release'].includes(result.configuration)) throw new Error('Use --results <new external directory> [--configuration Debug|Release] [--workers auto|1|2|4] [--suite all|fast|heavy] [--timings <qualified profile>] [--resume] [--detach]');
  if (!['auto', '1', '2', '4'].includes(result.workers)) throw new Error('Workers must be auto, 1, 2 or 4');
  if (!['all', 'fast', 'heavy'].includes(result.suite)) throw new Error('Suite must be all, fast or heavy');
  if (result.allowMemoryPressure && !['1', 'auto'].includes(result.workers)) throw new Error('Memory pressure override requires --workers 1 or auto');
  const root = resolve(dirname(script), '..');
  for (const path of result.untestedDocs) {
    const normalized = relative(root, resolve(root, path)).replaceAll('\\', '/').toLowerCase();
    if (Object.keys(testedDocumentation).some(known => known.toLowerCase() === normalized)) throw new Error('A tested document cannot be excluded from verification: ' + normalized);
  }
  return result;
}

async function run(options) {
  const root = resolve(dirname(script), '..');
  const results = resolve(options.results);
  const normalize = value => process.platform === 'win32' ? value.toLowerCase() : value;
  if (normalize(results) === normalize(root) || normalize(results).startsWith(`${normalize(root)}\\`) || normalize(results).startsWith(`${normalize(root)}/`)) throw new Error('Keep durable results outside the checkout');
  mkdirSync(results, { recursive: true });
  const stateFile = resolve(results, 'state.json');
  const lock = resolve(results, 'runner.lock');
  let descriptor;
  if (existsSync(lock)) {
    // Serialize stale-lock recovery. Another resumer must never remove a newly
    // acquired lock between inspecting the old owner and opening its own lock.
    const recoveryPath = `${lock}.recovery`;
    const recovery = openSync(recoveryPath, 'wx');
    try {
      const previous = readJson(lock);
      if (!options.resume || alive(previous.pid)) throw new Error('Existing runner lock: wait for the runner or explicitly resume after it exits');
      const previousState = existsSync(stateFile) ? readJson(stateFile) : null;
      if (previousState) { assertStageCanResume(previousState); selectPendingShards(previousState.shards); }
      rmSync(lock);
      descriptor = openSync(lock, 'wx');
    } finally { closeSync(recovery); rmSync(recoveryPath); }
  } else descriptor = openSync(lock, 'wx');
  writeFileSync(descriptor, JSON.stringify({ pid: process.pid, startedUtc: new Date().toISOString() }));
  closeSync(descriptor);
  let state;
  let supervisorTool;
  try {
    if (process.platform !== 'win32') throw new Error('This local runner requires the existing Windows PostgreSQL 17 setup');
    process.env.BackgroundServices__Enabled = 'false';
    process.env.Email__Enabled = 'false';
    process.env.CI = 'true';
    process.env.PAYPLANNER_TEST_PSQL = 'C:/Program Files/PostgreSQL/17/bin/psql.exe';
    process.env.PAYPLANNER_TEST_PG_DUMP = 'C:/Program Files/PostgreSQL/17/bin/pg_dump.exe';
    verifyPostgres();
    if (!process.env.PAYPLANNER_TEST_PLAYWRIGHT_CLI || !existsSync(process.env.PAYPLANNER_TEST_PLAYWRIGHT_CLI)) throw new Error('Set PAYPLANNER_TEST_PLAYWRIGHT_CLI to the installed pinned CLI before the full gate');
    if (process.env.PAYPLANNER_TEST_BROWSER !== 'chrome') throw new Error('This qualified Windows runner uses PAYPLANNER_TEST_BROWSER=chrome');
    const cli = process.env.PAYPLANNER_TEST_PLAYWRIGHT_CLI;
    const cliPackage = readJson(resolve(dirname(cli), 'package.json'));
    if (cliPackage.name !== '@playwright/cli' || cliPackage.version !== '0.1.22') throw new Error('The release gate requires @playwright/cli 0.1.22');
    const chromeCandidates = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA]
      .filter(Boolean).map(base => resolve(base, 'Google/Chrome/Application/chrome.exe'));
    async function qualifyEnvironment() {
      verifyPostgres();
      const identity = spawnSync(process.env.PAYPLANNER_TEST_PSQL, ['-X', '-w', '-h', '127.0.0.1', '-p', '5433', '-U', 'payplanner_ci', '-d', 'postgres', '-A', '-t', '-q', '-v', 'ON_ERROR_STOP=1', '-c', "select current_setting('server_version'), inet_server_addr(), inet_server_port(), pg_postmaster_start_time(), current_user, current_setting('default_tablespace')"], {
        env: { ...process.env, PGPASSWORD: postgresSettings(process.env.PAYPLANNER_TEST_POSTGRES).password }, encoding: 'utf8', timeout: 15000, windowsHide: true,
      });
      if (identity.status !== 0 || !identity.stdout.trim()) throw new Error('Cannot qualify current PostgreSQL environment');
      const sdk = spawnSync('dotnet', ['--version'], { encoding: 'utf8', windowsHide: true });
      if (sdk.status !== 0) throw new Error('Existing .NET SDK is unavailable');
      const browsers = chromeCandidates.filter(existsSync);
      if (!browsers.length) throw new Error('Install the release browser before verification');
      const externalTools = [cli, process.env.PAYPLANNER_TEST_PSQL, process.env.PAYPLANNER_TEST_PG_DUMP, ...browsers];
      return { sdk: sdk.stdout.trim(), configuration: options.configuration, postgresIdentity: identity.stdout.trim(), externalToolHashes: externalTools.map(path => ({ path, sha256: hashFile(path) })) };
    }
    const inputOptions = { root, untestedDocs: options.untestedDocs, testedDocs: Object.keys(testedDocumentation), dependencyRoots: [resolve(root, 'node_modules'), dirname(dirname(dirname(cli)))], environment: qualifyEnvironment };
    const inputs = await snapshotVerificationInputs(inputOptions);
    if (options.resume) {
      state = readJson(stateFile);
      if (state.schema !== 2 || state.suite !== options.suite || state.configuration !== options.configuration || state.inputs.digest !== inputs.digest) throw new Error('Inputs/environment or requested suite changed: use a new results directory and one new gate');
      if (options.timings && state.timingProfile?.sha256 !== hashFile(resolve(options.timings))) throw new Error('Timing profile changed; preserve the original execution plan');
      selectPendingShards(state.shards);
      assertStageCanResume(state);
    } else {
      if (existsSync(stateFile)) throw new Error('Preserve previous results; use --resume or a new directory');
      state = { schema: 2, configuration: options.configuration, suite: options.suite, inputs, startedUtc: new Date().toISOString(), status: 'preflight', qualified: false,
        shards: Array.from({ length: 4 }, (_, shard) => ({ shard, status: 'pending', attempts: 0 })) };
      save(stateFile, state);
    }
    function checkedVstestEntryPoint() {
      try { return assertVstestEntryPoint(discoverySummary, state.vstestEntryPoint); }
      finally {
        // Persist drift before unwinding/draining siblings: restoring bytes must not revive this preparation.
        if (state.vstestEntryPoint?.invalidated) { state.qualified = false; save(stateFile, state); }
      }
    }
    async function command(name, executable, args, env = {}, shardState) {
      if (state.vstestEntryPoint) checkedVstestEntryPoint();
      const log = resolve(results, `${name}.log`);
      const fd = openSync(log, 'a');
      const started = Date.now();
      console.log(`${new Date().toISOString()} ${name}; log: ${log}`);
      const nativeExecutable = executable;
      const nativeArgs = args;
      if (supervisorTool && executable === 'dotnet') {
        const spec = resolve(results, `${name}.invocation.json`);
        save(spec, { FileName: executable, Arguments: args });
        args = [supervisorTool, 'exec', '--spec', spec];
      }
      // Persist intent BEFORE creating a process. If the supervisor dies between
      // spawn and recording the owner PID, resume must fail closed instead of
      // launching an overlapping copy of an unrecorded, still-running job.
      if (shardState) Object.assign(shardState, { launchPending: true, pid: null });
      else { state.activeLaunchPending = true; state.activePid = null; }
      save(stateFile, state);
      const child = spawn(executable, args, { cwd: root, env: { ...process.env, ...env }, stdio: ['ignore', fd, fd], windowsHide: true });
      closeSync(fd);
      if (shardState) Object.assign(shardState, { pid: child.pid, status: 'running', log, launchPending: false });
      else { state.activePid = child.pid; state.activeLaunchPending = false; }
      save(stateFile, state);
      const exit = await new Promise((accept, reject) => { child.once('error', reject); child.once('exit', code => accept(code ?? -1)); });
      if (shardState) Object.assign(shardState, { pid: null, exitCode: exit, status: exit === 0 ? 'passed' : 'failed', elapsedSeconds: (Date.now() - started) / 1000 });
      else state.activePid = null;
      (state.stages ??= []).push({ name, executable: nativeExecutable, args: nativeArgs, log, exitCode: exit, elapsedSeconds: (Date.now() - started) / 1000 });
      save(stateFile, state);
      if (exit !== 0) throw new Error(`${name} exited ${exit}; see ${log}`);
      if (state.vstestEntryPoint) checkedVstestEntryPoint();
    }
    const config = options.configuration;
    const runtime = resolve(results, 'runtime');
    const toolDir = resolve(results, 'tool');
    const tool = resolve(toolDir, 'PayPlanner.TestShards.dll');
    const assembly = resolve(runtime, 'PayPlanner.Api.Tests.dll');
    const manifest = resolve(results, 'test-plan.json');
    const discoverySummary = manifest.replace(/\.json$/, '.summary.json');
    if (!state.prepared) {
      if (existsSync(manifest) || existsSync(runtime) || existsSync(toolDir)) throw new Error('Preparation was interrupted; preserve evidence and start in a new directory');
      const disk = statfsSync(results);
      if (disk.bavail * disk.bsize < 24 * gib) throw new Error('Leave at least 20 GiB free plus 4 GiB for this operation');
      const freeMemory = freemem();
      if (freeMemory < 8 * gib && !options.allowMemoryPressure) throw new Error('Build needs 8 GiB available RAM; close unrelated heavy applications before retrying');
      (state.resourceChecks ??= []).push({ stage: 'build', freeMemory, allowMemoryPressure: Boolean(options.allowMemoryPressure) });
      if (freeMemory < 8 * gib) console.warn('Explicit build under memory pressure; elapsed times include contention. Test concurrency remains limited by measured RAM and CPU.');
      save(stateFile, state);
      await command('workflow-preflight', process.execPath, ['scripts/validate-deploy-workflow.mjs']);
      await command('encoding-preflight', process.execPath, ['scripts/check-source-encoding.mjs']);
      const buildEnv = { DOTNET_GCHeapHardLimit: '200000000', DOTNET_PROCESSOR_COUNT: '2', DOTNET_GCConserveMemory: '9', DOTNET_gcServer: '0' };
      await command('build-shard-tool', 'dotnet', ['build', 'tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj', '-c', config, '--no-restore', '-m:1', '-nr:false', '-p:UseSharedCompilation=false'], buildEnv);
      supervisorTool = resolve(root, `tools/PayPlanner.TestShards/bin/${config}/net9.0/PayPlanner.TestShards.dll`);
      await command('build-tests', 'dotnet', ['build', 'backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj', '-c', config, '--no-restore', '-m:1', '-nr:false', '-p:UseSharedCompilation=false'], buildEnv);
      await assertUnchanged(inputs, inputOptions);
      cpSync(resolve(root, `backend/PayPlanner.Api.Tests/bin/${config}/net9.0`), runtime, { recursive: true, errorOnExist: true, force: false });
      cpSync(resolve(root, `tools/PayPlanner.TestShards/bin/${config}/net9.0`), toolDir, { recursive: true, errorOnExist: true, force: false });
      supervisorTool = tool;
      const revision = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true });
      if (revision.status !== 0 || !/^[a-f0-9]{40}$/.test(revision.stdout.trim())) throw new Error('Exact source revision is unavailable');
      const timingArgs = [];
      if (options.timings) {
        const timingSource = resolve(options.timings);
        const timingCopy = resolve(results, 'input-timings.json');
        cpSync(timingSource, timingCopy, { errorOnExist: true, force: false });
        state.timingProfile = { source: timingSource, sha256: hashFile(timingCopy) };
        timingArgs.push('--timings', timingCopy);
      }
      await command('discover', 'dotnet', [tool, 'discover', '--assembly', assembly, '--manifest', manifest, '--revision', revision.stdout.trim(), '--shards', '4', '--execution-profile', 'bounded', ...timingArgs]);
      if (state.timingProfile && hashFile(resolve(results, 'input-timings.json')) !== state.timingProfile.sha256) throw new Error('The copied timing profile changed during discovery');
      state.manifestSha = hashFile(manifest);
      state.vstestEntryPoint = captureVstestEntryPoint(discoverySummary);
      state.artifacts = await snapshotVerificationInputs({ ...inputOptions, artifactRoots: [runtime, toolDir] });
      state.prepared = true;
      save(stateFile, state);
    }
    supervisorTool = tool;
    if (state.manifestSha !== hashFile(manifest)) throw new Error('Preserve the original manifest; its hash changed');
    const artifactOptions = { ...inputOptions, artifactRoots: [runtime, toolDir] };
    await assertUnchanged(state.artifacts, artifactOptions);
    const vstestConsolePath = checkedVstestEntryPoint();
    if (options.prepareOnly) {
      state.status = 'prepared'; state.qualified = false; save(stateFile, state);
      console.log(JSON.stringify({ prepared: true, qualified: false, results, cases: readJson(manifest).Cases.length }));
      return;
    }
    const testEnv = { DOTNET_GCHeapHardLimit: 'C0000000', DOTNET_PROCESSOR_COUNT: '2', DOTNET_GCConserveMemory: '9', DOTNET_gcServer: '0' };
    if (!state.guardsPassed) {
      // These cheap source/inventory guards catch drift before hours of DB/browser work.
      const preflight = resolve(results, 'preflight'); mkdirSync(preflight, { recursive: true });
      const settings = resolve(preflight, 'gate.runsettings');
      writeFileSync(settings, '<RunSettings><RunConfiguration><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>');
      await command('backend-preflight', 'dotnet', ['exec', vstestConsolePath, assembly, `/Settings:${settings}`, '/TestCaseFilter:FullyQualifiedName~BatchOperationAuditCoverageTests|FullyQualifiedName~DocumentationProcessTests|FullyQualifiedName~TestShardCoverageTests', '/Logger:trx;LogFileName=preflight.trx', `/ResultsDirectory:${preflight}`], testEnv);
      state.guardsPassed = true; save(stateFile, state);
    }
    const freeMemory = freemem();
    const count = workerCount(options.workers, freeMemory, availableParallelism(), options.allowMemoryPressure);
    (state.resourceChecks ??= []).push({ stage: 'tests', freeMemory, allowMemoryPressure: Boolean(options.allowMemoryPressure), workers: count });
    state.workers = options.workers; state.initialWorkerBudget = count; state.outerWorkers = 1; state.status = 'testing'; save(stateFile, state);
    const shardRoot = resolve(results, 'shards'); mkdirSync(shardRoot, { recursive: true });
    const attempts = resolve(results, 'previous-attempts');
    // The compiled coordinator owns the only concurrency budget. Never multiply
    // outer shard workers by its inner process workers; exclusive cases run alone.
    await pool(selectPendingShards(state.shards), 1, async index => {
      const item = state.shards[index];
      const directory = resolve(shardRoot, `shard-${index}`);
      if (existsSync(directory)) {
        mkdirSync(attempts, { recursive: true });
        renameSync(directory, resolve(attempts, `shard-${index}-attempt-${item.attempts}`));
      }
      item.attempts++;
      await command(`shard-${index}-attempt-${item.attempts}`, 'dotnet', [tool, 'run', '--manifest', manifest, '--manifest-sha', state.manifestSha, '--shard', String(index), '--results', directory, '--workers', options.workers, '--suite', options.suite], testEnv, item);
    });
    await assertUnchanged(state.artifacts, artifactOptions);
    const aggregate = resolve(shardRoot, options.suite === 'all' ? 'aggregate-qualification.json' : 'aggregate-subset-qualification.json');
    // Always re-verify the exact union. Do not trust persisted status booleans.
    if (existsSync(aggregate)) renameSync(aggregate, resolve(results, `aggregate-previous-${Date.now()}.json`));
    const suiteArgs = options.suite === 'all' ? [] : ['--suite', options.suite];
    await command('verify-union', 'dotnet', [tool, 'verify', '--manifest', manifest, '--manifest-sha', state.manifestSha, '--results-root', shardRoot, ...suiteArgs]);
    if (options.suite !== 'all') {
      await assertUnchanged(state.artifacts, artifactOptions);
      checkedVstestEntryPoint();
      state.status = 'completed'; state.qualified = false; state.subsetPassed = true; state.finishedUtc = new Date().toISOString();
      state.elapsedSeconds = (Date.parse(state.finishedUtc) - Date.parse(state.startedUtc)) / 1000;
      save(stateFile, state);
      console.log(JSON.stringify({ qualified: false, subsetPassed: true, suite: options.suite, elapsedSeconds: state.elapsedSeconds, results }));
      return;
    }
    if (!state.restoredPassed) {
      const restored = resolve(results, 'restored-browser');
      if (existsSync(restored)) renameSync(restored, resolve(results, `restored-previous-${Date.now()}`));
      mkdirSync(restored);
      const settings = resolve(restored, 'gate.runsettings');
      writeFileSync(settings, '<RunSettings><RunConfiguration><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>');
      await command('restored-browser', 'dotnet', ['exec', vstestConsolePath, assembly, `/Settings:${settings}`, '/Logger:trx;LogFileName=results.trx', `/ResultsDirectory:${restored}`, '/TestCaseFilter:FullyQualifiedName~RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource'], { ...testEnv, PAYPLANNER_TEST_RESTORED_BROWSER: 'true' });
      state.restoredExitCode = 0;
      state.restoredPassed = true; save(stateFile, state);
    }
    const restored = resolve(results, 'restored-browser');
    const restoredProof = resolve(restored, 'restored-qualification.json');
    if (existsSync(restoredProof)) renameSync(restoredProof, resolve(results, `restored-qualification-previous-${Date.now()}.json`));
    if (state.restoredExitCode !== 0) throw new Error('A successful restored browser invocation is required');
    await command('verify-restored', 'dotnet', [tool, 'verify-restored', '--manifest', manifest, '--manifest-sha', state.manifestSha, '--results', restored, '--exit-code', String(state.restoredExitCode)]);
    await assertUnchanged(state.artifacts, artifactOptions);
    const timingPath = resolve(results, 'timings.json');
    if (!existsSync(timingPath)) await command('timings', 'dotnet', [tool, 'timings', '--manifest', manifest, '--manifest-sha', state.manifestSha, '--results-root', shardRoot, '--output', timingPath]);
    checkedVstestEntryPoint();
    state.status = 'completed'; state.qualified = true; state.finishedUtc = new Date().toISOString();
    state.elapsedSeconds = (Date.parse(state.finishedUtc) - Date.parse(state.startedUtc)) / 1000;
    save(stateFile, state);
    console.log(JSON.stringify({ qualified: true, backendOnly: true, elapsedSeconds: state.elapsedSeconds, results }));
  } catch (error) {
    if (state) { state.status = 'failed'; state.qualified = false; state.error = error.message; save(stateFile, state); }
    throw error;
  } finally { rmSync(lock, { force: true }); }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const options = parseArguments(process.argv.slice(2));
    if (options.detach) {
      const results = resolve(options.results); mkdirSync(results, { recursive: true });
      const log = resolve(results, 'supervisor.log'); const fd = openSync(log, 'a');
      const child = spawn(process.execPath, [script, ...process.argv.slice(2).filter(x => x !== '--detach')], { detached: true, windowsHide: true, stdio: ['ignore', fd, fd] });
      closeSync(fd); child.unref();
      console.log(JSON.stringify({ started: true, pid: child.pid, log, state: resolve(results, 'state.json') }));
    } else await run(options);
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
