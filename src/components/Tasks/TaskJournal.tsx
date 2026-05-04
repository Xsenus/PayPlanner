import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  AlarmClock,
  Bell,
  CalendarClock,
  CheckCircle2,
  ClipboardList,
  Clock3,
  Download,
  FileText,
  LayoutGrid,
  ListFilter,
  Mail,
  Pencil,
  Phone,
  Plus,
  Search,
  Send,
  TimerReset,
  Trash2,
  WalletCards,
  X,
} from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import {
  isTaskJournalActive,
  TASK_JOURNAL_EVENT,
  type TaskJournalAssignee,
  type TaskJournalChannel,
  type TaskJournalItem,
  type TaskJournalPriority,
  type TaskJournalReminder,
  type TaskJournalStatus,
  type TaskJournalType,
  type TaskJournalUpsertPayload,
} from '../../utils/taskJournalStore';

type ViewMode = 'list' | 'board';
type PeriodMode = 'day' | 'week' | 'month';
type ModalState = { mode: 'create' | 'edit'; task?: TaskJournalItem } | null;

type TaskForm = TaskJournalUpsertPayload & { dueAt: string };

const STATUS_OPTIONS: Array<{ value: TaskJournalStatus; label: string }> = [
  { value: 'new', label: 'Новая' },
  { value: 'inProgress', label: 'В работе' },
  { value: 'waiting', label: 'Ожидает' },
  { value: 'done', label: 'Готово' },
  { value: 'cancelled', label: 'Отменена' },
];

const TYPE_OPTIONS: Array<{ value: TaskJournalType; label: string; icon: typeof ClipboardList }> = [
  { value: 'task', label: 'Задача', icon: ClipboardList },
  { value: 'budget', label: 'Бюджет', icon: WalletCards },
  { value: 'contact', label: 'Связаться', icon: Phone },
  { value: 'document', label: 'Документ', icon: FileText },
  { value: 'reminder', label: 'Не забыть', icon: Bell },
  { value: 'control', label: 'Контроль', icon: AlarmClock },
];

const PRIORITY_OPTIONS: Array<{ value: TaskJournalPriority; label: string }> = [
  { value: 'low', label: 'Низкий' },
  { value: 'normal', label: 'Обычный' },
  { value: 'high', label: 'Высокий' },
  { value: 'urgent', label: 'Срочно' },
];

const REMINDER_OPTIONS: Array<{ value: TaskJournalReminder; label: string }> = [
  { value: 'before1d', label: 'За день' },
  { value: 'before1h', label: 'За час' },
  { value: 'before15m', label: 'За 15 минут' },
  { value: 'atDue', label: 'В срок' },
  { value: 'dailyOverdue', label: 'Каждый день просрочки' },
];

const CHANNEL_OPTIONS: Array<{ value: TaskJournalChannel; label: string; icon: typeof Bell }> = [
  { value: 'inApp', label: 'В системе', icon: Bell },
  { value: 'email', label: 'Email', icon: Mail },
  { value: 'telegram', label: 'Telegram', icon: Send },
];

const emptyForm = (assigneeName: string, assigneeUserId?: number | null): TaskForm => {
  const dueAt = new Date();
  dueAt.setHours(dueAt.getHours() + 2);

  return {
    objectName: '',
    title: '',
    description: '',
    assigneeUserId,
    assigneeName,
    dueAt: toLocalInputValue(dueAt.toISOString()),
    type: 'task',
    priority: 'normal',
    status: 'new',
    result: '',
    reminders: ['before1h', 'atDue'],
    channels: ['inApp'],
    tags: [],
  };
};

function toLocalInputValue(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const offsetMs = date.getTimezoneOffset() * 60 * 1000;
  return new Date(date.getTime() - offsetMs).toISOString().slice(0, 16);
}

function fromLocalInputValue(value: string) {
  return value ? new Date(value).toISOString() : new Date().toISOString();
}

