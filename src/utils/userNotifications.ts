export const USER_NOTIFICATIONS_EVENT = 'payplanner:user-notifications-changed';

export type UserNotificationsChangedSource = 'inbox' | 'realtime' | 'system';

export type UserNotificationsChangedDetail = {
  source?: UserNotificationsChangedSource;
  reason?: string;
  unreadDelta?: number;
  notificationId?: number;
};

export function emitUserNotificationsChanged(detail: UserNotificationsChangedDetail = {}) {
  if (typeof window === 'undefined') {
    return;
  }

  window.dispatchEvent(new CustomEvent<UserNotificationsChangedDetail>(USER_NOTIFICATIONS_EVENT, { detail }));
}
