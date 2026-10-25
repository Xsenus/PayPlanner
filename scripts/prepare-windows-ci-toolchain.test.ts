// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import { prepareWindowsCiToolchain, selectWindowsPowerShell } from './prepare-windows-ci-toolchain.mjs';

const env = {
  PATH: 'C:\\Users\\runner\\AppData\\Local\\Microsoft\\WindowsApps;D:\\PowerShell;C:\\Windows\\System32',
  ProgramFiles: 'C:\\Program Files', SystemRoot: 'C:\\Windows',
};

describe('Windows CI toolchain preparation', () => {
  it('prefers a working PowerShell installation and excludes Store aliases from the job PATH', () => {
    const execute = vi.fn(() => ({ status: 0, stdout: '7.5.3\r\n' }));
    const result = selectWindowsPowerShell(env, () => true, execute);
    expect(result.executable).toBe('C:\\Program Files\\PowerShell\\7\\pwsh.exe');
    expect(result.path).not.toMatch(/WindowsApps/i);
    expect(result.path.startsWith('C:\\Program Files\\PowerShell\\7;')).toBe(true);
    expect(execute).toHaveBeenCalledTimes(1);
  });

  it('tests real executability and selects the next valid installation after EACCES', () => {
    const execute = vi.fn((file: string) => file.startsWith('D:')
      ? { status: 0, stdout: '7.4.6' } : { status: null, stdout: '', error: new Error('EACCES') });
    const result = selectWindowsPowerShell(env, () => true, execute);
    expect(result.executable).toBe('D:\\PowerShell\\pwsh.exe');
    expect(result.path).not.toMatch(/WindowsApps/i);
  });

  it('uses installed Windows PowerShell when pwsh is unavailable, as setup-dotnet supports', () => {
    const result = selectWindowsPowerShell(env,
      (file: string) => file.endsWith('powershell.exe'),
      () => ({ status: 0, stdout: '5.1.19041.6328' }));
    expect(result.executable).toBe('C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe');
    expect(result.path).not.toMatch(/WindowsApps/i);
  });

  it('rejects failed or misleading version probes instead of continuing to a long build', () => {
    for (const stdout of ['', '3.0.0', '7.5.3\nerror']) {
      expect(() => selectWindowsPowerShell(env, () => true, () => ({ status: 0, stdout }))).toThrow(/No runnable PowerShell/);
    }
    expect(() => selectWindowsPowerShell(env, () => false)).toThrow(/No runnable PowerShell/);
  });

  it('rejects untrusted machines before writing any environment or SDK directory', () => {
    expect(() => prepareWindowsCiToolchain({}, 'win32')).toThrow(/trusted PayPlanner/);
  });

  it('rejects a system-wide installation directory before writing any files', () => {
    expect(() => prepareWindowsCiToolchain({
      COMPUTERNAME: 'XSENUS', GITHUB_REPOSITORY: 'Xsenus/PayPlanner',
      GITHUB_REF: 'refs/heads/main', GITHUB_EVENT_NAME: 'push',
      GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/main',
      RUNNER_TOOL_CACHE: 'D:/runner/toolcache', DOTNET_INSTALL_DIR: 'C:/Program Files/dotnet',
      GITHUB_ENV: 'D:/runner/env', GITHUB_PATH: 'D:/runner/path',
    }, 'win32')).toThrow(/dedicated runner tool cache/);
  });
});
