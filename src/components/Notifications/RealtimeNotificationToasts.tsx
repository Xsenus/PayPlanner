import { useEffect, useRef } from 'react';
import { HubConnection, HubConnectionState } from '@microsoft/signalr';
import { useToast } from '../../hooks/useToast';
import {
  canStartNotificationsHubConnection,
  createNotificationsHubConnection,
  isExpectedRealtimeStartupError,
  REALTIME_USER_NOTIFICATION_CREATED_EVENT,
  stopNotificationsHubConnection,
  type RealtimeUserNotification,
} from '../../services/realtimeNotifications';
import { emitUserNotificationsChanged } from '../../utils/userNotifications';

type RealtimeNotificationToastsProps = {
  userId: number;
  onOpenTask: (taskId: number) => void;
};

function buildToastDescription(notification: RealtimeUserNotification): string {
  const value = notification.bodyPreview?.trim() || notification.title.trim();
  return value.length > 220 ? `${value.slice(0, 217)}...` : value;
}

export function RealtimeNotificationToasts({ userId, onOpenTask }: RealtimeNotificationToastsProps) {
  const toast = useToast();
  const connectionRef = useRef<HubConnection | null>(null);
  const shownNotificationIdsRef = useRef<Set<number>>(new Set());
  const openTaskRef = useRef(onOpenTask);

  useEffect(() => {
    openTaskRef.current = onOpenTask;
  }, [onOpenTask]);

  useEffect(() => {
    if (!userId || !canStartNotificationsHubConnection()) {
      return undefined;
    }

    let disposed = false;
    let retryTimer: number | null = null;
    shownNotificationIdsRef.current.clear();
    const connection = createNotificationsHubConnection();
    connectionRef.current = connection;

    connection.on(
      REALTIME_USER_NOTIFICATION_CREATED_EVENT,
      (notification: RealtimeUserNotification) => {
        if (!notification || notification.recipientUserId !== userId) {
          return;
        }

        emitUserNotificationsChanged();

        if (shownNotificationIdsRef.current.has(notification.id)) {
          return;
        }
        shownNotificationIdsRef.current.add(notification.id);

        if (notification.type !== 'taskAssigned' || !notification.taskJournalItemId) {
          return;
        }

        toast.showToast({
          title: notification.title || 'Новая задача',
          description: buildToastDescription(notification),
          variant: 'info',
          durationMs: 12000,
          actionLabel: 'Открыть задачу',
          onAction: () => openTaskRef.current(notification.taskJournalItemId!),
        });
      },
    );

    const start = async () => {
      if (disposed) {
        return;
      }

      try {
        await connection.start();
      } catch (error) {
        if (!disposed) {
          if (isExpectedRealtimeStartupError(error)) {
            console.debug('Realtime notifications are unavailable; retrying in background.', error);
          } else {
            console.error('Realtime notifications failed to start.', error);
          }
          retryTimer = window.setTimeout(start, 5000);
        }
      }
    };

    void start();

    return () => {
      disposed = true;
      if (retryTimer !== null) {
        window.clearTimeout(retryTimer);
      }
      connection.off(REALTIME_USER_NOTIFICATION_CREATED_EVENT);
      if (connection.state !== HubConnectionState.Disconnected) {
        void stopNotificationsHubConnection(connection);
      }
      if (connectionRef.current === connection) {
        connectionRef.current = null;
      }
    };
  }, [toast, userId]);

  return null;
}
