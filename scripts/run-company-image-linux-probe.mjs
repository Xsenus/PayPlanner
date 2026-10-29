import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { lstat, readFile, realpath, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { listArtifactFiles, sha256 } from './prepare-company-image-linux-probe.mjs';

export const memoryBytes = 512 * 1024 * 1024;
export function validateImage(image) {
  if (!/^mcr\.microsoft\.com\/dotnet\/runtime@sha256:[a-f0-9]{64}$/.test(image ?? ''))
    throw new Error('Use an already loaded official .NET runtime image pinned by digest. Tags/downloads are not allowed.');
}
export function safeRelative(file) {
  return typeof file === 'string' && file.length > 0 && !file.includes('\\') && !file.includes(',')
    && !file.includes(':') && !path.posix.isAbsolute(file) && file.split('/').every(part => part && part !== '.' && part !== '..');
}
export function createArguments({ app, image, runId }) {
  validateImage(image);
  if (!path.posix.isAbsolute(app) || app.includes(',') || !/^[a-f0-9-]{36}$/.test(runId)) throw new Error('Invalid isolated probe path/owner.');
  return ['create', '--pull=never', '--network=none', '--read-only', '--user=65532:65532', '--cap-drop=ALL',
    '--security-opt=no-new-privileges=true', '--pids-limit=64', '--memory=512m', '--memory-swap=512m', '--cpus=1',
    '--ipc=private', '--restart=no', '--log-driver=none', '--ulimit=core=0:0', '--ulimit=nofile=64:64',
    '--tmpfs=/tmp:rw,noexec,nosuid,nodev,size=16m', `--mount=type=bind,src=${app},dst=/probe,readonly`,
    '--label=payplanner.probe=company-image', `--label=payplanner.probe.run=${runId}`,
    '--env=PAYPLANNER_IMAGE_PROBE=isolated-linux', '--env=Email__Enabled=false', '--env=BackgroundServices__Enabled=false',
    '--env=DOTNET_PROCESSOR_COUNT=1', '--env=DOTNET_gcServer=0', '--env=DOTNET_GCHeapHardLimit=0x8000000',
    '--env=DOTNET_EnableDiagnostics=0', '--entrypoint=/usr/bin/timeout', image,
    '--signal=KILL', '40s', 'dotnet', '/probe/PayPlanner.CompanyImageProbe.dll'];
}

export function checkOwnership(container, runId) {
  if (container?.Config?.Labels?.['payplanner.probe'] !== 'company-image' || container.Config.Labels['payplanner.probe.run'] !== runId)
    throw new Error('Container ownership mismatch; no cleanup of this container is permitted.');
}
export function checkIsolation(container, runId, app) {
  checkOwnership(container, runId);
  const host = container.HostConfig;
  const bind = container.Mounts?.filter(mount => mount.Type === 'bind');
  if (JSON.stringify(container.Config.Entrypoint) !== JSON.stringify(['/usr/bin/timeout'])
    || JSON.stringify(container.Config.Cmd) !== JSON.stringify(['--signal=KILL', '40s', 'dotnet', '/probe/PayPlanner.CompanyImageProbe.dll'])
    || container.Config.User !== '65532:65532' || !host.ReadonlyRootfs || host.Privileged || host.NetworkMode !== 'none'
    || host.Memory !== memoryBytes || host.MemorySwap !== memoryBytes || host.NanoCpus !== 1_000_000_000 || host.PidsLimit !== 64
    || !host.CapDrop?.includes('ALL') || !host.SecurityOpt?.some(value => /^no-new-privileges(?:=true)?$/.test(value))
    || Object.keys(host.PortBindings ?? {}).length || host.PublishAllPorts || (host.PidMode && host.PidMode !== 'private') || host.IpcMode !== 'private'
    || host.Devices?.length || host.DeviceRequests?.length || host.VolumesFrom?.length
    || host.RestartPolicy?.Name !== 'no' || host.LogConfig?.Type !== 'none' || Object.keys(host.Tmpfs ?? {}).join() !== '/tmp'
    || bind?.length !== 1 || bind[0].Source !== app || bind[0].Destination !== '/probe' || bind[0].RW
    || container.Mounts?.some(mount => mount.Type !== 'bind' && !(mount.Type === 'tmpfs' && mount.Destination === '/tmp')))
    throw new Error('Docker did not apply the required isolation; probe was not started.');
  for (const setting of ['PAYPLANNER_IMAGE_PROBE=isolated-linux', 'Email__Enabled=false', 'BackgroundServices__Enabled=false'])
    if (!container.Config.Env?.includes(setting)) throw new Error('Required probe safety setting missing.');
}

export async function verifyArtifact(artifact, expectedManifestHash) {
  if (!artifact || !path.isAbsolute(artifact)) throw new Error('Provide an absolute artifact directory.');
  if (!/^[a-f0-9]{64}$/.test(expectedManifestHash ?? '')) throw new Error('Provide the manifest SHA-256 recorded by preparation.');
  const root = await realpath(artifact);
  const app = path.join(root, 'app');
  if ((await lstat(app)).isSymbolicLink() || await realpath(app) !== app) throw new Error('Probe app must not be a symlink.');
  const manifestSha256 = await sha256(path.join(root, 'probe-manifest.json'));
  if (manifestSha256 !== expectedManifestHash) throw new Error('Probe manifest hash changed.');
  const manifest = JSON.parse(await readFile(path.join(root, 'probe-manifest.json'), 'utf8'));
  if (manifest.schema !== 1 || manifest.kind !== 'payplanner-company-image-linux-probe' || manifest.rid !== 'linux-x64' || manifest.expectedCases !== 16)
    throw new Error('Unexpected probe manifest.');
  const actual = await listArtifactFiles(app);
  if (!Array.isArray(manifest.files) || actual.length !== manifest.files.length) throw new Error('Artifact file catalog changed.');
  const seen = new Set();
  for (const file of manifest.files) {
    if (!safeRelative(file.path) || seen.has(file.path) || !actual.includes(file.path) || !/^[a-f0-9]{64}$/.test(file.sha256)) throw new Error('Invalid artifact file entry.');
    seen.add(file.path);
    if (await sha256(path.join(app, file.path)) !== file.sha256) throw new Error(`Artifact hash changed: ${file.path}`);
  }
  for (const required of ['PayPlanner.CompanyImageProbe.dll', 'PayPlanner.CompanyImageProbe.deps.json', 'PayPlanner.CompanyImageProbe.runtimeconfig.json', 'libSkiaSharp.so'])
    if (!seen.has(required)) throw new Error(`Required probe file missing: ${required}`);
  return { app, manifest, manifestSha256 };
}

export async function resolveResultPath(file, app) {
  if (!file || !path.isAbsolute(file)) throw new Error('Provide a new absolute --result file outside the mounted app.');
  const result = path.join(await realpath(path.dirname(file)), path.basename(file));
  if (result === app || result.startsWith(`${app}${path.sep}`)) throw new Error('Result must be outside the mounted app.');
  try { await lstat(result); throw new Error('Result file already exists; refusing to repeat a probe or overwrite evidence.'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  return result;
}

export function parseProbeOutput(stdout) {
  const partialCases = [];
  // Per-case lines survive an OOM/timeout even when the final summary is incomplete.
  const summaryStart = stdout.lastIndexOf('\n{\n');
  const eventText = summaryStart < 0 ? stdout : stdout.slice(0, summaryStart);
  for (const line of eventText.split('\n')) {
    try { const event = JSON.parse(line); if (event.type === 'case') partialCases.push(event.result); } catch { /* incomplete native output is preserved by the caller */ }
  }
  let output;
  try { output = JSON.parse(summaryStart < 0 ? stdout : stdout.slice(summaryStart + 1)); } catch { /* no successful summary */ }
  return { partialCases, output };
}

export function executeProbe({ app, image, runId, docker }) {
  const engine = JSON.parse(docker(['info', '--format', '{{json .}}']).stdout);
  if (!engine.MemoryLimit || !engine.SwapLimit || !engine.CPUCfsQuota || !engine.PidsLimit
    || !engine.SecurityOptions?.some(value => value.startsWith('name=seccomp')))
    throw new Error('Existing Docker daemon must support memory/swap/CPU/PID limits and normal seccomp isolation.');
  const imageInfo = JSON.parse(docker(['image', 'inspect', image]).stdout)[0];
  if (imageInfo.Os !== 'linux' || imageInfo.Architecture !== 'amd64' || !imageInfo.RepoDigests?.includes(image)
    || !imageInfo.Config?.Env?.some(value => /^DOTNET_VERSION=9\.0\./.test(value))) throw new Error('Existing image is not the requested Linux x64 .NET 9 runtime.');
  let id;
  const report = { status: 'failed', image, runId };
  const inspect = () => JSON.parse(docker(['inspect', id]).stdout)[0];
  try {
    id = docker(createArguments({ app, image, runId })).stdout.trim();
    if (!/^[a-f0-9]{64}$/.test(id)) throw new Error('Invalid Docker container ID.');
    checkIsolation(inspect(), runId, app);
    if (inspect().Image !== imageInfo.Id) throw new Error('Container image differs from the inspected digest.');
    const execution = docker(['start', '--attach', id], 45_000, true);
    const container = inspect();
    checkOwnership(container, runId);
    report.exitCode = container.State.ExitCode;
    report.oomKilled = container.State.OOMKilled;
    report.executionError = execution.error?.message;
    report.stderr = execution.stderr;
    const parsed = parseProbeOutput(execution.stdout);
    report.partialCases = parsed.partialCases;
    report.output = parsed.output;
    if (!parsed.output) report.incompleteStdout = execution.stdout;
    if (execution.status === 0 && !execution.error && !container.State.OOMKilled && container.State.ExitCode === 0
      && report.output?.status === 'passed' && report.output.cases?.length === 16 && report.output.cases.every(item => item.passed)) report.status = 'passed';
  } catch (error) { report.error = error.message; }
  finally {
    if (id && /^[a-f0-9]{64}$/.test(id)) {
      try {
        const container = inspect();
        checkOwnership(container, runId);
        if (container.State.Running) docker(['kill', id]);
        docker(['rm', id]);
        report.ownedContainerRemoved = true;
      } catch (error) { report.cleanupError = error.message; report.status = 'failed'; }
    }
  }
  return report;
}

async function main() {
  const option = key => process.argv.slice(2).find(value => value.startsWith(`--${key}=`))?.slice(key.length + 3);
  const image = option('image');
  validateImage(image);
  const artifact = await verifyArtifact(option('artifact'), option('manifest-sha256'));
  const runId = randomUUID();
  if (process.argv.includes('--dry-run')) return console.log(JSON.stringify({ status: 'plan-only', ...artifact, arguments: createArguments({ app: artifact.app.replaceAll('\\', '/').replace(/^[A-Za-z]:/, '/artifact-host'), image, runId }) }, null, 2));
  if (process.platform !== 'linux') throw new Error('Execution is allowed only on an existing isolated Linux host.');
  const socket = option('socket') ?? '/var/run/docker.sock';
  if (!path.isAbsolute(socket) || !(await lstat(socket)).isSocket()) throw new Error('A local existing Docker Unix socket is required; remote contexts/daemon startup are not allowed.');
  const result = await resolveResultPath(option('result'), artifact.app);
  const env = { ...process.env };
  for (const key of ['DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH']) delete env[key];
  const docker = (args, timeout = 5_000, allowFailure = false) => {
    const execution = spawnSync('docker', [`--host=unix://${socket}`, ...args], { env, encoding: 'utf8', timeout, maxBuffer: 1024 * 1024 });
    if (!allowFailure && (execution.error || execution.status !== 0)) throw new Error(`Local Docker operation failed: ${execution.error?.message ?? execution.stderr}`);
    return execution;
  };
  const report = { ...executeProbe({ app: artifact.app, image, runId, docker }), manifestSha256: artifact.manifestSha256, finishedAt: new Date().toISOString(), fullGate: false };
  await writeFile(result, `${JSON.stringify(report, null, 2)}\n`, { flag: 'wx' });
  console.log(JSON.stringify(report, null, 2));
  if (report.status !== 'passed') process.exitCode = 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { await main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
