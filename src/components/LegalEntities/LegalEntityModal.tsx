import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  ArrowLeft,
  CheckCircle2,
  Loader2,
  Mail,
  MapPin,
  Phone,
  Search,
  Plus,
  X,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import type {
  Client,
  LegalEntityDetail,
  LegalEntityInput,
  LegalEntitySuggestion,
} from '../../types';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { useDialog } from '../../hooks/useDialog';

interface LegalEntityModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (payload: LegalEntityInput) => Promise<LegalEntityDetail>;
  onDelete?: () => Promise<void>;
  initial?: LegalEntityDetail | null;
  clients: Client[];
  suggest: (payload: { query?: string; inn?: string; limit?: number }) => Promise<LegalEntitySuggestion[]>;
  allowEdit: boolean;
  allowDelete: boolean;
  mode?: 'modal' | 'page';
  onBack?: () => void;
}

interface FormState {
  shortName: string;
  fullName: string;
  inn: string;
  kpp: string;
  ogrn: string;
  address: string;
  phone: string;
  email: string;
  director: string;
  country: string;
  baseCurrency: string;
  serviceLanguage: string;
  organizationForm: string;
  timezone: string;
  taxSystem: string;
  taxSystemMode: string;
  incomeTaxRate: string;
  incomeExpenseTaxRate: string;
  vatWithoutVat: boolean;
  okved: string;
  okpo: string;
  okato: string;
  oktmo: string;
  okogu: string;
  okfs: string;
  okopf: string;
  pfr: string;
  fss: string;
  charterCapital: string;
  taxRegistrationDate: string;
  taxOfficeCode: string;
  taxRegion: string;
  ifnsName: string;
  taxOfficePhone: string;
  treasuryInn: string;
  treasuryKpp: string;
  treasuryRecipientName: string;
  treasuryBankBik: string;
  treasuryBankName: string;
  treasuryAccountNumber: string;
  treasuryCorrAccountNumber: string;
  treasuryKbk: string;
  notes: string;
}


interface BankAccountItem {
  bank: string;
  name: string;
  bik: string;
  currency: string;
  accountNumber: string;
  accountType: string;
}

interface CashRegisterItem {
  name: string;
  currency: string;
  access: string;
}

interface CardItem {
  number: string;
  bank: string;
  currency: string;
  holder: string;
}

interface PersonDocInfo {
  position: string;
  lastName: string;
  firstName: string;
  middleName: string;
  noMiddleName: boolean;
}

interface EnglishRequisites {
  organizationName: string;
  fullName: string;
  form: string;
  legalAddress: string;
  actualAddress: string;
  position: string;
  lastName: string;
  firstName: string;
  middleName: string;
}

const EMPTY_BANK_ACCOUNT: BankAccountItem = {
  bank: '',
  name: '',
  bik: '',
  currency: 'RUB',
  accountNumber: '',
  accountType: '',
};

const EMPTY_CASH_REGISTER: CashRegisterItem = {
  name: '',
  currency: 'RUB',
  access: '',
};

const EMPTY_CARD: CardItem = {
  number: '',
  bank: '',
  currency: 'RUB',
  holder: '',
};

const EMPTY_PERSON_DOC: PersonDocInfo = {
  position: '',
  lastName: '',
  firstName: '',
  middleName: '',
  noMiddleName: false,
};

const EMPTY_ENGLISH_REQUISITES: EnglishRequisites = {
  organizationName: '',
  fullName: '',
  form: '',
  legalAddress: '',
  actualAddress: '',
  position: '',
  lastName: '',
  firstName: '',
  middleName: '',
};

function parseJsonOrDefault<T>(json: string | null | undefined, fallback: T): T {
  if (!json) return fallback;
  try {
    return JSON.parse(json) as T;
  } catch {
    return fallback;
  }
}

const DEFAULT_STATE: FormState = {
  shortName: '',
  fullName: '',
  inn: '',
  kpp: '',
  ogrn: '',
  address: '',
  phone: '',
  email: '',
  director: '',
  country: '',
  baseCurrency: '',
  serviceLanguage: '',
  organizationForm: '',
  timezone: '',
  taxSystem: '',
  taxSystemMode: '',
  incomeTaxRate: '',
  incomeExpenseTaxRate: '',
  vatWithoutVat: false,
  okved: '',
  okpo: '',
  okato: '',
  oktmo: '',
  okogu: '',
  okfs: '',
  okopf: '',
  pfr: '',
  fss: '',
  charterCapital: '',
  taxRegistrationDate: '',
  taxOfficeCode: '',
  taxRegion: '',
  ifnsName: '',
  taxOfficePhone: '',
  treasuryInn: '',
  treasuryKpp: '',
  treasuryRecipientName: '',
  treasuryBankBik: '',
  treasuryBankName: '',
  treasuryAccountNumber: '',
  treasuryCorrAccountNumber: '',
  treasuryKbk: '',
  notes: '',
};

