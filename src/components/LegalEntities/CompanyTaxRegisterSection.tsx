import { useCallback, useEffect, useState } from 'react';

import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type { TaxRegisterBook, TaxRegisterEntryItem } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface CompanyTaxRegisterSectionProps {
  companyId: number;
}

const money = (value: number) => value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const emptyForm = () => ({ type: 'Income' as 'Income' | 'Expense', operationDate: '', amount: '', documentRef: '', description: '' });

/**
 * Регистр книги доходов и расходов собственной компании: черновики, утверждение, корректировка сторно, закрытие года и выгрузка.
 * Видно только администратору (сервер отдаёт данные только ему). Налоговые правила здесь не применяются: в книгу попадают
 * только утверждённые вручную записи со ссылкой на первичный документ.
 */
export function CompanyTaxRegisterSection({ companyId }: CompanyTaxRegisterSectionProps) {
  const toast = useToast();
  const dialog = useDialog();
  const [year, setYear] = useState(new Date().getFullYear());
  const [book, setBook] = useState<TaxRegisterBook | null>(null);
  const [drafts, setDrafts] = useState<TaxRegisterEntryItem[]>([]);
  const [busy, setBusy] = useState(false);
  const [form, setForm] = useState(emptyForm);

  const load = useCallback(async () => {
    try {
      const [loadedBook, loadedDrafts] = await Promise.all([
        apiService.getTaxRegisterBook(companyId, year),
        apiService.getTaxRegisterDrafts(companyId, year),
      ]);
      setBook(loadedBook);
      setDrafts(loadedDrafts.items);
    } catch {
      setBook(null);
      setDrafts([]);
    }
  }, [companyId, year]);

  useEffect(() => {
    void load();
  }, [load]);

  const run = async (action: () => Promise<void>, failure: string) => {
    setBusy(true);
    try {
      await action();
      await load();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, failure), 'Книга доходов и расходов');
    } finally {
      setBusy(false);
    }
  };

  if (!book) {
    return null;
  }

  const amount = Number(form.amount.replace(',', '.'));
  const canAdd = !book.isClosed && form.operationDate !== '' && Number.isFinite(amount) && amount > 0;

  const download = async () => {
    const blob = await apiService.downloadTaxRegisterCsv(companyId, year);
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `kudir-${year}.csv`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  };

  return (
    <section className="mt-4 rounded-2xl border border-slate-200 bg-white p-3" data-testid="company-tax-register">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-base font-semibold text-slate-900">Книга доходов и расходов</h3>
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <label className="flex items-center gap-1">
            Год
            <input
              type="number"
              value={year}
              min={2000}
              max={2100}
              onChange={(event) => setYear(Number(event.target.value) || year)}
              className="w-24 rounded-lg border border-slate-300 px-2 py-1"
            />
          </label>
          <button
            type="button"
            disabled={busy}
            onClick={() => void run(download, 'Не удалось выгрузить книгу.')}
            className="rounded-lg border border-slate-200 px-2 py-1 text-slate-700 hover:bg-slate-50 disabled:opacity-50"
          >
            Выгрузить CSV
          </button>
          <button
            type="button"
            disabled={busy || book.isClosed}
            onClick={() =>
              void (async () => {
                const confirmed = await dialog.confirm({
                  title: `Закрыть ${year} год?`,
                  description: 'После закрытия записи года менять нельзя.',
                  confirmLabel: 'Закрыть год',
                  cancelLabel: 'Отмена',
                  variant: 'danger',
                });
                if (confirmed) {
                  await run(async () => {
                    await apiService.closeTaxRegisterYear(companyId, year);
                  }, 'Не удалось закрыть год.');
                }
              })()
            }
            className="rounded-lg border border-slate-200 px-2 py-1 text-slate-700 hover:bg-slate-50 disabled:opacity-50"
          >
            {book.isClosed ? 'Год закрыт' : 'Закрыть год'}
          </button>
        </div>
      </div>
      <p className="mt-1 text-xs text-slate-500">
        В книгу попадают только утверждённые записи со ссылкой на первичный документ. Форму книги и признание расходов определяет бухгалтерия.
      </p>

      <div className="mt-2 grid grid-cols-3 gap-2 text-sm" data-testid="tax-register-totals">
        <div>Доходы: <b>{money(book.totals.income)}</b></div>
        <div>Расходы: <b>{money(book.totals.expense)}</b></div>
        <div>Разность: <b>{money(book.totals.taxBase)}</b></div>
      </div>

      {!book.isClosed ? (
        <div className="mt-3 flex flex-wrap items-end gap-2 text-sm">
          <select
            aria-label="Тип записи"
            value={form.type}
            onChange={(event) => setForm((prev) => ({ ...prev, type: event.target.value as 'Income' | 'Expense' }))}
            className="rounded-lg border border-slate-300 px-2 py-1"
          >
            <option value="Income">Доход</option>
            <option value="Expense">Расход</option>
          </select>
          <input
            type="date"
            aria-label="Дата операции"
            value={form.operationDate}
            onChange={(event) => setForm((prev) => ({ ...prev, operationDate: event.target.value }))}
            className="rounded-lg border border-slate-300 px-2 py-1"
          />
          <input
            aria-label="Сумма"
            inputMode="decimal"
            placeholder="Сумма"
            value={form.amount}
            onChange={(event) => setForm((prev) => ({ ...prev, amount: event.target.value }))}
            className="w-28 rounded-lg border border-slate-300 px-2 py-1"
          />
          <input
            aria-label="Первичный документ"
            placeholder="Первичный документ"
            value={form.documentRef}
            onChange={(event) => setForm((prev) => ({ ...prev, documentRef: event.target.value }))}
            className="w-48 rounded-lg border border-slate-300 px-2 py-1"
          />
          <input
            aria-label="Описание"
            placeholder="Описание"
            value={form.description}
            onChange={(event) => setForm((prev) => ({ ...prev, description: event.target.value }))}
            className="w-48 rounded-lg border border-slate-300 px-2 py-1"
          />
          <button
            type="button"
            disabled={busy || !canAdd}
            onClick={() =>
              void run(async () => {
                await apiService.createTaxRegisterEntry({
                  legalEntityId: companyId,
                  type: form.type,
                  operationDate: form.operationDate,
                  amount,
                  documentRef: form.documentRef.trim() || undefined,
                  description: form.description.trim() || undefined,
                });
                setForm(emptyForm());
              }, 'Не удалось добавить запись.')
            }
            className="rounded-lg bg-blue-600 px-3 py-1.5 text-white hover:bg-blue-700 disabled:opacity-50"
          >
            Добавить черновик
          </button>
        </div>
      ) : null}

      {drafts.length > 0 ? (
        <div className="mt-3" data-testid="tax-register-drafts">
          <h4 className="text-sm font-semibold text-slate-700">Черновики ({drafts.length})</h4>
          <ul className="mt-1 divide-y divide-slate-100 text-sm">
            {drafts.map((draft) => (
              <li key={draft.id} className="flex flex-wrap items-center justify-between gap-2 py-1">
                <span>
                  {draft.operationDate.slice(0, 10)} · {draft.direction === 'Income' ? 'Доход' : 'Расход'} · {money(draft.amount)} · {draft.documentRef || 'без документа'}
                </span>
                <span className="flex gap-2">
                  <button
                    type="button"
                    disabled={busy || !draft.documentRef}
                    onClick={() => void run(async () => { await apiService.approveTaxRegisterEntry(draft.id); }, 'Не удалось утвердить запись.')}
                    className="rounded-lg border border-emerald-300 px-2 py-0.5 text-emerald-700 hover:bg-emerald-50 disabled:opacity-50"
                  >
                    Утвердить
                  </button>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => void run(async () => { await apiService.archiveTaxRegisterEntry(draft.id); }, 'Не удалось архивировать запись.')}
                    className="rounded-lg border border-slate-200 px-2 py-0.5 text-slate-700 hover:bg-slate-50 disabled:opacity-50"
                  >
                    В архив
                  </button>
                </span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      <div className="mt-3 overflow-x-auto">
        <table className="min-w-full divide-y divide-slate-200 text-sm">
          <thead className="bg-slate-50 text-left text-slate-600">
            <tr>
              <th className="px-2 py-1 font-semibold">Дата</th>
              <th className="px-2 py-1 font-semibold">Графа</th>
              <th className="px-2 py-1 text-right font-semibold">Сумма</th>
              <th className="px-2 py-1 font-semibold">Документ</th>
              <th className="px-2 py-1 font-semibold" />
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {book.entries.map((entry) => (
              <tr key={entry.id} className={entry.type === 'Correction' ? 'text-amber-800' : undefined}>
                <td className="px-2 py-1">{entry.operationDate.slice(0, 10)}</td>
                <td className="px-2 py-1">
                  {entry.direction === 'Income' ? 'Доход' : 'Расход'}
                  {entry.type === 'Correction' ? ' (корректировка)' : ''}
                </td>
                <td className="px-2 py-1 text-right">{money(entry.sign * entry.amount)}</td>
                <td className="px-2 py-1">{entry.documentRef}</td>
                <td className="px-2 py-1 text-right">
                  {!book.isClosed && entry.type !== 'Correction' && !entry.correctedByEntryId ? (
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() =>
                        void (async () => {
                          const reason = (await dialog.prompt({
                            title: 'Причина корректировки',
                            label: 'Причина',
                            multiline: true,
                            confirmLabel: 'Исправить',
                            cancelLabel: 'Отмена',
                          }))?.trim();
                          if (reason) {
                            await run(async () => { await apiService.correctTaxRegisterEntry(entry.id, reason); }, 'Не удалось создать корректировку.');
                          }
                        })()
                      }
                      className="rounded-lg border border-slate-200 px-2 py-0.5 text-slate-700 hover:bg-slate-50 disabled:opacity-50"
                    >
                      Исправить
                    </button>
                  ) : entry.correctedByEntryId ? (
                    <span className="text-xs text-slate-400">исправлена</span>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {book.entries.length === 0 ? <p className="py-2 text-sm text-slate-500">Утверждённых записей за {year} год нет.</p> : null}
      </div>
    </section>
  );
}
