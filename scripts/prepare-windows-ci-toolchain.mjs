import { appendFileSync, existsSync, mkdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { dirname, join, resolve, win32 } from 'node:path';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { requireTrustedHost } from './verify-xsenus-runner.mjs';

const windowsApps = /(?:^|[\\/])WindowsApps(?:[\\/]|$)/i;

function envValue(env, name) {
  return Object.entries(env).find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1] ?? '';
}

export function selectWindowsPowerShell(env, exists = existsSync, execute = spawnSync) {
  const entries = envValue(env, 'PATH').split(';').filter(value => value && !windowsApps.test(value));
  const programFiles = envValue(env, 'ProgramFiles');
  const systemRoot = envValue(env, 'SystemRoot');
  const candidates = [
    ...(programFiles ? [win32.join(programFiles, 'PowerShell', '7', 'pwsh.exe')] : []),
    ...entries.map(entry => win32.join(entry, 'pwsh.exe')),
    ...(systemRoot ? [win32.join(systemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')] : []),
  ];
  const rejectedDirectories = new Set();
  for (const executable of [...new Set(candidates)]) {
    if (!exists(executable)) continue;
    const result = execute(executable, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.ToString()'], {
      encoding: 'utf8', timeout: 10000, windowsHide: true,
    });
    const minimumMajor = win32.basename(executable).toLowerCase() === 'pwsh.exe' ? 7 : 5;
    const version = (result.stdout ?? '').trim();
    if (result.status === 0 && /^\d+\.\d+\.\d+(?:\.\d+)?$/.test(version) && Number(version.split('.')[0]) >= minimumMajor) {
      const directory = win32.dirname(executable);
      return {
        executable, version,
        path: [directory, ...entries.filter(entry => entry.toLowerCase() !== directory.toLowerCase()
          && !rejectedDirectories.has(win32.normalize(entry).toLowerCase()))].join(';'),
      };
    }
    rejectedDirectories.add(win32.dirname(executable).toLowerCase());
  }
  throw new Error('No runnable PowerShell installation was found; WindowsApps execution aliases are not supported');
}

export function prepareWindowsCiToolchain(env = process.env, platform = process.platform) {
  requireTrustedHost(env, platform);
  for (const name of ['RUNNER_TOOL_CACHE', 'DOTNET_INSTALL_DIR', 'GITHUB_ENV', 'GITHUB_PATH']) {
    if (!env[name] || /[\r\n]/.test(env[name])) throw new Error(`Missing or invalid ${name}`);
  }
  const expectedDirectory = resolve(env.RUNNER_TOOL_CACHE, 'payplanner-dotnet');
  if (resolve(env.DOTNET_INSTALL_DIR).toLowerCase() !== expectedDirectory.toLowerCase()) {
    throw new Error('DOTNET_INSTALL_DIR must use the dedicated runner tool cache, not Program Files');
  }
  const powershell = selectWindowsPowerShell(env);
  mkdirSync(expectedDirectory, { recursive: true });
  const probe = join(expectedDirectory, `.write-probe-${randomUUID()}`);
  writeFileSync(probe, '', { flag: 'wx' });
  unlinkSync(probe);
  // setup-dotnet prefers pwsh over powershell. Remove broken Store aliases from
  // this job's PATH before the action resolves its installer, without host edits.
  appendFileSync(env.GITHUB_ENV, `DOTNET_INSTALL_DIR=${expectedDirectory}\nPATH=${powershell.path}\n`);
  appendFileSync(env.GITHUB_PATH, `${dirname(powershell.executable)}\n`);
  return { installDirectory: expectedDirectory, powershell: powershell.executable, powershellVersion: powershell.version };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    console.log(JSON.stringify(prepareWindowsCiToolchain()));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
