import { WalletCards } from 'lucide-react';
import type { TaskJournalListParams } from '../../services/api';

type BillingFilter = TaskJournalListParams['billingStatus'];
type BillingAnalysis = { unbilled: number; invoicesToPrepare: number; fixed: number; nonBillable: number; invoiced: number; unclassified: number };

export function TaskBillingAnalysisPanel({ billingAnalysis, billingFilter, responsibleName, selectedCount, canCreateInvoice, onBillingFilterClick, onCreateInvoice }: {
  billingAnalysis: BillingAnalysis;
  billingFilter: BillingFilter;
  responsibleName?: string | null;
  selectedCount: number;
  canCreateInvoice: boolean;
  onBillingFilterClick: (filter: NonNullable<BillingFilter>) => void;
  onCreateInvoice: () => void;
}) {
  const unbilledClientTaskCount = billingAnalysis.unbilled;
  return (
          <section className="mb-3 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-rose-200 bg-gradient-to-r from-rose-50 to-amber-50 p-3" data-testid="task-unbilled-reminder">
            <div className="flex items-center gap-3">
              <span className="rounded-xl bg-rose-600 p-2 text-white"><WalletCards className="h-5 w-5" /></span>
              <div>
                <div className="font-semibold text-rose-950">Анализ оплаты задач</div>
                <div className="mt-1 flex flex-wrap gap-2 text-xs font-bold">
                  <button type="button" aria-pressed={billingFilter === 'unbilled'} onClick={() => onBillingFilterClick('unbilled')} title="Показать все платные задачи без счёта, включая уже выполненные, сгруппированные по клиентам" className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 hover:shadow ${billingFilter === 'unbilled' ? 'border-rose-700 bg-rose-700 text-white ring-2 ring-rose-200' : 'border-rose-300 bg-rose-100 text-rose-900 hover:bg-rose-200'}`}>Счетов подготовить: {billingAnalysis.invoicesToPrepare}</button>
                  <button type="button" aria-pressed={billingFilter === 'unbilled'} onClick={() => onBillingFilterClick('unbilled')} title="Показать все платные задачи, ещё не включённые в счёт, в том числе выполненные" className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 hover:shadow ${billingFilter === 'unbilled' ? 'border-rose-700 bg-rose-700 text-white ring-2 ring-rose-200' : 'border-rose-300 bg-white text-rose-900 hover:bg-rose-100'}`}>Платные задачи без счёта: {unbilledClientTaskCount}</button>
                  <button type="button" aria-pressed={billingFilter === 'fixed'} onClick={() => onBillingFilterClick('fixed')} className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 ${billingFilter === 'fixed' ? 'border-violet-700 bg-violet-700 text-white ring-2 ring-violet-200' : 'border-violet-300 bg-violet-100 text-violet-900 hover:bg-violet-200'}`}>По фиксированному договору: {billingAnalysis.fixed}</button>
                  <button type="button" aria-pressed={billingFilter === 'internal'} onClick={() => onBillingFilterClick('internal')} className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 ${billingFilter === 'internal' ? 'border-slate-700 bg-slate-700 text-white ring-2 ring-slate-200' : 'border-slate-300 bg-slate-100 text-slate-800 hover:bg-slate-200'}`}>Неоплачиваемые: {billingAnalysis.nonBillable}</button>
                  <button type="button" aria-pressed={billingFilter === 'invoiced'} onClick={() => onBillingFilterClick('invoiced')} className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 ${billingFilter === 'invoiced' ? 'border-emerald-700 bg-emerald-700 text-white ring-2 ring-emerald-200' : 'border-emerald-300 bg-emerald-100 text-emerald-900 hover:bg-emerald-200'}`}>Уже включены в счёт: {billingAnalysis.invoiced}</button>
                  {billingAnalysis.unclassified > 0 ? (
                    <button type="button" aria-pressed={billingFilter === 'legacy'} onClick={() => onBillingFilterClick('legacy')} className={`rounded-lg border px-3 py-1.5 shadow-sm transition hover:-translate-y-0.5 ${billingFilter === 'legacy' ? 'border-amber-700 bg-amber-700 text-white ring-2 ring-amber-200' : 'border-amber-300 bg-amber-100 text-amber-900 hover:bg-amber-200'}`}>Нужно распределить: {billingAnalysis.unclassified}</button>
                  ) : null}
                </div>
                <div className="mt-1.5 text-xs leading-5 text-rose-800" data-testid="task-billing-counter-help">
                  «Счетов подготовить» — количество клиентов, для которых можно сформировать объединённый счёт. «Платные задачи без счёта» — общее количество отдельных работ, ещё не включённых в счета.
                </div>
                <div className="mt-1 text-sm text-rose-700">
                  {billingFilter
                    ? 'Категория открыта в таблице ниже. Повторное нажатие вернёт полный журнал.'
                    : unbilledClientTaskCount > 0
                    ? 'В очереди остаются и выполненные задачи: отметьте работы одного клиента и подготовьте по ним единый счёт.'
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
              <button
                type="button"
                onClick={onCreateInvoice}
                disabled={selectedCount === 0 || !canCreateInvoice}
                className="inline-flex items-center gap-2 rounded-xl bg-rose-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-rose-700 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <WalletCards className="h-4 w-4" />
                Подготовить счёт{selectedCount > 0 ? ` (${selectedCount})` : ''}
              </button>
            ) : null}
          </section>
  );
}
