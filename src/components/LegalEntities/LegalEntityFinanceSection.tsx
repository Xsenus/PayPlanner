import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import {
  CreditCard,
  Landmark,
  Pencil,
  Plus,
  RefreshCw,
  Trash2,
  Wallet,
  X,
} from 'lucide-react';
import { createPortal } from 'react-dom';
import { useCallback, useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react';

import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService, type BankDto } from '../../services/api';
import type {
  LegalEntityBankAccount,
  LegalEntityCard,
  LegalEntityCashRegister,
} from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

import {
  isActivePrimaryBankAccount,
  setBankAccountArchived,
  upsertBankAccount,
} from './legalEntityBankAccountUtils';

type FinanceTab = 'bankAccounts' | 'cashRegisters' | 'cards';

interface LegalEntityFinanceSectionProps {
  readOnly: boolean;
  compactReadOnly?: boolean;
  bankAccounts: LegalEntityBankAccount[];
  cashRegisters: LegalEntityCashRegister[];
  cards: LegalEntityCard[];
  onBankAccountsChange: Dispatch<SetStateAction<LegalEntityBankAccount[]>>;
  onCashRegistersChange: Dispatch<SetStateAction<LegalEntityCashRegister[]>>;
  onCardsChange: Dispatch<SetStateAction<LegalEntityCard[]>>;
}

interface BankAccountFormState {
  bankId: string;
  bank: string;
  name: string;
  bik: string;
  currency: string;
  accountNumber: string;
  accountType: string;
  corrAccountNumber: string;
  city: string;
  isForeignBank: boolean;
}

interface CashRegisterFormState {
  name: string;
  currency: string;
  access: string;
  accountType: string;
  isPrimary: boolean;
  isClosed: boolean;
  accountingEnabled: boolean;
}

interface CardFormState {
  bankId: string;
  bank: string;
  name: string;
  number: string;
  currency: string;
  holder: string;
  bik: string;
  corrAccountNumber: string;
  city: string;
  access: string;
  accountingEnabled: boolean;
}

type FinanceEditorState =
  | { kind: 'bankAccount'; index: number | null; form: BankAccountFormState }
  | { kind: 'cashRegister'; index: number | null; form: CashRegisterFormState }
  | { kind: 'card'; index: number | null; form: CardFormState }
  | null;

const CURRENCY_OPTIONS = ['RUB', 'USD', 'EUR', 'CNY', 'KZT'] as const;
const ACCOUNT_TYPE_OPTIONS = ['Основной', 'Дополнительный'] as const;

const INPUT_CLASS =
  'w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100';
const SELECT_CLASS = INPUT_CLASS;
const CHECKBOX_CLASS = 'h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500';

function normalizeOptional(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function renderCurrencyOption(currentValue: string) {
  if (!currentValue || CURRENCY_OPTIONS.includes(currentValue as (typeof CURRENCY_OPTIONS)[number])) {
    return null;
  }

  return <option value={currentValue}>{currentValue}</option>;
}

function formatOptional(value?: string | null) {
  return value && value.trim().length > 0 ? value : '—';
}

function formatBool(value?: boolean | null, trueLabel = 'Да', falseLabel = 'Нет') {
  return value ? trueLabel : falseLabel;
}

function getBankById(banks: BankDto[], bankId: string) {
  const parsed = Number(bankId);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return null;
  }

  return banks.find((item) => item.id === parsed) ?? null;
}

function createEmptyBankAccountForm(preferPrimary: boolean): BankAccountFormState {
  return {
    bankId: '',
    bank: '',
    name: '',
    bik: '',
    currency: 'RUB',
    accountNumber: '',
    accountType: preferPrimary ? 'Основной' : 'Дополнительный',
    corrAccountNumber: '',
    city: '',
    isForeignBank: false,
  };
}

function createEmptyCashRegisterForm(preferPrimary: boolean): CashRegisterFormState {
  return {
    name: '',
    currency: 'RUB',
    access: '',
    accountType: preferPrimary ? 'Основной' : 'Дополнительный',
    isPrimary: preferPrimary,
    isClosed: false,
    accountingEnabled: true,
  };
}

function createEmptyCardForm(): CardFormState {
  return {
    bankId: '',
    bank: '',
    name: '',
    number: '',
    currency: 'RUB',
    holder: '',
    bik: '',
    corrAccountNumber: '',
    city: '',
    access: '',
    accountingEnabled: false,
  };
}

function mapBankAccountToForm(value: LegalEntityBankAccount): BankAccountFormState {
  return {
    bankId: value.bankId ? String(value.bankId) : '',
    bank: value.bank ?? '',
    name: value.name ?? '',
    bik: value.bik ?? '',
    currency: value.currency ?? 'RUB',
    accountNumber: value.accountNumber ?? '',
    accountType: value.accountType ?? 'Дополнительный',
    corrAccountNumber: value.corrAccountNumber ?? '',
    city: value.city ?? '',
    isForeignBank: Boolean(value.isForeignBank),
  };
}

