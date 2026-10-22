import { existsSync, readFileSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

export const postgresBin = 'C:/Program Files/PostgreSQL/17/bin';

export function requireTrustedHost(env = process.env, platform = process.platform) {
  const trustedRef = env.GITHUB_REF === 'refs/heads/main'
    || (env.GITHUB_REF === 'refs/heads/claude/practical-bardeen-pglcf3' && env.GITHUB_EVENT_NAME === 'workflow_dispatch');
  if (platform !== 'win32' || env.COMPUTERNAME?.toUpperCase() !== 'XSENUS'
    || env.GITHUB_REPOSITORY !== 'Xsenus/PayPlanner' || !trustedRef
    || env.GITHUB_WORKFLOW_REF !== `Xsenus/PayPlanner/.github/workflows/deploy.yml@${env.GITHUB_REF}`
    || !['push', 'workflow_dispatch'].includes(env.GITHUB_EVENT_NAME)) {
    throw new Error('Only trusted PayPlanner release refs on XSENUS may use this runner');
  }
}

export function postgresSettings(raw) {
  const fields = new Map();
  for (const part of (raw ?? '').split(';').filter(Boolean)) {
    const index = part.indexOf('=');
    const key = part.slice(0, index).trim().toLowerCase();
    if (index < 1 || fields.has(key)) throw new Error('Invalid dedicated test connection');
    fields.set(key, part.slice(index + 1).trim());
  }
  if (fields.size !== 6 || fields.get('host') !== '127.0.0.1' || fields.get('port') !== '5433'
    || fields.get('database') !== 'postgres' || fields.get('username') !== 'payplanner_ci'
    || fields.get('pooling')?.toLowerCase() !== 'false' || !/^[a-f0-9]{64}$/.test(fields.get('password') ?? '')) {
    throw new Error('Expected isolated payplanner_ci role on existing loopback PostgreSQL 17');
  }
  return { password: fields.get('password') };
}

export function verifyPostgres(env = process.env) {
  const { password } = postgresSettings(env.PAYPLANNER_TEST_POSTGRES);
  for (const name of ['psql.exe', 'pg_dump.exe']) {
    if (!existsSync(`${postgresBin}/${name}`)) throw new Error('PostgreSQL 17 client is missing');
  }
  const query = "SELECT current_user, current_database(), current_setting('server_version_num')::int / 10000, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls FROM pg_roles WHERE rolname = current_user";
  const run = database => spawnSync(`${postgresBin}/psql.exe`, [
    '-X', '-w', '-h', '127.0.0.1', '-p', '5433', '-U', 'payplanner_ci', '-d', database,
    '-A', '-t', '-q', '-v', 'ON_ERROR_STOP=1', '-c', query,
  ], { env: { ...env, PGPASSWORD: password, PGCONNECT_TIMEOUT: '5' }, encoding: 'utf8', timeout: 15000, windowsHide: true });
  const result = run('postgres');
  if (result.status !== 0 || result.stdout.trim() !== 'payplanner_ci|postgres|17|f|t|f|f|f') {
    throw new Error('Dedicated PostgreSQL role or server preflight failed');
  }
  // This probe must fail before any query executes in the restored working database.
  const denied = run('payplanner');
  // PostgreSQL's rejection text follows the server locale; the HBA file name is stable.
  if (denied.status !== 2 || !/pg_hba\.conf/.test(denied.stderr ?? '')) {
    throw new Error('Test role must be explicitly denied access to the working database');
  }
}

export function verifyPortablePackage(root = 'out/be') {
  const deps = JSON.parse(readFileSync(resolve(root, 'PayPlanner.Api.deps.json'), 'utf8'));
  if (deps.runtimeTarget?.name !== '.NETCoreApp,Version=v9.0'
    || !existsSync(resolve(root, 'PayPlanner.Api.dll'))
    || !existsSync(resolve(root, 'PayPlanner.Api.Migrations.dll'))
    || !existsSync(resolve(root, 'runtimes/linux-x64/native/libe_sqlite3.so'))) {
    throw new Error('Expected portable .NET 9 backend with migrations and Linux SQLite runtime');
  }
  // systemd starts the verified portable DLL with dotnet on the existing Linux VPS.
  rmSync(resolve(root, 'PayPlanner.Api.exe'), { force: true });
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    requireTrustedHost();
    const mode = process.argv[2];
    if (mode === 'postgres') verifyPostgres();
    else if (mode === 'package') verifyPortablePackage();
    else if (!['build', 'hook'].includes(mode)) throw new Error('Unknown runner preflight mode');
    console.log(`XSENUS ${mode} preflight passed`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
