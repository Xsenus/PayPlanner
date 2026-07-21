import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { CalendarDays, CreditCard, Loader2, ReceiptText, UserRoundSearch, Wallet } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useClientCases } from '../../hooks/useClientCases';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type {
  Client,
  InstallmentPlanInput,
  InstallmentRequest,
  InstallmentResponse,
  InstallmentRoundingMode,
} from '../../types';
import { formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { cloneInstallmentPlan, createInstallmentPlanInput } from '../../utils/installmentPlan';
import { CounterpartyPickerModal, type CounterpartyKind, type CounterpartySelection } from '../ui/CounterpartyPickerModal';

type BasePlannerProps = {
  initialPlan?: InstallmentPlanInput | null;
  initialTotal?: number;
  initialStartDate?: string;
  initialClientId?: number;
  initialCaseId?: number;
  initialClientName?: string;
  initialInvoiceNumber?: string;
  initialCaseName?: string;
};

type InvoicePlannerProps = BasePlannerProps & {
  mode: 'invoice';
  onApplyPlan: (plan: InstallmentPlanInput | null) => Promise<void> | void;
  onCancel: () => void;
};

type CalculatorPlannerProps = BasePlannerProps & {
  mode: 'calculator';
  clients: Client[];
  canSelectCounterparty?: boolean;
  allowedCounterpartyScopes?: CounterpartyKind[];
  canSelectCase?: boolean;
  canSavePayments?: boolean;
  onSavePayments: (args: {
    plan: InstallmentPlanInput;
    clientId?: number;
    legalEntityId?: number;
    clientCaseId?: number;
    invoiceNumber?: string | null;
    clientName?: string | null;
  }) => Promise<void>;
};

type InstallmentPlannerProps = InvoicePlannerProps | CalculatorPlannerProps;

type RequestFormState = {
  total: string;
  downPayment: string;
  annualRate: string;
  months: string;
  startDate: string;
  roundingMode: InstallmentRoundingMode;
  roundingStep: string;
};

const roundingModeOptions: Array<{ value: InstallmentRoundingMode; label: string }> = [
  { value: 'none', label: 'Без округления' },
  { value: 'down', label: 'Округлять вниз' },
  { value: 'up', label: 'Округлять вверх' },
];
const MAX_INSTALLMENT_MONTHS = 60;

function groupThousands(value: string): string {
  return value.replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
}

function normalizeMoneyInput(value: string | number | null | undefined): string {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? value.toFixed(2) : '';
  }

  const raw = String(value ?? '')
    .replace(/\s+/g, '')
    .replace(',', '.')
    .replace(/[^\d.]/g, '');

  if (!raw) return '';

  const dotIndex = raw.indexOf('.');
  if (dotIndex < 0) {
    return raw;
  }

  return `${raw.slice(0, dotIndex + 1)}${raw.slice(dotIndex + 1).replace(/\./g, '')}`;
}

function parseMoneyInput(value: string | number | null | undefined): number {
  const normalized = normalizeMoneyInput(value);
  if (!normalized || normalized === '.') return 0;

  const parsed = Number.parseFloat(normalized);
  return Number.isFinite(parsed) ? parsed : 0;
}


function formatFixedAmount(value: string | number | null | undefined): string {
  const parsed = parseMoneyInput(value);
  if (!Number.isFinite(parsed)) return '0.00';
  const [integerPart, decimalPart] = parsed.toFixed(2).split('.');
  return `${groupThousands(integerPart)}.${decimalPart}`;
}

function normalizeRateInput(value: string | number | null | undefined): string {
  const raw = String(value ?? '')
    .replace(',', '.')
    .replace(/[^\d.]/g, '');

  if (!raw) return '';

  const dotIndex = raw.indexOf('.');
  if (dotIndex < 0) {
    return raw;
  }

  return `${raw.slice(0, dotIndex + 1)}${raw.slice(dotIndex + 1).replace(/\./g, '')}`.slice(0, dotIndex + 4);
}