function mapCashRegisterToForm(value: LegalEntityCashRegister): CashRegisterFormState {
  const accountType = value.accountType ?? (value.isPrimary ? 'Основной' : 'Дополнительный');

  return {
    name: value.name ?? '',
    currency: value.currency ?? 'RUB',
    access: value.access ?? '',
    accountType,
    isPrimary: Boolean(value.isPrimary) || accountType === 'Основной',
    isClosed: Boolean(value.isClosed),
    accountingEnabled: value.accountingEnabled ?? true,
  };
}

function mapCardToForm(value: LegalEntityCard): CardFormState {
  return {
    bankId: value.bankId ? String(value.bankId) : '',
    bank: value.bank ?? '',
    name: value.name ?? '',
    number: value.number ?? '',
    currency: value.currency ?? 'RUB',
    holder: value.holder ?? '',
    bik: value.bik ?? '',
    corrAccountNumber: value.corrAccountNumber ?? '',
    city: value.city ?? '',
    access: value.access ?? '',
    accountingEnabled: Boolean(value.accountingEnabled),
  };
}

function applyBankToBankAccountForm(form: BankAccountFormState, bank: BankDto | null): BankAccountFormState {
  if (!bank) {
    return { ...form, bankId: '' };
  }

  return {
    ...form,
    bankId: String(bank.id),
    bank: bank.name,
    bik: bank.bik ?? '',
    corrAccountNumber: bank.correspondentAccount ?? '',
    city: bank.city ?? '',
    isForeignBank: bank.isForeign,
    name: form.name.trim() || bank.name,
  };
}

function applyBankToCardForm(form: CardFormState, bank: BankDto | null): CardFormState {
  if (!bank) {
    return { ...form, bankId: '' };
  }

  return {
    ...form,
    bankId: String(bank.id),
    bank: bank.name,
    bik: bank.bik ?? '',
    corrAccountNumber: bank.correspondentAccount ?? '',
    city: bank.city ?? '',
    name: form.name.trim() || `Карта ${bank.name}`,
  };
}

function mapFormToBankAccount(form: BankAccountFormState): LegalEntityBankAccount {
  return {
    bankId: form.bankId ? Number(form.bankId) : null,
    bank: form.bank.trim(),
    name: form.name.trim() || form.bank.trim(),
    bik: form.bik.trim(),
    currency: form.currency.trim() || 'RUB',
    accountNumber: form.accountNumber.trim(),
    accountType: form.accountType.trim() || 'Дополнительный',
    corrAccountNumber: normalizeOptional(form.corrAccountNumber),
    city: normalizeOptional(form.city),
    isForeignBank: form.isForeignBank,
  };
}

function mapFormToCashRegister(form: CashRegisterFormState): LegalEntityCashRegister {
  const accountType = form.accountType.trim() || (form.isPrimary ? 'Основной' : 'Дополнительный');

  return {
    name: form.name.trim(),
    currency: form.currency.trim() || 'RUB',
    access: normalizeOptional(form.access),
    accountType,
    isPrimary: form.isPrimary || accountType === 'Основной',
    isClosed: form.isClosed,
    accountingEnabled: form.accountingEnabled,
  };
}

function mapFormToCard(form: CardFormState): LegalEntityCard {
  return {
    bankId: form.bankId ? Number(form.bankId) : null,
    bank: form.bank.trim(),
    number: form.number.trim(),
    currency: form.currency.trim() || 'RUB',
    holder: form.holder.trim(),
    name: normalizeOptional(form.name),
    bik: normalizeOptional(form.bik),
    corrAccountNumber: normalizeOptional(form.corrAccountNumber),
    city: normalizeOptional(form.city),
    access: normalizeOptional(form.access),
    accountingEnabled: form.accountingEnabled,
  };
}

