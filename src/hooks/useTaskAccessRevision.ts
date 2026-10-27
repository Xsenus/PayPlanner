import { useEffect, useState } from 'react';
import { USER_NOTIFICATIONS_EVENT, type UserNotificationsChangedDetail } from '../utils/userNotifications';

/** Invalidates visible task data when the server signals an access change or reconnect. */
export function useTaskAccessRevision() {
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const refresh = (event: Event) => {
      const detail = event instanceof CustomEvent ? event.detail as UserNotificationsChangedDetail | null : null;
      if (detail?.source === 'realtime' && (detail.reason === 'user-role-changed' || detail.reason === 'reconnected')) {
        setRevision(value => value + 1);
      }
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, refresh);
    return () => window.removeEventListener(USER_NOTIFICATIONS_EVENT, refresh);
  }, []);
  return revision;
}
