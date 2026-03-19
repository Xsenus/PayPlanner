import {
  ArrowLeft,
  Ban,
  Users,
  TrendingUp,
  TrendingDown,
  Calendar as CalendarIcon,
  CheckCircle,
  Clock,
  AlertTriangle,
  CalendarRange,
  Loader2,
  Plus,
  Edit,
  GripVertical,
  Trash2,
  RotateCcw,
  Search,
  Settings2,
  WalletCards,
  CheckCircle2,
  Send,
  FileCheck2,
  FileSignature,
  Calculator,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useClientCases } from '../../hooks/useClientCases';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useActs, type ActsSortKey } from '../../hooks/useActs';
import { useInvoices, type InvoicesSortKey } from '../../hooks/useInvoices';
import { useContracts, type ContractsSortKey } from '../../hooks/useContracts';
import { usePaymentsPaged } from '../../hooks/usePaymentsPaged';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { apiService } from '../../services/api';
import { useTranslation } from '../../hooks/useTranslation';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { PaymentModal } from '../Calendar/PaymentModal';
import type {
  Act,
  ActInput,
  ActResponsible,
  ActStatus,
  Client,
  ClientCase,
  ClientInput,
  ClientReferenceItem,
  Contract,
  ContractClient,
  ContractInput,
  DadataAddressSuggestion,
  Invoice,
  InvoiceInput,
  Payment,
  PaymentPayload,
  PaymentStatus,
  PowerOfAttorney,
} from '../../types';
import type { MenuSectionKey } from '../../types/permissions';
import { toRuDate, formatLocalYMD } from '../../utils/dateUtils';
import { MonthRangePicker } from '../MonthRange/MonthRangePicker';
import { CaseModal } from './CaseModal';
import { TwoTypeStats } from '../Statistics/TwoTypeStats';
import { formatCurrencySmart } from '../../utils/formatters';
import { ActModal } from '../Acts/ActModal';
import { InvoiceModal } from '../Accounts/InvoiceModal';
import { ContractModal } from '../Contracts/ContractModal';
import { FormPromptModal } from '../ui/FormPromptModal';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { useClientStatuses } from '../../hooks/useClientStatuses';
import { ClientStatusBadge } from './ClientStatusBadge';
import {
  ClientAddressEntriesForm,
  type AddressTypeOption,
  type ClientAddressEntryFormValue,
} from './ClientAddressEntriesForm';

interface ClientDetailProps {
  clientId: number;
  onBack: () => void;
  onClientUpdated?: (client: Client) => void;
  initialCaseId?: number | 'all';
}

type PaymentUpsert = PaymentPayload | ({ id: number } & PaymentPayload);

type NormalizedStatus = 'completed' | 'pending' | 'overdue';

type ClientDetailSection =
  | 'profile'
  | 'payments'
  | 'accounts'
  | 'acts'
  | 'contracts'
  | 'settlement';

type PaymentTableColumnKey =
  | 'period'
  | 'amountType'
  | 'category'
  | 'caseContact'
  | 'details'
  | 'status';

type InvoiceTableColumnKey =
  | 'date'
  | 'number'
  | 'amount'
  | 'status'
  | 'dueDate'
  | 'responsible'
  | 'createdAt';

type ActTableColumnKey =
  | 'date'
  | 'number'
  | 'title'
  | 'amount'
  | 'status'
  | 'responsible'
  | 'counterpartyInn';

type ContractTableColumnKey =
  | 'number'
  | 'date'
  | 'clients'
  | 'amount'
  | 'validUntil'
  | 'createdAt';

// Временно закомментировано: вкладки/документы профиля, чтобы не ломать lint до возвращения UI.
// type ProfileTabKey =
//   | 'phone'
//   | 'email'
//   | 'address'
//   | 'passport'
//   | 'birthCertificate'
//   | 'militaryId'
//   | 'notes';

// type ProfileDocumentsState = {
//   passport: string;
//   birthCertificate: string;
//   militaryId: string;
// };

const SECTION_ORDER: ClientDetailSection[] = [
  'profile',
  'payments',
  'accounts',
  'acts',
  'contracts',
  'settlement',
];

const DETAIL_PAGE_SIZE_OPTIONS = [10, 25, 50, 100];
const CLIENT_DETAIL_TABLE_STORAGE_PREFIX = 'pp.client_detail_table';

const PAYMENT_TABLE_COLUMNS: Array<{
  key: PaymentTableColumnKey;
  label: string;
  defaultWidth: number;
}> = [
  { key: 'period', label: 'Период', defaultWidth: 210 },
  { key: 'amountType', label: 'Сумма и тип', defaultWidth: 230 },
  { key: 'category', label: 'Категория / источник', defaultWidth: 250 },
  { key: 'caseContact', label: 'Дело и контакт', defaultWidth: 240 },
  { key: 'details', label: 'Документы и комментарии', defaultWidth: 300 },
  { key: 'status', label: 'Статус и прогресс', defaultWidth: 240 },
];

const INVOICE_TABLE_COLUMNS: Array<{
  key: InvoiceTableColumnKey;
  label: string;
  defaultWidth: number;
}> = [
  { key: 'date', label: 'Дата', defaultWidth: 140 },
  { key: 'number', label: 'Номер счета', defaultWidth: 240 },
  { key: 'amount', label: 'Сумма', defaultWidth: 160 },
  { key: 'status', label: 'Статус', defaultWidth: 170 },
  { key: 'dueDate', label: 'Срок оплаты', defaultWidth: 170 },
  { key: 'responsible', label: 'Ответственный', defaultWidth: 180 },
  { key: 'createdAt', label: 'Создан', defaultWidth: 150 },
];

const ACT_TABLE_COLUMNS: Array<{
  key: ActTableColumnKey;
  label: string;
  defaultWidth: number;
}> = [
  { key: 'date', label: 'Дата', defaultWidth: 140 },
  { key: 'number', label: 'Номер', defaultWidth: 180 },
  { key: 'title', label: 'Название', defaultWidth: 260 },
  { key: 'amount', label: 'Сумма', defaultWidth: 160 },
  { key: 'status', label: 'Статус', defaultWidth: 160 },
  { key: 'responsible', label: 'Ответственный', defaultWidth: 180 },
  { key: 'counterpartyInn', label: 'ИНН', defaultWidth: 160 },
];

const CONTRACT_TABLE_COLUMNS: Array<{
  key: ContractTableColumnKey;
  label: string;
  defaultWidth: number;
}> = [
  { key: 'number', label: 'Номер договора', defaultWidth: 180 },
  { key: 'date', label: 'Дата договора', defaultWidth: 150 },
  { key: 'clients', label: 'Клиенты', defaultWidth: 260 },
  { key: 'amount', label: 'Сумма договора', defaultWidth: 170 },
  { key: 'validUntil', label: 'Действует до', defaultWidth: 170 },
  { key: 'createdAt', label: 'Создано', defaultWidth: 150 },
];

const SECTION_META: Record<
  ClientDetailSection,
  {
    label: string;
    actionLabel: string;
    placeholderTitle?: string;
    placeholderDescription?: string;
    icon?: LucideIcon | null;
  }
> = {
  profile: {
    label: 'Карточка клиента',
    actionLabel: 'Сохранить',
  },
  payments: {
    label: 'Платежи',
    actionLabel: 'Добавить платёж',
  },
  accounts: {
    label: 'Счета',
    actionLabel: 'Добавить счёт',
    placeholderTitle: 'Счета клиента',
    placeholderDescription: 'Здесь появится список счетов клиента после подключения модуля.',
    icon: WalletCards,
  },
  acts: {
    label: 'Акты',
    actionLabel: 'Добавить акт',
    placeholderTitle: 'Документы клиента',
    placeholderDescription: 'Управление документами будет доступно после доработки раздела.',
    icon: FileCheck2,
  },
  contracts: {
    label: 'Договоры',
    actionLabel: 'Добавить договор',
    placeholderTitle: 'Договоры клиента',
    placeholderDescription: 'Список договоров появится в этом разделе.',
    icon: FileSignature,
  },
  settlement: {
    label: 'Расчёт дела',
    actionLabel: 'Расчёт дела',
    placeholderTitle: 'Расчёт дела',
    placeholderDescription: 'Инструменты расчёта будут добавлены на следующем этапе.',
    icon: Calculator,
  },
};

const SECTION_PERMISSION_MAP: Record<ClientDetailSection, MenuSectionKey> = {
  profile: 'clients',
  payments: 'clients',
  accounts: 'accounts',
  acts: 'acts',
  contracts: 'contracts',
  settlement: 'clients',
};

const ACT_STATUS_ORDER: ActStatus[] = ['Created', 'Transferred', 'Signed', 'Terminated'];

const ACT_STATUS_CLASSES: Record<ActStatus, string> = {
  Created: 'bg-slate-100 text-slate-700 border border-slate-200',
  Transferred: 'bg-amber-100 text-amber-800 border border-amber-200',
  Signed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
  Terminated: 'bg-rose-100 text-rose-700 border border-rose-200',
};

function normalizeStatus(s?: string): NormalizedStatus {
  const v = (s ?? '').toLowerCase();
  if (v.includes('complete') || v.includes('выполн')) return 'completed';
  if (v.includes('overdue') || v.includes('проср')) return 'overdue';
  return 'pending';
}

function ymStart(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1));
}
function ymEnd(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return formatLocalYMD(new Date(y, m ?? 1, 0));
}

function caseStatusLabel(s?: string) {
  const v = (s ?? '').toLowerCase();
  if (v.includes('open')) return 'Открыто';
  if (v.includes('hold')) return 'Приостановлено';
  if (v.includes('closed')) return 'Закрыто';
  return s ?? '';
}

function toStatsStatusFilter(
  s: 'all' | NormalizedStatus,
): 'All' | 'Pending' | 'Completed' | 'Overdue' {
  switch (s) {
    case 'completed':
      return 'Completed';
    case 'overdue':
      return 'Overdue';
    case 'pending':
      return 'Pending';
    default:
      return 'All';
  }
}

function toRuPaymentStatus(status?: string, fallback: string = '—'): string {
  if (!status) return fallback;
  const normalized = status.toLowerCase();
  if (normalized.includes('overdue') || normalized.includes('проср')) return 'Просрочено';
  if (normalized.includes('complete') || normalized.includes('оплач')) return 'Оплачено';
  if (normalized.includes('process')) return 'В обработке';
  if (normalized.includes('cancel')) return 'Отменено';
  if (normalized.includes('pending') || normalized.includes('ожид')) return 'Ожидается';
  if (normalized.includes('draft')) return 'Черновик';
  if (normalized.includes('schedule')) return 'Запланировано';
  if (normalized.includes('fail') || normalized.includes('error')) return 'Ошибка';
  return fallback;
}

function toPaymentStatusFilter(s: 'all' | NormalizedStatus): PaymentStatus | undefined {
  switch (s) {
    case 'completed':
      return 'Completed';
    case 'overdue':
      return 'Overdue';
    case 'pending':
      return 'Pending';
    default:
      return undefined;
  }
}

const MIN_DATE = '1900-01-01';
const MAX_DATE = '2100-12-31';

type ClientKind = 'individual' | 'legal';

type ClientDocumentKey = 'passports' | 'birthCertificates' | 'militaryIds';

type ClientGender = 'male' | 'female' | '';
type ClientCitizenship = string;
type ClientMaritalStatus = 'never_married' | 'married' | 'divorced' | 'widowed' | 'civil_union' | '';

interface ClientEducationItem {
  institution: string;
  specialty: string;
  diplomaNumber: string;
  oksoCode: string;
}

interface ClientProfessionItem {
  profession: string;
  okpdtrCode: string;
}

interface ClientWorkplaceItem {
  organizationName: string;
  position: string;
  startDate: string;
  endDate: string;
}

interface ClientRelativeItem {
  relation: string;
  fullName: string;
  birthYear: string;
}

interface ClientBirthCertificateItem {
  series: string;
  number: string;
  issueDate: string;
  issuedBy: string;
  actRecordNumber: string;
  childFullName: string;
}

interface ClientMilitaryIdItem {
  series: string;
  number: string;
  issueDate: string;
  issuedBy: string;
  category: string;
  rank: string;
  vusCode: string;
  fitnessCategory: string;
  militaryCommissariat: string;
}

interface ClientPassportItem {
  series: string;
  number: string;
  issuedBy: string;
  issueDate: string;
  departmentCode: string;
  birthPlacePassport: string;
  birthPlaceFias: string;
  validFrom: string;
  validTo: string;
}

interface ClientAddressItem extends ClientAddressEntryFormValue {
  sourceQuery: string;
  fiasId: string;
  kladrId: string;
  geoLat: string;
  geoLon: string;
}

interface ClientExtendedProfile {
  kind: ClientKind;
  lastName: string;
  firstName: string;
  middleName: string;
  gender: ClientGender;
  birthDate: string;
  birthPlacePassport: string;
  birthPlaceFias: string;
  citizenship: ClientCitizenship;
  inn: string;
  snils: string;
  passportSeries: string;
  passportNumber: string;
  passportIssuedBy: string;
  passportIssueDate: string;
  passportDepartmentCode: string;
  education: ClientEducationItem[];
  professions: ClientProfessionItem[];
  workplaces: ClientWorkplaceItem[];
  maritalStatus: ClientMaritalStatus;
  relatives: ClientRelativeItem[];
  registrationAddress: string;
  registrationAddressPostalCode: string;
  registrationAddressFiasId: string;
  registrationAddressKladrId: string;
  registrationAddressGeoLat: string;
  registrationAddressGeoLon: string;
  actualAddress: string;
  actualAddressPostalCode: string;
  actualAddressFiasId: string;
  actualAddressKladrId: string;
  actualAddressGeoLat: string;
  actualAddressGeoLon: string;
  actualAddressMatchesRegistration: boolean;
  phones: string[];
  emails: string[];
  addresses: string[];
  passports: string[];
  birthCertificates: string[];
  militaryIds: string[];
  birthCertificateEntries: ClientBirthCertificateItem[];
  militaryIdEntries: ClientMilitaryIdItem[];
  passportEntries: ClientPassportItem[];
  addressEntries: ClientAddressItem[];
  comment: string;
  legacyNotes: string;
}

const CLIENT_PROFILE_NOTES_PREFIX = '__PP_CLIENT_PROFILE_V2__:';

const createEmptyEducation = (): ClientEducationItem => ({ institution: '', specialty: '', diplomaNumber: '', oksoCode: '' });
const createEmptyProfession = (): ClientProfessionItem => ({ profession: '', okpdtrCode: '' });
const createEmptyWorkplace = (): ClientWorkplaceItem => ({ organizationName: '', position: '', startDate: '', endDate: '' });
const createEmptyRelative = (): ClientRelativeItem => ({ relation: '', fullName: '', birthYear: '' });
const createEmptyBirthCertificateEntry = (): ClientBirthCertificateItem => ({ series: '', number: '', issueDate: '', issuedBy: '', actRecordNumber: '', childFullName: '' });
const createEmptyMilitaryIdEntry = (): ClientMilitaryIdItem => ({ series: '', number: '', issueDate: '', issuedBy: '', category: '', rank: '', vusCode: '', fitnessCategory: '', militaryCommissariat: '' });
const createEmptyPassportEntry = (): ClientPassportItem => ({
  series: '',
  number: '',
  issuedBy: '',
  issueDate: '',
  departmentCode: '',
  birthPlacePassport: '',
  birthPlaceFias: '',
  validFrom: '',
  validTo: '',
});
const createEmptyAddressEntry = (): ClientAddressItem => ({
  type: 'registration',
  additionalTypes: [],
  sourceQuery: '',
  fullAddress: '',
  postalCode: '',
  region: '',
  district: '',
  city: '',
  settlement: '',
  street: '',
  house: '',
  flat: '',
  fiasId: '',
  kladrId: '',
  geoLat: '',
  geoLon: '',
  validFrom: '',
  validTo: '',
});

const ADDRESS_TYPE_OPTIONS: AddressTypeOption[] = [
  { value: 'registration', label: 'Адрес регистрации' },
  { value: 'actual', label: 'Фактический адрес' },
  { value: 'legal', label: 'Юридический адрес' },
  { value: 'temporary', label: 'Временная регистрация' },
  { value: 'mailing', label: 'Почтовый адрес' },
  { value: 'other', label: 'Иной адрес' },
] as const;

const DATE_INPUT_CLASS = 'w-full rounded-lg border border-gray-300 px-3 py-2 text-center disabled:bg-gray-100';

const MARITAL_STATUS_OPTIONS: Array<{ value: Exclude<ClientMaritalStatus, ''>; label: string }> = [
  { value: 'never_married', label: 'Никогда не состоял(а) в браке' },
  { value: 'married', label: 'В браке' },
  { value: 'divorced', label: 'Разведён(а)' },
  { value: 'widowed', label: 'Вдовец / вдова' },
  { value: 'civil_union', label: 'Гражданский брак' },
];


const normalizeList = (values: string[]): string[] =>
  values
    .map((value) => value.trim())
    .filter(Boolean)
    .filter((value, index, source) => source.indexOf(value) === index);

const buildClientDisplayName = (
  profile: ClientExtendedProfile,
  legalEntityName?: string,
): string => {
  if (profile.kind === 'legal') {
    const normalizedLegalEntityName = legalEntityName?.trim();
    if (normalizedLegalEntityName) return normalizedLegalEntityName;
    return 'Юридическое лицо';
  }
  return [profile.lastName, profile.firstName, profile.middleName].map((item) => item.trim()).filter(Boolean).join(' ') || 'Новый клиент';
};

const mapBackendAddressTypeToProfile = (type?: number): string => {
  switch (type) {
    case 1:
      return 'registration';
    case 2:
      return 'actual';
    case 3:
      return 'legal';
    default:
      return 'other';
  }
};

const composeAddressFromParts = (entry: ClientAddressItem): string => {
  const parts = [
    entry.postalCode,
    entry.region,
    entry.district,
    entry.city,
    entry.settlement,
    entry.street,
    entry.house && `д. ${entry.house}`,
    entry.flat && `кв. ${entry.flat}`,
  ]
    .map((item) => (item ?? '').trim())
    .filter(Boolean);

  return parts.join(', ');
};

const buildAddressEntriesFromClient = (client: Client): ClientAddressItem[] => {
  const prepared = (client.addresses ?? [])
    .map((address) => {
      const fullAddress = (address.unrestrictedValue ?? address.value ?? '').trim();
      if (!fullAddress) return null;
      return {
        ...createEmptyAddressEntry(),
        type: mapBackendAddressTypeToProfile(address.type),
        sourceQuery: fullAddress,
        fullAddress,
        postalCode: address.postalCode ?? '',
        region: address.regionWithType ?? '',
        district: '',
        city: address.cityWithType ?? '',
        settlement: '',
        street: address.streetWithType ?? '',
        house: address.house ?? '',
        flat: address.flat ?? '',
        fiasId: address.fiasId ?? '',
        kladrId: address.kladrId ?? '',
        geoLat: address.geoLat ?? '',
        geoLon: address.geoLon ?? '',
      };
    })
    .filter((item): item is ClientAddressItem => item !== null);

  if (prepared.length > 0) return prepared;

  if (client.address?.trim()) {
    return [
      {
        ...createEmptyAddressEntry(),
        type: 'registration',
        sourceQuery: client.address,
        fullAddress: client.address,
      },
    ];
  }

  return [createEmptyAddressEntry()];
};

