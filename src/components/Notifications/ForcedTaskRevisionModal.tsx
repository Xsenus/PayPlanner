import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, ExternalLink, Loader2 } from 'lucide-react';
import { apiService, type UserNotificationDto } from '../../services/api';
import { emitUserNotificationsChanged, USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';
import { parseUserNotificationMetadata } from '../../utils/userNotificationMetadata';

type ForcedTaskRevisionModalProps = {
  onOpenTask?: (taskId: number) => void;
};

function getRevisionText(notification: UserNotificationDto): string {
  const metadata = parseUserNotificationMetadata(notification.metadataJson);
  const resolution = metadata.resolution;
  if (typeof resolution === 'string' && resolution.trim()) {
    return resolution.trim();
  }

  return notification.bodyPreview || 'Задача возвращена на доработку. Откройте задачу и проверьте запись в истории.';
}

function getTaskTitle(notification: UserNotificationDto): string {
  const title = notification.title?.trim();
  if (!title) {
    return 'Задача вернулась на доработку';
  }

  return title.replace(/^Задача возвращена на доработку:\s*/i, '').trim() || title;
}

export function ForcedTaskRevisionModal({ onOpenTask }: ForcedTaskRevisionModalProps) {
  const [items, setItems] = useState<UserNotificationDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [acknowledgingId, setAcknowledgingId] = useState<number | null>(null);
  const acknowledgedIdsRef = useRef<Set<number>>(new Set());

  const loadItems = useCallback(async () => {
    setLoading(true);
    try {
      const page = await apiService.getUserNotifications({
        status: 'unread',
        type: 'taskRevisionRequested',
        requiresAction: true,
        page: 1,
        pageSize: 10,
      });
      setItems(page.items.filter((item) =>
        item.type === 'taskRevisionRequested'
        && !item.isRead
        && !acknowledgedIdsRef.current.has(item.id)));
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
  const revisionText = useMemo(() => current ? getRevisionText(current) : '', [current]);

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

  if (!current) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-[80] flex items-center justify-center bg-slate-950/55 px-4 py-6 backdrop-blur-sm" role="dialog" aria-modal="true" aria-labelledby="forced-task-revision-title">
      <div className="w-full max-w-3xl rounded-2xl bg-white p-5 shadow-2xl sm:p-6">
        <div className="flex items-start gap-4">
          <span className="inline-flex h-12 w-12 shrink-0 items-center justify-center rounded-2xl bg-orange-100 text-orange-700">
            <AlertTriangle className="h-7 w-7" />
          </span>
          <div className="min-w-0">
            <div id="forced-task-revision-title" className="text-xl font-semibold text-slate-950">
              Задача вернулась на доработку
            </div>
            <p className="mt-1 text-sm leading-6 text-slate-600">
              Прочитайте рецензию. После исправления можно повторно направить задачу на согласование.
            </p>
          </div>
        </div>

        <div className="mt-5 rounded-2xl border border-slate-200 bg-slate-50 p-4">
          <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">Задача</div>
          <div className="mt-1 text-lg font-semibold text-slate-950">{getTaskTitle(current)}</div>
          <div className="mt-4 text-xs font-semibold uppercase tracking-[0.14em] text-orange-700">Что исправить</div>
          <p className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-700">{revisionText}</p>
        </div>

        <div className="mt-4 rounded-xl border border-blue-100 bg-blue-50 px-4 py-3 text-sm leading-6 text-blue-800">
          Уже согласовавшие участники повторно в маршруте не участвуют. Исправьте замечания и отправьте задачу на согласование еще раз.
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
            className="inline-flex items-center justify-center gap-2 rounded-xl bg-orange-600 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-orange-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {acknowledgingId === current.id || loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
            Информация прочитана
          </button>
        </div>
      </div>
    </div>
  );
}