function formatRateInputValue(value: string | number | null | undefined): string {
  const normalized = normalizeRateInput(value);
  if (!normalized) return '';
  return normalized.endsWith('.') ? normalized : normalized;
}

function formatFixedRate(value: string | number | null | undefined): string {
  const parsed = Number.parseFloat(normalizeRateInput(value));
  return Number.isFinite(parsed) ? parsed.toFixed(2) : '';
}

function normalizeIntegerInput(value: string | number | null | undefined): string {
  return String(value ?? '').replace(/[^\d]/g, '');
}

function createFormState(props: InstallmentPlannerProps): RequestFormState {
  const today = formatLocalYMD(new Date());
  const total = props.initialPlan?.total ?? props.initialTotal ?? 0;
  const downPayment = props.initialPlan?.downPayment ?? 0;
  const annualRate = props.initialPlan?.annualRate ?? 0;
  const months = Math.min(props.initialPlan?.months ?? 6, MAX_INSTALLMENT_MONTHS);
  const startDate = toDateInputValue(props.initialPlan?.startDate ?? props.initialStartDate) || today;
  const roundingMode = props.initialPlan?.roundingMode ?? 'none';
  const roundingStep = props.initialPlan?.roundingStep ?? null;

  return {
    total: formatFixedAmount(total),
    downPayment: formatFixedAmount(downPayment),
    annualRate: annualRate ? formatFixedRate(annualRate) : '0.00',
    months: String(months),
    startDate,
    roundingMode,
    roundingStep: roundingStep && roundingStep > 0 ? formatFixedAmount(roundingStep) : '',
  };
}

function summaryAmountCardClass(kind: 'loan' | 'pay' | 'overpay' | 'monthly'): string {
  switch (kind) {
    case 'loan':
      return 'border-blue-200 bg-blue-50 text-blue-700';
    case 'pay':
      return 'border-emerald-200 bg-emerald-50 text-emerald-700';
    case 'overpay':
      return 'border-amber-200 bg-amber-50 text-amber-700';
    case 'monthly':
    default:
      return 'border-violet-200 bg-violet-50 text-violet-700';
  }
}