const parseClientExtendedProfile = (client: Client): ClientExtendedProfile => {
  const sourceNotes = client.notes ?? '';
  const profileAddressEntries = buildAddressEntriesFromClient(client);
  const registrationEntry =
    profileAddressEntries.find((entry) => entry.type === 'registration')
    ?? profileAddressEntries.find((entry) => entry.type === 'legal')
    ?? profileAddressEntries[0]
    ?? createEmptyAddressEntry();
  const actualEntry =
    profileAddressEntries.find((entry) => entry.type === 'actual')
    ?? profileAddressEntries[1]
    ?? registrationEntry;
  const defaultProfile: ClientExtendedProfile = {
    kind: client.legalEntityId ? 'legal' : 'individual',
    lastName: '',
    firstName: '',
    middleName: '',
    gender: '',
    birthDate: '',
    birthPlacePassport: '',
    birthPlaceFias: '',
    citizenship: '',
    inn: '',
    snils: '',
    passportSeries: '',
    passportNumber: '',
    passportIssuedBy: '',
    passportIssueDate: '',
    passportDepartmentCode: '',
    education: [createEmptyEducation()],
    professions: [createEmptyProfession()],
    workplaces: [createEmptyWorkplace()],
    maritalStatus: '',
    relatives: [createEmptyRelative()],
    registrationAddress: registrationEntry.fullAddress,
    registrationAddressPostalCode: registrationEntry.postalCode,
    registrationAddressFiasId: registrationEntry.fiasId,
    registrationAddressKladrId: registrationEntry.kladrId,
    registrationAddressGeoLat: registrationEntry.geoLat,
    registrationAddressGeoLon: registrationEntry.geoLon,
    actualAddress: actualEntry.fullAddress,
    actualAddressPostalCode: actualEntry.postalCode,
    actualAddressFiasId: actualEntry.fiasId,
    actualAddressKladrId: actualEntry.kladrId,
    actualAddressGeoLat: actualEntry.geoLat,
    actualAddressGeoLon: actualEntry.geoLon,
    actualAddressMatchesRegistration:
      Boolean(actualEntry.fullAddress)
      && actualEntry.fullAddress === registrationEntry.fullAddress,
    phones: client.phone ? [client.phone] : [''],
    emails: client.email ? [client.email] : [''],
    addresses: profileAddressEntries.map((entry) => entry.fullAddress).filter(Boolean),
    passports: [''],
    birthCertificates: [''],
    militaryIds: [''],
    birthCertificateEntries: [createEmptyBirthCertificateEntry()],
    militaryIdEntries: [createEmptyMilitaryIdEntry()],
    passportEntries: [createEmptyPassportEntry()],
    addressEntries: profileAddressEntries,
    comment: '',
    legacyNotes: sourceNotes.startsWith(CLIENT_PROFILE_NOTES_PREFIX) ? '' : sourceNotes,
  };

  if (client.name && !client.legalEntityId) {
    const [lastName = '', firstName = '', ...rest] = client.name.trim().split(/\s+/);
    defaultProfile.lastName = lastName;
    defaultProfile.firstName = firstName;
    defaultProfile.middleName = rest.join(' ');
  }

  const rawProfilePayload = sourceNotes.startsWith(CLIENT_PROFILE_NOTES_PREFIX)
    ? sourceNotes.slice(CLIENT_PROFILE_NOTES_PREFIX.length)
    : '';

  if (!rawProfilePayload) return defaultProfile;

  try {
    const parsed = JSON.parse(rawProfilePayload) as Partial<ClientExtendedProfile>;
    return {
      ...defaultProfile,
      ...parsed,
      kind: parsed.kind === 'legal' ? 'legal' : 'individual',
      gender: parsed.gender === 'male' || parsed.gender === 'female' ? parsed.gender : '',
      citizenship: parsed.citizenship && ['rf', 'double', 'foreign', 'stateless', 'other'].includes(parsed.citizenship)
        ? parsed.citizenship as ClientCitizenship
        : '',
      maritalStatus: parsed.maritalStatus && ['never_married', 'married', 'divorced', 'widowed', 'civil_union'].includes(parsed.maritalStatus)
        ? parsed.maritalStatus as ClientMaritalStatus
        : '',
      passportSeries: parsed.passportSeries ?? defaultProfile.passportSeries,
      passportNumber: parsed.passportNumber ?? defaultProfile.passportNumber,
      passportIssuedBy: parsed.passportIssuedBy ?? defaultProfile.passportIssuedBy,
      passportIssueDate: parsed.passportIssueDate ?? defaultProfile.passportIssueDate,
      passportDepartmentCode: parsed.passportDepartmentCode ?? defaultProfile.passportDepartmentCode,
      education: (parsed.education ?? defaultProfile.education).map((item) => ({ ...createEmptyEducation(), ...item })),
      professions: (parsed.professions ?? defaultProfile.professions).map((item) => ({ ...createEmptyProfession(), ...item })),
      workplaces: (parsed.workplaces ?? defaultProfile.workplaces).map((item) => ({ ...createEmptyWorkplace(), ...item })),
      relatives: (parsed.relatives ?? defaultProfile.relatives).map((item) => ({ ...createEmptyRelative(), ...item })),
      phones: normalizeList(parsed.phones ?? defaultProfile.phones),
      emails: normalizeList(parsed.emails ?? defaultProfile.emails),
      addresses: normalizeList(parsed.addresses ?? defaultProfile.addresses),
      passports: normalizeList(parsed.passports ?? defaultProfile.passports),
      birthCertificates: normalizeList(parsed.birthCertificates ?? defaultProfile.birthCertificates),
      militaryIds: normalizeList(parsed.militaryIds ?? defaultProfile.militaryIds),
      birthCertificateEntries: (parsed.birthCertificateEntries ?? defaultProfile.birthCertificateEntries).map((item) => ({ ...createEmptyBirthCertificateEntry(), ...item })),
      militaryIdEntries: (parsed.militaryIdEntries ?? defaultProfile.militaryIdEntries).map((item) => ({ ...createEmptyMilitaryIdEntry(), ...item })),
      passportEntries: (parsed.passportEntries ?? [
        {
          ...createEmptyPassportEntry(),
          series: parsed.passportSeries ?? defaultProfile.passportSeries,
          number: parsed.passportNumber ?? defaultProfile.passportNumber,
          issuedBy: parsed.passportIssuedBy ?? defaultProfile.passportIssuedBy,
          issueDate: parsed.passportIssueDate ?? defaultProfile.passportIssueDate,
          departmentCode: parsed.passportDepartmentCode ?? defaultProfile.passportDepartmentCode,
          birthPlacePassport: parsed.birthPlacePassport ?? defaultProfile.birthPlacePassport,
          birthPlaceFias: parsed.birthPlaceFias ?? defaultProfile.birthPlaceFias,
        },
      ]).map((item) => ({ ...createEmptyPassportEntry(), ...item })),
      addressEntries: (parsed.addressEntries ?? [
        {
          ...createEmptyAddressEntry(),
          type: 'registration',
          sourceQuery: parsed.registrationAddress ?? defaultProfile.registrationAddress,
          fullAddress: parsed.registrationAddress ?? defaultProfile.registrationAddress,
          postalCode: parsed.registrationAddressPostalCode ?? defaultProfile.registrationAddressPostalCode,
          fiasId: parsed.registrationAddressFiasId ?? defaultProfile.registrationAddressFiasId,
          kladrId: parsed.registrationAddressKladrId ?? defaultProfile.registrationAddressKladrId,
          geoLat: parsed.registrationAddressGeoLat ?? defaultProfile.registrationAddressGeoLat,
          geoLon: parsed.registrationAddressGeoLon ?? defaultProfile.registrationAddressGeoLon,
        },
        {
          ...createEmptyAddressEntry(),
          type: 'actual',
          sourceQuery: parsed.actualAddress ?? defaultProfile.actualAddress,
          fullAddress: parsed.actualAddress ?? defaultProfile.actualAddress,
          postalCode: parsed.actualAddressPostalCode ?? defaultProfile.actualAddressPostalCode,
          fiasId: parsed.actualAddressFiasId ?? defaultProfile.actualAddressFiasId,
          kladrId: parsed.actualAddressKladrId ?? defaultProfile.actualAddressKladrId,
          geoLat: parsed.actualAddressGeoLat ?? defaultProfile.actualAddressGeoLat,
          geoLon: parsed.actualAddressGeoLon ?? defaultProfile.actualAddressGeoLon,
        },
      ]).map((item) => ({ ...createEmptyAddressEntry(), ...item, additionalTypes: Array.isArray((item as ClientAddressItem).additionalTypes) ? (item as ClientAddressItem).additionalTypes : [] })),
      comment: parsed.comment ?? '',
      legacyNotes: parsed.legacyNotes ?? defaultProfile.legacyNotes,
    };
  } catch {
    return defaultProfile;
  }
};

const serializeClientExtendedProfile = (profile: ClientExtendedProfile): string =>
  JSON.stringify({
    ...profile,
    phones: normalizeList(profile.phones),
    emails: normalizeList(profile.emails),
    addresses: normalizeList(profile.addresses),
    passports: normalizeList(profile.passports),
    birthCertificates: normalizeList(profile.birthCertificates),
    militaryIds: normalizeList(profile.militaryIds),
    birthCertificateEntries: profile.birthCertificateEntries,
    militaryIdEntries: profile.militaryIdEntries,
    passportEntries: profile.passportEntries,
    addressEntries: profile.addressEntries,
    comment: profile.comment.trim(),
    legacyNotes: profile.legacyNotes.trim(),
  });

