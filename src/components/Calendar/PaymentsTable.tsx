import { useCallback, useEffect, useMemo, useState } from 'react';
import type { Payment } from '../../types';
import { useTranslation } from '../../hooks/useTranslation';
import { formatCurrencySmart } from '../../utils/formatters';

interface PaymentsTableProps {
  payments: Payment[];
  onEditPayment: (payment: Payment) => void;
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onAddPayment?: (type: Payment['type'], payment?: Payment) => void;
  onDeletePayment?: (payment: Payment) => void | Promise<void>;
  canAdd: boolean;
  canEdit: boolean;
  canDelete: boolean;
}

const STATUS_STYLES: Record<string, string> = {
  Completed: 'bg-emerald-100 text-emerald-700',
  Pending: 'bg-amber-100 text-amber-700',
  Overdue: 'bg-purple-100 text-purple-700',
  Processing: 'bg-sky-100 text-sky-700',
  Cancelled: 'bg-gray-100 text-gray-600',
};

type MenuState = { payment: Payment; x: number; y: number } | null;

export function PaymentsTable({
  payments,
  onEditPayment,
  onOpenClient,
  onAddPayment,
  onDeletePayment,
  canAdd,
  canEdit,
  canDelete,
}: PaymentsTableProps) {
  const { t, formatDate } = useTranslation();
  const [menuState, setMenuState] = useState<MenuState>(null);

  const statusLabels = useMemo(
    () => ({
      Completed: t('statusCompleted') ?? t('completedStatus') ?? 'Выполнено',
      Pending: t('pending') ?? 'Ожидается',
      Overdue: t('overdue') ?? 'Просрочено',
      Processing: t('processingStatus') ?? 'В обработке',
      Cancelled: t('cancelledStatus') ?? 'Отменено',
    }),
    [t],
  );

  const columns = useMemo(
    () => [
      t('date') ?? 'Дата',
      t('clients') ?? 'Клиенты',
      t('description') ?? 'Описание',
      t('type') ?? 'Тип',
      t('status') ?? t('statusFilter') ?? 'Статус',
      t('category') ?? 'Категория',
      t('amount') ?? 'Сумма',
    ],
    [t],
  );

  const closeMenu = useCallback(() => setMenuState(null), []);

  useEffect(() => {
    if (!menuState) return;
    const close = () => setMenuState(null);
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setMenuState(null);
    };
    window.addEventListener('click', close);
    window.addEventListener('keydown', handleKey);
    return () => {
      window.removeEventListener('click', close);
      window.removeEventListener('keydown', handleKey);
    };
  }, [menuState]);

  const openMenu = useCallback(
    (event: React.MouseEvent<HTMLTableRowElement>, payment: Payment) => {
      if (!canAdd && !canEdit && !canDelete) return;
      event.preventDefault();
      const width = 220;
      const height = 156;
      let x = event.clientX;
      let y = event.clientY;
      if (x + width > window.innerWidth) {
        x = Math.max(window.innerWidth - width - 12, 12);
      }
      if (y + height > window.innerHeight) {
        y = Math.max(window.innerHeight - height - 12, 12);
      }
      setMenuState({ payment, x, y });
    },
    [canAdd, canEdit, canDelete],
  );

  return (
    <div className="bg-white rounded-xl shadow-sm border border-gray-200">
      <div className="overflow-x-auto thin-scrollbar">
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
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {payments.length === 0 ? (
              <tr>
                <td colSpan={columns.length} className="px-4 py-6 text-center text-sm text-gray-500">
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

                const statusLabel = statusLabels[payment.status] ?? payment.status;

                return (
                  <tr
                    key={payment.id}
                    onContextMenu={(event) => openMenu(event, payment)}
                    onDoubleClick={() => {
                      if (canEdit) onEditPayment(payment);
                    }}
                    className="hover:bg-gray-50/60 transition-colors cursor-default"
                    title={
                      canAdd || canEdit || canDelete
                        ? t('contextMenuHint') ?? 'ПКМ для действий'
                        : undefined
                    }>
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
                        {statusLabel}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-600">
                      {category}
                    </td>
                    <td className="px-4 py-3 text-sm font-semibold text-gray-900 whitespace-nowrap">
                      {amountFmt}
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>

      {menuState ? (
        <div
          className="fixed inset-0 z-30"
          role="presentation"
          onClick={closeMenu}
          onContextMenu={(event) => {
            event.preventDefault();
            closeMenu();
          }}>
          <div
            role="menu"
            className="absolute z-40 min-w-[200px] rounded-lg border border-gray-200 bg-white py-2 shadow-xl"
            style={{ top: menuState.y, left: menuState.x }}
            onClick={(event) => event.stopPropagation()}>
            <button
              type="button"
              onClick={() => {
                closeMenu();
                if (canAdd) onAddPayment?.(menuState.payment.type, menuState.payment);
              }}
              className={`flex w-full items-center justify-between px-4 py-2 text-sm text-left transition-colors ${
                canAdd ? 'text-gray-700 hover:bg-gray-100' : 'text-gray-400 cursor-not-allowed'
              }`}
              disabled={!canAdd}
              title={
                !canAdd
                  ? t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.'
                  : undefined
              }>
              {t('contextAddPayment') ?? 'Добавить платёж'}
            </button>
            <button
              type="button"
              onClick={() => {
                closeMenu();
                if (canEdit) onEditPayment(menuState.payment);
              }}
              className={`flex w-full items-center justify-between px-4 py-2 text-sm text-left transition-colors ${
                canEdit ? 'text-gray-700 hover:bg-gray-100' : 'text-gray-400 cursor-not-allowed'
              }`}
              disabled={!canEdit}
              title={
                !canEdit
                  ? t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.'
                  : undefined
              }>
              {t('contextEditPayment') ?? 'Редактировать платёж'}
            </button>
            <hr className="my-2 border-gray-100" />
            <button
              type="button"
              onClick={async () => {
                closeMenu();
                if (!canDelete) return;
                await onDeletePayment?.(menuState.payment);
              }}
              className={`flex w-full items-center justify-between px-4 py-2 text-sm text-left transition-colors ${
                canDelete
                  ? 'text-red-600 hover:bg-red-50'
                  : 'text-gray-400 cursor-not-allowed'
              }`}
              disabled={!canDelete}
              title={
                !canDelete
                  ? t('permissionNoDeletePayment') ?? 'Недостаточно прав для удаления платежей.'
                  : undefined
              }>
              {t('contextDeletePayment') ?? 'Удалить платёж'}
            </button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
