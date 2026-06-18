export const USER_NOTIFICATION_DO_NOT_DISTURB_SETTING_KEY = 'notifications.doNotDisturb.v1';

export type UserNotificationDoNotDisturbSettings = {
  enabled: boolean;
  workStart: string;
  workEnd: string;
  muteWeekends: boolean;
  vacationUntil: string | null;
  allowCritical: boolean;
  workDays: number[];
};

export const DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB: UserNotificationDoNotDisturbSettings = {
  enabled: false,
  workStart: '09:00',
  workEnd: '18:00',
  muteWeekends: true,
  vacationUntil: null,
  allowCritical: true,
  workDays: [1, 2, 3, 4, 5],
};

const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d$/;
const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

function normalizeTime(value: unknown, fallback: string) {
  return typeof value === 'string' && TIME_RE.test(value.trim()) ? value.trim() : fallback;
}

function normalizeDate(value: unknown) {
  return typeof value === 'string' && DATE_RE.test(value.trim()) ? value.trim() : null;
}

function normalizeBool(value: unknown, fallback: boolean) {
  return typeof value === 'boolean' ? value : fallback;
}

function normalizeWorkDays(value: unknown) {
  if (!Array.isArray(value)) {
    return DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB.workDays;
  }

  const days = Array.from(new Set(
    value
      .filter((item): item is number => Number.isInteger(item) && item >= 1 && item <= 7)
      .sort((left, right) => left - right),
  ));

  return days.length > 0 ? days : DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB.workDays;
}

export function normalizeUserNotificationDoNotDisturb(
  value: Partial<UserNotificationDoNotDisturbSettings> | null | undefined,
): UserNotificationDoNotDisturbSettings {
  return {
    enabled: normalizeBool(value?.enabled, DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB.enabled),
    workStart: normalizeTime(value?.workStart, DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB.workStart),
    workEnd: normalizeTime(value?.workEnd, DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB.workEnd),
    muteWeekends: normalizeBool(value?.muteWeekends, DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB.muteWeekends),
    vacationUntil: normalizeDate(value?.vacationUntil),
    allowCritical: normalizeBool(value?.allowCritical, DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB.allowCritical),
    workDays: normalizeWorkDays(value?.workDays),
  };
}

export function parseUserNotificationDoNotDisturb(rawValue?: string | null): UserNotificationDoNotDisturbSettings {
  if (!rawValue) {
    return DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB;
  }

  try {
    return normalizeUserNotificationDoNotDisturb(JSON.parse(rawValue) as Partial<UserNotificationDoNotDisturbSettings>);
  } catch {
    return DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB;
  }
}

export function serializeUserNotificationDoNotDisturb(value: Partial<UserNotificationDoNotDisturbSettings>) {
  return JSON.stringify(normalizeUserNotificationDoNotDisturb(value));
}
