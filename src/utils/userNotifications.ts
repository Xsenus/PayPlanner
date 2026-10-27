export const USER_NOTIFICATIONS_EVENT = 'payplanner:user-notifications-changed';

export type UserNotificationsChangedSource = 'inbox' | 'realtime' | 'system';

export type UserNotificationsChangedDetail = {
  source?: UserNotificationsChangedSource;
  reason?: string;
  unreadDelta?: number;
  notificationId?: number;
  notificationType?: string;
  objectType?: string;
  objectId?: number | null;
  taskJournalItemId?: number | null;
  clientCaseId?: number | null;
};

const TASK_JOURNAL_REALTIME_REASONS = new Set([
  'created',
  'fallback-poll',
  'missed-replay',
  'visibility-resume',
]);

export function isTaskJournalRealtimeChange(detail?: UserNotificationsChangedDetail | null): boolean {
  if (detail?.source !== 'realtime') {
    return false;
  }

  if (detail.reason === 'reconnected' || detail.reason === 'user-role-changed') {
    return true;
  }

  if (!detail.reason || !TASK_JOURNAL_REALTIME_REASONS.has(detail.reason)) {
    return false;
  }

  const objectType = detail.objectType?.trim().toLowerCase();
  return Boolean(
    detail.taskJournalItemId
    || detail.clientCaseId
    || detail.notificationType?.startsWith('task')
    || objectType === 'task'
    || objectType === 'case'
    || objectType === 'client-case',
  );
}

export function emitUserNotificationsChanged(detail: UserNotificationsChangedDetail = {}) {
  if (typeof window === 'undefined') {
    return;
  }

  window.dispatchEvent(new CustomEvent<UserNotificationsChangedDetail>(USER_NOTIFICATIONS_EVENT, { detail }));
}
