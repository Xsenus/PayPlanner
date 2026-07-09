import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { CheckCircle2, ExternalLink, Loader2, TimerReset, XCircle } from 'lucide-react';
import { apiService, type UserNotificationDto } from '../../services/api';
import { emitUserNotificationsChanged, USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';
import { parseUserNotificationMetadata } from '../../utils/userNotificationMetadata';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone, fromTimeZoneDateTimeInput, toTimeZoneDateTimeInput } from '../../utils/timeZoneDate';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_SETTING_KEY } from '../../utils/timeZoneOptions';

type ForcedTaskRevisionModalProps = {
  onOpenTask?: (taskId: number) => void;
};

type ForcedTaskDecisionKind = 'revision' | 'completedApproval' | 'deadlineChangeRequest';
type ForcedTaskQueueAction = 'acknowledge' | 'deadlineChangeDecision';

type ForcedTaskNotificationRule = {
  type: string;
  action: ForcedTaskQueueAction;
  resolveDecisionKind: (notification: UserNotificationDto) => ForcedTaskDecisionKind | null;
};

type ForcedTaskQueueItem = {
  notification: UserNotificationDto;
  rule: ForcedTaskNotificationRule;
  decisionKind: ForcedTaskDecisionKind;
};

const FORCED_TASK_QUEUE_PAGE_SIZE = 25;
const FORCED_TASK_POLL_INTERVAL_MS = 30_000;

function getNotificationMetadata(notification: UserNotificationDto): Record<string, unknown> {
  return parseUserNotificationMetadata(notification.metadataJson);
}

function readPositiveInteger(value: unknown): number | null {
  if (typeof value === 'number' && Number.isInteger(value) && value > 0) {
    return value;
  }

  if (typeof value === 'string') {
    const parsed = Number.parseInt(value, 10);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
  }

  return null;
}

