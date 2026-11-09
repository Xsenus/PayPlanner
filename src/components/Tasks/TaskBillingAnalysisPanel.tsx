import { WalletCards } from 'lucide-react';
import type { TaskJournalListParams } from '../../services/api';

type BillingFilter = TaskJournalListParams['billingStatus'];
type BillingAnalysis = { unbilled: number; invoicesToPrepare: number; fixed: number; nonBillable: number; invoiced: number; unclassified: number };
const russianPlural = new Intl.PluralRules('ru');
const taskNouns: Partial<Record<Intl.LDMLPluralRule, string>> = { one: 'задача', few: 'задачи' };
const formatTaskCount = (count: number) => `${count} ${taskNouns[russianPlural.select(count)] ?? 'задач'}`;

export function TaskBillingAnalysisPanel({ billingAnalysis, billingFilter, responsibleName, selectedCount, canCreateInvoice, canReconcile, showReconciliation, onBillingFilterClick, onCreateInvoice, onReconcile }: {
  billingAnalysis: BillingAnalysis;
  billingFilter: BillingFilter;
  responsibleName?: string | null;
  selectedCount: number;
  canCreateInvoice: boolean;
  canReconcile: boolean;
  showReconciliation: boolean;
  onBillingFilterClick: (filter: NonNullable<BillingFilter>) => void;
  onCreateInvoice: () => void;
  onReconcile: () => void;
}) {
  const unbilledClientTaskCount = billingAnalysis.unbilled;
  const clientCount = billingAnalysis.invoicesToPrepare;
  return (
          <section className="mb-3 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-rose-200 bg-gradient-to-r from-rose-50 to-amber-50 p-3" data-testid="task-unbilled-reminder">
            <div className="flex items-center gap-3">
              <span className="rounded-xl bg-rose-600 p-2 text-white"><WalletCards className="h-5 w-5" /></span>
              <div>
                <div className="font-semibold text-rose-950">Анализ оплаты задач</div>
                <div className="mt-1 flex flex-wrap gap-2 text-xs font-bold">
                  <button type="button" aria-pressed={billingFilter === 'unbilled'} onClick={() => onBillingFilterClick('unbilled')} title="Показать платные задачи без счёта, включая выполненные" className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 hover:shadow ${billingFilter === 'unbilled' ? 'border-rose-700 bg-rose-700 text-white ring-2 ring-rose-200' : 'border-rose-300 bg-rose-100 text-rose-900 hover:bg-rose-200'}`}>{formatTaskCount(unbilledClientTaskCount)} без счёта у {clientCount} {russianPlural.select(clientCount) === 'one' ? 'клиента' : 'клиентов'}</button>
                  <button type="button" aria-pressed={billingFilter === 'fixed'} onClick={() => onBillingFilterClick('fixed')} className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 ${billingFilter === 'fixed' ? 'border-violet-700 bg-violet-700 text-white ring-2 ring-violet-200' : 'border-violet-300 bg-violet-100 text-violet-900 hover:bg-violet-200'}`}>По фиксированному договору: {billingAnalysis.fixed}</button>
                  <button type="button" aria-pressed={billingFilter === 'internal'} onClick={() => onBillingFilterClick('internal')} className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 ${billingFilter === 'internal' ? 'border-slate-700 bg-slate-700 text-white ring-2 ring-slate-200' : 'border-slate-300 bg-slate-100 text-slate-800 hover:bg-slate-200'}`}>Неоплачиваемые: {billingAnalysis.nonBillable}</button>
                  <button type="button" aria-pressed={billingFilter === 'invoiced'} onClick={() => onBillingFilterClick('invoiced')} className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 ${billingFilter === 'invoiced' ? 'border-emerald-700 bg-emerald-700 text-white ring-2 ring-emerald-200' : 'border-emerald-300 bg-emerald-100 text-emerald-900 hover:bg-emerald-200'}`}>Уже включены в счёт: {billingAnalysis.invoiced}</button>
                  {billingAnalysis.unclassified > 0 ? (
                    <button type="button" aria-pressed={billingFilter === 'legacy'} onClick={() => onBillingFilterClick('legacy')} className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 ${billingFilter === 'legacy' ? 'border-amber-700 bg-amber-700 text-white ring-2 ring-amber-200' : 'border-amber-300 bg-amber-100 text-amber-900 hover:bg-amber-200'}`}>Нужно распределить: {billingAnalysis.unclassified}</button>
                  ) : null}
                </div>
                <div className="mt-1.5 text-xs leading-5 text-rose-800" data-testid="task-billing-counter-help">
                  В счётчике показаны платные задачи, которые ещё не вошли в счета.
                </div>
                <div className="mt-1 text-sm text-rose-700">
                  {billingFilter
                    ? 'Категория открыта в таблице ниже. Повторное нажатие вернёт полный журнал.'
                    : unbilledClientTaskCount > 0
                    ? 'В очереди остаются и выполненные задачи. Отметьте задачу клиента, чтобы сверить его работы со счетами.'
                    : 'Все активные задачи распределены, новых счетов по ним готовить не требуется.'}
                </div>
                <div className={`mt-1 text-xs font-semibold ${responsibleName ? 'text-slate-700' : 'text-amber-800'}`}>
                  {responsibleName
                    ? `Ответственный за счета, акты и договоры: ${responsibleName}`
                    : 'Ответственный за счета, акты и договоры не назначен. Его может выбрать администратор в настройках журнала задач.'}
                </div>
              </div>
            </div>
            {unbilledClientTaskCount > 0 ? (
              <div className="flex flex-wrap gap-2">
              {showReconciliation ? (
                <button type="button" onClick={onReconcile} disabled={!canReconcile}
                  title={canReconcile ? undefined : 'Отметьте одну или несколько задач без счёта одного клиента'}
                  className="rounded-xl border border-rose-300 bg-white px-4 py-2.5 text-sm font-semibold text-rose-900 disabled:cursor-not-allowed disabled:opacity-50">
                  Сверка по клиенту
                </button>
              ) : null}
              <button
                type="button"
                onClick={onCreateInvoice}
                disabled={selectedCount === 0 || !canCreateInvoice}
                className="inline-flex items-center gap-2 rounded-xl bg-rose-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-rose-700 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <WalletCards className="h-4 w-4" />
                Подготовить счёт{selectedCount > 0 ? ` (${selectedCount})` : ''}
              </button>
              </div>
            ) : null}
          </section>
  );
}
