import { useMemo } from 'react';
import type { Payment } from '../../types';
import { useTranslation } from '../../hooks/useTranslation';
import { formatCurrencySmart } from '../../utils/formatters';

interface PaymentsTableProps {
  payments: Payment[];
  onEditPayment: (payment: Payment) => void;
  onOpenClient?: (clientId: number, caseId?: number) => void;
}

const STATUS_STYLES: Record<string, string> = {
  Completed: 'bg-emerald-100 text-emerald-700',
  Pending: 'bg-amber-100 text-amber-700',
  Overdue: 'bg-purple-100 text-purple-700',
  Processing: 'bg-sky-100 text-sky-700',
  Cancelled: 'bg-gray-100 text-gray-600',
};

export function PaymentsTable({ payments, onEditPayment, onOpenClient }: PaymentsTableProps) {
  const { t, formatDate } = useTranslation();

  const columns = useMemo(
    () => [
      t('date') ?? 'Дата',
      t('clients') ?? 'Клиенты',
      t('description') ?? 'Описание',
      t('type') ?? 'Тип',
      t('statusFilter') ?? 'Статус',
      t('category') ?? 'Категория',
      t('amount') ?? 'Сумма',
    ],
    [t],
  );

  return (
    <div className="bg-white rounded-xl shadow-sm border border-gray-200">
      <div className="overflow-x-auto">
        <table className="min-w-[820px] w-full divide-y divide-gray-200">
          <thead className="bg-gray-50">
            <tr>
              {columns.map((col, idx) => (
                <th
                  key={idx}
                  scope="col"
                  className="px-4 py-3 text-left text-xs font-semibold text-gray-500 uppercase tracking-wide">
                  {col}
                </th>
              ))}
              <th className="px-4 py-3 text-left text-xs font-semibold text-gray-500 uppercase tracking-wide">
                {t('actions') ?? 'Действия'}
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {payments.length === 0 ? (
              <tr>
                <td colSpan={columns.length + 1} className="px-4 py-6 text-center text-sm text-gray-500">
                  {t('noPaymentsForFilter') ?? 'Нет платежей, подходящих под выбранные условия.'}
                </td>
              </tr>
            ) : (
              payments.map((payment) => {
                const isIncome = payment.type === 'Income';
                const statusStyle = STATUS_STYLES[payment.status] ?? 'bg-gray-100 text-gray-600';
                const amountFmt = formatCurrencySmart(payment.amount).full;
                const category =
                  payment.dealType?.name || payment.incomeType?.name || payment.paymentSource?.name || '—';

                return (
                  <tr key={payment.id} className="hover:bg-gray-50/60 transition-colors">
                    <td className="px-4 py-3 text-sm text-gray-700 whitespace-nowrap">
                      {payment.date ? formatDate(payment.date) : '—'}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700 min-w-[160px]">
                      {payment.client ? (
                        <button
                          type="button"
                          className="text-blue-600 hover:underline"
                          onClick={() => {
                            if (payment.clientId) {
                              onOpenClient?.(payment.clientId, payment.clientCaseId ?? undefined);
                            }
                          }}>
                          <span className="font-medium text-left block">{payment.client.name}</span>
                          {payment.clientCase?.title ? (
                            <span className="text-xs text-gray-500 truncate block">
                              {payment.clientCase.title}
                            </span>
                          ) : null}
                        </button>
                      ) : (
                        <span className="text-gray-400">—</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700 max-w-[260px]">
                      <div className="truncate" title={payment.description || undefined}>
                        {payment.description || t('noDescription') || 'Без описания'}
                      </div>
                      {payment.notes ? (
                        <div className="text-xs text-gray-500 truncate" title={payment.notes}>
                          {payment.notes}
                        </div>
                      ) : null}
                    </td>
                    <td className="px-4 py-3">
                      <span
                        className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${
                          isIncome ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-700'
                        }`}>
                        {isIncome ? t('income') ?? 'Доход' : t('expense') ?? 'Расход'}
                      </span>
                    </td>
                    <td className="px-4 py-3">
                      <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${statusStyle}`}>
                        {payment.status}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-600">
                      {category}
                    </td>
                    <td className="px-4 py-3 text-sm font-semibold text-gray-900 whitespace-nowrap">
                      {amountFmt}
                    </td>
                    <td className="px-4 py-3 text-sm text-right whitespace-nowrap">
                      <button
                        type="button"
                        onClick={() => onEditPayment(payment)}
                        className="inline-flex items-center gap-1 rounded-lg border border-gray-300 px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-100">
                        {t('edit') ?? 'Редактировать'}
                      </button>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
