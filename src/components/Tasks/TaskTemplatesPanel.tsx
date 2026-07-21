import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  CheckCircle2,
  ClipboardPlus,
  Edit3,
  Loader2,
  Plus,
  RefreshCw,
  Search,
  Trash2,
  UsersRound,
  X,
} from 'lucide-react';

import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type {
  TaskJournalAssignee,
  TaskJournalChannel,
  TaskJournalParticipantRole,
  TaskJournalPriority,
  TaskJournalReminder,
  TaskJournalTemplate,
  TaskJournalTemplateBindingType,
  TaskJournalTemplateParticipantUpsertPayload,
  TaskJournalTemplateUpsertPayload,
  TaskJournalType,
  TaskJournalUpsertPayload,
} from '../../utils/taskJournalStore';

type TaskTemplatesPanelProps = {
  assignees: TaskJournalAssignee[];
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  onUseTemplate: (defaults: Partial<TaskJournalUpsertPayload>) => void;
};

type TemplateForm = {
  id: number | null;
  name: string;
  description: string;
  title: string;
  taskDescription: string;
  type: TaskJournalType;
  priority: TaskJournalPriority;
  plannedDurationMinutes: string;
  dueOffsetMinutes: string;
  assigneeUserId: string;
  bindingType: TaskJournalTemplateBindingType;
  bindingKey: string;
  isActive: boolean;
  reminders: TaskJournalReminder[];
  channels: TaskJournalChannel[];
  tagsText: string;
  participants: TaskJournalTemplateParticipantUpsertPayload[];
  observerName: string;
};

const TYPE_OPTIONS: Array<{ value: TaskJournalType; label: string }> = [
  { value: 'task', label: 'Задача' },
  { value: 'budget', label: 'Бюджет' },
  { value: 'contact', label: 'Связаться' },
  { value: 'document', label: 'Документ' },
  { value: 'reminder', label: 'Не забыть' },
  { value: 'control', label: 'Контроль' },
  { value: 'courtHearing', label: 'Суд. заседание' },
  { value: 'meeting', label: 'Встреча' },
];

const PRIORITY_OPTIONS: Array<{ value: TaskJournalPriority; label: string }> = [
  { value: 'low', label: 'Низкий' },
  { value: 'normal', label: 'Обычный' },
  { value: 'high', label: 'Высокий' },
  { value: 'urgent', label: 'Срочно' },
];

const REMINDER_OPTIONS: Array<{ value: TaskJournalReminder; label: string }> = [
  { value: 'atDue', label: 'В срок' },
  { value: 'before15m', label: 'За 15 минут' },
  { value: 'before1h', label: 'За 1 час' },
  { value: 'before1d', label: 'За 1 день' },
  { value: 'before3d', label: 'За 3 дня' },
  { value: 'before7d', label: 'За 7 дней' },
  { value: 'dailyOverdue', label: 'Ежедневно при просрочке' },
];

const CHANNEL_OPTIONS: Array<{ value: TaskJournalChannel; label: string }> = [
  { value: 'inApp', label: 'В системе' },
  { value: 'email', label: 'Email' },
  { value: 'telegram', label: 'Telegram' },
];

const BINDING_TYPE_OPTIONS: Array<{ value: TaskJournalTemplateBindingType; label: string }> = [
  { value: 'none', label: 'Без привязки' },
  { value: 'caseStage', label: 'Этап дела' },
  { value: 'caseType', label: 'Тип дела' },
];

function createEmptyForm(): TemplateForm {
  return {
    id: null,
    name: '',
    description: '',
    title: '',
    taskDescription: '',
    type: 'task',
    priority: 'normal',
    plannedDurationMinutes: '60',
    dueOffsetMinutes: '1440',
    assigneeUserId: '',
    bindingType: 'none',
    bindingKey: '',
    isActive: true,
    reminders: ['atDue'],
    channels: ['inApp'],
    tagsText: '',
    participants: [],
    observerName: '',
  };
}

function getAssigneeName(assignees: TaskJournalAssignee[], userId: string) {
  const id = Number(userId);
  return assignees.find((item) => item.id === id)?.name ?? '';
}

function toggleValue<T extends string>(values: T[], value: T) {
  return values.includes(value) ? values.filter((item) => item !== value) : [...values, value];
}

function splitTags(value: string) {
  return value
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)
    .filter((item, index, items) => items.indexOf(item) === index);
}