export function LegalEntityModal({
  isOpen,
  onClose,
  onSubmit,
  onDelete,
  initial,
  clients,
  suggest,
  allowEdit,
  allowDelete,
  mode = 'modal',
  onBack,
}: LegalEntityModalProps) {
  const { t } = useTranslation();
  const { confirm } = useDialog();
  const [formState, setFormState] = useState<FormState>(DEFAULT_STATE);
  const [clientIds, setClientIds] = useState<number[]>([]);
  const [loading, setLoading] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerSearch, setPickerSearch] = useState('');
  const [dadataQuery, setDadataQuery] = useState('');
  const [suggestions, setSuggestions] = useState<LegalEntitySuggestion[]>([]);
  const [suggestLoading, setSuggestLoading] = useState(false);
  const [suggestError, setSuggestError] = useState<string | null>(null);
  const [bankAccounts, setBankAccounts] = useState<BankAccountItem[]>([]);
  const [cashRegisters, setCashRegisters] = useState<CashRegisterItem[]>([]);
  const [cards, setCards] = useState<CardItem[]>([]);
  const [englishRequisites, setEnglishRequisites] = useState<EnglishRequisites>(EMPTY_ENGLISH_REQUISITES);
  const [documentDirector, setDocumentDirector] = useState<PersonDocInfo>(EMPTY_PERSON_DOC);
  const [chiefAccountant, setChiefAccountant] = useState<PersonDocInfo>(EMPTY_PERSON_DOC);
  const [logoDataUrl, setLogoDataUrl] = useState<string>('');
  const [sealDataUrl, setSealDataUrl] = useState<string>('');
  const [directorSignatureDataUrl, setDirectorSignatureDataUrl] = useState<string>('');

  const readOnly = !allowEdit && !!initial;
  const isPageMode = mode === 'page';

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };
    window.addEventListener('keydown', handleKey);
    return () => window.removeEventListener('keydown', handleKey);
  }, [isOpen, onClose]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    if (initial) {
      setFormState({
        shortName: initial.shortName ?? '',
        fullName: initial.fullName ?? '',
        inn: initial.inn ?? '',
        kpp: initial.kpp ?? '',
        ogrn: initial.ogrn ?? '',
        address: initial.address ?? '',
        phone: initial.phone ?? '',
        email: initial.email ?? '',
        director: initial.director ?? '',
        country: initial.country ?? '',
        baseCurrency: initial.baseCurrency ?? '',
        serviceLanguage: initial.serviceLanguage ?? '',
        organizationForm: initial.organizationForm ?? '',
        timezone: initial.timezone ?? '',
        taxSystem: initial.taxSystem ?? '',
        taxSystemMode: initial.taxSystemMode ?? '',
        incomeTaxRate: initial.incomeTaxRate ?? '',
        incomeExpenseTaxRate: initial.incomeExpenseTaxRate ?? '',
        vatWithoutVat: initial.vatWithoutVat ?? false,
        okved: initial.okved ?? '',
        okpo: initial.okpo ?? '',
        okato: initial.okato ?? '',
        oktmo: initial.oktmo ?? '',
        okogu: initial.okogu ?? '',
        okfs: initial.okfs ?? '',
        okopf: initial.okopf ?? '',
        pfr: initial.pfr ?? '',
        fss: initial.fss ?? '',
        charterCapital: initial.charterCapital ?? '',
        taxRegistrationDate: initial.taxRegistrationDate ?? '',
        taxOfficeCode: initial.taxOfficeCode ?? '',
        taxRegion: initial.taxRegion ?? '',
        ifnsName: initial.ifnsName ?? '',
        taxOfficePhone: initial.taxOfficePhone ?? '',
        treasuryInn: initial.treasuryInn ?? '',
        treasuryKpp: initial.treasuryKpp ?? '',
        treasuryRecipientName: initial.treasuryRecipientName ?? '',
        treasuryBankBik: initial.treasuryBankBik ?? '',
        treasuryBankName: initial.treasuryBankName ?? '',
        treasuryAccountNumber: initial.treasuryAccountNumber ?? '',
        treasuryCorrAccountNumber: initial.treasuryCorrAccountNumber ?? '',
        treasuryKbk: initial.treasuryKbk ?? '',
        notes: initial.notes ?? '',
      });
      setClientIds((initial.clients ?? []).map((c) => c.id));
      setBankAccounts(parseJsonOrDefault<BankAccountItem[]>(initial.bankAccountsJson, []));
      setCashRegisters(parseJsonOrDefault<CashRegisterItem[]>(initial.cashRegistersJson, []));
      setCards(parseJsonOrDefault<CardItem[]>(initial.cardsJson, []));
      setEnglishRequisites(parseJsonOrDefault<EnglishRequisites>(initial.requisitesEnglishJson, EMPTY_ENGLISH_REQUISITES));
      setDocumentDirector(parseJsonOrDefault<PersonDocInfo>(initial.documentDirectorJson, EMPTY_PERSON_DOC));
      setChiefAccountant(parseJsonOrDefault<PersonDocInfo>(initial.chiefAccountantJson, EMPTY_PERSON_DOC));
      setLogoDataUrl(initial.logoDataUrl ?? "");
      setSealDataUrl(initial.sealDataUrl ?? "");
      setDirectorSignatureDataUrl(initial.directorSignatureDataUrl ?? "");
    } else {
      setFormState(DEFAULT_STATE);
      setClientIds([]);
      setBankAccounts([]);
      setCashRegisters([]);
      setCards([]);
      setEnglishRequisites(EMPTY_ENGLISH_REQUISITES);
      setDocumentDirector(EMPTY_PERSON_DOC);
      setChiefAccountant(EMPTY_PERSON_DOC);
      setLogoDataUrl("");
      setSealDataUrl("");
      setDirectorSignatureDataUrl("");
    }
    setError(null);
    setSuggestions([]);
    setSuggestError(null);
    setDadataQuery('');
    setPickerOpen(false);
    setPickerSearch('');
  }, [initial, isOpen]);

  const selectedClients = useMemo(
    () => clients.filter((client) => clientIds.includes(client.id)),
    [clientIds, clients],
  );

  const availableClients = useMemo(() => {
    const selected = new Set(clientIds);
    const term = pickerSearch.trim().toLowerCase();

    return clients
      .filter((client) => !selected.has(client.id))
      .filter((client) => {
        if (!term) return true;
        return `${client.name ?? ''} ${client.company ?? ''} ${client.phone ?? ''} ${client.email ?? ''}`
          .toLowerCase()
          .includes(term);
      });
  }, [clientIds, clients, pickerSearch]);

  const handleInputChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>,
  ) => {
    const { name, value, type } = e.target;
    const parsed = type === 'checkbox' ? (e.target as HTMLInputElement).checked : value;
    setFormState((prev) => ({ ...prev, [name]: parsed }));
  };

  const handleAddClient = (id: number) => {
    setClientIds((prev) => (prev.includes(id) ? prev : [...prev, id]));
  };

  const handleRemoveClient = (id: number) => {
    setClientIds((prev) => prev.filter((clientId) => clientId !== id));
  };

  const handleImageUpload = (
    file: File | null,
    setter: (value: string) => void,
  ) => {
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      setter(typeof reader.result === 'string' ? reader.result : '');
    };
    reader.readAsDataURL(file);
  };

  const updateEnglishRequisites = (key: keyof EnglishRequisites, value: string) => {
    setEnglishRequisites((prev) => ({ ...prev, [key]: value }));
  };

  const updatePersonInfo = (
    setter: (value: ((prev: PersonDocInfo) => PersonDocInfo)) => void,
    key: keyof PersonDocInfo,
    value: string | boolean,
  ) => {
    setter((prev) => ({ ...prev, [key]: value }));
  };

  const handleSuggest = async () => {
    setSuggestError(null);
    const query = dadataQuery.trim();
    if (!query) {
      setSuggestError(t('legalEntityDadataEmptyQuery') || 'Введите запрос для поиска');
      return;
    }
    try {
      setSuggestLoading(true);
      const results = await suggest({ query, limit: 6 });
      setSuggestions(results ?? []);
      if ((results ?? []).length === 0) {
        setSuggestError(t('legalEntityDadataNoResults') || 'Ничего не найдено');
      }
    } catch (err) {
      setSuggestError(
        err instanceof Error ? err.message : t('legalEntityDadataError') || 'Не удалось получить подсказки',
      );
    } finally {
      setSuggestLoading(false);
    }
  };

  const applySuggestion = (suggestion: LegalEntitySuggestion) => {
    setFormState((prev) => ({
      ...prev,
      shortName: suggestion.shortName || prev.shortName,
      fullName: suggestion.fullName ?? prev.fullName,
      inn: suggestion.inn ?? prev.inn,
      kpp: suggestion.kpp ?? prev.kpp,
      ogrn: suggestion.ogrn ?? prev.ogrn,
      address: suggestion.address ?? prev.address,
      phone: suggestion.phone ?? prev.phone,
      email: suggestion.email ?? prev.email,
      director: suggestion.director ?? prev.director,
    }));
  };

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (readOnly) {
      onClose();
      return;
    }
    setError(null);
    setLoading(true);
    try {
      const shortName = formState.shortName.trim();
      const normalize = (value: string) => {
        const trimmed = value.trim();
        return trimmed || null;
      };
      const payload: LegalEntityInput = {
        shortName,
        fullName: normalize(formState.fullName),
        inn: normalize(formState.inn),
        kpp: normalize(formState.kpp),
        ogrn: normalize(formState.ogrn),
        address: normalize(formState.address),
        phone: normalize(formState.phone),
        email: normalize(formState.email),
        director: normalize(formState.director),
        country: normalize(formState.country),
        baseCurrency: normalize(formState.baseCurrency),
        serviceLanguage: normalize(formState.serviceLanguage),
        organizationForm: normalize(formState.organizationForm),
        timezone: normalize(formState.timezone),
        taxSystem: normalize(formState.taxSystem),
        taxSystemMode: normalize(formState.taxSystemMode),
        incomeTaxRate: normalize(formState.incomeTaxRate),
        incomeExpenseTaxRate: normalize(formState.incomeExpenseTaxRate),
        vatWithoutVat: formState.vatWithoutVat,
        okved: normalize(formState.okved),
        okpo: normalize(formState.okpo),
        okato: normalize(formState.okato),
        oktmo: normalize(formState.oktmo),
        okogu: normalize(formState.okogu),
        okfs: normalize(formState.okfs),
        okopf: normalize(formState.okopf),
        pfr: normalize(formState.pfr),
        fss: normalize(formState.fss),
        charterCapital: normalize(formState.charterCapital),
        taxRegistrationDate: normalize(formState.taxRegistrationDate),
        taxOfficeCode: normalize(formState.taxOfficeCode),
        taxRegion: normalize(formState.taxRegion),
        ifnsName: normalize(formState.ifnsName),
        taxOfficePhone: normalize(formState.taxOfficePhone),
        treasuryInn: normalize(formState.treasuryInn),
        treasuryKpp: normalize(formState.treasuryKpp),
        treasuryRecipientName: normalize(formState.treasuryRecipientName),
        treasuryBankBik: normalize(formState.treasuryBankBik),
        treasuryBankName: normalize(formState.treasuryBankName),
        treasuryAccountNumber: normalize(formState.treasuryAccountNumber),
        treasuryCorrAccountNumber: normalize(formState.treasuryCorrAccountNumber),
        treasuryKbk: normalize(formState.treasuryKbk),
        bankAccountsJson: bankAccounts.length > 0 ? JSON.stringify(bankAccounts) : null,
        cashRegistersJson: cashRegisters.length > 0 ? JSON.stringify(cashRegisters) : null,
        cardsJson: cards.length > 0 ? JSON.stringify(cards) : null,
        requisitesEnglishJson: JSON.stringify(englishRequisites),
        logoDataUrl: logoDataUrl || null,
        sealDataUrl: sealDataUrl || null,
        directorSignatureDataUrl: directorSignatureDataUrl || null,
        documentDirectorJson: JSON.stringify(documentDirector),
        chiefAccountantJson: JSON.stringify(chiefAccountant),
        notes: normalize(formState.notes),
        clientIds,
      };
      if (!payload.shortName) {
        setError(t('legalEntityShortNameRequired') || 'Укажите краткое название');
        setLoading(false);
        return;
      }
      const saved = await onSubmit(payload);
      setFormState((prev) => ({
        ...prev,
        shortName: saved.shortName ?? payload.shortName,
        fullName: saved.fullName ?? prev.fullName,
        inn: saved.inn ?? prev.inn,
        kpp: saved.kpp ?? prev.kpp,
        ogrn: saved.ogrn ?? prev.ogrn,
        address: saved.address ?? prev.address,
        phone: saved.phone ?? prev.phone,
        email: saved.email ?? prev.email,
        director: saved.director ?? prev.director,
        country: saved.country ?? prev.country,
        baseCurrency: saved.baseCurrency ?? prev.baseCurrency,
        serviceLanguage: saved.serviceLanguage ?? prev.serviceLanguage,
        organizationForm: saved.organizationForm ?? prev.organizationForm,
        timezone: saved.timezone ?? prev.timezone,
        taxSystem: saved.taxSystem ?? prev.taxSystem,
        taxSystemMode: saved.taxSystemMode ?? prev.taxSystemMode,
        incomeTaxRate: saved.incomeTaxRate ?? prev.incomeTaxRate,
        incomeExpenseTaxRate: saved.incomeExpenseTaxRate ?? prev.incomeExpenseTaxRate,
        vatWithoutVat: saved.vatWithoutVat ?? prev.vatWithoutVat,
        okved: saved.okved ?? prev.okved,
        okpo: saved.okpo ?? prev.okpo,
        okato: saved.okato ?? prev.okato,
        oktmo: saved.oktmo ?? prev.oktmo,
        okogu: saved.okogu ?? prev.okogu,
        okfs: saved.okfs ?? prev.okfs,
        okopf: saved.okopf ?? prev.okopf,
        pfr: saved.pfr ?? prev.pfr,
        fss: saved.fss ?? prev.fss,
        charterCapital: saved.charterCapital ?? prev.charterCapital,
        taxRegistrationDate: saved.taxRegistrationDate ?? prev.taxRegistrationDate,
        taxOfficeCode: saved.taxOfficeCode ?? prev.taxOfficeCode,
        taxRegion: saved.taxRegion ?? prev.taxRegion,
        ifnsName: saved.ifnsName ?? prev.ifnsName,
        taxOfficePhone: saved.taxOfficePhone ?? prev.taxOfficePhone,
        treasuryInn: saved.treasuryInn ?? prev.treasuryInn,
        treasuryKpp: saved.treasuryKpp ?? prev.treasuryKpp,
        treasuryRecipientName: saved.treasuryRecipientName ?? prev.treasuryRecipientName,
        treasuryBankBik: saved.treasuryBankBik ?? prev.treasuryBankBik,
        treasuryBankName: saved.treasuryBankName ?? prev.treasuryBankName,
        treasuryAccountNumber: saved.treasuryAccountNumber ?? prev.treasuryAccountNumber,
        treasuryCorrAccountNumber: saved.treasuryCorrAccountNumber ?? prev.treasuryCorrAccountNumber,
        treasuryKbk: saved.treasuryKbk ?? prev.treasuryKbk,
        notes: saved.notes ?? prev.notes,
      }));
      setClientIds((saved.clients ?? []).map((c) => c.id));
      setBankAccounts(parseJsonOrDefault<BankAccountItem[]>(saved.bankAccountsJson, bankAccounts));
      setCashRegisters(parseJsonOrDefault<CashRegisterItem[]>(saved.cashRegistersJson, cashRegisters));
      setCards(parseJsonOrDefault<CardItem[]>(saved.cardsJson, cards));
      setEnglishRequisites(parseJsonOrDefault<EnglishRequisites>(saved.requisitesEnglishJson, englishRequisites));
      setDocumentDirector(parseJsonOrDefault<PersonDocInfo>(saved.documentDirectorJson, documentDirector));
      setChiefAccountant(parseJsonOrDefault<PersonDocInfo>(saved.chiefAccountantJson, chiefAccountant));
      setLogoDataUrl(saved.logoDataUrl ?? logoDataUrl);
      setSealDataUrl(saved.sealDataUrl ?? sealDataUrl);
      setDirectorSignatureDataUrl(saved.directorSignatureDataUrl ?? directorSignatureDataUrl);
      if (!isPageMode) {
        onClose();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : t('saveError') || 'Не удалось сохранить');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async () => {
    if (!onDelete || !initial) return;
    if (!(await confirm({ title: 'Подтверждение удаления', description: t('deleteLegalEntityConfirm') || 'Удалить юридическое лицо?', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }))) {
      return;
    }
    try {
      setDeleting(true);
      await onDelete();
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : t('legalEntityDeleteError') || 'Не удалось удалить запись');
    } finally {
      setDeleting(false);
    }
  };

  if (!isOpen) return null;

  const content = (
    <div className={isPageMode ? 'rounded-2xl border border-gray-200 bg-white shadow-sm' : 'w-full max-w-5xl max-h-[90vh] overflow-y-auto rounded-2xl bg-white shadow-2xl'}>
        <div className="flex items-start justify-between border-b border-gray-200 px-6 py-4">
          <div>
            {isPageMode && onBack && (
              <button
                type="button"
                onClick={onBack}
                className="mb-3 inline-flex items-center gap-2 rounded border px-3 py-2 text-sm">
                <ArrowLeft size={16} />
                {t('previous') || 'Назад'}
              </button>
            )}
            <h2 className="text-xl font-semibold text-gray-900">
              {initial
                ? t('editLegalEntity') || 'Редактирование юридического лица'
                : t('addLegalEntity') || 'Добавление юридического лица'}
            </h2>
            {initial && (
              <p className="mt-1 text-sm text-gray-500">
                {(t('createdAt') || 'Создано')}: {new Date(initial.createdAt).toLocaleString()}
                {initial.updatedAt ? (
                  <> • {(t('updatedAt') || 'Обновлено')}: {new Date(initial.updatedAt).toLocaleString()}</>
                ) : null}
              </p>
            )}
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg p-2 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
            aria-label={isPageMode ? (t('previous') || 'Назад') : (t('cancel') || 'Закрыть')}>
            <X size={20} />
          </button>
        </div>

        {error && (
          <div className="mx-6 mt-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
            {error}
          </div>
        )}

        <form onSubmit={handleSubmit} className="px-6 pb-6 pt-4 space-y-6">
          <div className="grid gap-6 md:grid-cols-2">
            <div className="space-y-4">
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityShortName') || 'Краткое название'}*
                </label>
                <input
                  type="text"
                  name="shortName"
                  value={formState.shortName}
                  onChange={handleInputChange}
                  required
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityFullName') || 'Полное название'}
                </label>
                <textarea
                  name="fullName"
                  value={formState.fullName}
                  onChange={handleInputChange}
                  rows={2}
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityInn') || 'ИНН'}
                  </label>
                  <input
                    type="text"
                    name="inn"
                    value={formState.inn}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityKpp') || 'КПП'}
                  </label>
                  <input
                    type="text"
                    name="kpp"
                    value={formState.kpp}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityOgrn') || 'ОГРН'}
                  </label>
                  <input
                    type="text"
                    name="ogrn"
                    value={formState.ogrn}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityDirector') || 'Руководитель'}
                </label>
                <input
                  type="text"
                  name="director"
                  value={formState.director}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
            </div>
            <div className="space-y-4">
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityAddress') || 'Адрес'}
                </label>
                <div className="relative">
                  <MapPin className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
                  <input
                    type="text"
                    name="address"
                    value={formState.address}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
              </div>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityPhone') || 'Телефон'}
                  </label>
                  <div className="relative">
                    <Phone className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
                    <input
                      type="text"
                      name="phone"
                      value={formState.phone}
                      onChange={handleInputChange}
                      disabled={readOnly}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                  </div>
                </div>
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityEmail') || 'Email'}
                  </label>
                  <div className="relative">
                    <Mail className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
                    <input
                      type="email"
                      name="email"
                      value={formState.email}
                      onChange={handleInputChange}
                      disabled={readOnly}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                  </div>
                </div>
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityNotes') || 'Заметки'}
                </label>
                <textarea
                  name="notes"
                  value={formState.notes}
                  onChange={handleInputChange}
                  rows={3}
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
            </div>
          </div>

          <div className="grid gap-6 lg:grid-cols-[2fr,1fr]">
            <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
              <div className="flex items-center justify-between gap-2 border-b border-gray-100 pb-3">
                <div>
                  <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">
                    {t('legalEntityClients') || 'Клиенты юридического лица'}
                  </h3>
                  <p className="text-xs text-gray-500">
                    {t('legalEntityClientsHint') || 'Выберите клиентов, которые относятся к этому юридическому лицу.'}
                  </p>
                </div>
                <button
                  type="button"
                  onClick={() => setPickerOpen(true)}
                  disabled={readOnly}
                  className="inline-flex items-center gap-2 rounded border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700 disabled:cursor-not-allowed disabled:opacity-60">
                  <Plus className="h-4 w-4" />
                  {t('addClient') || 'Добавить клиента'}
                </button>
              </div>

              <div className="mt-3 overflow-x-auto">
                <table className="min-w-full divide-y text-sm">
                  <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                    <tr>
                      <th className="px-3 py-2 text-left">ФИО</th>
                      <th className="px-3 py-2 text-left">Компания</th>
                      <th className="px-3 py-2 text-left">Контакты</th>
                      <th className="px-3 py-2 text-right">Действия</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {selectedClients.map((client) => (
                      <tr key={client.id}>
                        <td className="px-3 py-2">
                          <div className="font-medium text-gray-900">{client.name}</div>
                          <div className="mt-1"><ClientStatusBadge status={client.clientStatus} /></div>
                        </td>
                        <td className="px-3 py-2">{client.company || '—'}</td>
                        <td className="px-3 py-2 text-xs text-gray-600">
                          <div>{client.phone || '—'}</div>
                          <div>{client.email || '—'}</div>
                        </td>
                        <td className="px-3 py-2 text-right">
                          <button
                            type="button"
                            onClick={() => handleRemoveClient(client.id)}
                            disabled={readOnly}
                            className="rounded border border-rose-200 p-1 text-rose-600 disabled:opacity-60">
                            <X className="h-4 w-4" />
                          </button>
                        </td>
                      </tr>
                    ))}
                    {selectedClients.length === 0 && (
                      <tr>
                        <td colSpan={4} className="px-3 py-6 text-center text-slate-500">
                          {t('nothingFound') || 'Ничего не найдено'}
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            </div>
            <div className="rounded-xl border border-dashed border-blue-200 bg-blue-50/50 p-4">
              <h3 className="text-sm font-semibold text-blue-900">
                {t('legalEntityDadataTitle') || 'Заполнить по данным DaData'}
              </h3>
              <p className="mt-1 text-xs text-blue-700/80">
                {t('legalEntityDadataDescription') ||
                  'Введите ИНН, название или адрес компании, чтобы автоматически заполнить основные поля.'}
              </p>
              <div className="mt-3 flex items-center gap-2">
                <div className="relative flex-1">
                  <Search className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-blue-400" />
                  <input
                    type="text"
                    value={dadataQuery}
                    onChange={(e) => setDadataQuery(e.target.value)}
                    placeholder={t('legalEntityDadataPlaceholder') || 'Например: 7707083893 или ООО Пример'}
                    className="w-full rounded-lg border border-blue-200 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-400"
                  />
                </div>
                <button
                  type="button"
                  onClick={handleSuggest}
                  disabled={suggestLoading || readOnly}
                  className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300">
                  {suggestLoading && <Loader2 className="h-4 w-4 animate-spin" />}
                  {t('legalEntityDadataSearch') || 'Найти'}
                </button>
              </div>
              {suggestError && (
                <div className="mt-2 rounded-lg border border-amber-200 bg-amber-50 p-2 text-xs text-amber-700">
                  {suggestError}
                </div>
              )}
              <div className="mt-3 space-y-2">
                {suggestions.map((suggestion, idx) => (
                  <button
                    key={`${suggestion.inn ?? suggestion.shortName}-${idx}`}
                    type="button"
                    onClick={() => applySuggestion(suggestion)}
                    className="w-full rounded-lg border border-blue-200 bg-white px-4 py-3 text-left text-sm shadow-sm transition hover:border-blue-400 hover:shadow">
                    <div className="flex items-start justify-between gap-2">
                      <div>
                        <div className="font-medium text-blue-900">{suggestion.shortName}</div>
                        {suggestion.fullName && (
                          <div className="text-xs text-blue-700">{suggestion.fullName}</div>
                        )}
                        <div className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-blue-700/80">
                          {suggestion.inn && <span>ИНН: {suggestion.inn}</span>}
                          {suggestion.kpp && <span>КПП: {suggestion.kpp}</span>}
                          {suggestion.ogrn && <span>ОГРН: {suggestion.ogrn}</span>}
                        </div>
                        {suggestion.address && (
                          <div className="mt-1 text-xs text-blue-700/80">{suggestion.address}</div>
                        )}
                      </div>
                      <CheckCircle2 className="h-5 w-5 text-blue-500" />
                    </div>
                  </button>
                ))}
                {!suggestLoading && suggestions.length === 0 && !suggestError && (
                  <div className="rounded-lg border border-dashed border-blue-200 p-3 text-center text-xs text-blue-700/80">
                    {t('legalEntityDadataHint') || 'Результаты появятся после выполнения поиска.'}
                  </div>
                )}
              </div>
            </div>
          </div>

          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">Расширенные реквизиты (из профиля компании)</h3>
            <div className="mt-4 grid gap-4 md:grid-cols-3">
              {[
                ['country', 'Страна'],
                ['baseCurrency', 'Базовая валюта'],
                ['serviceLanguage', 'Язык сервиса'],
                ['organizationForm', 'Форма организации'],
                ['timezone', 'Часовой пояс'],
                ['taxSystem', 'Система налогообложения'],
                ['taxSystemMode', 'Режим налогообложения'],
                ['incomeTaxRate', 'Ставка (доход)'],
                ['incomeExpenseTaxRate', 'Ставка (доход-расход)'],
                ['okved', 'ОКВЭД'],
                ['okpo', 'ОКПО'],
                ['okato', 'ОКАТО'],
                ['oktmo', 'ОКТМО'],
                ['okogu', 'ОКОГУ'],
                ['okfs', 'ОКФС'],
                ['okopf', 'ОКОПФ'],
                ['pfr', 'ПФР'],
                ['fss', 'ФСС'],
                ['charterCapital', 'Уставный капитал'],
                ['taxRegistrationDate', 'Дата постановки на учет'],
                ['taxOfficeCode', 'Код налоговой'],
                ['taxRegion', 'Регион налоговой'],
                ['ifnsName', 'Наименование ИФНС'],
                ['taxOfficePhone', 'Телефон налоговой'],
                ['treasuryInn', 'ИНН получателя (ЕНС)'],
                ['treasuryKpp', 'КПП получателя (ЕНС)'],
                ['treasuryRecipientName', 'Получатель (ЕНС)'],
                ['treasuryBankBik', 'БИК банка (ЕНС)'],
                ['treasuryBankName', 'Банк (ЕНС)'],
                ['treasuryAccountNumber', 'Счет получателя (ЕНС)'],
                ['treasuryCorrAccountNumber', 'Кор. счет (ЕНС)'],
                ['treasuryKbk', 'КБК (ЕНС)'],
              ].map(([name, label]) => (
                <div key={name as string}>
                  <label className="mb-2 block text-sm font-medium text-gray-700">{label}</label>
                  <input
                    type="text"
                    name={name as string}
                    value={(formState as Record<string, string | boolean>)[name as string] as string}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
              ))}
              <label className="flex items-center gap-2 text-sm text-gray-700">
                <input
                  type="checkbox"
                  name="vatWithoutVat"
                  checked={formState.vatWithoutVat}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className="h-4 w-4"
                />
                Без НДС
              </label>
            </div>
          </div>

          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm space-y-4">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">Банковские счета / Кассы / Карты</h3>
            <div className="grid gap-3 md:grid-cols-3">
              <button type="button" disabled={readOnly} onClick={() => setBankAccounts((prev) => [...prev, { ...EMPTY_BANK_ACCOUNT }])} className="rounded-xl border border-blue-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50">+ Добавить банковский счет</button>
              <button type="button" disabled={readOnly} onClick={() => setCashRegisters((prev) => [...prev, { ...EMPTY_CASH_REGISTER }])} className="rounded-xl border border-blue-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50">+ Добавить кассу</button>
              <button type="button" disabled={readOnly} onClick={() => setCards((prev) => [...prev, { ...EMPTY_CARD }])} className="rounded-xl border border-blue-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50">+ Добавить карту</button>
            </div>

            <div>
              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Банковские счета ({bankAccounts.length})</div>
              <div className="space-y-2">
                {bankAccounts.map((item, idx) => (
                  <div key={`bank-${idx}`} className="grid gap-2 md:grid-cols-6">
                    <input value={item.bank} onChange={(e) => setBankAccounts((prev) => prev.map((x, i) => i === idx ? { ...x, bank: e.target.value } : x))} disabled={readOnly} placeholder="Банк" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.name} onChange={(e) => setBankAccounts((prev) => prev.map((x, i) => i === idx ? { ...x, name: e.target.value } : x))} disabled={readOnly} placeholder="Название" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.bik} onChange={(e) => setBankAccounts((prev) => prev.map((x, i) => i === idx ? { ...x, bik: e.target.value } : x))} disabled={readOnly} placeholder="БИК" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.currency} onChange={(e) => setBankAccounts((prev) => prev.map((x, i) => i === idx ? { ...x, currency: e.target.value } : x))} disabled={readOnly} placeholder="Валюта" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.accountNumber} onChange={(e) => setBankAccounts((prev) => prev.map((x, i) => i === idx ? { ...x, accountNumber: e.target.value } : x))} disabled={readOnly} placeholder="Р/с" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <div className="flex gap-2">
                      <input value={item.accountType} onChange={(e) => setBankAccounts((prev) => prev.map((x, i) => i === idx ? { ...x, accountType: e.target.value } : x))} disabled={readOnly} placeholder="Тип" className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                      <button type="button" disabled={readOnly} onClick={() => setBankAccounts((prev) => prev.filter((_, i) => i !== idx))} className="rounded-xl border border-rose-200 px-3 text-rose-600 hover:bg-rose-50">✕</button>
                    </div>
                  </div>
                ))}
              </div>
            </div>

            <div>
              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Кассы ({cashRegisters.length})</div>
              <div className="space-y-2">
                {cashRegisters.map((item, idx) => (
                  <div key={`cash-${idx}`} className="grid gap-2 md:grid-cols-4">
                    <input value={item.name} onChange={(e) => setCashRegisters((prev) => prev.map((x, i) => i === idx ? { ...x, name: e.target.value } : x))} disabled={readOnly} placeholder="Название" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.currency} onChange={(e) => setCashRegisters((prev) => prev.map((x, i) => i === idx ? { ...x, currency: e.target.value } : x))} disabled={readOnly} placeholder="Валюта" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.access} onChange={(e) => setCashRegisters((prev) => prev.map((x, i) => i === idx ? { ...x, access: e.target.value } : x))} disabled={readOnly} placeholder="Доступ" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <button type="button" disabled={readOnly} onClick={() => setCashRegisters((prev) => prev.filter((_, i) => i !== idx))} className="rounded-xl border border-rose-200 px-3 text-rose-600 hover:bg-rose-50">Удалить</button>
                  </div>
                ))}
              </div>
            </div>

            <div>
              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Карты ({cards.length})</div>
              <div className="space-y-2">
                {cards.map((item, idx) => (
                  <div key={`card-${idx}`} className="grid gap-2 md:grid-cols-5">
                    <input value={item.number} onChange={(e) => setCards((prev) => prev.map((x, i) => i === idx ? { ...x, number: e.target.value } : x))} disabled={readOnly} placeholder="Номер" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.bank} onChange={(e) => setCards((prev) => prev.map((x, i) => i === idx ? { ...x, bank: e.target.value } : x))} disabled={readOnly} placeholder="Банк" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.currency} onChange={(e) => setCards((prev) => prev.map((x, i) => i === idx ? { ...x, currency: e.target.value } : x))} disabled={readOnly} placeholder="Валюта" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <input value={item.holder} onChange={(e) => setCards((prev) => prev.map((x, i) => i === idx ? { ...x, holder: e.target.value } : x))} disabled={readOnly} placeholder="Держатель" className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" />
                    <button type="button" disabled={readOnly} onClick={() => setCards((prev) => prev.filter((_, i) => i !== idx))} className="rounded-xl border border-rose-200 px-3 text-rose-600 hover:bg-rose-50">Удалить</button>
                  </div>
                ))}
              </div>
            </div>
          </div>

          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">Реквизиты на английском</h3>
            <div className="mt-3 grid gap-3 md:grid-cols-3">
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Название организации" value={englishRequisites.organizationName} onChange={(e) => updateEnglishRequisites('organizationName', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Полное наименование" value={englishRequisites.fullName} onChange={(e) => updateEnglishRequisites('fullName', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Форма" value={englishRequisites.form} onChange={(e) => updateEnglishRequisites('form', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Юридический адрес" value={englishRequisites.legalAddress} onChange={(e) => updateEnglishRequisites('legalAddress', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Фактический адрес" value={englishRequisites.actualAddress} onChange={(e) => updateEnglishRequisites('actualAddress', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Должность" value={englishRequisites.position} onChange={(e) => updateEnglishRequisites('position', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Фамилия" value={englishRequisites.lastName} onChange={(e) => updateEnglishRequisites('lastName', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Имя" value={englishRequisites.firstName} onChange={(e) => updateEnglishRequisites('firstName', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Отчество" value={englishRequisites.middleName} onChange={(e) => updateEnglishRequisites('middleName', e.target.value)} disabled={readOnly} />
            </div>
          </div>

          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">Логотип / Печать / Подпись руководителя</h3>
            <div className="mt-3 grid gap-4 md:grid-cols-3 text-sm">
              <div>
                <div>Логотип компании</div>
                <input type="file" accept="image/*" className="mt-2 block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-slate-700" disabled={readOnly} onChange={(e) => handleImageUpload(e.target.files?.[0] ?? null, setLogoDataUrl)} />
                {logoDataUrl && <img src={logoDataUrl} alt="Логотип" className="mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1" />}
              </div>
              <div>
                <div>Печать компании</div>
                <input type="file" accept="image/*" className="mt-2 block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-slate-700" disabled={readOnly} onChange={(e) => handleImageUpload(e.target.files?.[0] ?? null, setSealDataUrl)} />
                {sealDataUrl && <img src={sealDataUrl} alt="Печать" className="mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1" />}
              </div>
              <div>
                <div>Подпись руководителя</div>
                <input type="file" accept="image/*" className="mt-2 block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-slate-700" disabled={readOnly} onChange={(e) => handleImageUpload(e.target.files?.[0] ?? null, setDirectorSignatureDataUrl)} />
                {directorSignatureDataUrl && <img src={directorSignatureDataUrl} alt="Подпись" className="mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1" />}
              </div>
            </div>
          </div>

          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">Руководитель для документов</h3>
            <div className="mt-3 grid gap-3 md:grid-cols-5">
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Должность" value={documentDirector.position} onChange={(e) => updatePersonInfo(setDocumentDirector, 'position', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Фамилия" value={documentDirector.lastName} onChange={(e) => updatePersonInfo(setDocumentDirector, 'lastName', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Имя" value={documentDirector.firstName} onChange={(e) => updatePersonInfo(setDocumentDirector, 'firstName', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Отчество" value={documentDirector.middleName} onChange={(e) => updatePersonInfo(setDocumentDirector, 'middleName', e.target.value)} disabled={readOnly} />
              <label className="flex items-center gap-2 text-sm text-slate-600"><input type="checkbox" checked={documentDirector.noMiddleName} onChange={(e) => updatePersonInfo(setDocumentDirector, 'noMiddleName', e.target.checked)} disabled={readOnly} />Нет отчества</label>
            </div>
          </div>

          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">Главный бухгалтер</h3>
            <div className="mt-3 grid gap-3 md:grid-cols-5">
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Должность" value={chiefAccountant.position} onChange={(e) => updatePersonInfo(setChiefAccountant, 'position', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Фамилия" value={chiefAccountant.lastName} onChange={(e) => updatePersonInfo(setChiefAccountant, 'lastName', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Имя" value={chiefAccountant.firstName} onChange={(e) => updatePersonInfo(setChiefAccountant, 'firstName', e.target.value)} disabled={readOnly} />
              <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100" placeholder="Отчество" value={chiefAccountant.middleName} onChange={(e) => updatePersonInfo(setChiefAccountant, 'middleName', e.target.value)} disabled={readOnly} />
              <label className="flex items-center gap-2 text-sm text-slate-600"><input type="checkbox" checked={chiefAccountant.noMiddleName} onChange={(e) => updatePersonInfo(setChiefAccountant, 'noMiddleName', e.target.checked)} disabled={readOnly} />Нет отчества</label>
            </div>
          </div>

          <div className="flex flex-col gap-3 border-t border-gray-200 pt-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="text-xs text-gray-500">
              {t('legalEntityFormHint') || 'Поля, помеченные * обязательны для заполнения.'}
            </div>
            <div className="flex flex-wrap items-center gap-3">
              {allowDelete && initial && onDelete && (
                <button
                  type="button"
                  onClick={handleDelete}
                  disabled={deleting}
                  className="inline-flex items-center gap-2 rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-600 hover:bg-red-100">
                  {deleting && <Loader2 className="h-4 w-4 animate-spin" />}
                  {t('delete') || 'Удалить'}
                </button>
              )}
              <div className="flex items-center gap-3">
                <button
                  type="button"
                  onClick={onClose}
                  className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-600 hover:border-gray-400 hover:text-gray-700">
                  {isPageMode ? (t('previous') || 'Назад') : (t('cancel') || 'Отмена')}
                </button>
                <button
                  type="submit"
                  disabled={loading || readOnly}
                  className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300">
                  {loading && <Loader2 className="h-4 w-4 animate-spin" />}
                  {initial ? t('save') || 'Сохранить' : t('add') || 'Добавить'}
                </button>
              </div>
            </div>
          </div>
        </form>

        {pickerOpen && (
          <div className="fixed inset-0 z-[1100] flex items-center justify-center bg-black/50 p-4">
            <div className="w-full max-w-3xl rounded-xl bg-white shadow-2xl">
              <div className="flex items-center justify-between border-b px-4 py-3">
                <h3 className="text-lg font-semibold">Выбор клиента</h3>
                <button className="rounded border px-2 py-1 text-sm" onClick={() => setPickerOpen(false)}>
                  Закрыть
                </button>
              </div>
              <div className="p-4">
                <div className="relative mb-3">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="search"
                    value={pickerSearch}
                    onChange={(e) => setPickerSearch(e.target.value)}
                    className="w-full rounded-lg border border-slate-200 py-2 pl-10 pr-3"
                    placeholder={t('searchClientsPlaceholder') || 'Поиск клиентов'}
                  />
                </div>

                <div className="max-h-80 overflow-auto rounded border">
                  <table className="min-w-full divide-y text-sm">
                    <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                      <tr>
                        <th className="px-3 py-2 text-left">ФИО</th>
                        <th className="px-3 py-2 text-left">Компания</th>
                        <th className="px-3 py-2 text-right">Действие</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y">
                      {availableClients.map((client) => (
                        <tr key={client.id}>
                          <td className="px-3 py-2">{client.name}</td>
                          <td className="px-3 py-2">{client.company || '—'}</td>
                          <td className="px-3 py-2 text-right">
                            <button
                              type="button"
                              className="rounded border border-emerald-200 bg-emerald-50 px-2 py-1 text-xs text-emerald-700"
                              onClick={() => handleAddClient(client.id)}>
                              Добавить
                            </button>
                          </td>
                        </tr>
                      ))}
                      {availableClients.length === 0 && (
                        <tr>
                          <td colSpan={3} className="px-3 py-6 text-center text-slate-500">
                            Нет доступных клиентов
                          </td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
  );

  if (isPageMode) {
    return content;
  }

  return createPortal(
    <div className="fixed inset-0 z-[1000] flex items-center justify-center bg-black/50 p-4">
      {content}
    </div>,
    document.body,
  );
}
