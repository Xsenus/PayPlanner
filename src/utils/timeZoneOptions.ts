export const USER_TIME_ZONE_SETTING_KEY = 'profile.timeZoneId';

export const DEFAULT_TIME_ZONE_ID = 'Europe/Moscow';

export const USER_TIME_ZONE_OPTIONS = [
  { value: 'Europe/Kaliningrad', label: 'Калининград (UTC+2)' },
  { value: 'Europe/Moscow', label: 'Москва (UTC+3)' },
  { value: 'Europe/Samara', label: 'Самара (UTC+4)' },
  { value: 'Asia/Yekaterinburg', label: 'Екатеринбург (UTC+5)' },
  { value: 'Asia/Omsk', label: 'Омск (UTC+6)' },
  { value: 'Asia/Novosibirsk', label: 'Новосибирск (UTC+7)' },
  { value: 'Asia/Krasnoyarsk', label: 'Красноярск (UTC+7)' },
  { value: 'Asia/Irkutsk', label: 'Иркутск (UTC+8)' },
  { value: 'Asia/Yakutsk', label: 'Якутск (UTC+9)' },
  { value: 'Asia/Vladivostok', label: 'Владивосток (UTC+10)' },
  { value: 'Asia/Magadan', label: 'Магадан (UTC+11)' },
  { value: 'Asia/Kamchatka', label: 'Камчатка (UTC+12)' },
  { value: 'UTC', label: 'UTC' },
];

export function getBrowserTimeZoneId() {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || DEFAULT_TIME_ZONE_ID;
}

export function normalizeUserTimeZoneId(value?: string | null) {
  const normalized = (value ?? '').trim();
  if (!normalized) return getBrowserTimeZoneId();
  return USER_TIME_ZONE_OPTIONS.some((option) => option.value === normalized)
    ? normalized
    : getBrowserTimeZoneId();
}
