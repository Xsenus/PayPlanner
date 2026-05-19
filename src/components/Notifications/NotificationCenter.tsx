import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Archive,
  Bell,
  CheckCheck,
  CheckCircle2,
  ExternalLink,
  Filter,
  Inbox,
  RefreshCw,
  RotateCcw,
  Search,
  Trash2,
} from 'lucide-react';
import {
  apiService,
  type PagedResult,
  type UserNotificationDto,
  type UserNotificationsQuery,
  type UserNotificationTypeDto,
} from '../../services/api';
import { useToast } from '../../hooks/useToast';
import { emitUserNotificationsChanged } from '../../utils/userNotifications';

type NotificationStatusFilter = NonNullable<UserNotificationsQuery['status']>;

type NotificationCenterProps = {
  onOpenTask?: (taskId: number) => void;
};

const STATUS_FILTERS: Array<{ value: NotificationStatusFilter; label: string }> = [
  { value: 'active', label: 'Активные' },
  { value: 'unread', label: 'Непрочитанные' },
  { value: 'read', label: 'Прочитанные' },
  { value: 'archived', label: 'Архив' },
  { value: 'all', label: 'Все' },
];

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

export function NotificationCenter({ onOpenTask }: NotificationCenterProps) {
  const toast = useToast();
  const [itemsPage, setItemsPage] = useState<PagedResult<UserNotificationDto>>(EMPTY_PAGE);
  const [types, setTypes] = useState<UserNotificationTypeDto[]>([]);
  const [status, setStatus] = useState<NotificationStatusFilter>('active');
  const [type, setType] = useState('all');
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState<number | null>(null);
  const [bulkBusy, setBulkBusy] = useState(false);

  const totalPages = Math.max(1, Math.ceil(itemsPage.total / itemsPage.pageSize));
  const unreadCount = useMemo(
    () => itemsPage.items.reduce((count, item) => count + (item.isRead ? 0 : 1), 0),
    [itemsPage.items],
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
      search,
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
  }, [itemsPage.pageSize, page, search, status, toast, type]);

  useEffect(() => refresh(), [refresh]);

  const updateItem = (nextItem: UserNotificationDto) => {
    setItemsPage((previous) => ({
      ...previous,
      items: previous.items.map((item) => (item.id === nextItem.id ? nextItem : item)),
    }));
    emitUserNotificationsChanged();
  };

  const removeItem = (id: number) => {
    setItemsPage((previous) => ({
      ...previous,
      items: previous.items.filter((item) => item.id !== id),
      total: Math.max(0, previous.total - 1),
    }));
    emitUserNotificationsChanged();
  };

  const markRead = async (item: UserNotificationDto) => {
    if (item.isRead) {
      return item;
    }

    const updated = await apiService.markUserNotificationRead(item.id);
    updateItem(updated);
    return updated;
  };

  const handleOpenTask = async (item: UserNotificationDto) => {
    if (!item.taskId || !onOpenTask) {
      return;
    }

    try {
      setBusyId(item.id);
      await markRead(item);
      onOpenTask(item.taskId);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось открыть задачу.');
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
        search,
      });
      emitUserNotificationsChanged();
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
            onClick={() => refresh()}
            disabled={loading || bulkBusy}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm transition hover:border-slate-300 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            Обновить
          </button>
        </div>
      </div>

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
      </section>

      <section className="overflow-hidden rounded-lg border border-slate-200 bg-white shadow-sm">
        <div className="grid grid-cols-[minmax(0,1fr)_12rem_16rem] border-b border-slate-200 bg-slate-50 px-4 py-3 text-xs font-semibold uppercase tracking-[0.14em] text-slate-500 max-xl:hidden">
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
                  className={`grid gap-3 px-4 py-4 transition hover:bg-slate-50 max-xl:grid-cols-1 xl:grid-cols-[minmax(0,1fr)_12rem_16rem] ${
                    item.isRead ? 'bg-white' : 'bg-blue-50/40'
                  }`}
                >
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