function formatDateTime(value: string) {
  return new Date(value).toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function formatRelative(value: string) {
  const ms = new Date(value).getTime() - Date.now();
  const absHours = Math.ceil(Math.abs(ms) / 36e5);
  if (ms < 0) return `просрочено на ${absHours} ч.`;
  if (absHours < 24) return `через ${absHours} ч.`;
  return `через ${Math.ceil(absHours / 24)} дн.`;
}

function isSameDay(a: Date, b: Date) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

function addDays(date: Date, days: number) {
  const next = new Date(date);
  next.setDate(next.getDate() + days);
  return next;
}

function startOfWeek(date: Date) {
  const next = new Date(date);
  const day = next.getDay() || 7;
  next.setDate(next.getDate() - day + 1);
  next.setHours(0, 0, 0, 0);
  return next;
}

function endOfDay(date: Date) {
  const next = new Date(date);
  next.setHours(23, 59, 59, 999);
  return next;
}

function getTypeMeta(type: TaskJournalType) {
  return TYPE_OPTIONS.find((item) => item.value === type) ?? TYPE_OPTIONS[0];
}

function getStatusLabel(status: TaskJournalStatus) {
  return STATUS_OPTIONS.find((item) => item.value === status)?.label ?? status;
}

function getPriorityLabel(priority: TaskJournalPriority) {
  return PRIORITY_OPTIONS.find((item) => item.value === priority)?.label ?? priority;
}

function priorityClass(priority: TaskJournalPriority) {
  switch (priority) {
    case 'urgent':
      return 'border-rose-200 bg-rose-50 text-rose-700';
    case 'high':
      return 'border-amber-200 bg-amber-50 text-amber-700';
    case 'low':
      return 'border-slate-200 bg-slate-50 text-slate-600';
    default:
      return 'border-blue-200 bg-blue-50 text-blue-700';
  }
}

function statusClass(status: TaskJournalStatus) {
  switch (status) {
    case 'done':
      return 'bg-emerald-50 text-emerald-700';
    case 'cancelled':
      return 'bg-slate-100 text-slate-500';
    case 'waiting':
      return 'bg-amber-50 text-amber-700';
    case 'inProgress':
      return 'bg-blue-50 text-blue-700';
    default:
      return 'bg-slate-50 text-slate-700';
  }
}

function unique(values: string[]) {
  return Array.from(new Set(values.map((item) => item.trim()).filter(Boolean)));
}

function serializeCsvValue(value: string) {
  return `"${value.replaceAll('"', '""')}"`;
}

export function TaskJournal() {
  const { user } = useAuth();
  const toast = useToast();
  const { confirm } = useDialog();
  const [tasks, setTasks] = useState<TaskJournalItem[]>([]);
  const [assignees, setAssignees] = useState<TaskJournalAssignee[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');
  const [viewMode, setViewMode] = useState<ViewMode>('list');
  const [periodMode, setPeriodMode] = useState<PeriodMode>('day');
  const [statusFilter, setStatusFilter] = useState<TaskJournalStatus | 'active' | 'all'>('active');
  const [assigneeFilter, setAssigneeFilter] = useState('all');
  const [typeFilter, setTypeFilter] = useState<TaskJournalType | 'all'>('all');
  const [modal, setModal] = useState<ModalState>(null);

  const currentUserName = user?.fullName || user?.email || 'Текущий пользователь';
  const currentUserId = user?.id ?? null;

  const refreshTasks = useCallback(async () => {
    setLoading(true);
    try {
      const [tasksResponse, assigneeResponse] = await Promise.all([
        apiService.getTaskJournalItems({ page: 1, pageSize: 200 }),
        apiService.getTaskJournalAssignees(),
      ]);
      setTasks(tasksResponse.items);
      setAssignees(assigneeResponse);
    } catch (error) {
      toast.error('Не удалось загрузить журнал задач.', 'Журнал задач');
    } finally {
      setLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    void refreshTasks();
    const refresh = () => void refreshTasks();
    window.addEventListener(TASK_JOURNAL_EVENT, refresh);
    return () => window.removeEventListener(TASK_JOURNAL_EVENT, refresh);
  }, [refreshTasks]);

  const assigneeOptions = useMemo(() => {
    const byId = new Map<number, TaskJournalAssignee>();
    assignees.forEach((assignee) => byId.set(assignee.id, assignee));
    return Array.from(byId.values());
  }, [assignees]);

  const filteredTasks = useMemo(() => {
    const q = search.trim().toLowerCase();
    const nowDate = new Date();
    const weekStart = startOfWeek(nowDate);
    const monthStart = new Date(nowDate.getFullYear(), nowDate.getMonth(), 1);
    const periodEnd =
      periodMode === 'day'
        ? endOfDay(nowDate)
        : periodMode === 'week'
          ? endOfDay(addDays(weekStart, 6))
          : endOfDay(new Date(nowDate.getFullYear(), nowDate.getMonth() + 1, 0));
    const periodStart = periodMode === 'day' ? new Date(nowDate.getFullYear(), nowDate.getMonth(), nowDate.getDate()) : periodMode === 'week' ? weekStart : monthStart;

    return tasks
      .filter((task) => {
        const due = new Date(task.dueAt);
        const statusOk =
          statusFilter === 'all'
            ? true
            : statusFilter === 'active'
              ? isTaskJournalActive(task.status)
              : task.status === statusFilter;
        const assigneeOk = assigneeFilter === 'all' || String(task.assigneeUserId ?? task.assigneeName) === assigneeFilter;
        const typeOk = typeFilter === 'all' || task.type === typeFilter;
        const periodOk = viewMode === 'list' || due < periodStart || (due >= periodStart && due <= periodEnd);
        const searchOk = !q || [
          task.objectName,
          task.title,
          task.description,
          task.assigneeName,
          task.authorName,
          task.result,
          task.tags.join(' '),
        ].some((value) => value.toLowerCase().includes(q));

        return statusOk && assigneeOk && typeOk && periodOk && searchOk;
      })
      .sort((a, b) => new Date(a.dueAt).getTime() - new Date(b.dueAt).getTime());
  }, [assigneeFilter, periodMode, search, statusFilter, tasks, typeFilter, viewMode]);

  const stats = useMemo(() => {
    const active = tasks.filter((task) => isTaskJournalActive(task.status));
    const overdue = active.filter((task) => new Date(task.dueAt).getTime() < Date.now());
    const today = active.filter((task) => isSameDay(new Date(task.dueAt), new Date()));
    const high = active.filter((task) => task.priority === 'high' || task.priority === 'urgent');
    return { active: active.length, overdue: overdue.length, today: today.length, high: high.length };
  }, [tasks]);

  const boardColumns = useMemo(() => {
    const nowDate = new Date();
    const tomorrow = addDays(nowDate, 1);
    const active = filteredTasks.filter((task) => isTaskJournalActive(task.status));

    return [
      {
        key: 'overdue',
        title: 'Просроченные',
        tone: 'rose',
        items: active.filter((task) => new Date(task.dueAt).getTime() < Date.now()),
      },
      {
        key: 'today',
        title: 'На сегодня',
        tone: 'blue',
        items: active.filter((task) => isSameDay(new Date(task.dueAt), nowDate)),
      },
      {
        key: 'tomorrow',
        title: 'На завтра',
        tone: 'emerald',
        items: active.filter((task) => isSameDay(new Date(task.dueAt), tomorrow)),
      },
      {
        key: 'later',
        title: periodMode === 'month' ? 'Дальше в месяце' : 'Ближайшие',
        tone: 'slate',
        items: active.filter((task) => {
          const due = new Date(task.dueAt);
          return due.getTime() >= Date.now() && !isSameDay(due, nowDate) && !isSameDay(due, tomorrow);
        }),
      },
    ];
  }, [filteredTasks, periodMode]);

  const emitTaskChange = () => {
    window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT));
  };

  const handleSave = async (form: TaskForm, taskId?: number) => {
    if (!form.objectName.trim()) {
      toast.error('Укажите объект задачи.', 'Журнал задач');
      return;
    }
    if (!form.title.trim()) {
      toast.error('Укажите текст задачи.', 'Журнал задач');
      return;
    }

    const normalized: TaskForm = {
      ...form,
      objectName: form.objectName.trim(),
      title: form.title.trim(),
      description: form.description.trim(),
      result: form.result.trim(),
      dueAt: fromLocalInputValue(form.dueAt),
      tags: unique(form.tags),
      reminders: form.reminders.length ? form.reminders : ['atDue'],
      channels: form.channels.length ? form.channels : ['inApp'],
    };

    try {
      if (taskId) {
        await apiService.updateTaskJournalItem(taskId, normalized);
        toast.success('Задача обновлена.', 'Журнал задач');
      } else {
        await apiService.createTaskJournalItem(normalized);
        toast.success('Задача создана.', 'Журнал задач');
      }
      setModal(null);
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error('Не удалось сохранить задачу.', 'Журнал задач');
    }
  };

  const handleDelete = async (task: TaskJournalItem) => {
    const accepted = await confirm({
      title: 'Удалить задачу?',
      description: `Задача "${task.title}" будет удалена из журнала.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!accepted) return;
    try {
      await apiService.deleteTaskJournalItem(task.id);
      toast.success('Задача удалена.', 'Журнал задач');
      await refreshTasks();
      emitTaskChange();
    } catch {
      toast.error('Не удалось удалить задачу.', 'Журнал задач');
    }
  };

  const setTaskStatus = async (task: TaskJournalItem, status: TaskJournalStatus) => {
    try {
      await apiService.updateTaskJournalStatus(task.id, status);
      await refreshTasks();
      emitTaskChange();
    } catch {
      toast.error('Не удалось обновить статус задачи.', 'Журнал задач');
    }
  };

  const handleExport = () => {
    const header = ['Объект', 'Текст задачи', 'Исполнитель', 'Срок', 'Тип', 'Приоритет', 'Статус', 'Результат'];
    const rows = filteredTasks.map((task) => [
      task.objectName,
      task.title,
      task.assigneeName,
      formatDateTime(task.dueAt),
      getTypeMeta(task.type).label,
      getPriorityLabel(task.priority),
      getStatusLabel(task.status),
      task.result,
    ]);
    const csv = [header, ...rows].map((row) => row.map(serializeCsvValue).join(';')).join('\n');
    const blob = new Blob([`\ufeff${csv}`], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = 'task-journal.csv';
    link.click();
    URL.revokeObjectURL(url);
    toast.success('Журнал задач экспортирован в CSV.', 'Журнал задач');
  };

  return (
    <div className="min-h-screen bg-slate-50">
      <div className="mx-auto max-w-[1720px] p-4 sm:p-6 lg:p-8">
        <section className="mb-6">
          <div className="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">Рабочий контроль</div>
              <h1 className="mt-2 text-3xl font-semibold text-slate-900">Журнал задач</h1>
              <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-600">
                Задачи по объектам, платежам, звонкам и документам: исполнитель, срок, результат и напоминания в одном рабочем журнале.
              </p>
            </div>

            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={handleExport}
                className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
              >
                <Download className="h-4 w-4" />
                CSV
              </button>
              <button
                type="button"
                onClick={() => setModal({ mode: 'create' })}
                className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800"
              >
                <Plus className="h-4 w-4" />
                Добавить задачу
              </button>
            </div>
          </div>
        </section>

        <section className="mb-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <MetricCard label="Активные" value={stats.active} icon={ClipboardList} tone="blue" />
          <MetricCard label="Просрочены" value={stats.overdue} icon={TimerReset} tone="rose" />
          <MetricCard label="Сегодня" value={stats.today} icon={CalendarClock} tone="emerald" />
          <MetricCard label="Высокий приоритет" value={stats.high} icon={AlarmClock} tone="amber" />
        </section>

        <section className="mb-6 grid gap-3 xl:grid-cols-[minmax(0,1fr)_auto]">
          <div className="grid gap-3 md:grid-cols-[minmax(260px,1fr)_repeat(3,minmax(180px,220px))]">
            <label className="relative block">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="Поиск по объекту, тексту, исполнителю или результату"
                className="h-11 w-full rounded-xl border border-slate-200 bg-white pl-9 pr-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              />
            </label>

            <select
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value as TaskJournalStatus | 'active' | 'all')}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Статус"
            >
              <option value="active">Активные</option>
              <option value="all">Все статусы</option>
              {STATUS_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>

            <select
              value={assigneeFilter}
              onChange={(event) => setAssigneeFilter(event.target.value)}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Исполнитель"
            >
              <option value="all">Все исполнители</option>
              {assigneeOptions.map((assignee) => (
                <option key={assignee.id} value={String(assignee.id)}>{assignee.name}</option>
              ))}
            </select>

            <select
              value={typeFilter}
              onChange={(event) => setTypeFilter(event.target.value as TaskJournalType | 'all')}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Тип задачи"
            >
              <option value="all">Все типы</option>
              {TYPE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </div>

          <div className="flex flex-wrap justify-end gap-2">
            <div className="flex rounded-xl bg-slate-100 p-1">
              {(['day', 'week', 'month'] as PeriodMode[]).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  onClick={() => setPeriodMode(mode)}
                  className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${periodMode === mode ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
                >
                  {mode === 'day' ? 'День' : mode === 'week' ? 'Неделя' : 'Месяц'}
                </button>
              ))}
            </div>

            <div className="flex rounded-xl bg-slate-100 p-1">
              <button
                type="button"
                onClick={() => setViewMode('list')}
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'list' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <ListFilter className="h-4 w-4" />
                Список
              </button>
              <button
                type="button"
                onClick={() => setViewMode('board')}
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'board' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <LayoutGrid className="h-4 w-4" />
                Доска
              </button>
            </div>
          </div>
        </section>

        {viewMode === 'list' ? (
          loading ? (
            <div className="rounded-2xl border border-slate-200 bg-white px-6 py-12 text-center text-sm text-slate-500 shadow-sm">
              Загружаем журнал задач...
            </div>
          ) : (
          <TaskTable
            tasks={filteredTasks}
            onEdit={(task) => setModal({ mode: 'edit', task })}
            onDelete={handleDelete}
            onStatusChange={setTaskStatus}
          />
          )
        ) : (
          <TaskBoard
            columns={boardColumns}
            onEdit={(task) => setModal({ mode: 'edit', task })}
            onDone={(task) => setTaskStatus(task, 'done')}
          />
        )}
      </div>

      {modal ? (
        <TaskModal
          state={modal}
          assignees={assignees}
          currentUserId={currentUserId}
          currentUserName={currentUserName}
          onClose={() => setModal(null)}
          onSave={handleSave}
        />
      ) : null}
    </div>
  );
}

function MetricCard({ label, value, icon: Icon, tone }: { label: string; value: number; icon: typeof ClipboardList; tone: 'blue' | 'rose' | 'emerald' | 'amber' }) {
  const toneClass = {
    blue: 'bg-blue-50 text-blue-700',
    rose: 'bg-rose-50 text-rose-700',
    emerald: 'bg-emerald-50 text-emerald-700',
    amber: 'bg-amber-50 text-amber-700',
  }[tone];

  return (
    <div className="rounded-2xl border border-slate-200 bg-white px-5 py-4 shadow-sm">
      <div className="flex items-center justify-between gap-3">
        <div>
          <div className="text-sm text-slate-500">{label}</div>
          <div className="mt-2 text-3xl font-semibold text-slate-900">{value}</div>
        </div>
        <div className={`rounded-xl p-3 ${toneClass}`}>
          <Icon className="h-5 w-5" />
        </div>
      </div>
    </div>
  );
}

function TaskTable({
  tasks,
  onEdit,
  onDelete,
  onStatusChange,
}: {
  tasks: TaskJournalItem[];
  onEdit: (task: TaskJournalItem) => void;
  onDelete: (task: TaskJournalItem) => void;
  onStatusChange: (task: TaskJournalItem, status: TaskJournalStatus) => void;
}) {
  return (
    <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div className="overflow-x-auto">
        <table className="min-w-[1180px] w-full">
          <thead className="border-b border-slate-200 bg-slate-50">
            <tr className="text-left text-xs font-semibold uppercase tracking-[0.16em] text-slate-500">
              <th className="px-4 py-4">Объект</th>
              <th className="px-4 py-4">Текст задачи</th>
              <th className="px-4 py-4">Исполнитель</th>
              <th className="px-4 py-4">Дата исполнения</th>
              <th className="px-4 py-4">Тип</th>
              <th className="px-4 py-4">Результат</th>
              <th className="px-4 py-4">Статус</th>
              <th className="px-4 py-4 text-right">Действия</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {tasks.length === 0 ? (
              <tr>
                <td colSpan={8} className="px-4 py-12 text-center text-sm text-slate-500">
                  По текущим фильтрам задач нет.
                </td>
              </tr>
            ) : tasks.map((task) => {
              const TypeIcon = getTypeMeta(task.type).icon;
              const isOverdue = isTaskJournalActive(task.status) && new Date(task.dueAt).getTime() < Date.now();

              return (
                <tr key={task.id} className="align-top transition hover:bg-slate-50">
                  <td className="max-w-[280px] px-4 py-4">
                    <div className="font-semibold text-slate-900">{task.objectName}</div>
                    <div className="mt-2 flex flex-wrap gap-1.5">
                      {task.tags.map((tag) => (
                        <span key={tag} className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-500">{tag}</span>
                      ))}
                    </div>
                  </td>
                  <td className="max-w-[360px] px-4 py-4">
                    <div className="font-medium text-slate-900">{task.title}</div>
                    <div className="mt-1 line-clamp-2 text-sm text-slate-500">{task.description || 'Описание не заполнено'}</div>
                    <span className={`mt-3 inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${priorityClass(task.priority)}`}>
                      {getPriorityLabel(task.priority)}
                    </span>
                  </td>
                  <td className="px-4 py-4 text-sm text-slate-600">{task.assigneeName}</td>
                  <td className="px-4 py-4">
                    <div className={isOverdue ? 'font-semibold text-rose-600' : 'font-medium text-slate-800'}>{formatDateTime(task.dueAt)}</div>
                    <div className="mt-1 text-xs text-slate-500">{formatRelative(task.dueAt)}</div>
                  </td>
                  <td className="px-4 py-4">
                    <span className="inline-flex items-center gap-1.5 rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-700">
                      <TypeIcon className="h-3.5 w-3.5" />
                      {getTypeMeta(task.type).label}
                    </span>
                  </td>
                  <td className="max-w-[260px] px-4 py-4 text-sm text-slate-600">{task.result || '—'}</td>
                  <td className="px-4 py-4">
                    <select
                      value={task.status}
                      onChange={(event) => onStatusChange(task, event.target.value as TaskJournalStatus)}
                      className={`rounded-xl border-0 px-3 py-2 text-sm font-semibold outline-none ${statusClass(task.status)}`}
                    >
                      {STATUS_OPTIONS.map((option) => (
                        <option key={option.value} value={option.value}>{option.label}</option>
                      ))}
                    </select>
                  </td>
                  <td className="px-4 py-4">
                    <div className="flex justify-end gap-2">
                      <button type="button" onClick={() => onEdit(task)} className="rounded-xl p-2 text-slate-500 transition hover:bg-slate-100 hover:text-slate-900" title="Редактировать">
                        <Pencil className="h-4 w-4" />
                      </button>
                      <button type="button" onClick={() => onDelete(task)} className="rounded-xl p-2 text-rose-500 transition hover:bg-rose-50 hover:text-rose-700" title="Удалить">
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function TaskBoard({
  columns,
  onEdit,
  onDone,
}: {
  columns: Array<{ key: string; title: string; tone: string; items: TaskJournalItem[] }>;
  onEdit: (task: TaskJournalItem) => void;
  onDone: (task: TaskJournalItem) => void;
}) {
  return (
    <div className="grid gap-4 xl:grid-cols-4">
      {columns.map((column) => (
        <section key={column.key} className="min-h-[360px] rounded-2xl border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-100 px-4 py-4">
            <div className="flex items-center justify-between gap-3">
              <h2 className="font-semibold text-slate-900">{column.title}</h2>
              <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">{column.items.length}</span>
            </div>
          </div>
          <div className="space-y-3 p-3">
            {column.items.length === 0 ? (
              <div className="rounded-xl border border-dashed border-slate-200 px-3 py-8 text-center text-sm text-slate-400">
                Нет задач
              </div>
            ) : column.items.map((task) => {
              const TypeIcon = getTypeMeta(task.type).icon;
              return (
                <article key={task.id} className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                  <div className="flex items-start justify-between gap-3">
                    <span className={`rounded-full border px-2 py-0.5 text-xs font-semibold ${priorityClass(task.priority)}`}>
                      {getPriorityLabel(task.priority)}
                    </span>
                    <button type="button" onClick={() => onEdit(task)} className="rounded-lg p-1 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700" title="Редактировать">
                      <Pencil className="h-4 w-4" />
                    </button>
                  </div>
                  <div className="mt-3 text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{task.objectName}</div>
                  <h3 className="mt-2 text-sm font-semibold leading-5 text-slate-900">{task.title}</h3>
                  <div className="mt-3 flex items-center gap-2 text-xs text-slate-500">
                    <TypeIcon className="h-3.5 w-3.5" />
                    {getTypeMeta(task.type).label}
                    <span>•</span>
                    {task.assigneeName}
                  </div>
                  <div className="mt-3 flex items-center gap-2 text-xs text-slate-500">
                    <Clock3 className="h-3.5 w-3.5" />
                    {formatDateTime(task.dueAt)}
                  </div>
                  {task.status !== 'done' ? (
                    <button
                      type="button"
                      onClick={() => onDone(task)}
                      className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-emerald-200 px-3 py-2 text-sm font-semibold text-emerald-700 transition hover:bg-emerald-50"
                    >
                      <CheckCircle2 className="h-4 w-4" />
                      Завершить
                    </button>
                  ) : null}
                </article>
              );
            })}
          </div>
        </section>
      ))}
    </div>
  );
}

function TaskModal({
  state,
  assignees,
  currentUserId,
  currentUserName,
  onClose,
  onSave,
}: {
  state: ModalState;
  assignees: TaskJournalAssignee[];
  currentUserId: number | null;
  currentUserName: string;
  onClose: () => void;
  onSave: (form: TaskForm, taskId?: number) => void;
}) {
  const [form, setForm] = useState<TaskForm>(() => {
    if (state?.task) {
      const rest = {
        objectName: state.task.objectName,
        title: state.task.title,
        description: state.task.description,
        assigneeUserId: state.task.assigneeUserId,
        assigneeName: state.task.assigneeName,
        dueAt: state.task.dueAt,
        type: state.task.type,
        priority: state.task.priority,
        status: state.task.status,
        result: state.task.result,
        reminders: state.task.reminders,
        channels: state.task.channels,
        tags: state.task.tags,
      };
      return { ...rest, dueAt: toLocalInputValue(rest.dueAt) };
    }
    return emptyForm(currentUserName, currentUserId);
  });
  const [tagText, setTagText] = useState(() => form.tags.join(', '));

  const update = <K extends keyof TaskForm>(key: K, value: TaskForm[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const toggleArray = <T extends string>(key: 'reminders' | 'channels', value: T) => {
    setForm((prev) => {
      const current = prev[key] as T[];
      return {
        ...prev,
        [key]: current.includes(value) ? current.filter((item) => item !== value) : [...current, value],
      };
    });
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 p-4 backdrop-blur-sm">
      <div className="flex max-h-[92vh] w-full max-w-5xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-slate-200 px-6 py-5">
          <div>
            <h2 className="text-2xl font-semibold text-slate-900">{state?.mode === 'edit' ? 'Редактировать задачу' : 'Новая задача'}</h2>
            <p className="mt-1 text-sm text-slate-500">Заполните объект, исполнителя, срок и правила напоминаний.</p>
          </div>
          <button type="button" onClick={onClose} className="rounded-xl p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700" aria-label="Закрыть">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="min-h-0 overflow-y-auto px-6 py-5">
          <div className="grid gap-5 lg:grid-cols-2">
            <Field label="Объект">
              <input value={form.objectName} onChange={(event) => update('objectName', event.target.value)} className="form-input" placeholder="Например: ГОРБАТЮК В.Б. - БАНКРОТСТВО" />
            </Field>
            <Field label="Исполнитель">
              <select
                value={form.assigneeUserId ?? ''}
                onChange={(event) => {
                  const nextId = Number(event.target.value) || null;
                  const selected = assignees.find((assignee) => assignee.id === nextId);
                  update('assigneeUserId', nextId);
                  update('assigneeName', selected?.name ?? form.assigneeName);
                }}
                className="form-input"
              >
                <option value="">Без привязки к пользователю</option>
                {assignees.map((assignee) => (
                  <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
                ))}
              </select>
            </Field>
            <Field label="Имя исполнителя">
              <input value={form.assigneeName} onChange={(event) => update('assigneeName', event.target.value)} className="form-input" />
            </Field>
            <Field label="Текст задачи">
              <input value={form.title} onChange={(event) => update('title', event.target.value)} className="form-input" placeholder="Что нужно сделать" />
            </Field>
            <Field label="Дата и время исполнения">
              <input type="datetime-local" value={form.dueAt} onChange={(event) => update('dueAt', event.target.value)} className="form-input" />
            </Field>
            <Field label="Тип задачи">
              <select value={form.type} onChange={(event) => update('type', event.target.value as TaskJournalType)} className="form-input">
                {TYPE_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </Field>
            <Field label="Приоритет">
              <select value={form.priority} onChange={(event) => update('priority', event.target.value as TaskJournalPriority)} className="form-input">
                {PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </Field>
            <Field label="Статус">
              <select value={form.status} onChange={(event) => update('status', event.target.value as TaskJournalStatus)} className="form-input">
                {STATUS_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </Field>
          </div>

          <div className="mt-5 grid gap-5 lg:grid-cols-2">
            <Field label="Описание">
              <textarea value={form.description} onChange={(event) => update('description', event.target.value)} className="form-textarea" rows={5} placeholder="Контекст, телефоны, комментарии, откуда пришла задача" />
            </Field>
            <Field label="Результат">
              <textarea value={form.result} onChange={(event) => update('result', event.target.value)} className="form-textarea" rows={5} placeholder="Итог выполнения или причина ожидания" />
            </Field>
          </div>

          <div className="mt-5 grid gap-5 lg:grid-cols-2">
            <Field label="Напоминания">
              <div className="grid gap-2 sm:grid-cols-2">
                {REMINDER_OPTIONS.map((option) => (
                  <label key={option.value} className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700">
                    <input type="checkbox" checked={form.reminders.includes(option.value)} onChange={() => toggleArray('reminders', option.value)} className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500" />
                    {option.label}
                  </label>
                ))}
              </div>
            </Field>
            <Field label="Каналы уведомлений">
              <div className="grid gap-2 sm:grid-cols-3">
                {CHANNEL_OPTIONS.map((option) => {
                  const Icon = option.icon;
                  return (
                    <label key={option.value} className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700">
                      <input type="checkbox" checked={form.channels.includes(option.value)} onChange={() => toggleArray('channels', option.value)} className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500" />
                      <Icon className="h-4 w-4 text-slate-400" />
                      {option.label}
                    </label>
                  );
                })}
              </div>
            </Field>
          </div>

          <Field label="Теги" className="mt-5">
            <input
              value={tagText}
              onChange={(event) => {
                setTagText(event.target.value);
                update('tags', unique(event.target.value.split(',')));
              }}
              className="form-input"
              placeholder="контроль, платеж, звонок"
            />
          </Field>
        </div>

        <div className="flex flex-col gap-3 border-t border-slate-200 px-6 py-4 sm:flex-row sm:justify-end">
          <button type="button" onClick={onClose} className="rounded-xl border border-slate-300 px-5 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50">Отмена</button>
          <button type="button" onClick={() => onSave(form, state?.task?.id)} className="rounded-xl bg-slate-900 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800">
            {state?.mode === 'edit' ? 'Сохранить' : 'Создать задачу'}
          </button>
        </div>
      </div>
      <style>{`
        .form-input {
          height: 44px;
          width: 100%;
          border-radius: 12px;
          border: 1px solid rgb(226 232 240);
          background: white;
          padding: 0 12px;
          font-size: 14px;
          color: rgb(51 65 85);
          outline: none;
        }
        .form-input:focus,
        .form-textarea:focus {
          border-color: rgb(96 165 250);
          box-shadow: 0 0 0 2px rgb(219 234 254);
        }
        .form-textarea {
          width: 100%;
          border-radius: 12px;
          border: 1px solid rgb(226 232 240);
          background: white;
          padding: 10px 12px;
          font-size: 14px;
          color: rgb(51 65 85);
          outline: none;
          resize: vertical;
        }
      `}</style>
    </div>
  );
}

function Field({ label, children, className = '' }: { label: string; children: ReactNode; className?: string }) {
  return (
    <label className={`block ${className}`}>
      <span className="mb-2 block text-sm font-semibold text-slate-700">{label}</span>
      {children}
    </label>
  );
}
