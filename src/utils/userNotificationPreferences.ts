export const USER_NOTIFICATION_TYPE_PREFERENCES_SETTING_KEY = 'notifications.typePreferences.v1';

export type UserNotificationTypePreferences = {
  disabledTypes: string[];
};

export const DEFAULT_USER_NOTIFICATION_TYPE_PREFERENCES: UserNotificationTypePreferences = {
  disabledTypes: [],
};

function normalizeDisabledTypes(values: unknown, knownTypes?: string[]) {
  if (!Array.isArray(values)) {
    return [];
  }

  const allowed = knownTypes ? new Set(knownTypes) : null;
  const normalized = values
    .filter((value): value is string => typeof value === 'string' && value.trim().length > 0)
    .map((value) => value.trim())
    .filter((value) => !allowed || allowed.has(value));

  return Array.from(new Set(normalized));
}

export function normalizeUserNotificationTypePreferences(
  value: Partial<UserNotificationTypePreferences> | null | undefined,
  knownTypes?: string[],
): UserNotificationTypePreferences {
  return {
    disabledTypes: normalizeDisabledTypes(value?.disabledTypes, knownTypes),
  };
}

export function parseUserNotificationTypePreferences(
  rawValue?: string | null,
  knownTypes?: string[],
): UserNotificationTypePreferences {
  if (!rawValue) {
    return DEFAULT_USER_NOTIFICATION_TYPE_PREFERENCES;
  }

  try {
    const parsed = JSON.parse(rawValue) as Partial<UserNotificationTypePreferences>;
    return normalizeUserNotificationTypePreferences(parsed, knownTypes);
  } catch {
    return DEFAULT_USER_NOTIFICATION_TYPE_PREFERENCES;
  }
}

export function serializeUserNotificationTypePreferences(
  value: Partial<UserNotificationTypePreferences>,
  knownTypes?: string[],
) {
  return JSON.stringify(normalizeUserNotificationTypePreferences(value, knownTypes));
}

export function isUserNotificationTypeEnabled(
  preferences: UserNotificationTypePreferences,
  type: string,
) {
  return !preferences.disabledTypes.includes(type);
}

export function toggleUserNotificationTypePreference(
  preferences: UserNotificationTypePreferences,
  type: string,
  isEnabled: boolean,
): UserNotificationTypePreferences {
  const disabledTypes = new Set(preferences.disabledTypes);
  if (isEnabled) {
    disabledTypes.delete(type);
  } else {
    disabledTypes.add(type);
  }

  return {
    disabledTypes: Array.from(disabledTypes),
  };
}
