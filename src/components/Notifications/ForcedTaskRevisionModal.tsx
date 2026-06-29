import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, ExternalLink, Loader2 } from 'lucide-react';
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
  const iconClassName = isCompletedApproval
    ? 'inline-flex h-12 w-12 shrink-0 items-center justify-center rounded-2xl bg-emerald-100 text-emerald-700'
    : 'inline-flex h-12 w-12 shrink-0 items-center justify-center rounded-2xl bg-orange-100 text-orange-700';
  const detailsTitleClassName = isCompletedApproval
    ? 'mt-4 text-xs font-semibold uppercase tracking-[0.14em] text-emerald-700'
    : 'mt-4 text-xs font-semibold uppercase tracking-[0.14em] text-orange-700';
  const infoClassName = isCompletedApproval
    ? 'mt-4 rounded-xl border border-emerald-100 bg-emerald-50 px-4 py-3 text-sm leading-6 text-emerald-800'
    : 'mt-4 rounded-xl border border-blue-100 bg-blue-50 px-4 py-3 text-sm leading-6 text-blue-800';
  const buttonClassName = isCompletedApproval
    ? 'inline-flex items-center justify-center gap-2 rounded-xl bg-emerald-600 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-not-allowed disabled:opacity-60'
    : 'inline-flex items-center justify-center gap-2 rounded-xl bg-orange-600 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-orange-700 disabled:cursor-not-allowed disabled:opacity-60';
  const Icon = isCompletedApproval ? CheckCircle2 : AlertTriangle;

  return (
    <div className="fixed inset-0 z-[80] flex items-center justify-center bg-slate-950/55 px-4 py-6 backdrop-blur-sm" role="dialog" aria-modal="true" aria-labelledby="forced-task-revision-title">
      <div className="w-full max-w-3xl rounded-2xl bg-white p-5 shadow-2xl sm:p-6">
        <div className="flex items-start gap-4">
          <span className={iconClassName}>
            <Icon className="h-7 w-7" />
          </span>
          <div className="min-w-0">
            <div id="forced-task-revision-title" className="text-xl font-semibold text-slate-950">
              {modalTitle}
            </div>
            <p className="mt-1 text-sm leading-6 text-slate-600">
              {modalDescription}
            </p>
          </div>
        </div>

        <div className="mt-5 rounded-2xl border border-slate-200 bg-slate-50 p-4">
          <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">Задача</div>
          <div className="mt-1 text-lg font-semibold text-slate-950">{getTaskTitle(current)}</div>
          <div className={detailsTitleClassName}>{detailsTitle}</div>
          <p className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-700">{decisionText}</p>
        </div>

        <div className={infoClassName}>
          {infoText}
        </div>

        <div className="mt-5 flex flex-col-reverse gap-2 sm:flex-row sm:items-center sm:justify-end">
          {current.taskId && onOpenTask ? (
            <button
              type="button"
              onClick={() => void acknowledge(true)}
              disabled={acknowledgingId === current.id}
              className="inline-flex items-center justify-center gap-2 rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60"
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
            {acknowledgingId === current.id || loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
            {primaryButtonText}
          </button>
        </div>
      </div>
    </div>
  );
}
