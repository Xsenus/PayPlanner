import { requireRestoredInstanceId, resolveRestoredApiUrl } from '../utils/restoredRuntimeSafety';

const INSTANCE_HEADER = 'X-PayPlanner-Restored-Instance';
const FAILURE = 'API не подтвердил выбранную восстановленную среду. Рабочий запрос не отправлен.';
export type RestoredRuntimeFetch = typeof fetch & { verify: () => Promise<void> };

/** A dedicated transport; callers must not fall back to the ordinary API on failure. */
export function createRestoredRuntimeFetch(apiUrl: string, instanceId: string, transport: typeof fetch): RestoredRuntimeFetch {
  const base = resolveRestoredApiUrl(apiUrl);
  const origin = new URL(base).origin;
  requireRestoredInstanceId(instanceId);
  let verification: Promise<void> | undefined;

  async function verify(): Promise<void> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 10_000);
    try {
      const response = await transport(`${base}/restored-runtime/identity`, {
        method: 'GET', cache: 'no-store', credentials: 'omit', redirect: 'error',
        signal: controller.signal,
      });
      if (!response.ok) throw new Error();
      const identity: unknown = await response.json();
      if (!identity || typeof identity !== 'object' ||
        !('isolated' in identity) || identity.isolated !== true ||
        !('instanceId' in identity) || identity.instanceId !== instanceId) throw new Error();
    } catch {
      throw new Error(FAILURE);
    } finally {
      clearTimeout(timer);
    }
  }

  const ensureVerified = () => verification ??= verify();
  const request: typeof fetch = async (input, init) => {
    let url: URL;
    try {
      url = new URL(input instanceof Request ? input.url : String(input));
    } catch {
      throw new Error(FAILURE);
    }
    if (url.origin !== origin || url.username || url.password || url.hash) throw new Error(FAILURE);
    if (init?.signal?.aborted || (input instanceof Request && input.signal.aborted)) {
      throw new DOMException('Запрос отменён.', 'AbortError');
    }
    // Share one read-only handshake, including a failed result, across concurrent calls.
    // A retry requires a new transport, never a silent switch to a different API.
    await ensureVerified();
    if (init?.signal?.aborted || (input instanceof Request && input.signal.aborted)) {
      throw new DOMException('Запрос отменён.', 'AbortError');
    }
    const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
    headers.set(INSTANCE_HEADER, instanceId);
    return transport(input, { ...init, headers, redirect: 'error' });
  };
  return Object.assign(request, { verify: ensureVerified });
}
