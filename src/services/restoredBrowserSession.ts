import { assertRestoredBrowserHost, parseRestoredRuntimeMode, requireRestoredInstanceId } from '../utils/restoredRuntimeSafety';

export const RESTORED_BROWSER_INSTANCE_KEY = 'pp_restored_browser_instance';
export const RESTORED_BROWSER_FAILURE = 'Пробную CRM нужно открыть в отдельном чистом профиле браузера на локальном IP. Найдены прежние данные или фоновый обработчик. Ничего не удалено.';

type BrowserSessionOptions = {
  enabled?: boolean;
  instanceId?: string;
  hostname?: string;
  local?: Storage;
  session?: Storage;
  hasController?: () => boolean;
  workerRegistrations?: () => Promise<unknown[]>;
  cacheNames?: () => Promise<string[]>;
  databases?: () => Promise<unknown[]>;
};

/** No cleanup: a reused origin/profile must not destroy another application's data. */
export async function prepareRestoredBrowserSession(options: BrowserSessionOptions = {}): Promise<void> {
  const enabled = options.enabled ?? parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION);
  if (!enabled) return;
  try {
    const instanceId = requireRestoredInstanceId(options.instanceId ?? import.meta.env.VITE_RESTORED_RUNTIME_INSTANCE_ID);
    assertRestoredBrowserHost((options.hostname ?? window.location.hostname).replace(/^\[|\]$/g, ''));
    const local = options.local ?? localStorage;
    const session = options.session ?? sessionStorage;
    const hasController = options.hasController ?? (() => Boolean(navigator.serviceWorker?.controller));
    const workerRegistrations = options.workerRegistrations ?? (() => navigator.serviceWorker?.getRegistrations() ?? Promise.resolve([]));
    const cacheNames = options.cacheNames ?? (() => typeof caches === 'undefined' ? Promise.resolve([]) : caches.keys());
    const databases = options.databases ?? (() => {
      if (typeof indexedDB === 'undefined') return Promise.resolve([]);
      if (typeof indexedDB.databases !== 'function') throw new Error();
      return indexedDB.databases();
    });
    if (hasController()) throw new Error();
    const [workers, names, dbs] = await Promise.all([workerRegistrations(), cacheNames(), databases()]);
    if (hasController() || workers.length || names.length) throw new Error();
    // Repeat storage reads after asynchronous inspection: no marker is written before all checks pass.
    const localId = local.getItem(RESTORED_BROWSER_INSTANCE_KEY);
    const sessionId = session.getItem(RESTORED_BROWSER_INSTANCE_KEY);
    if ((localId !== null && localId !== instanceId) || (sessionId !== null && sessionId !== instanceId)
      || (local.length > 0 && localId !== instanceId) || (session.length > 0 && sessionId !== instanceId)
      || (dbs.length > 0 && localId !== instanceId)) throw new Error();
    local.setItem(RESTORED_BROWSER_INSTANCE_KEY, instanceId);
    session.setItem(RESTORED_BROWSER_INSTANCE_KEY, instanceId);
  } catch {
    throw new Error(RESTORED_BROWSER_FAILURE);
  }
}
