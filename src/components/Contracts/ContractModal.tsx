import { useEffect, useMemo, useRef, useState } from 'react';
import { Loader2, Plus, X } from 'lucide-react';
import { apiService, type ContractRulesSettingsDto } from '../../services/api';
import type { Contract, ContractBillingMode, ContractClient, ContractInput, ContractLegalEntity, ContractStatus } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { CounterpartyPickerModal, type CounterpartySelection } from '../ui/CounterpartyPickerModal';

interface ContractModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  contract: Contract | null;
  onClose: () => void;
  onSubmit: (payload: ContractInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  defaultClients?: ContractClient[];
  overlayClassName?: string;
}

type FormState = {
  number: string;
  title: string;
  date: string;
  billingMode: ContractBillingMode;
  status: ContractStatus;
  isOpenEnded: boolean;
  amount: string;
  description: string;
  validUntil: string;
};

type ContractCounterparty =
  | ({ kind: 'client' } & ContractClient)
  | ({ kind: 'legalEntity' } & ContractLegalEntity);

function contractCounterpartyKey(item: Pick<ContractCounterparty, 'kind' | 'id'>): string {
  return `${item.kind}:${item.id}`;
}

function legalEntityName(entity: Pick<ContractLegalEntity, 'shortName' | 'fullName' | 'id'>): string {
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юрлицо #${entity.id}`;
}

function buildInitialCounterparties(contract: Contract | null, defaultClients?: ContractClient[]): ContractCounterparty[] {
  const clients = (contract?.clients ?? defaultClients ?? []).map((client) => ({
    ...client,
    kind: 'client' as const,
  }));
  const legalEntities = (contract?.legalEntities ?? []).map((entity) => ({
    ...entity,
    kind: 'legalEntity' as const,
  }));
  return [...clients, ...legalEntities];
}

function normalizeContractToForm(contract: Contract | null): FormState {
  if (!contract) {
    const today = toDateInputValue(new Date());
    return {
      number: '',
      title: '',
      date: today,
      billingMode: 'Fixed',
      status: 'Draft',
      isOpenEnded: false,
      amount: '',
      description: '',
      validUntil: '',
    };
  }

  return {
    number: contract.number ?? '',
    title: contract.title ?? '',
    date: toDateInputValue(contract.date) || toDateInputValue(new Date()),
    billingMode: contract.billingMode ?? 'Fixed',
    status: contract.status ?? 'Draft',
    isOpenEnded: contract.isOpenEnded ?? false,
    amount:
      contract.billingMode !== 'Actual' && contract.amount !== undefined && contract.amount !== null
        ? String(contract.amount)
        : '',
    description: contract.description ?? '',
    validUntil: contract.isOpenEnded ? '' : (toDateInputValue(contract.validUntil) ?? ''),
  };
}

