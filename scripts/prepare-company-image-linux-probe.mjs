import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createReadStream } from 'node:fs';
import { lstat, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const project = 'tests/PayPlanner.CompanyImageProbe/PayPlanner.CompanyImageProbe.csproj';
export async function sha256(file) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}

export async function listArtifactFiles(directory, prefix = '') {
  const result = [];
  for (const entry of await readdir(path.join(directory, prefix), { withFileTypes: true })) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Symlink in probe artifact: ${relative}`);
    if (entry.isDirectory()) result.push(...await listArtifactFiles(directory, relative));
    else if (entry.isFile()) result.push(relative);
    else throw new Error(`Unexpected artifact entry: ${relative}`);
  }
  return result.sort();
}

export async function prepare(output) {
  if (!output || !path.isAbsolute(output)) throw new Error('Provide a new absolute --output directory; its parent must exist.');
  const target = path.resolve(output);
  if (!(await lstat(path.dirname(target))).isDirectory()) throw new Error('Output parent is not a directory.');
  const api = await readFile(path.join(root, 'backend/PayPlanner.Api.csproj'), 'utf8');
  const probe = await readFile(path.join(root, project), 'utf8');
  for (const name of ['SkiaSharp', 'SkiaSharp.NativeAssets.Linux.NoDependencies']) {
    const version = text => text.match(new RegExp(`Include="${name.replaceAll('.', '\\.')}" Version="([^"]+)"`))?.[1];
    if (!version(api) || version(api) !== version(probe)) throw new Error(`Probe/API version mismatch: ${name}`);
  }
  await mkdir(target); // Deliberately refuses replacement/recursive cleanup of prior evidence.
  const app = path.join(target, 'app');
  const args = ['publish', project, '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'false', '-m:1',
    '-p:UseAppHost=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:UseSharedCompilation=false', '-p:PayPlannerReleaseAudit=true',
    `-p:BaseIntermediateOutputPath=${path.join(target, 'obj')}${path.sep}`, `-p:BaseOutputPath=${path.join(target, 'bin')}${path.sep}`, '-o', app];
  const env = { ...process.env, DOTNET_PROCESSOR_COUNT: '2', DOTNET_gcServer: '0', DOTNET_GCHeapHardLimit: '0x20000000',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: 'true', Email__Enabled: 'false', BackgroundServices__Enabled: 'false' };
  const build = spawnSync('dotnet', args, { cwd: root, env, encoding: 'utf8', maxBuffer: 4 * 1024 * 1024, timeout: 120_000 });
  await writeFile(path.join(target, 'build.log'), `${build.stdout ?? ''}\n${build.stderr ?? ''}`, { flag: 'wx' });
  if (build.error || build.status !== 0) throw new Error(`Probe publication failed (${build.error?.message ?? build.status}); preserved ${target}`);
  const dependencies = JSON.parse(await readFile(path.join(app, 'PayPlanner.CompanyImageProbe.deps.json'), 'utf8'));
  if (Object.keys(dependencies.libraries).some(name => /EntityFramework|Npgsql|Sqlite|AspNetCore|^PayPlanner\.Api\//i.test(name)))
    throw new Error('Probe unexpectedly includes an application/database dependency.');
  const files = [];
  for (const file of await listArtifactFiles(app)) files.push({ path: file, sha256: await sha256(path.join(app, file)) });
  const inputs = [];
  for (const file of [project, 'tests/PayPlanner.CompanyImageProbe/Program.cs', 'backend/Services/Companies/CompanyImageUploadValidator.cs',
    'backend/Models/CompanyBrandImageVersion.cs', 'scripts/prepare-company-image-linux-probe.mjs', 'scripts/run-company-image-linux-probe.mjs'])
    inputs.push({ path: file, sha256: await sha256(path.join(root, file)) });
  const manifest = { schema: 1, kind: 'payplanner-company-image-linux-probe', rid: 'linux-x64', fullGate: false,
    expectedCases: 16, builtAt: new Date().toISOString(), command: ['dotnet', ...args], inputs, files };
  await writeFile(path.join(target, 'probe-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx' });
  return { status: 'prepared-only', directory: target, files: files.length, manifestSha256: await sha256(path.join(target, 'probe-manifest.json')) };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const output = process.argv.slice(2).find(arg => arg.startsWith('--output='))?.slice('--output='.length);
  try { console.log(JSON.stringify(await prepare(output), null, 2)); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
