import type { IHttpConnectionOptions } from '@microsoft/signalr';
import { API_BASE_URL } from './apiBaseUrl';
import { ensureRestoredApiIdentity } from './apiTransport';
import { parseRestoredRuntimeMode, requireRestoredInstanceId } from '../utils/restoredRuntimeSafety';

export function restoredResourceUrl(url: string): string {
  if (!parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION)) return url;
  const parsed = new URL(url);
  if (parsed.origin !== new URL(API_BASE_URL).origin || parsed.username || parsed.password || parsed.hash) {
    throw new Error('Пробная CRM допускает только ресурсы выбранного локального API.');
  }
  parsed.searchParams.set('pp_restored_instance', requireRestoredInstanceId(import.meta.env.VITE_RESTORED_RUNTIME_INSTANCE_ID));
  return parsed.toString();
}

export function restoredPhotoUrl(value?: string | null): string | undefined {
  if (!parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION)) return value ?? undefined;
  if (!value) return undefined;
  try {
    return restoredResourceUrl(new URL(value, new URL(API_BASE_URL).origin).toString());
  } catch {
    // Do not load an old external avatar from the copied database.
    return undefined;
  }
}

export function restoredRealtimeOptions(getToken: () => string): Partial<IHttpConnectionOptions> {
  if (!parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION)) return {};
  return {
    headers: { 'X-PayPlanner-Restored-Instance': requireRestoredInstanceId(import.meta.env.VITE_RESTORED_RUNTIME_INSTANCE_ID) },
    accessTokenFactory: async () => {
      await ensureRestoredApiIdentity();
      return getToken();
    },
  };
}
