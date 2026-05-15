export const USER_NOTIFICATIONS_EVENT = 'payplanner:user-notifications-changed';

export function emitUserNotificationsChanged() {
  if (typeof window === 'undefined') {
    return;
  }

  window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT));
}
