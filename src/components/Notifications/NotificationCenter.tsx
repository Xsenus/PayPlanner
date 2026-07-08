import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Archive,
  CalendarDays,
  Bell,
  CheckCheck,
  CheckCircle2,
  CircleDollarSign,
  Clock,
  ExternalLink,
  Filter,
  Gavel,
  Inbox,
  ListTodo,
  Mail,
  MessageSquare,
  Monitor,
  Moon,
  RefreshCw,
  RotateCcw,
  Search,
  Send,
  Settings2,
  Save,
  Smartphone,
  StickyNote,
  Trash2,
  type LucideIcon,
} from 'lucide-react';
import {
  apiService,
  type PagedResult,
  type UserNotificationDto,
  type UserNotificationsQuery,
  type UserNotificationTypeDto,
} from '../../services/api';
import type { Client } from '../../types';
import { useToast } from '../../hooks/useToast';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';
import { emitUserNotificationsChanged, USER_NOTIFICATIONS_EVENT, type UserNotificationsChangedDetail } from '../../utils/userNotifications';
import {
  DEFAULT_USER_NOTIFICATION_TYPE_PREFERENCES,
  USER_NOTIFICATION_TYPE_PREFERENCES_SETTING_KEY,
  isUserNotificationChannelEnabled,
  isUserNotificationTypeEnabled,
  parseUserNotificationTypePreferences,
  serializeUserNotificationTypePreferences,
  toggleUserNotificationChannelPreference,
  toggleUserNotificationTypePreference,
  type UserNotificationPreferenceChannel,
  type UserNotificationTypePreferences,
} from '../../utils/userNotificationPreferences';
import {
  DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB,
  USER_NOTIFICATION_DO_NOT_DISTURB_SETTING_KEY,
  parseUserNotificationDoNotDisturb,
  serializeUserNotificationDoNotDisturb,
  type UserNotificationDoNotDisturbSettings,
} from '../../utils/userNotificationDoNotDisturb';
import { WebPushDevicePanel } from './WebPushDevicePanel';
import { getClientCaseDocumentIdFromNotification } from '../../utils/userNotificationMetadata';

type NotificationStatusFilter = NonNullable<UserNotificationsQuery['status']>;
type NotificationTaskRoleFilter = 'all' | 'assignee' | 'author' | 'observer' | 'approver';
type NotificationDomainFilter = 'all' | 'task' | 'calendar' | 'note' | 'finance' | 'case' | 'system';

type NotificationCenterProps = {
  onOpenTask?: (taskId: number) => void;
  onOpenCase?: (caseId: number) => void;
  onOpenCaseDocument?: (caseId: number, documentId?: number) => void;
  onOpenPayment?: (paymentId: number) => void;
  onOpenInvoice?: (invoiceId: number) => void;
  onOpenAct?: (actId: number) => void;
  onOpenContract?: (contractId: number) => void;
};

const STATUS_FILTERS: Array<{ value: NotificationStatusFilter; label: string }> = [
  { value: 'active', label: 'Активные' },
  { value: 'unread', label: 'Непрочитанные' },
  { value: 'read', label: 'Прочитанные' },
  { value: 'archived', label: 'Архив' },
  { value: 'all', label: 'Все' },
];

const TASK_ROLE_FILTERS: Array<{ value: NotificationTaskRoleFilter; label: string }> = [
  { value: 'all', label: 'Все роли' },
  { value: 'assignee', label: 'Исполнитель' },
  { value: 'author', label: 'Постановщик' },
  { value: 'observer', label: 'Наблюдатель' },
  { value: 'approver', label: 'Согласующий' },
];

const DOMAIN_FILTERS: Array<{ value: NotificationDomainFilter; label: string; Icon: LucideIcon }> = [
  { value: 'all', label: 'Все', Icon: Inbox },
  { value: 'task', label: 'Задачи', Icon: ListTodo },
  { value: 'calendar', label: 'Календарь', Icon: CalendarDays },
  { value: 'note', label: 'Заметки', Icon: StickyNote },
  { value: 'finance', label: 'Финансы', Icon: CircleDollarSign },
  { value: 'case', label: 'Дела и суды', Icon: Gavel },
  { value: 'system', label: 'Система', Icon: Monitor },
];

const CHANNEL_OPTIONS: Array<{ value: UserNotificationPreferenceChannel; label: string; Icon: LucideIcon }> = [
  { value: 'inApp', label: 'Центр', Icon: Monitor },
  { value: 'toast', label: 'Всплывающие', Icon: Bell },
  { value: 'webPush', label: 'Push', Icon: Smartphone },
  { value: 'email', label: 'Email', Icon: Mail },
  { value: 'telegram', label: 'Telegram', Icon: Send },
  { value: 'sms', label: 'SMS', Icon: MessageSquare },
];

const TYPE_DOMAIN_ORDER = ['task', 'calendar', 'note', 'payment', 'invoice', 'act', 'contract', 'case', 'court', 'system'];

function groupNotificationTypesByDomain(types: UserNotificationTypeDto[]) {
  const order = new Map(TYPE_DOMAIN_ORDER.map((key, index) => [key, index]));
  const groups = new Map<string, { key: string; label: string; items: UserNotificationTypeDto[] }>();

  for (const item of types) {
    const key = item.domain || 'system';
    const group = groups.get(key) ?? { key, label: item.domainLabel || 'Система', items: [] };
    group.items.push(item);
    groups.set(key, group);
  }

  return Array.from(groups.values())
    .sort((left, right) => (order.get(left.key) ?? 999) - (order.get(right.key) ?? 999));
}

function notificationPriorityLabel(priority?: string) {
  switch ((priority ?? '').toLowerCase()) {
    case 'critical':
      return 'Критичный приоритет';
    case 'high':
      return 'Высокий приоритет';
    case 'silent':
      return 'Тихое уведомление';
    default:
      return 'Обычный приоритет';
  }
}

