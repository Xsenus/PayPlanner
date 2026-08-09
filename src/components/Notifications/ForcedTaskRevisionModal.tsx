import { SystemDateInput } from '../ui/SystemDateInput';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { CheckCircle2, Clock3, Download, ExternalLink, FileText, Loader2, TimerReset, XCircle } from 'lucide-react';
import { apiService, type UserNotificationDto } from '../../services/api';
import { emitUserNotificationsChanged, USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';
import { parseUserNotificationMetadata } from '../../utils/userNotificationMetadata';
import { extractDisplayErrorMessage, isHttpErrorStatus } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone, fromTimeZoneDateTimeInput, toTimeZoneDateTimeInput } from '../../utils/timeZoneDate';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_SETTING_KEY } from '../../utils/timeZoneOptions';
import type {
  TaskJournalDeadlineChangeRequest,
  TaskJournalFile,
  TaskJournalItem,
  TaskJournalParticipant,
} from '../../utils/taskJournalStore';
import {
  FORCED_TASK_SNOOZE_OPTIONS,
  getForcedTaskApprovalSnoozeKey,
  getForcedTaskDeadlineChangeSnoozeKey,
  getForcedTaskNotificationSnoozeKey,
  isForcedTaskActionSnoozed,
  shareForcedTaskSnooze,
  snoozeForcedTaskActions,
} from '../../utils/forcedTaskActionSnoozes';

type ForcedTaskRevisionModalProps = {
  onOpenTask?: (taskId: number) => void;
};

type ForcedTaskDecisionKind = 'revision' | 'completedApproval' | 'deadlineChangeRequest' | 'approvalRequest';
type ForcedTaskQueueAction = 'acknowledge' | 'deadlineChangeDecision' | 'approvalDecision';

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
const FORCED_TASK_OPEN_SNOOZE_MINUTES = 10;

function formatFileSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 Б';
  if (bytes < 1024) return `${bytes} Б`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} КБ`;
  return `${(bytes / 1024 / 1024).toFixed(1)} МБ`;
}

function downloadBlob(fileName: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.click();
  URL.revokeObjectURL(url);
}

function getLatestTaskFiles(files: TaskJournalFile[]): TaskJournalFile[] {
  const latestByDocument = new Map<string, TaskJournalFile>();
  files.forEach((file) => {
    const current = latestByDocument.get(file.documentKey);
    if (!current || file.version > current.version || (file.version === current.version && file.id > current.id)) {
      latestByDocument.set(file.documentKey, file);
    }
  });

  return [...latestByDocument.values()]
    .sort((left, right) => Date.parse(right.uploadedAt) - Date.parse(left.uploadedAt) || right.id - left.id);
}

function SnoozeMenu({
  disabled,
  onSnooze,
}: {
  disabled?: boolean;
  onSnooze: (minutes: number) => void;
}) {
  return (
    <details className="relative">
      <summary
        className={`inline-flex w-fit cursor-pointer list-none items-center gap-1.5 rounded-full border border-slate-200 bg-white px-3 py-1 text-xs font-semibold text-slate-700 shadow-sm transition hover:bg-slate-50 ${
          disabled ? 'pointer-events-none opacity-60' : ''
        }`}
      >
        <Clock3 className="h-3.5 w-3.5" />
        Отложить
      </summary>
      <div className="absolute right-0 z-10 mt-2 w-36 overflow-hidden rounded-xl border border-slate-200 bg-white p-1 shadow-xl">
        {FORCED_TASK_SNOOZE_OPTIONS.map((option) => (
          <button
            key={option.minutes}
            type="button"
            onClick={() => onSnooze(option.minutes)}
            disabled={disabled}
            className="block w-full rounded-lg px-3 py-2 text-left text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {option.label}
          </button>
        ))}
      </div>
    </details>
  );
}

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

function getApprovalParticipantId(notification: UserNotificationDto): number | null {
  const metadata = getNotificationMetadata(notification);
  return readPositiveInteger(metadata.currentApproverId);
}

function getDecisionKind(notification: UserNotificationDto): ForcedTaskDecisionKind | null {
  if (notification.type === 'taskDeadlineChangeRequested') {
    return notification.taskId && getDeadlineChangeRequestId(notification) ? 'deadlineChangeRequest' : null;
  }

  if (notification.type === 'taskApprovalRequested') {
    return notification.taskId ? 'approvalRequest' : null;
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
    type: 'taskApprovalRequested',
    action: 'approvalDecision',
    resolveDecisionKind: getDecisionKind,
  },
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

  if (notification.forcedModalEnabled === false) {
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

function getQueueItemActionSnoozeKey(item: ForcedTaskQueueItem): string | null {
  const taskId = item.notification.taskId;
  if (!taskId) {
    return null;
  }

  if (item.decisionKind === 'approvalRequest') {
    return getForcedTaskApprovalSnoozeKey(taskId);
  }

  if (item.decisionKind === 'deadlineChangeRequest') {
    const requestId = getDeadlineChangeRequestId(item.notification);
    return requestId ? getForcedTaskDeadlineChangeSnoozeKey(taskId, requestId) : null;
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
    .replace(/^Запрос переноса срока:\s*/i, '')
    .replace(/^Задача возвращена на доработку:\s*/i, '')
    .replace(/^Задача согласована и завершена:\s*/i, '')
    .replace(/^Задача согласована:\s*/i, '')
    .trim() || title;
}

function getOrderedApprovers(task: TaskJournalItem): TaskJournalParticipant[] {
  return (task.participants ?? [])
    .filter((participant) => participant.role === 'approver')
    .slice()
    .sort((left, right) => (left.sortOrder ?? 0) - (right.sortOrder ?? 0) || (left.id ?? 0) - (right.id ?? 0));
}

function getCurrentApprovalParticipant(task: TaskJournalItem, notification: UserNotificationDto): TaskJournalParticipant | null {
  const participantId = getApprovalParticipantId(notification);
  const approvers = getOrderedApprovers(task);
  if (participantId) {
    const notificationParticipant = approvers.find((participant) => participant.id === participantId);
    if (notificationParticipant) {
      return notificationParticipant;
    }
  }

  return approvers.find((participant) => participant.approvalStatus !== 'approved') ?? null;
}

function getParticipantDisplayName(participant?: TaskJournalParticipant | null): string {
  return participant?.displayName?.trim() || participant?.name?.trim() || participant?.email?.trim() || '';
}

function isFinalApprovalParticipant(task: TaskJournalItem, participant?: TaskJournalParticipant | null): boolean {
  if (!participant) return false;
  const approvers = getOrderedApprovers(task);
  return approvers.length > 0 && approvers.every((item) => item.id === participant.id || item.approvalStatus === 'approved');
}

function getDefaultRevisionDueAtInput(task: TaskJournalItem, timeZoneId: string): string {
  const currentDueAt = new Date(task.dueAt);
  const now = new Date();
  if (!Number.isNaN(currentDueAt.getTime()) && currentDueAt.getTime() > now.getTime()) {
    return toTimeZoneDateTimeInput(currentDueAt.toISOString(), timeZoneId);
  }

  const nextDueAt = new Date(now);
  nextDueAt.setDate(nextDueAt.getDate() + 1);
  nextDueAt.setMinutes(0, 0, 0);
  return toTimeZoneDateTimeInput(nextDueAt.toISOString(), timeZoneId);
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
  const [deadlineChangeRequest, setDeadlineChangeRequest] = useState<TaskJournalDeadlineChangeRequest | null>(null);
  const [deadlineChangeRequestLoading, setDeadlineChangeRequestLoading] = useState(false);
  const [deadlineChangeSaving, setDeadlineChangeSaving] = useState<'approve' | 'reject' | null>(null);
  const [approvalTask, setApprovalTask] = useState<TaskJournalItem | null>(null);
  const [approvalTaskLoading, setApprovalTaskLoading] = useState(false);
  const [approvalFiles, setApprovalFiles] = useState<TaskJournalFile[]>([]);
  const [approvalFilesLoading, setApprovalFilesLoading] = useState(false);
  const [approvalFilesError, setApprovalFilesError] = useState<string | null>(null);
  const [approvalFileDownloadingId, setApprovalFileDownloadingId] = useState<number | null>(null);
  const [approvalRevisionText, setApprovalRevisionText] = useState('');
  const [approvalRevisionDueAt, setApprovalRevisionDueAt] = useState('');
  const [approvalError, setApprovalError] = useState<string | null>(null);
  const [approvalSaving, setApprovalSaving] = useState<'approve' | 'approveComplete' | 'approveReturn' | 'revision' | null>(null);
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
          if (!queueItem) {
            return;
          }

          const notificationKey = getForcedTaskNotificationSnoozeKey(notification.id);
          const actionKey = getQueueItemActionSnoozeKey(queueItem);
          const notificationSnoozed = isForcedTaskActionSnoozed(notificationKey);
          if (notificationSnoozed) {
            shareForcedTaskSnooze(notificationKey, actionKey);
          }
          if (notificationSnoozed || (actionKey && isForcedTaskActionSnoozed(actionKey))) {
            return;
          }

          nextItems.set(notification.id, queueItem);
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
  const approvalLatestFiles = useMemo(() => getLatestTaskFiles(approvalFiles), [approvalFiles]);
  const currentMetadata = useMemo(() => current ? getNotificationMetadata(current) : {}, [current]);
  const currentDeadlineChangeRequestId = readPositiveInteger(currentMetadata.deadlineChangeRequestId);
  const currentDeadlineChangeTaskId = current?.taskId ?? null;
  const notificationRequestedDueAt = readString(currentMetadata.requestedDueAt);

  const finishNotificationById = useCallback(async (notificationId: number, reason: string) => {
    await apiService.markUserNotificationRead(notificationId);
    acknowledgedIdsRef.current.add(notificationId);
    setItems((previous) => previous.filter((item) => item.notification.id !== notificationId));
    emitUserNotificationsChanged({
      source: 'system',
      reason,
      unreadDelta: -1,
      notificationId,
    });
  }, []);

  useEffect(() => {
    if (decisionKind !== 'deadlineChangeRequest') {
      setDeadlineChangeRequest(null);
      setDeadlineChangeRequestLoading(false);
      return;
    }

    let cancelled = false;
    setDeadlineChangeRequest(null);
    setDeadlineChangeDueAt(notificationRequestedDueAt ? toTimeZoneDateTimeInput(notificationRequestedDueAt, timeZoneId) : '');
    setDeadlineChangeComment('');
    setDeadlineChangeError(null);
    setDeadlineChangeSaving(null);

    if (!currentDeadlineChangeTaskId || !currentDeadlineChangeRequestId) {
      setDeadlineChangeRequestLoading(false);
      return;
    }

    setDeadlineChangeRequestLoading(true);
    apiService.getTaskJournalDeadlineChangeRequest(currentDeadlineChangeTaskId, currentDeadlineChangeRequestId)
      .then(async (persistedRequest) => {
        if (cancelled) return;

        if (!persistedRequest || persistedRequest.status.toLowerCase() !== 'pending') {
          if (current?.id) {
            await finishNotificationById(current.id, 'forced-task-deadline-change-processed');
          }
          return;
        }

        setDeadlineChangeRequest(persistedRequest);
        if (persistedRequest?.requestedDueAt) {
          setDeadlineChangeDueAt(toTimeZoneDateTimeInput(persistedRequest.requestedDueAt, timeZoneId));
        }
      })
      .catch(async (error) => {
        if (cancelled) return;

        if (isHttpErrorStatus(error, 404) && current?.id) {
          try {
            await finishNotificationById(current.id, 'forced-task-deadline-change-unavailable');
          } catch (acknowledgeError) {
            if (!cancelled) {
              console.error('Failed to close an unavailable deadline change request notification.', acknowledgeError);
            }
          }
          return;
        }

        console.error('Failed to load the deadline change request.', error);
      })
      .finally(() => {
        if (!cancelled) {
          setDeadlineChangeRequestLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [
    current?.id,
    currentDeadlineChangeRequestId,
    currentDeadlineChangeTaskId,
    decisionKind,
    finishNotificationById,
    notificationRequestedDueAt,
    timeZoneId,
  ]);

  useEffect(() => {
    if (!current || decisionKind !== 'approvalRequest' || !current.taskId) {
      setApprovalTask(null);
      setApprovalTaskLoading(false);
      setApprovalFiles([]);
      setApprovalFilesLoading(false);
      setApprovalFilesError(null);
      setApprovalFileDownloadingId(null);
      setApprovalRevisionText('');
      setApprovalRevisionDueAt('');
      setApprovalError(null);
      setApprovalSaving(null);
      return;
    }

    let cancelled = false;
    setApprovalTaskLoading(true);
    setApprovalFilesLoading(true);
    setApprovalTask(null);
    setApprovalFiles([]);
    setApprovalFilesError(null);
    setApprovalFileDownloadingId(null);
    setApprovalRevisionText('');
    setApprovalRevisionDueAt('');
    setApprovalError(null);
    setApprovalSaving(null);

    Promise.allSettled([
      apiService.getTaskJournalItem(current.taskId),
      apiService.getTaskJournalFiles(current.taskId, { latestOnly: true }),
    ])
      .then(([taskResult, filesResult]) => {
        if (cancelled) {
          return;
        }

        if (taskResult.status === 'fulfilled') {
          setApprovalTask(taskResult.value);
          setApprovalRevisionDueAt(getDefaultRevisionDueAtInput(taskResult.value, timeZoneId));
        } else {
          setApprovalError(extractDisplayErrorMessage(taskResult.reason, 'Не удалось загрузить задачу для согласования.'));
        }

        if (filesResult.status === 'fulfilled') {
          setApprovalFiles(filesResult.value);
        } else {
          setApprovalFilesError(extractDisplayErrorMessage(filesResult.reason, 'Не удалось загрузить вложения задачи.'));
        }
      })
      .finally(() => {
        if (!cancelled) {
          setApprovalTaskLoading(false);
          setApprovalFilesLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [current?.id, current?.taskId, current, decisionKind, timeZoneId]);

  const finishCurrentNotification = async (reason: string) => {
    if (!current) return;
    await finishNotificationById(current.id, reason);
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

  const snoozeCurrent = (minutes: number) => {
    if (!current) return;

    snoozeForcedTaskActions([
      getForcedTaskNotificationSnoozeKey(current.id),
      currentItem ? getQueueItemActionSnoozeKey(currentItem) : null,
    ], minutes);
    setItems((previous) => previous.filter((item) => item.notification.id !== current.id));
    emitUserNotificationsChanged({
      source: 'system',
      reason: 'forced-task-action-snoozed',
      notificationId: current.id,
    });
  };

  const openCurrentTask = () => {
    if (!current?.taskId || !onOpenTask || !currentItem) {
      return;
    }

    if (currentItem.rule.action === 'acknowledge') {
      void acknowledge(true);
      return;
    }

    snoozeCurrent(FORCED_TASK_OPEN_SNOOZE_MINUTES);
    onOpenTask(current.taskId);
  };

  const downloadApprovalFile = async (file: TaskJournalFile) => {
    const taskId = current?.taskId;
    if (!taskId || approvalFileDownloadingId !== null) {
      return;
    }

    setApprovalFileDownloadingId(file.id);
    setApprovalFilesError(null);
    try {
      const response = await apiService.downloadTaskJournalFile(taskId, file.id);
      downloadBlob(response.fileName || file.originalFileName, response.blob);
    } catch (error) {
      setApprovalFilesError(extractDisplayErrorMessage(error, 'Не удалось скачать вложение задачи.'));
    } finally {
      setApprovalFileDownloadingId(null);
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

  const approveTaskApprovalRequest = async (finalAction?: 'complete' | 'returnToAssignee') => {
    if (!current || approvalSaving) return;

    const taskId = current.taskId;
    if (!taskId) {
      setApprovalError('Не удалось определить задачу для согласования.');
      return;
    }

    const participantId = approvalTask ? getCurrentApprovalParticipant(approvalTask, current)?.id : getApprovalParticipantId(current) ?? undefined;
    const savingKey = finalAction === 'complete'
      ? 'approveComplete'
      : finalAction === 'returnToAssignee'
        ? 'approveReturn'
        : 'approve';
    setApprovalSaving(savingKey);
    setApprovalError(null);
    try {
      await apiService.updateTaskJournalApproval(
        taskId,
        'approved',
        approvalTask?.result?.trim() || undefined,
        participantId ?? undefined,
        finalAction,
      );
      await finishCurrentNotification('forced-task-approval-approved');
    } catch (error) {
      setApprovalError(extractDisplayErrorMessage(error, 'Не удалось согласовать задачу.'));
    } finally {
      setApprovalSaving(null);
    }
  };

  const returnTaskApprovalToRevision = async () => {
    if (!current || approvalSaving) return;

    const taskId = current.taskId;
    if (!taskId) {
      setApprovalError('Не удалось определить задачу для согласования.');
      return;
    }

    const resolution = approvalRevisionText.trim();
    if (!resolution) {
      setApprovalError('Укажите, что нужно исправить перед возвратом задачи на доработку.');
      return;
    }

    const participantId = approvalTask ? getCurrentApprovalParticipant(approvalTask, current)?.id : getApprovalParticipantId(current) ?? undefined;
    const revisionDueAt = approvalRevisionDueAt ? fromTimeZoneDateTimeInput(approvalRevisionDueAt, timeZoneId) : undefined;
    setApprovalSaving('revision');
    setApprovalError(null);
    try {
      await apiService.updateTaskJournalApproval(
        taskId,
        'revisionRequested',
        resolution,
        participantId ?? undefined,
        undefined,
        revisionDueAt,
      );
      await finishCurrentNotification('forced-task-approval-revision');
    } catch (error) {
      setApprovalError(extractDisplayErrorMessage(error, 'Не удалось вернуть задачу на доработку.'));
    } finally {
      setApprovalSaving(null);
    }
  };

  if (!current || !decisionKind) {
    return null;
  }

  if (currentItem?.rule.action === 'approvalDecision') {
    const participant = approvalTask ? getCurrentApprovalParticipant(approvalTask, current) : null;
    const resultText = approvalTask?.result?.trim() || current.bodyPreview || 'Результат по задаче не заполнен.';
    const isFinalApprover = approvalTask ? isFinalApprovalParticipant(approvalTask, participant) : false;
    const queueCountText = items.length > 1 ? `Еще обязательных уведомлений: ${items.length - 1}` : null;
    const isSaving = Boolean(approvalSaving);

    return (
      <div className="fixed inset-0 z-[80] flex items-start justify-center overflow-y-auto bg-slate-950/55 px-3 py-4 backdrop-blur-sm sm:items-center sm:px-4 sm:py-6" role="dialog" aria-modal="true" aria-labelledby="forced-task-approval-title" data-testid="forced-task-approval-prompt">
        <div className="max-h-[calc(100vh-2rem)] w-full max-w-[min(48rem,calc(100vw-1.5rem))] overflow-y-auto rounded-2xl border border-emerald-200 bg-white p-4 shadow-2xl sm:p-6">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
            <div className="min-w-0">
              <p className="text-xs font-semibold uppercase tracking-[0.16em] text-emerald-700">Согласование задачи</p>
              <h2 id="forced-task-approval-title" className="mt-2 text-xl font-semibold text-slate-950">
                У вас задача на согласовании
              </h2>
              {queueCountText ? (
                <p className="mt-1 text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">
                  {queueCountText}
                </p>
              ) : null}
              <p className="mt-2 line-clamp-2 text-sm text-slate-600">{approvalTask?.title || getTaskTitle(current)}</p>
            </div>
            <div className="flex shrink-0 flex-col items-start gap-2 sm:items-end">
              <span className="inline-flex w-fit items-center gap-1.5 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-xs font-semibold text-emerald-800">
                <CheckCircle2 className="h-3.5 w-3.5" />
                Требует решения
              </span>
              <SnoozeMenu disabled={isSaving || approvalTaskLoading} onSnooze={snoozeCurrent} />
            </div>
          </div>

          <div className="mt-5 grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-700 sm:grid-cols-2">
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Согласующий</div>
              <div className="mt-1 font-semibold text-slate-900">{getParticipantDisplayName(participant) || 'Не указан'}</div>
            </div>
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Исполнитель</div>
              <div className="mt-1 font-semibold text-slate-900">{approvalTask?.assigneeName || 'Не указан'}</div>
            </div>
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Постановщик</div>
              <div className="mt-1 font-medium">{approvalTask?.authorName || 'Не указан'}</div>
            </div>
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Срок задачи</div>
              <div className="mt-1 font-medium">{approvalTask?.dueAt ? formatDateTimeInTimeZone(approvalTask.dueAt, timeZoneId) : 'Не указан'}</div>
            </div>
          </div>

          {approvalTaskLoading ? (
            <div className="mt-4 flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 py-3 text-sm text-slate-600">
              <Loader2 className="h-4 w-4 animate-spin" />
              Загружаем данные задачи...
            </div>
          ) : null}

          {approvalTask?.description ? (
            <div className="mt-4 rounded-xl border border-slate-200 bg-white px-3 py-3 text-sm leading-6 text-slate-700">
              <div className="mb-1 text-xs font-semibold uppercase text-slate-400">Описание задачи</div>
              <div className="whitespace-pre-wrap">{approvalTask.description}</div>
            </div>
          ) : null}

          <div className="mt-4 rounded-xl border border-emerald-200 bg-emerald-50 px-3 py-3 text-sm leading-6 text-emerald-950">
            <div className="mb-1 text-xs font-semibold uppercase text-emerald-700">Результат исполнителя</div>
            <div className="whitespace-pre-wrap">{resultText}</div>
          </div>

          <section className="mt-4 rounded-xl border border-slate-200 bg-white px-3 py-3" aria-labelledby="forced-task-approval-files-title">
            <div className="flex items-center justify-between gap-3">
              <div id="forced-task-approval-files-title" className="flex items-center gap-2 text-sm font-semibold text-slate-700">
                <FileText className="h-4 w-4 text-slate-500" />
                Вложения к задаче
              </div>
              {!approvalFilesLoading ? (
                <span className="text-xs font-medium text-slate-400">{approvalLatestFiles.length}</span>
              ) : null}
            </div>
            {approvalFilesLoading ? (
              <div className="mt-3 flex items-center gap-2 text-sm text-slate-500">
                <Loader2 className="h-4 w-4 animate-spin" />
                Загружаем вложения...
              </div>
            ) : approvalLatestFiles.length > 0 ? (
              <div className="mt-3 space-y-2">
                {approvalLatestFiles.map((file) => (
                  <div key={file.id} className="flex items-center gap-3 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <span className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-white text-slate-600 ring-1 ring-slate-200">
                      <FileText className="h-4 w-4" />
                    </span>
                    <div className="min-w-0 flex-1">
                      <div className="truncate text-sm font-semibold text-slate-800" title={file.originalFileName}>
                        {file.originalFileName}
                      </div>
                      <div className="text-xs text-slate-500">
                        v{file.version} · {formatFileSize(file.sizeBytes)}
                      </div>
                    </div>
                    <button
                      type="button"
                      onClick={() => void downloadApprovalFile(file)}
                      disabled={approvalFileDownloadingId !== null}
                      className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-slate-200 bg-white text-blue-600 transition hover:bg-blue-50 disabled:cursor-wait disabled:opacity-60"
                      aria-label={`Скачать файл ${file.originalFileName}`}
                      title="Скачать файл"
                    >
                      {approvalFileDownloadingId === file.id
                        ? <Loader2 className="h-4 w-4 animate-spin" />
                        : <Download className="h-4 w-4" />}
                    </button>
                  </div>
                ))}
              </div>
            ) : (
              <div className="mt-3 text-sm text-slate-500">К задаче не приложены файлы.</div>
            )}
            {approvalFilesError ? <p className="mt-2 text-sm font-semibold text-rose-600">{approvalFilesError}</p> : null}
          </section>

          <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="forced-task-approval-revision-due-at">
            Срок доработки
          </label>
          <SystemDateInput
            id="forced-task-approval-revision-due-at"
            type="datetime-local"
            value={approvalRevisionDueAt}
            onChange={(event) => {
              setApprovalRevisionDueAt(event.target.value);
              if (approvalError) setApprovalError(null);
            }}
            className="mt-2 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50"
            disabled={isSaving || approvalTaskLoading}
            aria-label="Срок доработки"
          />
          <p className="mt-2 text-sm text-slate-500">
            Поле используется только при возврате на доработку. Если задача согласована, срок доработки не применяется.
          </p>

          <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="forced-task-approval-revision-comment">
            Комментарий для возврата на доработку
          </label>
          <textarea
            id="forced-task-approval-revision-comment"
            value={approvalRevisionText}
            onChange={(event) => {
              setApprovalRevisionText(event.target.value);
              if (approvalError) setApprovalError(null);
            }}
            className="mt-2 block w-full min-h-[104px] resize-y rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm leading-6 text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50"
            rows={4}
            placeholder="Если задача требует правок, укажите что именно нужно исправить."
            disabled={isSaving || approvalTaskLoading}
          />
          {approvalError ? <p className="mt-2 text-sm font-semibold text-rose-600">{approvalError}</p> : null}

          <div className="mt-5 flex flex-col-reverse gap-3 sm:flex-row sm:items-center sm:justify-end">
            {current.taskId && onOpenTask ? (
              <button
                type="button"
                onClick={openCurrentTask}
                disabled={isSaving}
                className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60 sm:w-auto"
              >
                <ExternalLink className="h-4 w-4" />
                Открыть задачу
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => void returnTaskApprovalToRevision()}
              disabled={isSaving || approvalTaskLoading}
              className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-orange-200 bg-white px-4 py-2.5 text-sm font-semibold text-orange-700 transition hover:bg-orange-50 disabled:cursor-not-allowed disabled:opacity-60 sm:w-auto"
            >
              {approvalSaving === 'revision' ? <Loader2 className="h-4 w-4 animate-spin" /> : <XCircle className="h-4 w-4" />}
              Вернуть на доработку
            </button>
            {isFinalApprover ? (
              <>
                <button
                  type="button"
                  onClick={() => void approveTaskApprovalRequest('returnToAssignee')}
                  disabled={isSaving || approvalTaskLoading}
                  className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-blue-200 bg-blue-50 px-4 py-2.5 text-sm font-semibold text-blue-700 transition hover:bg-blue-100 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
                >
                  {approvalSaving === 'approveReturn' ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
                  Согласовать и отправить исполнителю
                </button>
                <button
                  type="button"
                  onClick={() => void approveTaskApprovalRequest('complete')}
                  disabled={isSaving || approvalTaskLoading}
                  className="inline-flex w-full items-center justify-center gap-2 rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
                >
                  {approvalSaving === 'approveComplete' ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
                  Согласовать и завершить
                </button>
              </>
            ) : (
              <button
                type="button"
                onClick={() => void approveTaskApprovalRequest()}
                disabled={isSaving || approvalTaskLoading}
                className="inline-flex w-full items-center justify-center gap-2 rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
              >
                {approvalSaving === 'approve' ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
                Согласовать
              </button>
            )}
          </div>
        </div>
      </div>
    );
  }

  if (currentItem?.rule.action === 'deadlineChangeDecision') {
    const requestedDueAt = deadlineChangeRequest?.requestedDueAt || notificationRequestedDueAt;
    const currentDueAt = deadlineChangeRequest?.currentDueAt || readString(currentMetadata.currentDueAt);
    const formattedRequestedDueAt = requestedDueAt ? formatDateTimeInTimeZone(requestedDueAt, timeZoneId) : 'Не указано';
    const formattedCurrentDueAt = currentDueAt ? formatDateTimeInTimeZone(currentDueAt, timeZoneId) : 'Не указано';
    const requestedByName = deadlineChangeRequest?.requestedByName || readString(currentMetadata.requestedByName) || 'Не указано';
    const reviewerName = deadlineChangeRequest?.reviewerName || readString(currentMetadata.reviewerName) || 'Не указано';
    const reason = deadlineChangeRequest?.reason
      || readString(currentMetadata.reason)
      || current.bodyPreview
      || 'Причина переноса срока не указана.';
    const queueCountText = items.length > 1 ? `Еще обязательных уведомлений: ${items.length - 1}` : null;
    const isSaving = Boolean(deadlineChangeSaving) || deadlineChangeRequestLoading;

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
            <div className="flex shrink-0 flex-col items-start gap-2 sm:items-end">
              <span className="inline-flex w-fit items-center gap-1.5 rounded-full border border-amber-200 bg-amber-50 px-3 py-1 text-xs font-semibold text-amber-800">
                <TimerReset className="h-3.5 w-3.5" />
                Требует решения
              </span>
              <SnoozeMenu disabled={isSaving} onSnooze={snoozeCurrent} />
            </div>
          </div>

          <div className="mt-5 grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-700 sm:grid-cols-2">
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Запросил</div>
              <div className="mt-1 font-semibold text-slate-900">{requestedByName}</div>
            </div>
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Постановщик</div>
              <div className="mt-1 font-semibold text-slate-900">{reviewerName}</div>
            </div>
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Текущий срок</div>
              <div className="mt-1 font-medium text-slate-900">{formattedCurrentDueAt}</div>
            </div>
            <div>
              <div className="text-xs font-semibold uppercase text-slate-400">Запрошенный срок</div>
              <div className="mt-1 font-semibold text-blue-700">{formattedRequestedDueAt}</div>
            </div>
          </div>

          <div className="mt-4 rounded-xl border border-amber-200 bg-amber-50 px-3 py-3 text-sm leading-6 text-amber-950">
            <span className="font-semibold">Причина переноса:</span> {reason}
          </div>

          <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="forced-deadline-change-due-at">
            Новый срок задачи
          </label>
          <SystemDateInput
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
            {deadlineChangeRequestLoading
              ? 'Загружаем дату из сохраненного запроса...'
              : 'По умолчанию указана дата, которую запросил исполнитель. Перед согласованием ее можно изменить.'}
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
                onClick={openCurrentTask}
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
          <SnoozeMenu disabled={acknowledgingId === current.id} onSnooze={snoozeCurrent} />
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
              onClick={openCurrentTask}
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
