import type { TaskJournalChannel, TaskJournalReminder } from './taskJournalStore';

export const TASK_NOTIFICATION_DEFAULTS_SETTING_KEY = 'tasks.notificationDefaults.v1';

export const TASK_REMINDER_DEFAULT_OPTIONS: Array<{ value: TaskJournalReminder; label: string }> = [
  { value: 'before1d', label: 'За день' },
  { value: 'before1h', label: 'За час' },
  { value: 'before15m', label: 'За 15 минут' },
  { value: 'atDue', label: 'В срок' },
  { value: 'dailyOverdue', label: 'Каждый день просрочки' },
];

export const TASK_CHANNEL_DEFAULT_OPTIONS: Array<{ value: TaskJournalChannel; label: string }> = [
  { value: 'inApp', label: 'В системе' },
  { value: 'email', label: 'Email' },
  { value: 'telegram', label: 'Telegram' },
];

export type TaskNotificationDefaults = {
  reminders: TaskJournalReminder[];
  channels: TaskJournalChannel[];
};

export const DEFAULT_TASK_NOTIFICATION_DEFAULTS: TaskNotificationDefaults = {
  reminders: ['before1h', 'atDue'],
  channels: ['inApp'],
};

export const ALL_TASK_NOTIFICATION_DEFAULTS: TaskNotificationDefaults = {
  reminders: TASK_REMINDER_DEFAULT_OPTIONS.map((option) => option.value),
  channels: TASK_CHANNEL_DEFAULT_OPTIONS.map((option) => option.value),
};

const REMINDER_VALUES = new Set(TASK_REMINDER_DEFAULT_OPTIONS.map((option) => option.value));
const CHANNEL_VALUES = new Set(TASK_CHANNEL_DEFAULT_OPTIONS.map((option) => option.value));

function normalizeList<T extends string>(values: unknown, allowed: Set<T>, fallback: T[]) {
  if (!Array.isArray(values)) {
    return fallback;
  }

  const normalized = values.filter((value): value is T => typeof value === 'string' && allowed.has(value as T));
  return Array.from(new Set(normalized));
}

export function normalizeTaskNotificationDefaults(value: Partial<TaskNotificationDefaults> | null | undefined): TaskNotificationDefaults {
  return {
    reminders: normalizeList(value?.reminders, REMINDER_VALUES, DEFAULT_TASK_NOTIFICATION_DEFAULTS.reminders),
    channels: normalizeList(value?.channels, CHANNEL_VALUES, DEFAULT_TASK_NOTIFICATION_DEFAULTS.channels),
  };
}

export function parseTaskNotificationDefaults(rawValue?: string | null): TaskNotificationDefaults {
  if (!rawValue) {
    return DEFAULT_TASK_NOTIFICATION_DEFAULTS;
  }

  try {
    const parsed = JSON.parse(rawValue) as Partial<TaskNotificationDefaults>;
    return normalizeTaskNotificationDefaults(parsed);
  } catch {
    return DEFAULT_TASK_NOTIFICATION_DEFAULTS;
  }
}

export function serializeTaskNotificationDefaults(value: Partial<TaskNotificationDefaults>) {
  return JSON.stringify(normalizeTaskNotificationDefaults(value));
}
