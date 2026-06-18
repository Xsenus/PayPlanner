export type RealtimeNotificationConnectionStatus =
  | 'idle'
  | 'connecting'
  | 'connected'
  | 'fallback'
  | 'reconnecting'
  | 'disconnected'
  | 'unavailable';

export type RealtimeNotificationDiagnostics = {
  status: RealtimeNotificationConnectionStatus;
  connectionId?: string | null;
  fallbackPollingActive: boolean;
  lastConnectedAt?: string | null;
  lastDisconnectedAt?: string | null;
  lastError?: string | null;
  updatedAt: string;
};

export const REALTIME_NOTIFICATION_DIAGNOSTICS_EVENT = 'payplanner:realtime-notification-diagnostics';

const DEFAULT_DIAGNOSTICS: RealtimeNotificationDiagnostics = {
  status: 'idle',
  connectionId: null,
  fallbackPollingActive: false,
  lastConnectedAt: null,
  lastDisconnectedAt: null,
  lastError: null,
  updatedAt: new Date(0).toISOString(),
};

let currentDiagnostics = DEFAULT_DIAGNOSTICS;

function normalizeError(error: unknown) {
  if (!error) {
    return null;
  }

  const message = error instanceof Error ? error.message : String(error);
  return message.trim() || null;
}

export function getRealtimeNotificationDiagnostics() {
  return currentDiagnostics;
}

export function updateRealtimeNotificationDiagnostics(
  patch: Partial<Omit<RealtimeNotificationDiagnostics, 'updatedAt'>> & { error?: unknown },
) {
  const now = new Date().toISOString();
  const { error, ...diagnosticsPatch } = patch;
  const hasLastError = Object.prototype.hasOwnProperty.call(patch, 'lastError');
  const hasError = Object.prototype.hasOwnProperty.call(patch, 'error');
  const next: RealtimeNotificationDiagnostics = {
    ...currentDiagnostics,
    ...diagnosticsPatch,
    lastError: hasError
      ? normalizeError(error)
      : hasLastError
        ? patch.lastError ?? null
        : currentDiagnostics.lastError,
    updatedAt: now,
  };

  currentDiagnostics = next;

  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent(REALTIME_NOTIFICATION_DIAGNOSTICS_EVENT, { detail: currentDiagnostics }));
  }

  return currentDiagnostics;
}

export function resetRealtimeNotificationDiagnostics() {
  currentDiagnostics = DEFAULT_DIAGNOSTICS;
}
