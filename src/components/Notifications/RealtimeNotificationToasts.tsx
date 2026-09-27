import { useEffect, useRef } from 'react';
import { HubConnection, HubConnectionState } from '@microsoft/signalr';
import { useToast } from '../../hooks/useToast';
import { apiService, type UserNotificationDto } from '../../services/api';
import {
  canStartNotificationsHubConnection,
  createNotificationsHubConnection,
  isExpectedRealtimeStartupError,
  REALTIME_TASK_JOURNAL_CHANGED_EVENT,
  REALTIME_USER_NOTIFICATION_CREATED_EVENT,
  stopNotificationsHubConnection,
  TASK_JOURNAL_REALTIME_EVENT,
  type RealtimeUserNotification,
} from '../../services/realtimeNotifications';
import type { ToastDomain } from '../../contexts/toastContextCore';
import { emitUserNotificationsChanged } from '../../utils/userNotifications';
import { updateRealtimeNotificationDiagnostics } from '../../utils/realtimeNotificationDiagnostics';
import {
  getClientCaseDocumentIdFromNotification,
  getClientCaseIdFromNotification,
  parseUserNotificationMetadata,
} from '../../utils/userNotificationMetadata';

const FALLBACK_POLL_INTERVAL_MS = 30_000;
const FALLBACK_PAGE_SIZE = 15;
const FALLBACK_MAX_PAGES = 7;
const MISSED_REPLAY_PAGE_SIZE = 50;
const MISSED_REPLAY_MAX_ITEMS = 25;
const MISSED_REPLAY_SPACING_MS = 2500;
const MISSED_REPLAY_LOOKBACK_MS = 48 * 60 * 60 * 1000;
const TOAST_SNOOZE_MS = 30 * 60 * 1000;
const TOAST_SNOOZE_STORAGE_KEY = 'payplanner.realtimeToastSnoozes.v1';
const LAST_SEEN_STORAGE_KEY_PREFIX = 'payplanner.realtimeToastLastSeenAt.v1';

type RealtimeNotificationToastsProps = {
  userId: number;
  onOpenTask?: (taskId: number) => void;
  onOpenCase?: (caseId: number) => void;
  onOpenCaseDocument?: (caseId: number, documentId?: number) => void;
  onOpenPayment?: (paymentId: number, type?: 'Income' | 'Expense') => void;
  onOpenInvoice?: (invoiceId: number) => void;
  onOpenAct?: (actId: number) => void;
  onOpenContract?: (contractId: number) => void;
  onOpenCalendar?: () => void;
  onOpenNotes?: () => void;
  onOpenNotifications?: () => void;
};

function buildToastDescription(notification: RealtimeUserNotification): string {
  const value = notification.bodyPreview?.trim() || notification.title.trim();
  if (notification.type === 'taskChanged') {
    const changes = value.match(/(?:^|\r?\n)Изменения:[ \t]*\r?\n([\s\S]*?)(?:\r?\n[ \t]*\r?\n|$)/i)?.[1]
      ?.split(/\r?\n/)
      .map((line) => line.trim().replace(/^-\s*/, ''))
      .filter(Boolean);
    if (changes?.length) {
      const priority = (change: string) => /^(Постановщик|Срок|Исполнитель):/i.test(change) ? 0 : 1;
      const summary = changes
        .map((change, index) => ({ change, index }))
        .sort((left, right) => priority(left.change) - priority(right.change) || left.index - right.index)
        .slice(0, 3)
        .map(({ change }) => change.length > 95 ? `${change.slice(0, 92)}...` : change)
        .join('\n');
      return summary.length > 220 ? `${summary.slice(0, 217)}...` : summary;
    }
  }
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
    toastEnabled: item.toastEnabled,
    objectType: item.objectType,
    objectId: item.objectId,
    taskJournalItemId: item.taskId,
    metadataJson: item.metadataJson,
    createdAt: item.createdAt,
  };
}

