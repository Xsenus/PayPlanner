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
  onOpenTask?: (taskId: number) => void;
  onOpenCase?: (caseId: number) => void;
  onOpenPayment?: (paymentId: number) => void;
  onOpenInvoice?: (invoiceId: number) => void;
  onOpenAct?: (actId: number) => void;
  onOpenContract?: (contractId: number) => void;
  onOpenNotifications?: () => void;
};

function buildToastDescription(notification: RealtimeUserNotification): string {
  const value = notification.bodyPreview?.trim() || notification.title.trim();
  return value.length > 220 ? `${value.slice(0, 217)}...` : value;
}

const REALTIME_TOAST_TYPES = new Set([
  'taskAssigned',
  'taskReminder',
  'taskChanged',
  'taskComment',
  'taskCompleted',
  'taskScheduleConflict',
  'taskApprovalRequested',
  'taskApproved',
  'taskRevisionRequested',
  'caseInactivityControl',
  'caseAccessRequested',
  'caseAccessResolved',
  'caseCourtEvent',
  'courtHearing',
  'courtStatusChanged',
  'courtDecisionPublished',
  'courtSyncFailed',
  'caseProceduralDeadline',
  'caseDocument',
  'caseInactivityReminder',
  'paymentAdvanceReviewRequired',
  'paymentAdvanceReviewEscalation',
  'systemAdminWarning',
]);

const HIGH_PRIORITY_TYPES = new Set([
  'taskAssigned',
  'taskReminder',
  'taskScheduleConflict',
  'taskApprovalRequested',
  'taskRevisionRequested',
  'caseInactivityControl',
  'courtHearing',
  'courtStatusChanged',
  'courtDecisionPublished',
  'courtSyncFailed',
  'caseProceduralDeadline',
  'caseInactivityReminder',
  'paymentAdvanceReviewRequired',
  'paymentAdvanceReviewEscalation',
  'systemAdminWarning',
]);

function shouldShowRealtimeToast(notification: RealtimeUserNotification): boolean {
  return REALTIME_TOAST_TYPES.has(notification.type);
}

function resolveToastVariant(notification: RealtimeUserNotification): 'info' | 'success' | 'error' {
  if (notification.type === 'taskCompleted' || notification.type === 'taskApproved') {
    return 'success';
  }

  if (
    notification.tone === 'rose' ||
    notification.type === 'courtSyncFailed' ||
    notification.type === 'paymentAdvanceReviewRequired' ||
    notification.type === 'paymentAdvanceReviewEscalation' ||
    notification.type === 'systemAdminWarning'
  ) {
    return 'error';
  }

  return 'info';
}

function resolveToastDuration(notification: RealtimeUserNotification): number {
  return HIGH_PRIORITY_TYPES.has(notification.type) ? 16000 : 12000;
}

type RealtimeToastAction = {
  label: string;
  run: () => void;
};

type RealtimeToastHandlers = Omit<RealtimeNotificationToastsProps, 'userId' | 'onOpenNotifications'> & {
  onOpenNotifications: () => void;
};

function resolveToastAction(
  notification: RealtimeUserNotification,
  handlers: RealtimeToastHandlers,
): RealtimeToastAction {
  if (notification.taskJournalItemId && handlers.onOpenTask) {
    return {
      label: 'Открыть задачу',
      run: () => handlers.onOpenTask?.(notification.taskJournalItemId!),
    };
  }

  if (notification.objectId) {
    switch (notification.objectType) {
      case 'case':
      case 'client-case':
        if (handlers.onOpenCase) {
          return {
            label: 'Открыть дело',
            run: () => handlers.onOpenCase?.(notification.objectId!),
          };
        }
        break;
      case 'payment':
        if (handlers.onOpenPayment) {
          return {
            label: 'Открыть платеж',
            run: () => handlers.onOpenPayment?.(notification.objectId!),
          };
        }
        break;
      case 'invoice':
      case 'account':
        if (handlers.onOpenInvoice) {
          return {
            label: 'Открыть счет',
            run: () => handlers.onOpenInvoice?.(notification.objectId!),
          };
        }
        break;
      case 'act':
        if (handlers.onOpenAct) {
          return {
            label: 'Открыть акт',
            run: () => handlers.onOpenAct?.(notification.objectId!),
          };
        }
        break;
      case 'contract':
        if (handlers.onOpenContract) {
          return {
            label: 'Открыть договор',
            run: () => handlers.onOpenContract?.(notification.objectId!),
          };
        }
        break;
      default:
        break;
    }
  }

  return {
    label: 'Открыть уведомления',
    run: handlers.onOpenNotifications,
  };
}

export function RealtimeNotificationToasts({
  userId,
  onOpenTask,
  onOpenCase,
  onOpenPayment,
  onOpenInvoice,
  onOpenAct,
  onOpenContract,
  onOpenNotifications,
}: RealtimeNotificationToastsProps) {
  const toast = useToast();
  const connectionRef = useRef<HubConnection | null>(null);
  const shownNotificationIdsRef = useRef<Set<number>>(new Set());
  const handlersRef = useRef({
    onOpenTask,
    onOpenCase,
    onOpenPayment,
    onOpenInvoice,
    onOpenAct,
    onOpenContract,
    onOpenNotifications: onOpenNotifications ?? (() => undefined),
  });

  useEffect(() => {
    handlersRef.current = {
      onOpenTask,
      onOpenCase,
      onOpenPayment,
      onOpenInvoice,
      onOpenAct,
      onOpenContract,
      onOpenNotifications: onOpenNotifications ?? (() => undefined),
    };
  }, [onOpenAct, onOpenCase, onOpenContract, onOpenInvoice, onOpenNotifications, onOpenPayment, onOpenTask]);

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

        emitUserNotificationsChanged({
          source: 'realtime',
          reason: 'created',
          unreadDelta: 1,
          notificationId: notification.id,
        });

        if (shownNotificationIdsRef.current.has(notification.id)) {
          return;
        }
        shownNotificationIdsRef.current.add(notification.id);

        if (!shouldShowRealtimeToast(notification)) {
          return;
        }

        const action = resolveToastAction(notification, handlersRef.current);
        toast.showToast({
          title: notification.title || 'Уведомление',
          description: buildToastDescription(notification),
          variant: resolveToastVariant(notification),
          durationMs: resolveToastDuration(notification),
          actionLabel: action.label,
          onAction: action.run,
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

    connection.onreconnected(() => {
      emitUserNotificationsChanged({ source: 'realtime', reason: 'reconnected' });
    });

    connection.onclose((error) => {
      if (disposed) {
        return;
      }

      if (error && !isExpectedRealtimeStartupError(error)) {
        console.error('Realtime notifications connection closed.', error);
      }

      retryTimer = window.setTimeout(start, 5000);
    });

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
