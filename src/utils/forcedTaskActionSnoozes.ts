export const FORCED_TASK_SNOOZE_STORAGE_KEY = 'pp.forcedTaskModalSnoozes.v1';
export const FORCED_TASK_SNOOZES_EVENT = 'payplanner:forced-task-snoozes-changed';

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

export function readForcedTaskSnoozes(now = Date.now()): Record<string, number> {
  try {
    const parsed = JSON.parse(window.localStorage.getItem(FORCED_TASK_SNOOZE_STORAGE_KEY) || '{}') as Record<string, unknown>;
    const active: Record<string, number> = {};
    Object.entries(parsed).forEach(([key, value]) => {
      if (typeof value === 'number' && value > now) {
        active[key] = value;
      }
    });
    window.localStorage.setItem(FORCED_TASK_SNOOZE_STORAGE_KEY, JSON.stringify(active));
    return active;
  } catch {
    return {};
  }
}

export function isForcedTaskActionSnoozed(key: string, now = Date.now()): boolean {
  return (readForcedTaskSnoozes(now)[key] ?? 0) > now;
}

export function snoozeForcedTaskActions(keys: Array<string | null | undefined>, minutes: number, now = Date.now()) {
  const snoozes = readForcedTaskSnoozes(now);
  const snoozedUntil = now + minutes * 60_000;
  keys.forEach((key) => {
    if (key) {
      snoozes[key] = snoozedUntil;
    }
  });
  window.localStorage.setItem(FORCED_TASK_SNOOZE_STORAGE_KEY, JSON.stringify(snoozes));
  window.dispatchEvent(new Event(FORCED_TASK_SNOOZES_EVENT));
}

export function shareForcedTaskSnooze(sourceKey: string, targetKey: string | null | undefined, now = Date.now()): boolean {
  if (!targetKey) {
    return false;
  }

  const snoozes = readForcedTaskSnoozes(now);
  const sourceUntil = snoozes[sourceKey] ?? 0;
  if (sourceUntil <= now || (snoozes[targetKey] ?? 0) >= sourceUntil) {
    return false;
  }

  snoozes[targetKey] = sourceUntil;
  window.localStorage.setItem(FORCED_TASK_SNOOZE_STORAGE_KEY, JSON.stringify(snoozes));
  window.dispatchEvent(new Event(FORCED_TASK_SNOOZES_EVENT));
  return true;
}