function notificationPriorityBadgeClass(priority?: string) {
  switch ((priority ?? '').toLowerCase()) {
    case 'critical':
      return 'bg-rose-50 text-rose-700';
    case 'high':
      return 'bg-amber-50 text-amber-700';
    case 'silent':
      return 'bg-slate-100 text-slate-500';
    default:
      return 'bg-blue-50 text-blue-700';
  }
}

const EMPTY_PAGE: PagedResult<UserNotificationDto> = {
  items: [],
  total: 0,
  page: 1,
  pageSize: 25,
};

function toneClasses(tone: string, isRead: boolean) {
  if (isRead) {
    return 'bg-slate-300 text-slate-500';
  }

  switch (tone) {
    case 'rose':
      return 'bg-rose-500 text-white';
    case 'amber':
      return 'bg-amber-500 text-white';
    case 'emerald':
      return 'bg-emerald-500 text-white';
    default:
      return 'bg-blue-500 text-white';
  }
}

function formatDateTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
}

export function NotificationCenter({
  onOpenTask,
  onOpenCase,
  onOpenCaseDocument,
  onOpenPayment,
  onOpenInvoice,
  onOpenAct,
  onOpenContract,
}: NotificationCenterProps) {
  const toast = useToast();
  const [itemsPage, setItemsPage] = useState<PagedResult<UserNotificationDto>>(EMPTY_PAGE);
  const [types, setTypes] = useState<UserNotificationTypeDto[]>([]);
  const [status, setStatus] = useState<NotificationStatusFilter>('active');
  const [domain, setDomain] = useState<NotificationDomainFilter>('all');
  const [requiresActionOnly, setRequiresActionOnly] = useState(false);
  const [type, setType] = useState('all');
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [dateFrom, setDateFrom] = useState('');
  const [dateTo, setDateTo] = useState('');
  const [employeeId, setEmployeeId] = useState('all');
  const [clientId, setClientId] = useState('all');
  const [taskRole, setTaskRole] = useState<NotificationTaskRoleFilter>('all');
  const [assignees, setAssignees] = useState<TaskJournalAssignee[]>([]);
  const [clients, setClients] = useState<Client[]>([]);
  const [referenceLoading, setReferenceLoading] = useState(false);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState<number | null>(null);
  const [bulkBusy, setBulkBusy] = useState(false);
  const [selectedIds, setSelectedIds] = useState<Set<number>>(() => new Set());
  const [preferencesOpen, setPreferencesOpen] = useState(false);
  const [preferences, setPreferences] = useState<UserNotificationTypePreferences>(DEFAULT_USER_NOTIFICATION_TYPE_PREFERENCES);
  const [doNotDisturb, setDoNotDisturb] = useState<UserNotificationDoNotDisturbSettings>(DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB);
  const [preferencesLoading, setPreferencesLoading] = useState(false);
  const [preferencesSaving, setPreferencesSaving] = useState(false);
  const pageSelectionRef = useRef<HTMLInputElement | null>(null);

  const totalPages = Math.max(1, Math.ceil(itemsPage.total / itemsPage.pageSize));
  const unreadCount = useMemo(
    () => itemsPage.items.reduce((count, item) => count + (item.isRead ? 0 : 1), 0),
    [itemsPage.items],
  );
  const knownTypeKeys = useMemo(() => types.map((item) => item.key), [types]);
  const typeGroups = useMemo(() => groupNotificationTypesByDomain(types), [types]);
  const enabledTypesCount = useMemo(
    () => types.filter((item) => isUserNotificationTypeEnabled(preferences, item.key)).length,
    [preferences, types],
  );
  const selectedEmployeeId = employeeId === 'all' ? undefined : Number(employeeId);
  const selectedClientId = clientId === 'all' ? undefined : Number(clientId);
  const selectedTaskRole = taskRole === 'all' ? undefined : taskRole;
  const selectedDomain = domain === 'all' ? undefined : domain;
  const currentPageIds = useMemo(() => itemsPage.items.map((item) => item.id), [itemsPage.items]);
  const selectedCount = selectedIds.size;
  const allPageSelected = currentPageIds.length > 0 && currentPageIds.every((id) => selectedIds.has(id));
  const somePageSelected = currentPageIds.some((id) => selectedIds.has(id));
  const hasCustomFilters = Boolean(
    searchInput
      || search
      || domain !== 'all'
      || requiresActionOnly
      || type !== 'all'
      || status !== 'active'
      || dateFrom
      || dateTo
      || selectedEmployeeId
      || selectedClientId
      || selectedTaskRole,
  );

  useEffect(() => {
    let alive = true;
    apiService.getUserNotificationTypes()
      .then((nextTypes) => {
        if (alive) {
          setTypes(nextTypes);
        }
      })
      .catch(() => {
        if (alive) {
          setTypes([]);
        }
      });

    return () => {
      alive = false;
    };
  }, []);

  useEffect(() => {
    let alive = true;
    setReferenceLoading(true);
    Promise.all([
      apiService.getTaskJournalAssignees().catch(() => [] as TaskJournalAssignee[]),
      apiService.getClientsV2({
        page: 1,
        pageSize: 500,
        isActive: true,
        sortBy: 'name',
      }).then((response) => response.items).catch(() => [] as Client[]),
    ])
      .then(([nextAssignees, nextClients]) => {
        if (alive) {
          setAssignees(nextAssignees);
          setClients(nextClients);
        }
      })
      .finally(() => {
        if (alive) {
          setReferenceLoading(false);
        }
      });

    return () => {
      alive = false;
    };
  }, []);

  useEffect(() => {
    if (knownTypeKeys.length === 0) {
      return;
    }

    let alive = true;
    setPreferencesLoading(true);
    Promise.all([
      apiService.getUserSetting(USER_NOTIFICATION_TYPE_PREFERENCES_SETTING_KEY),
      apiService.getUserSetting(USER_NOTIFICATION_DO_NOT_DISTURB_SETTING_KEY),
    ])
      .then(([typeSetting, quietSetting]) => {
        if (alive) {
          setPreferences(parseUserNotificationTypePreferences(typeSetting?.value, knownTypeKeys));
          setDoNotDisturb(parseUserNotificationDoNotDisturb(quietSetting?.value));
        }
      })
      .catch(() => {
        if (alive) {
          setPreferences(DEFAULT_USER_NOTIFICATION_TYPE_PREFERENCES);
          setDoNotDisturb(DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB);
        }
      })
      .finally(() => {
        if (alive) {
          setPreferencesLoading(false);
        }
      });

    return () => {
      alive = false;
    };
  }, [knownTypeKeys]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setSearch(searchInput.trim());
      setPage(1);
    }, 300);

    return () => window.clearTimeout(timer);
  }, [searchInput]);

  const refresh = useCallback(() => {
    let alive = true;
    setLoading(true);
    apiService.getUserNotifications({
      status,
      type: type === 'all' ? undefined : type,
      domain: selectedDomain,
      requiresAction: requiresActionOnly ? true : undefined,
      search,
      dateFrom: dateFrom || undefined,
      dateTo: dateTo || undefined,
      employeeId: selectedEmployeeId,
      clientId: selectedClientId,
      taskRole: selectedTaskRole,
      page,
      pageSize: itemsPage.pageSize || 25,
    })
      .then((nextPage) => {
        if (alive) {
          setItemsPage(nextPage);
        }
      })
      .catch((error) => {
        if (alive) {
          setItemsPage((previous) => ({ ...previous, items: [] }));
          toast.error(error instanceof Error ? error.message : 'Не удалось загрузить уведомления.');
        }
      })
      .finally(() => {
        if (alive) {
          setLoading(false);
        }
      });

    return () => {
      alive = false;
    };
  }, [
    dateFrom,
    dateTo,
    itemsPage.pageSize,
    page,
    search,
    selectedClientId,
    selectedDomain,
    selectedEmployeeId,
    selectedTaskRole,
    requiresActionOnly,
    status,
    toast,
    type,
  ]);

  useEffect(() => refresh(), [refresh]);

  useEffect(() => {
    const handleNotificationsChanged = (event: Event) => {
      const detail = (event as CustomEvent<UserNotificationsChangedDetail>).detail;

      if (detail?.source === 'inbox') {
        return;
      }

      refresh();
    };

    window.addEventListener(USER_NOTIFICATIONS_EVENT, handleNotificationsChanged);
    return () => window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleNotificationsChanged);
  }, [refresh]);

  useEffect(() => {
    if (pageSelectionRef.current) {
      pageSelectionRef.current.indeterminate = somePageSelected && !allPageSelected;
    }
  }, [allPageSelected, somePageSelected]);

  useEffect(() => {
    setSelectedIds((previous) => {
      if (previous.size === 0) {
        return previous;
      }

      const pageIdSet = new Set(currentPageIds);
      const next = new Set([...previous].filter((id) => pageIdSet.has(id)));
      return next.size === previous.size ? previous : next;
    });
  }, [currentPageIds]);

  const updateItem = (nextItem: UserNotificationDto) => {
    setItemsPage((previous) => ({
      ...previous,
      items: previous.items.map((item) => (item.id === nextItem.id ? nextItem : item)),
    }));
    emitUserNotificationsChanged({ source: 'inbox', reason: 'item-updated', notificationId: nextItem.id });
  };

  const removeItem = (id: number) => {
    setItemsPage((previous) => ({
      ...previous,
      items: previous.items.filter((item) => item.id !== id),
      total: Math.max(0, previous.total - 1),
    }));
    setSelectedIds((previous) => {
      if (!previous.has(id)) {
        return previous;
      }

      const next = new Set(previous);
      next.delete(id);
      return next;
    });
    emitUserNotificationsChanged({ source: 'inbox', reason: 'item-removed', notificationId: id });
  };

  const toggleSelection = (id: number, checked: boolean) => {
    setSelectedIds((previous) => {
      const next = new Set(previous);
      if (checked) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
  };

  const togglePageSelection = (checked: boolean) => {
    setSelectedIds((previous) => {
      const next = new Set(previous);
      currentPageIds.forEach((id) => {
        if (checked) {
          next.add(id);
        } else {
          next.delete(id);
        }
      });
      return next;
    });
  };

  const markRead = async (item: UserNotificationDto) => {
    if (item.isRead) {
      return item;
    }

    const updated = await apiService.markUserNotificationRead(item.id);
    updateItem(updated);
    return updated;
  };

  const handleOpenTask = (item: UserNotificationDto) => {
    if (!item.taskId || !onOpenTask) {
      return;
    }

    onOpenTask(item.taskId);

    if (!item.isRead) {
      apiService
        .markUserNotificationRead(item.id, { suppressAuthSessionExpired: true })
        .catch((error) => {
          toast.error(error instanceof Error ? error.message : 'Не удалось отметить уведомление прочитанным.');
        });
    }
  };

  const handleOpenCase = async (item: UserNotificationDto) => {
    if (item.objectType !== 'case' || !item.objectId || (!onOpenCase && !onOpenCaseDocument)) {
      return;
    }

    try {
      setBusyId(item.id);
      await markRead(item);
      const documentId = getClientCaseDocumentIdFromNotification(item);
      if (documentId && onOpenCaseDocument) {
        onOpenCaseDocument(item.objectId, documentId);
      } else {
        onOpenCase?.(item.objectId);
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось открыть дело.');
    } finally {
      setBusyId(null);
    }
  };

  const openLinkedObjectHandlers: Record<string, ((objectId: number) => void) | undefined> = {
    payment: onOpenPayment,
    invoice: onOpenInvoice,
    act: onOpenAct,
    contract: onOpenContract,
  };

  const openLinkedObjectLabels: Record<string, string> = {
    payment: 'К платежу',
    invoice: 'К счету',
    act: 'К акту',
    contract: 'К договору',
  };

  const handleOpenLinkedObject = async (item: UserNotificationDto) => {
    const handler = openLinkedObjectHandlers[item.objectType];
    if (!handler || !item.objectId) {
      return;
    }

    try {
      setBusyId(item.id);
      await markRead(item);
      handler(item.objectId);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось открыть связанный документ.');
    } finally {
      setBusyId(null);
    }
  };

  const handleArchive = async (item: UserNotificationDto) => {
    try {
      setBusyId(item.id);
      const updated = await apiService.archiveUserNotification(item.id);
      if (status === 'archived' || status === 'all') {
        updateItem(updated);
      } else {
        removeItem(item.id);
      }
      toast.success('Уведомление отправлено в архив.');
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось архивировать уведомление.');
    } finally {
      setBusyId(null);
    }
  };

  const handleDelete = async (item: UserNotificationDto) => {
    try {
      setBusyId(item.id);
      await apiService.deleteUserNotification(item.id);
      removeItem(item.id);
      toast.success('Уведомление удалено.');
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось удалить уведомление.');
    } finally {
      setBusyId(null);
    }
  };

  const handleToggleRead = async (item: UserNotificationDto) => {
    try {
      setBusyId(item.id);
      const updated = item.isRead
        ? await apiService.markUserNotificationUnread(item.id)
        : await apiService.markUserNotificationRead(item.id);
      updateItem(updated);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось изменить отметку прочтения.');
    } finally {
      setBusyId(null);
    }
  };

  const handleMarkAllRead = async () => {
    try {
      setBulkBusy(true);
      const result = await apiService.markAllUserNotificationsRead({
        status,
        type: type === 'all' ? undefined : type,
        domain: selectedDomain,
        requiresAction: requiresActionOnly ? true : undefined,
        search,
        dateFrom: dateFrom || undefined,
        dateTo: dateTo || undefined,
        employeeId: selectedEmployeeId,
        clientId: selectedClientId,
        taskRole: selectedTaskRole,
      });
      emitUserNotificationsChanged({ source: 'inbox', reason: 'bulk-read' });
      refresh();
      toast.success(
        result.updatedCount > 0
          ? `Отмечено прочитанными: ${result.updatedCount}.`
          : 'Непрочитанных уведомлений по текущему фильтру нет.',
      );
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось отметить уведомления прочитанными.');
    } finally {
      setBulkBusy(false);
    }
  };

  const handleBulkArchive = async () => {
    const ids = [...selectedIds];
    if (ids.length === 0) return;

    try {
      setBulkBusy(true);
      const result = await apiService.archiveUserNotifications(ids);
      setSelectedIds(new Set());
      emitUserNotificationsChanged({ source: 'inbox', reason: 'bulk-archive' });
      refresh();
      toast.success(
        result.updatedCount > 0
          ? `Отправлено в архив: ${result.updatedCount}.`
          : 'Выбранные уведомления уже недоступны для архивации.',
      );
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось архивировать выбранные уведомления.');
    } finally {
      setBulkBusy(false);
    }
  };

  const handleBulkDelete = async () => {
    const ids = [...selectedIds];
    if (ids.length === 0) return;

    try {
      setBulkBusy(true);
      const result = await apiService.deleteUserNotifications(ids);
      setSelectedIds(new Set());
      emitUserNotificationsChanged({ source: 'inbox', reason: 'bulk-delete' });
      refresh();
      toast.success(
        result.updatedCount > 0
          ? `Удалено уведомлений: ${result.updatedCount}.`
          : 'Выбранные уведомления уже удалены или недоступны.',
      );
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось удалить выбранные уведомления.');
    } finally {
      setBulkBusy(false);
    }
  };

  const handleSavePreferences = async () => {
    try {
      setPreferencesSaving(true);
      await apiService.upsertUserSetting(
        USER_NOTIFICATION_TYPE_PREFERENCES_SETTING_KEY,
        serializeUserNotificationTypePreferences(preferences, knownTypeKeys),
      );
      await apiService.upsertUserSetting(
        USER_NOTIFICATION_DO_NOT_DISTURB_SETTING_KEY,
        serializeUserNotificationDoNotDisturb(doNotDisturb),
      );
      emitUserNotificationsChanged({ source: 'inbox', reason: 'preferences-saved' });
      setPage(1);
      refresh();
      toast.success('Настройки уведомлений сохранены.');
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось сохранить настройки уведомлений.');
    } finally {
      setPreferencesSaving(false);
    }
  };

  const handleResetFilters = () => {
    setStatus('active');
    setDomain('all');
    setRequiresActionOnly(false);
    setType('all');
    setSearchInput('');
    setSearch('');
    setDateFrom('');
    setDateTo('');
    setEmployeeId('all');
    setClientId('all');
    setTaskRole('all');
    setPage(1);
    setSelectedIds(new Set());
  };

  return (
    <div className="space-y-5 p-6">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <div className="text-xs font-semibold uppercase tracking-[0.28em] text-slate-500">Рабочий центр</div>
          <h1 className="mt-2 text-3xl font-semibold text-slate-950">Центр уведомлений</h1>
          <p className="mt-2 max-w-3xl text-sm text-slate-600">
            Журнал событий по задачам: новые сообщения, изменения, напоминания и завершения.
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => void handleMarkAllRead()}
            disabled={loading || bulkBusy || itemsPage.total === 0}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-2 text-sm font-medium text-emerald-700 shadow-sm transition hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <CheckCheck className="h-4 w-4" />
            Прочитать все
          </button>
          <button
            type="button"
            onClick={() => void handleBulkArchive()}
            disabled={loading || bulkBusy || selectedCount === 0}
            aria-label={`В архив выбранные уведомления: ${selectedCount}`}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-4 py-2 text-sm font-medium text-amber-700 shadow-sm transition hover:bg-amber-100 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Archive className="h-4 w-4" />
            В архив
            {selectedCount > 0 ? <span className="rounded-full bg-white px-2 py-0.5 text-xs">{selectedCount}</span> : null}
          </button>
          <button
            type="button"
            onClick={() => void handleBulkDelete()}
            disabled={loading || bulkBusy || selectedCount === 0}
            aria-label={`Удалить выбранные уведомления: ${selectedCount}`}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-700 shadow-sm transition hover:bg-red-100 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Trash2 className="h-4 w-4" />
            Удалить
            {selectedCount > 0 ? <span className="rounded-full bg-white px-2 py-0.5 text-xs">{selectedCount}</span> : null}
          </button>
          <button
            type="button"
            onClick={() => refresh()}
            disabled={loading || bulkBusy}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm transition hover:border-slate-300 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            Обновить
          </button>
          <button
            type="button"
            onClick={() => setPreferencesOpen((value) => !value)}
            disabled={preferencesLoading}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm transition hover:border-slate-300 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Settings2 className="h-4 w-4" />
            Типы
          </button>
        </div>
      </div>

      <WebPushDevicePanel
        showDevices={false}
        title="Не пропускайте срочные уведомления"
        description="Push помогает сразу увидеть новую задачу, перенос срока или важное событие, даже если центр уведомлений закрыт."
        className="border-blue-100 bg-blue-50/40"
      />

      <div className="grid gap-3 md:grid-cols-3">
        <div className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
          <div className="flex items-center justify-between">
            <div className="text-sm text-slate-500">Найдено</div>
            <Inbox className="h-5 w-5 text-blue-500" />
          </div>
          <div className="mt-2 text-3xl font-semibold text-slate-950">{itemsPage.total}</div>
        </div>
        <div className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
          <div className="flex items-center justify-between">
            <div className="text-sm text-slate-500">Непрочитано на странице</div>
            <Bell className="h-5 w-5 text-rose-500" />
          </div>
          <div className="mt-2 text-3xl font-semibold text-slate-950">{unreadCount}</div>
        </div>
        <div className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
          <div className="flex items-center justify-between">
            <div className="text-sm text-slate-500">Страница</div>
            <Filter className="h-5 w-5 text-emerald-500" />
          </div>
          <div className="mt-2 text-3xl font-semibold text-slate-950">{itemsPage.page}/{totalPages}</div>
        </div>
      </div>

      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
        <div className="mb-3 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
          <div className="flex flex-wrap gap-2">
            {DOMAIN_FILTERS.map(({ value, label, Icon }) => {
              const active = domain === value;
              return (
                <button
                  key={value}
                  type="button"
                  onClick={() => {
                    setDomain(value);
                    setPage(1);
                    setSelectedIds(new Set());
                  }}
                  className={`inline-flex h-9 items-center gap-2 rounded-lg border px-3 text-sm font-medium transition ${
                    active
                      ? 'border-blue-500 bg-blue-50 text-blue-700 shadow-sm'
                      : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
                  }`}
                  aria-pressed={active}
                >
                  <Icon className="h-4 w-4" />
                  {label}
                </button>
              );
            })}
          </div>

          <button
            type="button"
            onClick={() => {
              setRequiresActionOnly((value) => !value);
              setPage(1);
              setSelectedIds(new Set());
            }}
            className={`inline-flex h-9 items-center justify-center gap-2 rounded-lg border px-3 text-sm font-medium transition ${
              requiresActionOnly
                ? 'border-rose-500 bg-rose-50 text-rose-700 shadow-sm'
                : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
            }`}
            aria-pressed={requiresActionOnly}
          >
            <Bell className="h-4 w-4" />
            Требуют действия
          </button>
        </div>

        <div className="grid gap-3 lg:grid-cols-[minmax(16rem,1fr)_12rem_14rem]">
          <label className="relative block">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              value={searchInput}
              onChange={(event) => setSearchInput(event.target.value)}
              placeholder="Поиск по уведомлению, задаче или событию"
              className="h-10 w-full rounded-lg border border-slate-200 bg-white pl-9 pr-3 text-sm outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
            />
          </label>

          <select
            value={status}
            onChange={(event) => {
              setStatus(event.target.value as NotificationStatusFilter);
              setPage(1);
            }}
            className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
          >
            {STATUS_FILTERS.map((option) => (
              <option key={option.value} value={option.value}>{option.label}</option>
            ))}
          </select>

          <select
            value={type}
            onChange={(event) => {
              setType(event.target.value);
              setPage(1);
            }}
            className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
          >
            <option value="all">Все типы</option>
            {types.map((item) => (
              <option key={item.key} value={item.key}>{item.label}</option>
            ))}
          </select>
        </div>

        <div className="mt-3 grid gap-3 md:grid-cols-2 xl:grid-cols-[10rem_10rem_minmax(12rem,1fr)_minmax(12rem,1fr)_12rem_auto]">
          <label className="block">
            <span className="mb-1 block text-xs font-semibold uppercase tracking-[0.12em] text-slate-500">С даты</span>
            <input
              type="date"
              value={dateFrom}
              onChange={(event) => {
                setDateFrom(event.target.value);
                setPage(1);
              }}
              className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
            />
          </label>

          <label className="block">
            <span className="mb-1 block text-xs font-semibold uppercase tracking-[0.12em] text-slate-500">По дату</span>
            <input
              type="date"
              value={dateTo}
              onChange={(event) => {
                setDateTo(event.target.value);
                setPage(1);
              }}
              className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
            />
          </label>

          <label className="block">
            <span className="mb-1 block text-xs font-semibold uppercase tracking-[0.12em] text-slate-500">Сотрудник</span>
            <select
              value={employeeId}
              onChange={(event) => {
                setEmployeeId(event.target.value);
                setPage(1);
              }}
              disabled={referenceLoading}
              className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50 disabled:text-slate-400"
            >
              <option value="all">Все сотрудники</option>
              {assignees.map((item) => (
                <option key={item.id} value={item.id}>{item.name || item.email}</option>
              ))}
            </select>
          </label>

          <label className="block">
            <span className="mb-1 block text-xs font-semibold uppercase tracking-[0.12em] text-slate-500">Клиент</span>
            <select
              value={clientId}
              onChange={(event) => {
                setClientId(event.target.value);
                setPage(1);
              }}
              disabled={referenceLoading}
              className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50 disabled:text-slate-400"
            >
              <option value="all">Все клиенты</option>
              {clients.map((item) => (
                <option key={item.id} value={item.id}>{item.name || item.company || `Клиент #${item.id}`}</option>
              ))}
            </select>
          </label>

          <label className="block">
            <span className="mb-1 block text-xs font-semibold uppercase tracking-[0.12em] text-slate-500">Роль</span>
            <select
              value={taskRole}
              onChange={(event) => {
                setTaskRole(event.target.value as NotificationTaskRoleFilter);
                setPage(1);
              }}
              className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
            >
              {TASK_ROLE_FILTERS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>

          <div className="flex items-end">
            <button
              type="button"
              onClick={handleResetFilters}
              disabled={!hasCustomFilters}
              className="inline-flex h-10 w-full items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50 xl:w-auto"
            >
              <RotateCcw className="h-4 w-4" />
              Сбросить
            </button>
          </div>
        </div>
      </section>

      {preferencesOpen ? (
        <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
          <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
            <div>
              <h2 className="text-base font-semibold text-slate-950">Типы уведомлений</h2>
              <p className="mt-1 text-sm text-slate-500">
                Включено {enabledTypesCount} из {types.length}. Отключенные типы не создаются и не отправляются, а каналы можно настроить отдельно.
              </p>
            </div>
            <button
              type="button"
              onClick={() => void handleSavePreferences()}
              disabled={preferencesLoading || preferencesSaving}
              className="inline-flex h-10 items-center justify-center gap-2 rounded-lg bg-slate-950 px-4 text-sm font-medium text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Save className={`h-4 w-4 ${preferencesSaving ? 'animate-pulse' : ''}`} />
              Сохранить
            </button>
          </div>

          <WebPushDevicePanel className="mt-3" autoLoad={preferencesOpen} />

          <div className="mt-3 rounded-lg border border-slate-200 bg-slate-50/70 p-3">
            <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
              <label className="flex cursor-pointer gap-3">
                <input
                  type="checkbox"
                  checked={doNotDisturb.enabled}
                  onChange={(event) => {
                    setDoNotDisturb((previous) => ({ ...previous, enabled: event.target.checked }));
                  }}
                  className="mt-1 h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                <span>
                  <span className="flex items-center gap-2 text-sm font-semibold text-slate-900">
                    <Moon className="h-4 w-4 text-slate-500" />
                    Не беспокоить
                  </span>
                  <span className="mt-1 block max-w-2xl text-xs leading-5 text-slate-500">
                    Вне рабочего времени CRM оставит события в центре уведомлений, но не будет показывать всплывающие сообщения, Push, Email и Telegram.
                  </span>
                </span>
              </label>

              <label className="flex items-center gap-2 text-xs font-medium text-slate-600">
                <input
                  type="checkbox"
                  checked={doNotDisturb.allowCritical}
                  onChange={(event) => {
                    setDoNotDisturb((previous) => ({ ...previous, allowCritical: event.target.checked }));
                  }}
                  disabled={!doNotDisturb.enabled}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
                />
                Срочные уведомления показывать сразу
              </label>
            </div>

            <div className="mt-3 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
              <label className="text-xs font-medium text-slate-600">
                <span className="mb-1 flex items-center gap-1.5">
                  <Clock className="h-3.5 w-3.5" />
                  Рабочий день с
                </span>
                <input
                  type="time"
                  value={doNotDisturb.workStart}
                  onChange={(event) => {
                    setDoNotDisturb((previous) => ({ ...previous, workStart: event.target.value }));
                  }}
                  disabled={!doNotDisturb.enabled}
                  className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm text-slate-900 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-400"
                />
              </label>
              <label className="text-xs font-medium text-slate-600">
                <span className="mb-1 block">Рабочий день до</span>
                <input
                  type="time"
                  value={doNotDisturb.workEnd}
                  onChange={(event) => {
                    setDoNotDisturb((previous) => ({ ...previous, workEnd: event.target.value }));
                  }}
                  disabled={!doNotDisturb.enabled}
                  className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm text-slate-900 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-400"
                />
              </label>
              <label className="text-xs font-medium text-slate-600">
                <span className="mb-1 block">Отпуск или выходной до</span>
                <input
                  type="date"
                  value={doNotDisturb.vacationUntil ?? ''}
                  onChange={(event) => {
                    setDoNotDisturb((previous) => ({ ...previous, vacationUntil: event.target.value || null }));
                  }}
                  disabled={!doNotDisturb.enabled}
                  className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm text-slate-900 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-400"
                />
              </label>
              <label className="flex items-center gap-2 rounded-lg border border-white bg-white px-3 py-2 text-xs font-medium text-slate-600">
                <input
                  type="checkbox"
                  checked={doNotDisturb.muteWeekends}
                  onChange={(event) => {
                    setDoNotDisturb((previous) => ({ ...previous, muteWeekends: event.target.checked }));
                  }}
                  disabled={!doNotDisturb.enabled}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
                />
                Не беспокоить в выходные
              </label>
            </div>
          </div>

          {preferencesLoading ? (
            <div className="mt-4 flex items-center gap-2 text-sm text-slate-500">
              <RefreshCw className="h-4 w-4 animate-spin" />
              Загружаем настройки типов...
            </div>
          ) : (
            <div className="mt-4 space-y-3">
              {typeGroups.map((group) => (
                <div key={group.key} className="rounded-lg border border-slate-200 bg-slate-50/70 p-3">
                  <div className="mb-3 flex items-center justify-between gap-3">
                    <h3 className="text-sm font-semibold text-slate-900">{group.label}</h3>
                    <span className="rounded-full bg-white px-2 py-0.5 text-xs font-medium text-slate-500">
                      {group.items.length}
                    </span>
                  </div>
                  <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                    {group.items.map((item) => {
                      const isEnabled = isUserNotificationTypeEnabled(preferences, item.key);
                      const availableChannels = CHANNEL_OPTIONS.filter((channel) => item.channels.includes(channel.value));
                      return (
                        <div
                          key={item.key}
                          className={`min-h-32 rounded-lg border p-3 transition ${
                            isEnabled
                              ? 'border-blue-200 bg-white hover:bg-blue-50/60'
                              : 'border-slate-200 bg-white/70 text-slate-500 hover:bg-slate-100'
                          }`}
                        >
                          <label className="flex cursor-pointer gap-3">
                            <input
                              type="checkbox"
                              checked={isEnabled}
                              onChange={(event) => {
                                setPreferences((previous) => toggleUserNotificationTypePreference(previous, item.key, event.target.checked));
                              }}
                              className="mt-1 h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                            />
                            <span>
                              <span className="block text-sm font-semibold text-slate-900">{item.label}</span>
                              <span className="mt-1 block text-xs leading-5 text-slate-500">{item.description}</span>
                              <span className="mt-2 flex flex-wrap gap-1.5">
                                {item.requiresAction ? (
                                  <span className="inline-flex rounded-full bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700">Требует действия</span>
                                ) : null}
                                {item.showToastByDefault ? (
                                  <span className="inline-flex rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">Toast: {item.toastPriority}</span>
                                ) : null}
                                {item.pushEligible ? (
                                  <span className="inline-flex rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">Push: {item.pushPriority}</span>
                                ) : null}
                                {item.groupingWindowSeconds > 0 ? (
                                  <span className="inline-flex rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-500">
                                    Группировка {item.groupingWindowSeconds} сек.
                                  </span>
                                ) : null}
                              </span>
                              <span className={`mt-2 inline-flex rounded-full px-2 py-0.5 text-xs font-medium ${notificationPriorityBadgeClass(item.priority)}`}>
                                {notificationPriorityLabel(item.priority)}
                              </span>
                            </span>
                          </label>
                          {availableChannels.length > 0 ? (
                            <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-3">
                              {availableChannels.map(({ value, label, Icon }) => {
                                const channelEnabled = isUserNotificationChannelEnabled(preferences, item.key, value);
                                return (
                                  <label
                                    key={value}
                                    className={`flex h-9 cursor-pointer items-center justify-center gap-1.5 rounded-lg border px-2 text-xs font-medium transition ${
                                      channelEnabled
                                        ? 'border-slate-300 bg-white text-slate-700 hover:bg-slate-50'
                                        : 'border-slate-200 bg-slate-100 text-slate-400'
                                    } ${isEnabled ? '' : 'cursor-not-allowed opacity-60'}`}
                                    title={`Канал: ${label}`}
                                  >
                                    <input
                                      type="checkbox"
                                      checked={channelEnabled}
                                      disabled={!isEnabled}
                                      onChange={(event) => {
                                        setPreferences((previous) => toggleUserNotificationChannelPreference(previous, item.key, value, event.target.checked));
                                      }}
                                      className="sr-only"
                                    />
                                    <Icon className="h-3.5 w-3.5" />
                                    <span className="truncate">{label}</span>
                                  </label>
                                );
                              })}
                            </div>
                          ) : null}
                        </div>
                      );
                    })}
                  </div>
                </div>
              ))}
            </div>
          )}
        </section>
      ) : null}

      <section className="overflow-hidden rounded-lg border border-slate-200 bg-white shadow-sm">
        <div className="flex items-center justify-between gap-3 border-b border-slate-200 bg-slate-50 px-4 py-3 xl:hidden">
          <label className="inline-flex items-center gap-2 text-sm font-medium text-slate-700">
            <input
              type="checkbox"
              checked={allPageSelected}
              disabled={loading || currentPageIds.length === 0}
              onChange={(event) => togglePageSelection(event.target.checked)}
              className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-40"
            />
            Выбрать все на странице
          </label>
          {selectedCount > 0 ? <span className="text-xs font-semibold text-blue-700">Выбрано: {selectedCount}</span> : null}
        </div>
        <div className="grid grid-cols-[3rem_minmax(0,1fr)_12rem_16rem] border-b border-slate-200 bg-slate-50 px-4 py-3 text-xs font-semibold uppercase tracking-[0.14em] text-slate-500 max-xl:hidden">
          <div>
            <input
              ref={pageSelectionRef}
              type="checkbox"
              checked={allPageSelected}
              disabled={loading || currentPageIds.length === 0}
              onChange={(event) => togglePageSelection(event.target.checked)}
              aria-label="Выбрать все уведомления на странице"
              className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-40"
            />
          </div>
          <div>Уведомление</div>
          <div>Тип</div>
          <div className="text-right">Действия</div>
        </div>

        {loading ? (
          <div className="flex items-center justify-center gap-3 px-4 py-12 text-sm text-slate-500">
            <RefreshCw className="h-5 w-5 animate-spin" />
            Загружаем уведомления...
          </div>
        ) : itemsPage.items.length === 0 ? (
          <div className="px-4 py-12 text-center text-sm text-slate-500">По текущим фильтрам уведомлений нет.</div>
        ) : (
          <div className="divide-y divide-slate-100">
            {itemsPage.items.map((item) => {
              const itemBusy = busyId === item.id;
              const linkedObjectLabel = item.objectId ? openLinkedObjectLabels[item.objectType] : undefined;
              const canOpenLinkedObject = Boolean(linkedObjectLabel && openLinkedObjectHandlers[item.objectType]);
              const caseDocumentId = getClientCaseDocumentIdFromNotification(item);
              const caseButtonLabel = caseDocumentId && onOpenCaseDocument ? 'К документам' : 'К делу';
              return (
                <article
                  key={item.id}
                  role="button"
                  tabIndex={0}
                  onClick={() => void markRead(item)}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                      event.preventDefault();
                      void markRead(item);
                    }
                  }}
                  className={`grid gap-3 px-4 py-4 transition hover:bg-slate-50 max-xl:grid-cols-1 xl:grid-cols-[3rem_minmax(0,1fr)_12rem_16rem] ${
                    item.isRead ? 'bg-white' : 'bg-blue-50/40'
                  }`}
                >
                  <div className="flex items-start xl:pt-1">
                    <input
                      type="checkbox"
                      checked={selectedIds.has(item.id)}
                      onClick={(event) => event.stopPropagation()}
                      onChange={(event) => toggleSelection(item.id, event.target.checked)}
                      aria-label={`Выбрать уведомление ${item.title}`}
                      className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                    />
                  </div>
                  <div className="min-w-0">
                    <div className="flex items-start gap-3">
                      <span className={`mt-1 flex h-6 w-6 shrink-0 items-center justify-center rounded-full ${toneClasses(item.tone, item.isRead)}`}>
                        {item.isRead ? <CheckCircle2 className="h-4 w-4" /> : <span className="h-2.5 w-2.5 rounded-full bg-white" />}
                      </span>
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <h2 className="text-base font-semibold text-slate-950">{item.title}</h2>
                          {!item.isRead ? (
                            <span className="rounded-full bg-rose-100 px-2 py-0.5 text-xs font-semibold text-rose-700">Новое</span>
                          ) : (
                            <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-500">Прочитано</span>
                          )}
                          {item.isArchived ? (
                            <span className="rounded-full bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700">Архив</span>
                          ) : null}
                        </div>
                        <p className="mt-1 whitespace-pre-line text-sm leading-6 text-slate-600">{item.bodyPreview}</p>
                        <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-slate-500">
                          <span>{formatDateTime(item.createdAt)}</span>
                          {item.taskId ? <span>Задача #{item.taskId}</span> : null}
                          {item.objectType === 'case' && item.objectId ? <span>Дело #{item.objectId}</span> : null}
                          {caseDocumentId ? <span>Документ #{caseDocumentId}</span> : null}
                          {linkedObjectLabel && item.objectId ? <span>{linkedObjectLabel.replace('К ', '')} #{item.objectId}</span> : null}
                          <span>{item.eventKey}</span>
                        </div>
                      </div>
                    </div>
                  </div>

                  <div className="flex items-start xl:pt-1">
                    <span className="rounded-full border border-slate-200 bg-white px-3 py-1 text-xs font-medium text-slate-700">
                      {item.typeLabel || item.type}
                    </span>
                  </div>

                  <div className="flex flex-wrap items-center justify-start gap-2 xl:justify-end">
                    {item.taskId ? (
                      <button
                        type="button"
                        disabled={itemBusy}
                        onClick={(event) => {
                          event.stopPropagation();
                          void handleOpenTask(item);
                        }}
                        className="inline-flex h-9 items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 text-sm font-medium text-blue-700 transition hover:bg-blue-100 disabled:cursor-wait disabled:opacity-60"
                      >
                        <ExternalLink className="h-4 w-4" />
                        К задаче
                      </button>
                    ) : null}
                    {item.objectType === 'case' && item.objectId ? (
                      <button
                        type="button"
                        disabled={itemBusy}
                        onClick={(event) => {
                          event.stopPropagation();
                          void handleOpenCase(item);
                        }}
                        className="inline-flex h-9 items-center gap-2 rounded-lg border border-violet-200 bg-violet-50 px-3 text-sm font-medium text-violet-700 transition hover:bg-violet-100 disabled:cursor-wait disabled:opacity-60"
                      >
                        <ExternalLink className="h-4 w-4" />
                        {caseButtonLabel}
                      </button>
                    ) : null}
                    {canOpenLinkedObject ? (
                      <button
                        type="button"
                        disabled={itemBusy}
                        onClick={(event) => {
                          event.stopPropagation();
                          void handleOpenLinkedObject(item);
                        }}
                        className="inline-flex h-9 items-center gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-3 text-sm font-medium text-emerald-700 transition hover:bg-emerald-100 disabled:cursor-wait disabled:opacity-60"
                      >
                        <ExternalLink className="h-4 w-4" />
                        {linkedObjectLabel}
                      </button>
                    ) : null}
                    <button
                      type="button"
                      disabled={itemBusy}
                      onClick={(event) => {
                        event.stopPropagation();
                        void handleToggleRead(item);
                      }}
                      className="inline-flex h-9 items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-wait disabled:opacity-60"
                    >
                      {item.isRead ? <RotateCcw className="h-4 w-4" /> : <CheckCircle2 className="h-4 w-4" />}
                      {item.isRead ? 'Вернуть' : 'Прочитано'}
                    </button>
                    {!item.isArchived ? (
                      <button
                        type="button"
                        disabled={itemBusy}
                        onClick={(event) => {
                          event.stopPropagation();
                          void handleArchive(item);
                        }}
                        className="inline-flex h-9 items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 text-sm font-medium text-amber-700 transition hover:bg-amber-100 disabled:cursor-wait disabled:opacity-60"
                      >
                        <Archive className="h-4 w-4" />
                        Архив
                      </button>
                    ) : null}
                    <button
                      type="button"
                      disabled={itemBusy}
                      onClick={(event) => {
                        event.stopPropagation();
                        void handleDelete(item);
                      }}
                      className="inline-flex h-9 items-center gap-2 rounded-lg border border-red-200 bg-red-50 px-3 text-sm font-medium text-red-700 transition hover:bg-red-100 disabled:cursor-wait disabled:opacity-60"
                    >
                      <Trash2 className="h-4 w-4" />
                      Удалить
                    </button>
                  </div>
                </article>
              );
            })}
          </div>
        )}

        <div className="flex flex-col gap-3 border-t border-slate-200 bg-slate-50 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="text-sm text-slate-500">
            Показано {itemsPage.items.length} из {itemsPage.total}
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={page <= 1 || loading}
              onClick={() => setPage((value) => Math.max(1, value - 1))}
              className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Назад
            </button>
            <span className="min-w-16 text-center text-sm font-medium text-slate-700">{page}/{totalPages}</span>
            <button
              type="button"
              disabled={page >= totalPages || loading}
              onClick={() => setPage((value) => Math.min(totalPages, value + 1))}
              className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Вперед
            </button>
          </div>
        </div>
      </section>
    </div>
  );
}
