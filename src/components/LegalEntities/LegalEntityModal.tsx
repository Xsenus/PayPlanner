import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  ArrowLeft,
  CheckCircle2,
  Loader2,
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
  LegalEntityDadataDetails,
  DadataAddressSuggestion,
  ActResponsible,
} from '../../types';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { ClientAddressEntriesForm, type ClientAddressEntryFormValue } from '../Clients/ClientAddressEntriesForm';
import { apiService } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useDictionaries } from '../../hooks/useDictionaries';
import DictionariesModal from '../Dictionaries/DictionariesModal';

interface LegalEntityModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (payload: LegalEntityInput) => Promise<LegalEntityDetail>;
  onDelete?: () => Promise<void>;
  initial?: LegalEntityDetail | null;
  clients: Client[];
  suggest: (payload: { query?: string; inn?: string; limit?: number }) => Promise<LegalEntitySuggestion[]>;
  findById: (payload: { query: string; kpp?: string }) => Promise<LegalEntityDadataDetails>;
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
  responsibleUserId: string;
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

type FinanceTab = 'bank' | 'cash' | 'cards';
type RequisitesTab = 'base' | 'tax' | 'codes' | 'treasury' | 'english' | 'addresses' | 'contacts';
type LegalEntityDictTab = 'countries' | 'base-currencies' | 'service-languages' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes';

const BACKEND_IMAGE_DATA_URL_MAX_LENGTH = 4_000_000;
const TARGET_IMAGE_DATA_URL_LENGTH = 3_900_000;

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

const ADDRESS_TYPE_OPTIONS = [
  { value: 'registration', label: 'Адрес регистрации' },
  { value: 'actual', label: 'Фактический адрес' },
  { value: 'legal', label: 'Юридический адрес' },
  { value: 'other', label: 'Иной адрес' },
] as const;

const DATE_INPUT_CLASS = 'w-full rounded-lg border border-gray-300 px-3 py-2';

const createEmptyLegalEntityAddressEntry = (): ClientAddressEntryFormValue => ({
  type: 'legal',
  additionalTypes: [],
  fullAddress: '',
  postalCode: '',
  region: '',
  district: '',
  city: '',
  settlement: '',
  street: '',
  house: '',
  flat: '',
  validFrom: '',
  validTo: '',
});

const mapAddressTypeToForm = (type: number): string => {
  if (type == 0) return 'registration';
  if (type == 1) return 'actual';
  if (type == 2) return 'legal';
  return 'other';
};

const mapAddressTypeFromForm = (type: string): number => {
  if (type == 'registration') return 0;
  if (type == 'actual') return 1;
  if (type == 'legal') return 2;
  return 3;
};

function readFileAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : '');
    reader.onerror = () => reject(new Error('Не удалось прочитать файл изображения.'));
    reader.readAsDataURL(file);
  });
}

function loadImageElement(dataUrl: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error('Не удалось обработать изображение.'));
    image.src = dataUrl;
  });
}

