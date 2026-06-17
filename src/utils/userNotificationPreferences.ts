export const USER_NOTIFICATION_TYPE_PREFERENCES_SETTING_KEY = 'notifications.typePreferences.v1';

export type UserNotificationTypePreferences = {
  disabledTypes: string[];
  disabledChannels: Record<string, UserNotificationPreferenceChannel[]>;
};

export type UserNotificationPreferenceChannel = 'inApp' | 'email' | 'telegram' | 'webPush';

const KNOWN_CHANNELS: UserNotificationPreferenceChannel[] = ['inApp', 'email', 'telegram', 'webPush'];

export const DEFAULT_USER_NOTIFICATION_TYPE_PREFERENCES: UserNotificationTypePreferences = {
  disabledTypes: [],
  disabledChannels: {},
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

function normalizeDisabledChannels(values: unknown, knownTypes?: string[]) {
  if (!values || typeof values !== 'object' || Array.isArray(values)) {
    return {};
  }

  const allowedTypes = knownTypes ? new Set(knownTypes) : null;
  const allowedChannels = new Set<string>(KNOWN_CHANNELS);

  return Object.entries(values).reduce<Record<string, UserNotificationPreferenceChannel[]>>((acc, [type, channels]) => {
    const normalizedType = type.trim();
    if (!normalizedType || (allowedTypes && !allowedTypes.has(normalizedType)) || !Array.isArray(channels)) {
      return acc;
    }

    const normalizedChannels = Array.from(new Set(
      channels
        .filter((value): value is string => typeof value === 'string')
        .map((value) => value.trim())
        .filter((value): value is UserNotificationPreferenceChannel => allowedChannels.has(value)),
    ));

    if (normalizedChannels.length > 0) {
      acc[normalizedType] = normalizedChannels;
    }

    return acc;
  }, {});
}

export function normalizeUserNotificationTypePreferences(
  value: Partial<UserNotificationTypePreferences> | null | undefined,
  knownTypes?: string[],
): UserNotificationTypePreferences {
  return {
    disabledTypes: normalizeDisabledTypes(value?.disabledTypes, knownTypes),
    disabledChannels: normalizeDisabledChannels(value?.disabledChannels, knownTypes),
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

export function isUserNotificationChannelEnabled(
  preferences: UserNotificationTypePreferences,
  type: string,
  channel: UserNotificationPreferenceChannel,
) {
  return isUserNotificationTypeEnabled(preferences, type)
    && !(preferences.disabledChannels[type] ?? []).includes(channel);
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
    ...preferences,
    disabledTypes: Array.from(disabledTypes),
  };
}

export function toggleUserNotificationChannelPreference(
  preferences: UserNotificationTypePreferences,
  type: string,
  channel: UserNotificationPreferenceChannel,
  isEnabled: boolean,
): UserNotificationTypePreferences {
  const disabledChannels = { ...preferences.disabledChannels };
  const channels = new Set(disabledChannels[type] ?? []);
  if (isEnabled) {
    channels.delete(channel);
  } else {
    channels.add(channel);
  }

  if (channels.size > 0) {
    disabledChannels[type] = Array.from(channels);
  } else {
    delete disabledChannels[type];
  }

  return {
    ...preferences,
    disabledChannels,
  };
}
