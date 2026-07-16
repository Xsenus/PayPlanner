import { useCallback, useEffect, useState } from 'react';
import { RefreshCcw, Save } from 'lucide-react';
import { apiService, type TaskJournalAutomationDiagnosticsDto, type TaskJournalSettingsDto } from '../../services/api';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';

const DEFAULT_SETTINGS: TaskJournalSettingsDto = {
  restrictExecutorEditing: true,
  allowExecutorDeadlineChangeRequests: true,
  executorCompletionAllowedUserIds: [],
  requireCompletionResult: true,
  dailyOverdueReminderEnabled: true,
  allowAdminCompleteAnyTask: true,
  approvalDueDays: 5,
  taskCardWindowMode: 'legacy',
  caseInactivityAutomationEnabled: true,
  caseInactivityDays: 14,
  caseInactivityLimit: 25,
  caseInactivityDefaultAssigneeUserId: null,
  caseInactivityEscalationDays: 30,
  caseInactivityEscalationUserId: null,
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
          setSettings(response);
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
      setSettings(response);
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

        <label className="flex items-start gap-3 rounded-2xl border border-gray-200 bg-white p-4 text-sm text-gray-700">
          <input
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
          <input
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
                  <input
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
          <input
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
          <input
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
          <input
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
            <input
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
            <span className="text-sm font-medium text-gray-700">Резервный исполнитель</span>
            <select
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
            </select>
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
            <select
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
            </select>
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
