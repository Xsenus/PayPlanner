import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { CheckCircle2, ExternalLink, Loader2 } from 'lucide-react';
import { apiService, type UserNotificationDto } from '../../services/api';
import { emitUserNotificationsChanged, USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';
import { parseUserNotificationMetadata } from '../../utils/userNotificationMetadata';

type ForcedTaskRevisionModalProps = {
  onOpenTask?: (taskId: number) => void;
};

type ForcedTaskDecisionKind = 'revision' | 'completedApproval';

function getNotificationMetadata(notification: UserNotificationDto): Record<string, unknown> {
  return parseUserNotificationMetadata(notification.metadataJson);
}

function getDecisionKind(notification: UserNotificationDto): ForcedTaskDecisionKind | null {
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
    .replace(/^Задача возвращена на доработку:\s*/i, '')
    .replace(/^Задача согласована и завершена:\s*/i, '')
    .replace(/^Задача согласована:\s*/i, '')
    .trim() || title;
}

function sortNotifications(left: UserNotificationDto, right: UserNotificationDto): number {
  const leftTime = Date.parse(left.createdAt) || 0;
  const rightTime = Date.parse(right.createdAt) || 0;
  return leftTime - rightTime || left.id - right.id;
}

export function ForcedTaskRevisionModal({ onOpenTask }: ForcedTaskRevisionModalProps) {
  const [items, setItems] = useState<UserNotificationDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [acknowledgingId, setAcknowledgingId] = useState<number | null>(null);
  const acknowledgedIdsRef = useRef<Set<number>>(new Set());

  const loadItems = useCallback(async () => {
    setLoading(true);
    try {
      const [revisionPage, approvedPage] = await Promise.all([
        apiService.getUserNotifications({
          status: 'unread',
          type: 'taskRevisionRequested',
          requiresAction: true,
          page: 1,
          pageSize: 10,
        }),
        apiService.getUserNotifications({
          status: 'unread',
          type: 'taskApproved',
          requiresAction: true,
          page: 1,
          pageSize: 10,
        }),
      ]);
      setItems([...revisionPage.items, ...approvedPage.items]
        .filter((item) =>
          !item.isRead
          && !acknowledgedIdsRef.current.has(item.id)
          && getDecisionKind(item) !== null)
        .sort(sortNotifications));
    } catch (error) {
      console.error('Failed to load forced task revision messages.', error);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadItems();

    const handleNotificationsChanged = () => {
      void loadItems();
    };
    const intervalId = window.setInterval(() => void loadItems(), 30_000);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, handleNotificationsChanged);

    return () => {
      window.clearInterval(intervalId);
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleNotificationsChanged);
    };
  }, [loadItems]);

  const current = items[0] ?? null;
  const decisionKind = current ? getDecisionKind(current) : null;
  const decisionText = useMemo(() => current && decisionKind ? getDecisionText(current, decisionKind) : '', [current, decisionKind]);

  const acknowledge = async (openTask = false) => {
    if (!current || acknowledgingId) return;

    setAcknowledgingId(current.id);
    try {
      await apiService.markUserNotificationRead(current.id);
      acknowledgedIdsRef.current.add(current.id);
      setItems((previous) => previous.filter((item) => item.id !== current.id));
      emitUserNotificationsChanged({
        source: 'system',
        reason: 'forced-task-revision-read',
        unreadDelta: -1,
        notificationId: current.id,
      });
      if (openTask && current.taskId && onOpenTask) {
        onOpenTask(current.taskId);
      }
    } catch (error) {
      console.error('Failed to acknowledge task revision message.', error);
    } finally {
      setAcknowledgingId(null);
    }
  };

  if (!current || !decisionKind) {
    return null;
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

  return (
    <div className="fixed inset-0 z-[80] flex items-start justify-center overflow-y-auto bg-slate-900/45 px-3 py-4 backdrop-blur-sm sm:items-center sm:px-4 sm:py-6" role="dialog" aria-modal="true" aria-labelledby="forced-task-revision-title">
      <div className="max-h-[calc(100vh-2rem)] w-full max-w-[min(42rem,calc(100vw-1.5rem))] overflow-y-auto rounded-2xl bg-white p-4 shadow-2xl sm:p-6">
        <div className="flex items-start justify-between gap-4">
          <div className="min-w-0">
            <p className={modalEyebrowClassName}>{modalEyebrow}</p>
            <h2 id="forced-task-revision-title" className="mt-2 text-xl font-semibold text-slate-900">
              {modalTitle}
            </h2>
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
