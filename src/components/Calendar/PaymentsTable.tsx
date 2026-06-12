import { AlertTriangle, CheckCircle2, EyeOff, GripVertical, SplitSquareHorizontal } from 'lucide-react';
import {
  useCallback,
  useEffect,
  useMemo,
  useState,
  type DragEvent as ReactDragEvent,
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent as ReactMouseEvent,
} from 'react';
import type { Payment } from '../../types';
import { useTranslation } from '../../hooks/useTranslation';
import { formatCurrencySmart } from '../../utils/formatters';
import { getVisibleNotes } from '../../utils/systemNotes';
import { needsPartialRemainderTransfer } from '../../utils/invoicePayments';

export type PaymentTableColumnKey =
  | 'date'
  | 'plannedDate'
  | 'client'
  | 'description'
  | 'type'
  | 'status'
  | 'category'
  | 'amount'
  | 'paidDate'
  | 'createdAt';

export interface PaymentTableColumnDefinition {
  key: PaymentTableColumnKey;
  label: string;
}

interface PaymentsTableProps {
  payments: Payment[];
  onEditPayment: (payment: Payment) => void;
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onAddPayment?: (type: Payment['type'], payment?: Payment) => void;
  onDeletePayment?: (payment: Payment) => void | Promise<void>;
  onStatusChange?: (payment: Payment) => void;
  onAcceptAdvance?: (payment: Payment) => void | Promise<void>;
  onIgnoreAdvance?: (payment: Payment) => void | Promise<void>;
  onAllocateAdvance?: (payment: Payment) => void | Promise<void>;
  canAdd: boolean;
  canEdit: boolean;
  canDelete: boolean;
  canEditPayment?: (payment: Payment) => boolean;
  canDeletePayment?: (payment: Payment) => boolean;
  columns?: PaymentTableColumnDefinition[];
  draggingColumn?: PaymentTableColumnKey | null;
  onColumnDragStart?: (key: PaymentTableColumnKey, event: ReactDragEvent<HTMLElement>) => void;
  onColumnDragOver?: (event: ReactDragEvent<HTMLElement>) => void;
  onColumnDrop?: (key: PaymentTableColumnKey, event: ReactDragEvent<HTMLElement>) => void;
  onColumnDragEnd?: () => void;
  onResizeStart?: (key: PaymentTableColumnKey, event: ReactMouseEvent<HTMLDivElement>) => void;
  getColumnWidth?: (key: PaymentTableColumnKey) => number;
  getTableWidth?: (extraWidth?: number, minimumWidth?: number) => number;
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
  onStatusChange,
  onAcceptAdvance,
  onIgnoreAdvance,
  onAllocateAdvance,
  canAdd,
  canEdit,
  canDelete,
  canEditPayment,
  canDeletePayment,
  columns,
  draggingColumn = null,
  onColumnDragStart,
  onColumnDragOver,
  onColumnDrop,
  onColumnDragEnd,
  onResizeStart,
  getColumnWidth,
  getTableWidth,
}: PaymentsTableProps) {
  const { t, formatDate } = useTranslation();
  const [menuState, setMenuState] = useState<MenuState>(null);

  const statusLabels = useMemo(
    () => ({
      Completed: t('statusCompleted') ?? t('completedStatus') ?? 'Выполнено',
      Pending: t('pending') ?? 'Ожидается',
      Overdue: t('overdue') ?? 'Просрочено',
      Processing: t('processingStatus') ?? 'Частично оплачено',
      Cancelled: t('cancelledStatus') ?? 'Отменено',
    }),
    [t],
  );

  const defaultColumns = useMemo<PaymentTableColumnDefinition[]>(
    () => [
      { key: 'date', label: t('date') ?? 'Дата' },
      { key: 'client', label: t('clients') ?? 'Клиенты' },
      { key: 'description', label: t('description') ?? 'Описание' },
      { key: 'type', label: t('type') ?? 'Тип' },
      { key: 'status', label: t('status') ?? t('statusFilter') ?? 'Статус' },
      { key: 'category', label: t('category') ?? 'Категория' },
      { key: 'amount', label: t('amount') ?? 'Сумма' },
    ],
    [t],
  );

  const visibleColumns = columns?.length ? columns : defaultColumns;
  const supportsColumnSettings = Boolean(
    onColumnDragStart && onColumnDragOver && onColumnDrop && onColumnDragEnd && onResizeStart && getColumnWidth,
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
    (event: ReactMouseEvent<HTMLTableRowElement>, payment: Payment) => {
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

  const renderCell = useCallback(
    (payment: Payment, columnKey: PaymentTableColumnKey) => {
      const isIncome = payment.type === 'Income';
      const statusStyle = STATUS_STYLES[payment.status] ?? 'bg-gray-100 text-gray-600';
      const amountText = formatCurrencySmart(payment.amount).full;
      const visibleNotes = getVisibleNotes(payment.notes);
      const needsRemainderTransfer = needsPartialRemainderTransfer(payment);
      const advanceReviewStatus = payment.advanceReviewStatus ?? 'None';
      const needsAdvanceDecision = advanceReviewStatus === 'RequiresDecision';
      const acceptedAsAdvance = advanceReviewStatus === 'AcceptedAsAdvance';
      const category =
        payment.dealType?.name || payment.incomeType?.name || payment.paymentSource?.name || '—';
      const statusLabel = statusLabels[payment.status] ?? payment.status;
      const paymentDocumentLabel = payment.paymentOrderNumber
        ? `Документ: ${payment.paymentOrderNumber}${payment.paymentDocumentDate ? ` от ${formatDate(payment.paymentDocumentDate)}` : ''}`
        : payment.paymentDocumentDate
        ? `Дата документа: ${formatDate(payment.paymentDocumentDate)}`
        : null;
      const linkedInvoiceLabel = payment.account
        ? `Счет: ${payment.account}${payment.accountDate ? ` от ${formatDate(payment.accountDate)}` : ''}`
        : payment.invoiceId
        ? `Счет ID ${payment.invoiceId}`
        : null;

      switch (columnKey) {
        case 'date':
          return payment.date ? formatDate(payment.date) : '—';
        case 'plannedDate':
          return payment.plannedDate ? formatDate(payment.plannedDate) : '—';
        case 'client':
          return payment.client ? (
            <div className="leading-tight">
              <button
                type="button"
                className="block w-full text-left text-blue-600 hover:underline"
                onClick={(event) => {
                  event.stopPropagation();
                  if (payment.clientId) {
                    onOpenClient?.(payment.clientId, payment.clientCaseId ?? undefined);
                  }
                }}>
                <span className="block font-medium">
                  {payment.type === 'Expense' ? `Поставщик: ${payment.client.name}` : payment.client.name}
                </span>
              </button>
              {payment.type === 'Expense' && payment.expenseClient?.name ? (
                <span className="mt-0.5 block truncate text-xs leading-tight text-gray-500">
                  {`Клиент расхода: ${payment.expenseClient.name}`}
                </span>
              ) : null}
              {payment.clientCase?.title ? (
                <span className="mt-0.5 block truncate text-xs leading-tight text-gray-500">
                  {payment.type === 'Expense'
                    ? `Дело расхода: ${payment.clientCase.title}`
                    : payment.clientCase.title}
                </span>
              ) : null}
            </div>
          ) : (
            <span className="text-gray-400">—</span>
          );
        case 'description':
          return (
            <>
              <div className="truncate" title={payment.description || undefined}>
                {payment.description || t('noDescription') || 'Без описания'}
              </div>
              {paymentDocumentLabel ? (
                <div className="truncate text-xs text-gray-500" title={paymentDocumentLabel}>
                  {paymentDocumentLabel}
                </div>
              ) : null}
              {linkedInvoiceLabel ? (
                <div className="truncate text-xs text-gray-500" title={linkedInvoiceLabel}>
                  {linkedInvoiceLabel}
                </div>
              ) : null}
              {visibleNotes ? (
                <div className="truncate text-xs text-gray-500" title={visibleNotes}>
                  {visibleNotes}
                </div>
              ) : null}
            </>
          );
        case 'type':
          return (
            <span
              className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${
                isIncome ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-700'
              }`}>
              {isIncome ? t('income') ?? 'Доход' : t('expense') ?? 'Расход'}
            </span>
          );
        case 'status':
          return (
            <div className="flex flex-col items-start gap-1">
              {canEdit && onStatusChange ? (
                <button
                  type="button"
                  onClick={(event) => {
                    event.stopPropagation();
                    onStatusChange(payment);
                  }}
                  className="inline-flex rounded-full transition hover:brightness-95 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                  title="Изменить статус платежа"
                >
                  <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${statusStyle}`}>
                    {statusLabel}
                  </span>
                </button>
              ) : (
                <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${statusStyle}`}>
                  {statusLabel}
                </span>
              )}
              {needsRemainderTransfer ? (
                <span className="inline-flex items-center rounded-full bg-amber-50 px-2.5 py-1 text-[11px] font-semibold text-amber-700 ring-1 ring-amber-200">
                  Нужен перенос остатка
                </span>
              ) : null}
              {needsAdvanceDecision ? (
                <span className="inline-flex items-center gap-1 rounded-full bg-red-50 px-2.5 py-1 text-[11px] font-semibold text-red-700 ring-1 ring-red-200">
                  <AlertTriangle className="h-3 w-3" />
                  Нужен разбор аванса
                </span>
              ) : null}
              {acceptedAsAdvance ? (
                <span className="inline-flex items-center gap-1 rounded-full bg-emerald-50 px-2.5 py-1 text-[11px] font-semibold text-emerald-700 ring-1 ring-emerald-200">
                  <CheckCircle2 className="h-3 w-3" />
                  Аванс на балансе
                </span>
              ) : null}
              {acceptedAsAdvance && canEdit ? (
                <button
                  type="button"
                  onClick={(event) => {
                    event.stopPropagation();
                    void onAllocateAdvance?.(payment);
                  }}
                  className="inline-flex items-center gap-1 rounded-md border border-blue-200 bg-blue-50 px-2 py-1 text-[11px] font-semibold text-blue-700 transition hover:bg-blue-100"
                  title="Распределить аванс по открытому долгу клиента"
                >
                  <SplitSquareHorizontal className="h-3 w-3" />
                  Распределить
                </button>
              ) : null}
              {needsAdvanceDecision && canEdit ? (
                <div className="flex flex-wrap gap-1">
                  <button
                    type="button"
                    onClick={(event) => {
                      event.stopPropagation();
                      void onAcceptAdvance?.(payment);
                    }}
                    className="inline-flex items-center gap-1 rounded-md border border-emerald-200 bg-emerald-50 px-2 py-1 text-[11px] font-semibold text-emerald-700 transition hover:bg-emerald-100"
                    title="Принять деньги как аванс на баланс клиента"
                  >
                    <CheckCircle2 className="h-3 w-3" />
                    Аванс
                  </button>
                  <button
                    type="button"
                    onClick={(event) => {
                      event.stopPropagation();
                      void onIgnoreAdvance?.(payment);
                    }}
                    className="inline-flex items-center gap-1 rounded-md border border-slate-200 bg-white px-2 py-1 text-[11px] font-semibold text-slate-600 transition hover:bg-slate-50"
                    title="Исключить платеж из контроля авансов"
                  >
                    <EyeOff className="h-3 w-3" />
                    Игнор
                  </button>
                </div>
              ) : null}
            </div>
          );
        case 'category':
          return category;
        case 'amount':
          return (
            <div className="leading-tight">
              <span className="font-semibold text-gray-900">{amountText}</span>
              {needsRemainderTransfer ? (
                <span className="mt-1 block text-xs font-medium text-amber-700">
                  {`Остаток: ${formatCurrencySmart(payment.amount - payment.paidAmount).full}`}
                </span>
              ) : null}
            </div>
          );
        case 'paidDate':
          return payment.paidDate || payment.lastPaymentDate
            ? formatDate(payment.paidDate || payment.lastPaymentDate || '')
            : '—';
        case 'createdAt':
          return payment.createdAt ? formatDate(payment.createdAt) : '—';
        default:
          return '—';
      }
    },
    [canEdit, formatDate, onAcceptAdvance, onAllocateAdvance, onIgnoreAdvance, onOpenClient, onStatusChange, statusLabels, t],
  );

  const isEditAllowed = useCallback(
    (payment: Payment) => canEdit && (canEditPayment ? canEditPayment(payment) : true),
    [canEdit, canEditPayment],
  );

  const isDeleteAllowed = useCallback(
    (payment: Payment) => canDelete && (canDeletePayment ? canDeletePayment(payment) : true),
    [canDelete, canDeletePayment],
  );

  const openPayment = useCallback(
    (payment: Payment) => {
      if (isEditAllowed(payment)) {
        onEditPayment(payment);
      }
    },
    [isEditAllowed, onEditPayment],
  );

  const handleRowKeyDown = useCallback(
    (event: ReactKeyboardEvent<HTMLTableRowElement>, payment: Payment) => {
      if (event.key !== 'Enter' && event.key !== ' ') {
        return;
      }

      event.preventDefault();
      openPayment(payment);
    },
    [openPayment],
  );

  const menuEditAllowed = menuState ? isEditAllowed(menuState.payment) : false;
  const menuDeleteAllowed = menuState ? isDeleteAllowed(menuState.payment) : false;

  return (
    <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
      <div className="overflow-x-auto">
        <table
          className="min-w-[960px] w-full table-fixed divide-y divide-gray-200"
          style={getTableWidth ? { width: getTableWidth(0, 960), minWidth: '100%' } : undefined}
        >
          <colgroup>
            {visibleColumns.map((column) => (
              <col
                key={column.key}
                style={getColumnWidth ? { width: getColumnWidth(column.key) } : undefined}
              />
            ))}
          </colgroup>
          <thead className="bg-gray-50">
            <tr>
              {visibleColumns.map((column) => (
                <th
                  key={column.key}
                  scope="col"
                  style={getColumnWidth ? { width: getColumnWidth(column.key) } : undefined}
                  className={`relative px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide ${
                    draggingColumn === column.key ? 'bg-blue-50 text-blue-700' : 'text-gray-500'
                  }`}
                  onDragOver={onColumnDragOver}
                  onDrop={onColumnDrop ? (event) => onColumnDrop(column.key, event) : undefined}>
                  <div className="flex items-center justify-between gap-2">
                    {supportsColumnSettings ? (
                      <div
                        className="inline-flex min-w-0 select-none items-center gap-2"
                        draggable
                        onDragStart={(event) => onColumnDragStart?.(column.key, event)}
                        onDragEnd={onColumnDragEnd}
                        title="Перетащите, чтобы изменить порядок">
                        <GripVertical className="h-4 w-4 text-slate-400" />
                        <span className="truncate">{column.label}</span>
                      </div>
                    ) : (
                      <span className="truncate">{column.label}</span>
                    )}
                  </div>
                  {onResizeStart ? (
                    <div
                      role="separator"
                      aria-label={`Изменить ширину колонки ${column.label}`}
                      className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                      onMouseDown={(event) => onResizeStart(column.key, event)}
                      onDragStart={(event) => event.preventDefault()}
                    />
                  ) : null}
                </th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {payments.length === 0 ? (
              <tr>
                <td
                  colSpan={visibleColumns.length}
                  className="px-4 py-6 text-center text-sm text-gray-500">
                  {t('noPaymentsForFilter') ?? 'Нет платежей, подходящих под выбранные условия.'}
                </td>
              </tr>
            ) : (
              payments.map((payment) => (
                <tr
                  key={payment.id}
                  onContextMenu={(event) => openMenu(event, payment)}
                  onClick={() => openPayment(payment)}
                  onKeyDown={(event) => handleRowKeyDown(event, payment)}
                  tabIndex={isEditAllowed(payment) ? 0 : undefined}
                  className={`transition-colors hover:bg-gray-50/60 ${
                    isEditAllowed(payment) ? 'cursor-pointer focus:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-inset focus:ring-emerald-500' : 'cursor-default'
                  }`}
                  title={
                    canAdd || isEditAllowed(payment) || isDeleteAllowed(payment)
                      ? t('contextMenuHint') ?? 'ПКМ для действий'
                      : undefined
                  }>
                  {visibleColumns.map((column) => (
                    <td
                      key={column.key}
                      style={getColumnWidth ? { width: getColumnWidth(column.key) } : undefined}
                      className="max-w-0 overflow-hidden px-4 py-3 align-top text-sm text-gray-700">
                      {renderCell(payment, column.key)}
                    </td>
                  ))}
                </tr>
              ))
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
              className={`flex w-full items-center justify-between px-4 py-2 text-left text-sm transition-colors ${
                canAdd ? 'text-gray-700 hover:bg-gray-100' : 'cursor-not-allowed text-gray-400'
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
                if (menuEditAllowed) onEditPayment(menuState.payment);
              }}
              className={`flex w-full items-center justify-between px-4 py-2 text-left text-sm transition-colors ${
                menuEditAllowed ? 'text-gray-700 hover:bg-gray-100' : 'cursor-not-allowed text-gray-400'
              }`}
              disabled={!menuEditAllowed}
              title={
                !menuEditAllowed
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
                if (!menuDeleteAllowed) return;
                await onDeletePayment?.(menuState.payment);
              }}
              className={`flex w-full items-center justify-between px-4 py-2 text-left text-sm transition-colors ${
                menuDeleteAllowed ? 'text-red-600 hover:bg-red-50' : 'cursor-not-allowed text-gray-400'
              }`}
              disabled={!menuDeleteAllowed}
              title={
                !menuDeleteAllowed
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