function readString(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

function getDeadlineChangeRequestId(notification: UserNotificationDto): number | null {
  const metadata = getNotificationMetadata(notification);
  return readPositiveInteger(metadata.deadlineChangeRequestId);
}

function getDeadlineChangeRequestedDueAt(notification: UserNotificationDto): string {
  const metadata = getNotificationMetadata(notification);
  return readString(metadata.requestedDueAt);
}

function getDeadlineChangeReason(notification: UserNotificationDto): string {
  const metadata = getNotificationMetadata(notification);
  return readString(metadata.reason) || notification.bodyPreview || 'Причина переноса срока не указана.';
}

function getDecisionKind(notification: UserNotificationDto): ForcedTaskDecisionKind | null {
  if (notification.type === 'taskDeadlineChangeRequested') {
    return notification.taskId && getDeadlineChangeRequestId(notification) ? 'deadlineChangeRequest' : null;
  }

  if (notification.type === 'taskRevisionRequested') {
    return 'revision';
  }

  if (notification.type !== 'taskApproved') {
    return null;
  }

  const metadata = getNotificationMetadata(notification);
  const resultingStatus = typeof metadata.resultingStatus === 'string' ? metadata.resultingStatus : '';
  const status = typeof metadata.status === 'string' ? metadata.status : '';
  if (metadata.completed === true || resultingStatus === 'done' || status === 'done') {
    return 'completedApproval';
  }

  return null;
}

const FORCED_TASK_NOTIFICATION_RULES: ForcedTaskNotificationRule[] = [
  {
    type: 'taskDeadlineChangeRequested',
    action: 'deadlineChangeDecision',
    resolveDecisionKind: getDecisionKind,
  },
  {
    type: 'taskRevisionRequested',
    action: 'acknowledge',
    resolveDecisionKind: getDecisionKind,
  },
  {
    type: 'taskApproved',
    action: 'acknowledge',
    resolveDecisionKind: getDecisionKind,
  },
];

function buildForcedTaskQueueItem(notification: UserNotificationDto, rule: ForcedTaskNotificationRule): ForcedTaskQueueItem | null {
  if (notification.type !== rule.type) {
    return null;
  }

  const decisionKind = rule.resolveDecisionKind(notification);
  if (!decisionKind) {
    return null;
  }

  return {
    notification,
    rule,
    decisionKind,
  };
}

function getRevisionText(notification: UserNotificationDto): string {
  const metadata = getNotificationMetadata(notification);
  const resolution = metadata.resolution;
  if (typeof resolution === 'string' && resolution.trim()) {
    return resolution.trim();
  }

  return notification.bodyPreview || 'Задача возвращена на доработку. Откройте задачу и проверьте запись в истории.';
}

function getDecisionText(notification: UserNotificationDto, kind: ForcedTaskDecisionKind): string {
  if (kind === 'completedApproval') {
    return notification.bodyPreview || 'Задача согласована и завершена. Работа принята, задача закрыта.';
  }

  return getRevisionText(notification);
}

function getTaskTitle(notification: UserNotificationDto): string {
  const title = notification.title?.trim();
  if (!title) {
    return 'Задача вернулась на доработку';
  }

  return title
    .replace(/^Запрос переноса срока:\s*/i, '')
    .replace(/^Задача возвращена на доработку:\s*/i, '')
    .replace(/^Задача согласована и завершена:\s*/i, '')
    .replace(/^Задача согласована:\s*/i, '')
    .trim() || title;
}

function sortQueueItems(left: ForcedTaskQueueItem, right: ForcedTaskQueueItem): number {
  const leftTime = Date.parse(left.notification.createdAt) || 0;
  const rightTime = Date.parse(right.notification.createdAt) || 0;
  return leftTime - rightTime || left.notification.id - right.notification.id;
}

export function ForcedTaskRevisionModal({ onOpenTask }: ForcedTaskRevisionModalProps) {
  const [items, setItems] = useState<ForcedTaskQueueItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [acknowledgingId, setAcknowledgingId] = useState<number | null>(null);
  const [timeZoneId, setTimeZoneId] = useState(() => normalizeUserTimeZoneId());
  const [deadlineChangeDueAt, setDeadlineChangeDueAt] = useState('');
  const [deadlineChangeComment, setDeadlineChangeComment] = useState('');
  const [deadlineChangeError, setDeadlineChangeError] = useState<string | null>(null);
  const [deadlineChangeSaving, setDeadlineChangeSaving] = useState<'approve' | 'reject' | null>(null);
  const acknowledgedIdsRef = useRef<Set<number>>(new Set());

  const loadItems = useCallback(async () => {
    setLoading(true);
    try {
      const pages = await Promise.all(FORCED_TASK_NOTIFICATION_RULES.map((rule) =>
        apiService.getUserNotifications({
          status: 'unread',
          type: rule.type,
          requiresAction: true,
          page: 1,
          pageSize: FORCED_TASK_QUEUE_PAGE_SIZE,
        })));

      const nextItems = new Map<number, ForcedTaskQueueItem>();
      pages.forEach((page, index) => {
        const rule = FORCED_TASK_NOTIFICATION_RULES[index];
        page.items.forEach((notification) => {
          if (notification.isRead || acknowledgedIdsRef.current.has(notification.id)) {
            return;
          }

          const queueItem = buildForcedTaskQueueItem(notification, rule);
          if (queueItem) {
            nextItems.set(notification.id, queueItem);
          }
        });
      });

      setItems([...nextItems.values()].sort(sortQueueItems));
    } catch (error) {
      console.error('Failed to load forced task action messages.', error);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;

    apiService.getUserSetting(USER_TIME_ZONE_SETTING_KEY)
      .then((setting) => {
        if (!cancelled) {
          setTimeZoneId(normalizeUserTimeZoneId(setting?.value));
        }
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    void loadItems();

    const handleNotificationsChanged = () => {
      void loadItems();
    };
    const intervalId = window.setInterval(() => void loadItems(), FORCED_TASK_POLL_INTERVAL_MS);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, handleNotificationsChanged);

    return () => {
      window.clearInterval(intervalId);
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleNotificationsChanged);
    };
  }, [loadItems]);

  const currentItem = items[0] ?? null;
  const current = currentItem?.notification ?? null;
  const decisionKind = currentItem?.decisionKind ?? null;
  const decisionText = useMemo(() => current && decisionKind ? getDecisionText(current, decisionKind) : '', [current, decisionKind]);

  useEffect(() => {
    if (!current || decisionKind !== 'deadlineChangeRequest') {
      return;
    }

    const requestedDueAt = getDeadlineChangeRequestedDueAt(current);
    setDeadlineChangeDueAt(requestedDueAt ? toTimeZoneDateTimeInput(requestedDueAt, timeZoneId) : '');
    setDeadlineChangeComment('');
    setDeadlineChangeError(null);
    setDeadlineChangeSaving(null);
  }, [current?.id, current, decisionKind, timeZoneId]);

  const finishCurrentNotification = async (reason: string) => {
    if (!current) return;

    await apiService.markUserNotificationRead(current.id);
    acknowledgedIdsRef.current.add(current.id);
    setItems((previous) => previous.filter((item) => item.notification.id !== current.id));
    emitUserNotificationsChanged({
      source: 'system',
      reason,
      unreadDelta: -1,
      notificationId: current.id,
    });
  };

  const acknowledge = async (openTask = false) => {
    if (!current || acknowledgingId) return;

    setAcknowledgingId(current.id);
    try {
      await finishCurrentNotification('forced-task-action-read');
      if (openTask && current.taskId && onOpenTask) {
        onOpenTask(current.taskId);
      }
    } catch (error) {
      console.error('Failed to acknowledge task action message.', error);
    } finally {
      setAcknowledgingId(null);
    }
  };

  const approveDeadlineChange = async () => {
    if (!current || deadlineChangeSaving) return;

    const taskId = current.taskId;
    const requestId = getDeadlineChangeRequestId(current);
    if (!taskId || !requestId) {
      setDeadlineChangeError('Не удалось определить задачу или запрос переноса срока.');
      return;
    }

    if (!deadlineChangeDueAt) {
      setDeadlineChangeError('Укажите дату и время нового срока.');
      return;
    }

    setDeadlineChangeSaving('approve');
    setDeadlineChangeError(null);
    try {
      await apiService.approveTaskJournalDeadlineChangeRequest(taskId, requestId, {
        approvedDueAt: fromTimeZoneDateTimeInput(deadlineChangeDueAt, timeZoneId),
        comment: deadlineChangeComment.trim() || null,
      });
      await finishCurrentNotification('forced-task-deadline-change-approved');
    } catch (error) {
      setDeadlineChangeError(extractDisplayErrorMessage(error, 'Не удалось согласовать перенос срока.'));
    } finally {
      setDeadlineChangeSaving(null);
    }
  };

  const rejectDeadlineChange = async () => {
    if (!current || deadlineChangeSaving) return;

    const taskId = current.taskId;
    const requestId = getDeadlineChangeRequestId(current);
    if (!taskId || !requestId) {
      setDeadlineChangeError('Не удалось определить задачу или запрос переноса срока.');
      return;
    }

    const comment = deadlineChangeComment.trim();
    if (!comment) {
      setDeadlineChangeError('Укажите причину отказа в переносе срока.');
      return;
    }

    setDeadlineChangeSaving('reject');
    setDeadlineChangeError(null);
    try {
      await apiService.rejectTaskJournalDeadlineChangeRequest(taskId, requestId, { comment });
      await finishCurrentNotification('forced-task-deadline-change-rejected');
    } catch (error) {
      setDeadlineChangeError(extractDisplayErrorMessage(error, 'Не удалось отклонить перенос срока.'));
    } finally {
      setDeadlineChangeSaving(null);
    }
  };

  if (!current || !decisionKind) {
    return null;
  }

  if (currentItem?.rule.action === 'deadlineChangeDecision') {
    const requestedDueAt = getDeadlineChangeRequestedDueAt(current);
    const formattedRequestedDueAt = requestedDueAt ? formatDateTimeInTimeZone(requestedDueAt, timeZoneId) : 'Не указано';
    const queueCountText = items.length > 1 ? `Еще обязательных уведомлений: ${items.length - 1}` : null;
    const isSaving = Boolean(deadlineChangeSaving);

    return (
      <div className="fixed inset-0 z-[80] flex items-start justify-center overflow-y-auto bg-slate-950/55 px-3 py-4 backdrop-blur-sm sm:items-center sm:px-4 sm:py-6" role="dialog" aria-modal="true" aria-labelledby="forced-deadline-change-title" data-testid="forced-deadline-change-prompt">
        <div className="max-h-[calc(100vh-2rem)] w-full max-w-[min(46rem,calc(100vw-1.5rem))] overflow-y-auto rounded-2xl border border-amber-200 bg-white p-4 shadow-2xl sm:p-6">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
            <div className="min-w-0">
              <p className="text-xs font-semibold uppercase tracking-[0.16em] text-amber-700">Запрос переноса срока</p>
              <h2 id="forced-deadline-change-title" className="mt-2 text-xl font-semibold text-slate-950">
                У вас запросили перенос срока по задаче
              </h2>
              {queueCountText ? (
                <p className="mt-1 text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">
                  {queueCountText}
                </p>
              ) : null}
              <p className="mt-2 line-clamp-2 text-sm text-slate-600">{getTaskTitle(current)}</p>
            </div>
            <span className="inline-flex w-fit items-center gap-1.5 rounded-full border border-amber-200 bg-amber-50 px-3 py-1 text-xs font-semibold text-amber-800">
              <TimerReset className="h-3.5 w-3.5" />
              Требует решения
            </span>
          </div>

          <div className="mt-5 grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-700 sm:grid-cols-2">
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Запрошенный срок</div>
              <div className="mt-1 font-semibold text-slate-900">{formattedRequestedDueAt}</div>
            </div>
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Действие</div>
              <div className="mt-1 font-semibold text-slate-900">Согласуйте новый срок или откажите</div>
            </div>
          </div>

          <div className="mt-4 rounded-xl border border-amber-200 bg-amber-50 px-3 py-3 text-sm leading-6 text-amber-950">
            <span className="font-semibold">Причина переноса:</span> {getDeadlineChangeReason(current)}
          </div>

          <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="forced-deadline-change-due-at">
            Новый срок задачи
          </label>
          <input
            id="forced-deadline-change-due-at"
            type="datetime-local"
            value={deadlineChangeDueAt}
            onChange={(event) => {
              setDeadlineChangeDueAt(event.target.value);
              if (deadlineChangeError) setDeadlineChangeError(null);
            }}
            className="mt-2 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50"
            disabled={isSaving}
            aria-label="Новый срок задачи"
            autoFocus
          />
          <p className="mt-2 text-sm text-slate-500">
            По умолчанию указана дата, которую запросил исполнитель. Перед согласованием ее можно изменить.
          </p>

          <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="forced-deadline-change-comment">
            Комментарий к решению
          </label>
          <textarea
            id="forced-deadline-change-comment"
            value={deadlineChangeComment}
            onChange={(event) => {
              setDeadlineChangeComment(event.target.value);
              if (deadlineChangeError) setDeadlineChangeError(null);
            }}
            className="mt-2 block w-full min-h-[96px] resize-y rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm leading-6 text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50"
            rows={4}
            placeholder="Для отказа укажите причину. Для согласования комментарий можно оставить пустым."
            disabled={isSaving}
          />
          {deadlineChangeError ? <p className="mt-2 text-sm font-semibold text-rose-600">{deadlineChangeError}</p> : null}

          <div className="mt-5 flex flex-col-reverse gap-3 sm:flex-row sm:items-center sm:justify-end">
            {current.taskId && onOpenTask ? (
              <button
                type="button"
                onClick={() => onOpenTask(current.taskId!)}
                disabled={isSaving}
                className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60 sm:w-auto"
              >
                <ExternalLink className="h-4 w-4" />
                Открыть задачу
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => void rejectDeadlineChange()}
              disabled={isSaving}
              className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-rose-200 bg-white px-4 py-2.5 text-sm font-semibold text-rose-700 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-60 sm:w-auto"
            >
              {deadlineChangeSaving === 'reject' ? <Loader2 className="h-4 w-4 animate-spin" /> : <XCircle className="h-4 w-4" />}
              Отказать
            </button>
            <button
              type="button"
              onClick={() => void approveDeadlineChange()}
              disabled={isSaving || !deadlineChangeDueAt}
              className="inline-flex w-full items-center justify-center gap-2 rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
            >
              {deadlineChangeSaving === 'approve' ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
              Согласовать перенос
            </button>
          </div>
        </div>
      </div>
    );
  }

  const isCompletedApproval = decisionKind === 'completedApproval';
  const modalTitle = isCompletedApproval ? 'Задача согласована и завершена' : 'Задача вернулась на доработку';
  const modalDescription = isCompletedApproval
    ? 'Задача согласована, работа принята и задача закрыта. Ознакомьтесь с решением.'
    : 'Прочитайте рецензию. После исправления можно повторно направить задачу на согласование.';
  const detailsTitle = isCompletedApproval ? 'Решение' : 'Что исправить';
  const infoText = isCompletedApproval
    ? 'Дополнительных действий по задаче не требуется. Сообщение закроется после подтверждения.'
    : 'Уже согласовавшие участники повторно в маршруте не участвуют. Исправьте замечания и отправьте задачу на согласование еще раз.';
  const primaryButtonText = isCompletedApproval ? 'Ознакомлен' : 'Информация прочитана';
  const modalEyebrow = isCompletedApproval ? 'Согласование завершено' : 'Задача на доработку';
  const modalEyebrowClassName = isCompletedApproval
    ? 'text-xs font-semibold uppercase tracking-[0.16em] text-emerald-600'
    : 'text-xs font-semibold uppercase tracking-[0.16em] text-orange-600';
  const infoClassName = isCompletedApproval
    ? 'mt-2 text-sm leading-6 text-emerald-700'
    : 'mt-2 text-sm leading-6 text-slate-500';
  const buttonClassName = isCompletedApproval
    ? 'w-full rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-not-allowed disabled:opacity-60 sm:w-auto'
    : 'w-full rounded-xl bg-orange-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-orange-700 disabled:cursor-not-allowed disabled:opacity-60 sm:w-auto';
  const queueCountText = items.length > 1 ? `Еще обязательных уведомлений: ${items.length - 1}` : null;

  return (
    <div className="fixed inset-0 z-[80] flex items-start justify-center overflow-y-auto bg-slate-900/45 px-3 py-4 backdrop-blur-sm sm:items-center sm:px-4 sm:py-6" role="dialog" aria-modal="true" aria-labelledby="forced-task-revision-title" data-testid="forced-task-action-queue">
      <div className="max-h-[calc(100vh-2rem)] w-full max-w-[min(42rem,calc(100vw-1.5rem))] overflow-y-auto rounded-2xl bg-white p-4 shadow-2xl sm:p-6">
        <div className="flex items-start justify-between gap-4">
          <div className="min-w-0">
            <p className={modalEyebrowClassName}>{modalEyebrow}</p>
            <h2 id="forced-task-revision-title" className="mt-2 text-xl font-semibold text-slate-900">
              {modalTitle}
            </h2>
            {queueCountText ? (
              <p className="mt-1 text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">
                {queueCountText}
              </p>
            ) : null}
            <p className="mt-2 line-clamp-2 text-sm text-slate-500">{getTaskTitle(current)}</p>
            <p className="mt-2 text-sm leading-6 text-slate-600">
              {modalDescription}
            </p>
          </div>
        </div>

        <div className="mt-5 block text-sm font-semibold text-slate-700">
          {detailsTitle}
        </div>
        <div className="mt-2 max-h-[40vh] min-h-[120px] overflow-y-auto whitespace-pre-wrap rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm leading-6 text-slate-700 shadow-sm sm:min-h-[150px]" data-testid="forced-task-message-box">
          {decisionText}
        </div>
        <p className={infoClassName}>
          {infoText}
        </p>

        <div className="mt-6 flex flex-col-reverse gap-3 sm:flex-row sm:items-center sm:justify-end">
          {current.taskId && onOpenTask ? (
            <button
              type="button"
              onClick={() => void acknowledge(true)}
              disabled={acknowledgingId === current.id}
              className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60 sm:w-auto"
            >
              <ExternalLink className="h-4 w-4" />
              Открыть задачу
            </button>
          ) : null}
          <button
            type="button"
            onClick={() => void acknowledge(false)}
            disabled={acknowledgingId === current.id}
            className={buttonClassName}
          >
            {acknowledgingId === current.id || loading ? <Loader2 className="mr-2 inline h-4 w-4 animate-spin" /> : <CheckCircle2 className="mr-2 inline h-4 w-4" />}
            {primaryButtonText}
          </button>
        </div>
      </div>
    </div>
  );
}