function buildToastSnoozeKey(notification: RealtimeUserNotification): string {
  if (notification.taskJournalItemId) {
    return `task:${notification.taskJournalItemId}`;
  }

  if (notification.objectType && notification.objectId) {
    return `${notification.objectType}:${notification.objectId}`;
  }

  return notification.eventKey || `notification:${notification.id}`;
}

function buildToastDedupeKey(notification: RealtimeUserNotification): string {
  const snoozeKey = buildToastSnoozeKey(notification);
  // Different changes to the same task must not replace each other's toast.
  return notification.taskJournalItemId
    ? `${snoozeKey}:notification:${notification.id}`
    : snoozeKey;
}

function getPaymentTypeFromNotification(metadataJson?: string | null): 'Income' | 'Expense' | undefined {
  const metadata = parseUserNotificationMetadata(metadataJson);
  const rawType = metadata.paymentType ?? metadata.paymentKind ?? metadata.type ?? metadata.PaymentType ?? metadata.PaymentKind;
  if (typeof rawType !== 'string') {
    return undefined;
  }

  if (rawType === 'Income' || rawType === 'Expense') {
    return rawType;
  }

  const normalizedType = rawType.toLowerCase();
  if (normalizedType === 'income') {
    return 'Income';
  }

  if (normalizedType === 'expense') {
    return 'Expense';
  }

  return undefined;
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
  'caseInactivityControl',
  'caseAccessRequested',
  'caseAccessResolved',
  'caseCourtEvent',
  'courtHearing',
  'courtStatusChanged',
  'courtDecisionRecorded',
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
  'caseInactivityControl',
  'courtHearing',
  'courtStatusChanged',
  'courtDecisionRecorded',
  'courtDecisionPublished',
  'courtSyncFailed',
  'caseProceduralDeadline',
  'caseInactivityReminder',
  'paymentAdvanceReviewRequired',
  'paymentAdvanceReviewEscalation',
  'systemAdminWarning',
]);

function shouldShowRealtimeToast(notification: RealtimeUserNotification): boolean {
  if (isForcedTaskDecisionNotification(notification)) {
    return false;
  }

  return REALTIME_TOAST_TYPES.has(notification.type)
    || isCalendarNotification(notification)
    || isNoteNotification(notification);
}

