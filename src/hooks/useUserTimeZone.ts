import { useEffect, useState } from 'react';

import { apiService } from '../services/api';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_SETTING_KEY } from '../utils/timeZoneOptions';

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
      .catch(() => {
        if (!cancelled) {
          setTimeZoneId(normalizeUserTimeZoneId());
        }
      });

    return () => {
      cancelled = true;
    };
  }, []);

  return timeZoneId;
}
