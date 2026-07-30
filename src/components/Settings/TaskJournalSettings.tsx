import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import { useCallback, useEffect, useState } from 'react';
import { RefreshCcw, RotateCcw, Save } from 'lucide-react';
import { apiService, type TaskJournalAutomationDiagnosticsDto, type TaskJournalSettingsDto } from '../../services/api';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { USER_TIME_ZONE_OPTIONS } from '../../utils/timeZoneOptions';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';
import {
  cloneDefaultTaskStickerStyles,
  getTaskStickerInlineStyle,
  normalizeTaskStickerStyles,
  TASK_STICKER_DEFINITIONS,
  type TaskStickerStyle,
} from '../../utils/taskStickerStyles';

const DEFAULT_SETTINGS: TaskJournalSettingsDto = {
  restrictExecutorEditing: true,
  allowExecutorDeadlineChangeRequests: true,
  executorCompletionAllowedUserIds: [],
  requireCompletionResult: true,
  dailyOverdueReminderEnabled: true,
  allowAdminCompleteAnyTask: true,
  approvalDueDays: 5,
  taskCardWindowMode: 'legacy',
  approvalQuickActionMode: 'completionButton',
  caseInactivityAutomationEnabled: true,
  caseInactivityDays: 14,
  caseInactivityLimit: 25,
  caseInactivityDefaultAssigneeUserId: null,
  caseInactivityEscalationDays: 30,
  caseInactivityEscalationUserId: null,
  caseInactivityRunLocalTime: '09:00',
  caseInactivityScheduleTimeZone: 'Europe/Moscow',
  stickerStyles: cloneDefaultTaskStickerStyles(),
};