function clampMinutes(value: string, fallback: number) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= 0 ? Math.round(parsed) : fallback;
}

function formatOffset(minutes: number) {
  if (minutes <= 0) return 'Без автосрока';
  if (minutes % 1440 === 0) return `${minutes / 1440} дн.`;
  if (minutes % 60 === 0) return `${minutes / 60} ч.`;
  return `${minutes} мин.`;
}

function getTypeLabel(value: TaskJournalType) {
  return TYPE_OPTIONS.find((option) => option.value === value)?.label ?? value;
}

function getPriorityLabel(value: TaskJournalPriority) {
  return PRIORITY_OPTIONS.find((option) => option.value === value)?.label ?? value;
}

function getRoleLabel(role: TaskJournalParticipantRole) {
  return role === 'approver' ? 'Согласующий' : role === 'observer' ? 'Наблюдатель' : 'Исполнитель';
}

function createFormFromTemplate(template: TaskJournalTemplate): TemplateForm {
  return {
    id: template.id,
    name: template.name,
    description: template.description ?? '',
    title: template.title,
    taskDescription: template.taskDescription ?? '',
    type: template.type,
    priority: template.priority,
    plannedDurationMinutes: String(template.plannedDurationMinutes),
    dueOffsetMinutes: String(template.dueOffsetMinutes),
    assigneeUserId: template.assigneeUserId ? String(template.assigneeUserId) : '',
    bindingType: template.bindingType,
    bindingKey: template.bindingKey,
    isActive: template.isActive,
    reminders: template.reminders,
    channels: template.channels,
    tagsText: template.tags.join(', '),
    participants: template.participants.map((participant) => ({
      userId: participant.userId ?? null,
      name: participant.userId ? null : participant.displayName,
      role: participant.role,
      sortOrder: participant.sortOrder ?? 0,
    })),
    observerName: '',
  };
}

function buildPayload(form: TemplateForm, assignees: TaskJournalAssignee[]): TaskJournalTemplateUpsertPayload {
  return {
    name: form.name.trim(),
    description: form.description.trim() || null,
    title: form.title.trim(),
    taskDescription: form.taskDescription.trim() || null,
    type: form.type,
    priority: form.priority,
    plannedDurationMinutes: clampMinutes(form.plannedDurationMinutes, 60),
    dueOffsetMinutes: clampMinutes(form.dueOffsetMinutes, 0),
    assigneeUserId: form.assigneeUserId ? Number(form.assigneeUserId) : null,
    assigneeName: form.assigneeUserId ? getAssigneeName(assignees, form.assigneeUserId) : '',
    bindingType: form.bindingType,
    bindingKey: form.bindingType === 'none' ? '' : form.bindingKey.trim(),
    isActive: form.isActive,
    reminders: form.reminders,
    channels: form.channels,
    tags: splitTags(form.tagsText),
    participants: form.participants.map((participant, index) => ({
      ...participant,
      name: participant.userId ? null : participant.name,
      sortOrder: index + 1,
    })),
  };
}

function templateToTaskDefaults(template: TaskJournalTemplate): Partial<TaskJournalUpsertPayload> {
  const defaults: Partial<TaskJournalUpsertPayload> = {
    title: template.title,
    description: template.taskDescription ?? '',
    type: template.type,
    priority: template.priority,
    plannedDurationMinutes: template.plannedDurationMinutes,
    assigneeUserId: template.assigneeUserId ?? null,
    assigneeName: template.assigneeName,
    reminders: template.reminders,
    channels: template.channels,
    tags: template.tags,
    participants: template.participants.map((participant) => ({
      userId: participant.userId ?? null,
      role: participant.role,
      displayName: participant.displayName,
      email: participant.email,
      sortOrder: participant.sortOrder,
    })),
  };

  if (template.dueOffsetMinutes > 0) {
    defaults.dueAt = new Date(Date.now() + template.dueOffsetMinutes * 60_000).toISOString();
  }

  return defaults;
}

