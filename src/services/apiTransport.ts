import { API_BASE_URL } from './apiBaseUrl';
import { createRestoredRuntimeFetch, type RestoredRuntimeFetch } from './restoredRuntimeFetch';
import { parseRestoredRuntimeMode, requireRestoredInstanceId } from '../utils/restoredRuntimeSafety';

let restoredFetch: RestoredRuntimeFetch | undefined;

function getRestoredFetch(): RestoredRuntimeFetch {
  return restoredFetch ??= createRestoredRuntimeFetch(
    API_BASE_URL,
    requireRestoredInstanceId(import.meta.env.VITE_RESTORED_RUNTIME_INSTANCE_ID),
    (request, options) => globalThis.fetch(request, options),
  );
}

export function ensureRestoredApiIdentity(): Promise<void> {
  return parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION)
    ? getRestoredFetch().verify() : Promise.resolve();
}

/** Shared by authentication, business APIs and attachment/export downloads. */
export const apiFetch: typeof fetch = (input, init) => {
  if (!parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION)) {
    return globalThis.fetch(input, init);
  }
  return getRestoredFetch()(input, init);
};