export function InstallmentPlanner(props: InstallmentPlannerProps) {
  const { confirm } = useDialog();
  const toast = useToast();
  const isInvoiceMode = props.mode === 'invoice';
  const canSelectCounterparty = props.mode === 'calculator' ? (props.canSelectCounterparty ?? true) : false;
  const allowedCounterpartyScopes = props.mode === 'calculator' ? (props.allowedCounterpartyScopes ?? ['client', 'legalEntity']) : [];
  const canSelectCase = props.mode === 'calculator' ? (props.canSelectCase ?? true) : false;
  const canSavePayments = props.mode === 'calculator' ? (props.canSavePayments ?? true) : true;
  const [requestForm, setRequestForm] = useState<RequestFormState>(() => createFormState(props));
  const [planDraft, setPlanDraft] = useState<InstallmentPlanInput | null>(() => cloneInstallmentPlan(props.initialPlan) ?? null);
  const [calculationDirty, setCalculationDirty] = useState(false);
  const [calculating, setCalculating] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [selectedClientId, setSelectedClientId] = useState<number | undefined>(
    props.mode === 'calculator' ? props.initialClientId : undefined,
  );
  const [selectedLegalEntityId, setSelectedLegalEntityId] = useState<number | undefined>(undefined);
  const [selectedCounterpartyName, setSelectedCounterpartyName] = useState<string | null>(
    props.mode === 'calculator' ? props.initialClientName ?? null : null,
  );
  const [counterpartyPickerOpen, setCounterpartyPickerOpen] = useState(false);
  const [selectedCaseId, setSelectedCaseId] = useState<number | undefined>(
    props.mode === 'calculator' ? props.initialCaseId : undefined,
  );

  const { cases, loading: casesLoading } = useClientCases(
    props.mode === 'calculator' && canSelectCase ? selectedClientId : undefined,
    {
      enabled: props.mode === 'calculator' && canSelectCase,
      legalEntityId: props.mode === 'calculator' && canSelectCase ? selectedLegalEntityId : undefined,
    },
  );

  const clientOptions = useMemo(
    () =>
      props.mode === 'calculator'
        ? [...props.clients].sort((left, right) => left.name.localeCompare(right.name, 'ru'))
        : [],
    [props],
  );

  const selectedClient = useMemo(
    () =>
      props.mode === 'calculator'
        ? clientOptions.find((client) => client.id === selectedClientId) ?? null
        : null,
    [clientOptions, props.mode, selectedClientId],
  );
  const selectedCaseLabel = useMemo(() => {
    if (props.mode === 'calculator') {
      if (!canSelectCase) {
        return null;
      }

      if (!selectedCaseId) {
        return props.initialCaseName ?? null;
      }

      return cases.find((item) => item.id === selectedCaseId)?.title
        ?? props.initialCaseName
        ?? `Дело #${selectedCaseId}`;
    }

    return props.initialCaseName ?? null;
  }, [canSelectCase, cases, props.initialCaseName, props.mode, selectedCaseId]);

  const currentTotal = parseMoneyInput(requestForm.total);
  const currentDownPayment = parseMoneyInput(requestForm.downPayment);
  const currentLoanAmount = Math.max(currentTotal - currentDownPayment, 0);
  const currentMonthlyPayment =
    planDraft?.roundedMonthlyPayment
    ?? planDraft?.baseMonthlyPayment
    ?? planDraft?.items[0]?.payment
    ?? 0;

  const saveDisabled =
    !planDraft
    || calculating
    || submitting
    || calculationDirty
    || (!isInvoiceMode && !canSavePayments)
    || (!isInvoiceMode && !canSelectCounterparty)
    || (!isInvoiceMode && !selectedClientId && !selectedLegalEntityId);

  const calculateButtonLabel = planDraft ? 'Пересчитать график' : 'Рассчитать график';

  const fieldLabelClass = 'mb-1.5 block text-[13px] font-semibold text-slate-700';
  const fieldControlClass =
    'h-11 rounded-xl border border-slate-300 px-3 text-sm transition focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200';
  const fieldSelectClass = `${fieldControlClass} bg-white`;
  const fieldMoneyInputClass = `${fieldControlClass} text-right tabular-nums`;
  const fieldDateClass = `pp-date-input ${fieldControlClass} text-center`;
  const summaryCards = useMemo(
    () => [
      {
        key: 'loan',
        label: 'Сумма после аванса',
        value: currentLoanAmount,
        className: summaryAmountCardClass('loan'),
      },
      {
        key: 'pay',
        label: 'К оплате',
        value: planDraft?.toPay ?? 0,
        className: summaryAmountCardClass('pay'),
      },
      {
        key: 'overpay',
        label: 'Переплата',
        value: planDraft?.overpay ?? 0,
        className: summaryAmountCardClass('overpay'),
      },
      {
        key: 'monthly',
        label: 'Ежемесячный платеж',
        value: currentMonthlyPayment,
        className: summaryAmountCardClass('monthly'),
      },
    ],
    [currentLoanAmount, currentMonthlyPayment, planDraft?.overpay, planDraft?.toPay],
  );

  const showInstallmentError = (message: string, title = 'Рассрочка') => {
    toast.error(message, title);
  };

  const sanitizeInstallmentErrorMessage = (message: string): string => {
    const firstLine = message.split(/\r?\n/, 1)[0]?.trim() || '';
    const withoutPrefix = firstLine.replace(/^System\.[^:]+:\s*/, '').trim();
    const withoutParameter = withoutPrefix.replace(/\s*\(Parameter\s+'[^']+'\)\s*$/i, '').trim();

    if (/Срок не должен превышать \d+ месяцев/i.test(withoutParameter)) {
      return `Срок рассрочки не должен превышать ${MAX_INSTALLMENT_MONTHS} месяцев.`;
    }

    return withoutParameter || message;
  };

  const handleDirtyChange = () => {
    setCalculationDirty(true);
  };

  const handleMoneyFieldChange =
    (field: 'total' | 'downPayment' | 'roundingStep') =>
    (event: React.ChangeEvent<HTMLInputElement>) => {
      if (isInvoiceMode && field === 'total') return;

      const nextValue = normalizeMoneyInput(event.target.value);
      setRequestForm((prev) => ({
        ...prev,
        [field]: nextValue,
      }));
      handleDirtyChange();
    };

  const handleMoneyFieldFocus =
    (field: 'total' | 'downPayment' | 'roundingStep') =>
    () => {
      if (isInvoiceMode && field === 'total') return;

      setRequestForm((prev) => {
        const rawValue = prev[field];
        const normalized = rawValue ? normalizeMoneyInput(rawValue) : '';

        if (normalized === rawValue) {
          return prev;
        }

        return {
          ...prev,
          [field]: normalized,
        };
      });
    };

  const handleMoneyFieldBlur =
    (field: 'total' | 'downPayment' | 'roundingStep') =>
    () => {
      if (isInvoiceMode && field === 'total') return;

      setRequestForm((prev) => {
        const rawValue = prev[field];
        return {
          ...prev,
          [field]: rawValue ? formatFixedAmount(rawValue) : '',
        };
      });
    };

  const handleRateChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    setRequestForm((prev) => ({
      ...prev,
      annualRate: formatRateInputValue(event.target.value),
    }));
    handleDirtyChange();
  };

  const handleRateBlur = () => {
    setRequestForm((prev) => ({
      ...prev,
      annualRate: prev.annualRate ? formatFixedRate(prev.annualRate) : '0.00',
    }));
  };

  const handleMonthsChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    setRequestForm((prev) => ({
      ...prev,
      months: normalizeIntegerInput(event.target.value).slice(0, 2),
    }));
    handleDirtyChange();
  };

  const handleMonthsBlur = () => {
    setRequestForm((prev) => {
      const parsed = Number.parseInt(normalizeIntegerInput(prev.months), 10);

      if (!Number.isFinite(parsed)) {
        return prev;
      }

      if (parsed > MAX_INSTALLMENT_MONTHS) {
        showInstallmentError(`Максимальный срок рассрочки — ${MAX_INSTALLMENT_MONTHS} месяцев.`, 'Ограничение срока');
        return {
          ...prev,
          months: String(MAX_INSTALLMENT_MONTHS),
        };
      }

      return {
        ...prev,
        months: String(parsed),
      };
    });
  };

  const handleDateChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    setRequestForm((prev) => ({
      ...prev,
      startDate: event.target.value,
    }));
    handleDirtyChange();
  };

  const handleRoundingModeChange = (event: React.ChangeEvent<HTMLSelectElement>) => {
    const nextMode = event.target.value as InstallmentRoundingMode;
    setRequestForm((prev) => ({
      ...prev,
      roundingMode: nextMode,
      roundingStep: nextMode === 'none' ? '' : prev.roundingStep || '100.00',
    }));
    handleDirtyChange();
  };

  const handleCounterpartySelect = (selection: CounterpartySelection | null) => {
    if (!selection) {
      setSelectedClientId(undefined);
      setSelectedLegalEntityId(undefined);
      setSelectedCounterpartyName(null);
      setSelectedCaseId(undefined);
      return;
    }

    if (selection.kind === 'client') {
      setSelectedClientId(selection.id);
      setSelectedLegalEntityId(undefined);
    } else {
      setSelectedClientId(undefined);
      setSelectedLegalEntityId(selection.id);
    }

    setSelectedCounterpartyName(selection.name);
    setSelectedCaseId(undefined);
  };

  const handleCaseChange = (event: React.ChangeEvent<HTMLSelectElement>) => {
    setSelectedCaseId(Number.parseInt(event.target.value, 10) || undefined);
  };

  const handleScheduleDateChange = (sequence: number, value: string) => {
    setPlanDraft((prev) => {
      if (!prev) return prev;

      const items = prev.items.map((item) => (
        item.sequence === sequence
          ? { ...item, date: value }
          : item
      ));

      return {
        ...prev,
        startDate: sequence === 1 ? value : prev.startDate,
        items,
      };
    });

    if (sequence === 1) {
      setRequestForm((prev) => ({
        ...prev,
        startDate: value,
      }));
    }
  };

  const buildInstallmentRequest = (): InstallmentRequest | null => {
    const total = parseMoneyInput(requestForm.total);
    const downPayment = parseMoneyInput(requestForm.downPayment);
    const annualRate = Number.parseFloat(normalizeRateInput(requestForm.annualRate)) || 0;
    const months = Number.parseInt(normalizeIntegerInput(requestForm.months), 10) || 0;
    const roundingStep = parseMoneyInput(requestForm.roundingStep);

    if (total <= 0) {
      showInstallmentError('Сумма счета должна быть больше нуля.', 'Проверьте параметры рассрочки');
      return null;
    }

    if (downPayment < 0) {
      showInstallmentError('Аванс не может быть отрицательным.', 'Проверьте параметры рассрочки');
      return null;
    }

    if (downPayment >= total) {
      showInstallmentError('Сумма после аванса должна быть больше нуля.', 'Проверьте параметры рассрочки');
      return null;
    }

    if (annualRate < 0) {
      showInstallmentError('Ставка не может быть отрицательной.', 'Проверьте параметры рассрочки');
      return null;
    }

    if (months <= 0) {
      showInstallmentError('Укажите срок рассрочки в месяцах.', 'Проверьте параметры рассрочки');
      return null;
    }

    if (months > MAX_INSTALLMENT_MONTHS) {
      showInstallmentError(`Срок рассрочки не должен превышать ${MAX_INSTALLMENT_MONTHS} месяцев.`, 'Ограничение срока');
      return null;
    }

    if (!requestForm.startDate) {
      showInstallmentError('Укажите дату первого платежа.', 'Проверьте параметры рассрочки');
      return null;
    }

    if (requestForm.roundingMode !== 'none' && roundingStep <= 0) {
      showInstallmentError('Укажите шаг округления.', 'Проверьте параметры рассрочки');
      return null;
    }

    return {
      total,
      downPayment,
      annualRate,
      months,
      startDate: requestForm.startDate,
      roundingMode: requestForm.roundingMode,
      roundingStep: requestForm.roundingMode === 'none' ? undefined : roundingStep,
    };
  };

  const handleCalculate = async () => {
    const request = buildInstallmentRequest();
    if (!request) return;

    setCalculating(true);

    try {
      const response = await apiService.calculateInstallment(request) as InstallmentResponse;
      const nextPlan = createInstallmentPlanInput(request, response);
      setPlanDraft(nextPlan);
      setCalculationDirty(false);
    } catch (error) {
      showInstallmentError(
        sanitizeInstallmentErrorMessage(
          extractDisplayErrorMessage(error, 'Не удалось рассчитать график рассрочки.'),
        ),
        'Ошибка расчета',
      );
    } finally {
      setCalculating(false);
    }
  };

  const handleSave = async () => {
    if (!planDraft) {
      showInstallmentError('Сначала рассчитайте график рассрочки.');
      return;
    }

    if (calculationDirty) {
      showInstallmentError('Параметры изменились. Пересчитайте график перед сохранением.');
      return;
    }

    if (!isInvoiceMode && !canSavePayments) {
      showInstallmentError('Сохранение платежей недоступно для вашей роли.');
      return;
    }

    if (!isInvoiceMode && !selectedClientId && !selectedLegalEntityId) {
      showInstallmentError('Выберите контрагента для сохранения графика.');
      return;
    }

    const approved = await confirm(
      isInvoiceMode
        ? {
            title: 'Сохранить график рассрочки',
            description:
              'График будет сохранен в счете. После этого сохраните сам счет, чтобы создать или обновить будущие платежи.',
            confirmLabel: 'Сохранить',
            cancelLabel: 'Отмена',
          }
        : {
            title: 'Сохранить график рассрочки',
            description: `Будет создано платежей: ${planDraft.items.length}. Продолжить?`,
            confirmLabel: 'Сохранить',
            cancelLabel: 'Отмена',
          },
    );

    if (!approved) {
      return;
    }

    setSubmitting(true);

    try {
      if (isInvoiceMode) {
        await props.onApplyPlan(cloneInstallmentPlan(planDraft));
        return;
      }

      await props.onSavePayments({
        plan: cloneInstallmentPlan(planDraft)!,
        clientId: selectedClientId,
        legalEntityId: selectedLegalEntityId,
        clientCaseId: canSelectCase ? selectedCaseId : undefined,
        invoiceNumber: props.initialInvoiceNumber ?? null,
        clientName: selectedClient?.name ?? selectedCounterpartyName ?? props.initialClientName ?? null,
      });
    } catch (error) {
      showInstallmentError(
        sanitizeInstallmentErrorMessage(
          extractDisplayErrorMessage(error, 'Не удалось сохранить график рассрочки.'),
        ),
      );
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h2 className="text-2xl font-semibold text-slate-900">
            {isInvoiceMode ? 'Настройка рассрочки' : 'Калькулятор рассрочки'}
          </h2>
          {!isInvoiceMode ? (
            <p className="mt-0.5 text-sm text-slate-500">
              {canSavePayments
                ? 'Рассчитайте график и создайте будущие платежи по рассрочке.'
                : 'Рассчитайте график и проверьте будущие платежи по рассрочке.'}
            </p>
          ) : null}
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {(props.initialClientName || selectedClient?.name) ? (
            <span className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600">
              <Wallet className="h-3.5 w-3.5" />
              {selectedClient?.name ?? props.initialClientName}
            </span>
          ) : null}
          {props.initialInvoiceNumber ? (
            <span className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600">
              <ReceiptText className="h-3.5 w-3.5" />
              Счет №{props.initialInvoiceNumber}
            </span>
          ) : null}
          {selectedCaseLabel ? (
            <span className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600">
              <CreditCard className="h-3.5 w-3.5" />
              {selectedCaseLabel}
            </span>
          ) : null}
        </div>
      </div>

      <div className="grid gap-5 xl:grid-cols-[240px_minmax(0,1fr)]">
        <section className="rounded-3xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="flex items-center gap-2 border-b border-slate-200 pb-3">
            <CalendarDays className="h-5 w-5 text-blue-600" />
            <span className="text-sm font-semibold uppercase tracking-wide text-slate-500">
              Параметры рассрочки
            </span>
          </div>

          <div className="mt-4 space-y-3.5">
            {props.mode === 'calculator' ? (
              <>
                <div className="flex flex-col text-sm">
                  <span className={fieldLabelClass}>Контрагент</span>
                  <div className="flex gap-2">
                    <input
                      type="text"
                      value={selectedCounterpartyName ?? selectedClient?.name ?? ''}
                      readOnly
                      placeholder="Выберите контрагента"
                      className={`${fieldControlClass} min-w-0 flex-1 bg-slate-50`}
                    />
                    <button
                      type="button"
                      onClick={() => setCounterpartyPickerOpen(true)}
                      disabled={!canSelectCounterparty}
                      className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-xl border border-slate-300 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-400"
                      aria-label="Выбрать контрагента"
                    >
                      <UserRoundSearch className="h-4 w-4" />
                    </button>
                  </div>
                </div>
                {canSelectCase ? (
                  <label className="flex flex-col text-sm">
                    <span className={fieldLabelClass}>Дело</span>
                    <SystemSelect
                      value={selectedCaseId ? String(selectedCaseId) : ''}
                      onChange={handleCaseChange}
                      disabled={(!selectedClientId && !selectedLegalEntityId) || casesLoading}
                      className={`${fieldSelectClass} disabled:bg-slate-100 disabled:text-slate-400`}
                    >
                      <option value="">{casesLoading ? 'Загрузка дел...' : 'Без дела'}</option>
                      {cases.map((clientCase) => (
                        <option key={clientCase.id} value={clientCase.id}>
                          {clientCase.title}
                        </option>
                      ))}
                    </SystemSelect>
                  </label>
                ) : null}
              </>
            ) : null}

            <div className="grid grid-cols-1 gap-3.5">
              <label className="flex flex-col text-sm">
                <span className={fieldLabelClass}>Сумма счета</span>
                <input
                  type="text"
                  inputMode="decimal"
                  value={requestForm.total}
                  onChange={handleMoneyFieldChange('total')}
                  onFocus={handleMoneyFieldFocus('total')}
                  onBlur={handleMoneyFieldBlur('total')}
                  readOnly={isInvoiceMode}
                  className={`${fieldMoneyInputClass} read-only:bg-slate-50 read-only:text-slate-600`}
                  placeholder="0.00"
                />
              </label>

              <label className="flex flex-col text-sm">
                <span className={fieldLabelClass}>Аванс</span>
                <input
                  type="text"
                  inputMode="decimal"
                  value={requestForm.downPayment}
                  onChange={handleMoneyFieldChange('downPayment')}
                  onFocus={handleMoneyFieldFocus('downPayment')}
                  onBlur={handleMoneyFieldBlur('downPayment')}
                  className={fieldMoneyInputClass}
                  placeholder="0.00"
                />
              </label>
            </div>

            <div className="grid grid-cols-1 gap-3.5">
              <label className="flex flex-col text-sm">
                <span className={fieldLabelClass}>Годовая ставка, %</span>
                <input
                  type="text"
                  inputMode="decimal"
                  value={requestForm.annualRate}
                  onChange={handleRateChange}
                  onBlur={handleRateBlur}
                  className={fieldMoneyInputClass}
                  placeholder="0.00"
                />
              </label>

              <label className="flex flex-col text-sm">
                <span className={fieldLabelClass}>Срок, месяцев</span>
                <input
                  type="text"
                  inputMode="numeric"
                  value={requestForm.months}
                  onChange={handleMonthsChange}
                  onBlur={handleMonthsBlur}
                  maxLength={2}
                  className={`${fieldControlClass} text-right tabular-nums`}
                  placeholder="6"
                />
              </label>
            </div>

            <label className="flex flex-col text-sm">
              <span className={fieldLabelClass}>Дата первого платежа</span>
              <SystemDateInput
                type="date"
                value={requestForm.startDate}
                onChange={handleDateChange}
                className={fieldDateClass}
              />
            </label>

            <div className="grid grid-cols-1 gap-3.5">
              <label className="flex flex-col text-sm">
                <span className={fieldLabelClass}>Округление</span>
                <SystemSelect
                  value={requestForm.roundingMode}
                  onChange={handleRoundingModeChange}
                  className={fieldSelectClass}
                >
                  {roundingModeOptions.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </SystemSelect>
              </label>

              <label className="flex flex-col text-sm">
                <span className={fieldLabelClass}>Шаг округления</span>
                <input
                  type="text"
                  inputMode="decimal"
                  value={requestForm.roundingStep}
                  onChange={handleMoneyFieldChange('roundingStep')}
                  onFocus={handleMoneyFieldFocus('roundingStep')}
                  onBlur={handleMoneyFieldBlur('roundingStep')}
                  disabled={requestForm.roundingMode === 'none'}
                  className={`${fieldMoneyInputClass} disabled:bg-slate-100 disabled:text-slate-400`}
                  placeholder="100.00"
                />
              </label>
            </div>

            <button
              type="button"
              onClick={() => void handleCalculate()}
              disabled={calculating || currentTotal <= 0}
              className="inline-flex h-11 w-full items-center justify-center gap-2 rounded-2xl bg-blue-600 px-4 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {calculating ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              {calculateButtonLabel}
            </button>
          </div>
        </section>

        <section className="rounded-3xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
            {summaryCards.map((card) => (
              <div
                key={card.key}
                className={`rounded-2xl border px-3 py-3 text-center shadow-sm ${card.className}`}
              >
                <div className="text-[11px] font-semibold uppercase tracking-[0.14em]">
                  {card.label}
                </div>
                <div className="mt-1.5 text-lg font-semibold tabular-nums">
                  {formatFixedAmount(card.value)}
                </div>
              </div>
            ))}
          </div>

          <div className="mt-4 rounded-2xl border border-slate-200">
            <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 px-4 py-3">
              <div>
                <h3 className="text-base font-semibold text-slate-900">График платежей</h3>
                <p className="mt-0.5 text-sm text-slate-500">
                  {planDraft
                    ? `Платежей в графике: ${planDraft.items.length}`
                    : 'Рассчитайте график, чтобы увидеть будущие платежи.'}
                </p>
              </div>

              {calculationDirty && planDraft ? (
                <div className="max-w-sm rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-right text-xs font-medium text-amber-700">
                  Параметры изменились. Пересчитайте график.
                </div>
              ) : null}
            </div>

            {!planDraft ? (
              <div className="px-4 py-12 text-center text-sm text-slate-500">
                Заполните параметры слева и нажмите «Рассчитать график».
              </div>
            ) : (
              <div className="max-h-[52vh] overflow-auto">
                <table className="w-full min-w-[760px] table-fixed">
                  <thead className="sticky top-0 z-10 bg-slate-50">
                    <tr className="text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                      <th className="w-14 px-3 py-2.5 text-left">№</th>
                      <th className="w-[220px] px-3 py-2.5 text-center">Дата</th>
                      <th className="px-3 py-2.5 text-right">Основной долг</th>
                      <th className="px-3 py-2.5 text-right">Проценты</th>
                      <th className="px-3 py-2.5 text-right">Платеж</th>
                      <th className="px-3 py-2.5 text-right">Остаток</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-200">
                    {planDraft.items.map((item) => (
                      <tr key={item.sequence} className="bg-white">
                        <td className="px-3 py-2.5 text-sm font-medium text-slate-700">
                          {item.sequence}
                        </td>
                        <td className="w-[220px] px-3 py-2.5 text-center">
                          <SystemDateInput
                            type="date"
                            value={toDateInputValue(item.date)}
                            onChange={(event) => handleScheduleDateChange(item.sequence, event.target.value)}
                            className="pp-date-input mx-auto h-10 w-[152px] rounded-xl border border-slate-300 px-3 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                          />
                        </td>
                        <td className="px-3 py-2.5 text-right text-sm tabular-nums text-slate-700">
                          {formatFixedAmount(item.principal)}
                        </td>
                        <td className="px-3 py-2.5 text-right text-sm tabular-nums text-slate-700">
                          {formatFixedAmount(item.interest)}
                        </td>
                        <td className="px-3 py-2.5 text-right text-sm font-semibold tabular-nums text-slate-900">
                          {formatFixedAmount(item.payment)}
                        </td>
                        <td className="px-3 py-2.5 text-right text-sm tabular-nums text-slate-500">
                          {formatFixedAmount(item.balance)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </section>
      </div>

      <div className="flex flex-wrap items-center justify-end gap-3">
        {isInvoiceMode || canSavePayments ? (
          <button
            type="button"
            onClick={() => void handleSave()}
            disabled={saveDisabled}
            className="inline-flex items-center justify-center gap-2 rounded-2xl bg-blue-600 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
            Сохранить
          </button>
        ) : null}

        {isInvoiceMode ? (
          <button
            type="button"
            onClick={props.onCancel}
            disabled={submitting}
            className="rounded-2xl border border-slate-200 px-5 py-2.5 text-sm font-medium text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            Отмена
          </button>
        ) : null}
      </div>
      {props.mode === 'calculator' && canSelectCounterparty ? (
        <CounterpartyPickerModal
          open={counterpartyPickerOpen}
          title="Выбор контрагента"
          initialScope={selectedLegalEntityId ? 'legalEntity' : 'client'}
          allowedScopes={allowedCounterpartyScopes}
          onClose={() => setCounterpartyPickerOpen(false)}
          onSelect={handleCounterpartySelect}
        />
      ) : null}
    </div>
  );
}
