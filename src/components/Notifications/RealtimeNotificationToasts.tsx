import { useEffect, useRef } from 'react';
import { HubConnection, HubConnectionState } from '@microsoft/signalr';
import { useToast } from '../../hooks/useToast';
import { apiService, type UserNotificationDto } from '../../services/api';
import {
  canStartNotificationsHubConnection,
  createNotificationsHubConnection,
  isExpectedRealtimeStartupError,
  REALTIME_USER_NOTIFICATION_CREATED_EVENT,
  stopNotificationsHubConnection,
  type RealtimeUserNotification,
} from '../../services/realtimeNotifications';
import type { ToastDomain } from '../../contexts/toastContextCore';
import { emitUserNotificationsChanged } from '../../utils/userNotifications';

const FALLBACK_POLL_INTERVAL_MS = 30_000;
const FALLBACK_PAGE_SIZE = 15;

type RealtimeNotificationToastsProps = {
  userId: number;
  onOpenTask?: (taskId: number) => void;
  onOpenCase?: (caseId: number) => void;
  onOpenPayment?: (paymentId: number) => void;
  onOpenInvoice?: (invoiceId: number) => void;
  onOpenAct?: (actId: number) => void;
  onOpenContract?: (contractId: number) => void;
  onOpenCalendar?: () => void;
  onOpenNotes?: () => void;
  onOpenNotifications?: () => void;
};

function buildToastDescription(notification: RealtimeUserNotification): string {
  const value = notification.bodyPreview?.trim() || notification.title.trim();
  return value.length > 220 ? `${value.slice(0, 217)}...` : value;
}

function toRealtimeNotification(item: UserNotificationDto, recipientUserId: number): RealtimeUserNotification {
  return {
    id: item.id,
    recipientUserId,
    type: item.type,
    eventKey: item.eventKey,
    title: item.title,
    bodyPreview: item.bodyPreview,
    tone: item.tone,
    objectType: item.objectType,
    objectId: item.objectId,
    taskJournalItemId: item.taskId,
    createdAt: item.createdAt,
  };
}

