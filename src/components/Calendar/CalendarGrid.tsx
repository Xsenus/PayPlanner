import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { PaymentCard } from './PaymentCard';
import { useTranslation } from '../../hooks/useTranslation';
import type { Payment } from '../../types';
import { CheckCircle, Clock, AlertTriangle } from 'lucide-react';
import { toDateInputValue } from '../../utils/dateUtils';

interface CalendarGridProps {
  currentDate: Date;
  payments: Payment[];
  onEditPayment: (payment: Payment) => void;
  newPaymentIds?: Set<number>;
  onOpenClient?: (clientId: number, clientCaseId?: number) => void;
}

type NormalizedStatus = 'completed' | 'pending' | 'overdue';
type NormalizedType = 'income' | 'expense';

function normalizeStatus(s?: string): NormalizedStatus {
  const v = (s ?? '').toLowerCase();
  if (v.includes('complete') || v.includes('выполн')) return 'completed';
  if (v.includes('overdue') || v.includes('проср')) return 'overdue';
  return 'pending';
}
function normalizeType(t?: Payment['type']): NormalizedType {
  return t === 'Income' ? 'income' : 'expense';
}

function styleByTypeAndStatus(p: Payment): {
  icon: ReactNode;
  wrapperClass: string;
  stripeClass: string;
  ariaLabel: string;
} {
  const s = normalizeStatus(p.status);
  const k = normalizeType(p.type);

  const base =
    k === 'income'
      ? 'bg-emerald-50 border-emerald-200 text-emerald-900'
      : 'bg-rose-50 border-rose-200 text-rose-900';

  if (s === 'overdue') {
    return {
      icon: <AlertTriangle className="h-4 w-4 text-purple-700" />,
      wrapperClass: 'bg-purple-50 border-purple-300 ring-1 ring-purple-300 text-purple-900',
      stripeClass: 'bg-purple-600',
      ariaLabel: 'Просрочено',
    };
  }

  if (s === 'completed') {
    return {
      icon: <CheckCircle className="h-4 w-4 text-emerald-600" />,
      wrapperClass: `${base} ring-1 ring-emerald-300`,
      stripeClass: 'bg-emerald-600',
      ariaLabel: 'Выполнено',
    };
  }

  return {
    icon: <Clock className="h-4 w-4 text-amber-600" />,
    wrapperClass: `${base} ring-1 ring-amber-200`,
    stripeClass: 'bg-amber-500',
    ariaLabel: 'Ожидается',
  };
}

