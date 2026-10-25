import { useEffect, useRef, useState, type ReactNode } from 'react';
import { apiService } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { buildCaseTaskCsv, downloadCaseTaskCsv } from '../../utils/caseTaskCsv';
import { TASK_JOURNAL_EVENT, type TaskJournalAssignee, type TaskJournalItem, type TaskJournalStatus } from '../../utils/taskJournalStore';
import { SystemSelect } from '../ui/SystemSelect';
import { useAuth } from '../../contexts/AuthContext';
import { useTaskBulkBilling } from '../Tasks/useTaskBulkBilling';
import { billingSummary } from '../Tasks/taskBulkBilling';

const STATUS_OPTIONS: { value: TaskJournalStatus; label: string }[] = [
  { value: 'new', label: 'Новая' }, { value: 'inProgress', label: 'В работе' },
  { value: 'waiting', label: 'Ожидание' }, { value: 'onApproval', label: 'На согласовании' },
  { value: 'done', label: 'Выполнена' }, { value: 'cancelled', label: 'Отменена' },
];

interface SelectionControls {
  enabled: boolean;
  selected: Set<number>;
  busy: boolean;
  allSelected: boolean;
  toggle: (id: number) => void;
  toggleAll: () => void;
}

export function CaseTaskActions({ tasks, caseId, loading, canEdit, canEditBilling = canEdit, canExport, statusLabels, onChanged, onBillingChanged = onChanged, children, scopeKey = String(caseId), onBusyChange }: {
  tasks: TaskJournalItem[];
  caseId: number;
  loading: boolean;
  canEdit: boolean;
  canEditBilling?: boolean;
  canExport: boolean;
  statusLabels: Record<string, string>;
  onChanged: (message: string) => void;
  onBillingChanged?: (message: string) => void;
  children: (selection: SelectionControls) => ReactNode;
  scopeKey?: string;
  onBusyChange?: (busy: boolean) => void;
}) {
  const { user } = useAuth();
  const { confirm } = useDialog();
  const [selected, setSelected] = useState<Set<number>>(() => new Set());
  const [action, setAction] = useState('status');
  const [status, setStatus] = useState<TaskJournalStatus>('inProgress');
  const [assigneeId, setAssigneeId] = useState('');
  const [assignees, setAssignees] = useState<TaskJournalAssignee[] | null>(null);
  const [directoryLoading, setDirectoryLoading] = useState(false);
  const [directoryRevision, setDirectoryRevision] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const mounted = useRef(false);
  const saving = useRef(false);
  const current = useRef({ tasks, selected, canEdit, loading, scopeKey, actorId: user?.id });
  current.current = { tasks, selected, canEdit, loading, scopeKey, actorId: user?.id };
  const selectedTasks = tasks.filter((task) => selected.has(task.id));
  const enabled = canEdit || canEditBilling || canExport;
  const billing = useTaskBulkBilling({ tasks: selectedTasks, scopeKey: `${user?.id}:${scopeKey}`, identityKey: String(user?.id),
    scopeLabel: `Задачи дела №${caseId}, выбранная страница`, canEdit: Boolean(user?.id && canEditBilling), loading: loading || busy,
    onApplied: (result) => {
      const done = new Set(result.items.filter((item) => item.outcome === 'updated' || item.outcome === 'unchanged').map((item) => item.id));
      setSelected((previous) => new Set([...previous].filter((id) => !done.has(id))));
      window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT, { detail: { bulkBillingIds: [...done] } }));
      onBillingChanged(billingSummary(result.items));
    },
  });
  const actionsBusy = busy || billing.busy;
  useEffect(() => { onBusyChange?.(actionsBusy); }, [actionsBusy, onBusyChange]);
  useEffect(() => () => { onBusyChange?.(false); }, [onBusyChange]);

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => { setSelected(new Set()); }, [tasks, loading, canEdit, canEditBilling, canExport, scopeKey, user?.id]);
  useEffect(() => {
    if (!canEdit || action !== 'assignee' || assignees !== null || selectedTasks.length === 0) return;
    let cancelled = false;
    setDirectoryLoading(true);
    setError('');
    void apiService.getTaskJournalAssignees().then((items) => {
      if (!cancelled) { setAssignees(items); setDirectoryLoading(false); }
    }).catch((reason: unknown) => {
      if (!cancelled) { setError(extractDisplayErrorMessage(reason, 'Не удалось загрузить исполнителей.')); setDirectoryLoading(false); }
    });
    return () => { cancelled = true; };
  }, [action, assignees, canEdit, directoryRevision, selectedTasks.length]);

  const apply = async () => {
    if (!canEdit || loading || saving.current || billing.busy || selectedTasks.length === 0
      || (action === 'assignee' && (!assigneeId || directoryLoading))) return;
    const ids = selectedTasks.map((task) => task.id);
    saving.current = true;
    setBusy(true);
    setError('');
    try {
      const description = action === 'assignee'
        ? `Назначить исполнителя «${assignees?.find((item) => item.id === Number(assigneeId))?.name ?? ''}».`
        : `Установить статус «${STATUS_OPTIONS.find((item) => item.value === status)?.label}».`;
      if (!await confirm({ title: `Изменить выбранные задачи (${ids.length})?`, description,
        confirmLabel: 'Применить', variant: action === 'status' && status === 'cancelled' ? 'danger' : 'primary' })) return;
      if (!mounted.current || !current.current.canEdit || current.current.loading
        || current.current.scopeKey !== scopeKey || current.current.actorId !== user?.id
        || current.current.tasks !== tasks || current.current.selected !== selected) return;
      const result = await apiService.bulkUpdateTaskJournalItems(action === 'assignee'
        ? { ids, assigneeUserId: Number(assigneeId) } : { ids, status });
      if (!mounted.current || current.current.scopeKey !== scopeKey || current.current.actorId !== user?.id) return;
      const summary = `Изменено: ${result.updated}. Пропущено: ${result.skipped}. Выбор сброшен; пропущенные задачи можно открыть отдельно.`;
      setSelected(new Set());
      window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT));
      onChanged(summary);
    } catch (reason) {
      if (mounted.current) setError(extractDisplayErrorMessage(reason, 'Не удалось изменить задачи. Выбор сохранён.'));
    } finally {
      saving.current = false;
      if (mounted.current) setBusy(false);
    }
  };

  return <>
    {enabled && selectedTasks.length > 0 ? (
      <section aria-label="Действия с выбранными задачами" className="space-y-2 rounded-lg border border-blue-100 bg-blue-50/50 p-3">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm font-semibold">Выбрано на этой странице: {selectedTasks.length}</span>
          {canExport ? <button type="button" disabled={actionsBusy || loading} className="rounded-lg border px-3 py-1.5 text-sm disabled:opacity-50"
            onClick={() => { if (canExport && !loading && !saving.current) downloadCaseTaskCsv(`case-${caseId}-selected-tasks.csv`, buildCaseTaskCsv(selectedTasks, statusLabels)); }}>Экспорт выбранных CSV</button> : null}
          <button type="button" disabled={actionsBusy} onClick={() => setSelected(new Set())} className="rounded-lg px-3 py-1.5 text-sm underline disabled:opacity-50">Снять выбор</button>
        </div>
        {canEditBilling ? <button type="button" disabled={actionsBusy || loading} onClick={billing.begin} className="rounded-lg border px-3 py-1.5 text-sm disabled:opacity-50">Изменить учёт выбранных</button> : null}
        {canEdit ? <>
          <div className="flex flex-wrap items-end gap-2">
            <label className="text-sm">Действие<SystemSelect aria-label="Массовое действие" value={action} disabled={actionsBusy} onChange={(event) => { setAction(event.target.value); setError(''); }}>
              <option value="status">Изменить статус</option><option value="assignee">Сменить исполнителя</option>
            </SystemSelect></label>
            {action === 'status' ? <label className="text-sm">Статус<SystemSelect aria-label="Новый статус выбранных задач" value={status} disabled={actionsBusy} onChange={(event) => setStatus(event.target.value as TaskJournalStatus)}>
              {STATUS_OPTIONS.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
            </SystemSelect></label> : <label className="text-sm">Исполнитель<SystemSelect aria-label="Новый исполнитель выбранных задач" value={assigneeId} disabled={actionsBusy || directoryLoading} onChange={(event) => setAssigneeId(event.target.value)}>
              <option value="">{directoryLoading ? 'Загрузка…' : 'Выберите исполнителя'}</option>
              {assignees?.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
            </SystemSelect></label>}
            <button type="button" disabled={actionsBusy || loading || (action === 'assignee' && (!assigneeId || directoryLoading))} onClick={() => void apply()}
              className="rounded-lg bg-blue-600 px-3 py-1.5 text-sm font-semibold text-white disabled:opacity-50">{busy ? 'Сохранение…' : 'Применить к выбранным'}</button>
            {action === 'assignee' && assignees === null && !directoryLoading && error ? <button type="button" onClick={() => setDirectoryRevision((value) => value + 1)} className="text-sm underline">Повторить загрузку исполнителей</button> : null}
          </div>
          <p className="text-xs text-slate-600">Права проверяются для каждой задачи. Завершение учитывает требование результата и правила согласования; отправка на согласование — состав участников. Решение согласующего принимается отдельно в карточке. Недоступные или уже совпадающие изменения будут пропущены.</p>
        </> : null}
      </section>
    ) : null}
    {error ? <p role="alert" className="text-sm text-rose-700">{error}</p> : null}
    {billing.hasDraft ? <button type="button" onClick={billing.begin} disabled={actionsBusy || !canEditBilling} className="rounded border px-3 py-1.5 text-sm disabled:opacity-50">Открыть зафиксированный учёт задач</button> : null}
    {billing.error ? <p role="alert" className="text-sm text-rose-700">{billing.error}</p> : null}
    {billing.dialog}
    {children({ enabled, selected, busy: actionsBusy || loading,
      allSelected: tasks.length > 0 && selectedTasks.length === tasks.length,
      toggle: (id) => { if (!enabled || actionsBusy || loading || !tasks.some((task) => task.id === id)) return;
        setSelected((previous) => { const next = new Set(previous); if (next.has(id)) next.delete(id); else next.add(id); return next; }); },
      toggleAll: () => { if (!enabled || actionsBusy || loading) return; setSelected(selectedTasks.length === tasks.length ? new Set() : new Set(tasks.map((task) => task.id))); },
    })}
  </>;
}
