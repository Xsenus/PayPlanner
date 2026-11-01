import { useEffect, useMemo, useRef, useState } from 'react';
import { Calculator as CalculatorIcon, Info, X } from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import type { InstallmentPlanInput, Payment, PaymentPayload } from '../../types';
import { apiService } from '../../services/api';
import { acknowledgeCreatedPayment, canContinueCreatedPayment, PaymentCreateAdoptedError, PAYMENT_CREATE_CHANGED, PaymentCreateRecoveryError, paymentCreateSessionVersion } from '../../services/paymentCreateAttempts';
import { PaymentCreateRecovery } from '../Payments/PaymentCreateRecovery';
import { useDialog } from '../../hooks/useDialog';
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

export function Calculator({ onOpenPayment }: { onOpenPayment?: (id: number, type: Payment['type']) => void } = {}) {
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const canViewClients = permissions.clients.canView;
  const canViewLegalEntities = permissions.legalEntities.canView;
  const canViewCases = permissions.cases.canView;
  const canSavePayments = permissions.payments.canView && permissions.payments.canCreate;
  const allowedCounterpartyScopes = useMemo(
    () => [
      ...(canViewClients ? ['client' as const] : []),
      ...(canViewLegalEntities ? ['legalEntity' as const] : []),
    ],
    [canViewClients, canViewLegalEntities],
  );
  const canSelectCounterparty = allowedCounterpartyScopes.length > 0;
  const clientOwnerScope = JSON.stringify([user?.id, user?.role?.id, permissions.clients, permissions.cases, permissions.contracts,
    permissions.accounts, permissions.acts, permissions.payments]);
  const clientsOptions = useMemo(() => ({ enabled: canViewClients, ownerScope: clientOwnerScope }), [canViewClients, clientOwnerScope]);
  const { clients } = useClients(clientsOptions);
  const toast = useToast();
  const { confirm } = useDialog();
  const savingRef = useRef(false);
  const liveRef = useRef(true);
  const ownerVersionRef = useRef(paymentCreateSessionVersion());
  const runRef = useRef<{ actorVersion: string; completed: number; blocked: boolean; attemptId?: string } | null>(null);
  const [partialNotice, setPartialNotice] = useState<string | null>(null);
  const [draft, setDraft] = useState<DraftState | null>(null);
  useEffect(() => {
    liveRef.current = true;
    const ownerChanged = () => {
      const next = paymentCreateSessionVersion();
      if (ownerVersionRef.current === next) return;
      ownerVersionRef.current = next;
      runRef.current = null;
      savingRef.current = false;
      setPartialNotice(null);
      setDraft(null);
    };
    window.addEventListener(PAYMENT_CREATE_CHANGED, ownerChanged);
    window.addEventListener('storage', ownerChanged);
    return () => {
      liveRef.current = false;
      window.removeEventListener(PAYMENT_CREATE_CHANGED, ownerChanged);
      window.removeEventListener('storage', ownerChanged);
    };
  }, []);


  useEffect(() => {
    setDraft(consumeInstallmentDraft());
  }, []);

  const handleSavePayments = async ({
    plan,
    clientId,
    legalEntityId,
    clientCaseId,
    invoiceNumber,
    clientName,
  }: {
    plan: InstallmentPlanInput;
    clientId?: number;
    legalEntityId?: number;
    clientCaseId?: number;
    invoiceNumber?: string | null;
    clientName?: string | null;
  }) => {
    if (!canSavePayments) throw new Error('Недостаточно прав для создания платежей.');
    if (savingRef.current) throw new Error('Сохранение графика уже выполняется.');
    const actorVersion = paymentCreateSessionVersion();
    if (runRef.current?.actorVersion !== actorVersion) runRef.current = null;
    if (runRef.current?.blocked) throw new Error('График сохранён частично. Проверьте созданные платежи перед новым графиком.');
    const run = { actorVersion, completed: 0, blocked: false, attemptId: undefined as string | undefined };
    runRef.current = run;
    savingRef.current = true;
    try {
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
          legalEntityId,
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

        const saved = await apiService.createPayment(payload);
        if (!liveRef.current || actorVersion !== paymentCreateSessionVersion()) return;
        if (!canContinueCreatedPayment(saved)) {
          run.completed += 1; run.blocked = true;
          setPartialNotice(`Подтверждено платежей: ${run.completed}. Автоматическое продолжение остановлено после открытия сохранённого платежа.`); return;
        }
        run.completed += 1;
        const adopted = await acknowledgeCreatedPayment(saved);
        if (!liveRef.current || actorVersion !== paymentCreateSessionVersion()) return;
        if (!adopted) {
          run.blocked = true; setPartialNotice(`Подтверждено платежей: ${run.completed}. Проверьте сохранение перед продолжением.`); return;
        }
      }

      toast.success(
        clientName
          ? `График платежей сохранен для клиента «${clientName}».`
          : 'График платежей сохранен.',
      );
      runRef.current = null;
      setPartialNotice(null);
    } catch (error) {
      if (error instanceof PaymentCreateAdoptedError) {
        if (liveRef.current && actorVersion === paymentCreateSessionVersion()) {
          run.completed += 1; run.blocked = true;
          setPartialNotice(`Подтверждено платежей: ${run.completed}. Сохранённый платёж открыт; график автоматически не продолжается.`);
        }
        return;
      }
      if (actorVersion === paymentCreateSessionVersion()) {
        if (error instanceof PaymentCreateRecoveryError && error.startedHere) run.attemptId = error.attempt.attemptId;
        run.blocked = run.completed > 0 || Boolean(run.attemptId);
        if (run.blocked) setPartialNotice(`Подтверждено платежей: ${run.completed}. График остановлен; повтор всего графика может создать дубликаты.`);
      }
      throw error;
    } finally { if (!runRef.current || runRef.current === run) savingRef.current = false; }
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
                {canSavePayments
                  ? 'Рассчитайте график, проверьте платежи и сохраните их в календарь.'
                  : 'Рассчитайте график и проверьте будущие платежи.'}
              </p>
            </div>
          </div>
        </div>
      </header>

      <div className="mx-auto max-w-[calc(100vw-2rem)] px-4 py-6 sm:px-6">
        {draft ? (
          <div className="mb-5 rounded-2xl border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-800">
            <div className="flex items-start justify-between gap-3">
              <div className="flex items-start gap-2">
                <Info className="mt-0.5 h-4 w-4 shrink-0" />
                <div>
                  <div className="font-medium">Данные подставлены из счета</div>
                  <div className="mt-1">
                    {draft.invoiceNumber ? `Счет №${draft.invoiceNumber}. ` : ''}
                    {canViewClients && draft.clientName ? `Клиент: ${draft.clientName}. ` : ''}
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

        <PaymentCreateRecovery
          scopeVersion={paymentCreateSessionVersion}
          onOpen={(saved, attempt) => {
            if (!onOpenPayment) throw new Error('Открытие платежа недоступно. Перейдите в раздел «Платежи» и проверьте сохранённый номер.');
            const run = runRef.current;
            if (run?.actorVersion === paymentCreateSessionVersion() && run.attemptId === attempt.attemptId) {
              run.completed += 1;
              run.attemptId = undefined;
              run.blocked = true;
              setPartialNotice(`Подтверждено платежей: ${run.completed}. Проверьте их перед новым графиком.`);
            }
            onOpenPayment(saved.id, saved.type);
            return true;
          }}
          onCancelled={(attempt) => {
            const run = runRef.current;
            if (run?.actorVersion === paymentCreateSessionVersion() && run.attemptId === attempt.attemptId) {
              run.attemptId = undefined;
              run.blocked = run.completed > 0;
              if (!run.blocked) setPartialNotice(null);
            }
          }}
        />
        {partialNotice && <div className="my-3 space-y-2 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm">
          <p>{partialNotice}</p>
          <button type="button" className="rounded border px-2 py-1" onClick={async () => {
            const version = paymentCreateSessionVersion();
            const approved = await confirm({ title: 'Начать другой график?',
              description: 'Уже созданные платежи останутся. Повтор прежнего графика может добавить их заново. Сначала проверьте список платежей.',
              confirmLabel: 'Начать другой график', cancelLabel: 'Вернуться к проверке', variant: 'danger' });
            if (approved && version === paymentCreateSessionVersion() && !savingRef.current) {
              runRef.current = null;
              setPartialNotice(null);
            }
          }}>Начать другой график</button>
        </div>}
        <InstallmentPlanner
          mode="calculator"
          clients={clients}
          initialTotal={draft?.total}
          initialStartDate={draft?.startDate}
          initialClientId={canViewClients ? draft?.clientId : undefined}
          initialClientName={canViewClients ? draft?.clientName : undefined}
          initialInvoiceNumber={draft?.invoiceNumber}
          canSelectCounterparty={canSelectCounterparty}
          allowedCounterpartyScopes={allowedCounterpartyScopes}
          canSelectCase={canViewCases}
          canSavePayments={canSavePayments}
          onSavePayments={handleSavePayments}
        />
      </div>
    </div>
  );
}
