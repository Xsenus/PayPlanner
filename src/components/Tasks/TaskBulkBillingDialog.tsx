import { useRef } from 'react';
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap';
import type { TaskBillingBulkItemResult } from '../../utils/taskJournalStore';
import { SystemSelect } from '../ui/SystemSelect';
import { BILLING_REASONS, billingPreview, billingSummary, type BillingSnapshot, type BillingTarget } from './taskBulkBilling';

export function TaskBulkBillingDialog({ snapshot, outcomes, target, includeCompleted, busy, attempted, error, scopeCurrent, refreshRequired, remaining, readyCount, readErrors, canReplace, onTarget, onIncludeCompleted, onApply, onRefresh, onReplace, onClose }: {
  snapshot: BillingSnapshot | null; outcomes: TaskBillingBulkItemResult[]; target: BillingTarget; includeCompleted: boolean;
  busy: boolean; attempted: boolean; error: string; scopeCurrent: boolean; refreshRequired: boolean; remaining: number; readyCount: number; readErrors: Record<number, string>; canReplace: boolean;
  onTarget: (value: BillingTarget) => void; onIncludeCompleted: (value: boolean) => void;
  onApply: () => void; onRefresh: () => void; onReplace: () => void; onClose: () => void;
}) {
  const container = useRef<HTMLDivElement>(null);
  const onKeyDown = useModalFocusTrap(container, true, onClose);
  const byId = new Map(outcomes.map((item) => [item.id, item]));
  // The existing DialogProvider confirmation is z=2400; keep this caller below its portal.
  return <div className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-950/50 p-3">
    <div ref={container} role="dialog" aria-modal="true" aria-label="Массовая смена учёта задач" tabIndex={-1} onKeyDown={(event) => { if (event.key === 'Escape') event.stopPropagation(); onKeyDown(event); }}
      className="max-h-[calc(100vh-1.5rem)] w-full max-w-2xl space-y-3 overflow-y-auto rounded-xl bg-white p-3 shadow-xl">
      <h3 className="text-lg font-bold">Массовая смена учёта задач</h3>
      {snapshot ? <>
        <p className="text-sm">Зафиксировано: {snapshot.tasks.length}. {snapshot.scopeLabel}. Изменение текущих фильтров не добавляет задачи в этот набор.</p>
        <label className="block text-sm">Новый тип учёта<SystemSelect aria-label="Новый тип учёта выбранных задач" value={target} disabled={busy || attempted} onChange={(event) => onTarget(event.target.value as BillingTarget)}>
          <option value="internal">Неоплачиваемая / внутренняя</option><option value="billable">Платная</option>
        </SystemSelect></label>
        <label className="flex items-start gap-2 text-sm"><input type="checkbox" checked={includeCompleted} disabled={busy || attempted} onChange={(event) => onIncludeCompleted(event.target.checked)} />Также включить выбранные завершённые задачи без счёта</label>
        <p className="text-xs text-slate-600">Задачи со счётом, выплатой исполнителю, учётом по договору, отменённые и недоступные задачи будут пропущены. Суммы, счета, выплаты, статус, результат и файлы не меняются. Предварительная оценка ниже не гарантирует изменение.</p>
        {!scopeCurrent ? <p role="alert" className="text-sm text-amber-800">Изменилась страница, компания или право редактирования. Отправка исходного набора заблокирована. Вернитесь к исходной области либо явно замените выбор.</p> : null}
        {outcomes.length ? <p role="status" className="text-sm font-semibold">{billingSummary(outcomes)}</p> : null}
        {refreshRequired ? <p className="text-sm">Перед повтором оставшихся {remaining} задач обновите их сведения. Уже изменённые и совпадающие строки повторно не отправляются.</p> : null}
        {!refreshRequired && Object.keys(readErrors).length ? <p className="text-sm">Готово к повтору после свежего чтения: {readyCount}. Недоступные строки не включены.</p> : null}
        <ul aria-label="Построчный итог смены учёта" className="max-h-64 space-y-2 overflow-y-auto text-sm">
          {snapshot.tasks.map((task) => {
            const item = byId.get(task.id);
            return <li key={task.id} className="rounded border border-slate-200 p-2"><span className="font-semibold">#{task.id} · {task.title}</span>
              <div>{item ? item.outcome === 'updated' ? 'Учёт изменён.' : item.outcome === 'unchanged' ? 'Уже установлен нужный учёт.' : item.reason ? BILLING_REASONS[item.reason] : 'Не изменена; причина не указана.' : billingPreview(task, target, includeCompleted)}</div>
              {readErrors[task.id] ? <div className="text-xs text-amber-800">Обновление перед повтором: {readErrors[task.id]}</div> : null}
            </li>;
          })}
        </ul>
      </> : null}
      {error ? <p role="alert" className="text-sm text-rose-700">{error}</p> : null}
      <div className="flex flex-wrap justify-end gap-2">
        <button type="button" disabled={busy} onClick={onClose} className="rounded border px-3 py-1.5 text-sm disabled:opacity-50">Закрыть</button>
        <button type="button" disabled={busy || !canReplace} onClick={onReplace} className="rounded border px-3 py-1.5 text-sm disabled:opacity-50">Новый выбор с текущей страницы</button>
        {refreshRequired || Object.keys(readErrors).length ? <button type="button" disabled={busy || !scopeCurrent || !remaining} onClick={onRefresh} className="rounded border px-3 py-1.5 text-sm disabled:opacity-50">Обновить оставшиеся задачи</button> : null}
        <button type="button" disabled={busy || !scopeCurrent || refreshRequired || !readyCount} onClick={onApply} className="rounded bg-blue-600 px-3 py-1.5 text-sm font-semibold text-white disabled:opacity-50">{busy ? 'Ожидание операции…' : attempted ? 'Подтвердить повтор оставшихся' : 'Изменить учёт'}</button>
      </div>
    </div>
  </div>;
}