export function ContractModal({
  open,
  mode,
  contract,
  onClose,
  onSubmit,
  submitting,
  errorMessage,
  defaultClients,
  overlayClassName,
}: ContractModalProps) {
  const { t } = useTranslation();
  const isEdit = mode === 'edit';
  const [form, setForm] = useState<FormState>(() => normalizeContractToForm(contract));
  const [localError, setLocalError] = useState<string | null>(null);
  const [numberManuallyEdited, setNumberManuallyEdited] = useState(isEdit);
  const numberManuallyEditedRef = useRef(isEdit);
  const [contractRules, setContractRules] = useState<ContractRulesSettingsDto>({
    autoGenerateWhenEmpty: true,
    allowManualNumberEdit: true,
  });
  const [selectedCounterparties, setSelectedCounterparties] = useState<ContractCounterparty[]>(
    () => buildInitialCounterparties(contract, defaultClients),
  );
  const [pickerOpen, setPickerOpen] = useState(false);

  useEffect(() => {
    if (open) {
      setForm(normalizeContractToForm(contract));
      setLocalError(null);
      setSelectedCounterparties(buildInitialCounterparties(contract, defaultClients));
      setPickerOpen(false);
      setNumberManuallyEdited(isEdit);
      numberManuallyEditedRef.current = isEdit;
    }
  }, [open, contract, defaultClients, isEdit]);

  useEffect(() => {
    numberManuallyEditedRef.current = numberManuallyEdited;
  }, [numberManuallyEdited]);

  useEffect(() => {
    if (!open || isEdit || !form.date || numberManuallyEdited) return;

    let cancelled = false;
    const requestDate = form.date;

    const loadNextNumber = async () => {
      try {
        const nextNumber = await apiService.getNextContractNumber(fromInputToApiDate(requestDate) ?? requestDate);
        if (cancelled) return;

        setContractRules({
          autoGenerateWhenEmpty: nextNumber.autoGenerateWhenEmpty,
          allowManualNumberEdit: nextNumber.allowManualNumberEdit,
        });

        setForm((prev) => {
          const shouldAutofill =
            nextNumber.autoGenerateWhenEmpty
            && (!numberManuallyEditedRef.current || !nextNumber.allowManualNumberEdit);

          if (prev.date !== requestDate || !shouldAutofill) {
            return prev;
          }

          return { ...prev, number: nextNumber.number };
        });
      } catch {
        if (!cancelled) {
          setContractRules({
            autoGenerateWhenEmpty: true,
            allowManualNumberEdit: true,
          });
        }
      }
    };

    void loadNextNumber();

    return () => {
      cancelled = true;
    };
  }, [form.date, isEdit, numberManuallyEdited, open]);

  useEffect(() => {
    if (!open) return;

    let cancelled = false;
    const loadRules = async () => {
      try {
        const response = await apiService.getContractRulesSettings();
        if (cancelled) return;
        setContractRules(response);
      } catch {
        if (!cancelled) {
          setContractRules({
            autoGenerateWhenEmpty: true,
            allowManualNumberEdit: true,
          });
        }
      }
    };

    void loadRules();

    return () => {
      cancelled = true;
    };
  }, [open]);

  const selectedPickerValue = useMemo<CounterpartySelection[]>(
    () => selectedCounterparties.map((item) => ({
      kind: item.kind,
      id: item.id,
      name: item.kind === 'client' ? item.name : legalEntityName(item),
      subtitle: item.kind === 'client'
        ? item.company ?? null
        : [item.inn ? `ИНН ${item.inn}` : '', item.kpp ? `КПП ${item.kpp}` : ''].filter(Boolean).join(' · ') || item.fullName || null,
    })),
    [selectedCounterparties],
  );

  const handleApplyCounterparties = (selection: CounterpartySelection[]) => {
    const previousComments = new Map(selectedCounterparties.map((item) => [contractCounterpartyKey(item), item.comment ?? '']));
    setSelectedCounterparties(selection.map((item): ContractCounterparty => {
      const comment = previousComments.get(`${item.kind}:${item.id}`) ?? '';
      if (item.kind === 'legalEntity') {
        const entity = item.legalEntity;
        return {
          kind: 'legalEntity',
          id: item.id,
          shortName: entity?.shortName ?? item.name,
          fullName: entity?.fullName ?? item.subtitle ?? null,
          inn: entity?.inn ?? null,
          kpp: entity?.kpp ?? null,
          comment,
        };
      }

      const client = item.client;
      return {
        kind: 'client',
        id: item.id,
        name: client?.name ?? item.name,
        company: client?.company ?? item.subtitle ?? null,
        clientStatusId: client?.clientStatusId ?? null,
        clientStatus: client?.clientStatus ?? null,
        comment,
      };
    }));
  };

  const handleRemoveCounterparty = (target: ContractCounterparty) => {
    setSelectedCounterparties((prev) => prev.filter((item) => contractCounterpartyKey(item) !== contractCounterpartyKey(target)));
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    if (!form.number.trim() && !contractRules.autoGenerateWhenEmpty) {
      setLocalError(t('contractNumberRequired') ?? 'Номер договора обязателен');
      return;
    }

    if (!form.date) {
      setLocalError(t('contractDateRequired') ?? 'Дата договора обязательна');
      return;
    }

    if (selectedCounterparties.length === 0) {
      setLocalError(t('contractClientsRequired') ?? 'Выберите хотя бы одного контрагента');
      return;
    }

    let amountValue: number | undefined;
    if (form.billingMode === 'Fixed' && form.amount.trim()) {
      const normalized = form.amount.replace(/\s+/g, '').replace(',', '.');
      const parsed = Number(normalized);
      if (Number.isNaN(parsed)) {
        setLocalError(t('contractAmountInvalid') ?? 'Введите корректную сумму');
        return;
      }
      if (parsed < 0) {
        setLocalError(t('contractAmountInvalid') ?? 'Введите корректную сумму');
        return;
      }
      amountValue = parsed;
    }

    if (form.billingMode === 'Fixed' && amountValue === undefined) {
      setLocalError('Укажите сумму договора');
      return;
    }

    const payload: ContractInput = {
      number: form.number.trim(),
      title: form.title.trim() ? form.title.trim() : undefined,
      date: fromInputToApiDate(form.date) ?? form.date,
      billingMode: form.billingMode,
      status: form.status,
      isOpenEnded: form.isOpenEnded,
      description: form.description.trim() ? form.description.trim() : undefined,
      amount: form.billingMode === 'Fixed' ? amountValue : undefined,
      validUntil: !form.isOpenEnded && form.validUntil ? fromInputToApiDate(form.validUntil) ?? form.validUntil : undefined,
      clients: selectedCounterparties
        .filter((item): item is Extract<ContractCounterparty, { kind: 'client' }> => item.kind === 'client')
        .map((client) => ({ clientId: client.id, comment: client.comment ?? undefined })),
      legalEntities: selectedCounterparties
        .filter((item): item is Extract<ContractCounterparty, { kind: 'legalEntity' }> => item.kind === 'legalEntity')
        .map((entity) => ({ legalEntityId: entity.id, comment: entity.comment ?? undefined })),
      actIds: [],
      invoiceIds: [],
      paymentIds: [],
    };

    try {
      await onSubmit(payload);
    } catch (err) {
      if (err instanceof Error && !errorMessage) {
        setLocalError(err.message);
      }
    }
  };

  if (!open) return null;

  return (
    <div className={`fixed inset-0 flex items-center justify-center bg-slate-900/60 px-4 py-8 ${overlayClassName ?? 'z-50'}`}>
      <div className="relative w-full max-w-3xl overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
          <div>
            <h2 className="text-xl font-semibold text-slate-900">
              {isEdit ? t('contractEditTitle') ?? 'Редактировать договор' : t('contractCreateTitle') ?? 'Новый договор'}
            </h2>
            <p className="text-sm text-slate-500">
              {t('contractModalSubtitle') ?? 'Укажите ключевую информацию и выберите клиентов.'}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
            aria-label={t('close') ?? 'Закрыть'}
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="max-h-[75vh] overflow-y-auto px-6 py-5">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractNumber') ?? 'Номер договора'}</span>
              <input
                type="text"
                value={form.number}
                onChange={(event) => {
                  if (!contractRules.allowManualNumberEdit) {
                    return;
                  }

                  if (!isEdit) {
                    numberManuallyEditedRef.current = true;
                    setNumberManuallyEdited(true);
                  }

                  setForm((prev) => ({ ...prev, number: event.target.value }));
                }}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                disabled={!contractRules.allowManualNumberEdit}
                required={!contractRules.autoGenerateWhenEmpty}
              />
              {!contractRules.allowManualNumberEdit && (
                <span className="text-xs text-slate-500">Номер договора формируется автоматически по настройкам.</span>
              )}
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractDate') ?? 'Дата договора'}</span>
              <input
                type="date"
                value={form.date}
                onChange={(event) => setForm((prev) => ({ ...prev, date: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                required
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractTitle') ?? 'Название'}</span>
              <input
                type="text"
                value={form.title}
                onChange={(event) => setForm((prev) => ({ ...prev, title: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Режим договора</span>
              <select
                value={form.billingMode}
                onChange={(event) =>
                  setForm((prev) => ({
                    ...prev,
                    billingMode: event.target.value as ContractBillingMode,
                    amount: event.target.value === 'Actual' ? '' : prev.amount,
                  }))
                }
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              >
                <option value="Fixed">Фиксированная сумма</option>
                <option value="Actual">Оплата по факту</option>
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Статус документа</span>
              <select
                value={form.status}
                onChange={(event) => setForm((prev) => ({ ...prev, status: event.target.value as ContractStatus }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              >
                <option value="Draft">Черновик</option>
                <option value="NotSigned">Не подписан</option>
                <option value="RequiresClientSignature">Требуется подпись клиента</option>
                <option value="OnApproval">На согласовании</option>
                <option value="Approved">Согласован</option>
                <option value="ApprovalRejected">Отказ в согласовании</option>
                <option value="NeedsCorrection">Требует исправления</option>
                <option value="OnSigning">На подписании</option>
                <option value="Concluded">Подписан</option>
                <option value="Terminated">Расторгнут</option>
                <option value="Cancelled">Аннулирован</option>
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractAmount') ?? 'Сумма договора'}</span>
              <input
                type="text"
                value={form.amount}
                onChange={(event) => setForm((prev) => ({ ...prev, amount: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                placeholder="0,00"
                disabled={form.billingMode === 'Actual'}
              />
              {form.billingMode === 'Actual' && (
                <span className="text-xs text-slate-500">Сумма будет считаться по привязанным счетам.</span>
              )}
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">{t('contractDescription') ?? 'Описание'}</span>
              <textarea
                value={form.description}
                onChange={(event) => setForm((prev) => ({ ...prev, description: event.target.value }))}
                className="min-h-[96px] rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                placeholder={t('contractDescriptionPlaceholder') ?? 'Условия, комментарии и другая важная информация'}
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractValidUntil') ?? 'Действует до'}</span>
              <input
                type="date"
                value={form.validUntil}
                onChange={(event) => setForm((prev) => ({ ...prev, validUntil: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                disabled={form.isOpenEnded}
              />
            </label>
            <label className="flex items-center gap-3 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
              <input
                type="checkbox"
                checked={form.isOpenEnded}
                onChange={(event) =>
                  setForm((prev) => ({
                    ...prev,
                    isOpenEnded: event.target.checked,
                    validUntil: event.target.checked ? '' : prev.validUntil,
                  }))
                }
                className="h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500"
              />
              <span>До исполнения обязательств</span>
            </label>
        <div className="flex flex-col gap-2 text-sm sm:col-span-2">
          <div className="flex items-center justify-between gap-3">
            <span className="font-medium text-slate-700">{t('contractClients') ?? 'Контрагенты'}</span>
            <button
              type="button"
              onClick={() => setPickerOpen(true)}
              className="inline-flex items-center gap-2 rounded-lg border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm font-medium text-emerald-700 transition hover:bg-emerald-100"
            >
              <Plus className="h-4 w-4" />
              Добавить
            </button>
          </div>
          {selectedCounterparties.length === 0 ? (
            <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50 px-3 py-5 text-center text-sm text-slate-500">
              Контрагенты не добавлены
            </div>
          ) : (
            <div className="flex flex-wrap gap-2">
              {selectedCounterparties.map((counterparty) => (
                <span
                  key={contractCounterpartyKey(counterparty)}
                  className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-xs font-medium text-emerald-700"
                >
                  <span className="flex flex-col items-start gap-1 sm:flex-row sm:items-center">
                    <span>
                      {counterparty.kind === 'client' ? counterparty.name : legalEntityName(counterparty)}
                      {' · '}
                      {counterparty.kind === 'client' ? 'Клиент' : 'Юрлицо'}
                    </span>
                    {counterparty.kind === 'client' ? <ClientStatusBadge status={counterparty.clientStatus} /> : null}
                  </span>
                  <button
                    type="button"
                    onClick={() => handleRemoveCounterparty(counterparty)}
                    className="rounded-full p-0.5 text-emerald-600 transition hover:bg-emerald-100 hover:text-emerald-800"
                    aria-label={t('remove') ?? 'Удалить'}
                  >
                    <X className="h-3 w-3" />
                  </button>
                </span>
              ))}
            </div>
          )}
        </div>
          </div>

          {(localError || errorMessage) && (
            <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              {localError || errorMessage}
            </div>
          )}

          <div className="mt-6 flex flex-col-reverse gap-3 sm:flex-row sm:items-center sm:justify-between">
            <button
              type="button"
              onClick={onClose}
              className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
              disabled={submitting}
            >
              {t('cancel') ?? 'Отмена'}
            </button>
            <button
              type="submit"
              disabled={submitting}
              className="inline-flex items-center justify-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-emerald-500 disabled:opacity-70"
            >
              {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
              <span>{isEdit ? t('update') ?? 'Обновить' : t('create') ?? 'Создать'}</span>
            </button>
          </div>
        </form>
      </div>
      <CounterpartyPickerModal
        open={pickerOpen}
        mode="multiple"
        title="Выбор контрагентов"
        selected={selectedPickerValue}
        overlayClassName="z-[1600]"
        onApply={handleApplyCounterparties}
        onClose={() => setPickerOpen(false)}
      />
    </div>
  );
}