function buildToastDedupeKey(notification: RealtimeUserNotification): string {
  if (notification.taskJournalItemId) {
    return `task:${notification.taskJournalItemId}`;
  }

  if (notification.objectType && notification.objectId) {
    return `${notification.objectType}:${notification.objectId}`;
  }

  return notification.eventKey || `notification:${notification.id}`;
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
  return REALTIME_TOAST_TYPES.has(notification.type)
    || isCalendarNotification(notification)
    || isNoteNotification(notification);
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

function resolveToastDomain(notification: RealtimeUserNotification): ToastDomain {
  if (
    notification.taskJournalItemId ||
    notification.objectType === 'task' ||
    notification.type.startsWith('task')
  ) {
    return 'task';
  }

  if (
    notification.objectType === 'payment' ||
    notification.objectType === 'invoice' ||
    notification.objectType === 'account' ||
    notification.objectType === 'act' ||
    notification.objectType === 'contract' ||
    notification.type.startsWith('payment')
  ) {
    return 'payment';
  }

  if (
    notification.objectType === 'case' ||
    notification.objectType === 'client-case' ||
    notification.type.startsWith('case') ||
    notification.type.startsWith('court')
  ) {
    return 'case';
  }

  if (isCalendarNotification(notification)) {
    return 'calendar';
  }

  if (isNoteNotification(notification)) {
    return 'note';
  }

  return 'system';
}

function isCalendarNotification(notification: RealtimeUserNotification): boolean {
  return notification.objectType === 'calendar'
    || notification.type.toLowerCase().includes('calendar');
}

function isNoteNotification(notification: RealtimeUserNotification): boolean {
  const normalizedType = notification.type.toLowerCase();
  return notification.objectType === 'note'
    || normalizedType.startsWith('note')
    || normalizedType.startsWith('stickynote');
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

  if (isCalendarNotification(notification) && handlers.onOpenCalendar) {
    return {
      label: 'Открыть календарь',
      run: handlers.onOpenCalendar,
    };
  }

  if (isNoteNotification(notification) && handlers.onOpenNotes) {
    return {
      label: 'Открыть доску',
      run: handlers.onOpenNotes,
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
  onOpenCalendar,
  onOpenNotes,
  onOpenNotifications,
}: RealtimeNotificationToastsProps) {
  const toast = useToast();
  const connectionRef = useRef<HubConnection | null>(null);
  const shownNotificationIdsRef = useRef<Set<number>>(new Set());
  const fallbackKnownNotificationIdsRef = useRef<Set<number>>(new Set());
  const fallbackBaselineReadyRef = useRef(false);
  const fallbackNoticeShownRef = useRef(false);
  const handlersRef = useRef({
    onOpenTask,
    onOpenCase,
    onOpenPayment,
    onOpenInvoice,
    onOpenAct,
    onOpenContract,
    onOpenCalendar,
    onOpenNotes,
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
      onOpenCalendar,
      onOpenNotes,
      onOpenNotifications: onOpenNotifications ?? (() => undefined),
    };
  }, [onOpenAct, onOpenCalendar, onOpenCase, onOpenContract, onOpenInvoice, onOpenNotes, onOpenNotifications, onOpenPayment, onOpenTask]);

  useEffect(() => {
    if (!userId || !canStartNotificationsHubConnection()) {
      return undefined;
    }

    let disposed = false;
    let retryTimer: number | null = null;
    let fallbackTimer: number | null = null;
    let fallbackPollingActive = false;
    let fallbackPollInFlight = false;
    let fallbackActivatedAtMs = 0;
    shownNotificationIdsRef.current.clear();
    fallbackKnownNotificationIdsRef.current.clear();
    fallbackBaselineReadyRef.current = false;
    fallbackNoticeShownRef.current = false;
    const connection = createNotificationsHubConnection();
    connectionRef.current = connection;

    const showIncomingNotification = (
      notification: RealtimeUserNotification,
      reason: 'created' | 'fallback-poll',
    ) => {
      if (!notification || notification.recipientUserId !== userId) {
        return;
      }

      emitUserNotificationsChanged({
        source: 'realtime',
        reason,
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
        dedupeKey: buildToastDedupeKey(notification),
        domain: resolveToastDomain(notification),
      });
    };

    const showFallbackNotice = () => {
      if (fallbackNoticeShownRef.current) {
        return;
      }

      fallbackNoticeShownRef.current = true;
      toast.showToast({
        title: 'Моментальные уведомления недоступны',
        description: 'Новые уведомления временно проверяются автоматически раз в 30 секунд.',
        variant: 'info',
        durationMs: 12000,
        dedupeKey: 'notifications:fallback-polling',
        domain: 'system',
      });
    };

    const pollFallbackNotifications = async () => {
      if (disposed || !fallbackPollingActive || fallbackPollInFlight) {
        return;
      }

      fallbackPollInFlight = true;
      try {
        const page = await apiService.getUserNotifications({
          status: 'unread',
          page: 1,
          pageSize: FALLBACK_PAGE_SIZE,
        });
        if (disposed || !fallbackPollingActive) {
          return;
        }

        const firstPoll = !fallbackBaselineReadyRef.current;
        const activationThresholdMs = fallbackActivatedAtMs - 1000;

        for (const item of page.items) {
          const notificationCreatedAtMs = Date.parse(item.createdAt);
          const isKnown = fallbackKnownNotificationIdsRef.current.has(item.id);
          const isFreshForFallback =
            Number.isFinite(notificationCreatedAtMs) && notificationCreatedAtMs >= activationThresholdMs;

          fallbackKnownNotificationIdsRef.current.add(item.id);

          if (isKnown) {
            continue;
          }

          if (firstPoll && !isFreshForFallback) {
            continue;
          }

          showIncomingNotification(toRealtimeNotification(item, userId), 'fallback-poll');
        }

        fallbackBaselineReadyRef.current = true;
      } catch (error) {
        if (!disposed) {
          console.error('Fallback notifications polling failed.', error);
        }
      } finally {
        fallbackPollInFlight = false;
      }
    };

    const startFallbackPolling = () => {
      if (fallbackTimer !== null || disposed) {
        return;
      }

      fallbackActivatedAtMs = Date.now();
      fallbackPollingActive = true;
      fallbackBaselineReadyRef.current = false;
      fallbackKnownNotificationIdsRef.current.clear();
      showFallbackNotice();
      void pollFallbackNotifications();
      fallbackTimer = window.setInterval(() => {
        void pollFallbackNotifications();
      }, FALLBACK_POLL_INTERVAL_MS);
    };

    const stopFallbackPolling = () => {
      fallbackPollingActive = false;
      if (fallbackTimer !== null) {
        window.clearInterval(fallbackTimer);
        fallbackTimer = null;
      }
      fallbackPollInFlight = false;
    };

    connection.on(REALTIME_USER_NOTIFICATION_CREATED_EVENT, (notification: RealtimeUserNotification) => {
      showIncomingNotification(notification, 'created');
    });

    const start = async () => {
      if (disposed) {
        return;
      }

      try {
        await connection.start();
        stopFallbackPolling();
      } catch (error) {
        if (!disposed) {
          if (isExpectedRealtimeStartupError(error)) {
            console.debug('Realtime notifications are unavailable; retrying in background.', error);
            startFallbackPolling();
          } else {
            console.error('Realtime notifications failed to start.', error);
          }
          retryTimer = window.setTimeout(start, 5000);
        }
      }
    };

    connection.onreconnected(() => {
      stopFallbackPolling();
      emitUserNotificationsChanged({ source: 'realtime', reason: 'reconnected' });
    });

    connection.onclose((error) => {
      if (disposed) {
        return;
      }

      if (error && !isExpectedRealtimeStartupError(error)) {
        console.error('Realtime notifications connection closed.', error);
      }

      startFallbackPolling();
      retryTimer = window.setTimeout(start, 5000);
    });

    void start();

    return () => {
      disposed = true;
      if (retryTimer !== null) {
        window.clearTimeout(retryTimer);
      }
      stopFallbackPolling();
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
