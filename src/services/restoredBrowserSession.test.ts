import { beforeEach, describe, expect, it, vi } from 'vitest';
import { prepareRestoredBrowserSession, RESTORED_BROWSER_FAILURE, RESTORED_BROWSER_INSTANCE_KEY } from './restoredBrowserSession';

const ID = 'cefe9b7e3bb64c51b3c80a9e96bb57ea';
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); });
const options = () => ({
  enabled: true, instanceId: ID, hostname: '127.0.0.1', local: localStorage, session: sessionStorage,
  hasController: () => false, workerRegistrations: async (): Promise<unknown[]> => [],
  cacheNames: async (): Promise<string[]> => [], databases: async (): Promise<unknown[]> => [],
});

describe('restored browser session', () => {
  it('marks an empty session and permits reopening the same copy with its own data', async () => {
    await prepareRestoredBrowserSession(options());
    expect(localStorage.getItem(RESTORED_BROWSER_INSTANCE_KEY)).toBe(ID);
    expect(sessionStorage.getItem(RESTORED_BROWSER_INSTANCE_KEY)).toBe(ID);
    localStorage.setItem('pp_token', 'fixture-probe-token');
    await expect(prepareRestoredBrowserSession({ ...options(), databases: async () => [{}] })).resolves.toBeUndefined();
    expect(localStorage.getItem('pp_token')).toBe('fixture-probe-token');
  });

  it.each(['local', 'session'])('preserves existing unmarked %s data and refuses startup', async target => {
    const storage = target === 'local' ? localStorage : sessionStorage;
    storage.setItem('other-app', 'fixture-secret');
    await expect(prepareRestoredBrowserSession(options())).rejects.toThrow(RESTORED_BROWSER_FAILURE);
    expect(storage.getItem('other-app')).toBe('fixture-secret');
    expect(localStorage.getItem(RESTORED_BROWSER_INSTANCE_KEY)).toBeNull();
    expect(sessionStorage.getItem(RESTORED_BROWSER_INSTANCE_KEY)).toBeNull();
  });

  it('refuses another copy without replacing its marker or token', async () => {
    localStorage.setItem(RESTORED_BROWSER_INSTANCE_KEY, 'another-instance');
    localStorage.setItem('pp_token', 'fixture-secret');
    await expect(prepareRestoredBrowserSession(options())).rejects.toThrow();
    expect(localStorage.getItem(RESTORED_BROWSER_INSTANCE_KEY)).toBe('another-instance');
    expect(localStorage.getItem('pp_token')).toBe('fixture-secret');
  });

  it.each(['controller', 'registration', 'cache', 'database'])('rejects an existing %s without clearing it', async kind => {
    const settings = options();
    if (kind === 'controller') settings.hasController = () => true;
    if (kind === 'registration') settings.workerRegistrations = async () => [{}];
    if (kind === 'cache') settings.cacheNames = async () => ['other-app'];
    if (kind === 'database') settings.databases = async () => [{}];
    await expect(prepareRestoredBrowserSession(settings)).rejects.toThrow();
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('rechecks the controller after asynchronous inspection', async () => {
    const hasController = vi.fn().mockReturnValueOnce(false).mockReturnValueOnce(true);
    await expect(prepareRestoredBrowserSession({ ...options(), hasController })).rejects.toThrow();
    expect(localStorage.length).toBe(0);
  });

  it('fails closed if browser inspection is unavailable without echoing its error', async () => {
    await expect(prepareRestoredBrowserSession({ ...options(), databases: async () => { throw new Error('fixture-secret'); } }))
      .rejects.toThrow(RESTORED_BROWSER_FAILURE);
    expect(localStorage.length).toBe(0);
  });

  it('does not inspect or mutate browser storage in ordinary mode', async () => {
    const inspect = vi.fn();
    localStorage.setItem('ordinary', 'fixture');
    await prepareRestoredBrowserSession({ enabled: false, hasController: inspect, workerRegistrations: inspect });
    expect(inspect).not.toHaveBeenCalled();
    expect(localStorage.getItem('ordinary')).toBe('fixture');
  });
});
