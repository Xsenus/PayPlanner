import { useEffect, useState } from 'react';
import { Calculator as CalculatorIcon, Info, X } from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useToast } from '../../hooks/useToast';
import type { InstallmentPlanInput, PaymentPayload } from '../../types';
import { apiService } from '../../services/api';
import { formatCurrencySmart } from '../../utils/formatters';
import { consumeInstallmentDraft } from '../../utils/installmentBridge';
import { InstallmentPlanner } from '../Accounts/InstallmentPlanner';

type DraftState = {
  total?: number;
  startDate?: string;
  clientId?: number;
  clientName?: string;
  invoiceNumber?: string;
};

export function Calculator() {
  const { clients } = useClients();
  const toast = useToast();
  const [draft, setDraft] = useState<DraftState | null>(null);

  useEffect(() => {
    setDraft(consumeInstallmentDraft());
  }, []);

  const handleSavePayments = async ({
    plan,
    clientId,
    clientCaseId,
    invoiceNumber,
    clientName,
  }: {
    plan: InstallmentPlanInput;
    clientId: number;
    clientCaseId?: number;
    invoiceNumber?: string | null;
    clientName?: string | null;
  }) => {
    const today = new Date().toISOString().slice(0, 10);
    const totalCount = plan.items.length;

    for (const item of plan.items) {
      const paymentDate = item.date;
      const payload: PaymentPayload = {
        date: paymentDate,
        plannedDate: paymentDate,
        amount: item.payment,
        paidAmount: 0,
        lastPaymentDate: undefined,
        type: 'Income',
        status: paymentDate < today ? 'Overdue' : 'Pending',
        description: invoiceNumber
          ? `Рассрочка по счету №${invoiceNumber} · платеж ${item.sequence}/${totalCount}`
          : `Платеж по рассрочке ${item.sequence}/${totalCount}`,
        isPaid: false,
        paidDate: undefined,
        notes: [
          `Основной долг: ${formatCurrencySmart(item.principal, { alwaysCents: true }).full}`,
          `Проценты: ${formatCurrencySmart(item.interest, { alwaysCents: true }).full}`,
          `Остаток: ${formatCurrencySmart(item.balance, { alwaysCents: true }).full}`,
        ].join('\n'),
        systemNotes: '',
        rescheduleCount: 0,
        clientId,
        clientCaseId,
        dealTypeId: undefined,
        incomeTypeId: undefined,
        paymentSourceId: undefined,
        paymentStatusId: undefined,
        invoiceId: undefined,
        account: undefined,
        accountDate: undefined,
        contractId: undefined,
        documentKind: 'Payment',
      };

      await apiService.createPayment(payload);
    }

    toast.success(
      clientName
        ? `График платежей сохранен для клиента «${clientName}».`
        : 'График платежей сохранен.',
    );
  };

  return (
    <div className="min-h-screen bg-slate-50">
      <header className="border-b border-slate-200 bg-white">
        <div className="mx-auto max-w-[calc(100vw-2rem)] px-4 py-5 sm:px-6">
          <div className="flex items-start gap-3">
            <div className="rounded-xl bg-blue-50 p-3 text-blue-600">
              <CalculatorIcon className="h-6 w-6" />
            </div>
            <div>
              <h1 className="text-2xl font-bold text-slate-900">Калькулятор рассрочки</h1>
              <p className="mt-1 text-sm text-slate-500">
                Рассчитайте график, проверьте платежи и сохраните их в календарь.
              </p>
            </div>
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-[calc(100vw-2rem)] px-4 py-6 sm:px-6">
        {draft ? (
          <div className="mb-5 rounded-2xl border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-800">
            <div className="flex items-start justify-between gap-3">
              <div className="flex items-start gap-2">
                <Info className="mt-0.5 h-4 w-4 shrink-0" />
                <div>
                  <div className="font-medium">Данные подставлены из счета</div>
                  <div className="mt-1">
                    {draft.invoiceNumber ? `Счет №${draft.invoiceNumber}. ` : ''}
                    {draft.clientName ? `Клиент: ${draft.clientName}. ` : ''}
                    При необходимости измените параметры и сохраните график.
                  </div>
                </div>
              </div>
              <button
                type="button"
                onClick={() => setDraft(null)}
                className="rounded-full p-1 text-blue-700 transition hover:bg-blue-100"
                aria-label="Скрыть уведомление"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </div>
        ) : null}

        <InstallmentPlanner
          mode="calculator"
          clients={clients}
          initialTotal={draft?.total}
          initialStartDate={draft?.startDate}
          initialClientId={draft?.clientId}
          initialClientName={draft?.clientName}
          initialInvoiceNumber={draft?.invoiceNumber}
          onSavePayments={handleSavePayments}
        />
      </main>
    </div>
  );
}
