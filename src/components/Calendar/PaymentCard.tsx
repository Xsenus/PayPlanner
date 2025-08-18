import { TrendingUp, TrendingDown } from 'lucide-react';
import type { Payment } from '../../types';
import type React from 'react';

interface PaymentCardProps {
  payment: Payment;
  onClick?: () => void;
  onClientClick?: (clientId: number, clientCaseId?: number) => void; // 👈 новый проп
  neutral?: boolean;
  className?: string;
}

export function PaymentCard({
  payment,
  onClick,
  onClientClick,
  neutral = false,
  className = '',
}: PaymentCardProps) {
  const isIncome = payment.type === 'Income';

  const base = 'p-1.5 sm:p-2 rounded-lg text-[11px] sm:text-xs select-none pr-6';
  const interactive = onClick ? 'cursor-pointer hover:bg-gray-50 transition-colors' : '';

  return (
    <div onClick={onClick} className={[base, interactive, className].join(' ')}>
      <div className="flex items-center justify-between mb-0.5 sm:mb-1">
        <div className="flex items-center gap-1">
          {isIncome ? (
            <TrendingUp className="h-3.5 w-3.5 text-emerald-600" />
          ) : (
            <TrendingDown className="h-3.5 w-3.5 text-red-600" />
          )}
          <span className="font-medium text-gray-900">
            {new Intl.NumberFormat('ru-RU').format(payment.amount)} ₽
          </span>
        </div>

        {!neutral && <span className="text-[10px] sm:text-xs text-gray-500">{payment.status}</span>}
      </div>

      <div className="truncate text-gray-700" title={payment.description}>
        {payment.description || 'Без описания'}
      </div>

      {payment.client && (
        <div className="text-[10px] sm:text-xs text-gray-500 mt-0.5 sm:mt-1 truncate">
          <button
            type="button"
            onClick={(e: React.MouseEvent<HTMLButtonElement>) => {
              e.stopPropagation(); // чтобы не сработал onClick карточки
              onClientClick?.(payment.clientId ?? -1, payment.clientCaseId);
            }}
            className="text-blue-600 hover:underline">
            {payment.client.name}
          </button>
        </div>
      )}
    </div>
  );
}