export function ClientDetail({ clientId, onBack, onClientUpdated, initialCaseId }: ClientDetailProps) {
  const { t } = useTranslation();
  const { user, isAdmin } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
  const permissions = useRolePermissions(user?.role?.id);
  const userIsAdmin = isAdmin();
  const clientPermissions = permissions.clients;
  const accountPermissions = permissions.accounts;
  const actPermissions = permissions.acts;
  const contractPermissions = permissions.contracts;
  const { legalEntities, createLegalEntity, refresh: refreshLegalEntities } = useLegalEntities();
  const { statuses: clientStatuses } = useClientStatuses();
  const sectionPermissions = useCallback(
    (section: ClientDetailSection) => permissions[SECTION_PERMISSION_MAP[section]],
    [permissions],
  );
  const [clientName, setClientName] = useState<string>('...');
  const [clientData, setClientData] = useState<Client | null>(null);
  const [selectedCaseId, setSelectedCaseId] = useState<number | 'all'>(initialCaseId ?? 'all');
  const [clientFormSaving, setClientFormSaving] = useState(false);
  // Временно закомментировано: будет возвращено при реинтеграции табов профиля.
  // const [activeProfileTab, setActiveProfileTab] = useState<ProfileTabKey>('phone');
  // const [profileDocuments, setProfileDocuments] = useState<ProfileDocumentsState>({
  //   passport: '',
  //   birthCertificate: '',
  //   militaryId: '',
  // });
  const [clientFormState, setClientFormState] = useState({
    kind: 'individual' as ClientKind,
    lastName: '',
    firstName: '',
    middleName: '',
    gender: '' as ClientGender,
    birthDate: '',
    birthPlacePassport: '',
    birthPlaceFias: '',
    citizenship: '' as ClientCitizenship,
    inn: '',
    snils: '',
    passportSeries: '',
    passportNumber: '',
    passportIssuedBy: '',
    passportIssueDate: '',
    passportDepartmentCode: '',
    education: [createEmptyEducation()],
    professions: [createEmptyProfession()],
    workplaces: [createEmptyWorkplace()],
    maritalStatus: '' as ClientMaritalStatus,
    relatives: [createEmptyRelative()],
    registrationAddress: '',
    registrationAddressPostalCode: '',
    registrationAddressFiasId: '',
    registrationAddressKladrId: '',
    registrationAddressGeoLat: '',
    registrationAddressGeoLon: '',
    actualAddress: '',
    actualAddressPostalCode: '',
    actualAddressFiasId: '',
    actualAddressKladrId: '',
    actualAddressGeoLat: '',
    actualAddressGeoLon: '',
    actualAddressMatchesRegistration: false,
    phones: [''],
    emails: [''],
    addresses: [''],
    passports: [''],
    birthCertificates: [''],
    militaryIds: [''],
    birthCertificateEntries: [createEmptyBirthCertificateEntry()],
    militaryIdEntries: [createEmptyMilitaryIdEntry()],
    passportEntries: [createEmptyPassportEntry()],
    addressEntries: [createEmptyAddressEntry()],
    comment: '',
    legacyNotes: '',
    legalEntityId: '',
    responsibleUserId: '',
    isActive: true,
    clientStatusId: '',
  });
  const [addressSuggestionsByIndex, setAddressSuggestionsByIndex] = useState<Record<number, DadataAddressSuggestion[]>>({});
  const addressSuggestionTimersRef = useRef<Record<number, number>>({});
  const [oksoOptions, setOksoOptions] = useState<ClientReferenceItem[]>([]);
  const [okpdtrOptions, setOkpdtrOptions] = useState<ClientReferenceItem[]>([]);
  const [powersOfAttorney, setPowersOfAttorney] = useState<PowerOfAttorney[]>([]);
  const [activeProfileGroup, setActiveProfileGroup] = useState<'main' | 'passports' | 'addresses' | 'education' | 'professions' | 'workplaces' | 'relatives' | 'birthCertificates' | 'militaryIds' | 'poa'>('main');
  const [legalEntityPromptOpen, setLegalEntityPromptOpen] = useState(false);
  const [legalEntityPromptSubmitting, setLegalEntityPromptSubmitting] = useState(false);
  const [legalEntityPromptError, setLegalEntityPromptError] = useState<string | null>(null);
  const [legalEntityDraft, setLegalEntityDraft] = useState({ shortName: '', inn: '' });
  const [poaPromptOpen, setPoaPromptOpen] = useState(false);
  const [poaPromptSubmitting, setPoaPromptSubmitting] = useState(false);
  const [poaPromptError, setPoaPromptError] = useState<string | null>(null);
  const [poaDraft, setPoaDraft] = useState({ number: '', issuedAt: formatLocalYMD(new Date()) });

  const hasMeaningfulValue = (value: unknown): boolean => {
    if (value === null || value === undefined) return false;
    if (typeof value === 'string') return value.trim().length > 0;
    if (Array.isArray(value)) return value.some((item) => hasMeaningfulValue(item));
    if (typeof value === 'object') {
      return Object.values(value as Record<string, unknown>).some((item) => hasMeaningfulValue(item));
    }
    return Boolean(value);
  };

  const requiredInputClass = (requiredKey: string, value: unknown) => {
    if (!isRequired(requiredKey)) return 'border-gray-300 focus:ring-emerald-500 focus:border-emerald-500';

    return hasMeaningfulValue(value)
      ? 'border-emerald-400 focus:ring-emerald-200 focus:border-emerald-500'
      : 'border-red-400 focus:ring-red-200 focus:border-red-500';
  };

  const requiredMark = (requiredKey: string) => (isRequired(requiredKey) ? ' *' : '');

  const isRequiredListMissing = (requiredKey: string, values: unknown[]) =>
    isRequired(requiredKey) && !values.some((item) => hasMeaningfulValue(item));

  const hasMissingRequiredList = (requiredKeys: string[] | undefined, values: unknown[]) =>
    Boolean(requiredKeys?.some((requiredKey) => isRequiredListMissing(requiredKey, values)));

  const hasAnyRequiredList = (requiredKeys: string[] | undefined) =>
    Boolean(requiredKeys?.some((requiredKey) => isRequired(requiredKey)));

  const closeLegalEntityPrompt = (force = false) => {
    if (legalEntityPromptSubmitting && !force) return;
    setLegalEntityPromptOpen(false);
    setLegalEntityPromptError(null);
    setLegalEntityDraft({ shortName: '', inn: '' });
  };

  const submitQuickLegalEntity = async () => {
    const shortName = legalEntityDraft.shortName.trim();
    const inn = legalEntityDraft.inn.trim();

    if (!shortName) {
      setLegalEntityPromptError('Укажите название организации.');
      return;
    }

    try {
      setLegalEntityPromptSubmitting(true);
      const created = await createLegalEntity({ shortName, inn: inn || undefined, clientIds: [] });
      await refreshLegalEntities();
      setClientFormState((prev) => ({
        ...prev,
        workplaces: prev.workplaces.length
          ? prev.workplaces.map((workplace, index) =>
              index === prev.workplaces.length - 1 ? { ...workplace, organizationName: created.shortName } : workplace,
            )
          : [{ organizationName: created.shortName, position: '', startDate: '', endDate: '' }],
      }));
      closeLegalEntityPrompt(true);
      toast.success('Организация создана.', 'Юрлица');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось создать организацию');
      setLegalEntityPromptError(message);
      toast.error(message, 'Юрлица');
    } finally {
      setLegalEntityPromptSubmitting(false);
    }
  };

  const closePoaPrompt = (force = false) => {
    if (poaPromptSubmitting && !force) return;
    setPoaPromptOpen(false);
    setPoaPromptError(null);
    setPoaDraft({ number: '', issuedAt: formatLocalYMD(new Date()) });
  };

  const submitQuickPowerOfAttorney = async () => {
    const number = poaDraft.number.trim();
    const issuedAt = poaDraft.issuedAt.trim();

    if (!number || !issuedAt) {
      setPoaPromptError('Укажите номер и дату выдачи доверенности.');
      return;
    }

    try {
      setPoaPromptSubmitting(true);
      const created = await apiService.createClientPowerOfAttorney(clientId, { number, issuedAt, isActive: true });
      setPowersOfAttorney((prev) => [created, ...prev]);
      closePoaPrompt(true);
      toast.success('Доверенность создана.', 'Доверенности');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось создать доверенность');
      setPoaPromptError(message);
      toast.error(message, 'Доверенности');
    } finally {
      setPoaPromptSubmitting(false);
    }
  };

  const fieldLabels: Record<string, string> = {
    Emails: 'Email',
    Relatives: 'Родственники',
    ResponsibleUserId: 'Ответственный',
    ClientStatusId: 'Статус клиента',
    LastName: 'Фамилия',
    FirstName: 'Имя',
    MiddleName: 'Отчество',
    Inn: 'ИНН',
    Snils: 'СНИЛС',
    Comment: 'Комментарий',
  };

  const formatApiValidationError = (error: unknown, fallback: string) => {
    return extractDisplayErrorMessage(error, fallback, fieldLabels);
  };

  const { cases: serverCases } = useClientCases(clientId);
  const [casesLocal, setCasesLocal] = useState<ClientCase[] | null>(null);
  useEffect(() => setCasesLocal(serverCases), [serverCases]);
  const cases = useMemo(() => casesLocal ?? serverCases, [casesLocal, serverCases]);

  useEffect(() => {
    void (async () => {
      try {
        const [okso, okpdtr, poa] = await Promise.all([
          apiService.getOksoDictionary(),
          apiService.getOkpdtrDictionary(),
          apiService.getClientPowersOfAttorney(clientId),
        ]);
        setOksoOptions(okso ?? []);
        setOkpdtrOptions(okpdtr ?? []);
        setPowersOfAttorney(poa ?? []);
      } catch {
        // optional
      }
    })();
  }, [clientId]);

  const [monthFrom, setMonthFrom] = useState<string>('');
  const [monthTo, setMonthTo] = useState<string>('');
  const [statusFilter, setStatusFilter] = useState<'all' | NormalizedStatus>('all');
  const [paymentPage, setPaymentPage] = useState(1);
  const [paymentTableSettingsOpen, setPaymentTableSettingsOpen] = useState(false);
  const {
    columnOrder: paymentColumnOrder,
    orderedVisibleColumns: paymentOrderedVisibleColumns,
    visibleColumns: paymentVisibleColumns,
    pageSize: paymentPageSize,
    setPageSize: setPaymentPageSize,
    draggingColumn: draggingPaymentColumn,
    toggleColumn: togglePaymentColumn,
    moveColumn: movePaymentColumn,
    handleColumnDragStart: handlePaymentColumnDragStart,
    handleColumnDragOver: handlePaymentColumnDragOver,
    handleColumnDrop: handlePaymentColumnDrop,
    handleColumnDragEnd: handlePaymentColumnDragEnd,
    handleResizeStart: handlePaymentResizeStart,
    getColumnWidth: getPaymentColumnWidth,
    resetTableSettings: resetPaymentTableSettings,
  } = usePersistedTableSettings<PaymentTableColumnKey>({
    storageKey: `${CLIENT_DETAIL_TABLE_STORAGE_PREFIX}.payments.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'client_detail_payments_table',
    columns: PAYMENT_TABLE_COLUMNS.map(({ key, defaultWidth }) => ({
      key,
      defaultWidth,
    })),
    defaultPageSize: DETAIL_PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 120,
  });

  const [actFrom, setActFrom] = useState<string>(() =>
    formatLocalYMD(new Date(new Date().getFullYear(), 0, 1)),
  );
  const [actTo, setActTo] = useState<string>(() => formatLocalYMD(new Date()));
  const [actStatusFilter, setActStatusFilter] = useState<'all' | ActStatus>('all');
  const [actSearch, setActSearch] = useState('');
  const debouncedActSearch = useDebouncedValue(actSearch.trim(), 400);
  const [actSort, setActSort] = useState<{ key: ActsSortKey; direction: 'asc' | 'desc' }>(
    { key: 'date', direction: 'desc' },
  );
  const [actPage, setActPage] = useState(1);
  const [actTableSettingsOpen, setActTableSettingsOpen] = useState(false);
  const {
    columnOrder: actColumnOrder,
    orderedVisibleColumns: actOrderedVisibleColumns,
    visibleColumns: actVisibleColumns,
    pageSize: actPageSize,
    setPageSize: setActPageSize,
    draggingColumn: draggingActColumn,
    toggleColumn: toggleActColumn,
    moveColumn: moveActColumn,
    handleColumnDragStart: handleActColumnDragStart,
    handleColumnDragOver: handleActColumnDragOver,
    handleColumnDrop: handleActColumnDrop,
    handleColumnDragEnd: handleActColumnDragEnd,
    handleResizeStart: handleActResizeStart,
    getColumnWidth: getActColumnWidth,
    resetTableSettings: resetActTableSettings,
  } = usePersistedTableSettings<ActTableColumnKey>({
    storageKey: `${CLIENT_DETAIL_TABLE_STORAGE_PREFIX}.acts.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'client_detail_acts_table',
    columns: ACT_TABLE_COLUMNS.map(({ key, defaultWidth }) => ({
      key,
      defaultWidth,
    })),
    defaultPageSize: DETAIL_PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 120,
  });

  const [payModalOpen, setPayModalOpen] = useState(false);
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);

  const [actModalOpen, setActModalOpen] = useState(false);
  const [actModalMode, setActModalMode] = useState<'create' | 'edit'>('create');
  const [selectedAct, setSelectedAct] = useState<Act | null>(null);
  const [actSubmitting, setActSubmitting] = useState(false);
  const [actModalError, setActModalError] = useState<string | null>(null);
  const [actClients, setActClients] = useState<Client[]>([]);
  const [actResponsibles, setActResponsibles] = useState<ActResponsible[]>([]);
  const [actLookupsLoading, setActLookupsLoading] = useState(false);
  const [actLookupsError, setActLookupsError] = useState<string | null>(null);

  const today = useMemo(() => new Date(), []);
  const startOfYear = useMemo(
    () => formatLocalYMD(new Date(today.getFullYear(), 0, 1)),
    [today],
  );
  const todayYMD = useMemo(() => formatLocalYMD(today), [today]);

  const [invoiceSort, setInvoiceSort] = useState<{ key: InvoicesSortKey; direction: 'asc' | 'desc' }>({
    key: 'date',
    direction: 'desc',
  });
  const [invoicePage, setInvoicePage] = useState(1);
  const [invoiceTableSettingsOpen, setInvoiceTableSettingsOpen] = useState(false);
  const {
    columnOrder: invoiceColumnOrder,
    orderedVisibleColumns: invoiceOrderedVisibleColumns,
    visibleColumns: invoiceVisibleColumns,
    pageSize: invoicePageSize,
    setPageSize: setInvoicePageSize,
    draggingColumn: draggingInvoiceColumn,
    toggleColumn: toggleInvoiceColumn,
    moveColumn: moveInvoiceColumn,
    handleColumnDragStart: handleInvoiceColumnDragStart,
    handleColumnDragOver: handleInvoiceColumnDragOver,
    handleColumnDrop: handleInvoiceColumnDrop,
    handleColumnDragEnd: handleInvoiceColumnDragEnd,
    handleResizeStart: handleInvoiceResizeStart,
    getColumnWidth: getInvoiceColumnWidth,
    resetTableSettings: resetInvoiceTableSettings,
  } = usePersistedTableSettings<InvoiceTableColumnKey>({
    storageKey: `${CLIENT_DETAIL_TABLE_STORAGE_PREFIX}.invoices.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'client_detail_invoices_table',
    columns: INVOICE_TABLE_COLUMNS.map(({ key, defaultWidth }) => ({
      key,
      defaultWidth,
    })),
    defaultPageSize: DETAIL_PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 120,
  });

  const [contractFrom, setContractFrom] = useState<string>(startOfYear);
  const [contractTo, setContractTo] = useState<string>(todayYMD);
  const [contractSearch, setContractSearch] = useState('');
  const debouncedContractSearch = useDebouncedValue(contractSearch.trim(), 400);
  const [contractSort, setContractSort] = useState<{ key: ContractsSortKey; direction: 'asc' | 'desc' }>({
    key: 'date',
    direction: 'desc',
  });
  const [contractPage, setContractPage] = useState(1);
  const [contractTableSettingsOpen, setContractTableSettingsOpen] = useState(false);
  const {
    columnOrder: contractColumnOrder,
    orderedVisibleColumns: contractOrderedVisibleColumns,
    visibleColumns: contractVisibleColumns,
    pageSize: contractPageSize,
    setPageSize: setContractPageSize,
    draggingColumn: draggingContractColumn,
    toggleColumn: toggleContractColumn,
    moveColumn: moveContractColumn,
    handleColumnDragStart: handleContractColumnDragStart,
    handleColumnDragOver: handleContractColumnDragOver,
    handleColumnDrop: handleContractColumnDrop,
    handleColumnDragEnd: handleContractColumnDragEnd,
    handleResizeStart: handleContractResizeStart,
    getColumnWidth: getContractColumnWidth,
    resetTableSettings: resetContractTableSettings,
  } = usePersistedTableSettings<ContractTableColumnKey>({
    storageKey: `${CLIENT_DETAIL_TABLE_STORAGE_PREFIX}.contracts.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'client_detail_contracts_table',
    columns: CONTRACT_TABLE_COLUMNS.map(({ key, defaultWidth }) => ({
      key,
      defaultWidth,
    })),
    defaultPageSize: DETAIL_PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 120,
  });

  const [invoiceModalOpen, setInvoiceModalOpen] = useState(false);
  const [invoiceModalMode, setInvoiceModalMode] = useState<'create' | 'edit'>('create');
  const [selectedInvoice, setSelectedInvoice] = useState<Invoice | null>(null);
  const [invoiceSubmitting, setInvoiceSubmitting] = useState(false);
  const [invoiceModalError, setInvoiceModalError] = useState<string | null>(null);
  const [invoiceClients, setInvoiceClients] = useState<Client[]>([]);
  const [invoiceLookupsLoading, setInvoiceLookupsLoading] = useState(false);
  const [invoiceLookupsError, setInvoiceLookupsError] = useState<string | null>(null);

  const [contractModalOpen, setContractModalOpen] = useState(false);
  const [contractModalMode, setContractModalMode] = useState<'create' | 'edit'>('create');
  const [selectedContract, setSelectedContract] = useState<Contract | null>(null);
  const [contractSubmitting, setContractSubmitting] = useState(false);
  const [contractModalError, setContractModalError] = useState<string | null>(null);
  const [contractActionError, setContractActionError] = useState<string | null>(null);

  const {
    acts: clientActs,
    loading: actsLoading,
    refreshing: actsRefreshing,
    error: actsError,
    summary: actsSummary,
    pagination: actsPagination,
    refresh: refreshActs,
    createAct,
    updateAct,
    deleteAct,
  } = useActs({
    from: actFrom || undefined,
    to: actTo || undefined,
    status: actStatusFilter === 'all' ? undefined : actStatusFilter,
    clientId,
    search: debouncedActSearch || undefined,
    sortBy: actSort.key,
    sortDir: actSort.direction,
    page: actPage,
    pageSize: actPageSize,
  });

  useEffect(() => {
    setActPage(1);
  }, [actFrom, actTo, actStatusFilter, debouncedActSearch, clientId, actPageSize]);

  const {
    invoices: clientInvoices,
    loading: invoicesLoading,
    refreshing: invoicesRefreshing,
    error: invoicesError,
    summary: invoicesSummary,
    pagination: invoicesPagination,
    refresh: refreshInvoices,
    createInvoice: createClientInvoice,
    updateInvoice: updateClientInvoice,
    deleteInvoice: deleteClientInvoice,
  } = useInvoices({
    clientId,
    type: 'Income',
    sortBy: invoiceSort.key,
    sortDir: invoiceSort.direction,
    page: invoicePage,
    pageSize: invoicePageSize,
  });

  useEffect(() => {
    setInvoicePage(1);
  }, [clientId, invoicePageSize]);

  const invoiceTotalPages = useMemo(() => {
    if (!invoicesPagination.pageSize) return 1;
    return Math.max(1, Math.ceil((invoicesPagination.total ?? 0) / invoicesPagination.pageSize));
  }, [invoicesPagination.pageSize, invoicesPagination.total]);

  const invoiceSummaryBuckets = invoicesSummary ?? {
    total: { amount: 0, count: 0 },
    pending: { amount: 0, count: 0 },
    paid: { amount: 0, count: 0 },
    overdue: { amount: 0, count: 0 },
  };

  const invoiceStatusLabels: Record<PaymentStatus, string> = {
    Pending: t('invoicePendingBadge') ?? t('pending') ?? 'Ожидается',
    Completed: t('invoicePaidBadge') ?? t('completedStatus') ?? 'Оплачено',
    Overdue: t('invoiceOverdueBadge') ?? t('overdue') ?? 'Просрочено',
    Processing: t('processingStatus') ?? 'В обработке',
    Cancelled: t('cancelledStatus') ?? 'Отменено',
  };

  const {
    contracts: clientContracts,
    loading: contractsLoading,
    refreshing: contractsRefreshing,
    error: contractsError,
    pagination: contractsPagination,
    refresh: refreshContracts,
    createContract,
    updateContract,
    deleteContract,
  } = useContracts({
    from: contractFrom || undefined,
    to: contractTo || undefined,
    search: debouncedContractSearch || undefined,
    clientId,
    sortBy: contractSort.key,
    sortDir: contractSort.direction,
    page: contractPage,
    pageSize: contractPageSize,
  });

  useEffect(() => {
    setContractPage(1);
  }, [contractFrom, contractTo, debouncedContractSearch, clientId, contractPageSize]);

  const contractTotalPages = useMemo(() => {
    if (!contractsPagination.pageSize) return 1;
    return Math.max(1, Math.ceil((contractsPagination.total ?? 0) / contractsPagination.pageSize));
  }, [contractsPagination.pageSize, contractsPagination.total]);

  const invoiceStatusClasses: Record<PaymentStatus, string> = {
    Pending: 'bg-amber-100 text-amber-700 border border-amber-200',
    Completed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Overdue: 'bg-rose-100 text-rose-700 border border-rose-200',
    Processing: 'bg-blue-100 text-blue-700 border border-blue-200',
    Cancelled: 'bg-slate-100 text-slate-600 border border-slate-200',
  };

  const openInvoiceCreate = () => {
    if (!accountPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления счетов.');
      return;
    }
    setInvoiceModalMode('create');
    setSelectedInvoice(null);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const openInvoiceEdit = (invoice: Invoice) => {
    if (!accountPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования счетов.');
      return;
    }
    setInvoiceModalMode('edit');
    setSelectedInvoice(invoice);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const closeInvoiceModal = () => {
    setInvoiceModalOpen(false);
    setSelectedInvoice(null);
    setInvoiceModalError(null);
  };

  const ensureInvoiceClients = useCallback(async () => {
    if (!invoiceModalOpen) return;
    if (invoiceClients.length > 0 && !invoiceLookupsError) return;
    setInvoiceLookupsLoading(true);
    try {
      const list = await apiService.getClients();
      setInvoiceClients(list ?? []);
      setInvoiceLookupsError(null);
    } catch (err) {
      setInvoiceLookupsError(
        err instanceof Error ? err.message : t('clientsLoadError') ?? 'Не удалось загрузить клиентов',
      );
    } finally {
      setInvoiceLookupsLoading(false);
    }
  }, [invoiceModalOpen, invoiceClients.length, invoiceLookupsError, t]);

  useEffect(() => {
    if (invoiceModalOpen) {
      void ensureInvoiceClients();
    }
  }, [invoiceModalOpen, ensureInvoiceClients]);

  const submitInvoice = async (payload: InvoiceInput) => {
    const isEdit = invoiceModalMode === 'edit' && selectedInvoice;
    if (isEdit && !accountPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования счетов.');
      return;
    }
    if (!isEdit && !accountPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления счетов.');
      return;
    }
    setInvoiceSubmitting(true);
    setInvoiceModalError(null);
    try {
      if (isEdit && selectedInvoice) {
        await updateClientInvoice(selectedInvoice.id, payload);
        toast.success('Изменения по счету сохранены.', 'Счет обновлен');
      } else {
        await createClientInvoice(payload);
        toast.success('Счет успешно создан.', 'Счет сохранен');
      }
      closeInvoiceModal();
    } catch (err) {
      setInvoiceModalError(extractDisplayErrorMessage(err, 'Не удалось сохранить счет'));
    } finally {
      setInvoiceSubmitting(false);
    }
  };

  const removeInvoice = async (invoice: Invoice) => {
    if (!accountPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления счетов.');
      return;
    }
    if (!(await confirm({ title: 'Подтверждение удаления', description: t('invoiceDeleteConfirm') ?? 'Удалить счёт?', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }))) return;
    try {
      await deleteClientInvoice(invoice.id);
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить счёт'), 'Ошибка удаления');
    }
  };

  const resetContractFilters = () => {
    setContractFrom(startOfYear);
    setContractTo(todayYMD);
    setContractSearch('');
  };

  const handleInvoiceSort = (key: InvoicesSortKey) => {
    setInvoiceSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' };
    });
    setInvoicePage(1);
  };

  const handleContractSort = (key: ContractsSortKey) => {
    setContractSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' };
    });
    setContractPage(1);
  };

  const openClientProfile = () => {
    setActiveSection('profile');
  };

  const handleUpdateClient = async (payload: ClientInput) => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования клиента.');
      throw new Error('Недостаточно прав для редактирования клиента.');
    }
    try {
      const updated = await apiService.updateClient(clientId, payload);
      setClientData(updated);
      setClientName(updated.name ?? `Client #${clientId}`);
      onClientUpdated?.(updated);
      return updated;
    } catch (err) {
      const message = formatApiValidationError(err, 'Не удалось обновить клиента');
      toast.error(message, 'Ошибка сохранения');
      throw err instanceof Error ? err : new Error(message);
    }
  };

  const handleDeleteClientCard = async () => {
    if (!clientPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления клиента.');
      return;
    }

    const ok = await confirm({
      title: 'Подтверждение удаления',
      description: 'Удалить клиента? Действие необратимо.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!ok) return;

    try {
      await apiService.deleteClient(clientId);
      onBack();
    } catch (err) {
      const raw = err instanceof Error ? err.message : 'Не удалось удалить клиента';
      const marker = ' - ';
      const markerIndex = raw.indexOf(marker);
      const payload = markerIndex >= 0 ? raw.slice(markerIndex + marker.length).trim() : raw;
      try {
        const parsed = JSON.parse(payload) as { message?: string; usage?: string[] };
        const message = parsed.message?.trim() || 'Не удалось удалить клиента';
        const usage = Array.isArray(parsed.usage) && parsed.usage.length > 0
          ? ` Используется в: ${parsed.usage.join(', ')}.`
          : '';
        toast.error(`${message}${usage}`.trim(), 'Удаление невозможно');
      } catch {
        toast.error(raw, 'Удаление невозможно');
      }
    }
  };

  const openContractCreate = () => {
    if (!contractPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления договоров.');
      return;
    }
    setSelectedContract(null);
    setContractModalMode('create');
    setContractModalError(null);
    setContractActionError(null);
    setContractModalOpen(true);
  };

  const openContractEdit = (contract: Contract) => {
    if (!contractPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования договоров.');
      return;
    }
    setSelectedContract(contract);
    setContractModalMode('edit');
    setContractModalError(null);
    setContractActionError(null);
    setContractModalOpen(true);
  };

  const closeContractModal = () => {
    setContractModalOpen(false);
    setSelectedContract(null);
    setContractModalError(null);
  };

  const submitContract = async (payload: ContractInput) => {
    const isEdit = contractModalMode === 'edit' && selectedContract;
    if (isEdit && !contractPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования договоров.');
      return;
    }
    if (!isEdit && !contractPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления договоров.');
      return;
    }
    const normalizedClients = payload.clients.some((entry) => entry.clientId === clientId)
      ? payload.clients
      : [...payload.clients, { clientId }];

    const normalizedPayload: ContractInput = {
      ...payload,
      clients: normalizedClients,
    };
    setContractSubmitting(true);
    setContractModalError(null);
    try {
      if (isEdit && selectedContract) {
        await updateContract(selectedContract.id, normalizedPayload);
      } else {
        await createContract(normalizedPayload);
      }
      closeContractModal();
    } catch (err) {
      setContractModalError(err instanceof Error ? err.message : 'Не удалось сохранить договор');
    } finally {
      setContractSubmitting(false);
    }
  };

  const removeContract = async (contract: Contract) => {
    if (!contractPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления договоров.');
      return;
    }
    const template = t('contractDeleteConfirm') ?? 'Удалить договор «{{number}}»?';
    const confirmMessage = template.replace(
      '{{number}}',
      contract.number || contract.title || String(contract.id),
    );
    if (!(await confirm({ title: 'Подтверждение удаления', description: confirmMessage, confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }))) return;
    setContractActionError(null);
    try {
      await deleteContract(contract.id);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось удалить договор';
      setContractActionError(message);
    }
  };

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('create');

  const visibleSections = useMemo(
    () =>
      SECTION_ORDER.filter(
        (section) => section !== 'settlement' && sectionPermissions(section).canView,
      ),
    [sectionPermissions],
  );

  const [activeSection, setActiveSection] = useState<ClientDetailSection>(() => {
    const firstAllowed = SECTION_ORDER.find((section) => {
      if (section === 'settlement') return false;
      try {
        return sectionPermissions(section).canView;
      } catch {
        return false;
      }
    });
    return firstAllowed ?? 'payments';
  });

  useEffect(() => {
    if (activeSection === 'settlement' || !sectionPermissions(activeSection).canView) {
      const fallback = SECTION_ORDER.find(
        (section) => section !== 'settlement' && sectionPermissions(section).canView,
      );
      if (fallback) setActiveSection(fallback);
    }
  }, [activeSection, sectionPermissions]);

  const { isRequired } = useFormRequirements('clients', activeSection === 'profile');

  const [statsReloadToken, setStatsReloadToken] = useState(0);
  const bumpStats = () => setStatsReloadToken((x) => x + 1);

  const showPermissionWarning = useCallback((message: string) => {
    toast.error(message || 'Недостаточно прав для выполнения действия.', 'Недостаточно прав');
  }, [toast]);

  useEffect(() => {
    let alive = true;
    apiService
      .getClient(clientId)
      .then((c) => {
        if (!alive) return;
        setClientName(c.name ?? `Client #${clientId}`);
        setClientData(c);
      })
      .catch(() => {
        if (!alive) return;
        setClientName(`Client #${clientId}`);
        setClientData(null);
      });
    return () => {
      alive = false;
    };
  }, [clientId]);

  useEffect(() => {
    if (!clientData) return;
    const profile = parseClientExtendedProfile(clientData);
    setClientFormState({
      kind: profile.kind,
      lastName: profile.lastName,
      firstName: profile.firstName,
      middleName: profile.middleName,
      gender: profile.gender,
      birthDate: profile.birthDate,
      birthPlacePassport: profile.birthPlacePassport,
      birthPlaceFias: profile.birthPlaceFias,
      citizenship: profile.citizenship,
      inn: profile.inn,
      snils: profile.snils,
      passportSeries: profile.passportSeries,
      passportNumber: profile.passportNumber,
      passportIssuedBy: profile.passportIssuedBy,
      passportIssueDate: profile.passportIssueDate,
      passportDepartmentCode: profile.passportDepartmentCode,
      education: profile.education.length > 0 ? profile.education : [createEmptyEducation()],
      professions: profile.professions.length > 0 ? profile.professions : [createEmptyProfession()],
      workplaces: profile.workplaces.length > 0 ? profile.workplaces : [createEmptyWorkplace()],
      maritalStatus: profile.maritalStatus,
      relatives: profile.relatives.length > 0 ? profile.relatives : [createEmptyRelative()],
      registrationAddress: profile.registrationAddress,
      registrationAddressPostalCode: profile.registrationAddressPostalCode,
      registrationAddressFiasId: profile.registrationAddressFiasId,
      registrationAddressKladrId: profile.registrationAddressKladrId,
      registrationAddressGeoLat: profile.registrationAddressGeoLat,
      registrationAddressGeoLon: profile.registrationAddressGeoLon,
      actualAddress: profile.actualAddress,
      actualAddressPostalCode: profile.actualAddressPostalCode,
      actualAddressFiasId: profile.actualAddressFiasId,
      actualAddressKladrId: profile.actualAddressKladrId,
      actualAddressGeoLat: profile.actualAddressGeoLat,
      actualAddressGeoLon: profile.actualAddressGeoLon,
      actualAddressMatchesRegistration: profile.actualAddressMatchesRegistration,
      phones: profile.phones.length > 0 ? profile.phones : [''],
      emails: profile.emails.length > 0 ? profile.emails : [''],
      addresses: profile.addresses.length > 0 ? profile.addresses : [''],
      passports: profile.passports.length > 0 ? profile.passports : [''],
      birthCertificates: profile.birthCertificates.length > 0 ? profile.birthCertificates : [''],
      militaryIds: profile.militaryIds.length > 0 ? profile.militaryIds : [''],
      birthCertificateEntries: profile.birthCertificateEntries.length > 0 ? profile.birthCertificateEntries : [createEmptyBirthCertificateEntry()],
      militaryIdEntries: profile.militaryIdEntries.length > 0 ? profile.militaryIdEntries : [createEmptyMilitaryIdEntry()],
      passportEntries: profile.passportEntries.length > 0 ? profile.passportEntries : [createEmptyPassportEntry()],
      addressEntries: profile.addressEntries.length > 0 ? profile.addressEntries : [createEmptyAddressEntry()],
      comment: profile.comment,
      legacyNotes: profile.legacyNotes,
      legalEntityId: clientData.legalEntityId ? String(clientData.legalEntityId) : '',
      responsibleUserId: clientData.responsibleUserId ? String(clientData.responsibleUserId) : '',
      isActive: clientData.isActive ?? true,
      clientStatusId: clientData.clientStatusId ? String(clientData.clientStatusId) : '',
    });
    // setProfileDocuments({
    //   passport: profile.passports[0] ?? '',
    //   birthCertificate: profile.birthCertificates[0] ?? '',
    //   militaryId: profile.militaryIds[0] ?? '',
    // });
  }, [clientData]);

  const caseIdForQuery = selectedCaseId === 'all' ? undefined : Number(selectedCaseId);

  const { fromDateStr, toDateStr } = useMemo(() => {
    if (monthFrom && monthTo) {
      return { fromDateStr: ymStart(monthFrom), toDateStr: ymEnd(monthTo) };
    }
    if (monthFrom && !monthTo) {
      const f = ymStart(monthFrom);
      return { fromDateStr: f, toDateStr: ymEnd(monthFrom) };
    }
    if (!monthFrom && monthTo) {
      const t = ymEnd(monthTo);
      return { fromDateStr: ymStart(monthTo), toDateStr: t };
    }
    return { fromDateStr: MIN_DATE, toDateStr: MAX_DATE };
  }, [monthFrom, monthTo]);

  const {
    payments,
    total: paymentsTotal,
    loading: loadingPayments,
    error: paymentsError,
    refresh: refreshPayments,
  } = usePaymentsPaged({
    from: fromDateStr,
    to: toDateStr,
    clientId,
    caseId: caseIdForQuery,
    status: toPaymentStatusFilter(statusFilter),
    sortBy: 'date',
    sortDir: 'asc',
    page: paymentPage,
    pageSize: paymentPageSize,
  });

  const visiblePayments = payments;
  const visiblePaymentTableColumns = useMemo(() => {
    const columnMap = new Map(PAYMENT_TABLE_COLUMNS.map((column) => [column.key, column]));
    return paymentOrderedVisibleColumns.flatMap((columnKey) => {
      const column = columnMap.get(columnKey);
      return column ? [column] : [];
    });
  }, [paymentOrderedVisibleColumns]);
  const visibleInvoiceTableColumns = useMemo(() => {
    const columnMap = new Map(INVOICE_TABLE_COLUMNS.map((column) => [column.key, column]));
    return invoiceOrderedVisibleColumns.flatMap((columnKey) => {
      const column = columnMap.get(columnKey);
      return column ? [column] : [];
    });
  }, [invoiceOrderedVisibleColumns]);
  const visibleActTableColumns = useMemo(() => {
    const columnMap = new Map(ACT_TABLE_COLUMNS.map((column) => [column.key, column]));
    return actOrderedVisibleColumns.flatMap((columnKey) => {
      const column = columnMap.get(columnKey);
      return column ? [column] : [];
    });
  }, [actOrderedVisibleColumns]);
  const visibleContractTableColumns = useMemo(() => {
    const columnMap = new Map(CONTRACT_TABLE_COLUMNS.map((column) => [column.key, column]));
    return contractOrderedVisibleColumns.flatMap((columnKey) => {
      const column = columnMap.get(columnKey);
      return column ? [column] : [];
    });
  }, [contractOrderedVisibleColumns]);
  const paymentTotalPages = useMemo(() => {
    if (!paymentPageSize) return 1;
    return Math.max(1, Math.ceil((paymentsTotal ?? 0) / paymentPageSize));
  }, [paymentPageSize, paymentsTotal]);

  useEffect(() => {
    if (activeSection !== 'payments') setPaymentTableSettingsOpen(false);
    if (activeSection !== 'accounts') setInvoiceTableSettingsOpen(false);
    if (activeSection !== 'acts') setActTableSettingsOpen(false);
    if (activeSection !== 'contracts') setContractTableSettingsOpen(false);
  }, [activeSection]);

  useEffect(() => {
    setPaymentPage(1);
  }, [caseIdForQuery, fromDateStr, paymentPageSize, statusFilter, toDateStr]);

  const actModalClients = useMemo(() => {
    const normalizedName = clientName && clientName !== '...' ? clientName : `Клиент #${clientId}`;
    if (actClients.length === 0) {
      return [
        {
          id: clientId,
          name: normalizedName,
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '1970-01-01T00:00:00.000Z',
          isActive: true,
          responsibleUserId: clientData?.responsibleUserId ?? null,
          responsibleUserName: clientData?.responsibleUserName ?? null,
        } satisfies Client,
      ];
    }

    const hasClient = actClients.some((c) => c.id === clientId);
    if (!hasClient) {
      return [
        ...actClients,
        {
          id: clientId,
          name: normalizedName,
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '1970-01-01T00:00:00.000Z',
          isActive: true,
        } satisfies Client,
      ];
    }

    return actClients;
  }, [actClients, clientId, clientName]);

  const invoiceModalClients = useMemo(() => {
    const normalizedName = clientName && clientName !== '...' ? clientName : `Клиент #${clientId}`;
    if (invoiceClients.length === 0) {
      return [
        {
          id: clientId,
          name: normalizedName,
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '1970-01-01T00:00:00.000Z',
          isActive: true,
        } satisfies Client,
      ];
    }

    const hasClient = invoiceClients.some((c) => c.id === clientId);
    if (!hasClient) {
      return [
        ...invoiceClients,
        {
          id: clientId,
          name: normalizedName,
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '1970-01-01T00:00:00.000Z',
          isActive: true,
          responsibleUserId: clientData?.responsibleUserId ?? null,
          responsibleUserName: clientData?.responsibleUserName ?? null,
        } satisfies Client,
      ];
    }

    return invoiceClients;
  }, [clientData?.responsibleUserId, clientData?.responsibleUserName, invoiceClients, clientId, clientName]);

  const contractDefaultClients = useMemo<ContractClient[]>(() => {
    const normalizedName =
      clientName && clientName !== '...'
        ? clientName
        : clientData?.name ?? `Клиент #${clientId}`;

    return [
      {
        id: clientId,
        name: normalizedName,
        company: clientData?.company ?? undefined,
      },
    ];
  }, [clientId, clientName, clientData?.name, clientData?.company]);

  const actSummaryBuckets = useMemo(
    () =>
      actsSummary ?? {
        created: { amount: 0, count: 0 },
        transferred: { amount: 0, count: 0 },
        signed: { amount: 0, count: 0 },
        terminated: { amount: 0, count: 0 },
        totalAmount: 0,
        totalCount: 0,
      },
    [actsSummary],
  );

  const actSummaryCards = useMemo(
    () => [
      {
        key: 'signed' as const,
        title: t('actSummarySigned') ?? 'Подписано',
        bucket: actSummaryBuckets.signed,
        icon: CheckCircle2,
        accent: 'text-emerald-700 bg-emerald-50 border border-emerald-100',
      },
      {
        key: 'transferred' as const,
        title: t('actSummaryTransferred') ?? 'Передано',
        bucket: actSummaryBuckets.transferred,
        icon: Send,
        accent: 'text-amber-700 bg-amber-50 border border-amber-100',
      },
      {
        key: 'terminated' as const,
        title: t('actSummaryTerminated') ?? 'Расторгнуто',
        bucket: actSummaryBuckets.terminated,
        icon: Ban,
        accent: 'text-rose-700 bg-rose-50 border border-rose-100',
      },
    ],
    [actSummaryBuckets, t],
  );

  const actStatusLabels = useMemo<Record<ActStatus, string>>(
    () => ({
      Created: t('actStatusCreated') ?? 'Создан',
      Transferred: t('actStatusTransferred') ?? 'Передано',
      Signed: t('actStatusSigned') ?? 'Подписано',
      Terminated: t('actStatusTerminated') ?? 'Расторгнуто',
    }),
    [t],
  );

  const actTotalPages = useMemo(() => {
    const size = actsPagination.pageSize || actPageSize;
    if (!size) return 1;
    return Math.max(1, Math.ceil((actsPagination.total ?? 0) / size));
  }, [actsPagination.pageSize, actsPagination.total, actPageSize]);

  useEffect(() => {
    setActPage(1);
  }, [actPageSize]);

  const editClientLabel = t('editClient') ?? 'Изменить клиента';
  const addCaseLabel = t('addCase') ?? 'Добавить дело';

  const sortedLegalEntities = useMemo(
    () =>
      [...legalEntities].sort((a, b) =>
        a.shortName.localeCompare(b.shortName, undefined, { sensitivity: 'base' }),
      ),
    [legalEntities],
  );

  const sortedClientStatuses = useMemo(
    () =>
      [...clientStatuses].sort((a, b) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
      ),
    [clientStatuses],
  );



  const selectedLegalEntity = useMemo(() => {
    if (!clientFormState.legalEntityId) return null;
    return sortedLegalEntities.find((entity) => entity.id === Number(clientFormState.legalEntityId)) ?? null;
  }, [clientFormState.legalEntityId, sortedLegalEntities]);

  const selectedClientStatus = useMemo(() => {
    if (!clientFormState.clientStatusId) return null;
    return (
      sortedClientStatuses.find((status) => status.id === Number(clientFormState.clientStatusId)) ??
      null
    );
  }, [clientFormState.clientStatusId, sortedClientStatuses]);

  const canManageClientResponsible = userIsAdmin && clientPermissions.canEdit;

  const clientResponsibleOptions = useMemo(() => {
    const options = [...actResponsibles];
    const currentResponsibleId = clientFormState.responsibleUserId
      ? Number(clientFormState.responsibleUserId)
      : null;
    if (
      currentResponsibleId
      && !options.some((responsible) => responsible.id === currentResponsibleId)
      && clientData?.responsibleUserName
    ) {
      options.push({ id: currentResponsibleId, fullName: clientData.responsibleUserName });
    }

    return options.sort((a, b) =>
      a.fullName.localeCompare(b.fullName, undefined, { sensitivity: 'base' }),
    );
  }, [actResponsibles, clientData?.responsibleUserName, clientFormState.responsibleUserId]);

  const ensureActLookups = useCallback(async () => {
    if (actLookupsLoading) return;
    if (actClients.length > 0 && actResponsibles.length > 0 && !actLookupsError) return;
    setActLookupsLoading(true);
    try {
      const [clientsResponse, responsiblesResponse] = await Promise.all([
        apiService.getClients(),
        apiService.getActResponsibles(),
      ]);
      setActClients(clientsResponse ?? []);
      setActResponsibles(responsiblesResponse ?? []);
      setActLookupsError(null);
    } catch (error) {
      setActLookupsError(error instanceof Error ? error.message : 'Не удалось загрузить справочники');
    } finally {
      setActLookupsLoading(false);
    }
  }, [actClients.length, actResponsibles.length, actLookupsError, actLookupsLoading]);

  useEffect(() => {
    if (actModalOpen) {
      void ensureActLookups();
    }
  }, [actModalOpen, ensureActLookups]);

  useEffect(() => {
    if (invoiceModalOpen || canManageClientResponsible) {
      void ensureActLookups();
    }
  }, [canManageClientResponsible, ensureActLookups, invoiceModalOpen]);

  const openAddPayment = () => {
    if (!clientPermissions.canCreate) {
      showPermissionWarning(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
      return;
    }
    setActiveSection('payments');
    setEditingPayment(null);
    setPayModalOpen(true);
  };
  const openEditPayment = (p: Payment) => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
      return;
    }
    setActiveSection('payments');
    setEditingPayment(p);
    setPayModalOpen(true);
  };
  const openAddAct = () => {
    if (!actPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления актов.');
      return;
    }
    setActiveSection('acts');
    setActModalMode('create');
    setSelectedAct(null);
    setActModalError(null);
    setActModalOpen(true);
  };
  const openEditAct = (act: Act) => {
    if (!actPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования актов.');
      return;
    }
    setActiveSection('acts');
    setActModalMode('edit');
    setSelectedAct(act);
    setActModalError(null);
    setActModalOpen(true);
  };
  const closePayModal = () => {
    setPayModalOpen(false);
    setEditingPayment(null);
  };
  const closeActModal = () => {
    setActModalOpen(false);
    setSelectedAct(null);
    setActModalError(null);
  };

  const toBody = (p: PaymentPayload): PaymentPayload => ({
    ...p,
    notes: p.notes ?? '',
    systemNotes: p.systemNotes ?? '',
    clientId: p.clientId ?? clientId ?? null,
    clientCaseId: p.clientCaseId ?? caseIdForQuery ?? null,
    dealTypeId: p.dealTypeId ?? null,
    incomeTypeId: p.incomeTypeId ?? null,
    paymentSourceId: p.paymentSourceId ?? null,
    paymentStatusId: p.paymentStatusId ?? null,
    account: p.account ?? null,
    accountDate: p.accountDate ?? null,
  });

  const submitPayment = async (payload: PaymentUpsert) => {
    const isEdit = 'id' in payload;
    if (isEdit && !clientPermissions.canEdit) {
      showPermissionWarning(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
      return;
    }
    if (!isEdit && !clientPermissions.canCreate) {
      showPermissionWarning(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
      return;
    }
    try {
      if (isEdit) {
        const { id, ...rest } = payload;
        await apiService.updatePayment(id, toBody(rest));
      } else {
        await apiService.createPayment(toBody(payload));
      }
      await refreshPayments();
      bumpStats();
      toast.success(isEdit ? 'Платёж обновлён.' : 'Платёж создан.', 'Платежи');
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(
          error,
          isEdit ? 'Не удалось сохранить платёж' : 'Не удалось создать платёж',
        ),
        'Платежи',
      );
      throw error;
    }
  };

  const removePayment = async (id: number) => {
    if (!clientPermissions.canDelete) {
      showPermissionWarning(t('permissionNoDeletePayment') ?? 'Недостаточно прав для удаления платежей.');
      return;
    }
    if (!(await confirm({ title: 'Подтверждение удаления', description: 'Удалить платёж?', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }))) return;
    try {
      await apiService.deletePayment(id);
      if (payments.length === 1 && paymentPage > 1) {
        setPaymentPage((currentPage) => Math.max(1, currentPage - 1));
      } else {
        await refreshPayments();
      }
      bumpStats();
      toast.success('Платёж удалён.', 'Платежи');
      return true;
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось удалить платёж'), 'Платежи');
      return false;
    }
  };

  const submitAct = async (payload: ActInput) => {
    const resolvedClientId = payload.clientId ?? clientId;
    if (!resolvedClientId) {
      setActModalError('Клиент не выбран');
      return;
    }

    setActSubmitting(true);
    setActModalError(null);
    const body: ActInput = {
      ...payload,
      clientId: resolvedClientId,
    };

    try {
      const isEdit = actModalMode === 'edit' && selectedAct;
      if (isEdit && !actPermissions.canEdit) {
        showPermissionWarning('Недостаточно прав для редактирования актов.');
        setActSubmitting(false);
        return;
      }
      if (!isEdit && !actPermissions.canCreate) {
        showPermissionWarning('Недостаточно прав для добавления актов.');
        setActSubmitting(false);
        return;
      }
      if (isEdit && selectedAct) {
        await updateAct(selectedAct.id, body);
      } else {
        await createAct(body);
      }
      closeActModal();
    } catch (error) {
      setActModalError(error instanceof Error ? error.message : 'Не удалось сохранить акт');
    } finally {
      setActSubmitting(false);
    }
  };

  const removeAct = async (act: Act) => {
    if (!actPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления актов.');
      return;
    }
    if (!(await confirm({ title: 'Подтверждение удаления', description: `Удалить акт №${act.number}?`, confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }))) return;
    try {
      await deleteAct(act.id);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось удалить акт'), 'Ошибка удаления');
    }
  };

  const handleActSort = (key: ActsSortKey) => {
    setActSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' };
    });
    setActPage(1);
  };

  const resetActFilters = () => {
    setActFrom(formatLocalYMD(new Date(new Date().getFullYear(), 0, 1)));
    setActTo(formatLocalYMD(new Date()));
    setActStatusFilter('all');
    setActSearch('');
    setActPage(1);
  };

  const openAddCase = () => {
    if (!clientPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления дел.');
      return;
    }
    setEditingCase(null);
    setCaseMode('create');
    setCaseModalOpen(true);
  };
  const saveCase = async (patch: Partial<ClientCase>): Promise<void> => {
    if (caseMode === 'edit' && editingCase) {
      if (!clientPermissions.canEdit) {
        showPermissionWarning('Недостаточно прав для редактирования дел.');
        return;
      }
    } else if (!clientPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления дел.');
      return;
    }

    const base: Omit<ClientCase, 'id' | 'createdAt' | 'payments'> = {
      clientId,
      title: (patch.title ?? editingCase?.title ?? '').trim(),
      description: (patch.description ?? editingCase?.description ?? '').trim(),
      status: (patch.status ?? editingCase?.status ?? 'Open') as ClientCase['status'],
    };

    if (caseMode === 'edit' && editingCase) {
      const updated = await apiService.updateCase(editingCase.id, base);
      setCasesLocal((prev) =>
        (prev ?? []).map((item) => (item.id === updated.id ? { ...item, ...updated } : item)),
      );
      setCaseModalOpen(false);
      setEditingCase(updated);
    } else {
      const created = await apiService.createCase(base);
      setCasesLocal((prev) => [created, ...(prev ?? [])]);
      setSelectedCaseId(created.id);
      setCaseModalOpen(false);
      setEditingCase(null);
    }
  };

  const deleteCase = async (): Promise<void> => {
    if (!editingCase) return;
    if (!clientPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления дел.');
      return;
    }
    await apiService.deleteCase(editingCase.id);
    setCasesLocal((prev) => (prev ?? []).filter((item) => item.id !== editingCase.id));
    setCaseModalOpen(false);
    setEditingCase(null);
    setSelectedCaseId('all');
  };

  const notFound = !clientData && clientName === `Client #${clientId}`;

  const handleClientFormChange = (
    event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const { name, value } = event.target;
    if (name === 'legalEntityId') {
      setClientFormState((prev) => ({ ...prev, legalEntityId: value }));
      return;
    }
    if (name === 'responsibleUserId') {
      setClientFormState((prev) => ({ ...prev, responsibleUserId: value }));
      return;
    }
    if (name === 'clientStatusId') {
      setClientFormState((prev) => ({ ...prev, clientStatusId: value }));
      return;
    }
    const el = event.target as HTMLInputElement;
    setClientFormState((prev) => ({
      ...prev,
      [name]: el.type === 'checkbox' ? el.checked : value,
    }));
  };

  const updateListField = (
    field: 'phones' | 'emails' | 'addresses' | ClientDocumentKey,
    index: number,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      [field]: prev[field].map((item, i) => (i === index ? value : item)),
    }));
  };

  const addListFieldRow = (field: 'phones' | 'emails' | 'addresses' | ClientDocumentKey) => {
    setClientFormState((prev) => ({ ...prev, [field]: [...prev[field], ''] }));
  };

    const removeListFieldRow = (
    field: 'phones' | 'emails' | 'addresses' | ClientDocumentKey,
    index: number,
  ) => {
    setClientFormState((prev) => {
      const nextValues = prev[field].filter((_, i) => i !== index);
      return { ...prev, [field]: nextValues.length ? nextValues : [''] };
    });
  };