function isForcedTaskDecisionNotification(notification: RealtimeUserNotification): boolean {
  if (
    notification.type === 'taskRevisionRequested' ||
    notification.type === 'taskDeadlineChangeRequested' ||
    notification.type === 'taskApprovalRequested'
  ) {
    return true;
  }

  if (notification.type !== 'taskApproved') {
    return false;
  }

  const metadata = parseUserNotificationMetadata(notification.metadataJson);
  const resultingStatus = typeof metadata.resultingStatus === 'string' ? metadata.resultingStatus : '';
  const status = typeof metadata.status === 'string' ? metadata.status : '';
  return metadata.completed === true || resultingStatus === 'done' || status === 'done';
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
  run: () => void | Promise<void>;
  variant?: 'primary' | 'secondary';
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
        if (handlers.onOpenCaseDocument) {
          const documentId = getClientCaseDocumentIdFromNotification(notification);
          if (documentId) {
            return {
              label: 'Открыть документы',
              run: () => handlers.onOpenCaseDocument?.(notification.objectId!, documentId),
            };
          }
        }

        if (handlers.onOpenCase) {
          return {
            label: 'Открыть дело',
            run: () => handlers.onOpenCase?.(notification.objectId!),
          };
        }
        break;
      case 'payment':
        if (handlers.onOpenPayment) {
          const paymentType = getPaymentTypeFromNotification(notification.metadataJson);
          if (paymentType) {
            return {
              label: 'Открыть платеж',
              run: () => handlers.onOpenPayment?.(notification.objectId!, paymentType),
            };
          }

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

function readToastSnoozes(now = Date.now()): Record<string, number> {
  if (typeof window === 'undefined') {
    return {};
  }

  try {
    const raw = window.localStorage.getItem(TOAST_SNOOZE_STORAGE_KEY);
    const parsed = raw ? JSON.parse(raw) as Record<string, unknown> : {};
    return Object.entries(parsed).reduce<Record<string, number>>((acc, [key, value]) => {
      if (typeof value === 'number' && value > now) {
        acc[key] = value;
      }
      return acc;
    }, {});
  } catch {
    return {};
  }
}

function isToastSnoozed(dedupeKey: string, now = Date.now()): boolean {
  return (readToastSnoozes(now)[dedupeKey] ?? 0) > now;
}

function snoozeToast(dedupeKey: string, now = Date.now()) {
  if (typeof window === 'undefined') {
    return;
  }

  const next = readToastSnoozes(now);
  next[dedupeKey] = now + TOAST_SNOOZE_MS;
  window.localStorage.setItem(TOAST_SNOOZE_STORAGE_KEY, JSON.stringify(next));
}

function buildLastSeenStorageKey(userId: number): string {
  return `${LAST_SEEN_STORAGE_KEY_PREFIX}.${userId}`;
}

function readLastSeenMs(userId: number, now = Date.now()): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const raw = window.localStorage.getItem(buildLastSeenStorageKey(userId));
  const parsed = raw ? Number.parseInt(raw, 10) : Number.NaN;
  if (!Number.isFinite(parsed)) {
    return null;
  }

  const lowerBound = now - MISSED_REPLAY_LOOKBACK_MS;
  const upperBound = now + 5 * 60 * 1000;
  return parsed >= lowerBound && parsed <= upperBound ? parsed : null;
}

function writeLastSeenMs(userId: number, now = Date.now()) {
  if (typeof window === 'undefined') {
    return;
  }

  window.localStorage.setItem(buildLastSeenStorageKey(userId), String(now));
}

function parseCreatedAtMs(value: string): number | null {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

export function RealtimeNotificationToasts({
  userId,
  onOpenTask,
  onOpenCase,
  onOpenCaseDocument,
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
  const overflowNoticeShownRef = useRef(false);
  const queuedToastNotificationsRef = useRef<RealtimeUserNotification[]>([]);
  const queuedToastTimerRef = useRef<number | null>(null);
  const handlersRef = useRef({
    onOpenTask,
    onOpenCase,
    onOpenCaseDocument,
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
      onOpenCaseDocument,
      onOpenPayment,
      onOpenInvoice,
      onOpenAct,
      onOpenContract,
      onOpenCalendar,
      onOpenNotes,
      onOpenNotifications: onOpenNotifications ?? (() => undefined),
    };
  }, [onOpenAct, onOpenCalendar, onOpenCase, onOpenCaseDocument, onOpenContract, onOpenInvoice, onOpenNotes, onOpenNotifications, onOpenPayment, onOpenTask]);

  useEffect(() => {
    if (!userId || !canStartNotificationsHubConnection()) {
      updateRealtimeNotificationDiagnostics({
        status: 'unavailable',
        fallbackPollingActive: false,
        connectionId: null,
        lastDisconnectedAt: new Date().toISOString(),
        lastError: 'Пользователь не авторизован для подключения к моментальным уведомлениям.',
      });
      return undefined;
    }

    let disposed = false;
    let retryTimer: number | null = null;
    let fallbackTimer: number | null = null;
    let fallbackPollingActive = false;
    let fallbackPollInFlight = false;
    let lastFallbackPollStartedAtMs: number | null = null;
    let missedReplayInFlight = false;
    const mountedAtMs = Date.now();
    const initialMissedSinceMs = readLastSeenMs(userId, mountedAtMs) ?? mountedAtMs;
    let pendingReplaySinceMs: number | null = initialMissedSinceMs;
    shownNotificationIdsRef.current.clear();
    fallbackKnownNotificationIdsRef.current.clear();
    fallbackBaselineReadyRef.current = false;
    fallbackNoticeShownRef.current = false;
    overflowNoticeShownRef.current = false;
    queuedToastNotificationsRef.current = [];
    const persistReplayFrontier = () => {
      const now = Date.now();
      const oldestQueuedAt = queuedToastNotificationsRef.current.reduce(
        (oldest, notification) => Math.min(oldest, parseCreatedAtMs(notification.createdAt) ?? initialMissedSinceMs),
        now,
      );
      const visibleFrontier = document.visibilityState === 'hidden'
        ? readLastSeenMs(userId, now) ?? initialMissedSinceMs
        : now;
      // A reload must still replay anything waiting in the queue or in an unfinished inbox request.
      writeLastSeenMs(userId, Math.min(visibleFrontier, oldestQueuedAt, pendingReplaySinceMs ?? now));
    };
    persistReplayFrontier();
    const connection = createNotificationsHubConnection();
    connectionRef.current = connection;
    updateRealtimeNotificationDiagnostics({
      status: 'connecting',
      fallbackPollingActive: false,
      connectionId: null,
      lastError: null,
    });

    const renderToastNotification = (notification: RealtimeUserNotification) => {
      if (notification.toastEnabled === false || !shouldShowRealtimeToast(notification)) {
        return;
      }

      const dedupeKey = buildToastDedupeKey(notification);
      const snoozeKey = buildToastSnoozeKey(notification);
      if (isToastSnoozed(snoozeKey)) {
        return;
      }

      const action = resolveToastAction(notification, handlersRef.current);
      const caseId = getClientCaseIdFromNotification(notification);
      const isCaseInactivity =
        (notification.type === 'caseInactivityControl' || notification.type === 'caseInactivityReminder') &&
        Boolean(caseId);
      const markRead = async () => {
        try {
          await apiService.markUserNotificationRead(notification.id);
          emitUserNotificationsChanged({
            source: 'realtime',
            reason: 'toast-read',
            unreadDelta: -1,
            notificationId: notification.id,
          });
        } catch (error) {
          console.error('Failed to mark realtime notification as read.', error);
          toast.error('Не удалось отметить уведомление прочитанным.', 'Уведомления');
        }
      };
      const openAction = () => {
        void markRead();
        return action.run();
      };
      const isTaskCompleted = notification.type === 'taskCompleted';
      const confirmCaseActivity = async () => {
        if (!caseId) return;
        try {
          await apiService.markCaseActivity(caseId, { confirmed: true });
          await markRead();
          toast.success('Активность по делу подтверждена.', 'Автоконтроль');
        } catch (error) {
          toast.error(
            error instanceof Error ? error.message : 'Не удалось подтвердить активность.',
            'Автоконтроль',
          );
        }
      };
      const snoozeAction = async () => {
        try {
          if (isCaseInactivity) {
            await apiService.snoozeUserNotification(notification.id, 30);
            emitUserNotificationsChanged({
              source: 'realtime',
              reason: 'case-inactivity-snoozed',
              unreadDelta: -1,
              notificationId: notification.id,
            });
          }
          snoozeToast(snoozeKey);
        } catch (error) {
          toast.error(
            error instanceof Error ? error.message : 'Не удалось отложить напоминание.',
            'Автоконтроль',
          );
        }
      };
      const actions: RealtimeToastAction[] = isTaskCompleted
        ? [
            {
              label: 'Всё понятно',
              run: markRead,
              variant: 'secondary',
            },
            {
              label: 'К задаче',
              run: openAction,
              variant: 'primary',
            },
          ]
        : [
            {
              label: action.label,
              run: openAction,
            },
            ...(isCaseInactivity
              ? [{
                  label: 'Подтвердить активность',
                  run: confirmCaseActivity,
                  variant: 'primary' as const,
                }]
              : []),
            {
              label: 'Прочитано',
              run: markRead,
              variant: 'secondary',
            },
            {
              label: 'Отложить',
              run: snoozeAction,
              variant: 'secondary',
            },
          ];
      toast.showToast({
        title: notification.title || 'Уведомление',
        description: buildToastDescription(notification),
        variant: resolveToastVariant(notification),
        durationMs: isTaskCompleted ? 0 : resolveToastDuration(notification),
        actionLabel: isTaskCompleted ? 'К задаче' : action.label,
        onAction: openAction,
        actions: actions.map((item) => ({
          label: item.label,
          onClick: item.run,
          variant: item.variant,
        })),
        dedupeKey,
        domain: resolveToastDomain(notification),
      });
    };

    const flushQueuedToastNotifications = () => {
      if (disposed || queuedToastTimerRef.current !== null || document.visibilityState === 'hidden') {
        return;
      }

      const next = queuedToastNotificationsRef.current.shift();
      if (!next) {
        return;
      }

      renderToastNotification(next);
      persistReplayFrontier();
      queuedToastTimerRef.current = window.setTimeout(() => {
        queuedToastTimerRef.current = null;
        flushQueuedToastNotifications();
      }, MISSED_REPLAY_SPACING_MS);
    };

    const enqueueToastNotification = (notification: RealtimeUserNotification) => {
      queuedToastNotificationsRef.current.push(notification);
      persistReplayFrontier();
      flushQueuedToastNotifications();
    };

    const showIncomingNotification = (
      notification: RealtimeUserNotification,
      reason: 'created' | 'fallback-poll' | 'missed-replay' | 'visibility-resume',
      delivery: 'immediate' | 'queued' = 'immediate',
    ) => {
      if (!notification || notification.recipientUserId !== userId) {
        return;
      }

      if (shownNotificationIdsRef.current.has(notification.id)) {
        return;
      }
      shownNotificationIdsRef.current.add(notification.id);

      emitUserNotificationsChanged({
        source: 'realtime',
        reason,
        unreadDelta: reason === 'created' || reason === 'fallback-poll' ? 1 : undefined,
        notificationId: notification.id,
        notificationType: notification.type,
        objectType: notification.objectType,
        objectId: notification.objectId,
        taskJournalItemId: notification.taskJournalItemId,
        clientCaseId: getClientCaseIdFromNotification(notification),
      });

      if (delivery === 'queued' || document.visibilityState === 'hidden') {
        enqueueToastNotification(notification);
        return;
      }

      renderToastNotification(notification);
      persistReplayFrontier();
    };

    const showOverflowNotice = () => {
      if (overflowNoticeShownRef.current) {
        return;
      }
      overflowNoticeShownRef.current = true;
      toast.showToast({
        title: 'Есть другие новые уведомления',
        description: 'Показана часть новых сообщений. Остальные доступны в разделе «Уведомления».',
        variant: 'info',
        durationMs: 16000,
        actionLabel: 'К уведомлениям',
        onAction: handlersRef.current.onOpenNotifications,
        dedupeKey: 'notifications:replay-overflow',
        domain: 'system',
      });
    };

    const replayMissedNotifications = async (
      sinceMs: number | null,
      reason: 'missed-replay' | 'visibility-resume',
    ) => {
      if (disposed || missedReplayInFlight || sinceMs === null) {
        return;
      }

      missedReplayInFlight = true;
      pendingReplaySinceMs = Math.min(pendingReplaySinceMs ?? sinceMs, sinceMs);
      persistReplayFrontier();
      try {
        const page = await apiService.getUserNotifications({
          status: 'unread',
          page: 1,
          pageSize: MISSED_REPLAY_PAGE_SIZE,
        });
        if (disposed) {
          return;
        }

        const recentItems = page.items
          .filter((item) => {
            const createdAtMs = parseCreatedAtMs(item.createdAt);
            return createdAtMs !== null && createdAtMs >= sinceMs - 1000;
          })
          .sort((left, right) => {
            const leftCreatedAtMs = parseCreatedAtMs(left.createdAt) ?? 0;
            const rightCreatedAtMs = parseCreatedAtMs(right.createdAt) ?? 0;
            return leftCreatedAtMs - rightCreatedAtMs || left.id - right.id;
          });
        const replayItems = recentItems.slice(-MISSED_REPLAY_MAX_ITEMS);

        for (const item of replayItems) {
          showIncomingNotification(toRealtimeNotification(item, userId), reason, 'queued');
        }
        const oldestPageItemMs = page.items.length > 0
          ? parseCreatedAtMs(page.items[page.items.length - 1].createdAt)
          : null;
        if (recentItems.length > MISSED_REPLAY_MAX_ITEMS
            || (page.total > MISSED_REPLAY_PAGE_SIZE
              && oldestPageItemMs !== null
              && oldestPageItemMs >= sinceMs - 1000)) {
          showOverflowNotice();
        }
        pendingReplaySinceMs = null;
        persistReplayFrontier();
      } catch (error) {
        if (!disposed) {
          console.error('Missed notifications replay failed.', error);
        }
      } finally {
        missedReplayInFlight = false;
      }
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
      const pollStartedAtMs = Date.now();
      try {
        const firstPoll = !fallbackBaselineReadyRef.current;
        const activationThresholdMs = (lastFallbackPollStartedAtMs ?? initialMissedSinceMs) - 1000;
        const freshItems: UserNotificationDto[] = [];
        const seenThisPoll = new Set<number>();
        let moreRecentItemsRemain = false;

        for (let pageNumber = 1; pageNumber <= FALLBACK_MAX_PAGES; pageNumber += 1) {
          const page = await apiService.getUserNotifications({
            status: 'unread',
            page: pageNumber,
            pageSize: FALLBACK_PAGE_SIZE,
          });
          if (disposed || !fallbackPollingActive) {
            return;
          }

          for (const item of page.items) {
            const createdAtMs = parseCreatedAtMs(item.createdAt);
            const isKnown = fallbackKnownNotificationIdsRef.current.has(item.id) || seenThisPoll.has(item.id);
            seenThisPoll.add(item.id);
            if (!isKnown && createdAtMs !== null && createdAtMs >= activationThresholdMs) {
              freshItems.push(item);
            }
          }

          const oldestCreatedAtMs = page.items.length > 0
            ? parseCreatedAtMs(page.items[page.items.length - 1].createdAt)
            : null;
          const hasNextPage = pageNumber * FALLBACK_PAGE_SIZE < page.total;
          moreRecentItemsRemain = hasNextPage
            && oldestCreatedAtMs !== null
            && oldestCreatedAtMs >= activationThresholdMs;
          if (!moreRecentItemsRemain) {
            break;
          }
        }

        freshItems.sort((left, right) =>
          (parseCreatedAtMs(left.createdAt) ?? 0) - (parseCreatedAtMs(right.createdAt) ?? 0) || left.id - right.id);
        fallbackKnownNotificationIdsRef.current = seenThisPoll;
        for (const item of freshItems.slice(-MISSED_REPLAY_MAX_ITEMS)) {
          showIncomingNotification(toRealtimeNotification(item, userId), 'fallback-poll', firstPoll ? 'queued' : 'immediate');
        }
        if (freshItems.length > MISSED_REPLAY_MAX_ITEMS || moreRecentItemsRemain) {
          showOverflowNotice();
        } else {
          overflowNoticeShownRef.current = false;
        }

        fallbackBaselineReadyRef.current = true;
        lastFallbackPollStartedAtMs = pollStartedAtMs;
        if (!missedReplayInFlight) {
          pendingReplaySinceMs = null;
          persistReplayFrontier();
        }
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

      fallbackPollingActive = true;
      lastFallbackPollStartedAtMs = readLastSeenMs(userId) ?? initialMissedSinceMs;
      overflowNoticeShownRef.current = false;
      fallbackBaselineReadyRef.current = false;
      fallbackKnownNotificationIdsRef.current.clear();
      updateRealtimeNotificationDiagnostics({
        status: 'fallback',
        fallbackPollingActive: true,
        connectionId: null,
        lastDisconnectedAt: new Date().toISOString(),
      });
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
      updateRealtimeNotificationDiagnostics({
        fallbackPollingActive: false,
      });
    };

    connection.on(REALTIME_USER_NOTIFICATION_CREATED_EVENT, (notification: RealtimeUserNotification) => {
      showIncomingNotification(notification, 'created');
    });
    connection.on(REALTIME_TASK_JOURNAL_CHANGED_EVENT, () => {
      window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT));
    });

    const handleVisibilityChange = () => {
      if (typeof document === 'undefined') {
        return;
      }

      if (document.visibilityState === 'hidden') {
        persistReplayFrontier();
        return;
      }

      const missedSinceMs = readLastSeenMs(userId);
      void replayMissedNotifications(missedSinceMs, 'visibility-resume');
      flushQueuedToastNotifications();
    };

    const handlePageHide = () => {
      persistReplayFrontier();
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    window.addEventListener('pagehide', handlePageHide);

    const start = async () => {
      if (disposed) {
        return;
      }

      try {
        await connection.start();
        updateRealtimeNotificationDiagnostics({
          status: 'connected',
          fallbackPollingActive: false,
          connectionId: (connection as { connectionId?: string | null }).connectionId ?? null,
          lastConnectedAt: new Date().toISOString(),
          lastError: null,
        });
        stopFallbackPolling();
        void replayMissedNotifications(initialMissedSinceMs, 'missed-replay');
      } catch (error) {
        if (!disposed) {
          if (isExpectedRealtimeStartupError(error)) {
            console.debug('Realtime notifications are unavailable; retrying in background.', error);
          } else {
            console.error('Realtime notifications failed to start.', error);
          }
          updateRealtimeNotificationDiagnostics({
            status: 'fallback',
            fallbackPollingActive: true,
            connectionId: null,
            error,
            lastDisconnectedAt: new Date().toISOString(),
          });
          startFallbackPolling();
          retryTimer = window.setTimeout(start, 5000);
        }
      }
    };

    connection.onreconnecting((error) => {
      if (disposed) {
        return;
      }
      updateRealtimeNotificationDiagnostics({
        status: 'reconnecting',
        connectionId: null,
        error,
        lastDisconnectedAt: new Date().toISOString(),
      });
      startFallbackPolling();
    });

    connection.onreconnected(() => {
      updateRealtimeNotificationDiagnostics({
        status: 'connected',
        fallbackPollingActive: false,
        connectionId: (connection as { connectionId?: string | null }).connectionId ?? null,
        lastConnectedAt: new Date().toISOString(),
        lastError: null,
      });
      stopFallbackPolling();
      emitUserNotificationsChanged({ source: 'realtime', reason: 'reconnected' });
      const missedSinceMs = readLastSeenMs(userId);
      void replayMissedNotifications(missedSinceMs, 'visibility-resume');
    });

    connection.onclose((error) => {
      if (disposed) {
        return;
      }

      if (error && !isExpectedRealtimeStartupError(error)) {
        console.error('Realtime notifications connection closed.', error);
      }

      updateRealtimeNotificationDiagnostics({
        status: 'reconnecting',
        fallbackPollingActive: true,
        connectionId: null,
        error,
        lastDisconnectedAt: new Date().toISOString(),
      });
      startFallbackPolling();
      retryTimer = window.setTimeout(start, 5000);
    });

    void start();

    return () => {
      disposed = true;
      persistReplayFrontier();
      if (retryTimer !== null) {
        window.clearTimeout(retryTimer);
      }
      if (queuedToastTimerRef.current !== null) {
        window.clearTimeout(queuedToastTimerRef.current);
        queuedToastTimerRef.current = null;
      }
      queuedToastNotificationsRef.current = [];
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      window.removeEventListener('pagehide', handlePageHide);
      stopFallbackPolling();
      connection.off(REALTIME_USER_NOTIFICATION_CREATED_EVENT);
      connection.off(REALTIME_TASK_JOURNAL_CHANGED_EVENT);
      if (connection.state !== HubConnectionState.Disconnected) {
        void stopNotificationsHubConnection(connection);
      }
      if (connectionRef.current === connection) {
        connectionRef.current = null;
      }
      updateRealtimeNotificationDiagnostics({
        status: 'disconnected',
        fallbackPollingActive: false,
        connectionId: null,
        lastDisconnectedAt: new Date().toISOString(),
      });
    };
  }, [toast, userId]);

  return null;
}
