import { useEffect, useState } from 'react';

import { apiService } from '../services/api';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_SETTING_KEY } from '../utils/timeZoneOptions';

let pendingAutoSave: Promise<unknown> | null = null;

function isMissingSettingError(error: unknown) {
  return error instanceof Error && error.message.includes('HTTP 404');
}

function autoSaveDetectedTimeZone(timeZoneId: string) {
  if (typeof apiService.upsertUserSetting !== 'function' || pendingAutoSave) {
    return;
  }

  pendingAutoSave = apiService
    .upsertUserSetting(USER_TIME_ZONE_SETTING_KEY, timeZoneId)
    .catch(() => undefined)
    .finally(() => {
      pendingAutoSave = null;
    });
}

export function useUserTimeZone() {
  const [timeZoneId, setTimeZoneId] = useState(() => normalizeUserTimeZoneId());

  useEffect(() => {
    let cancelled = false;
    if (typeof apiService.getUserSetting !== 'function') {
      return undefined;
    }

    apiService.getUserSetting(USER_TIME_ZONE_SETTING_KEY)
      .then((response) => {
        if (!cancelled) {
          setTimeZoneId(normalizeUserTimeZoneId(response?.value));
        }
      })
      .catch((error) => {
        const detectedTimeZoneId = normalizeUserTimeZoneId();
        if (!cancelled) {
          setTimeZoneId(detectedTimeZoneId);
        }

        if (isMissingSettingError(error)) {
          autoSaveDetectedTimeZone(detectedTimeZoneId);
        }
      });

    return () => {
      cancelled = true;
    };
  }, []);

  return timeZoneId;
}
