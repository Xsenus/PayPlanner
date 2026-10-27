export const FORCED_TASK_SNOOZE_STORAGE_KEY = 'pp.forcedTaskModalSnoozes.v1';
export const FORCED_TASK_SNOOZES_EVENT = 'payplanner:forced-task-snoozes-changed';

export function getForcedTaskSnoozeStorageKey(userId: number | null | undefined): string | null {
  return typeof userId === 'number' && Number.isSafeInteger(userId) && userId > 0
    ? `pp.forcedTaskModalSnoozes.v2.user.${userId}`
    : null;
}

export const FORCED_TASK_SNOOZE_OPTIONS = [
  { minutes: 10, label: '10 мин' },
  { minutes: 30, label: '30 мин' },
  { minutes: 60, label: '1 час' },
  { minutes: 24 * 60, label: '1 день' },
] as const;

export function getForcedTaskNotificationSnoozeKey(notificationId: number): string {
  return String(notificationId);
}

export function getForcedTaskApprovalSnoozeKey(taskId: number): string {
  return `task-approval:${taskId}`;
}

export function getForcedTaskDeadlineChangeSnoozeKey(taskId: number, requestId: number): string {
  return `task-deadline-change:${taskId}:${requestId}`;
}

function readStoredSnoozes(storageKey: string, now: number): Record<string, number> | null {
  try {
    // The owner of the old v1 value is unknown; never assign its snoozes to a signed-in user.
    const stored = window.localStorage.getItem(storageKey) || '{}';
    const parsed: unknown = JSON.parse(stored);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
    const active: Record<string, number> = {};
    Object.entries(parsed).forEach(([key, value]) => {
      if (typeof value === 'number' && Number.isFinite(value) && value > now) {
        active[key] = value;
      }
    });
    const serialized = JSON.stringify(active);
    if (serialized !== stored) window.localStorage.setItem(storageKey, serialized);
    return active;
  } catch {
    return null;
  }
}

export function readForcedTaskSnoozes(userId: number | null | undefined, now = Date.now()): Record<string, number> {
  const storageKey = getForcedTaskSnoozeStorageKey(userId);
  return storageKey ? (readStoredSnoozes(storageKey, now) ?? {}) : {};
}

export function isForcedTaskActionSnoozed(userId: number | null | undefined, key: string, now = Date.now()): boolean {
  return (readForcedTaskSnoozes(userId, now)[key] ?? 0) > now;
}

export function snoozeForcedTaskActions(userId: number | null | undefined, keys: Array<string | null | undefined>, minutes: number, now = Date.now()): boolean {
  const storageKey = getForcedTaskSnoozeStorageKey(userId);
  if (!storageKey) return false;

  const snoozes = readStoredSnoozes(storageKey, now);
  if (!snoozes) return false;
  const snoozedUntil = now + minutes * 60_000;
  keys.forEach((key) => {
    if (key) {
      snoozes[key] = snoozedUntil;
    }
  });
  try {
    window.localStorage.setItem(storageKey, JSON.stringify(snoozes));
  } catch {
    return false;
  }
  window.dispatchEvent(new CustomEvent(FORCED_TASK_SNOOZES_EVENT, { detail: { userId } }));
  return true;
}

export function shareForcedTaskSnooze(userId: number | null | undefined, sourceKey: string, targetKey: string | null | undefined, now = Date.now()): boolean {
  const storageKey = getForcedTaskSnoozeStorageKey(userId);
  if (!storageKey || !targetKey) {
    return false;
  }

  const snoozes = readStoredSnoozes(storageKey, now);
  if (!snoozes) return false;
  const sourceUntil = snoozes[sourceKey] ?? 0;
  if (sourceUntil <= now || (snoozes[targetKey] ?? 0) >= sourceUntil) {
    return false;
  }

  snoozes[targetKey] = sourceUntil;
  try {
    window.localStorage.setItem(storageKey, JSON.stringify(snoozes));
  } catch {
    return false;
  }
  window.dispatchEvent(new CustomEvent(FORCED_TASK_SNOOZES_EVENT, { detail: { userId } }));
  return true;
}