function formatDiagnosticDate(value?: string | null) {
  if (!value) {
    return 'Еще не было';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return date.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function TaskStickerStyleEditor({
  title,
  style,
  disabled,
  onChange,
  onReset,
}: {
  title: string;
  style: TaskStickerStyle;
  disabled: boolean;
  onChange: (nextStyle: TaskStickerStyle) => void;
  onReset: () => void;
}) {
  const update = <K extends keyof TaskStickerStyle>(field: K, value: TaskStickerStyle[K]) => {
    onChange({ ...style, [field]: value });
  };

  const colorFields = [
    { key: 'backgroundColor' as const, label: 'Фон' },
    { key: 'textColor' as const, label: 'Текст' },
    { key: 'borderColor' as const, label: 'Рамка' },
  ];

  return (
    <article className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h6 className="text-sm font-semibold text-slate-800">{title}</h6>
          <span
            className="mt-2 inline-flex max-w-full rounded-full border px-2.5 py-1 text-[11px] font-semibold"
            style={getTaskStickerInlineStyle(style)}
            title={style.label}
          >
            <span className="truncate">{style.shortLabel}</span>
          </span>
        </div>
        <button
          type="button"
          onClick={onReset}
          disabled={disabled}
          className="inline-flex h-8 shrink-0 items-center gap-1 rounded-lg border border-slate-200 px-2 text-xs font-semibold text-slate-600 transition hover:bg-slate-50 disabled:opacity-50"
          title={`Сбросить оформление «${title}»`}
          aria-label={`Сбросить оформление «${title}»`}
        >
          <RotateCcw className="h-3.5 w-3.5" />
          Сбросить
        </button>
      </div>

      <div className="mt-3 grid gap-2 sm:grid-cols-2">
        <label className="text-xs font-medium text-slate-600">
          Полное название
          <input
            value={style.label}
            onChange={(event) => update('label', event.target.value)}
            disabled={disabled}
            maxLength={80}
            aria-label={`${title}: полное название`}
            className="mt-1 h-9 w-full rounded-lg border border-slate-200 px-2.5 text-sm text-slate-800 outline-none focus:border-blue-400"
          />
        </label>
        <label className="text-xs font-medium text-slate-600">
          Короткое название
          <input
            value={style.shortLabel}
            onChange={(event) => update('shortLabel', event.target.value)}
            disabled={disabled}
            maxLength={30}
            aria-label={`${title}: короткое название`}
            className="mt-1 h-9 w-full rounded-lg border border-slate-200 px-2.5 text-sm text-slate-800 outline-none focus:border-blue-400"
          />
        </label>
      </div>

      <div className="mt-2 grid gap-2 sm:grid-cols-3">
        {colorFields.map((field) => (
          <label key={field.key} className="text-xs font-medium text-slate-600">
            {field.label}
            <span className="mt-1 flex h-9 items-center gap-2 rounded-lg border border-slate-200 bg-white px-2">
              <input
                type="color"
                value={style[field.key]}
                onChange={(event) => update(field.key, event.target.value)}
                disabled={disabled}
                aria-label={`${title}: ${field.label.toLowerCase()}`}
                className="h-6 w-7 cursor-pointer rounded border-0 bg-transparent p-0"
              />
              <span className="truncate font-mono text-[11px] uppercase text-slate-500">{style[field.key]}</span>
            </span>
          </label>
        ))}
      </div>
    </article>
  );
}

export function TaskJournalSettings() {
  const toast = useToast();
  const [settings, setSettings] = useState<TaskJournalSettingsDto>(DEFAULT_SETTINGS);
  const [assignees, setAssignees] = useState<TaskJournalAssignee[]>([]);
  const [diagnostics, setDiagnostics] = useState<TaskJournalAutomationDiagnosticsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [diagnosticsLoading, setDiagnosticsLoading] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const refreshDiagnostics = useCallback(async () => {
    setDiagnosticsLoading(true);
    try {
      const response = await apiService.getTaskJournalAutomationDiagnostics();
      setDiagnostics(response);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось обновить диагностику автоконтроля дел.'), 'Автоконтроль дел');
    } finally {
      setDiagnosticsLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    let active = true;
    setLoading(true);
    Promise.all([
      apiService.getTaskJournalSettings(),
      apiService.getTaskJournalAssignees().catch(() => [] as TaskJournalAssignee[]),
      apiService.getTaskJournalAutomationDiagnostics().catch(() => null),
    ])
      .then(([response, nextAssignees, nextDiagnostics]) => {
        if (active) {
          setSettings({ ...response, stickerStyles: normalizeTaskStickerStyles(response.stickerStyles) });
          setAssignees(nextAssignees);
          setDiagnostics(nextDiagnostics);
        }
      })
      .catch((error) => {
        if (active) {
          toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить настройки журнала задач.'), 'Журнал задач');
        }
      })
      .finally(() => {
        if (active) {
          setLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [toast]);

  const save = async () => {
    setSaving(true);
    setMessage(null);
    try {
      const response = await apiService.updateTaskJournalSettings(settings);
      setSettings({ ...response, stickerStyles: normalizeTaskStickerStyles(response.stickerStyles) });
      void refreshDiagnostics();
      setMessage('Настройки сохранены.');
      toast.success('Настройки журнала задач сохранены.', 'Журнал задач');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить настройки журнала задач.'), 'Журнал задач');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="mb-6">
        <h4 className="text-xl font-semibold text-gray-900">Журнал задач</h4>
      </div>

      <div className="space-y-3">
        <div className="rounded-2xl border border-blue-100 bg-blue-50/60 p-3 text-sm text-gray-700">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
            <div>
              <div className="font-semibold text-gray-900">Окно карточки задачи</div>
              <div className="mt-1 text-gray-600">
                Администратор выбирает вид карточки сразу для всех сотрудников. Старая карточка остается доступной для быстрого возврата.
              </div>
            </div>
            <div className="inline-flex rounded-xl bg-white p-1 shadow-sm ring-1 ring-blue-100" role="group" aria-label="Окно карточки задачи">
              {([
                { value: 'legacy', label: 'Старая' },
                { value: 'modern', label: 'Новая' },
              ] as const).map((option) => (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => setSettings((prev) => ({ ...prev, taskCardWindowMode: option.value }))}
                  disabled={loading || saving}
                  className={`rounded-lg px-3 py-1.5 text-sm font-semibold transition ${
                    settings.taskCardWindowMode === option.value
                      ? 'bg-blue-600 text-white shadow-sm'
                      : 'text-slate-600 hover:bg-blue-50'
                  } disabled:cursor-not-allowed disabled:opacity-60`}
                  aria-pressed={settings.taskCardWindowMode === option.value}
                >
                  {option.label}
                </button>
              ))}
            </div>
          </div>
        </div>

        <section className="rounded-2xl border border-sky-200 bg-sky-50/40 p-3 text-sm text-gray-700">
          <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
            <div>
              <h5 className="font-semibold text-gray-900">Стикеры задач</h5>
              <p className="mt-1 max-w-3xl text-gray-500">
                Настройте единые названия и цвета меток приоритета и учёта в счёте. Оформление применяется для всех сотрудников в журнале, на доске и в карточках задач.
              </p>
            </div>
            <button
              type="button"
              onClick={() => setSettings((prev) => ({ ...prev, stickerStyles: cloneDefaultTaskStickerStyles() }))}
              disabled={loading || saving}
              className="inline-flex h-9 shrink-0 items-center gap-2 rounded-lg border border-sky-200 bg-white px-3 text-xs font-semibold text-sky-700 transition hover:bg-sky-50 disabled:opacity-50"
            >
              <RotateCcw className="h-4 w-4" />
              Сбросить все
            </button>
          </div>

          {([
            { group: 'priority' as const, title: 'Приоритеты' },
            { group: 'billing' as const, title: 'Учёт в счёте' },
          ]).map((section) => (
            <div key={section.group} className="mt-4">
              <h6 className="mb-2 text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">{section.title}</h6>
              <div className="grid gap-3 xl:grid-cols-2">
                {TASK_STICKER_DEFINITIONS
                  .filter((definition) => definition.group === section.group)
                  .map((definition) => (
                    <TaskStickerStyleEditor
                      key={definition.key}
                      title={definition.title}
                      style={settings.stickerStyles[definition.key]}
                      disabled={loading || saving}
                      onChange={(nextStyle) => setSettings((prev) => ({
                        ...prev,
                        stickerStyles: {
                          ...prev.stickerStyles,
                          [definition.key]: nextStyle,
                        },
                      }))}
                      onReset={() => {
                        const defaults = cloneDefaultTaskStickerStyles();
                        setSettings((prev) => ({
                          ...prev,
                          stickerStyles: {
                            ...prev.stickerStyles,
                            [definition.key]: defaults[definition.key],
                          },
                        }));
                      }}
                    />
                  ))}
              </div>
            </div>
          ))}
        </section>

        <div className="rounded-2xl border border-violet-200 bg-violet-50/50 p-3 text-sm text-gray-700">
          <div className="font-semibold text-gray-900">Действие кнопки завершения при согласовании</div>
          <div className="mt-1 text-gray-600">
            Выберите, как сотрудник принимает решение по задаче со статусом «На согласовании». Обычное завершение такой задачи блокируется в обоих режимах.
          </div>
          <div className="mt-3 grid gap-2 md:grid-cols-2" role="radiogroup" aria-label="Действие при согласовании задачи">
            {([
              {
                value: 'completionButton',
                title: 'Через кнопку «Завершить»',
                description: 'Зелёная кнопка открывает окно согласования. Отдельный значок решения не показывается.',
              },
              {
                value: 'separateButton',
                title: 'Отдельным значком согласования',
                description: 'Кнопка завершения неактивна, а решение открывается отдельным значком согласования.',
              },
            ] as const).map((option) => (
              <button
                key={option.value}
                type="button"
                role="radio"
                aria-checked={settings.approvalQuickActionMode === option.value}
                onClick={() => setSettings((prev) => ({ ...prev, approvalQuickActionMode: option.value }))}
                disabled={loading || saving}
                className={`rounded-xl border p-3 text-left transition ${
                  settings.approvalQuickActionMode === option.value
                    ? 'border-violet-400 bg-white text-violet-950 shadow-sm ring-1 ring-violet-200'
                    : 'border-violet-100 bg-white/70 text-slate-700 hover:border-violet-300'
                } disabled:cursor-not-allowed disabled:opacity-60`}
              >
                <span className="block font-semibold">{option.title}</span>
                <span className="mt-1 block text-xs font-normal text-slate-500">{option.description}</span>
              </button>
            ))}
          </div>
        </div>

        <label className="flex items-start gap-3 rounded-2xl border border-gray-200 bg-white p-4 text-sm text-gray-700">
          <SystemCheckbox
            type="checkbox"
            checked={settings.restrictExecutorEditing}
            onChange={(event) => setSettings((prev) => ({ ...prev, restrictExecutorEditing: event.target.checked }))}
            disabled={loading || saving}
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          <span>
            <span className="block font-semibold text-gray-900">Исполнитель открывает задачу только на просмотр</span>
            <span className="mt-1 block text-gray-500">Поля задачи, срок, участники, файлы и удаление доступны постановщику или администратору.</span>
          </span>
        </label>

        <label className="flex items-start gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-3 text-sm text-gray-700">
          <SystemCheckbox
            type="checkbox"
            checked={settings.dailyOverdueReminderEnabled}
            onChange={(event) => setSettings((prev) => ({ ...prev, dailyOverdueReminderEnabled: event.target.checked }))}
            disabled={loading || saving}
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-amber-600 focus:ring-amber-500"
          />
          <span>
            <span className="block font-semibold text-gray-900">Ежедневно напоминать сотрудникам о просроченных задачах</span>
            <span className="mt-1 block text-gray-500">Один раз в день после входа CRM покажет сотруднику количество его просроченных задач и предложит сразу открыть их список.</span>
          </span>
        </label>

        <section className="rounded-2xl border border-emerald-200 bg-emerald-50/40 p-3 text-sm text-gray-700">
          <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
            <div>
              <h5 className="font-semibold text-gray-900">Кто может завершать назначенные задачи</h5>
              <p className="mt-1 text-gray-500">
                Выбранные сотрудники увидят кнопку «Завершить» в карточке задачи, созданной другим пользователем. Остальные смогут просматривать задачу, писать комментарии и запрашивать перенос срока, но не закрывать её.
              </p>
            </div>
            <div className="flex shrink-0 gap-2">
              <button
                type="button"
                onClick={() => setSettings((prev) => ({
                  ...prev,
                  executorCompletionAllowedUserIds: assignees.map((assignee) => assignee.id),
                }))}
                disabled={loading || saving || assignees.length === 0}
                className="rounded-lg border border-emerald-200 bg-white px-3 py-1.5 text-xs font-semibold text-emerald-700 hover:bg-emerald-50 disabled:opacity-50"
              >
                Выбрать всех
              </button>
              <button
                type="button"
                onClick={() => setSettings((prev) => ({ ...prev, executorCompletionAllowedUserIds: [] }))}
                disabled={loading || saving || settings.executorCompletionAllowedUserIds.length === 0}
                className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 disabled:opacity-50"
              >
                Очистить
              </button>
            </div>
          </div>
          <div className="mt-3 grid max-h-48 gap-2 overflow-y-auto pr-1 sm:grid-cols-2 xl:grid-cols-3" data-testid="executor-completion-access-list">
            {assignees.length === 0 ? (
              <div className="rounded-lg border border-dashed border-slate-200 bg-white px-3 py-3 text-slate-500 sm:col-span-2 xl:col-span-3">
                {loading ? 'Загружаем сотрудников...' : 'Нет доступных сотрудников.'}
              </div>
            ) : assignees.map((assignee) => {
              const checked = settings.executorCompletionAllowedUserIds.includes(assignee.id);
              return (
                <label key={assignee.id} className="flex cursor-pointer items-start gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 hover:border-emerald-300">
                  <SystemCheckbox
                    type="checkbox"
                    checked={checked}
                    onChange={(event) => setSettings((prev) => ({
                      ...prev,
                      executorCompletionAllowedUserIds: event.target.checked
                        ? Array.from(new Set([...prev.executorCompletionAllowedUserIds, assignee.id]))
                        : prev.executorCompletionAllowedUserIds.filter((id) => id !== assignee.id),
                    }))}
                    disabled={loading || saving}
                    className="mt-0.5 h-4 w-4 rounded border-gray-300 text-emerald-600 focus:ring-emerald-500"
                  />
                  <span className="min-w-0">
                    <span className="block truncate font-medium text-slate-800">{assignee.name}</span>
                    {assignee.email ? <span className="block truncate text-xs text-slate-500">{assignee.email}</span> : null}
                  </span>
                </label>
              );
            })}
          </div>
          <div className="mt-2 text-xs font-medium text-emerald-800">
            Выбрано: {settings.executorCompletionAllowedUserIds.length}
          </div>
        </section>

        <label className="flex items-start gap-3 rounded-2xl border border-gray-200 bg-white p-4 text-sm text-gray-700">
          <SystemCheckbox
            type="checkbox"
            checked={settings.allowExecutorDeadlineChangeRequests}
            onChange={(event) => setSettings((prev) => ({ ...prev, allowExecutorDeadlineChangeRequests: event.target.checked }))}
            disabled={loading || saving || !settings.restrictExecutorEditing}
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-60"
          />
          <span>
            <span className="block font-semibold text-gray-900">Разрешить запрос переноса срока</span>
            <span className="mt-1 block text-gray-500">Запрос фиксируется в истории задачи для постановщика без прямого изменения срока.</span>
          </span>
        </label>

        <label className="flex items-start gap-3 rounded-2xl border border-gray-200 bg-white p-4 text-sm text-gray-700">
          <SystemCheckbox
            type="checkbox"
            checked={settings.requireCompletionResult}
            onChange={(event) => setSettings((prev) => ({ ...prev, requireCompletionResult: event.target.checked }))}
            disabled={loading || saving}
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          <span>
            <span className="block font-semibold text-gray-900">Требовать результат при завершении задачи</span>
            <span className="mt-1 block text-gray-500">Если выключено, задачу можно закрыть без описания результата. Право закрытия по ролям сохраняется без изменений.</span>
          </span>
        </label>

        <label className="block rounded-2xl border border-gray-200 bg-white p-4 text-sm text-gray-700">
          <span className="block font-semibold text-gray-900">Срок согласования задачи, дней</span>
          <span className="mt-1 block text-gray-500">Если в карточке не указан отдельный срок согласования, CRM автоматически поставит срок от даты отправки на согласование.</span>
          <input
            type="number"
            min={1}
            max={365}
            value={settings.approvalDueDays}
            onChange={(event) => setSettings((prev) => ({
              ...prev,
              approvalDueDays: Math.min(365, Math.max(1, Number(event.target.value) || 1)),
            }))}
            disabled={loading || saving}
            className="mt-3 w-full max-w-40 rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100"
            data-testid="task-approval-due-days-input"
          />
        </label>

        <label className="flex items-start gap-3 rounded-2xl border border-gray-200 bg-white p-4 text-sm text-gray-700">
          <SystemCheckbox
            type="checkbox"
            checked={settings.allowAdminCompleteAnyTask}
            onChange={(event) => setSettings((prev) => ({ ...prev, allowAdminCompleteAnyTask: event.target.checked }))}
            disabled={loading || saving}
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          <span>
            <span className="block font-semibold text-gray-900">Администратор может завершать задачи в любом состоянии</span>
            <span className="mt-1 block text-gray-500">Если включено, администратор может закрыть задачу даже до окончания согласования. Для сотрудников порядок согласования не меняется.</span>
          </span>
        </label>
      </div>

      <div className="mt-6 rounded-2xl border border-slate-200 bg-slate-50 p-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <h5 className="text-base font-semibold text-gray-900">Автоконтроль дел</h5>
            <p className="mt-1 text-sm text-gray-500">Система сама создает контрольную задачу, если по делу давно не было задач, просмотра или ручного подтверждения активности.</p>
          </div>
          <label className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700">
            <SystemCheckbox
              type="checkbox"
              checked={settings.caseInactivityAutomationEnabled}
              onChange={(event) => setSettings((prev) => ({ ...prev, caseInactivityAutomationEnabled: event.target.checked }))}
              disabled={loading || saving}
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            />
            Включено
          </label>
        </div>

        <div className="mt-4 grid gap-4 md:grid-cols-3">
          <label className="block">
            <span className="text-sm font-medium text-gray-700">Период без движения, дней</span>
            <input
              type="number"
              min={1}
              max={365}
              value={settings.caseInactivityDays}
              onChange={(event) => setSettings((prev) => ({
                ...prev,
                caseInactivityDays: Math.min(365, Math.max(1, Number(event.target.value) || 1)),
                caseInactivityEscalationDays: Math.max(
                  prev.caseInactivityEscalationDays,
                  Math.min(365, Math.max(1, Number(event.target.value) || 1)),
                ),
              }))}
              disabled={loading || saving || !settings.caseInactivityAutomationEnabled}
              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100"
            />
          </label>

          <label className="block">
            <span className="text-sm font-medium text-gray-700">Лимит задач за запуск</span>
            <input
              type="number"
              min={1}
              max={100}
              value={settings.caseInactivityLimit}
              onChange={(event) => setSettings((prev) => ({
                ...prev,
                caseInactivityLimit: Math.min(100, Math.max(1, Number(event.target.value) || 1)),
              }))}
              disabled={loading || saving || !settings.caseInactivityAutomationEnabled}
              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100"
            />
          </label>

          <label className="block">
            <span className="text-sm font-medium text-gray-700">Время автоматического запуска</span>
            <input
              type="time"
              aria-label="Время автоматического запуска"
              value={settings.caseInactivityRunLocalTime || '09:00'}
              onChange={(event) => setSettings((prev) => ({
                ...prev,
                caseInactivityRunLocalTime: event.target.value || '09:00',
              }))}
              disabled={loading || saving || !settings.caseInactivityAutomationEnabled}
              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100"
            />
            <span className="mt-1 block text-xs text-gray-500">Автоматический проход выполняется один раз в сутки.</span>
          </label>

          <label className="block">
            <span className="text-sm font-medium text-gray-700">Часовой пояс запуска</span>
            <SystemSelect
              aria-label="Часовой пояс запуска"
              value={settings.caseInactivityScheduleTimeZone || 'Europe/Moscow'}
              onChange={(event) => setSettings((prev) => ({
                ...prev,
                caseInactivityScheduleTimeZone: event.target.value,
              }))}
              disabled={loading || saving || !settings.caseInactivityAutomationEnabled}
              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100"
            >
              {USER_TIME_ZONE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </SystemSelect>
            <span className="mt-1 block text-xs text-gray-500">Время выше интерпретируется в выбранном часовом поясе.</span>
          </label>

          <label className="block">
            <span className="text-sm font-medium text-gray-700">Резервный исполнитель</span>
            <SystemSelect
              value={settings.caseInactivityDefaultAssigneeUserId ?? ''}
              onChange={(event) => setSettings((prev) => ({
                ...prev,
                caseInactivityDefaultAssigneeUserId: event.target.value ? Number(event.target.value) : null,
              }))}
              disabled={loading || saving || !settings.caseInactivityAutomationEnabled}
              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100"
            >
              <option value="">Ответственный по делу</option>
              {assignees.map((assignee) => (
                <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
              ))}
            </SystemSelect>
            <span className="mt-1 block text-xs text-gray-500">Используется только если у дела, клиента или юрлица нет ответственного.</span>
          </label>

          <label className="block">
            <span className="text-sm font-medium text-gray-700">Эскалация через, дней</span>
            <input
              type="number"
              min={1}
              max={365}
              value={settings.caseInactivityEscalationDays}
              onChange={(event) => setSettings((prev) => ({
                ...prev,
                caseInactivityEscalationDays: Math.min(365, Math.max(prev.caseInactivityDays, Number(event.target.value) || prev.caseInactivityDays)),
              }))}
              disabled={loading || saving || !settings.caseInactivityAutomationEnabled}
              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100"
            />
            <span className="mt-1 block text-xs text-gray-500">После этого срока контрольная задача станет срочной.</span>
          </label>

          <label className="block md:col-span-2">
            <span className="text-sm font-medium text-gray-700">Ответственный за эскалации</span>
            <SystemSelect
              value={settings.caseInactivityEscalationUserId ?? ''}
              onChange={(event) => setSettings((prev) => ({
                ...prev,
                caseInactivityEscalationUserId: event.target.value ? Number(event.target.value) : null,
              }))}
              disabled={loading || saving || !settings.caseInactivityAutomationEnabled}
              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100"
            >
              <option value="">Без отдельного наблюдателя</option>
              {assignees.map((assignee) => (
                <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
              ))}
            </SystemSelect>
            <span className="mt-1 block text-xs text-gray-500">Если дело превысило порог эскалации, этот пользователь добавится наблюдателем.</span>
          </label>
        </div>

        <div className="mt-4 rounded-2xl border border-slate-200 bg-white p-4">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
            <div>
              <h6 className="text-sm font-semibold uppercase tracking-wide text-slate-500">Диагностика автоконтроля</h6>
              <p className="mt-1 text-sm text-slate-500">
                Проверяет, сколько дел сейчас попадает под автоматическую постановку задач и когда автоматика срабатывала в последний раз.
              </p>
            </div>
            <button
              type="button"
              onClick={() => void refreshDiagnostics()}
              disabled={loading || diagnosticsLoading}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <RefreshCcw className={`h-4 w-4 ${diagnosticsLoading ? 'animate-spin' : ''}`} />
              Обновить
            </button>
          </div>

          <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <div className="rounded-xl border border-slate-100 bg-slate-50 p-3">
              <div className="text-xs font-medium uppercase tracking-wide text-slate-500">Состояние</div>
              <div className="mt-1 text-lg font-semibold text-slate-900">
                {diagnostics ? (diagnostics.enabled ? 'Включен' : 'Выключен') : '—'}
              </div>
            </div>
            <div className="rounded-xl border border-slate-100 bg-slate-50 p-3">
              <div className="text-xs font-medium uppercase tracking-wide text-slate-500">Кандидаты</div>
              <div className="mt-1 text-lg font-semibold text-slate-900">{diagnostics?.visibleCandidateCount ?? '—'}</div>
            </div>
            <div className="rounded-xl border border-slate-100 bg-slate-50 p-3">
              <div className="text-xs font-medium uppercase tracking-wide text-slate-500">Эскалации</div>
              <div className="mt-1 text-lg font-semibold text-slate-900">{diagnostics?.visibleEscalationCandidateCount ?? '—'}</div>
            </div>
            <div className="rounded-xl border border-slate-100 bg-slate-50 p-3">
              <div className="text-xs font-medium uppercase tracking-wide text-slate-500">Создано за 7 дней</div>
              <div className="mt-1 text-lg font-semibold text-slate-900">{diagnostics?.createdTaskCountLast7Days ?? '—'}</div>
            </div>
          </div>

          <dl className="mt-4 grid gap-3 text-sm text-slate-600 md:grid-cols-2">
            <div>
              <dt className="font-medium text-slate-500">Последнее срабатывание</dt>
              <dd className="mt-1 text-slate-900">{formatDiagnosticDate(diagnostics?.lastCreatedAt)}</dd>
            </div>
            <div>
              <dt className="font-medium text-slate-500">Резервный исполнитель</dt>
              <dd className="mt-1 text-slate-900">{diagnostics?.defaultAssigneeName || 'Ответственный по делу'}</dd>
            </div>
            <div>
              <dt className="font-medium text-slate-500">Ответственный за эскалации</dt>
              <dd className="mt-1 text-slate-900">{diagnostics?.escalationUserName || 'Не задан'}</dd>
            </div>
            <div>
              <dt className="font-medium text-slate-500">Порог контроля</dt>
              <dd className="mt-1 text-slate-900">
                {diagnostics ? `${diagnostics.inactivityDays} дн., лимит ${diagnostics.limit}` : '—'}
              </dd>
            </div>
          </dl>

          {diagnostics?.lastCreatedDescription ? (
            <div className="mt-4 rounded-xl border border-blue-100 bg-blue-50 p-3 text-sm text-blue-900">
              {diagnostics.lastCreatedDescription}
            </div>
          ) : null}
        </div>
      </div>

      <div className="mt-6 flex min-h-10 items-center justify-between gap-3">
        <span className="text-sm text-gray-600">{message}</span>
        <button
          type="button"
          onClick={() => void save()}
          disabled={loading || saving}
          className="ml-auto inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <Save className="h-4 w-4" />
          {saving ? 'Сохранение...' : 'Сохранить'}
        </button>
      </div>
    </div>
  );
}