async function convertImageToFittingDataUrl(file: File, maxLength: number): Promise<string> {
  const original = await readFileAsDataUrl(file);
  if (original.length <= maxLength) return original;

  const image = await loadImageElement(original);
  const baseMaxSide = 1600;
  const maxSide = Math.max(image.width, image.height) || 1;
  const firstScale = Math.min(1, baseMaxSide / maxSide);
  const scales = [firstScale, firstScale * 0.85, firstScale * 0.72, firstScale * 0.6, firstScale * 0.5]
    .map((value) => Math.max(0.2, value));
  const qualities = [0.9, 0.82, 0.74, 0.66, 0.58, 0.5, 0.42];

  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d');
  if (!context) throw new Error('Браузер не поддерживает обработку изображений.');

  for (const scale of scales) {
    canvas.width = Math.max(1, Math.round(image.width * scale));
    canvas.height = Math.max(1, Math.round(image.height * scale));
    context.clearRect(0, 0, canvas.width, canvas.height);
    context.drawImage(image, 0, 0, canvas.width, canvas.height);

    for (const quality of qualities) {
      const candidate = canvas.toDataURL('image/jpeg', quality);
      if (candidate.length <= maxLength) {
        return candidate;
      }
    }
  }

  throw new Error('Изображение слишком большое даже после сжатия. Выберите файл меньшего размера.');
}

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
  responsibleUserId: '',
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
  findById,
  allowEdit,
  allowDelete,
  mode = 'modal',
  onBack,
}: LegalEntityModalProps) {
  const { t } = useTranslation();
  const { confirm, alert } = useDialog();

  const extractErrorMessage = (error: unknown, fallback: string) => {
    const raw = error instanceof Error ? error.message : String(error);
    const marker = ' - ';
    const markerIndex = raw.indexOf(marker);
    if (markerIndex >= 0) {
      const serverMessage = raw.slice(markerIndex + marker.length).trim();
      if (serverMessage) return serverMessage;
    }
    return raw || fallback;
  };
  const { countries, baseCurrencies, serviceLanguages, organizationForms, timezones, taxSystems, taxSystemModes } = useDictionaries();
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
  const [financeTab, setFinanceTab] = useState<FinanceTab>('bank');
  const [requisitesTab, setRequisitesTab] = useState<RequisitesTab>('base');
  const [addressEntries, setAddressEntries] = useState<ClientAddressEntryFormValue[]>([]);
  const [addressSuggestionsByIndex, setAddressSuggestionsByIndex] = useState<Record<number, DadataAddressSuggestion[]>>({});
  const addressSuggestionTimersRef = useRef<Record<number, number>>({});
  const [contactPhones, setContactPhones] = useState<string[]>([]);
  const [contactEmails, setContactEmails] = useState<string[]>([]);
  const [dictModalOpen, setDictModalOpen] = useState(false);
  const [dictModalTab, setDictModalTab] = useState<LegalEntityDictTab>('countries');
  const [saveToast, setSaveToast] = useState<string | null>(null);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);

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
        responsibleUserId: initial.responsibleUserId ? String(initial.responsibleUserId) : '',
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
      setAddressEntries((initial.addresses ?? []).map((a) => ({
        ...createEmptyLegalEntityAddressEntry(),
        type: mapAddressTypeToForm(a.type),
        fullAddress: a.value ?? '',
        postalCode: a.postalCode ?? '',
        region: a.regionWithType ?? '',
        city: a.cityWithType ?? '',
        street: a.streetWithType ?? '',
        house: a.house ?? '',
        flat: a.flat ?? '',
        additionalTypes: [],
      })));
      setContactPhones(parseJsonOrDefault<string[]>(initial.contactPhonesJson, initial.phone ? [initial.phone] : []));
      setContactEmails(parseJsonOrDefault<string[]>(initial.contactEmailsJson, initial.email ? [initial.email] : []));
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
      setAddressEntries([createEmptyLegalEntityAddressEntry()]);
      setAddressSuggestionsByIndex({});
      setContactPhones([]);
      setContactEmails([]);
    }
    setError(null);
    setSuggestions([]);
    setSuggestError(null);
    setDadataQuery('');
    setPickerOpen(false);
    setPickerSearch('');
  }, [initial, isOpen]);

  useEffect(() => () => {
    Object.values(addressSuggestionTimersRef.current).forEach((timerId) => window.clearTimeout(timerId));
  }, []);

  useEffect(() => {
    if (!saveToast) return;
    const timer = window.setTimeout(() => setSaveToast(null), 2600);
    return () => window.clearTimeout(timer);
  }, [saveToast]);

  useEffect(() => {
    if (!isOpen) return;
    void apiService.getActResponsibles().then((items) => setResponsibles(items ?? [])).catch(() => setResponsibles([]));
  }, [isOpen]);

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
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
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

  const handleImageUpload = async (
    file: File | null,
    setter: (value: string) => void,
  ) => {
    if (!file) return;
    setError(null);
    try {
      const optimized = await convertImageToFittingDataUrl(file, TARGET_IMAGE_DATA_URL_LENGTH);
      setter(optimized);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить изображение.');
    }
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


  const updateAddressFormEntry = (index: number, key: keyof ClientAddressEntryFormValue, value: string) => {
    setAddressEntries((prev) => prev.map((entry, i) => (i === index ? { ...entry, [key]: value } : entry)));
  };

  const toggleAdditionalAddressType = (index: number, type: string) => {
    setAddressEntries((prev) => prev.map((entry, i) => {
      if (i !== index) return entry;
      const exists = entry.additionalTypes.includes(type);
      return { ...entry, additionalTypes: exists ? entry.additionalTypes.filter((x) => x !== type) : [...entry.additionalTypes, type] };
    }));
  };

  const scheduleAddressSuggestions = (index: number, query: string) => {
    const timers = addressSuggestionTimersRef.current;
    if (timers[index]) window.clearTimeout(timers[index]);
    timers[index] = window.setTimeout(async () => {
      const normalized = query.trim();
      if (normalized.length < 3) {
        setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: [] }));
        return;
      }
      try {
        const items = await apiService.suggestClientAddresses(normalized, 7);
        setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: items ?? [] }));
      } catch {
        setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: [] }));
      }
    }, 350);
  };

  const fillAddressEntryByDaData = async (index: number, rawInput?: string) => {
    const source = (rawInput ?? addressEntries[index]?.fullAddress ?? '').trim();
    if (!source) return;
    const cleaned = await apiService.cleanAddress(source);
    setAddressEntries((prev) => prev.map((entry, i) => i === index ? ({
      ...entry,
      fullAddress: cleaned.result ?? source,
      postalCode: cleaned.postalCode ?? '',
      region: cleaned.regionWithType ?? '',
      district: '',
      city: cleaned.cityWithType ?? '',
      settlement: cleaned.settlementWithType ?? '',
      street: cleaned.streetWithType ?? '',
      house: cleaned.house ?? '',
      flat: cleaned.flat ?? '',
    }) : entry));
  };

  const applyAddressSuggestion = async (index: number, suggestion: DadataAddressSuggestion) => {
    const selected = (suggestion.unrestrictedValue ?? suggestion.value ?? '').trim();
    if (!selected) return;

    setAddressEntries((prev) => prev.map((entry, i) => i === index ? ({
      ...entry,
      fullAddress: selected,
      postalCode: suggestion.postalCode ?? '',
      region: suggestion.regionWithType ?? '',
      district: suggestion.areaWithType ?? '',
      city: suggestion.cityWithType ?? '',
      settlement: suggestion.settlementWithType ?? '',
      street: suggestion.streetWithType ?? '',
      house: suggestion.house ?? '',
      flat: suggestion.flat ?? '',
    }) : entry));
    setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: [] }));

    try {
      await fillAddressEntryByDaData(index, selected);
    } catch {
      // fallback from suggestion above is already applied
    }
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

  const applySuggestion = async (suggestion: LegalEntitySuggestion) => {
    const baseAddress = suggestion.address?.trim();

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

    if (baseAddress) {
      setAddressEntries((prev) => {
        if (prev.some((entry) => entry.fullAddress.trim() === baseAddress)) {
          return prev;
        }
        return [{
          ...createEmptyLegalEntityAddressEntry(),
          type: 'legal',
          fullAddress: baseAddress,
        }, ...prev];
      });
    }

    const query = (suggestion.inn || suggestion.ogrn || dadataQuery).trim();
    if (!query) return;

    try {
      const details = await findById({ query, kpp: suggestion.kpp ?? undefined });
      const normalizedAddress = (details.addressUnrestricted || details.address || '').trim();

      setFormState((prev) => ({
        ...prev,
        shortName: details.shortName || prev.shortName,
        fullName: details.fullName ?? prev.fullName,
        inn: details.inn ?? prev.inn,
        kpp: details.kpp ?? prev.kpp,
        ogrn: details.ogrn ?? prev.ogrn,
        address: normalizedAddress || prev.address,
        director: details.director ?? prev.director,
        country: details.country ?? prev.country,
        organizationForm: details.opfShort ?? details.opfFull ?? prev.organizationForm,
        taxSystem: details.taxSystem ?? prev.taxSystem,
        okved: details.okved ?? prev.okved,
        okpo: details.okpo ?? prev.okpo,
        okato: details.okato ?? prev.okato,
        oktmo: details.oktmo ?? prev.oktmo,
        okogu: details.okogu ?? prev.okogu,
        okfs: details.okfs ?? prev.okfs,
      }));

      if (details.phones && details.phones.length > 0) {
        setContactPhones((prev) => Array.from(new Set([...prev, ...details.phones!.filter((x) => x.trim())])));
      }
      if (details.emails && details.emails.length > 0) {
        setContactEmails((prev) => Array.from(new Set([...prev, ...details.emails!.filter((x) => x.trim())])));
      }

      if (normalizedAddress) {
        setAddressEntries((prev) => {
          const existingIndex = prev.findIndex((entry) =>
            entry.fullAddress.trim() === normalizedAddress ||
            entry.fullAddress.trim() === (details.address ?? '').trim());

          if (existingIndex >= 0) {
            return prev.map((entry, idx) => (idx === existingIndex ? {
              ...entry,
              fullAddress: normalizedAddress,
              postalCode: details.postalCode ?? entry.postalCode,
              region: details.regionWithType ?? entry.region,
              city: details.cityWithType ?? entry.city,
              street: details.streetWithType ?? entry.street,
              house: details.house ?? entry.house,
              flat: details.flat ?? entry.flat,
            } : entry));
          }

          return [{
            ...createEmptyLegalEntityAddressEntry(),
            type: 'legal',
            fullAddress: normalizedAddress,
            postalCode: details.postalCode ?? '',
            region: details.regionWithType ?? '',
            city: details.cityWithType ?? '',
            street: details.streetWithType ?? '',
            house: details.house ?? '',
            flat: details.flat ?? '',
          }, ...prev];
        });
      }
    } catch {
      // fallback to suggest data already applied
    }
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
      if (logoDataUrl.length > BACKEND_IMAGE_DATA_URL_MAX_LENGTH || sealDataUrl.length > BACKEND_IMAGE_DATA_URL_MAX_LENGTH || directorSignatureDataUrl.length > BACKEND_IMAGE_DATA_URL_MAX_LENGTH) {
        setError('Изображение превышает допустимый размер на сервере. Выберите файл меньшего размера.');
        setLoading(false);
        return;
      }

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
        responsibleUserId: formState.responsibleUserId ? Number(formState.responsibleUserId) : null,
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
        contactPhonesJson: JSON.stringify(contactPhones.filter((x) => x.trim())),
        contactEmailsJson: JSON.stringify(contactEmails.filter((x) => x.trim())),
        notes: normalize(formState.notes),
        addresses: addressEntries.filter((a) => a.fullAddress.trim()).map((a, idx) => ({ type: mapAddressTypeFromForm(a.type), value: a.fullAddress.trim(), isPrimary: idx === 0 })),
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
        responsibleUserId: saved.responsibleUserId ? String(saved.responsibleUserId) : prev.responsibleUserId,
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
      setAddressEntries((saved.addresses ?? []).map((a) => ({ ...createEmptyLegalEntityAddressEntry(), type: mapAddressTypeToForm(a.type), fullAddress: a.value ?? '', postalCode: a.postalCode ?? '', region: a.regionWithType ?? '', city: a.cityWithType ?? '', street: a.streetWithType ?? '', house: a.house ?? '', flat: a.flat ?? '' })));
      setContactPhones(parseJsonOrDefault<string[]>(saved.contactPhonesJson, contactPhones));
      setContactEmails(parseJsonOrDefault<string[]>(saved.contactEmailsJson, contactEmails));
      setSaveToast('Юридическое лицо успешно сохранено.');
      if (!isPageMode) {
        onClose();
      }
    } catch (err) {
      const message = extractErrorMessage(err, t('saveError') || 'Не удалось сохранить');
      setError(message);
      await alert({
        title: 'Ошибка сохранения',
        description: message,
        variant: 'error',
      });
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
      const message = extractErrorMessage(err, t('legalEntityDeleteError') || 'Не удалось удалить запись');
      setError(message);
      await alert({
        title: 'Удаление недоступно',
        description: message,
        variant: 'error',
      });
    } finally {
      setDeleting(false);
    }
  };

  const clientsSection = (
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
  );

  const dadataSection = (
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
            onClick={() => { void applySuggestion(suggestion); }}
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
  );


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

        {saveToast && (
          <div className="fixed right-6 top-6 z-[1200] rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm font-medium text-emerald-700 shadow-lg">
            {saveToast}
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
              <div className="grid gap-4 md:grid-cols-2">
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
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">Ответственный</label>
                  <select
                    name="responsibleUserId"
                    value={formState.responsibleUserId}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500">
                    <option value="">Не выбрано</option>
                    {responsibles.map((item) => (
                      <option key={item.id} value={item.id}>{item.fullName}</option>
                    ))}
                  </select>
                </div>
              </div>
            </div>
            {dadataSection}
          </div>
          <div className="rounded-2xl border border-slate-200 bg-white shadow-sm overflow-hidden">
            <div className="border-b border-slate-200 px-6 pt-4">
              <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">Реквизиты компании</h3>
              <div className="mt-3 flex flex-wrap gap-5">
                {([
                  { id: 'base', label: 'Основные' },
                  { id: 'tax', label: 'Налогообложение' },
                  { id: 'codes', label: 'Коды и фонды' },
                  { id: 'treasury', label: 'Реквизиты ЕНС' },
                  { id: 'english', label: 'Реквизиты на английском' },
                  { id: 'addresses', label: 'Адреса' },
                  { id: 'contacts', label: 'Контакты' },
                ] as Array<{ id: RequisitesTab; label: string }>).map((tab) => (
                  <button
                    key={tab.id}
                    type="button"
                    onClick={() => setRequisitesTab(tab.id)}
                    className={`pb-3 text-sm font-semibold transition ${
                      requisitesTab === tab.id
                        ? 'border-b-2 border-blue-600 text-blue-600'
                        : 'text-slate-500 hover:text-slate-700'
                    }`}>
                    {tab.label}
                  </button>
                ))}
              </div>
            </div>
            <div className="p-6">
              {requisitesTab === 'base' && (
                <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                  {[
                    { key: 'country', label: 'Страна', dict: 'countries', options: countries },
                    { key: 'baseCurrency', label: 'Базовая валюта', dict: 'base-currencies', options: baseCurrencies },
                    { key: 'serviceLanguage', label: 'Язык сервиса', dict: 'service-languages', options: serviceLanguages },
                    { key: 'organizationForm', label: 'Форма организации', dict: 'organization-forms', options: organizationForms },
                    { key: 'timezone', label: 'Часовой пояс', dict: 'timezones', options: timezones },
                  ].map((item) => {
                    const value = (formState as Record<string, string | boolean>)[item.key] as string;
                    const hasCurrent = !!value && item.options.some((opt) => opt.name === value);
                    return (
                      <div key={item.key}>
                        <div className="mb-2 flex items-center justify-between gap-2">
                          <label className="block text-sm font-medium text-gray-700">{item.label}</label>
                          <button
                            type="button"
                            onClick={() => {
                              setDictModalTab(item.dict as LegalEntityDictTab);
                              setDictModalOpen(true);
                            }}
                            className="text-xs font-medium text-blue-600 hover:text-blue-700">
                            Справочник
                          </button>
                        </div>
                        <select
                          name={item.key}
                          value={value}
                          onChange={handleInputChange}
                          disabled={readOnly}
                          className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500">
                          <option value="">Не выбрано</option>
                          {!hasCurrent && value ? <option value={value}>{value}</option> : null}
                          {item.options.map((opt) => (
                            <option key={opt.id} value={opt.name}>{opt.name}</option>
                          ))}
                        </select>
                      </div>
                    );
                  })}
                </div>
              )}

              {requisitesTab === 'tax' && (
                <div className="grid gap-4 md:grid-cols-3">
                  <div>
                    <div className="mb-2 flex items-center justify-between gap-2">
                      <label className="block text-sm font-medium text-gray-700">Система налогообложения</label>
                      <button type="button" onClick={() => { setDictModalTab('tax-systems'); setDictModalOpen(true); }} className="text-xs font-medium text-blue-600 hover:text-blue-700">Справочник</button>
                    </div>
                    <select name="taxSystem" value={formState.taxSystem} onChange={handleInputChange} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500">
                      <option value="">Не выбрано</option>
                      {!taxSystems.some((x) => x.name === formState.taxSystem) && formState.taxSystem ? <option value={formState.taxSystem}>{formState.taxSystem}</option> : null}
                      {taxSystems.map((x) => <option key={x.id} value={x.name}>{x.name}</option>)}
                    </select>
                  </div>
                  <div>
                    <div className="mb-2 flex items-center justify-between gap-2">
                      <label className="block text-sm font-medium text-gray-700">Режим налогообложения</label>
                      <button type="button" onClick={() => { setDictModalTab('tax-system-modes'); setDictModalOpen(true); }} className="text-xs font-medium text-blue-600 hover:text-blue-700">Справочник</button>
                    </div>
                    <select name="taxSystemMode" value={formState.taxSystemMode} onChange={handleInputChange} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500">
                      <option value="">Не выбрано</option>
                      {!taxSystemModes.some((x) => x.name === formState.taxSystemMode) && formState.taxSystemMode ? <option value={formState.taxSystemMode}>{formState.taxSystemMode}</option> : null}
                      {taxSystemModes.map((x) => <option key={x.id} value={x.name}>{x.name}</option>)}
                    </select>
                  </div>
                  {[
                    ['incomeTaxRate', 'Ставка (доход)'],
                    ['incomeExpenseTaxRate', 'Ставка (доход-расход)'],
                    ['charterCapital', 'Уставный капитал'],
                    ['taxRegistrationDate', 'Дата постановки на учет'],
                    ['taxOfficeCode', 'Код налоговой'],
                    ['taxRegion', 'Регион налоговой'],
                    ['ifnsName', 'Наименование ИФНС'],
                    ['taxOfficePhone', 'Телефон налоговой'],
                  ].map(([name, label]) => (
                    <div key={name as string}>
                      <label className="mb-2 block text-sm font-medium text-gray-700">{label}</label>
                      <input type="text" name={name as string} value={(formState as Record<string, string | boolean>)[name as string] as string} onChange={handleInputChange} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500" />
                    </div>
                  ))}
                  <label className="flex items-center gap-2 text-sm text-gray-700">
                    <input type="checkbox" name="vatWithoutVat" checked={formState.vatWithoutVat} onChange={handleInputChange} disabled={readOnly} className="h-4 w-4" />
                    Без НДС
                  </label>
                </div>
              )}

              {requisitesTab === 'codes' && (
                <div className="grid gap-4 md:grid-cols-3">
                  {[
                    ['okved', 'ОКВЭД'],
                    ['okpo', 'ОКПО'],
                    ['okato', 'ОКАТО'],
                    ['oktmo', 'ОКТМО'],
                    ['okogu', 'ОКОГУ'],
                    ['okfs', 'ОКФС'],
                    ['okopf', 'ОКОПФ'],
                    ['pfr', 'ПФР'],
                    ['fss', 'ФСС'],
                  ].map(([name, label]) => (
                    <div key={name as string}>
                      <label className="mb-2 block text-sm font-medium text-gray-700">{label}</label>
                      <input type="text" name={name as string} value={(formState as Record<string, string | boolean>)[name as string] as string} onChange={handleInputChange} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500" />
                    </div>
                  ))}
                </div>
              )}

              {requisitesTab === 'treasury' && (
                <div className="grid gap-4 md:grid-cols-3">
                  {[
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
                      <input type="text" name={name as string} value={(formState as Record<string, string | boolean>)[name as string] as string} onChange={handleInputChange} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500" />
                    </div>
                  ))}
                </div>
              )}



              {requisitesTab === 'addresses' && (
                <ClientAddressEntriesForm
                  entries={addressEntries}
                  canEdit={!readOnly}
                  dateInputClassName={DATE_INPUT_CLASS}
                  addressTypeOptions={[...ADDRESS_TYPE_OPTIONS]}
                  suggestionsByIndex={addressSuggestionsByIndex}
                  onUpdateEntry={updateAddressFormEntry}
                  onRemoveEntry={(index) => setAddressEntries((prev) => prev.length > 1 ? prev.filter((_, i) => i !== index) : [createEmptyLegalEntityAddressEntry()])}
                  onAddEntry={() => setAddressEntries((prev) => [...prev, createEmptyLegalEntityAddressEntry()])}
                  onToggleAdditionalType={toggleAdditionalAddressType}
                  onScheduleSuggestions={scheduleAddressSuggestions}
                  onFindAddress={(index) => { void fillAddressEntryByDaData(index); }}
                  onApplySuggestion={applyAddressSuggestion}
                />
              )}

              {requisitesTab === 'contacts' && (
                <div className="grid gap-6 md:grid-cols-2">
                  <div>
                    <div className="mb-2 text-sm font-medium text-gray-700">Телефоны</div>
                    <button type="button" onClick={() => setContactPhones((prev) => [...prev, ''])} disabled={readOnly} className="mb-2 rounded-xl border border-blue-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50">+ Добавить телефон</button>
                    <div className="space-y-2">
                      {contactPhones.map((v, idx) => <div key={`ph-${idx}`} className="flex gap-2"><input value={v} disabled={readOnly} onChange={(e) => setContactPhones((prev) => prev.map((x, i) => i===idx?e.target.value:x))} className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" /><button type="button" disabled={readOnly} onClick={() => setContactPhones((prev) => prev.filter((_, i) => i!==idx))} className="rounded-xl border border-rose-200 px-3 text-rose-600">✕</button></div>)}
                    </div>
                  </div>
                  <div>
                    <div className="mb-2 text-sm font-medium text-gray-700">Email</div>
                    <button type="button" onClick={() => setContactEmails((prev) => [...prev, ''])} disabled={readOnly} className="mb-2 rounded-xl border border-blue-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50">+ Добавить email</button>
                    <div className="space-y-2">
                      {contactEmails.map((v, idx) => <div key={`em-${idx}`} className="flex gap-2"><input value={v} disabled={readOnly} onChange={(e) => setContactEmails((prev) => prev.map((x, i) => i===idx?e.target.value:x))} className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" /><button type="button" disabled={readOnly} onClick={() => setContactEmails((prev) => prev.filter((_, i) => i!==idx))} className="rounded-xl border border-rose-200 px-3 text-rose-600">✕</button></div>)}
                    </div>
                  </div>
                </div>
              )}

              {requisitesTab === 'english' && (
                <div className="grid gap-4 md:grid-cols-3">
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Название организации" value={englishRequisites.organizationName} onChange={(e) => updateEnglishRequisites('organizationName', e.target.value)} disabled={readOnly} />
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Полное наименование" value={englishRequisites.fullName} onChange={(e) => updateEnglishRequisites('fullName', e.target.value)} disabled={readOnly} />
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Форма" value={englishRequisites.form} onChange={(e) => updateEnglishRequisites('form', e.target.value)} disabled={readOnly} />
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm md:col-span-2" placeholder="Юридический адрес" value={englishRequisites.legalAddress} onChange={(e) => updateEnglishRequisites('legalAddress', e.target.value)} disabled={readOnly} />
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Фактический адрес" value={englishRequisites.actualAddress} onChange={(e) => updateEnglishRequisites('actualAddress', e.target.value)} disabled={readOnly} />
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Должность" value={englishRequisites.position} onChange={(e) => updateEnglishRequisites('position', e.target.value)} disabled={readOnly} />
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Фамилия" value={englishRequisites.lastName} onChange={(e) => updateEnglishRequisites('lastName', e.target.value)} disabled={readOnly} />
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Имя" value={englishRequisites.firstName} onChange={(e) => updateEnglishRequisites('firstName', e.target.value)} disabled={readOnly} />
                  <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Отчество" value={englishRequisites.middleName} onChange={(e) => updateEnglishRequisites('middleName', e.target.value)} disabled={readOnly} />
                </div>
              )}
            </div>
          </div>

          {clientsSection}

          <div className="mt-6 grid gap-6 lg:grid-cols-3">
            <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
              <div className="text-sm font-semibold text-slate-700">Логотип компании</div>
              <div className="mt-4 rounded-xl border-2 border-dashed border-blue-400 px-4 py-6 text-center text-sm text-blue-600">
                <input type="file" accept="image/*" className="mx-auto block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-blue-50 file:px-3 file:py-2 file:text-blue-700" disabled={readOnly} onChange={(e) => { void handleImageUpload(e.target.files?.[0] ?? null, setLogoDataUrl); }} />
                {logoDataUrl ? <img src={logoDataUrl} alt="Логотип" className="mx-auto mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1" /> : <><div className="mt-2 font-semibold">Выберите файлы</div><div className="text-xs text-slate-400">или перетащите сюда.</div></>}
              </div>
              <div className="mt-4 text-xs text-slate-400">Рекомендуемый размер для загрузки: 545x185. Форматы: JPG, JPEG, PNG.</div>
            </div>
            <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
              <div className="text-sm font-semibold text-slate-700">Печать компании</div>
              <div className="mt-4 rounded-xl border-2 border-dashed border-slate-200 px-4 py-6 text-center text-sm text-slate-400">
                <input type="file" accept="image/*" className="mx-auto block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-slate-700" disabled={readOnly} onChange={(e) => { void handleImageUpload(e.target.files?.[0] ?? null, setSealDataUrl); }} />
                {sealDataUrl ? <img src={sealDataUrl} alt="Печать" className="mx-auto mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1" /> : <><div className="mt-2 font-semibold">Выберите файлы</div><div className="text-xs text-slate-400">или перетащите сюда.</div></>}
              </div>
              <div className="mt-4 text-xs text-slate-400">Рекомендуемый размер для загрузки: 200x200. Форматы: JPG, JPEG, PNG.</div>
            </div>
            <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
              <div className="text-sm font-semibold text-slate-700">Подпись руководителя</div>
              <div className="mt-4 rounded-xl border-2 border-dashed border-slate-200 px-4 py-6 text-center text-sm text-slate-400">
                <input type="file" accept="image/*" className="mx-auto block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-slate-700" disabled={readOnly} onChange={(e) => { void handleImageUpload(e.target.files?.[0] ?? null, setDirectorSignatureDataUrl); }} />
                {directorSignatureDataUrl ? <img src={directorSignatureDataUrl} alt="Подпись" className="mx-auto mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1" /> : <><div className="mt-2 font-semibold">Выберите файлы</div><div className="text-xs text-slate-400">или перетащите сюда.</div></>}
              </div>
              <div className="mt-4 text-xs text-slate-400">Рекомендуемый размер для загрузки: 165x50. Форматы: JPG, JPEG, PNG.</div>
            </div>
          </div>

          <div className="mt-6 grid gap-6 lg:grid-cols-2">
            <div className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
              <h3 className="text-2xl font-semibold text-slate-900">Руководитель (для документов)</h3>
              <div className="mt-4 grid gap-4">
                <label className="block text-sm text-slate-600"><span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">Должность</span><input className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Генеральный директор" value={documentDirector.position} onChange={(e) => updatePersonInfo(setDocumentDirector, 'position', e.target.value)} disabled={readOnly} /></label>
                <label className="block text-sm text-slate-600"><span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">Фамилия</span><input className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Швыдченко" value={documentDirector.lastName} onChange={(e) => updatePersonInfo(setDocumentDirector, 'lastName', e.target.value)} disabled={readOnly} /></label>
                <label className="block text-sm text-slate-600"><span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">Имя</span><input className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Сергей" value={documentDirector.firstName} onChange={(e) => updatePersonInfo(setDocumentDirector, 'firstName', e.target.value)} disabled={readOnly} /></label>
                <label className="block text-sm text-slate-600"><span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">Отчество</span><input className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Николаевич" value={documentDirector.middleName} onChange={(e) => updatePersonInfo(setDocumentDirector, 'middleName', e.target.value)} disabled={readOnly} /></label>
                <label className="flex items-center gap-2 text-base text-slate-700"><input type="checkbox" checked={documentDirector.noMiddleName} onChange={(e) => updatePersonInfo(setDocumentDirector, 'noMiddleName', e.target.checked)} disabled={readOnly} className="h-5 w-5" /> Нет отчества</label>
              </div>
            </div>
            <div className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
              <h3 className="text-2xl font-semibold text-slate-900">Главный бухгалтер (для документов)</h3>
              <div className="mt-4 flex items-center justify-between text-base text-slate-700"><span>Совпадает с руководителем</span><label className="flex items-center gap-2"><input type="checkbox" checked={false} readOnly className="h-5 w-5" /> ✓ Активно</label></div>
              <div className="mt-4 grid gap-4">
                <label className="block text-sm text-slate-600"><span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">Фамилия</span><input className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Швыдченко" value={chiefAccountant.lastName} onChange={(e) => updatePersonInfo(setChiefAccountant, 'lastName', e.target.value)} disabled={readOnly} /></label>
                <label className="block text-sm text-slate-600"><span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">Имя</span><input className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Сергей" value={chiefAccountant.firstName} onChange={(e) => updatePersonInfo(setChiefAccountant, 'firstName', e.target.value)} disabled={readOnly} /></label>
                <label className="block text-sm text-slate-600"><span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">Отчество</span><input className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder="Николаевич" value={chiefAccountant.middleName} onChange={(e) => updatePersonInfo(setChiefAccountant, 'middleName', e.target.value)} disabled={readOnly} /></label>
                <label className="flex items-center gap-2 text-base text-slate-700"><input type="checkbox" checked={chiefAccountant.noMiddleName} onChange={(e) => updatePersonInfo(setChiefAccountant, 'noMiddleName', e.target.checked)} disabled={readOnly} className="h-5 w-5" /> Нет отчества</label>
              </div>
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

        <DictionariesModal
          open={dictModalOpen}
          onClose={() => setDictModalOpen(false)}
          initialTab={dictModalTab}
          onlyTab={dictModalTab}
          onPickItem={(kind, item) => {
            const map: Record<string, keyof FormState> = {
              'countries': 'country',
              'base-currencies': 'baseCurrency',
              'service-languages': 'serviceLanguage',
              'organization-forms': 'organizationForm',
              'timezones': 'timezone',
              'tax-systems': 'taxSystem',
              'tax-system-modes': 'taxSystemMode',
            };
            const target = map[kind];
            if (target) {
              setFormState((prev) => ({ ...prev, [target]: item.name }));
            }
            setDictModalOpen(false);
          }}
        />


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
