import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, sep } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const tempRoot = resolve(tmpdir());
const fixture = mkdtempSync(join(tempRoot, 'pp-reference-cache-'));
const childEnvironment = {
  ...process.env,
  BackgroundServices__Enabled: 'false',
  Email__Enabled: 'false',
  DOTNET_PROCESSOR_COUNT: '1',
  DOTNET_gcServer: '0',
  DOTNET_GCHeapHardLimit: '0x20000000',
};
let passed = false;
function dotnet(stage, args) {
  try {
    return execFileSync('dotnet', args, { cwd: root, env: childEnvironment, encoding: 'utf8', timeout: 180_000, maxBuffer: 2 * 1024 * 1024 });
  } catch (error) {
    writeFileSync(join(fixture, 'private-error.log'), `${stage}\n${error.stdout ?? ''}\n${error.stderr ?? ''}`);
    throw new Error(`Проверка SDK не завершилась: ${stage}; локальный журнал: ${join(fixture, 'private-error.log')}.`);
  }
}
const project = join(fixture, 'Probe.csproj');
const source = join(fixture, 'Contract.cs');
const reference = join(fixture, 'obj', 'Release', 'net9.0', 'ref', 'Probe.dll');
function build(revision, includeRevision) {
  dotnet('compile reference probe', ['build', project, '-c', 'Release', '--nologo', '-v', 'quiet', '-m:1',
    '-p:UseSharedCompilation=false', `-p:SourceRevisionId=${revision}`,
    '-p:SourceControlInformationFeatureSupported=true', `-p:IncludeSourceRevisionInInformationalVersion=${includeRevision}`]);
  return createHash('sha256').update(readFileSync(reference)).digest('hex');
}
try {
  for (const relative of ['backend/PayPlanner.Api.csproj', 'backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj']) {
    const property = dotnet('evaluate project settings', ['msbuild', join(root, relative), '-nologo', '-getProperty:IncludeSourceRevisionInInformationalVersion']).trim();
    if (property !== 'false') throw new Error(`${relative}: служебная версия по-прежнему зависит от Git SHA.`);
  }
  writeFileSync(project, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Probe</AssemblyName><Deterministic>true</Deterministic></PropertyGroup></Project>');
  writeFileSync(source, 'public class MigrationContextContract { }');
  const revisionA = 'a'.repeat(40);
  const revisionB = 'b'.repeat(40);
  const stableA = build(revisionA, false);
  const stableB = build(revisionB, false);
  if (stableA !== stableB) throw new Error('Ссылочная сборка изменилась при смене только Git SHA.');
  writeFileSync(source, 'public class MigrationContextContract { public int Marker { get; set; } }');
  const changedContract = build(revisionA, false);
  if (changedContract === stableA) throw new Error('Изменение публичного контракта не изменило ссылочную сборку.');
  const oldA = build(revisionA, true);
  const oldB = build(revisionB, true);
  if (oldA === oldB) throw new Error('Контрольный пример не воспроизвёл прежнюю зависимость от Git SHA.');
  passed = true;
  console.log('Проверка ссылочных сборок пройдена: Git SHA не меняет контракт; изменение публичного API меняет его; прежнее поведение воспроизведено.');
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
} finally {
  const absoluteFixture = resolve(fixture);
  if (passed && absoluteFixture.startsWith(tempRoot + sep)) rmSync(absoluteFixture, { recursive: true, force: true });
}