export function LegalEntityFinanceSection({
  readOnly,
  compactReadOnly = false,
  bankAccounts,
  cashRegisters,
  cards,
  onBankAccountsChange,
  onCashRegistersChange,
  onCardsChange,
}: LegalEntityFinanceSectionProps) {
  const toast = useToast();
  const { confirm } = useDialog();
  const [activeTab, setActiveTab] = useState<FinanceTab>('bankAccounts');
  const [banks, setBanks] = useState<BankDto[]>([]);
  const [banksLoading, setBanksLoading] = useState(false);
  const [banksError, setBanksError] = useState<string | null>(null);
  const [editorState, setEditorState] = useState<FinanceEditorState>(null);
  const [editorError, setEditorError] = useState<string | null>(null);

  const hasPrimaryBankAccount = useMemo(
    () => bankAccounts.some(isActivePrimaryBankAccount),
    [bankAccounts],
  );
  const hasPrimaryCashRegister = useMemo(
    () => cashRegisters.some((item) => item.isPrimary || item.accountType?.trim().toLowerCase() === 'основной'),
    [cashRegisters],
  );

  const loadBanks = useCallback(async () => {
    try {
      setBanksLoading(true);
      setBanksError(null);
      const response = await apiService.getBanks();
      setBanks(response ?? []);
    } catch (error) {
      setBanksError(extractDisplayErrorMessage(error, 'Не удалось загрузить справочник банков.'));
    } finally {
      setBanksLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadBanks();

    const handleBanksChanged = () => {
      void loadBanks();
    };

    window.addEventListener('banks:changed', handleBanksChanged);
    return () => window.removeEventListener('banks:changed', handleBanksChanged);
  }, [loadBanks]);

  const bankAccountForm = editorState?.kind === 'bankAccount' ? editorState.form : null;
  const cashRegisterForm = editorState?.kind === 'cashRegister' ? editorState.form : null;
  const cardForm = editorState?.kind === 'card' ? editorState.form : null;

  const openCreateBankAccount = () => {
    setEditorError(null);
    setEditorState({
      kind: 'bankAccount',
      index: null,
      form: createEmptyBankAccountForm(!hasPrimaryBankAccount),
    });
  };

  const openEditBankAccount = (index: number) => {
    setEditorError(null);
    setEditorState({
      kind: 'bankAccount',
      index,
      form: mapBankAccountToForm(bankAccounts[index]),
    });
  };

  const openCreateCashRegister = () => {
    setEditorError(null);
    setEditorState({
      kind: 'cashRegister',
      index: null,
      form: createEmptyCashRegisterForm(!hasPrimaryCashRegister),
    });
  };

  const openEditCashRegister = (index: number) => {
    setEditorError(null);
    setEditorState({
      kind: 'cashRegister',
      index,
      form: mapCashRegisterToForm(cashRegisters[index]),
    });
  };

  const openCreateCard = () => {
    setEditorError(null);
    setEditorState({
      kind: 'card',
      index: null,
      form: createEmptyCardForm(),
    });
  };

  const openEditCard = (index: number) => {
    setEditorError(null);
    setEditorState({
      kind: 'card',
      index,
      form: mapCardToForm(cards[index]),
    });
  };

  const closeEditor = () => {
    setEditorState(null);
    setEditorError(null);
  };

  const updateBankAccountForm = (patch: Partial<BankAccountFormState>) => {
    setEditorState((prev) => (prev?.kind === 'bankAccount' ? { ...prev, form: { ...prev.form, ...patch } } : prev));
  };

  const updateCashRegisterForm = (patch: Partial<CashRegisterFormState>) => {
    setEditorState((prev) => (prev?.kind === 'cashRegister' ? { ...prev, form: { ...prev.form, ...patch } } : prev));
  };

  const updateCardForm = (patch: Partial<CardFormState>) => {
    setEditorState((prev) => (prev?.kind === 'card' ? { ...prev, form: { ...prev.form, ...patch } } : prev));
  };

  const saveBankAccount = () => {
    if (!bankAccountForm || editorState?.kind !== 'bankAccount') {
      return;
    }

    if (!bankAccountForm.bank.trim()) {
      setEditorError('Укажи банк или выбери его из справочника.');
      return;
    }

    if (!bankAccountForm.accountNumber.trim()) {
      setEditorError('Укажи расчетный счет.');
      return;
    }

    const normalized = mapFormToBankAccount(bankAccountForm);
    onBankAccountsChange((prev) => upsertBankAccount(prev, editorState.index, normalized));

    closeEditor();
  };

  const saveCashRegister = () => {
    if (!cashRegisterForm || editorState?.kind !== 'cashRegister') {
      return;
    }

    if (!cashRegisterForm.name.trim()) {
      setEditorError('Укажи название кассы.');
      return;
    }

    const normalized = mapFormToCashRegister(cashRegisterForm);
    const shouldBePrimary = normalized.isPrimary || normalized.accountType?.trim().toLowerCase() === 'основной';

    onCashRegistersChange((prev) => {
      const targetIndex = editorState.index;
      const next =
        targetIndex === null
          ? [...prev, normalized]
          : prev.map((item, index) => (index === targetIndex ? normalized : item));

      if (!shouldBePrimary) {
        return next;
      }

      const actualIndex = targetIndex === null ? next.length - 1 : targetIndex;
      return next.map((item, index) =>
        index === actualIndex
          ? {
              ...item,
              isPrimary: true,
              accountType: 'Основной',
            }
          : {
              ...item,
              isPrimary: false,
              accountType: item.accountType?.trim().toLowerCase() === 'основной' ? 'Дополнительный' : item.accountType,
            },
      );
    });

    closeEditor();
  };

  const saveCard = () => {
    if (!cardForm || editorState?.kind !== 'card') {
      return;
    }

    if (!cardForm.number.trim()) {
      setEditorError('Укажи номер карты или счета.');
      return;
    }

    const normalized = mapFormToCard(cardForm);

    onCardsChange((prev) => {
      const targetIndex = editorState.index;
      return targetIndex === null
        ? [...prev, normalized]
        : prev.map((item, index) => (index === targetIndex ? normalized : item));
    });

    closeEditor();
  };

  const toggleBankAccountArchived = (index: number) => {
    const archived = !bankAccounts[index]?.isArchived;
    onBankAccountsChange((prev) => setBankAccountArchived(prev, index, archived));
    toast.success(
      archived ? 'Банковский счет перенесен в архив.' : 'Банковский счет возвращен из архива.',
      'Реквизиты',
    );
  };

  const deleteBankAccount = async (index: number) => {
    const approved = await confirm({
      title: 'Удалить банковский счет',
      description: 'Запись будет удалена из формы после подтверждения и сохранится вместе с карточкой юрлица.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) {
      return;
    }

    onBankAccountsChange((prev) => prev.filter((_, itemIndex) => itemIndex !== index));
    toast.success('Банковский счет убран из формы.', 'Реквизиты');
  };

  const deleteCashRegister = async (index: number) => {
    const approved = await confirm({
      title: 'Удалить кассу',
      description: 'Запись будет удалена из формы после подтверждения и сохранится вместе с карточкой юрлица.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) {
      return;
    }

    onCashRegistersChange((prev) => prev.filter((_, itemIndex) => itemIndex !== index));
    toast.success('Касса убрана из формы.', 'Реквизиты');
  };

  const deleteCard = async (index: number) => {
    const approved = await confirm({
      title: 'Удалить карту',
      description: 'Запись будет удалена из формы после подтверждения и сохранится вместе с карточкой юрлица.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) {
      return;
    }

    onCardsChange((prev) => prev.filter((_, itemIndex) => itemIndex !== index));
    toast.success('Карта убрана из формы.', 'Реквизиты');
  };

  const bankCount = bankAccounts.length;
  const cashCount = cashRegisters.length;
  const cardCount = cards.length;

  const activeIcon =
    activeTab === 'bankAccounts' ? (
      <Landmark className="h-5 w-5 text-blue-600" />
    ) : activeTab === 'cashRegisters' ? (
      <Wallet className="h-5 w-5 text-emerald-600" />
    ) : (
      <CreditCard className="h-5 w-5 text-violet-600" />
    );

  const openActiveCreateDialog = () => {
    if (activeTab === 'bankAccounts') {
      openCreateBankAccount();
      return;
    }

    if (activeTab === 'cashRegisters') {
      openCreateCashRegister();
      return;
    }

    openCreateCard();
  };

  const renderTabButton = (tab: FinanceTab, label: string, count: number) => (
    <button
      key={tab}
      type="button"
      onClick={() => setActiveTab(tab)}
      className={`inline-flex items-center gap-2 rounded-xl border px-4 py-2.5 text-sm font-semibold transition ${
        activeTab === tab
          ? 'border-blue-200 bg-blue-50 text-blue-900'
          : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
      }`}
    >
      <span>{label}</span>
      <span className="rounded-full bg-white/90 px-2 py-0.5 text-xs text-slate-500">{count}</span>
    </button>
  );

  const renderBankDirectoryHint = () => {
    if (compactReadOnly) {
      return null;
    }

    if (activeTab !== 'bankAccounts' && activeTab !== 'cards') {
      return null;
    }

    return (
      <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <div className="font-semibold text-slate-800">Справочник банков</div>
            <div className="mt-1">
              {banksLoading
                ? 'Загружаю список банков...'
                : `Доступно банков в справочнике: ${banks.length}`}
            </div>
            {banksError ? <div className="mt-1 text-rose-600">{banksError}</div> : null}
          </div>
          <button
            type="button"
            onClick={() => {
              void loadBanks();
            }}
            className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-100"
          >
            <RefreshCw className={`h-4 w-4 ${banksLoading ? 'animate-spin' : ''}`} />
            Обновить
          </button>
        </div>
      </div>
    );
  };

  const renderBankAccountsTab = () => (
    <div className="overflow-hidden rounded-2xl border border-slate-200">
      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-slate-200 text-sm">
          <thead className="bg-slate-50 text-left text-slate-600">
            <tr>
              <th className="px-4 py-3 font-semibold">Банк</th>
              <th className="px-4 py-3 font-semibold">Название</th>
              <th className="px-4 py-3 font-semibold">БИК</th>
              <th className="px-4 py-3 font-semibold">Валюта</th>
              <th className="px-4 py-3 font-semibold">Р/с</th>
              <th className="px-4 py-3 font-semibold">Тип</th>
              {!compactReadOnly ? (
                <th className="px-4 py-3 font-semibold text-right">Действия</th>
              ) : null}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 bg-white">
            {bankAccounts.length === 0 ? (
              <tr>
                <td colSpan={compactReadOnly ? 6 : 7} className="px-4 py-12 text-center text-sm text-slate-500">
                  Банковские счета пока не добавлены.
                </td>
              </tr>
            ) : (
              bankAccounts.map((item, index) => (
                <tr key={item.id ?? `bank-account-${index}`} className={item.isArchived ? 'bg-slate-50 text-slate-400' : undefined}>
                  <td className="px-4 py-3 font-medium text-slate-900">
                    {item.bank}
                    {item.isArchived ? (
                      <span className="ml-2 rounded-full bg-slate-200 px-2 py-0.5 text-xs font-normal text-slate-600">Архив</span>
                    ) : null}
                  </td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.name)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.bik)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.currency)}</td>
                  <td className="px-4 py-3 font-mono text-slate-700">{formatOptional(item.accountNumber)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.accountType)}</td>
                  {!compactReadOnly ? (
                    <td className="px-4 py-3">
                      <div className="flex items-center justify-end gap-2">
                        <button
                          type="button"
                          disabled={readOnly}
                          onClick={() => openEditBankAccount(index)}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                          title="Редактировать счет"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          disabled={readOnly}
                          onClick={() => toggleBankAccountArchived(index)}
                          className="inline-flex h-9 items-center justify-center rounded-xl border border-slate-200 px-3 text-xs text-slate-500 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                          title={item.isArchived ? 'Вернуть счет из архива' : 'Перенести счет в архив'}
                        >
                          {item.isArchived ? 'Из архива' : 'В архив'}
                        </button>
                        <button
                          type="button"
                          disabled={readOnly}
                          onClick={() => {
                            void deleteBankAccount(index);
                          }}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-rose-200 text-rose-500 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-40"
                          title="Удалить счет"
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  ) : null}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );

  const renderCashRegistersTab = () => (
    <div className="overflow-hidden rounded-2xl border border-slate-200">
      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-slate-200 text-sm">
          <thead className="bg-slate-50 text-left text-slate-600">
            <tr>
              <th className="px-4 py-3 font-semibold">Название</th>
              <th className="px-4 py-3 font-semibold">Валюта</th>
              <th className="px-4 py-3 font-semibold">Доступ</th>
              <th className="px-4 py-3 font-semibold">Для учета</th>
              <th className="px-4 py-3 font-semibold">Тип</th>
              {!compactReadOnly ? (
                <th className="px-4 py-3 font-semibold text-right">Действия</th>
              ) : null}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 bg-white">
            {cashRegisters.length === 0 ? (
              <tr>
                <td colSpan={compactReadOnly ? 5 : 6} className="px-4 py-12 text-center text-sm text-slate-500">
                  Кассы пока не добавлены.
                </td>
              </tr>
            ) : (
              cashRegisters.map((item, index) => (
                <tr key={`cash-register-${index}`}>
                  <td className="px-4 py-3 font-medium text-slate-900">{item.name}</td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.currency)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.access)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatBool(item.accountingEnabled, 'Да', 'Нет')}</td>
                  <td className="px-4 py-3 text-slate-700">
                    {item.isPrimary || item.accountType?.trim().toLowerCase() === 'основной'
                      ? 'Основная'
                      : formatOptional(item.accountType)}
                  </td>
                  {!compactReadOnly ? (
                    <td className="px-4 py-3">
                      <div className="flex items-center justify-end gap-2">
                        <button
                          type="button"
                          disabled={readOnly}
                          onClick={() => openEditCashRegister(index)}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                          title="Редактировать кассу"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          disabled={readOnly}
                          onClick={() => {
                            void deleteCashRegister(index);
                          }}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-rose-200 text-rose-500 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-40"
                          title="Удалить кассу"
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  ) : null}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );

  const renderCardsTab = () => (
    <div className="overflow-hidden rounded-2xl border border-slate-200">
      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-slate-200 text-sm">
          <thead className="bg-slate-50 text-left text-slate-600">
            <tr>
              <th className="px-4 py-3 font-semibold">Название</th>
              <th className="px-4 py-3 font-semibold">Банк</th>
              <th className="px-4 py-3 font-semibold">Валюта</th>
              <th className="px-4 py-3 font-semibold">Номер карты или счета</th>
              <th className="px-4 py-3 font-semibold">Держатель</th>
              <th className="px-4 py-3 font-semibold">Для учета</th>
              {!compactReadOnly ? (
                <th className="px-4 py-3 font-semibold text-right">Действия</th>
              ) : null}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 bg-white">
            {cards.length === 0 ? (
              <tr>
                <td colSpan={compactReadOnly ? 6 : 7} className="px-4 py-12 text-center text-sm text-slate-500">
                  Карты пока не добавлены.
                </td>
              </tr>
            ) : (
              cards.map((item, index) => (
                <tr key={`card-${index}`}>
                  <td className="px-4 py-3 font-medium text-slate-900">{formatOptional(item.name)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.bank)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.currency)}</td>
                  <td className="px-4 py-3 font-mono text-slate-700">{formatOptional(item.number)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatOptional(item.holder)}</td>
                  <td className="px-4 py-3 text-slate-700">{formatBool(item.accountingEnabled, 'Да', 'Нет')}</td>
                  {!compactReadOnly ? (
                    <td className="px-4 py-3">
                      <div className="flex items-center justify-end gap-2">
                        <button
                          type="button"
                          disabled={readOnly}
                          onClick={() => openEditCard(index)}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                          title="Редактировать карту"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          disabled={readOnly}
                          onClick={() => {
                            void deleteCard(index);
                          }}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-rose-200 text-rose-500 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-40"
                          title="Удалить карту"
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  ) : null}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );

  const renderBankAccountEditor = () => {
    if (!bankAccountForm) {
      return null;
    }

    return (
      <div className="grid gap-4 md:grid-cols-2">
        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Банк из справочника</span>
          <SystemSelect
            value={bankAccountForm.bankId}
            onChange={(event) => {
              const bank = getBankById(banks, event.target.value);
              updateBankAccountForm(applyBankToBankAccountForm(bankAccountForm, bank));
            }}
            className={SELECT_CLASS}
          >
            <option value="">Не выбрано</option>
            {banks.map((bank) => (
              <option key={bank.id} value={bank.id}>
                {bank.name}
                {bank.bik ? ` · ${bank.bik}` : ''}
              </option>
            ))}
          </SystemSelect>
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Название, которое будет отображаться</span>
          <input
            type="text"
            value={bankAccountForm.name}
            onChange={(event) => updateBankAccountForm({ name: event.target.value })}
            className={INPUT_CLASS}
            placeholder="Основной счет"
          />
        </label>
        <label className="block md:col-span-2">
          <span className="mb-2 block text-sm font-medium text-slate-700">Банк</span>
          <input
            type="text"
            value={bankAccountForm.bank}
            onChange={(event) => updateBankAccountForm({ bank: event.target.value })}
            className={INPUT_CLASS}
            placeholder="ООО «Банк Точка»"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Расчетный счет</span>
          <input
            type="text"
            value={bankAccountForm.accountNumber}
            onChange={(event) => updateBankAccountForm({ accountNumber: event.target.value })}
            className={INPUT_CLASS}
            placeholder="40802810..."
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Валюта</span>
          <SystemSelect
            value={bankAccountForm.currency}
            onChange={(event) => updateBankAccountForm({ currency: event.target.value })}
            className={SELECT_CLASS}
          >
            {renderCurrencyOption(bankAccountForm.currency)}
            {CURRENCY_OPTIONS.map((currency) => (
              <option key={currency} value={currency}>
                {currency}
              </option>
            ))}
          </SystemSelect>
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">БИК</span>
          <input
            type="text"
            value={bankAccountForm.bik}
            onChange={(event) => updateBankAccountForm({ bik: event.target.value })}
            className={INPUT_CLASS}
            placeholder="044525104"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Тип счета</span>
          <SystemSelect
            value={bankAccountForm.accountType}
            onChange={(event) => updateBankAccountForm({ accountType: event.target.value })}
            className={SELECT_CLASS}
          >
            {ACCOUNT_TYPE_OPTIONS.map((item) => (
              <option key={item} value={item}>
                {item}
              </option>
            ))}
          </SystemSelect>
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Корреспондентский счет</span>
          <input
            type="text"
            value={bankAccountForm.corrAccountNumber}
            onChange={(event) => updateBankAccountForm({ corrAccountNumber: event.target.value })}
            className={INPUT_CLASS}
            placeholder="30101810..."
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Город банка</span>
          <input
            type="text"
            value={bankAccountForm.city}
            onChange={(event) => updateBankAccountForm({ city: event.target.value })}
            className={INPUT_CLASS}
            placeholder="Москва"
          />
        </label>

        <label className="mt-2 inline-flex items-center gap-3 text-sm font-medium text-slate-700">
          <SystemCheckbox
            type="checkbox"
            checked={bankAccountForm.isForeignBank}
            onChange={(event) => updateBankAccountForm({ isForeignBank: event.target.checked })}
            className={CHECKBOX_CLASS}
          />
          Иностранный банк
        </label>
      </div>
    );
  };

  const renderCashRegisterEditor = () => {
    if (!cashRegisterForm) {
      return null;
    }

    return (
      <div className="grid gap-4 md:grid-cols-2">
        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Название, которое будет отображаться</span>
          <input
            type="text"
            value={cashRegisterForm.name}
            onChange={(event) => updateCashRegisterForm({ name: event.target.value })}
            className={INPUT_CLASS}
            placeholder="Касса офиса"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Валюта</span>
          <SystemSelect
            value={cashRegisterForm.currency}
            onChange={(event) => updateCashRegisterForm({ currency: event.target.value })}
            className={SELECT_CLASS}
          >
            {renderCurrencyOption(cashRegisterForm.currency)}
            {CURRENCY_OPTIONS.map((currency) => (
              <option key={currency} value={currency}>
                {currency}
              </option>
            ))}
          </SystemSelect>
        </label>

        <label className="block md:col-span-2">
          <span className="mb-2 block text-sm font-medium text-slate-700">Доступ</span>
          <input
            type="text"
            value={cashRegisterForm.access}
            onChange={(event) => updateCashRegisterForm({ access: event.target.value })}
            className={INPUT_CLASS}
            placeholder="Например: руководитель, бухгалтер"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Тип</span>
          <SystemSelect
            value={cashRegisterForm.accountType}
            onChange={(event) =>
              updateCashRegisterForm({
                accountType: event.target.value,
                isPrimary: event.target.value === 'Основной' ? true : cashRegisterForm.isPrimary,
              })
            }
            className={SELECT_CLASS}
          >
            {ACCOUNT_TYPE_OPTIONS.map((item) => (
              <option key={item} value={item}>
                {item}
              </option>
            ))}
          </SystemSelect>
        </label>

        <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
          <div className="grid gap-3 sm:grid-cols-3">
            <label className="inline-flex items-center gap-3 text-sm font-medium text-slate-700">
              <SystemCheckbox
                type="checkbox"
                checked={cashRegisterForm.isPrimary}
                onChange={(event) =>
                  updateCashRegisterForm({
                    isPrimary: event.target.checked,
                    accountType: event.target.checked ? 'Основной' : cashRegisterForm.accountType,
                  })
                }
                className={CHECKBOX_CLASS}
              />
              Основная
            </label>
            <label className="inline-flex items-center gap-3 text-sm font-medium text-slate-700">
              <SystemCheckbox
                type="checkbox"
                checked={cashRegisterForm.isClosed}
                onChange={(event) => updateCashRegisterForm({ isClosed: event.target.checked })}
                className={CHECKBOX_CLASS}
              />
              Касса закрыта
            </label>
            <label className="inline-flex items-center gap-3 text-sm font-medium text-slate-700">
              <SystemCheckbox
                type="checkbox"
                checked={cashRegisterForm.accountingEnabled}
                onChange={(event) => updateCashRegisterForm({ accountingEnabled: event.target.checked })}
                className={CHECKBOX_CLASS}
              />
              Для учета в бухгалтерии
            </label>
          </div>
        </div>
      </div>
    );
  };

  const renderCardEditor = () => {
    if (!cardForm) {
      return null;
    }

    return (
      <div className="grid gap-4 md:grid-cols-2">
        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Банк из справочника</span>
          <SystemSelect
            value={cardForm.bankId}
            onChange={(event) => {
              const bank = getBankById(banks, event.target.value);
              updateCardForm(applyBankToCardForm(cardForm, bank));
            }}
            className={SELECT_CLASS}
          >
            <option value="">Не выбрано</option>
            {banks.map((bank) => (
              <option key={bank.id} value={bank.id}>
                {bank.name}
                {bank.bik ? ` · ${bank.bik}` : ''}
              </option>
            ))}
          </SystemSelect>
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Банк</span>
          <input
            type="text"
            value={cardForm.bank}
            onChange={(event) => updateCardForm({ bank: event.target.value })}
            className={INPUT_CLASS}
            placeholder="Название банка"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Номер карты или счета</span>
          <input
            type="text"
            value={cardForm.number}
            onChange={(event) => updateCardForm({ number: event.target.value })}
            className={INPUT_CLASS}
            placeholder="**** 1234 или 4080..."
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Валюта</span>
          <SystemSelect
            value={cardForm.currency}
            onChange={(event) => updateCardForm({ currency: event.target.value })}
            className={SELECT_CLASS}
          >
            {renderCurrencyOption(cardForm.currency)}
            {CURRENCY_OPTIONS.map((currency) => (
              <option key={currency} value={currency}>
                {currency}
              </option>
            ))}
          </SystemSelect>
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Название, которое будет отображаться</span>
          <input
            type="text"
            value={cardForm.name}
            onChange={(event) => updateCardForm({ name: event.target.value })}
            className={INPUT_CLASS}
            placeholder="Корпоративная карта"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Аккаунт / держатель</span>
          <input
            type="text"
            value={cardForm.holder}
            onChange={(event) => updateCardForm({ holder: event.target.value })}
            className={INPUT_CLASS}
            placeholder="ФИО или подразделение"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">БИК</span>
          <input
            type="text"
            value={cardForm.bik}
            onChange={(event) => updateCardForm({ bik: event.target.value })}
            className={INPUT_CLASS}
            placeholder="044525104"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Корреспондентский счет</span>
          <input
            type="text"
            value={cardForm.corrAccountNumber}
            onChange={(event) => updateCardForm({ corrAccountNumber: event.target.value })}
            className={INPUT_CLASS}
            placeholder="30101810..."
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Город банка</span>
          <input
            type="text"
            value={cardForm.city}
            onChange={(event) => updateCardForm({ city: event.target.value })}
            className={INPUT_CLASS}
            placeholder="Москва"
          />
        </label>

        <label className="block">
          <span className="mb-2 block text-sm font-medium text-slate-700">Доступ</span>
          <input
            type="text"
            value={cardForm.access}
            onChange={(event) => updateCardForm({ access: event.target.value })}
            className={INPUT_CLASS}
            placeholder="Кто может использовать"
          />
        </label>

        <label className="mt-2 inline-flex items-center gap-3 text-sm font-medium text-slate-700">
          <SystemCheckbox
            type="checkbox"
            checked={cardForm.accountingEnabled}
            onChange={(event) => updateCardForm({ accountingEnabled: event.target.checked })}
            className={CHECKBOX_CLASS}
          />
          Для учета в бухгалтерии
        </label>
      </div>
    );
  };

  const renderEditor = () => {
    if (!editorState || typeof document === 'undefined') {
      return null;
    }

    return createPortal(
      <div className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-900/45 p-4">
        <div className="w-full max-w-5xl rounded-3xl bg-white shadow-2xl">
          <div className="flex items-start justify-between border-b border-slate-200 px-6 py-5">
            <div>
              <h3 className="text-2xl font-semibold text-slate-900">
                {editorState.kind === 'bankAccount'
                  ? editorState.index === null
                    ? 'Добавить банковский счет'
                    : 'Редактировать банковский счет'
                  : editorState.kind === 'cashRegister'
                    ? editorState.index === null
                      ? 'Добавить кассу'
                      : 'Редактировать кассу'
                    : editorState.index === null
                      ? 'Добавить карту'
                      : 'Редактировать карту'}
              </h3>
              <p className="mt-1 text-sm text-slate-500">
                {editorState.kind === 'bankAccount'
                  ? 'Счет сохраняется внутри карточки юрлица и потом может использоваться в печатных документах.'
                  : editorState.kind === 'cashRegister'
                    ? 'Касса сохранится как часть финансовой инфраструктуры компании.'
                    : 'Карта сохранится в карточке юрлица и будет доступна для дальнейшей логики работы с компаниями.'}
              </p>
            </div>
            <button
              type="button"
              onClick={closeEditor}
              className="rounded-xl p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600"
              aria-label="Закрыть"
            >
              <X className="h-5 w-5" />
            </button>
          </div>

          {editorError ? (
            <div className="px-6 pt-5">
              <div className="rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
                {editorError}
              </div>
            </div>
          ) : null}

          <div className="px-6 py-5">
            {editorState.kind === 'bankAccount' ? renderBankAccountEditor() : null}
            {editorState.kind === 'cashRegister' ? renderCashRegisterEditor() : null}
            {editorState.kind === 'card' ? renderCardEditor() : null}
          </div>

          <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-6 py-5">
            <button
              type="button"
              onClick={closeEditor}
              className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50"
            >
              Отмена
            </button>
            <button
              type="button"
              onClick={() => {
                if (editorState.kind === 'bankAccount') {
                  saveBankAccount();
                  return;
                }

                if (editorState.kind === 'cashRegister') {
                  saveCashRegister();
                  return;
                }

                saveCard();
              }}
              className="rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700"
            >
              Сохранить
            </button>
          </div>
        </div>
      </div>,
      document.body,
    );
  };

  return (
    <>
      <section className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
        <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
          <div>
            <div className="flex items-center gap-3">
              <div className="inline-flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-50">
                {activeIcon}
              </div>
              <div>
                <h3 className="text-2xl font-semibold text-slate-900">Финансовая инфраструктура компании</h3>
                <p className="mt-1 max-w-3xl text-sm text-slate-500">
                  Здесь мы ведем банковские счета, кассы и карты юридического лица. Изменения сохранятся вместе с карточкой.
                </p>
              </div>
            </div>
          </div>

          {!readOnly ? (
            <button
              type="button"
              onClick={openActiveCreateDialog}
              className="inline-flex items-center justify-center gap-2 rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700"
            >
              <Plus className="h-4 w-4" />
              {activeTab === 'bankAccounts'
                ? 'Добавить банковский счет'
                : activeTab === 'cashRegisters'
                  ? 'Добавить кассу'
                  : 'Добавить карту'}
            </button>
          ) : null}
        </div>

        <div className="mt-6 flex flex-wrap gap-3">
          {renderTabButton('bankAccounts', 'Банковские счета', bankCount)}
          {renderTabButton('cashRegisters', 'Кассы', cashCount)}
          {renderTabButton('cards', 'Карты', cardCount)}
        </div>

        <div className="mt-6">{renderBankDirectoryHint()}</div>

        <div className="mt-6">
          {activeTab === 'bankAccounts' ? renderBankAccountsTab() : null}
          {activeTab === 'cashRegisters' ? renderCashRegistersTab() : null}
          {activeTab === 'cards' ? renderCardsTab() : null}
        </div>

        <div className="mt-4 text-xs text-slate-500">
          Если банк уже есть в отдельном справочнике, лучше выбрать его там: так реквизиты будут переиспользоваться и не разъедутся по системе.
        </div>
      </section>

      {renderEditor()}
    </>
  );
}