const updateEducation = (index: number, key: keyof ClientEducationItem, value: string) => {
    setClientFormState((prev) => ({
      ...prev,
      education: prev.education.map((item, i) => (i === index ? { ...item, [key]: value } : item)),
    }));
  };

  const updateProfession = (index: number, key: keyof ClientProfessionItem, value: string) => {
    setClientFormState((prev) => ({
      ...prev,
      professions: prev.professions.map((item, i) => (i === index ? { ...item, [key]: value } : item)),
    }));
  };

  const updateWorkplace = (index: number, key: keyof ClientWorkplaceItem, value: string) => {
    setClientFormState((prev) => ({
      ...prev,
      workplaces: prev.workplaces.map((item, i) => (i === index ? { ...item, [key]: value } : item)),
    }));
  };

  const updateRelative = (index: number, key: keyof ClientRelativeItem, value: string) => {
    setClientFormState((prev) => ({
      ...prev,
      relatives: prev.relatives.map((item, i) => (i === index ? { ...item, [key]: value } : item)),
    }));
  };

  const updatePassportEntry = (index: number, key: keyof ClientPassportItem, value: string) => {
    setClientFormState((prev) => ({
      ...prev,
      passportEntries: prev.passportEntries.map((item, i) => (i === index ? { ...item, [key]: value } : item)),
    }));
  };

  const addPassportEntry = () => {
    setClientFormState((prev) => ({ ...prev, passportEntries: [...prev.passportEntries, createEmptyPassportEntry()] }));
  };

  const removePassportEntry = (index: number) => {
    setClientFormState((prev) => ({
      ...prev,
      passportEntries: prev.passportEntries.filter((_, i) => i !== index).length > 0
        ? prev.passportEntries.filter((_, i) => i !== index)
        : [createEmptyPassportEntry()],
    }));
  };

  const updateAddressEntry = (index: number, key: keyof ClientAddressItem, value: string) => {
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: prev.addressEntries.map((item, i) => {
        if (i !== index) return item;
        const updated = { ...item, [key]: value };
        if (['postalCode', 'region', 'district', 'city', 'settlement', 'street', 'house', 'flat'].includes(key)) {
          return {
            ...updated,
            fullAddress: composeAddressFromParts(updated),
          };
        }
        return updated;
      }),
    }));
  };

  const updateAddressFormEntry = (index: number, key: keyof ClientAddressEntryFormValue, value: string) => {
    updateAddressEntry(index, key as keyof ClientAddressItem, value);
  };

  const addAddressEntry = () => {
    setClientFormState((prev) => ({ ...prev, addressEntries: [...prev.addressEntries, createEmptyAddressEntry()] }));
  };

  const removeAddressEntry = (index: number) => {
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: prev.addressEntries.filter((_, i) => i !== index).length > 0
        ? prev.addressEntries.filter((_, i) => i !== index)
        : [createEmptyAddressEntry()],
    }));
  };

  const toggleAddressAdditionalType = (index: number, type: string) => {
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: prev.addressEntries.map((item, i) => {
        if (i !== index) return item;
        const has = item.additionalTypes.includes(type);
        return {
          ...item,
          additionalTypes: has ? item.additionalTypes.filter((value) => value !== type) : [...item.additionalTypes, type],
        };
      }),
    }));
  };

  const updateBirthCertificateEntry = (index: number, key: keyof ClientBirthCertificateItem, value: string) => {
    setClientFormState((prev) => ({
      ...prev,
      birthCertificateEntries: prev.birthCertificateEntries.map((item, i) => (i === index ? { ...item, [key]: value } : item)),
    }));
  };

  const addBirthCertificateEntry = () => {
    setClientFormState((prev) => ({ ...prev, birthCertificateEntries: [...prev.birthCertificateEntries, createEmptyBirthCertificateEntry()] }));
  };

  const removeBirthCertificateEntry = (index: number) => {
    setClientFormState((prev) => ({
      ...prev,
      birthCertificateEntries: prev.birthCertificateEntries.filter((_, i) => i !== index).length > 0
        ? prev.birthCertificateEntries.filter((_, i) => i !== index)
        : [createEmptyBirthCertificateEntry()],
    }));
  };

  const updateMilitaryIdEntry = (index: number, key: keyof ClientMilitaryIdItem, value: string) => {
    setClientFormState((prev) => ({
      ...prev,
      militaryIdEntries: prev.militaryIdEntries.map((item, i) => (i === index ? { ...item, [key]: value } : item)),
    }));
  };

  const addMilitaryIdEntry = () => {
    setClientFormState((prev) => ({ ...prev, militaryIdEntries: [...prev.militaryIdEntries, createEmptyMilitaryIdEntry()] }));
  };

  const removeMilitaryIdEntry = (index: number) => {
    setClientFormState((prev) => ({
      ...prev,
      militaryIdEntries: prev.militaryIdEntries.filter((_, i) => i !== index).length > 0
        ? prev.militaryIdEntries.filter((_, i) => i !== index)
        : [createEmptyMilitaryIdEntry()],
    }));
  };

  const loadAddressSuggestions = async (index: number, query: string) => {
    const raw = query.trim();
    if (raw.length < 3) {
      setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: [] }));
      return;
    }
    try {
      const suggestions = await apiService.suggestClientAddresses(raw, 7);
      setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: suggestions ?? [] }));
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось получить подсказки адресов'), 'DaData');
    }
  };

  const scheduleAddressSuggestions = (index: number, query: string) => {
    const timers = addressSuggestionTimersRef.current;
    if (timers[index]) window.clearTimeout(timers[index]);
    timers[index] = window.setTimeout(() => {
      void loadAddressSuggestions(index, query);
    }, 350);
  };

  const fillAddressEntryByDaData = async (index: number, rawInput?: string, options?: { silent?: boolean }) => {
    const entry = clientFormState.addressEntries[index];
    const raw = (rawInput || entry?.fullAddress || '').trim();
    if (!raw) return;
    try {
      const cleaned = await apiService.cleanAddress(raw);
      setClientFormState((prev) => ({
        ...prev,
        addressEntries: prev.addressEntries.map((item, i) => {
          if (i !== index) return item;
          const updated = {
            ...item,
            sourceQuery: raw,
            fullAddress: cleaned.result || raw,
            postalCode: cleaned.postalCode || '',
            region: cleaned.regionWithType || '',
            district: '',
            city: cleaned.cityWithType || '',
            settlement: cleaned.settlementWithType || '',
            street: cleaned.streetWithType || '',
            house: cleaned.house || '',
            flat: cleaned.flat || '',
            fiasId: cleaned.fiasId || '',
            kladrId: cleaned.kladrId || '',
            geoLat: cleaned.geoLat || '',
            geoLon: cleaned.geoLon || '',
          };
          return { ...updated, fullAddress: composeAddressFromParts(updated) || cleaned.result || raw };
        }),
      }));
    } catch (error) {
      try {
        const suggestions = await apiService.suggestClientAddresses(raw, 1);
        const fallbackSuggestion = suggestions?.[0];
        if (fallbackSuggestion) {
          const fallbackAddress = (fallbackSuggestion.unrestrictedValue || fallbackSuggestion.value || raw).trim();
          setClientFormState((prev) => ({
            ...prev,
            addressEntries: prev.addressEntries.map((item, i) => i === index ? {
              ...item,
              sourceQuery: raw,
              fullAddress: fallbackAddress,
              postalCode: fallbackSuggestion.postalCode || item.postalCode,
              region: fallbackSuggestion.regionWithType || item.region,
              district: fallbackSuggestion.areaWithType || item.district,
              city: fallbackSuggestion.cityWithType || item.city,
              settlement: fallbackSuggestion.settlementWithType || item.settlement,
              street: fallbackSuggestion.streetWithType || item.street,
              house: fallbackSuggestion.house || item.house,
              flat: fallbackSuggestion.flat || item.flat,
              fiasId: fallbackSuggestion.fiasId || item.fiasId,
              kladrId: fallbackSuggestion.kladrId || item.kladrId,
              geoLat: fallbackSuggestion.geoLat || item.geoLat,
              geoLon: fallbackSuggestion.geoLon || item.geoLon,
            } : item),
          }));
          return;
        }
      } catch {
        // ignore and show main error below
      }

      if (!options?.silent) {
        toast.error(extractDisplayErrorMessage(error, 'Не удалось найти адрес'), 'DaData');
        return;
      }
      throw error;
    }
  };

  const applyAddressSuggestion = async (index: number, suggestion: DadataAddressSuggestion) => {
    const selected = (suggestion.unrestrictedValue || suggestion.value || '').trim();
    if (!selected) return;

    // Быстро заполняем минимум из suggestion даже если clean endpoint временно недоступен.
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: prev.addressEntries.map((item, i) => i === index ? {
        ...item,
        sourceQuery: selected,
        fullAddress: selected,
        postalCode: suggestion.postalCode || item.postalCode,
        region: suggestion.regionWithType || item.region,
        district: suggestion.areaWithType || item.district,
        city: suggestion.cityWithType || item.city,
        settlement: suggestion.settlementWithType || item.settlement,
        street: suggestion.streetWithType || item.street,
        house: suggestion.house || item.house,
        flat: suggestion.flat || item.flat,
        fiasId: suggestion.fiasId || item.fiasId,
        kladrId: suggestion.kladrId || item.kladrId,
        geoLat: suggestion.geoLat || item.geoLat,
        geoLon: suggestion.geoLon || item.geoLon,
      } : item),
    }));

    setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: [] }));

    try {
      await fillAddressEntryByDaData(index, selected, { silent: true });
    } catch {
      // fallback уже применён выше через suggestion
    }
  };

  const handleClientFormSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!clientPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования клиента.');
      return;
    }
    setClientFormSaving(true);
    try {
      const primaryPassport = clientFormState.passportEntries[0] ?? createEmptyPassportEntry();
      const registrationEntry = clientFormState.addressEntries.find((entry) => {
        const type = entry.type.toLowerCase();
        return type.includes('рег') || type.includes('registration') || entry.additionalTypes.includes('registration');
      }) ?? clientFormState.addressEntries[0] ?? createEmptyAddressEntry();
      const actualEntry = clientFormState.addressEntries.find((entry) => {
        const type = entry.type.toLowerCase();
        return type.includes('факт') || type.includes('actual') || entry.additionalTypes.includes('actual');
      }) ?? clientFormState.addressEntries[1] ?? createEmptyAddressEntry();
      const profile: ClientExtendedProfile = {
        kind: clientFormState.kind,
        lastName: clientFormState.lastName,
        firstName: clientFormState.firstName,
        middleName: clientFormState.middleName,
        gender: clientFormState.gender,
        birthDate: clientFormState.birthDate,
        birthPlacePassport: primaryPassport.birthPlacePassport || clientFormState.birthPlacePassport,
        birthPlaceFias: primaryPassport.birthPlaceFias || clientFormState.birthPlaceFias,
        citizenship: clientFormState.citizenship,
        inn: clientFormState.inn,
        snils: clientFormState.snils,
        passportSeries: primaryPassport.series || clientFormState.passportSeries,
        passportNumber: primaryPassport.number || clientFormState.passportNumber,
        passportIssuedBy: primaryPassport.issuedBy || clientFormState.passportIssuedBy,
        passportIssueDate: primaryPassport.issueDate || clientFormState.passportIssueDate,
        passportDepartmentCode: primaryPassport.departmentCode || clientFormState.passportDepartmentCode,
        education: clientFormState.education,
        professions: clientFormState.professions,
        workplaces: clientFormState.workplaces,
        maritalStatus: clientFormState.maritalStatus,
        relatives: clientFormState.relatives,
        registrationAddress: registrationEntry.fullAddress || clientFormState.registrationAddress,
        registrationAddressPostalCode: registrationEntry.postalCode || clientFormState.registrationAddressPostalCode,
        registrationAddressFiasId: registrationEntry.fiasId || clientFormState.registrationAddressFiasId,
        registrationAddressKladrId: registrationEntry.kladrId || clientFormState.registrationAddressKladrId,
        registrationAddressGeoLat: registrationEntry.geoLat || clientFormState.registrationAddressGeoLat,
        registrationAddressGeoLon: registrationEntry.geoLon || clientFormState.registrationAddressGeoLon,
        actualAddress: actualEntry.fullAddress || clientFormState.actualAddress,
        actualAddressPostalCode: actualEntry.postalCode || clientFormState.actualAddressPostalCode,
        actualAddressFiasId: actualEntry.fiasId || clientFormState.actualAddressFiasId,
        actualAddressKladrId: actualEntry.kladrId || clientFormState.actualAddressKladrId,
        actualAddressGeoLat: actualEntry.geoLat || clientFormState.actualAddressGeoLat,
        actualAddressGeoLon: actualEntry.geoLon || clientFormState.actualAddressGeoLon,
        actualAddressMatchesRegistration: clientFormState.actualAddressMatchesRegistration,
        phones: clientFormState.phones,
        emails: clientFormState.emails,
        addresses: clientFormState.addresses,
        passports: clientFormState.passports,
        birthCertificates: clientFormState.birthCertificates,
        militaryIds: clientFormState.militaryIds,
        birthCertificateEntries: clientFormState.birthCertificateEntries,
        militaryIdEntries: clientFormState.militaryIdEntries,
        passportEntries: clientFormState.passportEntries,
        addressEntries: clientFormState.addressEntries,
        comment: clientFormState.comment,
        legacyNotes: clientFormState.legacyNotes,
      };
      const normalizedPhones = normalizeList(profile.phones);
      const normalizedEmails = normalizeList(profile.emails);
      const normalizedAddresses = normalizeList(profile.addresses);
      const registrationAddress = profile.registrationAddress.trim() || normalizedAddresses[0] || '';
      const actualAddress = profile.actualAddressMatchesRegistration
        ? registrationAddress
        : profile.actualAddress.trim();
      if (profile.kind === 'legal' && !clientFormState.legalEntityId) {
        toast.error(
          'Для карточки юридического лица выберите юридическое лицо из списка.',
          'Проверка данных',
        );
        return;
      }
      try {
        await handleUpdateClient({
          name: buildClientDisplayName(profile, selectedLegalEntity?.shortName ?? selectedLegalEntity?.fullName ?? undefined),
          email: normalizedEmails[0] ?? '',
          phone: normalizedPhones[0] ?? '',
          company: profile.kind === 'legal' ? selectedLegalEntity?.shortName ?? 'Юридическое лицо' : '',
          address: registrationAddress || actualAddress || normalizedAddresses[0] || '',
          notes: `${CLIENT_PROFILE_NOTES_PREFIX}${serializeClientExtendedProfile(profile)}`,
          isActive: clientFormState.isActive,
          legalEntityId:
            profile.kind === 'legal' && clientFormState.legalEntityId
              ? Number(clientFormState.legalEntityId)
              : null,
          responsibleUserId: clientFormState.responsibleUserId
            ? Number(clientFormState.responsibleUserId)
            : null,
          clientStatusId: clientFormState.clientStatusId
            ? Number(clientFormState.clientStatusId)
            : null,
        });
      toast.success('Изменения карточки клиента сохранены.', 'Клиент обновлен');
      } catch {
        return;
      }
    } finally {
      setClientFormSaving(false);
    }
  };

  if (visibleSections.length === 0) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center p-6">
        <div className="w-full max-w-lg rounded-2xl border border-red-200 bg-white px-6 py-10 text-center shadow-sm">
          <Ban className="mx-auto h-12 w-12 text-red-500" />
          <h1 className="mt-4 text-2xl font-semibold text-red-700">Доступ к данным клиента ограничен</h1>
          <p className="mt-2 text-sm text-red-600">
            Недостаточно прав для просмотра разделов этой карточки. Обратитесь к администратору.
          </p>
          <button
            type="button"
            onClick={onBack}
            className="mt-6 inline-flex items-center justify-center rounded-lg border border-red-300 px-4 py-2 text-sm font-medium text-red-600 hover:bg-red-50"
          >
            Назад
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-6">
        <div className="mb-6 flex flex-wrap items-center gap-3">
          <button
            onClick={onBack}
            className="p-1 hover:bg-gray-100 rounded-lg transition-colors shrink-0"
            title="Назад"
            aria-label="Назад"
          >
            <ArrowLeft size={24} />
          </button>
          <h1
            className="flex-1 min-w-0 text-2xl font-bold text-gray-900 flex flex-wrap items-center gap-3"
            title={clientName}>
            <span className="truncate">{clientName}</span>
            {clientData?.responsibleUserName && (
              <span className="inline-flex items-center rounded-full border border-slate-200 bg-slate-100 px-3 py-1 text-xs font-medium text-slate-700">
                Ответственный: {clientData.responsibleUserName}
              </span>
            )}
            <ClientStatusBadge status={clientData?.clientStatus} />
          </h1>
          <div className="ml-auto flex flex-wrap items-center gap-2">
            {clientPermissions.canEdit && (
              <button
                type="button"
                onClick={openClientProfile}
                className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-emerald-200 bg-white text-emerald-600 shadow-sm transition-colors hover:bg-emerald-50"
                title={editClientLabel}
                aria-label={editClientLabel}
              >
                <Edit className="h-5 w-5" />
              </button>
            )}
            {clientPermissions.canCreate && (
              <button
                type="button"
                onClick={openAddCase}
                className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-emerald-600 text-white shadow-sm transition-colors hover:bg-emerald-700"
                title={addCaseLabel}
                aria-label={addCaseLabel}
              >
                <Plus className="h-5 w-5" />
              </button>
            )}
          </div>
        </div>

        <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
          <div className="flex flex-wrap gap-2">
            {visibleSections.map((section) => {
              const meta = SECTION_META[section];
              const isActive = activeSection === section;
              return (
                <button
                  key={section}
                  type="button"
                  onClick={() => setActiveSection(section)}
                  className={`px-3 py-2 text-sm font-medium rounded-lg border transition-colors ${
                    isActive
                      ? 'bg-emerald-600 text-white border-emerald-600 shadow-sm'
                      : 'bg-white text-gray-600 border-gray-200 hover:bg-gray-50'
                  }`}
                >
                  {meta.label}
                </button>
              );
            })}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {clientPermissions.canCreate && (
              <button
                type="button"
                onClick={openAddPayment}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700"
              >
                <Plus size={16} /> {SECTION_META.payments.actionLabel}
              </button>
            )}
            {accountPermissions.canCreate && (
              <button
                type="button"
                onClick={openInvoiceCreate}
                className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700"
              >
                <Plus size={16} /> {SECTION_META.accounts.actionLabel}
              </button>
            )}
            {actPermissions.canCreate && (
              <button
                type="button"
                onClick={openAddAct}
                className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700"
              >
                <Plus size={16} /> {SECTION_META.acts.actionLabel}
              </button>
            )}
            {contractPermissions.canCreate && (
              <button
                type="button"
                onClick={openContractCreate}
                className="inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-indigo-700"
              >
                <Plus size={16} /> {SECTION_META.contracts.actionLabel}
              </button>
            )}
          </div>
        </div>

        {activeSection === 'profile' ? (
          <div className="bg-white rounded-xl shadow-sm border border-gray-100 mb-6 p-6">
            <div className="flex flex-wrap items-start justify-between gap-3 mb-4">
              <div>
                <h2 className="text-lg font-semibold text-gray-900">Карточка клиента</h2>
                <p className="text-sm text-gray-500">
                  Обновляйте контактные данные клиента и юридическую информацию.
                </p>
              </div>
              <div className="w-full max-w-2xl grid grid-cols-1 gap-4 md:grid-cols-2">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-2">
                    {'Ответственный' + requiredMark('ResponsibleUserId')}
                  </label>
                  <select
                    name="responsibleUserId"
                    value={clientFormState.responsibleUserId}
                    onChange={handleClientFormChange}
                    disabled={!canManageClientResponsible}
                    className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass('ResponsibleUserId', clientFormState.responsibleUserId)}`}
                  >
                    <option value="">Не указан</option>
                    {clientResponsibleOptions.map((responsible) => (
                      <option key={responsible.id} value={responsible.id}>
                        {responsible.fullName}
                      </option>
                    ))}
                  </select>
                  <p className="mt-2 text-xs text-gray-500">
                    {canManageClientResponsible
                      ? 'По умолчанию подставляется пользователь, который создал клиента.'
                      : 'Назначается автоматически при создании клиента. Изменение доступно администратору.'}
                  </p>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-2">{(t('clientStatus') || 'Статус клиента') + requiredMark('ClientStatusId')}</label>
                  <select name="clientStatusId" value={clientFormState.clientStatusId} onChange={handleClientFormChange} disabled={!clientPermissions.canEdit} className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass('ClientStatusId', clientFormState.clientStatusId)}`}>
                    <option value="">{t('clientStatusNotSelected') || 'Без статуса'}</option>
                    {sortedClientStatuses.map((status) => (<option key={status.id} value={status.id}>{status.name}</option>))}
                  </select>
                  {selectedClientStatus?.description && <p className="mt-2 text-xs text-gray-500">{selectedClientStatus.description}</p>}
                </div>
              </div>
            </div>
            {!clientPermissions.canEdit && (
              <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                Недостаточно прав для редактирования клиента. Доступен только просмотр.
              </div>
            )}
            <form onSubmit={handleClientFormSubmit} className="grid gap-4 md:grid-cols-2">
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-2">Тип карточки</label>
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                  <button
                    type="button"
                    onClick={() => setClientFormState((prev) => ({ ...prev, kind: 'individual', legalEntityId: '' }))}
                    disabled={!clientPermissions.canEdit}
                    className={`rounded-lg border px-3 py-2 text-sm ${clientFormState.kind === 'individual' ? 'border-emerald-500 bg-emerald-50 text-emerald-700' : 'border-gray-300 bg-white text-gray-700'} disabled:bg-gray-100`}
                  >
                    Физическое лицо
                  </button>
                  <button
                    type="button"
                    onClick={() => setClientFormState((prev) => ({ ...prev, kind: 'legal' }))}
                    disabled={!clientPermissions.canEdit}
                    className={`rounded-lg border px-3 py-2 text-sm ${clientFormState.kind === 'legal' ? 'border-emerald-500 bg-emerald-50 text-emerald-700' : 'border-gray-300 bg-white text-gray-700'} disabled:bg-gray-100`}
                  >
                    Юридическое лицо
                  </button>
                </div>
              </div>

              {clientFormState.kind === 'individual' ? (
                <>
                  <div className="md:col-span-2 grid gap-3 md:grid-cols-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">{'Фамилия' + requiredMark('LastName')}</label>
                      <input type="text" name="lastName" value={clientFormState.lastName} onChange={handleClientFormChange} required disabled={!clientPermissions.canEdit} className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass('LastName', clientFormState.lastName)}`} />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">{'Имя' + requiredMark('FirstName')}</label>
                      <input type="text" name="firstName" value={clientFormState.firstName} onChange={handleClientFormChange} required disabled={!clientPermissions.canEdit} className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass('FirstName', clientFormState.firstName)}`} />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">{'Отчество' + requiredMark('MiddleName')}</label>
                      <input type="text" name="middleName" value={clientFormState.middleName} onChange={handleClientFormChange} disabled={!clientPermissions.canEdit} className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass('MiddleName', clientFormState.middleName)}`} />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">Пол</label>
                      <select name="gender" value={clientFormState.gender} onChange={handleClientFormChange} disabled={!clientPermissions.canEdit} className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100">
                        <option value="">Не указан</option>
                        <option value="male">Мужской</option>
                        <option value="female">Женский</option>
                      </select>
                    </div>
                  </div>

                  <div className="md:col-span-2 grid gap-3 md:grid-cols-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">{'ИНН' + requiredMark('Inn')}</label>
                      <input type="text" name="inn" value={clientFormState.inn} onChange={handleClientFormChange} disabled={!clientPermissions.canEdit} className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass('Inn', clientFormState.inn)}`} />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">{'СНИЛС' + requiredMark('Snils')}</label>
                      <input type="text" name="snils" value={clientFormState.snils} onChange={handleClientFormChange} disabled={!clientPermissions.canEdit} className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass('Snils', clientFormState.snils)}`} />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">Дата рождения</label>
                      <input type="date" name="birthDate" value={clientFormState.birthDate} onChange={handleClientFormChange} disabled={!clientPermissions.canEdit} className={DATE_INPUT_CLASS} />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">Семейное положение</label>
                      <select name="maritalStatus" value={clientFormState.maritalStatus} onChange={handleClientFormChange} disabled={!clientPermissions.canEdit} className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100">
                        <option value="">Не указано</option>
                        {MARITAL_STATUS_OPTIONS.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                      </select>
                    </div>
                  </div>

                  <div className="md:col-span-2">
                    <div className="mb-3 flex flex-wrap gap-2">
                      {[
                        { id: 'main', label: 'Контакты', requiredKeys: ['Phones', 'Emails'], values: [...clientFormState.phones, ...clientFormState.emails] },
                        { id: 'passports', label: 'Паспорта', requiredKeys: ['Passports'], values: clientFormState.passportEntries },
                        { id: 'addresses', label: 'Адреса', requiredKeys: ['Addresses'], values: clientFormState.addressEntries },
                        { id: 'education', label: 'Образование (ОКСО)', requiredKeys: ['Education'], values: clientFormState.education },
                        { id: 'professions', label: 'Профессии (ОКПДТР)', requiredKeys: ['Professions'], values: clientFormState.professions },
                        { id: 'workplaces', label: 'Работа и должности', requiredKeys: ['Workplaces'], values: clientFormState.workplaces },
                        { id: 'relatives', label: 'Родственники', requiredKeys: ['Relatives'], values: clientFormState.relatives },
                        { id: 'birthCertificates', label: 'Свидетельства о рождении', requiredKeys: ['BirthCertificates'], values: clientFormState.birthCertificateEntries },
                        { id: 'militaryIds', label: 'Военные билеты', requiredKeys: ['MilitaryIds'], values: clientFormState.militaryIdEntries },
                        { id: 'poa', label: 'Доверенности', requiredKeys: ['PowersOfAttorney'], values: powersOfAttorney },
                      ].map((tab) => (
                        <button
                          key={tab.id}
                          type="button"
                          onClick={() => setActiveProfileGroup(tab.id as typeof activeProfileGroup)}
                          className={`rounded-lg border px-3 py-1.5 text-sm ${(() => {
                            const isActive = activeProfileGroup === tab.id;
                            const missing = hasMissingRequiredList(tab.requiredKeys, tab.values ?? []);
                            const requiredAndFilled = hasAnyRequiredList(tab.requiredKeys) && !missing;
                            if (isActive && missing) return 'border-red-500 bg-red-50 text-red-700';
                            if (isActive && requiredAndFilled) return 'border-emerald-500 bg-emerald-50 text-emerald-700';
                            if (isActive) return 'border-emerald-500 bg-emerald-50 text-emerald-700';
                            if (missing) return 'border-red-300 text-red-700';
                            if (requiredAndFilled) return 'border-emerald-300 text-emerald-700';
                            return 'border-gray-300 text-gray-700';
                          })()}`}
                        >
                          {tab.label}{hasAnyRequiredList(tab.requiredKeys) ? ' *' : ''}
                        </button>
                      ))}
                    </div>
                  </div>
                </>
              ) : (
                <div className="md:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-2">{t('legalEntity') || 'Юридическое лицо'}</label>
                  <select name="legalEntityId" value={clientFormState.legalEntityId} onChange={handleClientFormChange} disabled={!clientPermissions.canEdit} className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100">
                    <option value="">Выберите юридическое лицо</option>
                    {sortedLegalEntities.map((entity) => (
                      <option key={entity.id} value={entity.id}>{entity.shortName}{entity.inn ? ` · ${entity.inn}` : ''}</option>
                    ))}
                  </select>
                </div>
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'passports' && (
                <div className="md:col-span-2 space-y-4">
                  {clientFormState.passportEntries.map((passport, index) => (
                    <div key={`passport-entry-${index}`} className="rounded-xl border border-slate-200 bg-slate-50 p-4">
                      <div className="mb-3 flex items-center justify-between">
                        <h3 className="text-sm font-semibold text-slate-800">Паспорт #{index + 1}</h3>
                        <button type="button" onClick={() => removePassportEntry(index)} disabled={!clientPermissions.canEdit} className="text-sm text-rose-600">Удалить</button>
                      </div>
                      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
                        <label className="text-sm text-slate-700"><span className="mb-1 block">Серия</span><input value={passport.series} onChange={(e) => updatePassportEntry(index, 'series', e.target.value)} disabled={!clientPermissions.canEdit} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
                        <label className="text-sm text-slate-700"><span className="mb-1 block">Номер</span><input value={passport.number} onChange={(e) => updatePassportEntry(index, 'number', e.target.value)} disabled={!clientPermissions.canEdit} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
                        <label className="text-sm text-slate-700"><span className="mb-1 block">Дата выдачи</span><input type="date" value={passport.issueDate} onChange={(e) => updatePassportEntry(index, 'issueDate', e.target.value)} disabled={!clientPermissions.canEdit} className={DATE_INPUT_CLASS} /></label>
                        <label className="text-sm text-slate-700"><span className="mb-1 block">Код подразделения</span><input value={passport.departmentCode} onChange={(e) => updatePassportEntry(index, 'departmentCode', e.target.value)} disabled={!clientPermissions.canEdit} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
                        <label className="text-sm text-slate-700"><span className="mb-1 block">Действует с</span><input type="date" value={passport.validFrom} onChange={(e) => updatePassportEntry(index, 'validFrom', e.target.value)} disabled={!clientPermissions.canEdit} className={DATE_INPUT_CLASS} /></label>
                        <label className="text-sm text-slate-700"><span className="mb-1 block">Действует по</span><input type="date" value={passport.validTo} onChange={(e) => updatePassportEntry(index, 'validTo', e.target.value)} disabled={!clientPermissions.canEdit} className={DATE_INPUT_CLASS} /></label>
                        <label className="text-sm text-slate-700 xl:col-span-2"><span className="mb-1 block">Кем выдан</span><input value={passport.issuedBy} onChange={(e) => updatePassportEntry(index, 'issuedBy', e.target.value)} disabled={!clientPermissions.canEdit} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
                        <label className="text-sm text-slate-700 xl:col-span-2"><span className="mb-1 block">Место рождения (как в паспорте)</span><input value={passport.birthPlacePassport} onChange={(e) => updatePassportEntry(index, 'birthPlacePassport', e.target.value)} disabled={!clientPermissions.canEdit} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
                        <label className="text-sm text-slate-700 xl:col-span-2"><span className="mb-1 block">Место рождения (ФИАС)</span><input value={passport.birthPlaceFias} onChange={(e) => updatePassportEntry(index, 'birthPlaceFias', e.target.value)} disabled={!clientPermissions.canEdit} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
                      </div>
                    </div>
                  ))}
                  <button type="button" onClick={addPassportEntry} disabled={!clientPermissions.canEdit} className="text-sm text-emerald-600">+ Добавить паспорт</button>
                </div>
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'addresses' && (
                <ClientAddressEntriesForm
                  entries={clientFormState.addressEntries}
                  canEdit={clientPermissions.canEdit}
                  dateInputClassName={DATE_INPUT_CLASS}
                  addressTypeOptions={ADDRESS_TYPE_OPTIONS}
                  suggestionsByIndex={addressSuggestionsByIndex}
                  onUpdateEntry={updateAddressFormEntry}
                  onRemoveEntry={removeAddressEntry}
                  onAddEntry={addAddressEntry}
                  onToggleAdditionalType={toggleAddressAdditionalType}
                  onScheduleSuggestions={scheduleAddressSuggestions}
                  onFindAddress={(index) => { void fillAddressEntryByDaData(index); }}
                  onApplySuggestion={applyAddressSuggestion}
                />
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'main' && (
                <div className="md:col-span-2 grid gap-4 md:grid-cols-2">
                  {(['phones', 'emails'] as const).map((field) => (
                    <div key={field}>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {field === 'phones' ? 'Телефоны' : 'Email'}
                      </label>
                      <div className="space-y-2">
                        {clientFormState[field].map((value, index) => (
                          <div key={`${field}-${index}`} className="flex gap-2">
                            <input
                              type={field === 'emails' ? 'email' : 'text'}
                              value={value}
                              onChange={(event) => updateListField(field, index, event.target.value)}
                              disabled={!clientPermissions.canEdit}
                              className="flex-1 px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100"
                            />
                            <button type="button" onClick={() => removeListFieldRow(field, index)} disabled={!clientPermissions.canEdit} className="px-3 py-2 rounded-lg border border-gray-300 text-sm">Удалить</button>
                          </div>
                        ))}
                        <button type="button" onClick={() => addListFieldRow(field)} disabled={!clientPermissions.canEdit} className="text-sm text-emerald-600">+ Добавить</button>
                      </div>
                    </div>
                  ))}
                </div>
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'education' && (
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-2">Образование (ОКСО)</label>
                <div className="space-y-2">
                  {clientFormState.education.map((item, index) => (
                    <div key={`education-${index}`} className="grid gap-2 md:grid-cols-4">
                      <input value={item.institution} onChange={(e) => updateEducation(index, 'institution', e.target.value)} placeholder="Учебное заведение" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                      <input value={item.specialty} onChange={(e) => updateEducation(index, 'specialty', e.target.value)} placeholder="Специальность" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                      <input value={item.diplomaNumber} onChange={(e) => updateEducation(index, 'diplomaNumber', e.target.value)} placeholder="№ диплома" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                      <input value={item.oksoCode} onChange={(e) => updateEducation(index, 'oksoCode', e.target.value)} placeholder="Код ОКСО" list="okso-codes" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                    </div>
                  ))}
                  <button type="button" onClick={() => setClientFormState((prev) => ({ ...prev, education: [...prev.education, createEmptyEducation()] }))} disabled={!clientPermissions.canEdit} className="text-sm text-emerald-600">+ Добавить образование</button>
                </div>
              </div>
              )}

              <datalist id="okso-codes">{oksoOptions.map((item) => <option key={item.code} value={item.code}>{item.name}</option>)}</datalist>
              <datalist id="okpdtr-codes">{okpdtrOptions.map((item) => <option key={item.code} value={item.code}>{item.name}</option>)}</datalist>

              {clientFormState.kind === 'individual' && activeProfileGroup === 'professions' && (
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-2">Профессии (ОКПДТР)</label>
                <div className="space-y-2">
                  {clientFormState.professions.map((item, index) => (
                    <div key={`profession-${index}`} className="grid gap-2 md:grid-cols-2">
                      <input value={item.profession} onChange={(e) => updateProfession(index, 'profession', e.target.value)} placeholder="Профессия" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                      <input value={item.okpdtrCode} onChange={(e) => updateProfession(index, 'okpdtrCode', e.target.value)} placeholder="Код ОКПДТР" list="okpdtr-codes" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                    </div>
                  ))}
                  <button type="button" onClick={() => setClientFormState((prev) => ({ ...prev, professions: [...prev.professions, createEmptyProfession()] }))} disabled={!clientPermissions.canEdit} className="text-sm text-emerald-600">+ Добавить профессию</button>
                </div>
              </div>
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'workplaces' && (
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-2">Места работы и должности</label>
                <div className="space-y-2">
                  {clientFormState.workplaces.map((item, index) => (
                    <div key={`workplace-${index}`} className="grid gap-2 md:grid-cols-12">
                      <input value={item.organizationName} onChange={(e) => updateWorkplace(index, 'organizationName', e.target.value)} placeholder="Организация" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4" />
                      <input value={item.position} onChange={(e) => updateWorkplace(index, 'position', e.target.value)} placeholder="Должность" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4" />
                      <div className="md:col-span-2"><input type="date" value={item.startDate} onChange={(e) => updateWorkplace(index, 'startDate', e.target.value)} disabled={!clientPermissions.canEdit} className={DATE_INPUT_CLASS} /></div>
                      <div className="md:col-span-2"><input type="date" value={item.endDate} onChange={(e) => updateWorkplace(index, 'endDate', e.target.value)} disabled={!clientPermissions.canEdit} className={DATE_INPUT_CLASS} /></div>
                    </div>
                  ))}
                  <button type="button" onClick={() => setClientFormState((prev) => ({ ...prev, workplaces: [...prev.workplaces, createEmptyWorkplace()] }))} disabled={!clientPermissions.canEdit} className="text-sm text-emerald-600">+ Добавить место работы</button>
                  <button
                    type="button"
                    disabled={!clientPermissions.canEdit}
                    onClick={() => {
                      setLegalEntityPromptError(null);
                      setLegalEntityDraft({ shortName: '', inn: '' });
                      setLegalEntityPromptOpen(true);
                    }}
                    className="text-sm text-indigo-600"
                  >
                    + Добавить организацию
                  </button>
                </div>
              </div>
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'relatives' && (
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-2">Близкие родственники</label>
                <div className="space-y-2">
                  {clientFormState.relatives.map((item, index) => (
                    <div key={`relative-${index}`} className="grid gap-2 md:grid-cols-3">
                      <input value={item.relation} onChange={(e) => updateRelative(index, 'relation', e.target.value)} placeholder="Степень родства" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                      <input value={item.fullName} onChange={(e) => updateRelative(index, 'fullName', e.target.value)} placeholder="ФИО" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                      <input value={item.birthYear} onChange={(e) => updateRelative(index, 'birthYear', e.target.value)} placeholder="Год рождения" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                    </div>
                  ))}
                  <button type="button" onClick={() => setClientFormState((prev) => ({ ...prev, relatives: [...prev.relatives, createEmptyRelative()] }))} disabled={!clientPermissions.canEdit} className="text-sm text-emerald-600">+ Добавить родственника</button>
                </div>
              </div>
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'birthCertificates' && (
                <div className="md:col-span-2 space-y-4">
                  <div>
                    <div className="mb-2 flex items-center justify-between">
                      <label className="block text-sm font-medium text-gray-700">Свидетельства о рождении</label>
                      <button type="button" onClick={addBirthCertificateEntry} disabled={!clientPermissions.canEdit} className="text-sm text-emerald-600">+ Добавить свидетельство</button>
                    </div>
                    <div className="space-y-2">
                      {clientFormState.birthCertificateEntries.map((doc, index) => (
                        <div key={`birth-certificate-${index}`} className="grid gap-2 rounded-lg border border-gray-200 p-3 md:grid-cols-3">
                          <input value={doc.series} onChange={(e) => updateBirthCertificateEntry(index, 'series', e.target.value)} placeholder="Серия" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <input value={doc.number} onChange={(e) => updateBirthCertificateEntry(index, 'number', e.target.value)} placeholder="Номер" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <input type="date" value={doc.issueDate} onChange={(e) => updateBirthCertificateEntry(index, 'issueDate', e.target.value)} disabled={!clientPermissions.canEdit} className={DATE_INPUT_CLASS} />
                          <input value={doc.actRecordNumber} onChange={(e) => updateBirthCertificateEntry(index, 'actRecordNumber', e.target.value)} placeholder="№ актовой записи" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <input value={doc.childFullName} onChange={(e) => updateBirthCertificateEntry(index, 'childFullName', e.target.value)} placeholder="ФИО ребенка" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <button type="button" onClick={() => removeBirthCertificateEntry(index)} disabled={!clientPermissions.canEdit} className="text-left text-sm text-rose-600">Удалить</button>
                          <input value={doc.issuedBy} onChange={(e) => updateBirthCertificateEntry(index, 'issuedBy', e.target.value)} placeholder="Кем выдано" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3" />
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'militaryIds' && (
                <div className="md:col-span-2 space-y-4">
                  <div>
                    <div className="mb-2 flex items-center justify-between">
                      <label className="block text-sm font-medium text-gray-700">Военные билеты</label>
                      <button type="button" onClick={addMilitaryIdEntry} disabled={!clientPermissions.canEdit} className="text-sm text-emerald-600">+ Добавить военный билет</button>
                    </div>
                    <div className="space-y-2">
                      {clientFormState.militaryIdEntries.map((doc, index) => (
                        <div key={`military-id-${index}`} className="grid gap-2 rounded-lg border border-gray-200 p-3 md:grid-cols-3">
                          <input value={doc.series} onChange={(e) => updateMilitaryIdEntry(index, 'series', e.target.value)} placeholder="Серия" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <input value={doc.number} onChange={(e) => updateMilitaryIdEntry(index, 'number', e.target.value)} placeholder="Номер" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <input type="date" value={doc.issueDate} onChange={(e) => updateMilitaryIdEntry(index, 'issueDate', e.target.value)} disabled={!clientPermissions.canEdit} className={DATE_INPUT_CLASS} />
                          <input value={doc.rank} onChange={(e) => updateMilitaryIdEntry(index, 'rank', e.target.value)} placeholder="Воинское звание" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <input value={doc.category} onChange={(e) => updateMilitaryIdEntry(index, 'category', e.target.value)} placeholder="Категория запаса" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <input value={doc.fitnessCategory} onChange={(e) => updateMilitaryIdEntry(index, 'fitnessCategory', e.target.value)} placeholder="Категория годности" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <input value={doc.vusCode} onChange={(e) => updateMilitaryIdEntry(index, 'vusCode', e.target.value)} placeholder="Код ВУС" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg" />
                          <button type="button" onClick={() => removeMilitaryIdEntry(index)} disabled={!clientPermissions.canEdit} className="text-left text-sm text-rose-600">Удалить</button>
                          <input value={doc.issuedBy} onChange={(e) => updateMilitaryIdEntry(index, 'issuedBy', e.target.value)} placeholder="Кем выдан" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3" />
                          <input value={doc.militaryCommissariat} onChange={(e) => updateMilitaryIdEntry(index, 'militaryCommissariat', e.target.value)} placeholder="Военный комиссариат" disabled={!clientPermissions.canEdit} className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3" />
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              )}

              {clientFormState.kind === 'individual' && activeProfileGroup === 'poa' && (
              <div className="md:col-span-2">
                <div className="mb-2 flex items-center justify-between">
                  <label className="block text-sm font-medium text-gray-700">Доверенности</label>
                  <button
                    type="button"
                    disabled={!clientPermissions.canEdit}
                    onClick={() => {
                      setPoaPromptError(null);
                      setPoaDraft({ number: '', issuedAt: formatLocalYMD(new Date()) });
                      setPoaPromptOpen(true);
                    }}
                    className="text-sm text-emerald-600"
                  >
                    + Добавить доверенность
                  </button>
                </div>
                <div className="rounded-lg border border-gray-200 p-3">
                  {powersOfAttorney.length === 0 ? (
                    <p className="text-sm text-gray-500">Доверенности не найдены.</p>
                  ) : (
                    <div className="space-y-2">
                      {powersOfAttorney.map((poa) => (
                        <div key={poa.id} className="rounded border border-gray-200 px-3 py-2 text-sm">
                          №{poa.number} · {toRuDate(poa.issuedAt)} {poa.validUntil ? `— до ${toRuDate(poa.validUntil)}` : ""}
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              </div>
              )}

              <div className="md:col-span-2 grid gap-3 md:grid-cols-2">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-2">{'Комментарий к расширенной карточке' + requiredMark('Comment')}</label>
                  <textarea name="comment" value={clientFormState.comment} onChange={handleClientFormChange} rows={3} disabled={!clientPermissions.canEdit} className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass('Comment', clientFormState.comment)}`} placeholder={t('additionalInfo')} />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-2">Исторические заметки (не удаляются)</label>
                  <textarea name="legacyNotes" value={clientFormState.legacyNotes} onChange={handleClientFormChange} rows={3} disabled={!clientPermissions.canEdit} className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100" placeholder={t('notes') || 'Заметки'} />
                </div>
              </div>
              <div className="md:col-span-2 flex items-center gap-3">
                <input
                  type="checkbox"
                  name="isActive"
                  checked={clientFormState.isActive}
                  onChange={handleClientFormChange}
                  disabled={!clientPermissions.canEdit}
                  className="h-4 w-4 text-emerald-600 focus:ring-emerald-500 border-gray-300 rounded"
                  id="client-detail-is-active"
                />
                <label htmlFor="client-detail-is-active" className="text-sm text-gray-700">
                  {t('activeClient')}
                </label>
              </div>
              <div className="md:col-span-2 flex flex-wrap items-center gap-3">
                <button
                  type="submit"
                  disabled={!clientPermissions.canEdit || clientFormSaving}
                  className="inline-flex items-center justify-center rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700 disabled:opacity-60"
                >
                  {clientFormSaving ? t('saving') ?? 'Сохранение...' : t('update') ?? 'Сохранить'}
                </button>
                <button
                  type="button"
                  onClick={handleDeleteClientCard}
                  disabled={!clientPermissions.canDelete || clientFormSaving}
                  className="inline-flex items-center justify-center rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-red-700 disabled:opacity-60"
                >
                  {t('delete') ?? 'Удалить'}
                </button>
              </div>
            </form>
          </div>
        ) : activeSection === 'payments' ? (
          <>
            <TwoTypeStats
              clientId={clientId}
              caseId={caseIdForQuery}
              from={fromDateStr}
              to={toDateStr}
              statusFilter={toStatsStatusFilter(statusFilter)}
              reloadToken={statsReloadToken}
              storageKey={`pp.client-detail.stats_panels.${user?.id ?? 'guest'}`}
              className="mb-6"
            />

            <div className="bg-white rounded-xl shadow-sm border border-gray-100 mb-6">
              <div className="p-6 flex flex-wrap items-center gap-3">
                <MonthRangePicker
                  value={{ from: monthFrom || undefined, to: monthTo || undefined }}
                  onChange={(r) => {
                    setMonthFrom(r.from ?? '');
                    setMonthTo(r.to ?? '');
                  }}
                  yearsBack={8}
                  yearsForward={1}
                />

                <div className="relative w-full sm:w-56">
                  <select
                    value={statusFilter}
                    onChange={(e) => setStatusFilter(e.target.value as 'all' | NormalizedStatus)}
                    className="w-full bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500">
                    <option value="all">Все статусы</option>
                    <option value="pending">Ожидается</option>
                    <option value="completed">Выполнено</option>
                    <option value="overdue">Просрочено</option>
                  </select>
                </div>

              </div>

              <div className="px-6 pb-6 pt-0">
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={() => setSelectedCaseId('all')}
                    className={`px-3 py-1.5 rounded-full text-sm border transition-colors ${
                      selectedCaseId === 'all'
                        ? 'bg-slate-800 text-white border-slate-800 shadow-sm'
                        : 'bg-slate-50 text-slate-700 border-slate-200 hover:bg-slate-100'
                    }`}>
                    Все дела
                  </button>

                  {cases.map((k) => {
                    const active = selectedCaseId === k.id;
                    const s = (k.status ?? '').toLowerCase();
                    const statusClasses = s.includes('open')
                      ? 'bg-green-50 text-green-700 border-green-200'
                      : s.includes('hold')
                      ? 'bg-amber-50 text-amber-800 border-amber-200'
                      : s.includes('closed')
                      ? 'bg-red-50 text-red-700 border-red-200'
                      : 'bg-slate-50 text-slate-700 border-slate-200';
                    return (
                      <button
                        key={k.id}
                        type="button"
                        onClick={() => setSelectedCaseId(k.id)}
                        title={caseStatusLabel(k.status)}
                        className={`px-3 py-1.5 rounded-full text-sm border transition-colors ${
                          active
                            ? 'bg-slate-800 text-white border-slate-800 shadow-sm'
                            : `${statusClasses} hover:opacity-90`
                        }`}>
                        {k.title}
                      </button>
                    );
                  })}
                </div>
              </div>
            </div>
          </>
        ) : activeSection === 'accounts' ? (
          <>
            <div className="grid gap-4 mb-6 md:grid-cols-4">
              {[
                {
                  key: 'pending',
                  title: 'Не оплачено всего',
                  value: invoiceSummaryBuckets.pending.amount,
                  count: invoiceSummaryBuckets.pending.count,
                  accent: 'border border-amber-100 bg-amber-50 text-amber-700',
                  icon: WalletCards,
                },
                {
                  key: 'overdue',
                  title: 'Не оплачено в срок',
                  value: invoiceSummaryBuckets.overdue.amount,
                  count: invoiceSummaryBuckets.overdue.count,
                  accent: 'border border-rose-100 bg-rose-50 text-rose-700',
                  icon: AlertTriangle,
                },
                {
                  key: 'paid',
                  title: 'Оплачено',
                  value: invoiceSummaryBuckets.paid.amount,
                  count: invoiceSummaryBuckets.paid.count,
                  accent: 'border border-emerald-100 bg-emerald-50 text-emerald-700',
                  icon: CheckCircle2,
                },
              ].map((card) => {
                const Icon = card.icon;
                return (
                  <div key={card.key} className={`rounded-2xl p-5 shadow-sm ${card.accent}`}>
                    <div className="flex items-center justify-between">
                      <div>
                        <p className="text-sm font-medium">{card.title}</p>
                        <p className="mt-2 text-2xl font-semibold">
                          {formatCurrencySmart(card.value).full}
                        </p>
                      </div>
                      <Icon className="h-10 w-10 opacity-80" />
                    </div>
                    <p className="mt-4 text-xs">
                      {(t('invoiceSummaryCount') ?? 'Количество счетов') + ': '} {card.count}
                    </p>
                  </div>
                );
              })}
              <div className="rounded-2xl border border-rose-100 bg-rose-50 p-5 shadow-sm text-rose-700">
                <p className="text-sm font-medium">Просрочено на:</p>
                <div className="mt-3 space-y-1 text-sm">
                  <div className="flex items-center justify-between gap-2">
                    <span>0-10 дней</span>
                    <span>{formatCurrencySmart(invoiceSummaryBuckets.overdueAging?.day0To10 ?? 0).full}</span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span>11-30 дней</span>
                    <span>{formatCurrencySmart(invoiceSummaryBuckets.overdueAging?.day11To30 ?? 0).full}</span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span>31-60 дней</span>
                    <span>{formatCurrencySmart(invoiceSummaryBuckets.overdueAging?.day31To60 ?? 0).full}</span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span>61-90 дней</span>
                    <span>{formatCurrencySmart(invoiceSummaryBuckets.overdueAging?.day61To90 ?? 0).full}</span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span>Больше 90 дней</span>
                    <span>{formatCurrencySmart(invoiceSummaryBuckets.overdueAging?.day91Plus ?? 0).full}</span>
                  </div>
                </div>
                <div className="mt-3 border-t border-rose-200 pt-2 flex items-center justify-between font-semibold">
                  <span>Просрочено итого:</span>
                  <span>{formatCurrencySmart(invoiceSummaryBuckets.overdueAging?.total ?? 0).full}</span>
                </div>
              </div>
            </div>

            <div className="mb-6 flex justify-end">
              <button
                type="button"
                onClick={() => refreshInvoices()}
                className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
              >
                <RotateCcw className={invoicesRefreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
                {t('invoiceRefresh') ?? 'Обновить'}
              </button>
            </div>
          </>
        ) : null}

        {activeSection !== 'profile' && (
          <div className="bg-white rounded-xl shadow-sm border border-gray-100">
          <div className="p-6 border-b border-gray-100">
            <div className="flex items-center justify-between">
              <h2 className="text-xl font-semibold text-gray-900">
                {SECTION_META[activeSection].label}
              </h2>
              {activeSection === 'payments' || activeSection === 'accounts' || activeSection === 'acts' || activeSection === 'contracts' ? (
                <button
                  type="button"
                  onClick={() => {
                    if (activeSection === 'payments') {
                      setPaymentTableSettingsOpen((prev) => !prev);
                    } else if (activeSection === 'accounts') {
                      setInvoiceTableSettingsOpen((prev) => !prev);
                    } else if (activeSection === 'acts') {
                      setActTableSettingsOpen((prev) => !prev);
                    } else if (activeSection === 'contracts') {
                      setContractTableSettingsOpen((prev) => !prev);
                    }
                  }}
                  className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
                >
                  <Settings2 className="h-4 w-4" />
                  {(activeSection === 'payments' && paymentTableSettingsOpen) ||
                  (activeSection === 'accounts' && invoiceTableSettingsOpen) ||
                  (activeSection === 'acts' && actTableSettingsOpen) ||
                  (activeSection === 'contracts' && contractTableSettingsOpen)
                    ? 'Скрыть настройки'
                    : 'Настроить таблицу'}
                </button>
              ) : null}
            </div>
          </div>

          <div className="p-6">
            {activeSection === 'payments' ? (
              <>
                {paymentTableSettingsOpen ? (
                  <div className="mb-6">
                    <TableColumnSettings
                      columns={PAYMENT_TABLE_COLUMNS.map((column) => ({
                        key: column.key,
                        label: column.label,
                      }))}
                      columnOrder={paymentColumnOrder}
                      visibleColumns={paymentVisibleColumns}
                      draggingColumn={draggingPaymentColumn}
                      onToggleColumn={togglePaymentColumn}
                      onMoveColumn={movePaymentColumn}
                      onDragStart={handlePaymentColumnDragStart}
                      onDragOver={handlePaymentColumnDragOver}
                      onDrop={handlePaymentColumnDrop}
                      onDragEnd={handlePaymentColumnDragEnd}
                      onReset={resetPaymentTableSettings}
                      footer={
                        <div className="flex items-center gap-2 text-sm text-slate-600">
                          <span>Строк на странице</span>
                          <select
                            value={paymentPageSize}
                            onChange={(event) => setPaymentPageSize(Number(event.target.value) || paymentPageSize)}
                            className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                          >
                            {DETAIL_PAGE_SIZE_OPTIONS.map((size) => (
                              <option key={size} value={size}>
                                {size}
                              </option>
                            ))}
                          </select>
                        </div>
                      }
                    />
                  </div>
                ) : null}
              {loadingPayments ? (
                <div className="text-center py-8 text-gray-500">Загрузка...</div>
              ) : paymentsError ? (
                <div className="text-center py-8 text-red-600">{paymentsError}</div>
              ) : visiblePayments.length === 0 ? (
                <div className="text-center py-8">
                  <CalendarIcon size={48} className="mx-auto mb-4 text-gray-300" />
                  <h3 className="text-lg font-medium text-gray-900 mb-2">Платежи не найдены</h3>
                  <p className="text-gray-500">Нет платежей по выбранному фильтру</p>
                </div>
              ) : (
                <>
                  <div className="overflow-x-auto">
                    <table className="min-w-full table-fixed divide-y divide-gray-200 text-sm">
                    <colgroup>
                      {visiblePaymentTableColumns.map((column) => (
                        <col key={column.key} style={{ width: getPaymentColumnWidth(column.key) }} />
                      ))}
                      {clientPermissions.canEdit || clientPermissions.canDelete ? <col className="w-36" /> : null}
                    </colgroup>
                    <thead className="bg-slate-50">
                      <tr className="text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                        {visiblePaymentTableColumns.map((column) => (
                          <th
                            key={column.key}
                            scope="col"
                            style={{ width: getPaymentColumnWidth(column.key) }}
                            className={`relative px-4 py-3 ${
                              draggingPaymentColumn === column.key ? 'bg-blue-50 text-blue-700' : ''
                            }`}
                            onDragOver={handlePaymentColumnDragOver}
                            onDrop={(event) => handlePaymentColumnDrop(column.key, event)}
                          >
                            <div
                              className="inline-flex min-w-0 select-none items-center gap-2"
                              draggable
                              onDragStart={(event) => handlePaymentColumnDragStart(column.key, event)}
                              onDragEnd={handlePaymentColumnDragEnd}
                              title="Перетащите, чтобы изменить порядок"
                            >
                              <GripVertical className="h-4 w-4 text-slate-400" />
                              <span className="truncate">{column.label}</span>
                            </div>
                            <div
                              role="separator"
                              aria-label={`Изменить ширину колонки ${column.label}`}
                              className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                              onMouseDown={(event) => handlePaymentResizeStart(column.key, event)}
                              onDragStart={(event) => event.preventDefault()}
                            />
                          </th>
                        ))}
                        {(clientPermissions.canEdit || clientPermissions.canDelete) && (
                          <th scope="col" className="px-4 py-3 text-right">Действия</th>
                        )}
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-100 text-slate-700">
                      {visiblePayments.map((payment) => {
                        const status = normalizeStatus(payment.status);
                        const statusLabel = toRuPaymentStatus(
                          payment.paymentStatusEntity?.name ?? payment.status,
                          payment.paymentStatusEntity?.name || payment.status || '—',
                        );
                        const statusBadgeClass =
                          status === 'completed'
                            ? 'bg-emerald-50 text-emerald-700 border border-emerald-100'
                            : status === 'overdue'
                              ? 'bg-purple-50 text-purple-700 border border-purple-100'
                              : 'bg-amber-50 text-amber-700 border border-amber-100';
                        const StatusIcon =
                          status === 'completed'
                            ? CheckCircle
                            : status === 'overdue'
                              ? AlertTriangle
                              : Clock;
                        const directionLabel =
                          payment.dealType?.name ||
                          payment.incomeType?.name ||
                          payment.paymentSource?.name ||
                          '—';
                        const secondaryDirections = [
                          payment.dealType?.name,
                          payment.incomeType?.name,
                          payment.paymentSource?.name,
                        ]
                          .filter((item): item is string => Boolean(item && item !== directionLabel))
                          .filter((value, index, self) => self.indexOf(value) === index);
                        const DirectionIcon = payment.type === 'Income' ? TrendingUp : TrendingDown;
                        const hasProgress = payment.hasPartialPayment && payment.paidAmount > 0;
                        const paidProgress = payment.amount > 0 ? Math.min(payment.paidAmount / payment.amount, 1) : 0;

                        return (
                          <tr key={payment.id} className="align-top transition-colors hover:bg-slate-50/70">
                            {visiblePaymentTableColumns.map((column) => (
                              <td
                                key={column.key}
                                style={{ width: getPaymentColumnWidth(column.key) }}
                                className="max-w-0 overflow-hidden px-4 py-4 align-top"
                              >
                                {column.key === 'period' ? (
                                  <div className="text-slate-600">
                                    <div className="font-medium text-slate-900">{toRuDate(payment.date)}</div>
                                    {payment.plannedDate && payment.plannedDate !== payment.date ? (
                                      <div className="mt-1 text-xs text-slate-500">
                                        План: {toRuDate(payment.plannedDate)}
                                      </div>
                                    ) : null}
                                    {payment.paidDate ? (
                                      <div className="mt-1 text-xs text-emerald-600">
                                        Оплачен: {toRuDate(payment.paidDate)}
                                      </div>
                                    ) : null}
                                    <div className="mt-2 text-xs text-slate-400">
                                      Создан: {toRuDate(payment.createdAt)}
                                    </div>
                                    {payment.rescheduleCount > 0 ? (
                                      <div className="mt-2 inline-flex items-center rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-[11px] font-medium text-amber-700">
                                        <RotateCcw size={12} className="mr-1" /> Переносов: {payment.rescheduleCount}
                                      </div>
                                    ) : null}
                                  </div>
                                ) : column.key === 'amountType' ? (
                                  <>
                                    <div
                                      className={`flex items-center gap-2 text-sm font-semibold ${
                                        payment.type === 'Income' ? 'text-emerald-600' : 'text-rose-600'
                                      }`}
                                      title={formatCurrencySmart(payment.amount, { alwaysCents: true }).full}
                                    >
                                      <DirectionIcon size={18} />
                                      {formatCurrencySmart(payment.amount).full}
                                    </div>
                                    <div className="mt-2 inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] font-medium text-slate-600">
                                      {payment.type === 'Income' ? 'Поступление' : 'Списание'}
                                    </div>
                                    {payment.outstandingAmount > 0 ? (
                                      <div className="mt-2 text-xs text-rose-600">
                                        Осталось оплатить: {formatCurrencySmart(payment.outstandingAmount).full}
                                      </div>
                                    ) : null}
                                    {payment.hasPartialPayment && payment.paidAmount > 0 ? (
                                      <div className="mt-2">
                                        <div className="flex items-center justify-between text-xs text-slate-500">
                                          <span>Оплачено</span>
                                          <span className="font-medium text-slate-700">
                                            {formatCurrencySmart(payment.paidAmount).full}
                                          </span>
                                        </div>
                                        <div className="mt-1 h-1.5 w-full rounded-full bg-slate-200">
                                          <div
                                            className={`h-full rounded-full ${
                                              payment.type === 'Income' ? 'bg-emerald-500' : 'bg-rose-500'
                                            }`}
                                            style={{ width: `${Math.max(0, Math.min(100, paidProgress * 100))}%` }}
                                          />
                                        </div>
                                      </div>
                                    ) : null}
                                  </>
                                ) : column.key === 'category' ? (
                                  <>
                                    <div className="font-medium text-slate-900">{directionLabel}</div>
                                    {secondaryDirections.length ? (
                                      <ul className="mt-1 space-y-0.5 text-xs text-slate-500">
                                        {secondaryDirections.map((label) => (
                                          <li key={label}>{label}</li>
                                        ))}
                                      </ul>
                                    ) : null}
                                    {payment.account ? (
                                      <div className="mt-2 text-xs text-slate-500">Счёт: {payment.account}</div>
                                    ) : null}
                                    {payment.accountDate ? (
                                      <div className="text-xs text-slate-400">
                                        Дата счёта: {toRuDate(payment.accountDate)}
                                      </div>
                                    ) : null}
                                  </>
                                ) : column.key === 'caseContact' ? (
                                  <>
                                    {payment.clientCase?.title ? (
                                      <div className="font-medium text-slate-900">{payment.clientCase.title}</div>
                                    ) : (
                                      <div className="text-xs text-slate-400">Дело не указано</div>
                                    )}
                                    {payment.client?.name ? (
                                      <div className="mt-1 text-xs text-slate-500">{payment.client.name}</div>
                                    ) : null}
                                    {payment.clientCase?.status ? (
                                      <div className="mt-1 inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] text-slate-600">
                                        {caseStatusLabel(payment.clientCase.status)}
                                      </div>
                                    ) : null}
                                  </>
                                ) : column.key === 'details' ? (
                                  <>
                                    {payment.description ? (
                                      <div className="text-slate-800">{payment.description}</div>
                                    ) : (
                                      <div className="text-xs text-slate-400">Без описания</div>
                                    )}
                                    {payment.notes ? (
                                      <div className="mt-2 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 text-xs text-slate-600" title={payment.notes}>
                                        <span className="font-medium text-slate-700">Заметка:</span> {payment.notes}
                                      </div>
                                    ) : null}
                                    {payment.systemNotes ? (
                                      <div className="mt-2 rounded-lg border border-slate-100 bg-white px-3 py-2 text-xs text-slate-500" title={payment.systemNotes}>
                                        <span className="font-medium text-slate-600">Системно:</span> {payment.systemNotes}
                                      </div>
                                    ) : null}
                                  </>
                                ) : (
                                  <>
                                    <div
                                      className={`inline-flex items-center gap-2 rounded-full px-3 py-1 text-xs font-medium ${statusBadgeClass}`}
                                    >
                                      <StatusIcon size={14} />
                                      <span className="leading-none">{statusLabel}</span>
                                    </div>
                                    {payment.delayDays ? (
                                      <div className="mt-2 inline-flex items-center rounded-full border border-purple-200 bg-purple-50 px-2 py-0.5 text-[11px] font-medium text-purple-700">
                                        <AlertTriangle size={12} className="mr-1" /> Просрочка: {payment.delayDays} дн.
                                      </div>
                                    ) : null}
                                    {payment.lastPaymentDate ? (
                                      <div className="mt-2 text-xs text-slate-500">
                                        Последний платёж: {toRuDate(payment.lastPaymentDate)}
                                      </div>
                                    ) : null}
                                    {payment.hasPartialPayment && !hasProgress ? (
                                      <div className="mt-2 text-xs text-slate-500">Оплачено частично</div>
                                    ) : null}
                                  </>
                                )}
                              </td>
                            ))}
                            {(clientPermissions.canEdit || clientPermissions.canDelete) && (
                              <td className="px-4 py-4">
                                <div className="flex items-center justify-end gap-2">
                                  {clientPermissions.canEdit ? (
                                    <button
                                      type="button"
                                      className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-200 text-blue-600 transition-colors hover:border-blue-300 hover:bg-blue-50"
                                      onClick={() => openEditPayment(payment)}
                                      title="Редактировать"
                                      aria-label="Редактировать"
                                    >
                                      <Edit size={16} />
                                    </button>
                                  ) : null}
                                  {clientPermissions.canDelete ? (
                                    <button
                                      type="button"
                                      className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-200 text-rose-600 transition-colors hover:border-rose-300 hover:bg-rose-50"
                                      onClick={() => removePayment(payment.id)}
                                      title="Удалить"
                                      aria-label="Удалить"
                                    >
                                      <Trash2 size={16} />
                                    </button>
                                  ) : null}
                                </div>
                              </td>
                            )}
                          </tr>
                        );
                      })}
                    </tbody>
                    </table>
                  </div>
                  <TablePagination
                    page={paymentPage}
                    totalPages={paymentTotalPages}
                    totalItems={paymentsTotal}
                    pageSize={paymentPageSize}
                    pageSizeOptions={DETAIL_PAGE_SIZE_OPTIONS}
                    onPageChange={setPaymentPage}
                    onPageSizeChange={setPaymentPageSize}
                  />
                </>
              )}
              </>
            ) : activeSection === 'accounts' ? (
              <>
                {invoiceTableSettingsOpen ? (
                  <div className="mb-6">
                    <TableColumnSettings
                      columns={INVOICE_TABLE_COLUMNS.map((column) => ({
                        key: column.key,
                        label: column.label,
                      }))}
                      columnOrder={invoiceColumnOrder}
                      visibleColumns={invoiceVisibleColumns}
                      draggingColumn={draggingInvoiceColumn}
                      onToggleColumn={toggleInvoiceColumn}
                      onMoveColumn={moveInvoiceColumn}
                      onDragStart={handleInvoiceColumnDragStart}
                      onDragOver={handleInvoiceColumnDragOver}
                      onDrop={handleInvoiceColumnDrop}
                      onDragEnd={handleInvoiceColumnDragEnd}
                      onReset={resetInvoiceTableSettings}
                      footer={
                        <div className="flex items-center gap-2 text-sm text-slate-600">
                          <span>Строк на странице</span>
                          <select
                            value={invoicePageSize}
                            onChange={(event) => setInvoicePageSize(Number(event.target.value) || invoicePageSize)}
                            className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                          >
                            {DETAIL_PAGE_SIZE_OPTIONS.map((size) => (
                              <option key={size} value={size}>
                                {size}
                              </option>
                            ))}
                          </select>
                        </div>
                      }
                    />
                  </div>
                ) : null}
                {invoicesLoading ? (
                  <div className="py-10 text-center text-gray-500">
                    <div className="inline-flex items-center gap-2">
                      <Loader2 className="h-4 w-4 animate-spin" />
                      {t('loading') ?? 'Загрузка...'}
                    </div>
                  </div>
                ) : clientInvoices.length === 0 ? (
                  <div className="py-10 text-center text-gray-500">
                    {invoicesError ?? t('invoicesEmpty') ?? 'Счета не найдены'}
                  </div>
                ) : (
                    <div className="overflow-x-auto">
                      <table className="min-w-full table-fixed divide-y divide-gray-200">
                        <colgroup>
                          {visibleInvoiceTableColumns.map((column) => (
                            <col key={column.key} style={{ width: getInvoiceColumnWidth(column.key) }} />
                          ))}
                          <col style={{ width: 110 }} />
                        </colgroup>
                        <thead className="bg-gray-50">
                          <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                            {visibleInvoiceTableColumns.map((column) => {
                              const sortKey = column.key as InvoicesSortKey;
                              const isActive = invoiceSort.key === sortKey;
                              const direction = isActive ? invoiceSort.direction : undefined;
                              return (
                                <th
                                  key={column.key}
                                  scope="col"
                                  style={{ width: getInvoiceColumnWidth(column.key) }}
                                  className={`relative px-4 py-3 ${draggingInvoiceColumn === column.key ? 'bg-blue-50 text-blue-700' : ''}`}
                                  onDragOver={handleInvoiceColumnDragOver}
                                  onDrop={(event) => handleInvoiceColumnDrop(column.key, event)}
                                >
                                  <button
                                    type="button"
                                    onClick={() => handleInvoiceSort(sortKey)}
                                    className="flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-gray-500"
                                  >
                                    <span
                                      className="inline-flex min-w-0 select-none items-center gap-2"
                                      draggable
                                      onDragStart={(event) => handleInvoiceColumnDragStart(column.key, event)}
                                      onDragEnd={handleInvoiceColumnDragEnd}
                                      title="Перетащите, чтобы изменить порядок"
                                    >
                                      <GripVertical className="h-4 w-4 text-slate-400" />
                                      <span className="truncate">{column.label}</span>
                                    </span>
                                    <span className="text-gray-400">{isActive ? (direction === 'asc' ? '▲' : '▼') : ''}</span>
                                  </button>
                                  <div
                                    role="separator"
                                    aria-label={`Изменить ширину колонки ${column.label}`}
                                    className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                                    onMouseDown={(event) => handleInvoiceResizeStart(column.key, event)}
                                    onDragStart={(event) => event.preventDefault()}
                                  />
                                </th>
                              );
                            })}
                            <th scope="col" className="px-4 py-3 text-right">
                              {t('actions') ?? 'Действия'}
                            </th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                          {clientInvoices.map((invoice) => (
                            <tr key={invoice.id} className="hover:bg-gray-50/80">
                              {visibleInvoiceTableColumns.map((column) => (
                                <td
                                  key={column.key}
                                  style={{ width: getInvoiceColumnWidth(column.key) }}
                                  className="max-w-0 overflow-hidden px-4 py-3 align-top"
                                >
                                  {column.key === 'date' ? (
                                    <span className="font-medium text-gray-900">{toRuDate(invoice.date)}</span>
                                  ) : column.key === 'number' ? (
                                    (() => {
                                      const caseLabels = invoice.clientCases?.length
                                        ? invoice.clientCases.map((item) => item.title?.trim() || `Дело #${item.id}`)
                                        : invoice.clientCaseTitle
                                          ? [invoice.clientCaseTitle]
                                          : invoice.clientCaseId
                                            ? [`Дело #${invoice.clientCaseId}`]
                                            : [];
                                      const actLabels = invoice.acts?.length
                                        ? invoice.acts.map((item) => {
                                            const parts: string[] = [];
                                            if (item.number) parts.push(`№${item.number}`);
                                            if (item.title) parts.push(item.title);
                                            return parts.join(' · ') || `Акт #${item.id}`;
                                          })
                                        : invoice.actNumber
                                          ? [`${t('invoiceAct') ?? 'Акт'} №${invoice.actNumber}${invoice.actTitle ? ` · ${invoice.actTitle}` : ''}`]
                                          : [];

                                      return (
                                        <>
                                          <div className="font-medium text-gray-900">№{invoice.number}</div>
                                          {caseLabels.map((label) => (
                                            <div key={label} className="text-xs text-gray-500">{label}</div>
                                          ))}
                                          {actLabels.map((label) => (
                                            <div key={label} className="text-xs text-gray-500">{label}</div>
                                          ))}
                                        </>
                                      );
                                    })()
                                  ) : column.key === 'amount' ? (
                                    <span className="font-semibold text-gray-900">{formatCurrencySmart(invoice.amount).full}</span>
                                  ) : column.key === 'status' ? (
                                    <span
                                      className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${invoiceStatusClasses[invoice.status]}`}
                                    >
                                      {invoiceStatusLabels[invoice.status]}
                                    </span>
                                  ) : column.key === 'dueDate' ? (
                                    <span className="text-gray-900">
                                      {invoice.dueDate ? toRuDate(invoice.dueDate) : '—'}
                                      {invoice.paidDate ? (
                                        <div className="text-xs text-emerald-600">
                                          {(t('invoicePaidOn') ?? 'Оплачен') + ': '} {toRuDate(invoice.paidDate)}
                                        </div>
                                      ) : null}
                                    </span>
                                  ) : column.key === 'responsible' ? (
                                    <span className="text-gray-900">{invoice.responsibleName ?? '—'}</span>
                                  ) : (
                                    <span className="text-gray-900">{toRuDate(invoice.createdAt)}</span>
                                  )}
                                </td>
                              ))}
                              <td className="px-4 py-3">
                                <div className="flex items-center justify-end gap-2">
                                  {accountPermissions.canEdit && (
                                    <button
                                      type="button"
                                      onClick={() => openInvoiceEdit(invoice)}
                                      className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                      title={t('edit') ?? 'Редактировать'}
                                      aria-label={t('edit') ?? 'Редактировать'}
                                    >
                                      <Edit className="h-4 w-4" />
                                    </button>
                                  )}
                                  {accountPermissions.canDelete && (
                                    <button
                                      type="button"
                                      onClick={() => removeInvoice(invoice)}
                                      className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                      title={t('delete') ?? 'Удалить'}
                                      aria-label={t('delete') ?? 'Удалить'}
                                    >
                                      <Trash2 className="h-4 w-4" />
                                    </button>
                                  )}
                                </div>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                )}
                {!invoicesLoading && (invoicesPagination.total ?? clientInvoices.length) > 0 ? (
                  <>
                  <TablePagination
                    page={invoicesPagination.page || invoicePage}
                    totalPages={invoiceTotalPages}
                    totalItems={invoicesPagination.total ?? clientInvoices.length}
                    pageSize={invoicesPagination.pageSize || invoicePageSize}
                    pageSizeOptions={DETAIL_PAGE_SIZE_OPTIONS}
                    onPageChange={setInvoicePage}
                    onPageSizeChange={setInvoicePageSize}
                  />
                  {/*
                  <TablePagination
                    <div>
                      {(t('recordsFound') ?? 'Найдено записей')}: {invoicesPagination.total}
                    </div>
                    <div className="flex items-center gap-2">
                      <button
                        type="button"
                        onClick={() => setInvoicePage((value) => Math.max(1, value - 1))}
                        disabled={invoicePage <= 1}
                        className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {t('previous') ?? 'Назад'}
                      </button>
                      <span>
                        {invoicesPagination.page} / {invoiceTotalPages}
                      </span>
                      <button
                        type="button"
                        onClick={() => setInvoicePage((value) => Math.min(invoiceTotalPages, value + 1))}
                        disabled={invoicePage >= invoiceTotalPages}
                        className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {t('next') ?? 'Вперёд'}
                      </button>
                    </div>
                  */}
                  </>
                ) : null}
              </>
            ) : activeSection === 'acts' ? (
              <>
                <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-gray-900">
                      {t('actSectionTitle') ?? 'Сводка и список'}
                    </h3>
                    <p className="text-sm text-gray-500">
                      {t('actSectionDescription') ?? 'Отслеживайте статусы и суммы по документам клиента.'}
                    </p>
                  </div>
                  <div className="flex flex-wrap items-center gap-2">
                    <button
                      type="button"
                      onClick={() => {
                        void refreshActs();
                      }}
                      className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50">
                      <RotateCcw className={actsRefreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
                      {t('invoiceRefresh') ?? t('actFiltersApply') ?? 'Обновить'}
                    </button>
                  </div>
                </div>

                <div className="mb-6 grid gap-4 md:grid-cols-3">
                  {actSummaryCards.map((card) => {
                    const Icon = card.icon;
                    return (
                      <div
                        key={card.key}
                        className={`rounded-2xl border bg-white p-5 shadow-sm ${card.accent}`}>
                        <div className="flex items-center justify-between">
                          <div>
                            <p className="text-sm font-medium text-gray-700">{card.title}</p>
                            <p className="mt-2 text-2xl font-semibold text-gray-900">
                              {formatCurrencySmart(card.bucket.amount).full}
                            </p>
                          </div>
                          <Icon className="h-10 w-10 opacity-80" />
                        </div>
                        <p className="mt-4 text-xs text-gray-500">
                          {(t('actsSummaryCount') ?? 'Количество')}: {card.bucket.count}
                        </p>
                      </div>
                    );
                  })}
                </div>

                <div className="mb-4 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-gray-50 p-4">
                  <div className="flex items-center gap-2">
                    <CalendarRange className="h-4 w-4 text-gray-500" />
                    <input
                      type="date"
                      value={actFrom}
                      onChange={(event) => setActFrom(event.target.value)}
                      className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                    <span className="text-gray-500">—</span>
                    <input
                      type="date"
                      value={actTo}
                      onChange={(event) => setActTo(event.target.value)}
                      className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                  </div>
                  <div>
                    <select
                      value={actStatusFilter}
                      onChange={(event) => setActStatusFilter(event.target.value as 'all' | ActStatus)}
                      className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    >
                      <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
                      {ACT_STATUS_ORDER.map((status) => (
                        <option key={status} value={status}>
                          {actStatusLabels[status]}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="relative flex-1 min-w-[220px]">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                    <input
                      type="search"
                      value={actSearch}
                      onChange={(event) => setActSearch(event.target.value)}
                      placeholder={t('actSearchPlaceholder') ?? 'Номер акта, название или ИНН контрагента'}
                      className="w-full rounded-lg border border-gray-300 pl-9 pr-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                  </div>
                  <button
                    type="button"
                    onClick={resetActFilters}
                    className="ml-auto inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                  >
                    {t('actFiltersReset') ?? 'Сбросить'}
                  </button>
                </div>

                {actTableSettingsOpen ? (
                  <div className="mb-6">
                    <TableColumnSettings
                      columns={ACT_TABLE_COLUMNS.map((column) => ({
                        key: column.key,
                        label: column.label,
                      }))}
                      columnOrder={actColumnOrder}
                      visibleColumns={actVisibleColumns}
                      draggingColumn={draggingActColumn}
                      onToggleColumn={toggleActColumn}
                      onMoveColumn={moveActColumn}
                      onDragStart={handleActColumnDragStart}
                      onDragOver={handleActColumnDragOver}
                      onDrop={handleActColumnDrop}
                      onDragEnd={handleActColumnDragEnd}
                      onReset={resetActTableSettings}
                      footer={
                        <div className="flex items-center gap-2 text-sm text-slate-600">
                          <span>Строк на странице</span>
                          <select
                            value={actPageSize}
                            onChange={(event) => setActPageSize(Number(event.target.value) || actPageSize)}
                            className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                          >
                            {DETAIL_PAGE_SIZE_OPTIONS.map((size) => (
                              <option key={size} value={size}>
                                {size}
                              </option>
                            ))}
                          </select>
                        </div>
                      }
                    />
                  </div>
                ) : null}

                  <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
                    <div className="overflow-x-auto">
                      <table className="min-w-full table-fixed divide-y divide-gray-200">
                        <colgroup>
                          {visibleActTableColumns.map((column) => (
                            <col key={column.key} style={{ width: getActColumnWidth(column.key) }} />
                          ))}
                          <col style={{ width: 110 }} />
                        </colgroup>
                        <thead className="bg-gray-50">
                          <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                            {visibleActTableColumns.map((column) => {
                              const sortKey = column.key === 'title' ? null : (column.key as ActsSortKey);
                              const isActive = sortKey ? actSort.key === sortKey : false;
                              const direction = isActive ? actSort.direction : undefined;
                              return (
                                <th
                                  key={column.key}
                                  scope="col"
                                  style={{ width: getActColumnWidth(column.key) }}
                                  className={`relative px-4 py-3 ${draggingActColumn === column.key ? 'bg-blue-50 text-blue-700' : ''}`}
                                  onDragOver={handleActColumnDragOver}
                                  onDrop={(event) => handleActColumnDrop(column.key, event)}
                                >
                                  {sortKey ? (
                                    <button
                                      type="button"
                                      onClick={() => handleActSort(sortKey)}
                                      className="flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-gray-500"
                                    >
                                      <span
                                        className="inline-flex min-w-0 select-none items-center gap-2"
                                        draggable
                                        onDragStart={(event) => handleActColumnDragStart(column.key, event)}
                                        onDragEnd={handleActColumnDragEnd}
                                        title="Перетащите, чтобы изменить порядок"
                                      >
                                        <GripVertical className="h-4 w-4 text-slate-400" />
                                        <span className="truncate">{column.label}</span>
                                      </span>
                                      <span className="text-gray-400">{isActive ? (direction === 'asc' ? '▲' : '▼') : ''}</span>
                                    </button>
                                  ) : (
                                    <div
                                      className="inline-flex min-w-0 select-none items-center gap-2"
                                      draggable
                                      onDragStart={(event) => handleActColumnDragStart(column.key, event)}
                                      onDragEnd={handleActColumnDragEnd}
                                      title="Перетащите, чтобы изменить порядок"
                                    >
                                      <GripVertical className="h-4 w-4 text-slate-400" />
                                      <span className="truncate">{column.label}</span>
                                    </div>
                                  )}
                                  <div
                                    role="separator"
                                    aria-label={`Изменить ширину колонки ${column.label}`}
                                    className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                                    onMouseDown={(event) => handleActResizeStart(column.key, event)}
                                    onDragStart={(event) => event.preventDefault()}
                                  />
                                </th>
                              );
                            })}
                            <th scope="col" className="px-4 py-3 text-right">
                              {t('actions') ?? 'Действия'}
                            </th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                          {actsLoading ? (
                            <tr>
                              <td colSpan={visibleActTableColumns.length + 1} className="py-10 text-center text-gray-500">
                                <div className="inline-flex items-center gap-2">
                                  <Loader2 className="h-4 w-4 animate-spin" />
                                  {t('loading') ?? 'Загрузка...'}
                                </div>
                              </td>
                            </tr>
                          ) : clientActs.length === 0 ? (
                            <tr>
                              <td colSpan={visibleActTableColumns.length + 1} className="py-10 text-center text-gray-500">
                                {actsError ?? t('actsEmpty') ?? 'Документы не найдены'}
                              </td>
                            </tr>
                          ) : (
                            <>
                              {clientActs.map((act) => {
                                const title = act.title?.trim();
                                const displayTitle = title && title !== `№${act.number}` ? title : `Акт №${act.number}`;
                                return (
                                  <tr key={act.id} className="hover:bg-gray-50/80">
                                    {visibleActTableColumns.map((column) => (
                                      <td
                                        key={column.key}
                                        style={{ width: getActColumnWidth(column.key) }}
                                        className="max-w-0 overflow-hidden px-4 py-3 align-top"
                                      >
                                        {column.key === 'date' ? (
                                          <span className="font-medium text-gray-900">{toRuDate(act.date)}</span>
                                        ) : column.key === 'number' ? (
                                          <>
                                            <div className="font-medium text-gray-900">№{act.number}</div>
                                            {act.invoiceNumber && (
                                              <div className="text-xs text-gray-500">
                                                {(t('actInvoice') ?? 'Счёт')} {act.invoiceNumber}
                                              </div>
                                            )}
                                          </>
                                        ) : column.key === 'title' ? (
                                          <>
                                            <div className="font-medium text-gray-900">{displayTitle}</div>
                                            {act.comment && <div className="text-xs text-gray-500">{act.comment}</div>}
                                          </>
                                        ) : column.key === 'amount' ? (
                                          <span className="font-semibold text-gray-900">{formatCurrencySmart(act.amount).full}</span>
                                        ) : column.key === 'status' ? (
                                          <span
                                            className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${ACT_STATUS_CLASSES[act.status]}`}
                                          >
                                            {actStatusLabels[act.status]}
                                          </span>
                                        ) : column.key === 'responsible' ? (
                                          <span className="text-gray-900">
                                            {act.responsibleName ?? t('actResponsibleNotSelected') ?? '—'}
                                          </span>
                                        ) : (
                                          <span className="text-gray-900">{act.counterpartyInn ?? '—'}</span>
                                        )}
                                      </td>
                                    ))}
                                    <td className="px-4 py-3">
                                      <div className="flex items-center justify-end gap-2">
                                        {actPermissions.canEdit && (
                                          <button
                                            type="button"
                                            onClick={() => openEditAct(act)}
                                            className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                            title={t('edit') ?? 'Редактировать'}
                                            aria-label={t('edit') ?? 'Редактировать'}
                                          >
                                            <Edit className="h-4 w-4" />
                                          </button>
                                        )}
                                        {actPermissions.canDelete && (
                                          <button
                                            type="button"
                                            onClick={() => removeAct(act)}
                                            className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                            title={t('delete') ?? 'Удалить'}
                                            aria-label={t('delete') ?? 'Удалить'}
                                          >
                                            <Trash2 className="h-4 w-4" />
                                          </button>
                                        )}
                                      </div>
                                    </td>
                                  </tr>
                                );
                              })}
                              {actsError && (
                                <tr>
                                  <td colSpan={visibleActTableColumns.length + 1} className="bg-amber-50 px-4 py-3 text-sm text-amber-700">
                                    {actsError}
                                  </td>
                                </tr>
                              )}
                            </>
                          )}
                        </tbody>
                      </table>
                    </div>
                    {clientActs.length > 0 && !actsLoading ? (
                      <TablePagination
                        page={actsPagination.page || actPage}
                        totalPages={actTotalPages}
                        totalItems={actsPagination.total ?? clientActs.length}
                        pageSize={actPageSize}
                        pageSizeOptions={DETAIL_PAGE_SIZE_OPTIONS}
                        onPageChange={setActPage}
                        onPageSizeChange={setActPageSize}
                      />
                    ) : null}
                  </div>
              </>
            ) : activeSection === 'contracts' ? (
              <>
                <div className="mb-6 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
                  <div className="flex items-center gap-2">
                    <CalendarRange className="h-4 w-4 text-gray-500" />
                    <input
                      type="date"
                      value={contractFrom}
                      onChange={(event) => setContractFrom(event.target.value)}
                      className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                    />
                    <span className="text-gray-500">—</span>
                    <input
                      type="date"
                      value={contractTo}
                      onChange={(event) => setContractTo(event.target.value)}
                      className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                    />
                  </div>
                  <div className="relative flex-1 min-w-[220px]">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                    <input
                      type="search"
                      value={contractSearch}
                      onChange={(event) => setContractSearch(event.target.value)}
                      placeholder={
                        t('contractsSearchPlaceholder') ??
                        'Поиск по номеру, названию или описанию договора'
                      }
                      className="w-full rounded-lg border border-gray-300 pl-9 pr-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                    />
                  </div>
                  <div className="flex items-center gap-2 ml-auto">
                    <button
                      type="button"
                      onClick={() => refreshContracts()}
                      className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50"
                    >
                      <RotateCcw className={contractsRefreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
                      {t('invoiceRefresh') ?? 'Обновить'}
                    </button>
                    <button
                      type="button"
                      onClick={resetContractFilters}
                      className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                    >
                      {t('invoiceFiltersReset') ?? 'Сбросить'}
                    </button>
                  </div>
                </div>

                {contractActionError ? (
                  <div className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    {contractActionError}
                  </div>
                ) : null}

                {contractsError ? (
                  <div className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    {(t('contractsLoadError') ?? 'Не удалось загрузить договоры') + ': '}
                    {contractsError}
                  </div>
                ) : null}

                {contractTableSettingsOpen ? (
                  <div className="mb-6">
                    <TableColumnSettings
                      columns={CONTRACT_TABLE_COLUMNS.map((column) => ({
                        key: column.key,
                        label: column.label,
                      }))}
                      columnOrder={contractColumnOrder}
                      visibleColumns={contractVisibleColumns}
                      draggingColumn={draggingContractColumn}
                      onToggleColumn={toggleContractColumn}
                      onMoveColumn={moveContractColumn}
                      onDragStart={handleContractColumnDragStart}
                      onDragOver={handleContractColumnDragOver}
                      onDrop={handleContractColumnDrop}
                      onDragEnd={handleContractColumnDragEnd}
                      onReset={resetContractTableSettings}
                      footer={
                        <div className="flex items-center gap-2 text-sm text-slate-600">
                          <span>Строк на странице</span>
                          <select
                            value={contractPageSize}
                            onChange={(event) => setContractPageSize(Number(event.target.value) || contractPageSize)}
                            className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                          >
                            {DETAIL_PAGE_SIZE_OPTIONS.map((size) => (
                              <option key={size} value={size}>
                                {size}
                              </option>
                            ))}
                          </select>
                        </div>
                      }
                    />
                  </div>
                ) : null}

                {contractsLoading ? (
                  <div className="py-10 text-center text-gray-500">
                    <Loader2 className="mx-auto mb-2 h-5 w-5 animate-spin" />
                    {t('loading') ?? 'Загрузка...'}
                  </div>
                ) : clientContracts.length === 0 ? (
                  <div className="py-10 text-center text-gray-500">
                    <div className="text-lg font-semibold text-gray-900">
                      {t('contractsEmptyTitle') ?? 'Договоры отсутствуют'}
                    </div>
                    <div className="mt-2 text-sm text-gray-500">
                      {t('contractsEmptyDescription') ??
                        'Создайте первый договор, чтобы отслеживать обязательства и сроки.'}
                    </div>
                  </div>
                ) : (
                    <>
                      <div className="overflow-x-auto">
                        <table className="min-w-full table-fixed divide-y divide-gray-200 text-sm">
                          <colgroup>
                            {visibleContractTableColumns.map((column) => (
                              <col key={column.key} style={{ width: getContractColumnWidth(column.key) }} />
                            ))}
                            <col style={{ width: 110 }} />
                          </colgroup>
                          <thead className="bg-gray-50">
                            <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                              {visibleContractTableColumns.map((column) => {
                                const sortKey =
                                  column.key === 'clients' || column.key === 'validUntil'
                                    ? null
                                    : (column.key as ContractsSortKey);
                                const isActive = sortKey ? contractSort.key === sortKey : false;
                                const direction = isActive ? contractSort.direction : undefined;
                                return (
                                  <th
                                    key={column.key}
                                    scope="col"
                                    style={{ width: getContractColumnWidth(column.key) }}
                                    className={`relative px-4 py-3 ${draggingContractColumn === column.key ? 'bg-blue-50 text-blue-700' : ''}`}
                                    onDragOver={handleContractColumnDragOver}
                                    onDrop={(event) => handleContractColumnDrop(column.key, event)}
                                  >
                                    {sortKey ? (
                                      <button
                                        type="button"
                                        onClick={() => handleContractSort(sortKey)}
                                        className="flex items-center gap-1"
                                      >
                                        <span
                                          className="inline-flex min-w-0 select-none items-center gap-2"
                                          draggable
                                          onDragStart={(event) => handleContractColumnDragStart(column.key, event)}
                                          onDragEnd={handleContractColumnDragEnd}
                                          title="Перетащите, чтобы изменить порядок"
                                        >
                                          <GripVertical className="h-4 w-4 text-slate-400" />
                                          <span className="truncate">{column.label}</span>
                                        </span>
                                        <span className="text-gray-400">{isActive ? (direction === 'asc' ? '▲' : '▼') : ''}</span>
                                      </button>
                                    ) : (
                                      <div
                                        className="inline-flex min-w-0 select-none items-center gap-2"
                                        draggable
                                        onDragStart={(event) => handleContractColumnDragStart(column.key, event)}
                                        onDragEnd={handleContractColumnDragEnd}
                                        title="Перетащите, чтобы изменить порядок"
                                      >
                                        <GripVertical className="h-4 w-4 text-slate-400" />
                                        <span className="truncate">{column.label}</span>
                                      </div>
                                    )}
                                    <div
                                      role="separator"
                                      aria-label={`Изменить ширину колонки ${column.label}`}
                                      className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                                      onMouseDown={(event) => handleContractResizeStart(column.key, event)}
                                      onDragStart={(event) => event.preventDefault()}
                                    />
                                  </th>
                                );
                              })}
                              <th scope="col" className="px-4 py-3">
                                {t('actions') ?? 'Действия'}
                              </th>
                            </tr>
                          </thead>
                          <tbody className="divide-y divide-gray-200 text-gray-700">
                            {clientContracts.map((contract) => (
                              <tr key={contract.id} className="hover:bg-gray-50/80">
                                {visibleContractTableColumns.map((column) => (
                                  <td
                                    key={column.key}
                                    style={{ width: getContractColumnWidth(column.key) }}
                                    className="max-w-0 overflow-hidden px-4 py-3 align-top"
                                  >
                                    {column.key === 'number' ? (
                                      <span className="font-medium text-gray-900">№{contract.number}</span>
                                    ) : column.key === 'date' ? (
                                      <span className="text-gray-900">{toRuDate(contract.date)}</span>
                                    ) : column.key === 'clients' ? (
                                      contract.clients.length === 0 ? (
                                        <span className="text-xs text-gray-500">
                                          {t('contractClientsRequired') ?? 'Клиенты не указаны'}
                                        </span>
                                      ) : (
                                        <div className="flex flex-wrap gap-1">
                                          {contract.clients.map((client) => (
                                            <span
                                              key={client.id}
                                              className="inline-flex items-center rounded-full bg-gray-100 px-2 py-1 text-[11px] text-gray-700"
                                            >
                                              {client.name}
                                            </span>
                                          ))}
                                        </div>
                                      )
                                    ) : column.key === 'amount' ? (
                                      <span className="font-semibold text-gray-900">
                                        {contract.amount !== null && contract.amount !== undefined
                                          ? formatCurrencySmart(contract.amount).full
                                          : '—'}
                                      </span>
                                    ) : column.key === 'validUntil' ? (
                                      <span className="text-gray-900">{contract.validUntil ? toRuDate(contract.validUntil) : '—'}</span>
                                    ) : (
                                      <span className="text-gray-900">{toRuDate(contract.createdAt)}</span>
                                    )}
                                  </td>
                                ))}
                                <td className="px-4 py-3">
                                  <div className="flex items-center gap-2">
                                    {contractPermissions.canEdit && (
                                      <button
                                        type="button"
                                        onClick={() => openContractEdit(contract)}
                                        className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                        title={t('edit') ?? 'Редактировать'}
                                        aria-label={t('edit') ?? 'Редактировать'}
                                      >
                                        <Edit className="h-4 w-4" />
                                      </button>
                                    )}
                                    {contractPermissions.canDelete && (
                                      <button
                                        type="button"
                                        onClick={() => removeContract(contract)}
                                        className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                        title={t('delete') ?? 'Удалить'}
                                        aria-label={t('delete') ?? 'Удалить'}
                                      >
                                        <Trash2 className="h-4 w-4" />
                                      </button>
                                    )}
                                  </div>
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                      <TablePagination
                        page={contractsPagination.page || contractPage}
                        totalPages={contractTotalPages}
                        totalItems={contractsPagination.total ?? clientContracts.length}
                        pageSize={contractPageSize}
                        pageSizeOptions={DETAIL_PAGE_SIZE_OPTIONS}
                        onPageChange={setContractPage}
                        onPageSizeChange={setContractPageSize}
                      />
                    </>
                )}
              </>
            ) : (
              (() => {
                const meta = SECTION_META[activeSection];
                if (!meta.placeholderTitle) {
                  return null;
                }
                const PlaceholderIcon = meta.icon;
                return (
                  <div className="text-center py-12">
                    {PlaceholderIcon ? (
                      <PlaceholderIcon size={48} className="mx-auto mb-4 text-gray-300" />
                    ) : null}
                    <h3 className="text-lg font-medium text-gray-900 mb-2">{meta.placeholderTitle}</h3>
                    <p className="text-gray-500">{meta.placeholderDescription}</p>
                  </div>
                );
              })()
            )}
          </div>
          </div>
        )}

        <InvoiceModal
          open={invoiceModalOpen}
          mode={invoiceModalMode}
          invoice={selectedInvoice}
          onClose={closeInvoiceModal}
          onSubmit={submitInvoice}
          submitting={invoiceSubmitting}
          errorMessage={invoiceModalError}
          clients={invoiceModalClients}
          responsibles={actResponsibles}
          lookupsLoading={invoiceLookupsLoading}
          lookupsError={invoiceLookupsError}
          defaultClientId={clientId}
          defaultResponsibleId={clientData?.responsibleUserId ?? user?.id}
        />

        <ActModal
          open={actModalOpen}
          mode={actModalMode}
          act={selectedAct}
          onClose={closeActModal}
          onSubmit={submitAct}
          submitting={actSubmitting}
          errorMessage={actModalError}
          clients={actModalClients}
          responsibles={actResponsibles}
          lookupsLoading={actLookupsLoading}
          lookupsError={actLookupsError}
          defaultClientId={clientId}
        />

        <ContractModal
          open={contractModalOpen}
          mode={contractModalMode}
          contract={selectedContract}
          onClose={closeContractModal}
          onSubmit={submitContract}
          submitting={contractSubmitting}
          errorMessage={contractModalError}
          defaultClients={contractDefaultClients}
        />

        <PaymentModal
          key={payModalOpen ? (editingPayment ? `edit-${editingPayment.id}` : 'new') : 'closed'}
          isOpen={payModalOpen}
          onClose={closePayModal}
          payment={editingPayment ?? undefined}
          onSubmit={submitPayment}
          onCreateRemainder={async (payload) => {
            try {
              await apiService.createPayment(toBody(payload));
              await refreshPayments();
              bumpStats();
            } catch (error) {
              toast.error(extractDisplayErrorMessage(error, 'Не удалось создать остаток платежа'), 'Платежи');
              throw error;
            }
          }}
          onDelete={async (id: number) => {
            const removed = await removePayment(id);
            if (removed) {
              closePayModal();
            }
          }}
          type={editingPayment?.type ?? 'Income'}
          defaultClientId={clientId}
          defaultClientCaseId={selectedCaseId === 'all' ? undefined : Number(selectedCaseId)}
          casesPrefetch={cases}
        />

        {caseModalOpen && (
          <CaseModal
            isOpen={caseModalOpen}
            onClose={() => setCaseModalOpen(false)}
            caseData={
              caseMode === 'edit' && editingCase
                ? editingCase
                : ({
                    id: 0,
                    clientId,
                    title: '',
                    description: '',
                    status: 'Open',
                    createdAt: new Date().toISOString(),
                  } as ClientCase)
            }
            onSave={saveCase}
            onDelete={caseMode === 'edit' && clientPermissions.canDelete ? deleteCase : undefined}
          />
        )}

        <FormPromptModal
          open={legalEntityPromptOpen}
          title="Новая организация"
          description="Быстрое создание юрлица из карточки клиента."
          fields={[
            {
              key: 'shortName',
              label: 'Название организации',
              value: legalEntityDraft.shortName,
              onChange: (value) => {
                setLegalEntityDraft((prev) => ({ ...prev, shortName: value }));
                if (legalEntityPromptError) {
                  setLegalEntityPromptError(null);
                }
              },
              placeholder: 'ООО Ромашка',
              required: true,
              autoFocus: true,
            },
            {
              key: 'inn',
              label: 'ИНН',
              value: legalEntityDraft.inn,
              onChange: (value) => {
                setLegalEntityDraft((prev) => ({ ...prev, inn: value }));
                if (legalEntityPromptError) {
                  setLegalEntityPromptError(null);
                }
              },
              placeholder: 'Опционально',
            },
          ]}
          onCancel={closeLegalEntityPrompt}
          onConfirm={submitQuickLegalEntity}
          confirmLabel="Создать"
          cancelLabel="Отмена"
          error={legalEntityPromptError ?? undefined}
          loading={legalEntityPromptSubmitting}
        />

        <FormPromptModal
          open={poaPromptOpen}
          title="Новая доверенность"
          description="Добавление доверенности без перехода в отдельную форму."
          fields={[
            {
              key: 'number',
              label: 'Номер доверенности',
              value: poaDraft.number,
              onChange: (value) => {
                setPoaDraft((prev) => ({ ...prev, number: value }));
                if (poaPromptError) {
                  setPoaPromptError(null);
                }
              },
              placeholder: 'Например 12/24',
              required: true,
              autoFocus: true,
            },
            {
              key: 'issuedAt',
              label: 'Дата выдачи',
              value: poaDraft.issuedAt,
              onChange: (value) => {
                setPoaDraft((prev) => ({ ...prev, issuedAt: value }));
                if (poaPromptError) {
                  setPoaPromptError(null);
                }
              },
              type: 'date',
              required: true,
            },
          ]}
          onCancel={closePoaPrompt}
          onConfirm={submitQuickPowerOfAttorney}
          confirmLabel="Создать"
          cancelLabel="Отмена"
          error={poaPromptError ?? undefined}
          loading={poaPromptSubmitting}
        />

        {notFound && (
          <div className="min-h-[120px] flex items-center justify-center text-gray-500">
            <Users size={24} className="mr-2" /> Клиент не найден
          </div>
        )}
      </div>
    </div>
  );
}
