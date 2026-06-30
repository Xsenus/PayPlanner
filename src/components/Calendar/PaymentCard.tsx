import { TrendingUp, TrendingDown } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import type { Payment } from '../../types';
import type React from 'react';

interface PaymentCardProps {
  payment: Payment;
  onClick?: () => void;
  onClientClick?: (clientId: number, clientCaseId?: number) => void;
  onUserClick?: (userId: number) => void;
  neutral?: boolean;
  className?: string;
}

export function PaymentCard({
  payment,
  onClick,
  onClientClick,
  onUserClick,
  neutral = false,
  className = '',
}: PaymentCardProps) {
  const { t } = useTranslation();
  const isIncome = payment.type === 'Income';
  const hasRemainder = payment.hasPartialPayment && payment.outstandingAmount > 0;
  const amountToShow = hasRemainder ? payment.outstandingAmount : payment.amount;
  const counterpartyName = payment.counterpartyName?.trim() ?? '';
  const canOpenContractorUser = Boolean(onUserClick && payment.type === 'Expense' && payment.contractorUserId);

  const formatMoney = (v: number) => new Intl.NumberFormat('ru-RU').format(v);

  const base = 'p-1.5 sm:p-2 rounded-lg text-[11px] sm:text-xs select-none pr-6 border';
  const interactive = onClick ? 'cursor-pointer hover:-translate-y-[1px] transition-transform' : '';
  const surface = hasRemainder
    ? 'bg-amber-50 border-amber-200 shadow-[0_1px_0_rgba(251,191,36,0.45)]'
    : 'bg-white border-gray-100 shadow-[0_1px_0_rgba(0,0,0,0.05)]';

  return (
    <div onClick={onClick} className={[base, interactive, surface, className].join(' ')}>
      <div className="flex items-center justify-between mb-0.5 sm:mb-1">
        <div className="flex items-center gap-1.5">
          {isIncome ? (
            <TrendingUp className="h-3.5 w-3.5 text-emerald-600" />
          ) : (
            <TrendingDown className="h-3.5 w-3.5 text-red-600" />
          )}
          <div className="leading-tight">
            <div className="flex items-center gap-1">
              <span className="font-semibold text-gray-900">
                {formatMoney(amountToShow)} ₽
              </span>
              {hasRemainder && (
                <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-amber-800">
                  {t('outstandingShort') ?? 'Остаток'}
                </span>
              )}
            </div>
            {hasRemainder && (
              <div className="text-[10px] text-amber-800">
                {t('outstandingFromFull', { amount: formatMoney(payment.amount) }) ?? `От общей суммы ${formatMoney(payment.amount)} ₽`}
              </div>
            )}
          </div>
        </div>

        {!neutral && (
          <span className="text-[10px] sm:text-xs text-gray-500 whitespace-nowrap">
            {payment.status}
          </span>
        )}
      </div>

      <div className="truncate text-gray-700" title={payment.description}>
        {payment.description || 'Без описания'}
      </div>

      {payment.client && (
        <div className="text-[10px] sm:text-xs text-gray-500 mt-0.5 sm:mt-1 truncate">
          {onClientClick ? (
            <button
              type="button"
              onClick={(e: React.MouseEvent<HTMLButtonElement>) => {
                e.stopPropagation();
                onClientClick(
                  payment.clientId ?? -1,
                  payment.clientCaseId ? payment.clientCaseId : undefined,
                );
              }}
              className="text-blue-600 hover:underline">
              {payment.client.name}
            </button>
          ) : (
            <span>{payment.client.name}</span>
          )}
        </div>
      )}
      {!payment.client && counterpartyName ? (
        <div className="text-[10px] sm:text-xs text-gray-500 mt-0.5 sm:mt-1 truncate">
          {canOpenContractorUser ? (
            <button
              type="button"
              onClick={(e: React.MouseEvent<HTMLButtonElement>) => {
                e.stopPropagation();
                if (payment.contractorUserId) {
                  onUserClick?.(payment.contractorUserId);
                }
              }}
              className="text-blue-600 hover:underline">
              {payment.type === 'Expense' ? `Поставщик: ${counterpartyName}` : counterpartyName}
            </button>
          ) : (
            <span>{payment.type === 'Expense' ? `Поставщик: ${counterpartyName}` : counterpartyName}</span>
          )}
        </div>
      ) : null}
    </div>
  );
}
