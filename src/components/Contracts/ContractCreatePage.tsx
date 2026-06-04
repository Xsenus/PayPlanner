import { ArrowLeft, Loader2, Plus, Save, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from '../../hooks/useTranslation';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { apiService, type ContractRulesSettingsDto } from '../../services/api';
import type { ContractBillingMode, ContractClient, ContractInput, ContractLegalEntity, ContractStatus } from '../../types';
import { fromInputToApiDate, todayYMDInTimeZone } from '../../utils/dateUtils';
import { TablePagination } from '../ui/TablePagination';
import { CounterpartyPickerModal, type CounterpartySelection } from '../ui/CounterpartyPickerModal';

interface ContractCreatePageProps {
  onBack: () => void;
  onCreated: () => void;
}

type FormState = {
  number: string;
  title: string;
  date: string;
  startDate: string;
  billingMode: ContractBillingMode;
  status: ContractStatus;
  isOpenEnded: boolean;
  amount: string;
  description: string;
  validUntil: string;
  terminationDate: string;
};

const createInitialForm = (timeZoneId?: string | null): FormState => {
  const today = todayYMDInTimeZone(timeZoneId);
  return {
    number: '',
    title: '',
    date: today,
    startDate: today,
    billingMode: 'Fixed',
    status: 'Draft',
    isOpenEnded: false,
    amount: '',
    description: '',
    validUntil: '',
    terminationDate: '',
  };
};

const CLIENT_PAGE_SIZE_OPTIONS = [10, 25, 50];

type ContractCounterparty =
  | ({ kind: 'client' } & ContractClient)
  | ({ kind: 'legalEntity' } & ContractLegalEntity);

function contractCounterpartyKey(item: Pick<ContractCounterparty, 'kind' | 'id'>): string {
  return `${item.kind}:${item.id}`;
}

function legalEntityName(entity: Pick<ContractLegalEntity, 'shortName' | 'fullName' | 'id'>): string {
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юрлицо #${entity.id}`;
}

export function ContractCreatePage({ onBack, onCreated }: ContractCreatePageProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const profileTimeZoneId = useUserTimeZone();
  const defaultDate = todayYMDInTimeZone(profileTimeZoneId);
  const defaultDateRef = useRef(defaultDate);
  const [form, setForm] = useState<FormState>(() => createInitialForm(profileTimeZoneId));
  const [localError, setLocalError] = useState<string | null>(null);
  const [numberManuallyEdited, setNumberManuallyEdited] = useState(false);
  const numberManuallyEditedRef = useRef(false);
  const [contractRules, setContractRules] = useState<ContractRulesSettingsDto>({
    autoGenerateWhenEmpty: true,
    allowManualNumberEdit: true,
  });
  const [selectedCounterparties, setSelectedCounterparties] = useState<ContractCounterparty[]>([]);
  const [selectedPage, setSelectedPage] = useState(1);
  const [selectedPageSize, setSelectedPageSize] = useState(CLIENT_PAGE_SIZE_OPTIONS[0]);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    numberManuallyEditedRef.current = numberManuallyEdited;
  }, [numberManuallyEdited]);

  useEffect(() => {
    setForm((current) => ({
      ...current,
      date: current.date === defaultDateRef.current ? defaultDate : current.date,
      startDate: current.startDate === defaultDateRef.current ? defaultDate : current.startDate,
    }));
    defaultDateRef.current = defaultDate;
  }, [defaultDate]);

  useEffect(() => {
    if (!form.date || numberManuallyEdited) return;

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
  }, [form.date, numberManuallyEdited]);

  const selectedTotalPages = useMemo(
    () => Math.max(1, Math.ceil(selectedCounterparties.length / selectedPageSize)),
    [selectedCounterparties.length, selectedPageSize],
  );
  const pagedSelectedCounterparties = useMemo(() => {
    const offset = (selectedPage - 1) * selectedPageSize;
    return selectedCounterparties.slice(offset, offset + selectedPageSize);
  }, [selectedCounterparties, selectedPage, selectedPageSize]);
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

  useEffect(() => {
    setSelectedPage((prev) => Math.min(prev, selectedTotalPages));
  }, [selectedTotalPages]);

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

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    if (!form.number.trim() && !contractRules.autoGenerateWhenEmpty) {
      setLocalError('Номер договора обязателен');
      return;
    }
    if (!form.date) {
      setLocalError('Дата договора обязательна');
      return;
    }
    if (selectedCounterparties.length === 0) {
      setLocalError('Выберите хотя бы одного контрагента');
      return;
    }

    let amountValue: number | undefined;
    if (form.billingMode === 'Fixed' && form.amount.trim()) {
      const parsed = Number(form.amount.replace(/\s+/g, '').replace(',', '.'));
      if (Number.isNaN(parsed) || parsed < 0) {
        setLocalError('Введите корректную сумму');
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
      title: form.title.trim() || undefined,
      date: fromInputToApiDate(form.date) ?? form.date,
      startDate: form.startDate ? fromInputToApiDate(form.startDate) : undefined,
      billingMode: form.billingMode,
      status: form.status,
      isOpenEnded: form.isOpenEnded,
      amount: form.billingMode === 'Fixed' ? amountValue : undefined,
      description: form.description.trim() || undefined,
      validUntil: !form.isOpenEnded && form.validUntil ? fromInputToApiDate(form.validUntil) : undefined,
      terminationDate: form.terminationDate ? fromInputToApiDate(form.terminationDate) : undefined,
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

    setSubmitting(true);
    try {
      await apiService.createContract(payload);
      toast.success('Договор создан');
      onCreated();
    } catch (error) {
      const message = error instanceof Error ? error.message : (t('failedToSave') ?? '');
      setLocalError(message);
      toast.error(message, 'Ошибка сохранения');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="min-h-screen bg-gray-50 pb-12">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 pt-8 sm:px-6 lg:px-8">
        <div className="mb-6 flex items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-bold text-slate-900">Новый договор</h1>
            <p className="text-sm text-slate-600">Укажите ключевую информацию и добавьте клиентов.</p>
          </div>
          <button
            type="button"
            onClick={onBack}
            className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
          >
            <ArrowLeft className="h-4 w-4" />
            Назад
          </button>
        </div>

        <form onSubmit={handleSubmit} className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Номер договора</span>
              <input
                type="text"
                value={form.number}
                onChange={(e) => {
                  if (!contractRules.allowManualNumberEdit) {
                    return;
                  }

                  numberManuallyEditedRef.current = true;
                  setNumberManuallyEdited(true);
                  setForm((prev) => ({ ...prev, number: e.target.value }));
                }}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm"
                disabled={!contractRules.allowManualNumberEdit}
                required={!contractRules.autoGenerateWhenEmpty}
              />
              {!contractRules.allowManualNumberEdit && (
                <span className="text-xs text-slate-500">Номер договора формируется автоматически по настройкам.</span>
              )}
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Дата договора</span>
              <input type="date" value={form.date} onChange={(e) => setForm((prev) => ({ ...prev, date: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" required />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Дата начала действия</span>
              <input type="date" value={form.startDate} onChange={(e) => setForm((prev) => ({ ...prev, startDate: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Режим договора</span>
              <select
                value={form.billingMode}
                onChange={(e) =>
                  setForm((prev) => ({
                    ...prev,
                    billingMode: e.target.value as ContractBillingMode,
                    amount: e.target.value === 'Actual' ? '' : prev.amount,
                  }))
                }
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm"
              >
                <option value="Fixed">Фиксированная сумма</option>
                <option value="Actual">Оплата по факту</option>
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Статус документа</span>
              <select
                value={form.status}
                onChange={(e) => setForm((prev) => ({ ...prev, status: e.target.value as ContractStatus }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm"
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
              <span className="font-medium text-slate-700">Действует до</span>
              <input type="date" value={form.validUntil} onChange={(e) => setForm((prev) => ({ ...prev, validUntil: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" disabled={form.isOpenEnded} />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Дата расторжения</span>
              <input type="date" value={form.terminationDate} onChange={(e) => setForm((prev) => ({ ...prev, terminationDate: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Сумма договора</span>
              <input type="text" value={form.amount} onChange={(e) => setForm((prev) => ({ ...prev, amount: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" placeholder="0,00" disabled={form.billingMode === 'Actual'} />
              {form.billingMode === 'Actual' && (
                <span className="text-xs text-slate-500">Сумма будет считаться по сумме привязанных счетов.</span>
              )}
            </label>
            <label className="flex items-center gap-3 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 sm:col-span-2">
              <input
                type="checkbox"
                checked={form.isOpenEnded}
                onChange={(e) =>
                  setForm((prev) => ({
                    ...prev,
                    isOpenEnded: e.target.checked,
                    validUntil: e.target.checked ? '' : prev.validUntil,
                  }))
                }
                className="h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500"
              />
              <span>До исполнения обязательств</span>
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">Название договора</span>
              <input type="text" value={form.title} onChange={(e) => setForm((prev) => ({ ...prev, title: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">Описание</span>
              <textarea value={form.description} onChange={(e) => setForm((prev) => ({ ...prev, description: e.target.value }))} className="min-h-[96px] rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
          </div>

          <div className="mt-5 rounded-xl border border-slate-200">
            <div className="flex items-center justify-between border-b px-4 py-3">
              <h3 className="font-semibold text-slate-900">Контрагенты договора</h3>
              <button
                type="button"
                onClick={() => setPickerOpen(true)}
                className="inline-flex items-center gap-2 rounded-lg border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700"
              >
                <Plus className="h-4 w-4" />
                Добавить контрагента
              </button>
            </div>
            <div className="overflow-x-auto">
              <table className="min-w-full table-fixed divide-y divide-slate-200 text-sm">
                <colgroup>
                  <col style={{ width: '28%' }} />
                  <col style={{ width: '14%' }} />
                  <col style={{ width: '24%' }} />
                  <col style={{ width: '24%' }} />
                  <col style={{ width: '16%' }} />
                </colgroup>
                <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                  <tr>
                    <th className="px-3 py-2 text-left">Контрагент</th>
                    <th className="px-3 py-2 text-left">Тип</th>
                    <th className="px-3 py-2 text-left">Реквизиты</th>
                    <th className="px-3 py-2 text-left">Комментарий</th>
                    <th className="px-3 py-2 text-right">Действия</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {selectedCounterparties.length === 0 ? (
                    <tr>
                      <td colSpan={5} className="px-3 py-6 text-center text-slate-500">
                        Контрагенты не добавлены
                      </td>
                    </tr>
                  ) : (
                    pagedSelectedCounterparties.map((counterparty) => (
                      <tr key={contractCounterpartyKey(counterparty)}>
                        <td className="px-3 py-2">
                          {counterparty.kind === 'client' ? counterparty.name : legalEntityName(counterparty)}
                        </td>
                        <td className="px-3 py-2">{counterparty.kind === 'client' ? 'Клиент' : 'Юрлицо'}</td>
                        <td className="px-3 py-2">
                          {counterparty.kind === 'client'
                            ? counterparty.company || '—'
                            : [counterparty.inn ? `ИНН ${counterparty.inn}` : '', counterparty.kpp ? `КПП ${counterparty.kpp}` : ''].filter(Boolean).join(', ') || counterparty.fullName || '—'}
                        </td>
                        <td className="px-3 py-2">
                          <input
                            value={counterparty.comment ?? ''}
                            onChange={(e) =>
                              setSelectedCounterparties((prev) =>
                                prev.map((x) => (contractCounterpartyKey(x) === contractCounterpartyKey(counterparty) ? { ...x, comment: e.target.value } : x)),
                              )
                            }
                            className="w-full rounded border border-slate-200 px-2 py-1"
                            placeholder="Комментарий к контрагенту"
                          />
                        </td>
                        <td className="px-3 py-2 text-right">
                          <button
                            type="button"
                            onClick={() => setSelectedCounterparties((prev) => prev.filter((x) => contractCounterpartyKey(x) !== contractCounterpartyKey(counterparty)))}
                            className="rounded border border-rose-200 p-1 text-rose-600"
                          >
                            <X className="h-4 w-4" />
                          </button>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
            <TablePagination
              page={selectedPage}
              totalPages={selectedTotalPages}
              totalItems={selectedCounterparties.length}
              pageSize={selectedPageSize}
              pageSizeOptions={CLIENT_PAGE_SIZE_OPTIONS}
              onPageChange={setSelectedPage}
              onPageSizeChange={(value) => {
                setSelectedPageSize(value);
                setSelectedPage(1);
              }}
            />
          </div>

          {localError && <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{localError}</div>}

          <div className="mt-6 flex items-center justify-end gap-3">
            <button type="button" onClick={onBack} className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700">
              Отмена
            </button>
            <button type="submit" disabled={submitting} className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white disabled:opacity-70">
              {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              <span>Сохранить</span>
            </button>
          </div>
        </form>
      </div>

      <CounterpartyPickerModal
        open={pickerOpen}
        mode="multiple"
        title="Выбор контрагентов"
        selected={selectedPickerValue}
        onApply={handleApplyCounterparties}
        onClose={() => setPickerOpen(false)}
      />
    </div>
  );
}