export function CalendarGrid({
  currentDate,
  payments,
  onEditPayment,
  newPaymentIds,
  onOpenClient,
}: CalendarGridProps) {
  const { t } = useTranslation();
  const year = currentDate.getFullYear();
  const month = currentDate.getMonth();

  const firstDayOfMonth = new Date(year, month, 1);
  const lastDayOfMonth = new Date(year, month + 1, 0);
  const firstDayWeekday = ((firstDayOfMonth.getDay() + 6) % 7);
  const daysInMonth = lastDayOfMonth.getDate();

  const calendarDays: (number | null)[] = useMemo(() => {
    const arr: (number | null)[] = [];
    for (let i = 0; i < firstDayWeekday; i++) arr.push(null);
    for (let d = 1; d <= daysInMonth; d++) arr.push(d);
    return arr;
  }, [firstDayWeekday, daysInMonth]);

  const paymentsByDate = useMemo(() => {
    const acc: Record<number, Payment[]> = {};
    for (const p of payments) {
      const effective = p.effectiveDate ?? p.paidDate ?? p.date;
      const ymd = toDateInputValue(effective);
      if (!ymd) continue;
      const [ys, ms, ds] = ymd.split('-');
      const y = +ys,
        m = +ms - 1,
        d = +ds;
      if (y !== year || m !== month) continue;
      (acc[d] ||= []).push(p);
    }
    return acc;
  }, [payments, year, month]);

  const weekDays = [
    t('monday'),
    t('tuesday'),
    t('wednesday'),
    t('thursday'),
    t('friday'),
    t('saturday'),
    t('sunday'),
  ];

  const [isDesktop, setIsDesktop] = useState<boolean>(() =>
    typeof window !== 'undefined' ? window.matchMedia('(min-width: 768px)').matches : true,
  );
  useEffect(() => {
    if (typeof window === 'undefined') return;
    const mq = window.matchMedia('(min-width: 768px)');
    const onChange = () => setIsDesktop(mq.matches);
    onChange();
    mq.addEventListener?.('change', onChange);
    return () => mq.removeEventListener?.('change', onChange);
  }, []);

  const isThisMonth = new Date().getFullYear() === year && new Date().getMonth() === month;
  const today = isThisMonth ? new Date().getDate() : -1;

  const containerRef = useRef<HTMLDivElement | null>(null);
  const dayRefs = useRef<Record<number, HTMLDivElement | null>>({});

  useEffect(() => {
    if (isDesktop || !isThisMonth) return;
    const el = dayRefs.current[today];
    el?.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
  }, [isDesktop, isThisMonth, today]);

  const perCellLimit = isDesktop ? 3 : 2;

  const [expandedDays, setExpandedDays] = useState<Set<number>>(new Set());
  const toggleDay = (d: number) =>
    setExpandedDays((prev) => {
      const next = new Set(prev);
      if (next.has(d)) {
        next.delete(d);
      } else {
        next.add(d);
      }
      return next;
    });

  const cardVariants = {
    initial: { opacity: 0, y: 6, scale: 0.98 },
    animate: { opacity: 1, y: 0, scale: 1 },
    exit: { opacity: 0, y: -6, scale: 0.98 },
  } as const;

  return (
    <div className="bg-white rounded-xl shadow-sm border border-gray-200">
      <div ref={containerRef} className="overflow-x-auto">
        <div className="grid w-full [grid-template-columns:repeat(7,33.333%)] md:grid-cols-7">
          {weekDays.map((day) => (
            <div
              key={`head-${day}`}
              className="p-2 md:p-3 text-center text-xs md:text-sm font-medium text-gray-600 bg-gray-50 border-b border-gray-200">
              {day}
            </div>
          ))}

          {calendarDays.map((day, i) => {
            const isToday = day === today;
            return (
              <div
                key={`cell-${i}`}
                ref={(el) => {
                  if (day) dayRefs.current[day] = el;
                }}
                className="min-h-[110px] md:min-h-[140px] p-2 md:p-3 border-b border-r last:border-r-0 border-gray-200">
                {day && (
                  <>
                    <div className="flex items-center gap-2 mb-1 md:mb-2">
                      <span
                        className={[
                          'inline-flex items-center justify-center',
                          'h-6 w-6 text-[11px] font-semibold rounded-md',
                          isToday ? 'bg-emerald-100 text-emerald-700' : 'text-gray-900',
                        ].join(' ')}
                        title={String(day)}>
                        {day}
                      </span>

                      {(() => {
                        const items = paymentsByDate[day] ?? [];
                        const hasOverdue = items.some(
                          (p) => normalizeStatus(p.status) === 'overdue',
                        );
                        const hasCompleted = items.some(
                          (p) => normalizeStatus(p.status) === 'completed',
                        );
                        const hasPending = items.some(
                          (p) => normalizeStatus(p.status) === 'pending',
                        );

                        const dotClass = hasOverdue
                          ? 'bg-purple-600'
                          : hasPending
                          ? 'bg-amber-600'
                          : hasCompleted
                          ? 'bg-emerald-600'
                          : '';

                        return dotClass ? (
                          <span
                            className={`inline-block h-2 w-2 rounded-full ${dotClass}`}
                            title={
                              hasOverdue
                                ? 'Есть просроченные'
                                : hasPending
                                ? 'Есть ожидающиеся'
                                : 'Все выполнено'
                            }
                          />
                        ) : null;
                      })()}

                      {isToday && (
                        <motion.span
                          layout="position"
                          className="ml-auto hidden md:inline-block h-2 w-2 rounded-full bg-emerald-500/70"
                          initial={{ scale: 0.6, opacity: 0 }}
                          animate={{ scale: 1, opacity: 1 }}
                          transition={{ duration: 0.2 }}
                        />
                      )}
                    </div>

                    <div className="space-y-1">
                      <AnimatePresence initial={false}>
                        {(() => {
                          const items = paymentsByDate[day] ?? [];
                          const isExpanded = expandedDays.has(day);
                          const visible = isExpanded ? items : items.slice(0, perCellLimit);
                          return visible.map((payment, idx) => {
                            const isNew = !!newPaymentIds?.has(payment.id);
                            const meta = styleByTypeAndStatus(payment);

                            return (
                              <motion.div
                                key={payment.id}
                                layout
                                variants={cardVariants}
                                initial="initial"
                                animate="animate"
                                exit="exit"
                                transition={{
                                  duration: 0.18,
                                  delay: Math.min(idx * 0.03, 0.12),
                                }}
                                className={[
                                  'relative rounded-md shadow-sm',
                                  'border',
                                  'transition',
                                  meta.wrapperClass,
                                  isNew ? 'ring-2 ring-emerald-400' : '',
                                ]
                                  .filter(Boolean)
                                  .join(' ')}
                                whileHover={{ scale: 1.01 }}
                                aria-label={meta.ariaLabel}>
                                <div
                                  className={[
                                    'absolute left-0 top-0 bottom-0 w-1 rounded-l-md',
                                    meta.stripeClass,
                                  ].join(' ')}
                                  aria-hidden
                                />

                                <div
                                  className="absolute right-1.5 top-1.5 pointer-events-none"
                                  title={meta.ariaLabel}>
                                  {meta.icon}
                                </div>

                                <PaymentCard
                                  payment={payment}
                                  onClick={() => onEditPayment(payment)}
                                  onClientClick={onOpenClient}
                                  neutral
                                />
                              </motion.div>
                            );
                          });
                        })()}
                      </AnimatePresence>

                      {(() => {
                        const items = paymentsByDate[day] ?? [];
                        const isExpanded = expandedDays.has(day);
                        const hiddenCount = Math.max(
                          items.length - (isExpanded ? items.length : perCellLimit),
                          0,
                        );

                        if (!isExpanded && hiddenCount > 0) {
                          return (
                            <motion.button
                              type="button"
                              onClick={() => toggleDay(day)}
                              className="w-full text-[10px] md:text-xs text-emerald-700 hover:text-emerald-800 hover:underline py-0.5 md:py-1"
                              initial={{ opacity: 0 }}
                              animate={{ opacity: 1 }}
                              transition={{ duration: 0.15 }}>
                              +{hiddenCount} {t('more') ?? 'ещё'}
                            </motion.button>
                          );
                        }
                        if (isExpanded && items.length > perCellLimit) {
                          return (
                            <button
                              type="button"
                              onClick={() => toggleDay(day)}
                              className="w-full text-[10px] md:text-xs text-slate-600 hover:text-slate-800 hover:underline py-0.5 md:py-1">
                              — {'скрыть'}
                            </button>
                          );
                        }
                        return null;
                      })()}
                    </div>
                  </>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
