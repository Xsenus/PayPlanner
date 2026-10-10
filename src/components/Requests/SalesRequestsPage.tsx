import { useCallback, useEffect, useMemo, useState } from 'react';

import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type {
  SalesRequestClientOption,
  SalesRequestDetail,
  SalesRequestItem,
  SalesRequestSource,
  SalesRequestStatusItem,
} from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { SalesRequestFunnelSettings } from './SalesRequestFunnelSettings';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';

interface SalesRequestsPageProps {
  allowCreate: boolean;
  allowEdit: boolean;
  allowAnalytics: boolean;
  allowConfigure?: boolean;
  initialRequestId?: number | null;
}

const SOURCE_LABELS: Record<SalesRequestSource, string> = {
  Call: 'Звонок',
  Max: 'Макс',
  Avito: 'Авито',
  Site: 'Сайт',
  Other: 'Другое',
};

const TYPE_LABELS: Record<string, string> = { New: 'Новый', Fixed: 'Фиксированный', PerCase: 'По факту' };

const EVENT_LABELS: Record<string, string> = {
  Created: 'Заявка создана',
  StatusChanged: 'Смена статуса',
  FieldsChanged: 'Изменены данные',
  TaskCreated: 'Создана задача',
  TasksForceClosed: 'Задачи закрыты принудительно',
};

const money = (value: number) => value.toLocaleString('ru-RU', { minimumFractionDigits: 0, maximumFractionDigits: 2 });

const emptyForm = () => ({
  source: '' as SalesRequestSource | '',
  query: '',
  clientId: null as number | null,
  newClient: false,
  newName: '',
  newPhone: '',
  newEmail: '',
  caseId: null as number | null,
  summary: '',
  amount: '',
});

/**
 * Раздел «Заявки»: список, создание с поиском клиента и проверкой дел, карточка с переходами по статусам.
 * Доступ задаёт серверный раздел прав «requests»; здесь только отображение и подсказки.
 */
