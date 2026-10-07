import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({ render: vi.fn(), prepare: vi.fn(), register: vi.fn() }));
vi.mock('react-dom/client', () => ({ createRoot: () => ({ render: mocks.render }) }));
vi.mock('./App.tsx', () => ({ default: () => null }));
vi.mock('./utils/staleAssetReload', () => ({ installStaleAssetReload: vi.fn() }));
vi.mock('./services/applicationWorker', () => ({ registerPayPlannerApplicationWorker: mocks.register }));
vi.mock('./services/restoredBrowserSession', () => ({
  prepareRestoredBrowserSession: mocks.prepare, RESTORED_BROWSER_FAILURE: 'Отдельный чистый профиль требуется.',
}));

beforeEach(() => {
  vi.resetModules();
  mocks.render.mockReset();
  mocks.prepare.mockReset();
  mocks.register.mockReset().mockResolvedValue(null);
  document.body.innerHTML = '<div id="root"></div>';
});
afterEach(() => { vi.unstubAllEnvs(); document.body.innerHTML = ''; });

describe('application startup', () => {
  it('preserves immediate ordinary production startup and worker registration', async () => {
    vi.stubEnv('PROD', true);
    vi.stubEnv('VITE_RESTORED_RUNTIME_ISOLATION', 'false');
    await import('./main');
    expect(mocks.prepare).not.toHaveBeenCalled();
    expect(mocks.render).toHaveBeenCalledTimes(1);
    expect(mocks.register).toHaveBeenCalledTimes(1);
  });

  it('does not mount restored CRM or register a worker before session verification', async () => {
    vi.stubEnv('PROD', true);
    vi.stubEnv('VITE_RESTORED_RUNTIME_ISOLATION', 'true');
    let finish!: () => void;
    mocks.prepare.mockReturnValue(new Promise<void>(resolve => { finish = resolve; }));
    await import('./main');
    expect(mocks.render).not.toHaveBeenCalled();
    expect(mocks.register).not.toHaveBeenCalled();
    finish();
    await vi.waitFor(() => expect(mocks.render).toHaveBeenCalledTimes(1));
  });

  it('shows a safe explanation instead of mounting CRM when verification fails', async () => {
    vi.stubEnv('PROD', true);
    vi.stubEnv('VITE_RESTORED_RUNTIME_ISOLATION', 'true');
    mocks.prepare.mockRejectedValue(new Error('fixture-secret'));
    await import('./main');
    await vi.waitFor(() => expect(document.getElementById('root')?.getAttribute('role')).toBe('alert'));
    expect(document.getElementById('root')?.textContent).toBe('Отдельный чистый профиль требуется.');
    expect(mocks.render).not.toHaveBeenCalled();
    expect(mocks.register).not.toHaveBeenCalled();
  });
});