export function TaskTemplatesPanel({ assignees, canCreate, canEdit, canDelete, onUseTemplate }: TaskTemplatesPanelProps) {
  const toast = useToast();
  const { confirm } = useDialog();
  const [templates, setTemplates] = useState<TaskJournalTemplate[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [activeFilter, setActiveFilter] = useState<'all' | 'active' | 'inactive'>('active');
  const [typeFilter, setTypeFilter] = useState<TaskJournalType | 'all'>('all');
  const [priorityFilter, setPriorityFilter] = useState<TaskJournalPriority | 'all'>('all');
  const [formOpen, setFormOpen] = useState(false);
  const [form, setForm] = useState<TemplateForm>(() => createEmptyForm());

  const loadTemplates = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const items = await apiService.getTaskJournalTemplates({
        search: search.trim() || undefined,
        active: activeFilter === 'all' ? undefined : activeFilter === 'active',
        type: typeFilter,
        priority: priorityFilter,
      });
      setTemplates(items);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось загрузить шаблоны задач.'));
    } finally {
      setLoading(false);
    }
  }, [activeFilter, priorityFilter, search, typeFilter]);

  useEffect(() => {
    void loadTemplates();
  }, [loadTemplates]);

  const assigneeById = useMemo(() => new Map(assignees.map((item) => [item.id, item])), [assignees]);
  const canSaveForm = form.name.trim().length > 0 && form.title.trim().length > 0 && (form.id ? canEdit : canCreate);

  const update = <K extends keyof TemplateForm>(key: K, value: TemplateForm[K]) => {
    setForm((current) => ({ ...current, [key]: value }));
  };

  const openCreate = () => {
    setForm(createEmptyForm());
    setFormOpen(true);
  };

  const openEdit = (template: TaskJournalTemplate) => {
    setForm(createFormFromTemplate(template));
    setFormOpen(true);
  };

  const addUserParticipant = (role: Extract<TaskJournalParticipantRole, 'approver' | 'observer'>, userId: string) => {
    const id = Number(userId);
    if (!id || form.participants.some((participant) => participant.role === role && participant.userId === id)) {
      return;
    }
    setForm((current) => ({
      ...current,
      participants: [
        ...current.participants,
        { role, userId: id, name: null, sortOrder: current.participants.length + 1 },
      ],
    }));
  };

  const addObserverByName = () => {
    const name = form.observerName.trim();
    if (!name) return;
    setForm((current) => ({
      ...current,
      observerName: '',
      participants: [
        ...current.participants,
        { role: 'observer', userId: null, name, sortOrder: current.participants.length + 1 },
      ],
    }));
  };

  const removeParticipant = (index: number) => {
    setForm((current) => ({
      ...current,
      participants: current.participants.filter((_, itemIndex) => itemIndex !== index),
    }));
  };

  const saveTemplate = async () => {
    if (!canSaveForm) {
      toast.error('Укажите название шаблона и наименование задачи.', 'Шаблоны задач');
      return;
    }

    setSaving(true);
    try {
      const payload = buildPayload(form, assignees);
      const saved = form.id
        ? await apiService.updateTaskJournalTemplate(form.id, payload)
        : await apiService.createTaskJournalTemplate(payload);
      setTemplates((current) => {
        const withoutSaved = current.filter((item) => item.id !== saved.id);
        return [saved, ...withoutSaved].sort((left, right) => Number(right.isActive) - Number(left.isActive) || left.name.localeCompare(right.name));
      });
      setForm(createFormFromTemplate(saved));
      setFormOpen(false);
      toast.success(form.id ? 'Шаблон задачи обновлен.' : 'Шаблон задачи создан.', 'Шаблоны задач');
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось сохранить шаблон задачи.'), 'Шаблоны задач');
    } finally {
      setSaving(false);
    }
  };

  const deleteTemplate = async (template: TaskJournalTemplate) => {
    const accepted = await confirm({
      title: 'Удалить шаблон задачи?',
      description: `Шаблон «${template.name}» будет удален из библиотеки.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!accepted) return;

    try {
      await apiService.deleteTaskJournalTemplate(template.id);
      setTemplates((current) => current.filter((item) => item.id !== template.id));
      toast.success('Шаблон задачи удален.', 'Шаблоны задач');
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить шаблон задачи.'), 'Шаблоны задач');
    }
  };

  return (
    <section className="mb-4 rounded-xl border border-slate-200 bg-white p-3 shadow-sm" data-testid="task-templates-panel">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <div className="flex items-center gap-2 text-base font-semibold text-slate-900">
            <ClipboardPlus className="h-5 w-5 text-blue-600" />
            Шаблоны задач
          </div>
          <div className="mt-1 text-sm text-slate-500">
            Сохраняйте типовые задачи с участниками, сроками, напоминаниями и применяйте их к новой задаче.
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => void loadTemplates()}
            className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
          >
            {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            Обновить
          </button>
          {canCreate ? (
            <button
              type="button"
              onClick={openCreate}
              className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-3 py-2 text-sm font-semibold text-white transition hover:bg-slate-800"
            >
              <Plus className="h-4 w-4" />
              Новый шаблон
            </button>
          ) : null}
        </div>
      </div>

      <div className="mt-3 grid gap-2 md:grid-cols-[minmax(220px,1fr)_160px_160px_160px]">
        <label className="relative block">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder="Поиск по названию, задаче, тегу"
            className="h-10 w-full rounded-lg border border-slate-200 bg-white pl-9 pr-3 text-sm text-slate-700 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
          />
        </label>
        <SystemSelect value={activeFilter} onChange={(event) => setActiveFilter(event.target.value as typeof activeFilter)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none focus:border-blue-400">
          <option value="active">Активные</option>
          <option value="all">Все</option>
          <option value="inactive">Скрытые</option>
        </SystemSelect>
        <SystemSelect value={typeFilter} onChange={(event) => setTypeFilter(event.target.value as TaskJournalType | 'all')} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none focus:border-blue-400">
          <option value="all">Все типы</option>
          {TYPE_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
        </SystemSelect>
        <SystemSelect value={priorityFilter} onChange={(event) => setPriorityFilter(event.target.value as TaskJournalPriority | 'all')} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none focus:border-blue-400">
          <option value="all">Все приоритеты</option>
          {PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
        </SystemSelect>
      </div>

      {error ? <div className="mt-3 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{error}</div> : null}

      {formOpen ? (
        <div className="mt-3 rounded-xl border border-blue-100 bg-blue-50/40 p-3" data-testid="task-template-form">
          <div className="flex items-center justify-between gap-3">
            <div className="text-sm font-semibold text-slate-900">{form.id ? 'Редактировать шаблон' : 'Новый шаблон задачи'}</div>
            <button type="button" onClick={() => setFormOpen(false)} className="rounded-lg p-2 text-slate-500 transition hover:bg-white hover:text-slate-800" aria-label="Закрыть форму шаблона">
              <X className="h-4 w-4" />
            </button>
          </div>

          <div className="mt-3 grid gap-3 lg:grid-cols-2">
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Название шаблона</span>
              <input value={form.name} onChange={(event) => update('name', event.target.value)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400" />
            </label>
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Наименование задачи</span>
              <input value={form.title} onChange={(event) => update('title', event.target.value)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400" />
            </label>
            <label className="space-y-1 lg:col-span-2">
              <span className="text-xs font-semibold text-slate-500">Описание шаблона</span>
              <input value={form.description} onChange={(event) => update('description', event.target.value)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400" />
            </label>
            <label className="space-y-1 lg:col-span-2">
              <span className="text-xs font-semibold text-slate-500">Описание задачи</span>
              <textarea value={form.taskDescription} onChange={(event) => update('taskDescription', event.target.value)} rows={3} className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400" />
            </label>
          </div>

          <div className="mt-3 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Тип</span>
              <SystemSelect value={form.type} onChange={(event) => update('type', event.target.value as TaskJournalType)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400">
                {TYPE_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </SystemSelect>
            </label>
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Приоритет</span>
              <SystemSelect value={form.priority} onChange={(event) => update('priority', event.target.value as TaskJournalPriority)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400">
                {PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </SystemSelect>
            </label>
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Длительность, мин.</span>
              <input type="number" min={0} value={form.plannedDurationMinutes} onChange={(event) => update('plannedDurationMinutes', event.target.value)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400" />
            </label>
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Срок через, мин.</span>
              <input type="number" min={0} value={form.dueOffsetMinutes} onChange={(event) => update('dueOffsetMinutes', event.target.value)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400" />
            </label>
          </div>

          <div className="mt-3 grid gap-3 lg:grid-cols-3">
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Исполнитель</span>
              <SystemSelect value={form.assigneeUserId} onChange={(event) => update('assigneeUserId', event.target.value)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400">
                <option value="">Не задан</option>
                {assignees.map((assignee) => <option key={assignee.id} value={assignee.id}>{assignee.name}</option>)}
              </SystemSelect>
            </label>
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Привязка</span>
              <SystemSelect value={form.bindingType} onChange={(event) => update('bindingType', event.target.value as TaskJournalTemplateBindingType)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400">
                {BINDING_TYPE_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </SystemSelect>
            </label>
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Ключ этапа или типа</span>
              <input value={form.bindingKey} disabled={form.bindingType === 'none'} onChange={(event) => update('bindingKey', event.target.value)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400 disabled:bg-slate-100" />
            </label>
          </div>

          <div className="mt-3 grid gap-3 lg:grid-cols-2">
            <div className="rounded-lg border border-slate-200 bg-white p-3">
              <div className="text-xs font-semibold uppercase text-slate-500">Уведомления</div>
              <div className="mt-2 flex flex-wrap gap-2">
                {REMINDER_OPTIONS.map((option) => (
                  <label key={option.value} className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-2 py-1.5 text-sm text-slate-700">
                    <SystemCheckbox type="checkbox" checked={form.reminders.includes(option.value)} onChange={() => update('reminders', toggleValue(form.reminders, option.value))} />
                    {option.label}
                  </label>
                ))}
              </div>
              <div className="mt-2 flex flex-wrap gap-2">
                {CHANNEL_OPTIONS.map((option) => (
                  <label key={option.value} className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-2 py-1.5 text-sm text-slate-700">
                    <SystemCheckbox type="checkbox" checked={form.channels.includes(option.value)} onChange={() => update('channels', toggleValue(form.channels, option.value))} />
                    {option.label}
                  </label>
                ))}
              </div>
            </div>

            <div className="rounded-lg border border-slate-200 bg-white p-3">
              <div className="text-xs font-semibold uppercase text-slate-500">Участники</div>
              <div className="mt-2 grid gap-2 sm:grid-cols-2">
                <SystemSelect aria-label="Добавить согласующего в шаблон" defaultValue="" onChange={(event) => { addUserParticipant('approver', event.target.value); event.currentTarget.value = ''; }} className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm">
                  <option value="">Добавить согласующего</option>
                  {assignees.map((assignee) => <option key={assignee.id} value={assignee.id}>{assignee.name}</option>)}
                </SystemSelect>
                <SystemSelect aria-label="Добавить наблюдателя в шаблон" defaultValue="" onChange={(event) => { addUserParticipant('observer', event.target.value); event.currentTarget.value = ''; }} className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm">
                  <option value="">Добавить наблюдателя</option>
                  {assignees.map((assignee) => <option key={assignee.id} value={assignee.id}>{assignee.name}</option>)}
                </SystemSelect>
              </div>
              <div className="mt-2 flex gap-2">
                <input value={form.observerName} onChange={(event) => update('observerName', event.target.value)} placeholder="Внешний наблюдатель" className="h-9 min-w-0 flex-1 rounded-lg border border-slate-200 px-2 text-sm" />
                <button type="button" onClick={addObserverByName} className="rounded-lg border border-slate-200 bg-white px-3 text-sm font-semibold text-slate-700">Добавить</button>
              </div>
              <div className="mt-2 flex flex-wrap gap-2">
                {form.participants.length === 0 ? (
                  <span className="text-sm text-slate-500">Участники не выбраны.</span>
                ) : form.participants.map((participant, index) => {
                  const name = participant.userId ? assigneeById.get(participant.userId)?.name ?? `ID ${participant.userId}` : participant.name ?? '';
                  return (
                    <span key={`${participant.role}-${participant.userId ?? participant.name}-${index}`} className="inline-flex items-center gap-1 rounded-full border border-blue-100 bg-blue-50 px-2 py-1 text-xs font-semibold text-blue-700">
                      {getRoleLabel(participant.role)}: {name}
                      <button type="button" onClick={() => removeParticipant(index)} aria-label={`Удалить ${name}`} className="rounded-full p-0.5 hover:bg-blue-100">
                        <X className="h-3 w-3" />
                      </button>
                    </span>
                  );
                })}
              </div>
            </div>
          </div>

          <div className="mt-3 grid gap-3 lg:grid-cols-[1fr_auto] lg:items-center">
            <label className="space-y-1">
              <span className="text-xs font-semibold text-slate-500">Теги через запятую</span>
              <input value={form.tagsText} onChange={(event) => update('tagsText', event.target.value)} className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400" />
            </label>
            <label className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700">
              <SystemCheckbox type="checkbox" checked={form.isActive} onChange={(event) => update('isActive', event.target.checked)} />
              Активный шаблон
            </label>
          </div>

          <div className="mt-3 flex flex-wrap justify-end gap-2">
            <button type="button" onClick={() => setFormOpen(false)} className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700">Отмена</button>
            <button type="button" onClick={() => void saveTemplate()} disabled={!canSaveForm || saving} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60">
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
              Сохранить шаблон
            </button>
          </div>
        </div>
      ) : null}

      <div className="mt-3 overflow-x-auto">
        <table className="min-w-full divide-y divide-slate-200 text-sm">
          <thead className="bg-slate-50 text-xs font-semibold uppercase text-slate-500">
            <tr>
              <th className="px-3 py-2 text-left">Шаблон</th>
              <th className="px-3 py-2 text-left">Задача</th>
              <th className="px-3 py-2 text-left">Поля</th>
              <th className="px-3 py-2 text-left">Участники</th>
              <th className="px-3 py-2 text-right">Действия</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 bg-white">
            {loading ? (
              <tr><td colSpan={5} className="px-3 py-8 text-center text-slate-500">Загружаем шаблоны...</td></tr>
            ) : templates.length === 0 ? (
              <tr><td colSpan={5} className="px-3 py-8 text-center text-slate-500">Шаблоны задач не найдены.</td></tr>
            ) : templates.map((template) => (
              <tr key={template.id} className="align-top">
                <td className="px-3 py-3">
                  <div className="font-semibold text-slate-900">{template.name}</div>
                  {template.description ? <div className="mt-1 max-w-sm text-xs text-slate-500">{template.description}</div> : null}
                  <div className="mt-2 inline-flex rounded-full border border-slate-200 px-2 py-0.5 text-xs font-semibold text-slate-500">
                    {template.isActive ? 'Активный' : 'Скрытый'}
                  </div>
                </td>
                <td className="px-3 py-3">
                  <div className="font-medium text-slate-800">{template.title}</div>
                  {template.taskDescription ? <div className="mt-1 max-w-md text-xs text-slate-500">{template.taskDescription}</div> : null}
                  {template.tags.length > 0 ? (
                    <div className="mt-2 flex flex-wrap gap-1">
                      {template.tags.map((tag) => <span key={tag} className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-600">{tag}</span>)}
                    </div>
                  ) : null}
                </td>
                <td className="px-3 py-3 text-slate-600">
                  <div>{getTypeLabel(template.type)} · {getPriorityLabel(template.priority)}</div>
                  <div>Срок: {formatOffset(template.dueOffsetMinutes)}</div>
                  <div>Длительность: {template.plannedDurationMinutes} мин.</div>
                  <div>Исполнитель: {template.assigneeName || 'не задан'}</div>
                </td>
                <td className="px-3 py-3">
                  {template.participants.length === 0 ? (
                    <div className="text-slate-500">Не выбраны</div>
                  ) : (
                    <div className="space-y-1">
                      {template.participants.map((participant, index) => (
                        <div key={`${participant.role}-${participant.userId ?? participant.displayName}-${index}`} className="inline-flex items-center gap-1 rounded-full bg-blue-50 px-2 py-1 text-xs font-semibold text-blue-700">
                          <UsersRound className="h-3 w-3" />
                          {getRoleLabel(participant.role)}: {participant.displayName}
                        </div>
                      ))}
                    </div>
                  )}
                </td>
                <td className="px-3 py-3">
                  <div className="flex flex-wrap justify-end gap-2">
                    <button type="button" onClick={() => onUseTemplate(templateToTaskDefaults(template))} className="inline-flex items-center gap-1 rounded-lg border border-emerald-200 bg-emerald-50 px-2 py-1.5 text-xs font-semibold text-emerald-700 transition hover:bg-emerald-100">
                      <ClipboardPlus className="h-3.5 w-3.5" />
                      Применить
                    </button>
                    {canEdit ? (
                      <button type="button" onClick={() => openEdit(template)} className="inline-flex items-center gap-1 rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs font-semibold text-slate-700 transition hover:bg-slate-50">
                        <Edit3 className="h-3.5 w-3.5" />
                        Изменить
                      </button>
                    ) : null}
                    {canDelete ? (
                      <button type="button" onClick={() => void deleteTemplate(template)} className="inline-flex items-center gap-1 rounded-lg border border-rose-200 bg-rose-50 px-2 py-1.5 text-xs font-semibold text-rose-700 transition hover:bg-rose-100">
                        <Trash2 className="h-3.5 w-3.5" />
                        Удалить
                      </button>
                    ) : null}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