export function SalesRequestsPage({ allowCreate, allowEdit, allowAnalytics, allowConfigure = false, initialRequestId = null }: SalesRequestsPageProps) {
  const toast = useToast();
  const dialog = useDialog();
  const [statuses, setStatuses] = useState<SalesRequestStatusItem[]>([]);
  const [items, setItems] = useState<SalesRequestItem[]>([]);
  const [total, setTotal] = useState(0);
  const [filters, setFilters] = useState({ statusId: '', source: '', q: '', closed: false });
  const [assignees, setAssignees] = useState<TaskJournalAssignee[]>([]);
  const [detail, setDetail] = useState<SalesRequestDetail | null>(null);
  const [creating, setCreating] = useState(false);
  const [form, setForm] = useState(emptyForm);
  const [options, setOptions] = useState<SalesRequestClientOption[]>([]);
  const [selectedClient, setSelectedClient] = useState<SalesRequestClientOption | null>(null);
  const [busy, setBusy] = useState(false);
  const [paymentId, setPaymentId] = useState('');
  const [taskForm, setTaskForm] = useState({ assigneeUserId: '', title: '' });
  const [report, setReport] = useState<string | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);

  const statusById = useMemo(() => new Map(statuses.map((item) => [item.id, item])), [statuses]);
  const userName = useCallback((id?: number | null) => assignees.find((item) => item.id === id)?.name ?? (id ? `№${id}` : ''), [assignees]);

  const loadList = useCallback(async () => {
    try {
      const page = await apiService.getSalesRequests({
        statusId: filters.statusId ? Number(filters.statusId) : undefined,
        source: filters.source || undefined,
        q: filters.q.trim() || undefined,
        closed: filters.closed,
        pageSize: 50,
      });
      setItems(page.items);
      setTotal(page.total);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить заявки.'), 'Заявки');
    }
  }, [filters, toast]);

  useEffect(() => {
    if (initialRequestId) {
      void openDetail(initialRequestId);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialRequestId]);

  const loadStatuses = useCallback(() => {
    void apiService.getSalesRequestStatuses().then(setStatuses).catch(() => setStatuses([]));
  }, []);

  useEffect(() => {
    loadStatuses();
    void apiService.getTaskJournalAssignees().then(setAssignees).catch(() => setAssignees([]));
  }, [loadStatuses]);

  useEffect(() => {
    void loadList();
  }, [loadList]);

  useEffect(() => {
    const text = form.query.trim();
    if (!creating || form.clientId || form.newClient || text.length < 2) {
      setOptions([]);
      return;
    }
    const handle = window.setTimeout(() => {
      void apiService.searchSalesRequestClients(text).then(setOptions).catch(() => setOptions([]));
    }, 250);
    return () => window.clearTimeout(handle);
  }, [creating, form.clientId, form.newClient, form.query]);

  const openDetail = async (id: number) => {
    try {
      setDetail(await apiService.getSalesRequest(id));
      setPaymentId('');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось открыть заявку.'), 'Заявки');
    }
  };

  const run = async (action: () => Promise<void>, failure: string) => {
    setBusy(true);
    try {
      await action();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, failure), 'Заявки');
    } finally {
      setBusy(false);
    }
  };

  const amountValue = Number(form.amount.replace(',', '.'));
  const canSubmit =
    !busy &&
    form.source !== '' &&
    form.summary.trim() !== '' &&
    (form.clientId !== null || (form.newClient && form.newName.trim() !== '' && form.newPhone.trim() !== ''));

  const submitCreate = () =>
    run(async () => {
      const created = await apiService.createSalesRequest({
        source: form.source as SalesRequestSource,
        ...(form.clientId ? { clientId: form.clientId } : { newClientName: form.newName.trim(), newClientPhone: form.newPhone.trim(), newClientEmail: form.newEmail.trim() || undefined }),
        clientCaseId: form.caseId ?? undefined,
        summary: form.summary.trim(),
        amount: Number.isFinite(amountValue) && amountValue > 0 ? amountValue : undefined,
      });
      setCreating(false);
      setForm(emptyForm());
      setSelectedClient(null);
      await loadList();
      await openDetail(created.id);
    }, 'Не удалось создать заявку.');

  const transition = (target: SalesRequestStatusItem, force = false) =>
    run(async () => {
      if (!detail) return;
      try {
        const updated = await apiService.transitionSalesRequest(detail.request.id, {
          toStatusId: target.id,
          version: detail.request.version,
          paidPaymentId: target.finalKind === 'Success' && paymentId ? Number(paymentId) : undefined,
          forceCloseTasks: force || undefined,
        });
        await loadList();
        await openDetail(updated.id);
      } catch (error) {
        const message = error instanceof Error ? error.message : '';
        if (!force && message.includes('незакрытые задачи')) {
          const confirmed = await dialog.confirm({
            title: 'По заявке есть незакрытые задачи',
            description: 'Принудительно закрытые задачи будут отмечены как отменённые, а не выполненные. Закрыть заявку?',
            confirmLabel: 'Закрыть принудительно',
            cancelLabel: 'Отмена',
            variant: 'danger',
          });
          if (confirmed) {
            await transition(target, true);
          }
          return;
        }
        throw error;
      }
    }, 'Не удалось сменить статус.');

  const openStatuses = detail ? statuses.filter((item) => item.id !== detail.request.statusId) : [];
  const finalClosed = detail ? detail.request.finalKind !== 'None' : false;

  return (
    <section className="space-y-4 p-4" data-testid="sales-requests-page">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-xl font-semibold text-slate-900">Заявки</h1>
        <div className="flex gap-2">
          {allowConfigure ? (
            <button type="button" className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm hover:bg-slate-50" onClick={() => setSettingsOpen((value) => !value)}>
              Настройка воронки
            </button>
          ) : null}
          {allowAnalytics ? (
            <button
              type="button"
              className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm hover:bg-slate-50"
              onClick={() => {
                const to = new Date();
                const from = new Date(to.getFullYear(), to.getMonth(), 1);
                void apiService
                  .getSalesRequestReport(from.toISOString().slice(0, 10), to.toISOString().slice(0, 10))
                  .then((r) =>
                    setReport(
                      `За месяц: заявок ${r.total}, оплачено ${r.succeeded}, брошено/отказ ${r.failed}, в работе ${r.inProgress}, конверсия ${r.conversionPercent}% · получено ${money(r.wonAmount)}, потеряно ${money(r.lostAmount)}`,
                    ),
                  )
                  .catch((error) => toast.error(extractDisplayErrorMessage(error, 'Не удалось построить отчёт.'), 'Заявки'));
              }}
            >
              Отчёт за месяц
            </button>
          ) : null}
          {allowCreate ? (
            <button type="button" className="rounded-lg bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700" onClick={() => setCreating(true)}>
              Новая заявка
            </button>
          ) : null}
        </div>
      </div>
      {settingsOpen && allowConfigure ? <SalesRequestFunnelSettings statuses={statuses} assignees={assignees} onChanged={loadStatuses} /> : null}
      {report ? <p className="rounded-lg bg-slate-50 p-2 text-sm text-slate-700" data-testid="sales-requests-report">{report}</p> : null}

      <div className="flex flex-wrap items-end gap-2 text-sm">
        <select aria-label="Статус" value={filters.statusId} onChange={(event) => setFilters((prev) => ({ ...prev, statusId: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1">
          <option value="">Все статусы</option>
          {statuses.map((item) => (
            <option key={item.id} value={item.id}>{item.title}</option>
          ))}
        </select>
        <select aria-label="Источник" value={filters.source} onChange={(event) => setFilters((prev) => ({ ...prev, source: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1">
          <option value="">Все источники</option>
          {Object.entries(SOURCE_LABELS).map(([key, label]) => (
            <option key={key} value={key}>{label}</option>
          ))}
        </select>
        <input aria-label="Поиск" placeholder="Клиент, телефон, суть" value={filters.q} onChange={(event) => setFilters((prev) => ({ ...prev, q: event.target.value }))} className="w-56 rounded-lg border border-slate-300 px-2 py-1" />
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={filters.closed} onChange={(event) => setFilters((prev) => ({ ...prev, closed: event.target.checked }))} />
          Закрытые
        </label>
        <span className="text-slate-500">Всего: {total}</span>
      </div>

      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-slate-200 text-sm">
          <thead className="bg-slate-50 text-left text-slate-600">
            <tr>
              <th className="px-2 py-1 font-semibold">№</th>
              <th className="px-2 py-1 font-semibold">Дата</th>
              <th className="px-2 py-1 font-semibold">Клиент</th>
              <th className="px-2 py-1 font-semibold">Источник</th>
              <th className="px-2 py-1 font-semibold">Статус</th>
              <th className="px-2 py-1 font-semibold">Ответственный</th>
              <th className="px-2 py-1 text-right font-semibold">Сумма</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {items.map((item) => (
              <tr key={item.id} className="cursor-pointer hover:bg-slate-50" onClick={() => void openDetail(item.id)}>
                <td className="px-2 py-1">{item.id}</td>
                <td className="px-2 py-1">{item.createdAt.slice(0, 10)}</td>
                <td className="px-2 py-1">{item.clientName}<span className="ml-1 text-xs text-slate-500">{item.clientPhone}</span></td>
                <td className="px-2 py-1">{SOURCE_LABELS[item.source] ?? item.source}</td>
                <td className="px-2 py-1">{statusById.get(item.statusId)?.title ?? item.statusId}</td>
                <td className="px-2 py-1">{userName(item.responsibleUserId)}</td>
                <td className="px-2 py-1 text-right">{item.amount ? money(item.amount) : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {items.length === 0 ? <p className="py-3 text-sm text-slate-500">Заявок нет.</p> : null}
      </div>

      {creating ? (
        <div className="rounded-2xl border border-slate-200 bg-white p-3" data-testid="sales-request-create">
          <h2 className="font-semibold text-slate-900">Новая заявка</h2>
          <div className="mt-2 grid gap-2 text-sm md:grid-cols-2">
            <label className="flex flex-col gap-1">
              Источник
              <select aria-label="Источник заявки" value={form.source} onChange={(event) => setForm((prev) => ({ ...prev, source: event.target.value as SalesRequestSource }))} className="rounded-lg border border-slate-300 px-2 py-1">
                <option value="">Выберите</option>
                {Object.entries(SOURCE_LABELS).map(([key, label]) => (
                  <option key={key} value={key}>{label}</option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1">
              Сумма (если уже известна)
              <input aria-label="Сумма заявки" inputMode="decimal" value={form.amount} onChange={(event) => setForm((prev) => ({ ...prev, amount: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
            </label>
          </div>

          {form.clientId ? (
            <p className="mt-2 text-sm">
              Клиент: <b>{selectedClient?.name ?? form.query}</b>{' '}
              <button type="button" className="text-blue-600 underline" onClick={() => {
                  setSelectedClient(null);
                  setForm((prev) => ({ ...prev, clientId: null, caseId: null }));
                }}>изменить</button>
            </p>
          ) : form.newClient ? (
            <div className="mt-2 grid gap-2 text-sm md:grid-cols-3">
              <input aria-label="ФИО нового клиента" placeholder="ФИО" value={form.newName} onChange={(event) => setForm((prev) => ({ ...prev, newName: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
              <input aria-label="Телефон нового клиента" placeholder="Телефон" value={form.newPhone} onChange={(event) => setForm((prev) => ({ ...prev, newPhone: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
              <input aria-label="Email нового клиента" placeholder="Email (необязательно)" value={form.newEmail} onChange={(event) => setForm((prev) => ({ ...prev, newEmail: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
            </div>
          ) : (
            <div className="mt-2 text-sm">
              <input aria-label="Поиск клиента" placeholder="Клиент: ФИО, телефон или email" value={form.query} onChange={(event) => setForm((prev) => ({ ...prev, query: event.target.value }))} className="w-full rounded-lg border border-slate-300 px-2 py-1" />
              <ul className="mt-1 divide-y divide-slate-100 rounded-lg border border-slate-100">
                {options.map((option) => (
                  <li key={option.id}>
                    <button type="button" className="w-full px-2 py-1 text-left hover:bg-slate-50" onClick={() => {
                        setSelectedClient(option);
                        setForm((prev) => ({ ...prev, clientId: option.id }));
                      }}>
                      {option.name} <span className="text-xs text-slate-500">{option.phone}</span>
                      <span className="ml-2 text-xs text-slate-500">дел: {option.cases.length}</span>
                    </button>
                  </li>
                ))}
              </ul>
              <button type="button" className="mt-1 text-blue-600 underline" onClick={() => setForm((prev) => ({ ...prev, newClient: true, newName: prev.query }))}>+ Добавить нового</button>
            </div>
          )}

          {selectedClient && selectedClient.cases.length > 0 ? (
            <fieldset className="mt-2 text-sm" data-testid="sales-request-cases">
              <legend className="font-medium">У клиента есть дела — по какому обращение?</legend>
              <label className="flex items-center gap-2">
                <input type="radio" name="request-case" checked={form.caseId === null} onChange={() => setForm((prev) => ({ ...prev, caseId: null }))} />
                Новое обращение
              </label>
              {selectedClient.cases.map((item) => (
                <label key={item.id} className="flex items-center gap-2">
                  <input type="radio" name="request-case" checked={form.caseId === item.id} onChange={() => setForm((prev) => ({ ...prev, caseId: item.id }))} />
                  №{item.caseNumber} {item.title} <span className="text-xs text-slate-500">{item.status}</span>
                </label>
              ))}
            </fieldset>
          ) : null}

          <label className="mt-2 flex flex-col gap-1 text-sm">
            Суть запроса
            <textarea aria-label="Суть запроса" rows={3} value={form.summary} onChange={(event) => setForm((prev) => ({ ...prev, summary: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
          </label>
          <div className="mt-2 flex gap-2">
            <button type="button" disabled={!canSubmit} onClick={() => void submitCreate()} className="rounded-lg bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700 disabled:opacity-50">Сохранить</button>
            <button type="button" onClick={() => { setCreating(false); setForm(emptyForm()); }} className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm">Отмена</button>
          </div>
        </div>
      ) : null}

      {detail ? (
        <div className="rounded-2xl border border-slate-200 bg-white p-3" data-testid="sales-request-detail">
          <div className="flex items-start justify-between gap-2">
            <div>
              <h2 className="font-semibold text-slate-900">Заявка №{detail.request.id} · {detail.request.clientName}</h2>
              <p className="text-sm text-slate-600">
                {statusById.get(detail.request.statusId)?.title} · {SOURCE_LABELS[detail.request.source]} · {TYPE_LABELS[detail.request.type]}
                {detail.request.linkedCaseWasClosed ? ' · по закрытому делу' : ''}
              </p>
            </div>
            <button type="button" className="text-sm text-slate-500 hover:text-slate-800" onClick={() => setDetail(null)}>Закрыть</button>
          </div>
          <p className="mt-2 whitespace-pre-wrap text-sm">{detail.request.summary}</p>
          {detail.request.comment ? <p className="mt-1 text-xs text-slate-500">{detail.request.comment}</p> : null}

          {allowEdit && !finalClosed ? (
            <div className="mt-3 flex flex-wrap items-end gap-2 text-sm">
              <label className="flex flex-col gap-1">
                Платёж клиента в CRM (для «Оплачено»)
                <input aria-label="Номер платежа" inputMode="numeric" value={paymentId} onChange={(event) => setPaymentId(event.target.value)} className="w-32 rounded-lg border border-slate-300 px-2 py-1" />
              </label>
              {openStatuses.map((item) => (
                <button
                  key={item.id}
                  type="button"
                  disabled={busy || (item.finalKind === 'Success' && !paymentId)}
                  onClick={() => void transition(item)}
                  className={`rounded-lg border px-2 py-1 disabled:opacity-50 ${item.finalKind === 'Failure' ? 'border-rose-300 text-rose-700' : item.finalKind === 'Success' ? 'border-emerald-300 text-emerald-700' : 'border-slate-200 text-slate-700'}`}
                >
                  {item.title}
                </button>
              ))}
            </div>
          ) : null}
          {finalClosed ? (
            <p className="mt-2 text-sm text-slate-600">
              Заявка закрыта {detail.request.closedAt?.slice(0, 10)}.
              {detail.request.resultClientCaseId ? ` Создано дело №${detail.request.resultClientCaseId}.` : ''}
            </p>
          ) : null}

          <h3 className="mt-3 text-sm font-semibold text-slate-700">Задачи по заявке</h3>
          <ul className="mt-1 space-y-0.5 text-sm" data-testid="sales-request-tasks">
            {detail.tasks.map((task) => (
              <li key={task.id}>
                {task.status === 'done' ? '✅' : task.status === 'cancelled' ? '⛔' : '•'} {task.title} · {task.assigneeName} · до {task.dueAt.slice(0, 10)}
              </li>
            ))}
            {detail.tasks.length === 0 ? <li className="text-slate-500">Задач нет.</li> : null}
          </ul>
          {allowEdit && !finalClosed ? (
            <div className="mt-2 flex flex-wrap items-end gap-2 text-sm">
              <select aria-label="Исполнитель задачи" value={taskForm.assigneeUserId} onChange={(event) => setTaskForm((prev) => ({ ...prev, assigneeUserId: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1">
                <option value="">Исполнитель</option>
                {assignees.map((item) => (
                  <option key={item.id} value={item.id}>{item.name}</option>
                ))}
              </select>
              <input aria-label="Текст задачи" placeholder="Поставить задачу" value={taskForm.title} onChange={(event) => setTaskForm((prev) => ({ ...prev, title: event.target.value }))} className="w-64 rounded-lg border border-slate-300 px-2 py-1" />
              <button
                type="button"
                disabled={busy || !taskForm.assigneeUserId || !taskForm.title.trim()}
                onClick={() =>
                  void run(async () => {
                    await apiService.createSalesRequestTask(detail.request.id, { assigneeUserId: Number(taskForm.assigneeUserId), title: taskForm.title.trim() });
                    setTaskForm({ assigneeUserId: '', title: '' });
                    await openDetail(detail.request.id);
                  }, 'Не удалось поставить задачу.')
                }
                className="rounded-lg border border-slate-200 px-2 py-1 disabled:opacity-50"
              >
                Поставить задачу
              </button>
            </div>
          ) : null}

          <h3 className="mt-3 text-sm font-semibold text-slate-700">История</h3>
          <ul className="mt-1 space-y-0.5 text-xs text-slate-600">
            {detail.events.map((event) => (
              <li key={event.id}>
                {event.createdAt.slice(0, 16).replace('T', ' ')} · {EVENT_LABELS[event.kind] ?? event.kind}
                {event.toStatusId ? ` → ${statusById.get(event.toStatusId)?.title ?? ''}` : ''}
                {event.comment ? ` · ${event.comment}` : ''}
                {event.userId ? ` · ${userName(event.userId)}` : ''}
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </section>
  );
}
