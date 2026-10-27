import { PaymentCreateAdoptedError } from '../../services/paymentCreateAttempts';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import {
  ArrowLeft,
  ChevronDown,
  Ban,
  UserRound,
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
  ClipboardList,
  CheckCircle2,
  Send,
  FileCheck2,
  FileSignature,
  Calculator,
  History,
  X,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type MouseEvent,
} from "react";
import { useClientCases } from "../../hooks/useClientCases";
import { useDebouncedValue } from "../../hooks/useDebouncedValue";
import { useActs, type ActsSortKey } from "../../hooks/useActs";
import { useInvoices, type InvoicesSortKey } from "../../hooks/useInvoices";
import { useContracts, type ContractsSortKey } from "../../hooks/useContracts";
import { usePaymentsPaged } from "../../hooks/usePaymentsPaged";
import { usePersistedTableSettings } from "../../hooks/usePersistedTableSettings";
import { apiService } from "../../services/api";
import { useTranslation } from "../../hooks/useTranslation";
import { useFormRequirements } from "../../hooks/useFormRequirements";
import { useAuth } from "../../contexts/AuthContext";
import { useRolePermissions } from "../../hooks/useRolePermissions";
import { useDialog } from "../../hooks/useDialog";
import { useToast } from "../../hooks/useToast";
import { useUserTimeZone } from "../../hooks/useUserTimeZone";
import { extractDisplayErrorMessage } from "../../utils/errorUtils";
import { PaymentModal } from "../Calendar/PaymentModal";
import type {
  Act,
  ActInput,
  ActResponsible,
  ActStatus,
  Client,
  ClientCase,
  ClientCaseExpenseDetail,
  ClientCaseFinanceSummary,
  ClientInput,
  ClientReferenceItem,
  ClientRole,
  Contract,
  ContractClient,
  ContractInput,
  ContractStatus,
  DadataAddressSuggestion,
  Invoice,
  InvoiceInput,
  InvoiceWorkflowStatus,
  LegalEntityDetail,
  LegalEntityInput,
  LegalEntitySummary,
  Payment,
  PaymentPayload,
  PaymentStatus,
  ClientBalanceSummary,
  ClientBalanceTransaction,
  PowerOfAttorney,
} from "../../types";
import type { MenuSectionKey } from "../../types/permissions";
import { toRuDate, formatLocalYMD } from "../../utils/dateUtils";
import { MonthRangePicker } from "../MonthRange/MonthRangePicker";
import { CaseModal } from "./CaseModal";
import { TwoTypeStats } from "../Statistics/TwoTypeStats";
import { formatCurrencySmart } from "../../utils/formatters";
import { formatDateTimeInTimeZone } from "../../utils/timeZoneDate";
import { formatClientBirthDate, formatClientDate, getClientAgeLabel } from "./clientFormatters";
import { ActModal } from "../Acts/ActModal";
import { InvoiceModal } from "../Accounts/InvoiceModal";
import { ContractModal } from "../Contracts/ContractModal";
import type { CounterpartyKind } from "../ui/CounterpartyPickerModal";
import { LegalEntityModal } from "../LegalEntities/LegalEntityModal";
import { FormPromptModal } from "../ui/FormPromptModal";
import { LookupSelect } from "../ui/LookupSelect";
import { TableColumnSettings } from "../ui/TableColumnSettings";
import { TablePagination } from "../ui/TablePagination";
import { ObjectAuditModal } from "../ui/ObjectAuditModal";
import { TaskJournal } from "../Tasks/TaskJournal";
import { isInvoiceManagedPayment, isPartialRemainderSourcePayment, isTransferredRemainderPayment } from "../../utils/invoicePayments";
import { useClientStatuses } from "../../hooks/useClientStatuses";
import { ClientStatusBadge } from "./ClientStatusBadge";
import { ClientReconciliationAct } from "./ClientReconciliationAct";
import { DatePeriodPicker } from "../DateRange/DatePeriodPicker";
import { loadClientInvoiceRelatedPayments } from "../../utils/clientInvoiceRelatedPayments";
import { refreshClientInvoiceModalData as refreshClientInvoiceModalDataHelper } from "../../utils/clientInvoiceModalData";
import { bindClientInvoiceModalApi } from "../../utils/clientInvoiceModalApi";
import { parseInstallmentPaymentMetadata } from "../../utils/installmentPlan";
import {
  getVisibleNotes,
  getVisibleSystemNotes,
} from "../../utils/systemNotes";
import { getObjectAuditActorLabel } from "../../utils/objectAudit";
import { getContractEffectiveAmount } from "../../utils/contracts";
import {
  formatPeriodLabel,
  resolvePeriod,
  type PeriodFilter,
} from "../../utils/periodUtils";
import { readSessionViewState, writeSessionViewState } from "../../utils/sessionViewState";
import {
  createEmptyClientRelative as createEmptyRelative,
  normalizeClientRelative,
  type ClientRelativeItem,
} from "./clientRelativeUtils";
import {
  ClientAddressEntriesForm,
  type AddressTypeOption,
  type ClientAddressEntryFormValue,
} from "./ClientAddressEntriesForm";
import {
  ClientPassportEntriesForm,
  type ClientPassportEntryFormValue,
} from "./ClientPassportEntriesForm";
import {
  ensureSinglePrimaryAddressEntryList,
  setPrimaryAddressEntry,
  stripAddressEntryValidityList,
} from "./addressEntryPersistence";
import { stripPassportEntryValidityList } from "./passportEntryPersistence";
import { chooseBestDisplayAddress } from "./addressEntryUtils";

interface ClientDetailProps {
  clientId: number;
  onBack: () => void;
  onClientUpdated?: (client: Client) => void;
  initialCaseId?: number | "all";
  initialSection?: ClientDetailSection;
  onOpenInvoicePage?: (invoiceId: number) => void;
  onOpenActPage?: (
    actId: number,
    options?: { section?: "acts" | "accounts" },
  ) => void;
  onCreateActPage?: (
    initialValues?: Partial<ActInput> & { amount?: number | null },
    options?: { section?: "acts" | "accounts" },
  ) => void;
  onOpenCasePage?: (caseId: number, clientId: number) => void;
  onOpenTask?: (taskId: number) => void;
}

type PaymentUpsert = PaymentPayload | ({ id: number } & PaymentPayload);

type NormalizedStatus = "completed" | "pending" | "overdue";
type ClientCaseListStatus = "all" | "open" | "hold" | "closed" | "lost";
type InvoiceViewMode = "income" | "expenseClient" | "supplier";
type PaymentViewMode = "default" | "expenseClient" | "supplier";

type ClientDetailSection =
  | "profile"
  | "cases"
  | "tasks"
  | "payments"
  | "accounts"
  | "acts"
  | "contracts"
  | "settlement";

type ClientDetailViewState = {
  activeSection: ClientDetailSection;
  invoicePeriod: PeriodFilter;
  invoiceStatusFilter: "all" | PaymentStatus;
  invoiceViewMode: InvoiceViewMode;
  invoiceViewModeTouched: boolean;
  invoiceSearch: string;
  invoiceSort: { key: InvoicesSortKey; direction: "asc" | "desc" };
  invoicePage: number;
  actPeriod: PeriodFilter;
  actStatusFilter: "all" | ActStatus;
  actSearch: string;
  actSort: { key: ActsSortKey; direction: "asc" | "desc" };
  actPage: number;
};

type PaymentTableColumnKey =
  | "period"
  | "amountType"
  | "category"
  | "caseContact"
  | "details"
  | "linkedContract"
  | "linkedInvoice"
  | "linkedActs"
  | "status";

type InvoiceTableColumnKey =
  | "date"
  | "number"
  | "counterparty"
  | "amount"
  | "status"
  | "signingStatus"
  | "dueDate"
  | "responsible"
  | "linkedContract"
  | "linkedActs"
  | "linkedPayments"
  | "createdAt";

type ActTableColumnKey =
  | "date"
  | "number"
  | "title"
  | "amount"
  | "status"
  | "linkedContract"
  | "linkedInvoice"
  | "linkedPayments"
  | "responsible";

type ContractTableColumnKey =
  | "number"
  | "title"
  | "status"
  | "date"
  | "clients"
  | "amount"
  | "linkedInvoices"
  | "linkedActs"
  | "linkedPayments"
  | "validUntil"
  | "createdAt";

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
  "profile",
  "cases",
  "tasks",
  "payments",
  "accounts",
  "acts",
  "contracts",
  "settlement",
];

const DETAIL_PAGE_SIZE_OPTIONS = [10, 25, 50, 100];
const LEGAL_ENTITY_LOOKUP_PAGE_SIZE = 20;
const CLIENT_DETAIL_TABLE_STORAGE_PREFIX = "pp.client_detail_table";
const CLIENT_BALANCE_TRANSACTION_LABELS: Record<
  ClientBalanceTransaction["type"],
  string
> = {
  Overpayment: "Переплата",
  CreditApplied: "Зачет",
  ManualAdjustment: "Корректировка",
  Refund: "Возврат",
  AdvanceFromCancelledInvoice: "Аванс по аннулированному счёту",
  UnallocatedAdvance: "Нераспределенный аванс",
};

const PAYMENT_TABLE_COLUMNS: Array<{
  key: PaymentTableColumnKey;
  label: string;
  defaultWidth: number;
}> = [
  { key: "period", label: "Период", defaultWidth: 210 },
  { key: "amountType", label: "Сумма и тип", defaultWidth: 230 },
  { key: "category", label: "Категория / источник", defaultWidth: 250 },
  { key: "caseContact", label: "Дело и контакт", defaultWidth: 240 },
  { key: "details", label: "Документы и комментарии", defaultWidth: 300 },
  { key: "linkedContract", label: "Договор", defaultWidth: 180 },
  { key: "linkedInvoice", label: "Счет", defaultWidth: 180 },
  { key: "linkedActs", label: "Акты", defaultWidth: 190 },
  { key: "status", label: "Статус и прогресс", defaultWidth: 240 },
];

const INVOICE_TABLE_COLUMNS: Array<{
  key: InvoiceTableColumnKey;
  label: string;
  defaultWidth: number;
}> = [
  { key: "date", label: "Дата", defaultWidth: 140 },
  { key: "number", label: "Номер счета", defaultWidth: 240 },
  { key: "counterparty", label: "Контрагент", defaultWidth: 260 },
  { key: "amount", label: "Сумма", defaultWidth: 160 },
  { key: "status", label: "Статус оплаты", defaultWidth: 170 },
  { key: "signingStatus", label: "Статус документа", defaultWidth: 190 },
  { key: "dueDate", label: "Срок оплаты", defaultWidth: 170 },
  { key: "responsible", label: "Ответственный", defaultWidth: 180 },
  { key: "linkedContract", label: "Договор", defaultWidth: 180 },
  { key: "linkedActs", label: "Акты", defaultWidth: 190 },
  { key: "linkedPayments", label: "Платежи", defaultWidth: 210 },
  { key: "createdAt", label: "Создан", defaultWidth: 150 },
];

const INVOICE_VIEW_OPTIONS: Array<{ key: InvoiceViewMode; label: string; description: string }> = [
  {
    key: "income",
    label: "Доходные счета",
    description: "Счета, выставленные этому клиенту",
  },
  {
    key: "expenseClient",
    label: "Расходы по делам",
    description: "Счета поставщиков, отнесенные на дела этого клиента",
  },
  {
    key: "supplier",
    label: "Счета поставщика",
    description: "Расходные счета, где эта карточка является поставщиком",
  },
];

const PAYMENT_VIEW_OPTIONS: Array<{ key: PaymentViewMode; label: string; description: string }> = [
  {
    key: "default",
    label: "Все связи",
    description: "Доходы клиента и расходы, отнесенные на его дела",
  },
  {
    key: "expenseClient",
    label: "Расходы по делам",
    description: "Расходные платежи, где эта карточка является клиентом расхода",
  },
  {
    key: "supplier",
    label: "Платежи поставщика",
    description: "Расходные платежи, где эта карточка является поставщиком",
  },
];

const ACT_TABLE_COLUMNS: Array<{
  key: ActTableColumnKey;
  label: string;
  defaultWidth: number;
}> = [
  { key: "date", label: "Дата", defaultWidth: 140 },
  { key: "number", label: "Номер", defaultWidth: 180 },
  { key: "title", label: "Название", defaultWidth: 260 },
  { key: "amount", label: "Сумма", defaultWidth: 160 },
  { key: "status", label: "Статус", defaultWidth: 160 },
  { key: "linkedContract", label: "Договор", defaultWidth: 180 },
  { key: "linkedInvoice", label: "Счет", defaultWidth: 180 },
  { key: "linkedPayments", label: "Платежи", defaultWidth: 210 },
  { key: "responsible", label: "Ответственный", defaultWidth: 180 },
];

const CONTRACT_TABLE_COLUMNS: Array<{
  key: ContractTableColumnKey;
  label: string;
  defaultWidth: number;
}> = [
  { key: "number", label: "Номер договора", defaultWidth: 180 },
  { key: "title", label: "Название договора", defaultWidth: 240 },
  { key: "status", label: "Статус документа", defaultWidth: 180 },
  { key: "date", label: "Дата договора", defaultWidth: 150 },
  { key: "clients", label: "Клиенты", defaultWidth: 260 },
  { key: "amount", label: "Сумма договора", defaultWidth: 170 },
  { key: "linkedInvoices", label: "Счета", defaultWidth: 190 },
  { key: "linkedActs", label: "Акты", defaultWidth: 190 },
  { key: "linkedPayments", label: "Платежи", defaultWidth: 210 },
  { key: "validUntil", label: "Действует до", defaultWidth: 170 },
  { key: "createdAt", label: "Создано", defaultWidth: 150 },
];

const CONTRACT_STATUS_LABELS: Record<ContractStatus, string> = {
  Draft: "Черновик",
  NotSigned: "Не подписан",
  RequiresClientSignature: "Требуется подпись клиента",
  OnApproval: "На согласовании",
  Approved: "Согласован",
  ApprovalRejected: "Отказ в согласовании",
  NeedsCorrection: "Требует исправления",
  OnSigning: "На подписании",
  Concluded: "Подписан клиентом",
  Terminated: "Расторгнут",
  Cancelled: "Аннулирован",
};

const CONTRACT_STATUS_CLASSES: Record<ContractStatus, string> = {
  Draft: "bg-slate-100 text-slate-700 border border-slate-200",
  NotSigned: "bg-slate-100 text-slate-700 border border-slate-200",
  RequiresClientSignature: "bg-amber-100 text-amber-800 border border-amber-200",
  OnApproval: "bg-cyan-100 text-cyan-700 border border-cyan-200",
  Approved: "bg-emerald-100 text-emerald-700 border border-emerald-200",
  ApprovalRejected: "bg-rose-100 text-rose-700 border border-rose-200",
  NeedsCorrection: "bg-orange-100 text-orange-700 border border-orange-200",
  OnSigning: "bg-violet-100 text-violet-700 border border-violet-200",
  Concluded: "bg-emerald-100 text-emerald-700 border border-emerald-200",
  Terminated: "bg-orange-100 text-orange-700 border border-orange-200",
  Cancelled: "bg-rose-100 text-rose-700 border border-rose-200",
};

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
    label: "Карточка клиента",
    actionLabel: "Сохранить",
  },
  cases: {
    label: "Дела",
    actionLabel: "Добавить дело",
    placeholderTitle: "Дела клиента",
    placeholderDescription:
      "Здесь отображаются все дела, связанные с этим клиентом.",
    icon: FileCheck2,
  },
  tasks: {
    label: "Задачи",
    actionLabel: "Добавить задачу",
    icon: ClipboardList,
  },
  payments: {
    label: "Платежи",
    actionLabel: "Добавить платёж",
  },
  accounts: {
    label: "Счета",
    actionLabel: "Добавить счёт",
    placeholderTitle: "Счета клиента",
    placeholderDescription:
      "Здесь появится список счетов клиента после подключения модуля.",
    icon: WalletCards,
  },
  acts: {
    label: "Акты",
    actionLabel: "Добавить акт",
    placeholderTitle: "Документы клиента",
    placeholderDescription:
      "Управление документами будет доступно после доработки раздела.",
    icon: FileCheck2,
  },
  contracts: {
    label: "Договоры",
    actionLabel: "Добавить договор",
    placeholderTitle: "Договоры клиента",
    placeholderDescription: "Список договоров появится в этом разделе.",
    icon: FileSignature,
  },
  settlement: {
    label: "Акт сверки",
    actionLabel: "Акт сверки",
    placeholderTitle: "Акт сверки",
    placeholderDescription:
      "Сверка взаиморасчётов по счетам, актам и оплатам клиента.",
    icon: Calculator,
  },
};

const SECTION_PERMISSION_MAP: Record<ClientDetailSection, MenuSectionKey> = {
  profile: "clients",
  cases: "cases",
  tasks: "tasks",
  payments: "payments",
  accounts: "accounts",
  acts: "acts",
  contracts: "contracts",
  settlement: "clients",
};

const ACT_STATUS_ORDER: ActStatus[] = [
  "Created",
  "Transferred",
  "Signed",
  "Terminated",
];

const ACT_STATUS_CLASSES: Record<ActStatus, string> = {
  Created: "bg-slate-100 text-slate-700 border border-slate-200",
  Transferred: "bg-amber-100 text-amber-800 border border-amber-200",
  Signed: "bg-emerald-100 text-emerald-700 border border-emerald-200",
  Terminated: "bg-rose-100 text-rose-700 border border-rose-200",
};

const RELATED_FILTER_ALL = "all";

function parseRelatedFilterId(value: string): number | undefined {
  if (value === RELATED_FILTER_ALL) return undefined;
  const id = Number(value);
  return Number.isFinite(id) && id > 0 ? id : undefined;
}

function uniqById<T extends { id: number }>(items: T[]): T[] {
  return [...new Map(items.map((item) => [item.id, item])).values()];
}

function normalizeMoneyAmount(value: number | null | undefined): number {
  const numeric = Number(value ?? 0);
  if (!Number.isFinite(numeric)) return 0;
  return Math.max(0, Math.round(numeric * 100) / 100);
}

function ClientSectionFilterPanel({
  children,
  footer,
  badges,
  onReset,
}: {
  children: React.ReactNode;
  footer?: React.ReactNode;
  badges: string[];
  onReset?: () => void;
}) {
  return (
    <div className="mb-6 space-y-3">
      <div className="rounded-2xl border border-slate-200 bg-slate-50/80 p-4 shadow-sm">
        <div className="flex flex-wrap items-end gap-3">{children}</div>
        {footer ? (
          <div className="mt-4 border-t border-slate-200 pt-4">{footer}</div>
        ) : null}
      </div>

      {badges.length > 0 ? (
        <div className="flex flex-wrap items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
          <span className="text-sm font-semibold text-slate-700">
            Применены фильтры:
          </span>
          {badges.map((badge) => (
            <span
              key={badge}
              className="inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-medium text-slate-600"
            >
              {badge}
            </span>
          ))}
          {onReset ? (
            <button
              type="button"
              onClick={onReset}
              className="ml-auto inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 transition-colors hover:bg-slate-50"
            >
              <RotateCcw className="h-4 w-4" />
              Сбросить все
            </button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function ClientSectionFilterField({
  label,
  children,
  className = "",
}: {
  label: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <label className={`flex min-w-[180px] flex-col gap-1.5 ${className}`}>
      <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
        {label}
      </span>
      {children}
    </label>
  );
}

function normalizeStatus(s?: string): NormalizedStatus {
  const v = (s ?? "").toLowerCase();
  if (v.includes("complete") || v.includes("выполн")) return "completed";
  if (v.includes("overdue") || v.includes("проср")) return "overdue";
  return "pending";
}

function ymStart(ym: string): string {
  const [y, m] = ym.split("-").map(Number);
  return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1));
}
function ymEnd(ym: string): string {
  const [y, m] = ym.split("-").map(Number);
  return formatLocalYMD(new Date(y, m ?? 1, 0));
}

function caseStatusLabel(s?: string) {
  const v = (s ?? "").toLowerCase();
  if (v.includes("open")) return "Открыто";
  if (v.includes("hold")) return "Приостановлено";
  if (v.includes("closed")) return "Закрыто";
  if (v.includes("lost")) return "Проиграно";
  if (v.includes("проигр")) return "Проиграно";
  return s ?? "";
}

function normalizeCaseStatusFilter(
  s?: string,
): Exclude<ClientCaseListStatus, "all"> | "" {
  const value = (s ?? "").toLowerCase();
  if (value.includes("open")) return "open";
  if (value.includes("hold")) return "hold";
  if (value.includes("closed")) return "closed";
  if (value.includes("lost")) return "lost";
  if (value.includes("проигр")) return "lost";
  return "";
}

function formatMonthFilterLabel(from?: string, to?: string): string {
  const formatter = new Intl.DateTimeFormat("ru-RU", {
    month: "long",
    year: "numeric",
  });
  const formatSingle = (value: string) => {
    const [yearRaw, monthRaw] = value.split("-").map(Number);
    if (!yearRaw || !monthRaw) {
      return value;
    }
    return formatter.format(new Date(yearRaw, monthRaw - 1, 1));
  };

  if (from && to) {
    return `${formatSingle(from)} — ${formatSingle(to)}`;
  }
  if (from) {
    return `с ${formatSingle(from)}`;
  }
  if (to) {
    return `по ${formatSingle(to)}`;
  }
  return "";
}

function formatAuditShortDate(value?: string | null): string {
  if (!value) return "дата неизвестна";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "дата неизвестна";
  return date.toLocaleString("ru-RU", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function caseAuditSummary(caseItem: ClientCase): string {
  const createdBy =
    caseItem.auditCreatedByUserName?.trim()
      ? getObjectAuditActorLabel(caseItem.auditCreatedByUserName)
      : "автор неизвестен";
  const updatedBy = caseItem.auditUpdatedByUserName?.trim()
    ? getObjectAuditActorLabel(caseItem.auditUpdatedByUserName)
    : "";

  if (caseItem.auditUpdatedAt || updatedBy) {
    return `Изменил: ${updatedBy || "пользователь неизвестен"}, ${formatAuditShortDate(caseItem.auditUpdatedAt)}`;
  }

  return `Создал: ${createdBy}, ${formatAuditShortDate(caseItem.createdAt)}`;
}

function toStatsStatusFilter(
  s: "all" | NormalizedStatus,
): "All" | "Pending" | "Completed" | "Overdue" {
  switch (s) {
    case "completed":
      return "Completed";
    case "overdue":
      return "Overdue";
    case "pending":
      return "Pending";
    default:
      return "All";
  }
}

function toRuPaymentStatus(status?: string, fallback: string = "—"): string {
  if (!status) return fallback;
  const normalized = status.toLowerCase();
  if (normalized.includes("overdue") || normalized.includes("проср"))
    return "Просрочено";
  if (normalized.includes("complete") || normalized.includes("оплач"))
    return "Оплачено";
  if (normalized.includes("process")) return "Частично оплачено";
  if (normalized.includes("cancel")) return "Отменено";
  if (normalized.includes("pending") || normalized.includes("ожид"))
    return "Ожидается";
  if (normalized.includes("draft")) return "Черновик";
  if (normalized.includes("schedule")) return "Запланировано";
  if (normalized.includes("fail") || normalized.includes("error"))
    return "Ошибка";
  return fallback;
}

function toPaymentStatusFilter(
  s: "all" | NormalizedStatus,
): PaymentStatus | undefined {
  switch (s) {
    case "completed":
      return "Completed";
    case "overdue":
      return "Overdue";
    case "pending":
      return "Pending";
    default:
      return undefined;
  }
}

const MIN_DATE = "1900-01-01";
const MAX_DATE = "2100-12-31";

type ClientKind = "individual" | "legal";

type ClientDocumentKey = "passports" | "birthCertificates" | "militaryIds";

type ClientGender = "male" | "female" | "";
type ClientCitizenship = string;
type ClientMaritalStatus =
  | "never_married"
  | "married"
  | "divorced"
  | "widowed"
  | "civil_union"
  | "";

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

type ClientPassportItem = ClientPassportEntryFormValue;

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

interface ClientPowerOfAttorneyDraft {
  id: number | null;
  number: string;
  issuedAt: string;
  validUntil: string;
  issuer: string;
  notes: string;
  isActive: boolean;
}

interface ClientProfileDeleteActionOptions {
  title: string;
  description: string;
  successMessage: string;
  toastTitle: string;
  onDelete: () => void;
}

const CLIENT_PROFILE_NOTES_PREFIX = "__PP_CLIENT_PROFILE_V2__:";

const createEmptyEducation = (): ClientEducationItem => ({
  institution: "",
  specialty: "",
  diplomaNumber: "",
  oksoCode: "",
});
const createEmptyProfession = (): ClientProfessionItem => ({
  profession: "",
  okpdtrCode: "",
});
const createEmptyWorkplace = (): ClientWorkplaceItem => ({
  organizationName: "",
  position: "",
  startDate: "",
  endDate: "",
});
const createEmptyBirthCertificateEntry = (): ClientBirthCertificateItem => ({
  series: "",
  number: "",
  issueDate: "",
  issuedBy: "",
  actRecordNumber: "",
  childFullName: "",
});
const createEmptyMilitaryIdEntry = (): ClientMilitaryIdItem => ({
  series: "",
  number: "",
  issueDate: "",
  issuedBy: "",
  category: "",
  rank: "",
  vusCode: "",
  fitnessCategory: "",
  militaryCommissariat: "",
});
const createEmptyPassportEntry = (): ClientPassportItem => ({
  series: "",
  number: "",
  issuedBy: "",
  issueDate: "",
  departmentCode: "",
  birthPlacePassport: "",
  birthPlaceFias: "",
  validFrom: "",
  validTo: "",
});
const createEmptyAddressEntry = (isPrimary = false): ClientAddressItem => ({
  type: "registration",
  isPrimary,
  additionalTypes: [],
  sourceQuery: "",
  fullAddress: "",
  postalCode: "",
  region: "",
  district: "",
  city: "",
  settlement: "",
  street: "",
  house: "",
  flat: "",
  fiasId: "",
  kladrId: "",
  geoLat: "",
  geoLon: "",
  validFrom: "",
  validTo: "",
});
const createEmptyPoaDraft = (): ClientPowerOfAttorneyDraft => ({
  id: null,
  number: "",
  issuedAt: formatLocalYMD(new Date()),
  validUntil: "",
  issuer: "",
  notes: "",
  isActive: true,
});
const createPoaDraftFromItem = (
  poa: PowerOfAttorney,
): ClientPowerOfAttorneyDraft => ({
  id: poa.id,
  number: poa.number,
  issuedAt: poa.issuedAt,
  validUntil: poa.validUntil ?? "",
  issuer: poa.issuer ?? "",
  notes: poa.notes ?? "",
  isActive: poa.isActive,
});

const ADDRESS_TYPE_OPTIONS: AddressTypeOption[] = [
  { value: "registration", label: "Адрес регистрации" },
  { value: "actual", label: "Фактический адрес" },
  { value: "legal", label: "Юридический адрес" },
  { value: "temporary", label: "Временная регистрация" },
  { value: "mailing", label: "Почтовый адрес" },
  { value: "other", label: "Иной адрес" },
] as const;

const DATE_INPUT_CLASS =
  "w-full rounded-lg border border-gray-300 px-3 py-2 text-center disabled:bg-gray-100";
const PROFILE_DELETE_ICON_BUTTON_CLASS =
  "inline-flex h-9 w-9 items-center justify-center rounded-lg text-rose-600 transition-colors hover:bg-rose-50 hover:text-rose-700 disabled:cursor-not-allowed disabled:opacity-60";
const PROFILE_SECONDARY_ICON_BUTTON_CLASS =
  "inline-flex h-9 w-9 items-center justify-center rounded-lg text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-700 disabled:cursor-not-allowed disabled:opacity-60";

/** Профильные группы, свёрнутые в блок «Дополнительная информация» (PAY-32). Данные групп не удаляются. */
const PROFILE_ADDITIONAL_GROUPS: readonly string[] = ["education", "professions", "workplaces", "relatives", "poa"];

const MARITAL_STATUS_OPTIONS: Array<{
  value: Exclude<ClientMaritalStatus, "">;
  label: string;
}> = [
  { value: "never_married", label: "Никогда не состоял(а) в браке" },
  { value: "married", label: "В браке" },
  { value: "divorced", label: "Разведён(а)" },
  { value: "widowed", label: "Вдовец / вдова" },
  { value: "civil_union", label: "Гражданский брак" },
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
  if (profile.kind === "legal") {
    const normalizedLegalEntityName = legalEntityName?.trim();
    if (normalizedLegalEntityName) return normalizedLegalEntityName;
    return "Юридическое лицо";
  }
  return (
    [profile.lastName, profile.firstName, profile.middleName]
      .map((item) => item.trim())
      .filter(Boolean)
      .join(" ") || "Новый клиент"
  );
};

const mapBackendAddressTypeToProfile = (type?: number): string => {
  switch (type) {
    case 1:
      return "registration";
    case 2:
      return "actual";
    case 3:
      return "legal";
    default:
      return "other";
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
    .map((item) => (item ?? "").trim())
    .filter(Boolean);

  return parts.join(", ");
};

const buildAddressEntriesFromClient = (client: Client): ClientAddressItem[] => {
  const prepared = (client.addresses ?? [])
    .slice()
    .sort((left, right) => Number(right.isPrimary) - Number(left.isPrimary))
    .map((address): ClientAddressItem | null => {
      const fullAddress = (
        address.unrestrictedValue ??
        address.value ??
        ""
      ).trim();
      if (!fullAddress) return null;
      const structuredAddress = {
        postalCode: address.postalCode ?? "",
        country: address.country ?? "",
        region: address.regionWithType ?? "",
        district: address.areaWithType ?? "",
        city: address.cityWithType ?? "",
        settlement: address.settlementWithType ?? "",
        street: address.streetWithType ?? "",
        house: address.house ?? "",
        flat: address.flat ?? "",
      };
      return {
        ...createEmptyAddressEntry(address.isPrimary),
        type: mapBackendAddressTypeToProfile(address.type),
        sourceQuery: fullAddress,
        fullAddress: chooseBestDisplayAddress(
          structuredAddress,
          fullAddress,
          composeAddressFromParts({
            ...createEmptyAddressEntry(),
            ...structuredAddress,
          }),
        ),
        postalCode: structuredAddress.postalCode,
        country: structuredAddress.country,
        region: structuredAddress.region,
        district: structuredAddress.district,
        city: structuredAddress.city,
        settlement: structuredAddress.settlement,
        street: structuredAddress.street,
        house: structuredAddress.house,
        flat: structuredAddress.flat,
        fiasId: address.fiasId ?? "",
        kladrId: address.kladrId ?? "",
        geoLat: address.geoLat ?? "",
        geoLon: address.geoLon ?? "",
        isPrimary: address.isPrimary,
      };
    })
    .filter((item): item is ClientAddressItem => item !== null);

  if (prepared.length > 0) return ensureSinglePrimaryAddressEntryList(prepared);

  if (client.address?.trim()) {
    return [
      {
        ...createEmptyAddressEntry(true),
        type: "registration",
        sourceQuery: client.address,
        fullAddress: client.address,
      },
    ];
  }

  return [createEmptyAddressEntry(true)];
};

const parseClientExtendedProfile = (client: Client): ClientExtendedProfile => {
  const sourceNotes = client.notes ?? "";
  const profileAddressEntries = buildAddressEntriesFromClient(client);
  const registrationEntry =
    profileAddressEntries.find((entry) => entry.type === "registration") ??
    profileAddressEntries.find((entry) => entry.type === "legal") ??
    profileAddressEntries[0] ??
    createEmptyAddressEntry();
  const actualEntry =
    profileAddressEntries.find((entry) => entry.type === "actual") ??
    profileAddressEntries[1] ??
    registrationEntry;
  const defaultProfile: ClientExtendedProfile = {
    kind: client.legalEntityId ? "legal" : "individual",
    lastName: "",
    firstName: "",
    middleName: "",
    gender: "",
    birthDate: "",
    birthPlacePassport: "",
    birthPlaceFias: "",
    citizenship: "",
    inn: "",
    snils: "",
    passportSeries: "",
    passportNumber: "",
    passportIssuedBy: "",
    passportIssueDate: "",
    passportDepartmentCode: "",
    education: [createEmptyEducation()],
    professions: [createEmptyProfession()],
    workplaces: [createEmptyWorkplace()],
    maritalStatus: "",
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
      Boolean(actualEntry.fullAddress) &&
      actualEntry.fullAddress === registrationEntry.fullAddress,
    phones: client.phone ? [client.phone] : [""],
    emails: client.email ? [client.email] : [""],
    addresses: profileAddressEntries
      .map((entry) => entry.fullAddress)
      .filter(Boolean),
    passports: [""],
    birthCertificates: [""],
    militaryIds: [""],
    birthCertificateEntries: [createEmptyBirthCertificateEntry()],
    militaryIdEntries: [createEmptyMilitaryIdEntry()],
    passportEntries: [createEmptyPassportEntry()],
    addressEntries: profileAddressEntries,
    comment: "",
    legacyNotes: sourceNotes.startsWith(CLIENT_PROFILE_NOTES_PREFIX)
      ? ""
      : sourceNotes,
  };

  if (client.name && !client.legalEntityId) {
    const [lastName = "", firstName = "", ...rest] = client.name
      .trim()
      .split(/\s+/);
    defaultProfile.lastName = lastName;
    defaultProfile.firstName = firstName;
    defaultProfile.middleName = rest.join(" ");
  }

  const rawProfilePayload = sourceNotes.startsWith(CLIENT_PROFILE_NOTES_PREFIX)
    ? sourceNotes.slice(CLIENT_PROFILE_NOTES_PREFIX.length)
    : "";

  if (!rawProfilePayload) return defaultProfile;

  try {
    const parsed = JSON.parse(
      rawProfilePayload,
    ) as Partial<ClientExtendedProfile>;
    return {
      ...defaultProfile,
      ...parsed,
      kind: parsed.kind === "legal" ? "legal" : "individual",
      gender:
        parsed.gender === "male" || parsed.gender === "female"
          ? parsed.gender
          : "",
      citizenship:
        parsed.citizenship &&
        ["rf", "double", "foreign", "stateless", "other"].includes(
          parsed.citizenship,
        )
          ? (parsed.citizenship as ClientCitizenship)
          : "",
      maritalStatus:
        parsed.maritalStatus &&
        [
          "never_married",
          "married",
          "divorced",
          "widowed",
          "civil_union",
        ].includes(parsed.maritalStatus)
          ? (parsed.maritalStatus as ClientMaritalStatus)
          : "",
      passportSeries: parsed.passportSeries ?? defaultProfile.passportSeries,
      passportNumber: parsed.passportNumber ?? defaultProfile.passportNumber,
      passportIssuedBy:
        parsed.passportIssuedBy ?? defaultProfile.passportIssuedBy,
      passportIssueDate:
        parsed.passportIssueDate ?? defaultProfile.passportIssueDate,
      passportDepartmentCode:
        parsed.passportDepartmentCode ?? defaultProfile.passportDepartmentCode,
      education: (parsed.education ?? defaultProfile.education).map((item) => ({
        ...createEmptyEducation(),
        ...item,
      })),
      professions: (parsed.professions ?? defaultProfile.professions).map(
        (item) => ({ ...createEmptyProfession(), ...item }),
      ),
      workplaces: (parsed.workplaces ?? defaultProfile.workplaces).map(
        (item) => ({ ...createEmptyWorkplace(), ...item }),
      ),
      relatives: (parsed.relatives ?? defaultProfile.relatives).map((item) =>
        normalizeClientRelative(item),
      ),
      phones: normalizeList(parsed.phones ?? defaultProfile.phones),
      emails: normalizeList(parsed.emails ?? defaultProfile.emails),
      addresses: normalizeList(parsed.addresses ?? defaultProfile.addresses),
      passports: normalizeList(parsed.passports ?? defaultProfile.passports),
      birthCertificates: normalizeList(
        parsed.birthCertificates ?? defaultProfile.birthCertificates,
      ),
      militaryIds: normalizeList(
        parsed.militaryIds ?? defaultProfile.militaryIds,
      ),
      birthCertificateEntries: (
        parsed.birthCertificateEntries ?? defaultProfile.birthCertificateEntries
      ).map((item) => ({ ...createEmptyBirthCertificateEntry(), ...item })),
      militaryIdEntries: (
        parsed.militaryIdEntries ?? defaultProfile.militaryIdEntries
      ).map((item) => ({ ...createEmptyMilitaryIdEntry(), ...item })),
      passportEntries: (
        parsed.passportEntries ?? [
          {
            ...createEmptyPassportEntry(),
            series: parsed.passportSeries ?? defaultProfile.passportSeries,
            number: parsed.passportNumber ?? defaultProfile.passportNumber,
            issuedBy:
              parsed.passportIssuedBy ?? defaultProfile.passportIssuedBy,
            issueDate:
              parsed.passportIssueDate ?? defaultProfile.passportIssueDate,
            departmentCode:
              parsed.passportDepartmentCode ??
              defaultProfile.passportDepartmentCode,
            birthPlacePassport:
              parsed.birthPlacePassport ?? defaultProfile.birthPlacePassport,
            birthPlaceFias:
              parsed.birthPlaceFias ?? defaultProfile.birthPlaceFias,
          },
        ]
      ).map((item) => ({ ...createEmptyPassportEntry(), ...item })),
      addressEntries: ensureSinglePrimaryAddressEntryList(
        (
          parsed.addressEntries ?? [
            {
              ...createEmptyAddressEntry(),
              type: "registration",
              sourceQuery:
                parsed.registrationAddress ??
                defaultProfile.registrationAddress,
              fullAddress:
                parsed.registrationAddress ??
                defaultProfile.registrationAddress,
              postalCode:
                parsed.registrationAddressPostalCode ??
                defaultProfile.registrationAddressPostalCode,
              fiasId:
                parsed.registrationAddressFiasId ??
                defaultProfile.registrationAddressFiasId,
              kladrId:
                parsed.registrationAddressKladrId ??
                defaultProfile.registrationAddressKladrId,
              geoLat:
                parsed.registrationAddressGeoLat ??
                defaultProfile.registrationAddressGeoLat,
              geoLon:
                parsed.registrationAddressGeoLon ??
                defaultProfile.registrationAddressGeoLon,
            },
            {
              ...createEmptyAddressEntry(),
              type: "actual",
              sourceQuery: parsed.actualAddress ?? defaultProfile.actualAddress,
              fullAddress: parsed.actualAddress ?? defaultProfile.actualAddress,
              postalCode:
                parsed.actualAddressPostalCode ??
                defaultProfile.actualAddressPostalCode,
              fiasId:
                parsed.actualAddressFiasId ??
                defaultProfile.actualAddressFiasId,
              kladrId:
                parsed.actualAddressKladrId ??
                defaultProfile.actualAddressKladrId,
              geoLat:
                parsed.actualAddressGeoLat ??
                defaultProfile.actualAddressGeoLat,
              geoLon:
                parsed.actualAddressGeoLon ??
                defaultProfile.actualAddressGeoLon,
            },
          ]
        ).map((item) => ({
          ...createEmptyAddressEntry(),
          ...item,
          isPrimary: Boolean((item as ClientAddressItem).isPrimary),
          additionalTypes: Array.isArray(
            (item as ClientAddressItem).additionalTypes,
          )
            ? (item as ClientAddressItem).additionalTypes
            : [],
        })),
      ),
      comment: parsed.comment ?? "",
      legacyNotes: parsed.legacyNotes ?? defaultProfile.legacyNotes,
    };
  } catch {
    return defaultProfile;
  }
};

const serializeClientExtendedProfile = (
  profile: ClientExtendedProfile,
): string =>
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
    passportEntries: stripPassportEntryValidityList(profile.passportEntries),
    addressEntries: stripAddressEntryValidityList(profile.addressEntries),
    comment: profile.comment.trim(),
    legacyNotes: profile.legacyNotes.trim(),
  });

function ProfileDocumentReadList({ title, entries }: { title: string; entries: Array<Array<{ label: string; value: string }>> }) {
  const populatedEntries = entries.filter((entry) => entry.some((field) => typeof field.value === 'string' && field.value.trim()));
  return (
    <section className="min-w-0 space-y-2 md:col-span-2" aria-label={title}>
      <h3 className="text-sm font-semibold text-slate-800">{title}</h3>
      {populatedEntries.length === 0 ? <p className="text-sm text-slate-500">Не указано</p> : populatedEntries.map((entry, index) => (
        <dl key={index} className="grid min-w-0 gap-2 border-b border-slate-200 pb-3 last:border-0 last:pb-0 md:grid-cols-2 xl:grid-cols-4">
          {entry.map(({ label, value }) => <div key={label} className="min-w-0"><dt className="text-xs text-slate-500">{label}</dt><dd className="whitespace-pre-wrap text-sm text-slate-900 [overflow-wrap:anywhere]">{value || 'Не указано'}</dd></div>)}
        </dl>
      ))}
    </section>
  );
}

export function ClientDetail({
  clientId,
  onBack,
  onClientUpdated,
  initialCaseId,
  initialSection,
  onOpenInvoicePage,
  onCreateActPage,
  onOpenCasePage,
  onOpenTask,
}: ClientDetailProps) {
  const { t } = useTranslation();
  const { user, isAdmin } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
  const profileTimeZoneId = useUserTimeZone();
  const permissions = useRolePermissions(user?.role?.id);
  const userIsAdmin = isAdmin();
  const clientPermissions = permissions.clients;
  const legalEntityPermissions = permissions.legalEntities;
  const casePermissions = permissions.cases;
  const accountPermissions = permissions.accounts;
  const actPermissions = permissions.acts;
  const paymentPermissions = permissions.payments;
  const contractPermissions = permissions.contracts;
  const canViewAudit = userIsAdmin || permissions.audit?.canView === true;
  const allowedContractCounterpartyScopes = useMemo<CounterpartyKind[]>(() => {
    const scopes: CounterpartyKind[] = [];
    if (clientPermissions.canView) scopes.push("client");
    if (legalEntityPermissions.canView) scopes.push("legalEntity");
    return scopes;
  }, [clientPermissions.canView, legalEntityPermissions.canView]);
  const sectionPermissions = useCallback(
    (section: ClientDetailSection) =>
      permissions[SECTION_PERMISSION_MAP[section]],
    [permissions],
  );
  const canViewSection = useCallback(
    (section: ClientDetailSection) => {
      if (section === "settlement") {
        return (
          accountPermissions.canView &&
          actPermissions.canView &&
          paymentPermissions.canView
        );
      }

      return sectionPermissions(section).canView;
    },
    [
      accountPermissions.canView,
      actPermissions.canView,
      paymentPermissions.canView,
      sectionPermissions,
    ],
  );
  const canViewClientFinancialSummary = userIsAdmin || (
    paymentPermissions.canView && paymentPermissions.viewScope === "all"
  );
  const canViewClientCaseFinancialSummary =
    casePermissions.canView && paymentPermissions.canView;
  const clientViewStateKey = useMemo(
    () => `pp.clientDetail.${user?.id ?? "guest"}.${clientId}.viewState.v1`,
    [clientId, user?.id],
  );
  const savedViewState = useMemo(
    () => readSessionViewState<Partial<ClientDetailViewState> | null>(clientViewStateKey, null),
    [clientViewStateKey],
  );
  const [activeSection, setActiveSection] = useState<ClientDetailSection>(
    () => {
      if (initialSection) {
        try {
          if (canViewSection(initialSection)) {
            return initialSection;
          }
        } catch {
          // ignore invalid initial section and use the first available one
        }
      }

      const firstAllowed = SECTION_ORDER.find((section) => {
        try {
          return canViewSection(section);
        } catch {
          return false;
        }
      });
      const savedSection = savedViewState?.activeSection;
      if (savedSection) {
        try {
          if (canViewSection(savedSection)) {
            return savedSection;
          }
        } catch {
          // ignore invalid saved section and use the first available one
        }
      }
      return firstAllowed ?? "payments";
    },
  );
  const { statuses: clientStatuses } = useClientStatuses();
  const [clientName, setClientName] = useState<string>("...");
  const [clientData, setClientData] = useState<Client | null>(null);
  const taskJournalContext = useMemo(
    () => {
      const title = clientName && clientName !== "..." ? clientName : clientData?.name ?? `Клиент #${clientId}`;
      return {
        clientId,
        objectName: title,
        title,
      };
    },
    [clientData?.name, clientId, clientName],
  );
  const [clientBalance, setClientBalance] = useState<ClientBalanceSummary | null>(null);
  const [clientBalanceTransactions, setClientBalanceTransactions] = useState<ClientBalanceTransaction[]>([]);
  const [clientBalanceLoading, setClientBalanceLoading] = useState(false);
  const [clientBalanceExpanded, setClientBalanceExpanded] = useState(false);
  const [clientAuditOpen, setClientAuditOpen] = useState(false);
  const [clientProfileEditing, setClientProfileEditing] = useState(false);
  const [selectedCaseId, setSelectedCaseId] = useState<number | "all">(
    initialCaseId ?? "all",
  );
  const [clientFormSaving, setClientFormSaving] = useState(false);
  const refreshClientBalance = useCallback(async () => {
    if (!clientId || !canViewClientFinancialSummary) {
      setClientBalance(null);
      setClientBalanceLoading(false);
      return;
    }

    setClientBalanceLoading(true);
    try {
      const summary = await apiService.getClientBalance(clientId);
      setClientBalance(summary);
    } catch (error) {
      console.error("Failed to load client balance", error);
      setClientBalance(null);
    } finally {
      setClientBalanceLoading(false);
    }
  }, [canViewClientFinancialSummary, clientId]);

  const refreshClientBalanceTransactions = useCallback(async () => {
    if (!clientId || !canViewClientFinancialSummary) {
      setClientBalanceTransactions([]);
      return;
    }

    try {
      const transactions = await apiService.getClientBalanceTransactions(clientId);
      setClientBalanceTransactions(transactions);
    } catch (error) {
      console.error("Failed to load client balance transactions", error);
      setClientBalanceTransactions([]);
    }
  }, [canViewClientFinancialSummary, clientId]);

  useEffect(() => {
    void refreshClientBalance();
  }, [refreshClientBalance]);

  useEffect(() => {
    if (!clientBalanceExpanded || !canViewClientFinancialSummary) return;
    void refreshClientBalanceTransactions();
  }, [
    canViewClientFinancialSummary,
    clientBalanceExpanded,
    refreshClientBalanceTransactions,
  ]);
  useEffect(() => {
    if (canViewClientFinancialSummary) return;
    setClientBalanceExpanded(false);
    setClientBalanceTransactions([]);
  }, [canViewClientFinancialSummary]);
  // Временно закомментировано: будет возвращено при реинтеграции табов профиля.
  // const [activeProfileTab, setActiveProfileTab] = useState<ProfileTabKey>('phone');
  // const [profileDocuments, setProfileDocuments] = useState<ProfileDocumentsState>({
  //   passport: '',
  //   birthCertificate: '',
  //   militaryId: '',
  // });
  const [clientFormState, setClientFormState] = useState({
    kind: "individual" as ClientKind,
    lastName: "",
    firstName: "",
    middleName: "",
    gender: "" as ClientGender,
    birthDate: "",
    birthPlacePassport: "",
    birthPlaceFias: "",
    citizenship: "" as ClientCitizenship,
    inn: "",
    snils: "",
    passportSeries: "",
    passportNumber: "",
    passportIssuedBy: "",
    passportIssueDate: "",
    passportDepartmentCode: "",
    education: [createEmptyEducation()],
    professions: [createEmptyProfession()],
    workplaces: [createEmptyWorkplace()],
    maritalStatus: "" as ClientMaritalStatus,
    relatives: [createEmptyRelative()],
    registrationAddress: "",
    registrationAddressPostalCode: "",
    registrationAddressFiasId: "",
    registrationAddressKladrId: "",
    registrationAddressGeoLat: "",
    registrationAddressGeoLon: "",
    actualAddress: "",
    actualAddressPostalCode: "",
    actualAddressFiasId: "",
    actualAddressKladrId: "",
    actualAddressGeoLat: "",
    actualAddressGeoLon: "",
    actualAddressMatchesRegistration: false,
    phones: [""],
    emails: [""],
    addresses: [""],
    passports: [""],
    birthCertificates: [""],
    militaryIds: [""],
    birthCertificateEntries: [createEmptyBirthCertificateEntry()],
    militaryIdEntries: [createEmptyMilitaryIdEntry()],
    passportEntries: [createEmptyPassportEntry()],
    addressEntries: [createEmptyAddressEntry(true)],
    comment: "",
    legacyNotes: "",
    legalEntityId: "",
    responsibleUserId: "",
    isActive: true,
    clientStatusId: "",
    isClientAndSupplier: false,
  });
  const clientFormBaselineRef = useRef<typeof clientFormState | null>(null);
  const [legalEntityOptions, setLegalEntityOptions] = useState<LegalEntitySummary[]>([]);
  const [legalEntityLookupSearch, setLegalEntityLookupSearch] = useState("");
  const debouncedLegalEntityLookupSearch = useDebouncedValue(
    legalEntityLookupSearch.trim(),
    300,
  );
  const [legalEntityLookupLoading, setLegalEntityLookupLoading] =
    useState(false);
  const [legalEntityLookupTotal, setLegalEntityLookupTotal] = useState(0);
  const [addressSuggestionsByIndex, setAddressSuggestionsByIndex] = useState<
    Record<number, DadataAddressSuggestion[]>
  >({});
  const addressSuggestionTimersRef = useRef<Record<number, number>>({});
  const [oksoOptions, setOksoOptions] = useState<ClientReferenceItem[]>([]);
  const [okpdtrOptions, setOkpdtrOptions] = useState<ClientReferenceItem[]>([]);
  const [oksoSearch, setOksoSearch] = useState("");
  const [okpdtrSearch, setOkpdtrSearch] = useState("");
  const debouncedOksoSearch = useDebouncedValue(oksoSearch.trim(), 300);
  const debouncedOkpdtrSearch = useDebouncedValue(okpdtrSearch.trim(), 300);
  const [oksoLoading, setOksoLoading] = useState(false);
  const [okpdtrLoading, setOkpdtrLoading] = useState(false);
  const [powersOfAttorney, setPowersOfAttorney] = useState<PowerOfAttorney[]>(
    [],
  );
  const [powersOfAttorneyLoaded, setPowersOfAttorneyLoaded] = useState(false);
  const [activeProfileGroup, setActiveProfileGroup] = useState<
    | "main"
    | "passports"
    | "addresses"
    | "education"
    | "professions"
    | "workplaces"
    | "relatives"
    | "birthCertificates"
    | "militaryIds"
    | "poa"
  >("main");
  const [profileAdditionalOpen, setProfileAdditionalOpen] = useState(false);
  const [legalEntityPromptOpen, setLegalEntityPromptOpen] = useState(false);
  const [legalEntityPromptSubmitting, setLegalEntityPromptSubmitting] =
    useState(false);
  const [legalEntityPromptError, setLegalEntityPromptError] = useState<
    string | null
  >(null);
  const [legalEntityDraft, setLegalEntityDraft] = useState({
    shortName: "",
    inn: "",
  });
  const [legalEntityModalOpen, setLegalEntityModalOpen] = useState(false);
  const [legalEntityModalInitial, setLegalEntityModalInitial] =
    useState<LegalEntityDetail | null>(null);
  const [poaPromptOpen, setPoaPromptOpen] = useState(false);
  const [poaPromptSubmitting, setPoaPromptSubmitting] = useState(false);
  const [poaPromptError, setPoaPromptError] = useState<string | null>(null);
  const [poaDraft, setPoaDraft] = useState<ClientPowerOfAttorneyDraft>(() =>
    createEmptyPoaDraft(),
  );
  const isEditingPoa = poaDraft.id !== null;

  const hasMeaningfulValue = (value: unknown): boolean => {
    if (value === null || value === undefined) return false;
    if (typeof value === "string") return value.trim().length > 0;
    if (Array.isArray(value))
      return value.some((item) => hasMeaningfulValue(item));
    if (typeof value === "object") {
      return Object.values(value as Record<string, unknown>).some((item) =>
        hasMeaningfulValue(item),
      );
    }
    return Boolean(value);
  };

  const requiredInputClass = (requiredKey: string, value: unknown) => {
    if (!isRequired(requiredKey))
      return "border-gray-300 focus:ring-emerald-500 focus:border-emerald-500";

    return hasMeaningfulValue(value)
      ? "border-emerald-400 focus:ring-emerald-200 focus:border-emerald-500"
      : "border-red-400 focus:ring-red-200 focus:border-red-500";
  };

  const requiredMark = (requiredKey: string) =>
    isRequired(requiredKey) ? " *" : "";

  const isRequiredListMissing = (requiredKey: string, values: unknown[]) =>
    isRequired(requiredKey) && !values.some((item) => hasMeaningfulValue(item));

  const hasMissingRequiredList = (
    requiredKeys: string[] | undefined,
    values: unknown[],
  ) =>
    Boolean(
      requiredKeys?.some((requiredKey) =>
        isRequiredListMissing(requiredKey, values),
      ),
    );

  const hasAnyRequiredList = (requiredKeys: string[] | undefined) =>
    Boolean(requiredKeys?.some((requiredKey) => isRequired(requiredKey)));

  const closeLegalEntityPrompt = (force = false) => {
    if (legalEntityPromptSubmitting && !force) return;
    setLegalEntityPromptOpen(false);
    setLegalEntityPromptError(null);
    setLegalEntityDraft({ shortName: "", inn: "" });
  };

  const mergeLegalEntityOptions = useCallback((items: LegalEntitySummary[]) => {
    setLegalEntityOptions((prev) => {
      const byId = new Map<number, LegalEntitySummary>();
      [...prev, ...items].forEach((item) => byId.set(item.id, item));
      return [...byId.values()].sort((a, b) =>
        a.shortName.localeCompare(b.shortName, undefined, {
          sensitivity: "base",
        }),
      );
    });
  }, []);

  const openLegalEntityCreateModal = () => {
    setLegalEntityModalInitial(null);
    setLegalEntityModalOpen(true);
  };

  const openLegalEntityEditModal = async () => {
    if (!selectedLegalEntity) return;
    try {
      const detail = await apiService.getLegalEntity(selectedLegalEntity.id);
      setLegalEntityModalInitial(detail);
      setLegalEntityModalOpen(true);
    } catch (error) {
      const message = extractDisplayErrorMessage(
        error,
        "Не удалось загрузить карточку юридического лица",
      );
      toast.error(message, "Юрлица");
    }
  };

  const closeLegalEntityModal = () => {
    setLegalEntityModalOpen(false);
    setLegalEntityModalInitial(null);
  };

  const submitLegalEntityModal = async (
    payload: LegalEntityInput,
  ): Promise<LegalEntityDetail> => {
    const nextClientIds = Array.from(
      new Set([...(payload.clientIds ?? []), clientId].filter((id) => id > 0)),
    );
    const nextPayload = { ...payload, clientIds: nextClientIds };
    const saved = legalEntityModalInitial
      ? await apiService.updateLegalEntity(legalEntityModalInitial.id, nextPayload)
      : await apiService.createLegalEntity(nextPayload);

    mergeLegalEntityOptions([saved]);
    setClientFormState((prev) => ({
      ...prev,
      kind: "legal",
      legalEntityId: String(saved.id),
    }));
    closeLegalEntityModal();
    toast.success(
      legalEntityModalInitial
        ? "Карточка юридического лица обновлена."
        : "Юридическое лицо создано и выбрано.",
      "Юрлица",
    );
    return saved;
  };

  const submitQuickLegalEntity = async () => {
    const shortName = legalEntityDraft.shortName.trim();
    const inn = legalEntityDraft.inn.trim();

    if (!shortName) {
      setLegalEntityPromptError("Укажите название организации.");
      return;
    }

    try {
      setLegalEntityPromptSubmitting(true);
      const created = await apiService.createLegalEntity({
        shortName,
        inn: inn || undefined,
        clientIds: [clientId],
      });
      mergeLegalEntityOptions([created]);
      setClientFormState((prev) => ({
        ...prev,
        kind: "legal",
        legalEntityId: String(created.id),
        workplaces: prev.workplaces.length
          ? prev.workplaces.map((workplace, index) =>
              index === prev.workplaces.length - 1
                ? { ...workplace, organizationName: created.shortName }
                : workplace,
            )
          : [
              {
                organizationName: created.shortName,
                position: "",
                startDate: "",
                endDate: "",
              },
            ],
      }));
      closeLegalEntityPrompt(true);
      toast.success("Организация создана.", "Юрлица");
    } catch (error) {
      const message = extractDisplayErrorMessage(
        error,
        "Не удалось создать организацию",
      );
      setLegalEntityPromptError(message);
      toast.error(message, "Юрлица");
    } finally {
      setLegalEntityPromptSubmitting(false);
    }
  };

  const closePoaPrompt = (force = false) => {
    if (poaPromptSubmitting && !force) return;
    setPoaPromptOpen(false);
    setPoaPromptError(null);
    setPoaDraft(createEmptyPoaDraft());
  };

  const openCreatePoaPrompt = () => {
    setPoaPromptError(null);
    setPoaDraft(createEmptyPoaDraft());
    setPoaPromptOpen(true);
  };

  const openEditPoaPrompt = (poa: PowerOfAttorney) => {
    setPoaPromptError(null);
    setPoaDraft(createPoaDraftFromItem(poa));
    setPoaPromptOpen(true);
  };

  const submitQuickPowerOfAttorney = async () => {
    const number = poaDraft.number.trim();
    const issuedAt = poaDraft.issuedAt.trim();

    if (!number || !issuedAt) {
      setPoaPromptError("Укажите номер и дату выдачи доверенности.");
      return;
    }

    try {
      setPoaPromptSubmitting(true);
      const payload = {
        number,
        issuedAt,
        validUntil: poaDraft.validUntil.trim() || undefined,
        issuer: poaDraft.issuer.trim() || undefined,
        notes: poaDraft.notes.trim() || undefined,
        isActive: poaDraft.isActive,
      };

      if (poaDraft.id === null) {
        const created = await apiService.createClientPowerOfAttorney(
          clientId,
          payload,
        );
        setPowersOfAttorney((prev) => [created, ...prev]);
        toast.success("Доверенность создана.", "Доверенности");
      } else {
        const updated = await apiService.updateClientPowerOfAttorney(
          clientId,
          poaDraft.id,
          payload,
        );
        setPowersOfAttorney((prev) =>
          prev.map((item) => (item.id === updated.id ? updated : item)),
        );
        toast.success("Доверенность обновлена.", "Доверенности");
      }
      closePoaPrompt(true);
    } catch (error) {
      const message = extractDisplayErrorMessage(
        error,
        poaDraft.id === null
          ? "Не удалось создать доверенность"
          : "Не удалось обновить доверенность",
      );
      setPoaPromptError(message);
      toast.error(message, "Доверенности");
    } finally {
      setPoaPromptSubmitting(false);
    }
  };

  const deletePowerOfAttorney = async (poa: PowerOfAttorney) => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning("Недостаточно прав для редактирования клиента.");
      return;
    }

    const ok = await confirm({
      title: "Удаление доверенности",
      description: `Удалить доверенность №${poa.number}?`,
      confirmLabel: "Удалить",
      cancelLabel: "Отмена",
      variant: "danger",
    });

    if (!ok) return;

    try {
      await apiService.deleteClientPowerOfAttorney(clientId, poa.id);
      setPowersOfAttorney((prev) => prev.filter((item) => item.id !== poa.id));
      if (poaDraft.id === poa.id) {
        closePoaPrompt(true);
      }
      toast.success("Доверенность удалена.", "Доверенности");
    } catch (error) {
      const message = extractDisplayErrorMessage(
        error,
        "Не удалось удалить доверенность",
      );
      toast.error(message, "Доверенности");
    }
  };

  const fieldLabels: Record<string, string> = {
    Emails: "Email",
    Relatives: "Родственники",
    ResponsibleUserId: "Ответственный",
    ClientStatusId: "Статус клиента",
    LastName: "Фамилия",
    FirstName: "Имя",
    MiddleName: "Отчество",
    Inn: "ИНН",
    Snils: "СНИЛС",
    Comment: "Комментарий",
  };

  const formatApiValidationError = (error: unknown, fallback: string) => {
    return extractDisplayErrorMessage(error, fallback, fieldLabels);
  };

  const isSupplierCard = useMemo(() => {
    const systemKey =
      clientData?.clientStatus?.systemKey ??
      clientStatuses.find((status) => status.id === clientData?.clientStatusId)?.systemKey ??
      "";
    if (["supplier", "creditor"].includes(systemKey.toLowerCase())) {
      return true;
    }

    const statusName =
      clientData?.clientStatus?.name ??
      clientStatuses.find((status) => status.id === clientData?.clientStatusId)?.name ??
      "";
    return /поставщик|кредитор/i.test(statusName);
  }, [
    clientData?.clientStatus?.name,
    clientData?.clientStatus?.systemKey,
    clientData?.clientStatusId,
    clientStatuses,
  ]);

  const { cases: serverCases } = useClientCases(clientId, {
    enabled:
      casePermissions.canView &&
      (activeSection === "cases" ||
        (activeSection === "payments" && isSupplierCard)),
  });
  const [casesLocal, setCasesLocal] = useState<ClientCase[] | null>(null);
  useEffect(() => setCasesLocal(serverCases), [serverCases]);
  const cases = useMemo(
    () => casesLocal ?? serverCases,
    [casesLocal, serverCases],
  );
  const [caseSearch, setCaseSearch] = useState("");
  const [caseStatusFilter, setCaseStatusFilter] =
    useState<ClientCaseListStatus>("all");
  const [caseFinanceSummaries, setCaseFinanceSummaries] = useState<
    Record<number, ClientCaseFinanceSummary>
  >({});
  const [caseFinanceLoading, setCaseFinanceLoading] = useState(false);
  const [caseFinanceError, setCaseFinanceError] = useState<string | null>(null);
  const [expenseDetailsCase, setExpenseDetailsCase] = useState<{
    id: number;
    title: string;
    clientName?: string | null;
  } | null>(null);
  const [expenseDetails, setExpenseDetails] = useState<
    ClientCaseExpenseDetail[]
  >([]);
  const [expenseDetailsLoading, setExpenseDetailsLoading] = useState(false);
  const [expenseDetailsError, setExpenseDetailsError] = useState<string | null>(
    null,
  );

  useEffect(() => {
    setPowersOfAttorney([]);
    setPowersOfAttorneyLoaded(false);
  }, [clientId]);

  useEffect(() => {
    if (
      activeSection !== "profile" ||
      activeProfileGroup !== "education"
    ) {
      return;
    }

    const controller = new AbortController();
    setOksoLoading(true);
    apiService
      .getOksoDictionary(
        { search: debouncedOksoSearch || undefined, limit: 30 },
        { signal: controller.signal },
      )
      .then((items) => {
        if (controller.signal.aborted) return;
        setOksoOptions(items ?? []);
      })
      .catch(() => {
        if (controller.signal.aborted) return;
        setOksoOptions([]);
      })
      .finally(() => {
        if (!controller.signal.aborted) setOksoLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [activeProfileGroup, activeSection, debouncedOksoSearch]);

  useEffect(() => {
    if (
      activeSection !== "profile" ||
      activeProfileGroup !== "professions"
    ) {
      return;
    }

    const controller = new AbortController();
    setOkpdtrLoading(true);
    apiService
      .getOkpdtrDictionary(
        { search: debouncedOkpdtrSearch || undefined, limit: 30 },
        { signal: controller.signal },
      )
      .then((items) => {
        if (controller.signal.aborted) return;
        setOkpdtrOptions(items ?? []);
      })
      .catch(() => {
        if (controller.signal.aborted) return;
        setOkpdtrOptions([]);
      })
      .finally(() => {
        if (!controller.signal.aborted) setOkpdtrLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [activeProfileGroup, activeSection, debouncedOkpdtrSearch]);

  useEffect(() => {
    if (
      activeSection !== "profile" ||
      activeProfileGroup !== "poa" ||
      powersOfAttorneyLoaded
    ) {
      return;
    }

    let alive = true;
    apiService
      .getClientPowersOfAttorney(clientId)
      .then((items) => {
        if (!alive) return;
        setPowersOfAttorney(items ?? []);
        setPowersOfAttorneyLoaded(true);
      })
      .catch(() => {
        if (!alive) return;
        setPowersOfAttorneyLoaded(true);
      });

    return () => {
      alive = false;
    };
  }, [activeProfileGroup, activeSection, clientId, powersOfAttorneyLoaded]);

  const [monthFrom, setMonthFrom] = useState<string>("");
  const [monthTo, setMonthTo] = useState<string>("");
  const defaultPaymentViewMode: PaymentViewMode = isSupplierCard
    ? "supplier"
    : "default";
  const defaultInvoiceViewMode: InvoiceViewMode = isSupplierCard
    ? "supplier"
    : "income";
  const [paymentViewMode, setPaymentViewMode] =
    useState<PaymentViewMode>("default");
  const [paymentViewModeTouched, setPaymentViewModeTouched] = useState(false);
  const [statusFilter, setStatusFilter] = useState<"all" | NormalizedStatus>(
    "all",
  );
  const [paymentTypeFilter, setPaymentTypeFilter] = useState<
    "all" | Payment["type"]
  >("all");
  const [paymentSearch, setPaymentSearch] = useState("");
  const debouncedPaymentSearch = useDebouncedValue(paymentSearch.trim(), 400);
  const [paymentPage, setPaymentPage] = useState(1);
  const [expandedSystemNotePaymentIds, setExpandedSystemNotePaymentIds] =
    useState<Set<number>>(() => new Set());
  const [paymentTableSettingsOpen, setPaymentTableSettingsOpen] =
    useState(false);
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
    getTableWidth: getPaymentTableWidth,
    resetTableSettings: resetPaymentTableSettings,
  } = usePersistedTableSettings<PaymentTableColumnKey>({
    storageKey: `${CLIENT_DETAIL_TABLE_STORAGE_PREFIX}.payments.${user?.id ?? "guest"}.v1`,
    settingsKey: "client_detail_payments_table",
    enabled: paymentPermissions.canView && activeSection === "payments",
    columns: PAYMENT_TABLE_COLUMNS.map(({ key, defaultWidth }) => ({
      key,
      defaultWidth,
    })),
    defaultPageSize: DETAIL_PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 120,
  });

  const togglePaymentSystemNote = useCallback((paymentId: number) => {
    setExpandedSystemNotePaymentIds((current) => {
      const next = new Set(current);
      if (next.has(paymentId)) {
        next.delete(paymentId);
      } else {
        next.add(paymentId);
      }
      return next;
    });
  }, []);

  const [actPeriod, setActPeriod] = useState<PeriodFilter>(() => savedViewState?.actPeriod ?? {
    mode: "preset",
    preset: "year_to_date",
  });
  const [actStatusFilter, setActStatusFilter] = useState<"all" | ActStatus>(
    () => savedViewState?.actStatusFilter ?? "all",
  );
  const [actSearch, setActSearch] = useState(() => savedViewState?.actSearch ?? "");
  const debouncedActSearch = useDebouncedValue(actSearch.trim(), 400);
  const [actSort, setActSort] = useState<{
    key: ActsSortKey;
    direction: "asc" | "desc";
  }>(() => savedViewState?.actSort ?? { key: "date", direction: "desc" });
  const [actPage, setActPage] = useState(() => savedViewState?.actPage ?? 1);
  const actPageResetReadyRef = useRef(false);
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
    getTableWidth: getActTableWidth,
    resetTableSettings: resetActTableSettings,
  } = usePersistedTableSettings<ActTableColumnKey>({
    storageKey: `${CLIENT_DETAIL_TABLE_STORAGE_PREFIX}.acts.${user?.id ?? "guest"}.v1`,
    settingsKey: "client_detail_acts_table",
    enabled: actPermissions.canView && activeSection === "acts",
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
  const [actModalMode, setActModalMode] = useState<"create" | "edit">("create");
  const [selectedAct, setSelectedAct] = useState<Act | null>(null);
  const [actSubmitting, setActSubmitting] = useState(false);
  const [actModalError, setActModalError] = useState<string | null>(null);
  const [actClients, setActClients] = useState<Client[]>([]);
  const [actResponsibles, setActResponsibles] = useState<ActResponsible[]>([]);
  const [actClientsLoaded, setActClientsLoaded] = useState(false);
  const [actResponsiblesLoaded, setActResponsiblesLoaded] = useState(false);
  const [actLookupsLoading, setActLookupsLoading] = useState(false);
  const [actLookupsError, setActLookupsError] = useState<string | null>(null);

  const today = useMemo(() => new Date(), []);

  const [invoicePeriod, setInvoicePeriod] = useState<PeriodFilter>(() => savedViewState?.invoicePeriod ?? {
    mode: "custom",
  });
  const [invoiceStatusFilter, setInvoiceStatusFilter] = useState<
    "all" | PaymentStatus
  >(() => savedViewState?.invoiceStatusFilter ?? "all");
  const [invoiceViewMode, setInvoiceViewMode] =
    useState<InvoiceViewMode>(() => savedViewState?.invoiceViewMode ?? "income");
  const [invoiceViewModeTouched, setInvoiceViewModeTouched] = useState(() => savedViewState?.invoiceViewModeTouched ?? false);
  const [invoiceSearch, setInvoiceSearch] = useState(() => savedViewState?.invoiceSearch ?? "");
  const debouncedInvoiceSearch = useDebouncedValue(invoiceSearch.trim(), 400);
  const [invoiceSort, setInvoiceSort] = useState<{
    key: InvoicesSortKey;
    direction: "asc" | "desc";
  }>(() => savedViewState?.invoiceSort ?? {
    key: "date",
    direction: "desc",
  });
  const [invoicePage, setInvoicePage] = useState(() => savedViewState?.invoicePage ?? 1);
  const invoicePageResetReadyRef = useRef(false);
  const [invoiceTableSettingsOpen, setInvoiceTableSettingsOpen] =
    useState(false);
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
    getTableWidth: getInvoiceTableWidth,
    resetTableSettings: resetInvoiceTableSettings,
  } = usePersistedTableSettings<InvoiceTableColumnKey>({
    storageKey: `${CLIENT_DETAIL_TABLE_STORAGE_PREFIX}.invoices.${user?.id ?? "guest"}.v2`,
    settingsKey: "client_detail_invoices_table",
    enabled: accountPermissions.canView && activeSection === "accounts",
    columns: INVOICE_TABLE_COLUMNS.map(({ key, defaultWidth }) => ({
      key,
      defaultWidth,
    })),
    defaultPageSize: DETAIL_PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 120,
  });
  const clientDefaultStateReadyRef = useRef(false);
  const paymentDefaultModeReadyRef = useRef(false);
  const invoiceDefaultModeReadyRef = useRef(false);

  useEffect(() => {
    if (!clientDefaultStateReadyRef.current) {
      clientDefaultStateReadyRef.current = true;
      return;
    }

    setPaymentViewMode(defaultPaymentViewMode);
    setPaymentViewModeTouched(false);
    setInvoiceViewMode(defaultInvoiceViewMode);
    setInvoiceViewModeTouched(false);
    setPaymentPage(1);
    setInvoicePage(1);
  }, [clientId, defaultInvoiceViewMode, defaultPaymentViewMode]);

  useEffect(() => {
    if (!paymentDefaultModeReadyRef.current) {
      paymentDefaultModeReadyRef.current = true;
      return;
    }

    if (!paymentViewModeTouched) {
      setPaymentViewMode(defaultPaymentViewMode);
      setPaymentPage(1);
    }
  }, [defaultPaymentViewMode, paymentViewModeTouched]);

  useEffect(() => {
    if (!invoiceDefaultModeReadyRef.current) {
      invoiceDefaultModeReadyRef.current = true;
      return;
    }

    if (!invoiceViewModeTouched) {
      setInvoiceViewMode(defaultInvoiceViewMode);
      setInvoicePage(1);
    }
  }, [defaultInvoiceViewMode, invoiceViewModeTouched]);

  const [contractPeriod, setContractPeriod] = useState<PeriodFilter>({
    mode: "preset",
    preset: "year_to_date",
  });
  const [contractSearch, setContractSearch] = useState("");
  const debouncedContractSearch = useDebouncedValue(contractSearch.trim(), 400);
  const [contractSort, setContractSort] = useState<{
    key: ContractsSortKey;
    direction: "asc" | "desc";
  }>({
    key: "date",
    direction: "desc",
  });
  const [contractPage, setContractPage] = useState(1);
  const [contractTableSettingsOpen, setContractTableSettingsOpen] =
    useState(false);
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
    getTableWidth: getContractTableWidth,
    resetTableSettings: resetContractTableSettings,
  } = usePersistedTableSettings<ContractTableColumnKey>({
    storageKey: `${CLIENT_DETAIL_TABLE_STORAGE_PREFIX}.contracts.${user?.id ?? "guest"}.v1`,
    settingsKey: "client_detail_contracts_table",
    enabled: contractPermissions.canView && activeSection === "contracts",
    columns: CONTRACT_TABLE_COLUMNS.map(({ key, defaultWidth }) => ({
      key,
      defaultWidth,
    })),
    defaultPageSize: DETAIL_PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 120,
  });

  const [invoiceModalOpen, setInvoiceModalOpen] = useState(false);
  const [invoiceModalMode, setInvoiceModalMode] = useState<"create" | "edit">(
    "create",
  );
  const [selectedInvoice, setSelectedInvoice] = useState<Invoice | null>(null);
  const [invoiceSubmitting, setInvoiceSubmitting] = useState(false);
  const [invoiceModalError, setInvoiceModalError] = useState<string | null>(
    null,
  );
  const [invoiceClients, setInvoiceClients] = useState<Client[]>([]);
  const [invoiceLookupsLoading, setInvoiceLookupsLoading] = useState(false);
  const [invoiceLookupsError, setInvoiceLookupsError] = useState<string | null>(
    null,
  );
  const [invoiceModalRelatedPayments, setInvoiceModalRelatedPayments] =
    useState<Payment[]>([]);
  const [invoiceModalPaymentsLoading, setInvoiceModalPaymentsLoading] =
    useState(false);

  const [contractModalOpen, setContractModalOpen] = useState(false);
  const [contractModalMode, setContractModalMode] = useState<"create" | "edit">(
    "create",
  );
  const [selectedContract, setSelectedContract] = useState<Contract | null>(
    null,
  );
  const [contractSubmitting, setContractSubmitting] = useState(false);
  const [contractModalError, setContractModalError] = useState<string | null>(
    null,
  );
  const [contractActionError, setContractActionError] = useState<string | null>(
    null,
  );
  const [relatedContractFilter, setRelatedContractFilter] =
    useState(RELATED_FILTER_ALL);
  const [relatedInvoiceFilter, setRelatedInvoiceFilter] =
    useState(RELATED_FILTER_ALL);
  const [relatedActFilter, setRelatedActFilter] = useState(RELATED_FILTER_ALL);
  const [relatedPaymentFilter, setRelatedPaymentFilter] =
    useState(RELATED_FILTER_ALL);
  const [relatedDocumentLookupLoading, setRelatedDocumentLookupLoading] =
    useState(false);
  const [relatedDocumentLookupError, setRelatedDocumentLookupError] =
    useState<string | null>(null);
  const [relatedDocumentContracts, setRelatedDocumentContracts] = useState<
    Contract[]
  >([]);
  const [relatedDocumentInvoices, setRelatedDocumentInvoices] = useState<
    Invoice[]
  >([]);
  const [relatedDocumentActs, setRelatedDocumentActs] = useState<Act[]>([]);
  const [relatedDocumentPayments, setRelatedDocumentPayments] = useState<
    Payment[]
  >([]);

  const resolvedActPeriod = useMemo(
    () => resolvePeriod(actPeriod),
    [actPeriod],
  );
  const resolvedInvoicePeriod = useMemo(
    () => resolvePeriod(invoicePeriod),
    [invoicePeriod],
  );
  const resolvedContractPeriod = useMemo(
    () => resolvePeriod(contractPeriod),
    [contractPeriod],
  );
  const actFrom = resolvedActPeriod.from ?? "";
  const actTo = resolvedActPeriod.to ?? "";
  const contractFrom = resolvedContractPeriod.from ?? "";
  const contractTo = resolvedContractPeriod.to ?? "";
  const relatedContractId = parseRelatedFilterId(relatedContractFilter);
  const relatedInvoiceId = parseRelatedFilterId(relatedInvoiceFilter);
  const relatedActId = parseRelatedFilterId(relatedActFilter);
  const relatedPaymentId = parseRelatedFilterId(relatedPaymentFilter);

  useEffect(() => {
    setRelatedContractFilter(RELATED_FILTER_ALL);
    setRelatedInvoiceFilter(RELATED_FILTER_ALL);
    setRelatedActFilter(RELATED_FILTER_ALL);
    setRelatedPaymentFilter(RELATED_FILTER_ALL);
    setRelatedDocumentContracts([]);
    setRelatedDocumentInvoices([]);
    setRelatedDocumentActs([]);
    setRelatedDocumentPayments([]);
  }, [clientId]);

  useEffect(() => {
    if (
      activeSection !== "payments" &&
      activeSection !== "accounts" &&
      activeSection !== "acts" &&
      activeSection !== "contracts"
    ) {
      return;
    }

    let alive = true;
    setRelatedDocumentLookupLoading(true);
    setRelatedDocumentLookupError(null);

    const contractsRequest = contractPermissions.canView
      ? apiService.getContracts({ clientId, page: 1, pageSize: 200 })
      : Promise.resolve({ items: [] as Contract[] });
    const invoicesRequest = accountPermissions.canView
      ? apiService.getInvoices({ clientId, page: 1, pageSize: 200 })
      : Promise.resolve({ items: [] as Invoice[] });
    const actsRequest = actPermissions.canView
      ? apiService.getActs({ clientId, page: 1, pageSize: 200 })
      : Promise.resolve({ items: [] as Act[] });
    const paymentsRequest = paymentPermissions.canView
      ? apiService.getPaymentsV2({
          clientId,
          page: 1,
          pageSize: 500,
          sortBy: "date",
          sortDir: "desc",
        })
      : Promise.resolve({ items: [] as Payment[] });

    Promise.allSettled([
      contractsRequest,
      invoicesRequest,
      actsRequest,
      paymentsRequest,
    ])
      .then(([contractsResult, invoicesResult, actsResult, paymentsResult]) => {
        if (!alive) return;

        if (contractsResult.status === "fulfilled") {
          setRelatedDocumentContracts(contractsResult.value.items ?? []);
        }
        if (invoicesResult.status === "fulfilled") {
          setRelatedDocumentInvoices(invoicesResult.value.items ?? []);
        }
        if (actsResult.status === "fulfilled") {
          setRelatedDocumentActs(actsResult.value.items ?? []);
        }
        if (paymentsResult.status === "fulfilled") {
          setRelatedDocumentPayments(paymentsResult.value.items ?? []);
        }

        const failed = [
          contractsResult,
          invoicesResult,
          actsResult,
          paymentsResult,
        ].some((result) => result.status === "rejected");
        setRelatedDocumentLookupError(
          failed ? "Не все связанные документы удалось загрузить." : null,
        );
      })
      .catch((error) => {
        if (!alive) return;
        setRelatedDocumentLookupError(
          extractDisplayErrorMessage(
            error,
            "Не удалось загрузить связанные документы.",
          ),
        );
      })
      .finally(() => {
        if (alive) setRelatedDocumentLookupLoading(false);
      });

    return () => {
      alive = false;
    };
  }, [
    activeSection,
    accountPermissions.canView,
    actPermissions.canView,
    clientId,
    contractPermissions.canView,
    paymentPermissions.canView,
  ]);

  const handleActFromChange = useCallback((value: string) => {
    setActPeriod((prev) => {
      const current = resolvePeriod(prev);
      return {
        mode: "custom",
        from: value || undefined,
        to: current.to,
      };
    });
  }, []);

  const handleActToChange = useCallback((value: string) => {
    setActPeriod((prev) => {
      const current = resolvePeriod(prev);
      return {
        mode: "custom",
        from: current.from,
        to: value || undefined,
      };
    });
  }, []);

  const handleContractFromChange = useCallback((value: string) => {
    setContractPeriod((prev) => {
      const current = resolvePeriod(prev);
      return {
        mode: "custom",
        from: value || undefined,
        to: current.to,
      };
    });
  }, []);

  const handleContractToChange = useCallback((value: string) => {
    setContractPeriod((prev) => {
      const current = resolvePeriod(prev);
      return {
        mode: "custom",
        from: current.from,
        to: value || undefined,
      };
    });
  }, []);

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
    enabled: actPermissions.canView && activeSection === "acts",
    from: resolvedActPeriod.from,
    to: resolvedActPeriod.to,
    status: actStatusFilter === "all" ? undefined : actStatusFilter,
    clientId,
    contractId: relatedContractId,
    invoiceId: relatedInvoiceId,
    paymentId: relatedPaymentId,
    search: debouncedActSearch || undefined,
    sortBy: actSort.key,
    sortDir: actSort.direction,
    page: actPage,
    pageSize: actPageSize,
  });

  useEffect(() => {
    if (!actPageResetReadyRef.current) {
      actPageResetReadyRef.current = true;
      return;
    }

    setActPage(1);
  }, [
    resolvedActPeriod.from,
    resolvedActPeriod.to,
    actStatusFilter,
    relatedContractId,
    relatedInvoiceId,
    relatedPaymentId,
    debouncedActSearch,
    clientId,
    actPageSize,
  ]);

  const invoiceViewConfig = useMemo(() => {
    const type = invoiceViewMode === "income" ? "Income" : "Expense";
    const clientRole =
      invoiceViewMode === "expenseClient" ? "expenseClient" : "counterparty";
    const selected =
      INVOICE_VIEW_OPTIONS.find((option) => option.key === invoiceViewMode) ??
      INVOICE_VIEW_OPTIONS[0];

    return { ...selected, type, clientRole } as const;
  }, [invoiceViewMode]);

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
    enabled: accountPermissions.canView && activeSection === "accounts",
    from: resolvedInvoicePeriod.from,
    to: resolvedInvoicePeriod.to,
    status: invoiceStatusFilter === "all" ? undefined : invoiceStatusFilter,
    clientId,
    contractId: relatedContractId,
    actId: relatedActId,
    paymentId: relatedPaymentId,
    clientRole: invoiceViewConfig.clientRole,
    search: debouncedInvoiceSearch || undefined,
    type: invoiceViewConfig.type,
    sortBy: invoiceSort.key,
    sortDir: invoiceSort.direction,
    page: invoicePage,
    pageSize: invoicePageSize,
  });

  useEffect(() => {
    if (!invoicePageResetReadyRef.current) {
      invoicePageResetReadyRef.current = true;
      return;
    }

    setInvoicePage(1);
  }, [
    clientId,
    invoiceViewMode,
    invoicePageSize,
    resolvedInvoicePeriod.from,
    resolvedInvoicePeriod.to,
    invoiceStatusFilter,
    relatedContractId,
    relatedActId,
    relatedPaymentId,
    debouncedInvoiceSearch,
  ]);

  const invoiceTotalPages = useMemo(() => {
    if (!invoicesPagination.pageSize) return 1;
    return Math.max(
      1,
      Math.ceil((invoicesPagination.total ?? 0) / invoicesPagination.pageSize),
    );
  }, [invoicesPagination.pageSize, invoicesPagination.total]);

  const invoiceSummaryBuckets = invoicesSummary ?? {
    total: { amount: 0, count: 0 },
    pending: { amount: 0, count: 0 },
    paid: { amount: 0, count: 0 },
    overdue: { amount: 0, count: 0 },
  };

  const invoiceStatusLabels = useMemo<Record<PaymentStatus, string>>(
    () => ({
      Pending: t("invoicePendingBadge") ?? t("pending") ?? "Ожидается",
      Completed: t("invoicePaidBadge") ?? t("completedStatus") ?? "Оплачено",
      Overdue: t("invoiceOverdueBadge") ?? t("overdue") ?? "Просрочено",
      Processing: t("processingStatus") ?? "Частично оплачено",
      Cancelled: t("cancelledStatus") ?? "Отменено",
    }),
    [t],
  );

  const invoiceWorkflowStatusLabels = useMemo<Record<InvoiceWorkflowStatus, string>>(
    () => ({
      Draft: "Черновик",
      NotSigned: "Не подписан",
      RequiresClientSignature: "Требуется подпись клиента",
      OnApproval: "На согласовании",
      Approved: "Согласован",
      ApprovalRejected: "Отказ в согласовании",
      NeedsCorrection: "Требует исправления",
      OnSigning: "На подписании",
      SignedByClient: "Подписан клиентом",
      Paid: "Оплачен",
      PartiallyPaid: "Частично оплачен",
      Overdue: "Просрочен",
      Cancelled: "Аннулирован",
    }),
    [],
  );

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
    enabled: contractPermissions.canView && activeSection === "contracts",
    from: resolvedContractPeriod.from,
    to: resolvedContractPeriod.to,
    search: debouncedContractSearch || undefined,
    clientId,
    invoiceId: relatedInvoiceId,
    actId: relatedActId,
    paymentId: relatedPaymentId,
    sortBy: contractSort.key,
    sortDir: contractSort.direction,
    page: contractPage,
    pageSize: contractPageSize,
  });

  useEffect(() => {
    setContractPage(1);
  }, [
    resolvedContractPeriod.from,
    resolvedContractPeriod.to,
    debouncedContractSearch,
    clientId,
    relatedInvoiceId,
    relatedActId,
    relatedPaymentId,
    contractPageSize,
  ]);

  useEffect(() => {
    writeSessionViewState<ClientDetailViewState>(clientViewStateKey, {
      activeSection,
      invoicePeriod,
      invoiceStatusFilter,
      invoiceViewMode,
      invoiceViewModeTouched,
      invoiceSearch,
      invoiceSort,
      invoicePage,
      actPeriod,
      actStatusFilter,
      actSearch,
      actSort,
      actPage,
    });
  }, [
    activeSection,
    actPage,
    actPeriod,
    actSearch,
    actSort,
    actStatusFilter,
    clientViewStateKey,
    invoicePage,
    invoicePeriod,
    invoiceSearch,
    invoiceSort,
    invoiceStatusFilter,
    invoiceViewMode,
    invoiceViewModeTouched,
  ]);

  const contractTotalPages = useMemo(() => {
    if (!contractsPagination.pageSize) return 1;
    return Math.max(
      1,
      Math.ceil(
        (contractsPagination.total ?? 0) / contractsPagination.pageSize,
      ),
    );
  }, [contractsPagination.pageSize, contractsPagination.total]);

  const invoiceStatusClasses: Record<PaymentStatus, string> = {
    Pending: "bg-amber-100 text-amber-700 border border-amber-200",
    Completed: "bg-emerald-100 text-emerald-700 border border-emerald-200",
    Overdue: "bg-rose-100 text-rose-700 border border-rose-200",
    Processing: "bg-blue-100 text-blue-700 border border-blue-200",
    Cancelled: "bg-slate-100 text-slate-600 border border-slate-200",
  };

  const invoiceWorkflowStatusClasses: Record<InvoiceWorkflowStatus, string> = {
    Draft: "bg-slate-100 text-slate-700 border border-slate-200",
    NotSigned: "bg-slate-100 text-slate-700 border border-slate-200",
    RequiresClientSignature: "bg-amber-100 text-amber-800 border border-amber-200",
    OnApproval: "bg-cyan-100 text-cyan-700 border border-cyan-200",
    Approved: "bg-emerald-100 text-emerald-700 border border-emerald-200",
    ApprovalRejected: "bg-rose-100 text-rose-700 border border-rose-200",
    NeedsCorrection: "bg-orange-100 text-orange-700 border border-orange-200",
    OnSigning: "bg-violet-100 text-violet-700 border border-violet-200",
    SignedByClient: "bg-emerald-100 text-emerald-700 border border-emerald-200",
    Paid: "bg-emerald-100 text-emerald-700 border border-emerald-200",
    PartiallyPaid: "bg-blue-100 text-blue-700 border border-blue-200",
    Overdue: "bg-rose-100 text-rose-700 border border-rose-200",
    Cancelled: "bg-rose-100 text-rose-700 border border-rose-200",
  };

  const openInvoiceCreate = () => {
    if (!accountPermissions.canCreate) {
      showPermissionWarning("Недостаточно прав для добавления счетов.");
      return;
    }
    setInvoiceModalMode("create");
    setSelectedInvoice(null);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const openInvoiceEdit = (invoice: Invoice) => {
    if (!accountPermissions.canEdit) {
      showPermissionWarning("Недостаточно прав для редактирования счетов.");
      return;
    }
    setInvoiceModalMode("edit");
    setSelectedInvoice(invoice);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const handleInvoicePageLinkClick = useCallback(
    (event: MouseEvent<HTMLAnchorElement>, invoiceId: number) => {
      if (!onOpenInvoicePage) {
        return;
      }

      if (
        event.button !== 0 ||
        event.defaultPrevented ||
        event.metaKey ||
        event.ctrlKey ||
        event.shiftKey ||
        event.altKey
      ) {
        return;
      }

      event.preventDefault();
      onOpenInvoicePage(invoiceId);
    },
    [onOpenInvoicePage],
  );

  const closeInvoiceModal = () => {
    setInvoiceModalOpen(false);
    setSelectedInvoice(null);
    setInvoiceModalError(null);
    setInvoiceModalRelatedPayments([]);
    setInvoiceModalPaymentsLoading(false);
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
        err instanceof Error
          ? err.message
          : (t("clientsLoadError") ?? "Не удалось загрузить клиентов"),
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
    const isEdit = invoiceModalMode === "edit" && selectedInvoice;
    if (isEdit && !accountPermissions.canEdit) {
      showPermissionWarning("Недостаточно прав для редактирования счетов.");
      return;
    }
    if (!isEdit && !accountPermissions.canCreate) {
      showPermissionWarning("Недостаточно прав для добавления счетов.");
      return;
    }
    setInvoiceSubmitting(true);
    setInvoiceModalError(null);
    try {
      if (isEdit && selectedInvoice) {
        await updateClientInvoice(selectedInvoice.id, payload);
        toast.success("Изменения по счету сохранены.", "Счет обновлен");
      } else {
        await createClientInvoice(payload);
        toast.success("Счет успешно создан.", "Счет сохранен");
      }
      await refreshPayments();
      bumpStats();
      closeInvoiceModal();
    } catch (err) {
      setInvoiceModalError(
        extractDisplayErrorMessage(err, "Не удалось сохранить счет"),
      );
    } finally {
      setInvoiceSubmitting(false);
    }
  };

  const removeInvoice = async (invoice: Invoice) => {
    if (!accountPermissions.canDelete) {
      showPermissionWarning("Недостаточно прав для удаления счетов.");
      return;
    }
    if (
      !(await confirm({
        title: "Подтверждение удаления",
        description: t("invoiceDeleteConfirm") ?? "Удалить счёт?",
        confirmLabel: "Удалить",
        cancelLabel: "Отмена",
        variant: "danger",
      }))
    )
      return;
    try {
      await deleteClientInvoice(invoice.id);
      await refreshPayments();
      bumpStats();
    } catch (err) {
      toast.error(
        extractDisplayErrorMessage(err, "Не удалось удалить счёт"),
        "Ошибка удаления",
      );
    }
  };

  const resetRelatedDocumentFilters = () => {
    setRelatedContractFilter(RELATED_FILTER_ALL);
    setRelatedInvoiceFilter(RELATED_FILTER_ALL);
    setRelatedActFilter(RELATED_FILTER_ALL);
    setRelatedPaymentFilter(RELATED_FILTER_ALL);
  };

  const resetPaymentFilters = () => {
    setMonthFrom("");
    setMonthTo("");
    setStatusFilter("all");
    setPaymentTypeFilter("all");
    setPaymentViewMode(defaultPaymentViewMode);
    setPaymentViewModeTouched(false);
    setSelectedCaseId("all");
    resetRelatedDocumentFilters();
    setPaymentSearch("");
    setPaymentPage(1);
  };

  const resetInvoiceFilters = () => {
    setInvoicePeriod({ mode: "custom" });
    setInvoiceStatusFilter("all");
    setInvoiceViewMode(defaultInvoiceViewMode);
    setInvoiceViewModeTouched(false);
    resetRelatedDocumentFilters();
    setInvoiceSearch("");
    setInvoicePage(1);
  };

  const resetContractFilters = () => {
    setContractPeriod({ mode: "preset", preset: "year_to_date" });
    resetRelatedDocumentFilters();
    setContractSearch("");
    setContractPage(1);
  };

  const resetCaseFilters = () => {
    setCaseSearch("");
    setCaseStatusFilter("all");
  };

  const handleInvoiceSort = (key: InvoicesSortKey) => {
    setInvoiceSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === "asc" ? "desc" : "asc" };
      }
      return {
        key,
        direction: key === "date" || key === "createdAt" ? "desc" : "asc",
      };
    });
    setInvoicePage(1);
  };

  const handleContractSort = (key: ContractsSortKey) => {
    setContractSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === "asc" ? "desc" : "asc" };
      }
      return {
        key,
        direction: key === "date" || key === "createdAt" ? "desc" : "asc",
      };
    });
    setContractPage(1);
  };

  const handleUpdateClient = async (payload: ClientInput) => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning("Недостаточно прав для редактирования клиента.");
      throw new Error("Недостаточно прав для редактирования клиента.");
    }
    try {
      const updated = await apiService.updateClient(clientId, payload);
      setClientData(updated);
      setClientName(updated.name ?? `Client #${clientId}`);
      onClientUpdated?.(updated);
      return updated;
    } catch (err) {
      const message = formatApiValidationError(
        err,
        "Не удалось обновить клиента",
      );
      toast.error(message, "Ошибка сохранения");
      throw err instanceof Error ? err : new Error(message);
    }
  };

  const handleDeleteClientCard = async () => {
    if (!clientPermissions.canDelete) {
      showPermissionWarning("Недостаточно прав для удаления клиента.");
      return;
    }

    const ok = await confirm({
      title: "Подтверждение удаления",
      description: "Удалить клиента? Действие необратимо.",
      confirmLabel: "Удалить",
      cancelLabel: "Отмена",
      variant: "danger",
    });

    if (!ok) return;

    try {
      await apiService.deleteClient(clientId);
      onBack();
    } catch (err) {
      const raw =
        err instanceof Error ? err.message : "Не удалось удалить клиента";
      const marker = " - ";
      const markerIndex = raw.indexOf(marker);
      const payload =
        markerIndex >= 0 ? raw.slice(markerIndex + marker.length).trim() : raw;
      try {
        const parsed = JSON.parse(payload) as {
          message?: string;
          usage?: string[];
        };
        const message = parsed.message?.trim() || "Не удалось удалить клиента";
        const usage =
          Array.isArray(parsed.usage) && parsed.usage.length > 0
            ? ` Используется в: ${parsed.usage.join(", ")}.`
            : "";
        toast.error(`${message}${usage}`.trim(), "Удаление невозможно");
      } catch {
        toast.error(raw, "Удаление невозможно");
      }
    }
  };

  const openContractCreate = () => {
    if (!contractPermissions.canCreate) {
      showPermissionWarning("Недостаточно прав для добавления договоров.");
      return;
    }
    setSelectedContract(null);
    setContractModalMode("create");
    setContractModalError(null);
    setContractActionError(null);
    setContractModalOpen(true);
  };

  const openContractEdit = (contract: Contract) => {
    if (!contractPermissions.canEdit) {
      showPermissionWarning("Недостаточно прав для редактирования договоров.");
      return;
    }
    setSelectedContract(contract);
    setContractModalMode("edit");
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
    const isEdit = contractModalMode === "edit" && selectedContract;
    if (isEdit && !contractPermissions.canEdit) {
      showPermissionWarning("Недостаточно прав для редактирования договоров.");
      return;
    }
    if (!isEdit && !contractPermissions.canCreate) {
      showPermissionWarning("Недостаточно прав для добавления договоров.");
      return;
    }
    const normalizedClients = payload.clients.some(
      (entry) => entry.clientId === clientId,
    )
      ? payload.clients
      : [...payload.clients, { clientId }];

    const normalizedPayload: ContractInput = {
      ...payload,
      clients: normalizedClients,
      actIds:
        isEdit && selectedContract
          ? (selectedContract.actIds ?? [])
          : (payload.actIds ?? []),
      invoiceIds:
        isEdit && selectedContract
          ? (selectedContract.invoiceIds ?? [])
          : (payload.invoiceIds ?? []),
      paymentIds:
        isEdit && selectedContract
          ? (selectedContract.paymentIds ?? [])
          : (payload.paymentIds ?? []),
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
      setContractModalError(
        err instanceof Error ? err.message : "Не удалось сохранить договор",
      );
    } finally {
      setContractSubmitting(false);
    }
  };

  const removeContract = async (contract: Contract) => {
    if (!contractPermissions.canDelete) {
      showPermissionWarning("Недостаточно прав для удаления договоров.");
      return;
    }
    const template =
      t("contractDeleteConfirm") ?? "Удалить договор «{{number}}»?";
    const confirmMessage = template.replace(
      "{{number}}",
      contract.number || contract.title || String(contract.id),
    );
    if (
      !(await confirm({
        title: "Подтверждение удаления",
        description: confirmMessage,
        confirmLabel: "Удалить",
        cancelLabel: "Отмена",
        variant: "danger",
      }))
    )
      return;
    setContractActionError(null);
    try {
      await deleteContract(contract.id);
    } catch (err) {
      const message =
        err instanceof Error ? err.message : "Не удалось удалить договор";
      setContractActionError(message);
    }
  };

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<"create" | "edit">("create");

  const visibleSections = useMemo(
    () =>
      SECTION_ORDER.filter(
        (section) => canViewSection(section),
      ),
    [canViewSection],
  );

  useEffect(() => {
    if (!canViewSection(activeSection)) {
      const fallback = SECTION_ORDER.find(
        (section) => canViewSection(section),
      );
      if (fallback) setActiveSection(fallback);
    }
  }, [activeSection, canViewSection]);

  const { isRequired } = useFormRequirements(
    "clients",
    activeSection === "profile",
  );

  const [statsReloadToken, setStatsReloadToken] = useState(0);
  const bumpStats = () => setStatsReloadToken((x) => x + 1);
  useEffect(() => {
    if (!canViewClientCaseFinancialSummary) {
      setCaseFinanceSummaries({});
      setCaseFinanceLoading(false);
      setCaseFinanceError(null);
      return;
    }

    let alive = true;
    setCaseFinanceLoading(true);
    setCaseFinanceError(null);

    apiService
      .getClientCaseFinanceSummaries(
        clientId,
        isSupplierCard ? { clientRole: "counterparty" } : undefined,
      )
      .then((items) => {
        if (!alive) return;
        setCaseFinanceSummaries(
          Object.fromEntries((items ?? []).map((item) => [item.caseId, item])),
        );
      })
      .catch((error) => {
        if (!alive) return;
        setCaseFinanceError(
          extractDisplayErrorMessage(
            error,
            "Не удалось загрузить экономику дел",
          ),
        );
      })
      .finally(() => {
        if (!alive) return;
        setCaseFinanceLoading(false);
      });

    return () => {
      alive = false;
    };
  }, [
    canViewClientCaseFinancialSummary,
    clientId,
    isSupplierCard,
    statsReloadToken,
  ]);

  const showPermissionWarning = useCallback(
    (message: string) => {
      toast.error(
        message || "Недостаточно прав для выполнения действия.",
        "Недостаточно прав",
      );
    },
    [toast],
  );

  const confirmProfileDeleteAction = useCallback(
    async ({
      title,
      description,
      successMessage,
      toastTitle,
      onDelete,
    }: ClientProfileDeleteActionOptions) => {
      if (!clientPermissions.canEdit) {
        showPermissionWarning("Недостаточно прав для редактирования клиента.");
        return false;
      }

      const ok = await confirm({
        title,
        description,
        confirmLabel: "Удалить",
        cancelLabel: "Отмена",
        variant: "danger",
      });

      if (!ok) return false;

      onDelete();
      toast.success(successMessage, toastTitle);
      return true;
    },
    [clientPermissions.canEdit, confirm, showPermissionWarning, toast],
  );

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
    const nextClientFormState = {
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
      education:
        profile.education.length > 0
          ? profile.education
          : [createEmptyEducation()],
      professions:
        profile.professions.length > 0
          ? profile.professions
          : [createEmptyProfession()],
      workplaces:
        profile.workplaces.length > 0
          ? profile.workplaces
          : [createEmptyWorkplace()],
      maritalStatus: profile.maritalStatus,
      relatives:
        profile.relatives.length > 0
          ? profile.relatives
          : [createEmptyRelative()],
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
      actualAddressMatchesRegistration:
        profile.actualAddressMatchesRegistration,
      phones: profile.phones.length > 0 ? profile.phones : [""],
      emails: profile.emails.length > 0 ? profile.emails : [""],
      addresses: profile.addresses.length > 0 ? profile.addresses : [""],
      passports: profile.passports.length > 0 ? profile.passports : [""],
      birthCertificates:
        profile.birthCertificates.length > 0 ? profile.birthCertificates : [""],
      militaryIds: profile.militaryIds.length > 0 ? profile.militaryIds : [""],
      birthCertificateEntries:
        profile.birthCertificateEntries.length > 0
          ? profile.birthCertificateEntries
          : [createEmptyBirthCertificateEntry()],
      militaryIdEntries:
        profile.militaryIdEntries.length > 0
          ? profile.militaryIdEntries
          : [createEmptyMilitaryIdEntry()],
      passportEntries:
        profile.passportEntries.length > 0
          ? profile.passportEntries
          : [createEmptyPassportEntry()],
      addressEntries:
        profile.addressEntries.length > 0
          ? ensureSinglePrimaryAddressEntryList(profile.addressEntries)
          : [createEmptyAddressEntry(true)],
      comment: profile.comment,
      legacyNotes: profile.legacyNotes,
      legalEntityId: clientData.legalEntityId
        ? String(clientData.legalEntityId)
        : "",
      responsibleUserId: clientData.responsibleUserId
        ? String(clientData.responsibleUserId)
        : "",
      isActive: clientData.isActive ?? true,
      clientStatusId: clientData.clientStatusId
        ? String(clientData.clientStatusId)
        : "",
      isClientAndSupplier: Boolean(clientData.isClientAndSupplier),
    };
    clientFormBaselineRef.current = nextClientFormState;
    setClientFormState(nextClientFormState);
    setClientProfileEditing(false);
    // setProfileDocuments({
    //   passport: profile.passports[0] ?? '',
    //   birthCertificate: profile.birthCertificates[0] ?? '',
    //   militaryId: profile.militaryIds[0] ?? '',
    // });
  }, [clientData]);

  useEffect(() => {
    if (clientData?.legalEntity) {
      mergeLegalEntityOptions([clientData.legalEntity]);
    }
  }, [clientData?.legalEntity, mergeLegalEntityOptions]);

  useEffect(() => {
    if (activeSection !== "profile" || clientFormState.kind !== "legal") {
      return;
    }

    let alive = true;
    setLegalEntityLookupLoading(true);

    apiService
      .getLegalEntitiesPaged({
        search: debouncedLegalEntityLookupSearch || undefined,
        page: 1,
        pageSize: LEGAL_ENTITY_LOOKUP_PAGE_SIZE,
      })
      .then((response) => {
        if (!alive) return;
        setLegalEntityOptions((prev) => {
          const selectedId = clientFormState.legalEntityId
            ? Number(clientFormState.legalEntityId)
            : null;
          const selected = selectedId
            ? prev.find((item) => item.id === selectedId)
            : null;
          const byId = new Map<number, LegalEntitySummary>();
          if (selected) byId.set(selected.id, selected);
          (response.items ?? []).forEach((item) => byId.set(item.id, item));
          return [...byId.values()].sort((a, b) =>
            a.shortName.localeCompare(b.shortName, undefined, {
              sensitivity: "base",
            }),
          );
        });
        setLegalEntityLookupTotal(response.total ?? 0);
      })
      .catch(() => {
        if (!alive) return;
        setLegalEntityLookupTotal(0);
      })
      .finally(() => {
        if (!alive) return;
        setLegalEntityLookupLoading(false);
      });

    return () => {
      alive = false;
    };
  }, [
    activeSection,
    clientFormState.kind,
    clientFormState.legalEntityId,
    debouncedLegalEntityLookupSearch,
  ]);

  const caseIdForQuery =
    selectedCaseId === "all" ? undefined : Number(selectedCaseId);
  const filteredCases = useMemo(() => {
    const query = caseSearch.trim().toLowerCase();
    return cases.filter((caseItem) => {
      const matchesSearch =
        query.length === 0 ||
        caseItem.title?.toLowerCase().includes(query) ||
        caseItem.description?.toLowerCase().includes(query) ||
        caseItem.caseNumber?.toLowerCase().includes(query) ||
        caseItem.caseType?.toLowerCase().includes(query) ||
        caseItem.stage?.toLowerCase().includes(query) ||
        caseItem.courtName?.toLowerCase().includes(query) ||
        caseItem.courtCaseNumber?.toLowerCase().includes(query);
      const normalizedStatus = normalizeCaseStatusFilter(caseItem.status);
      const matchesStatus =
        caseStatusFilter === "all" || normalizedStatus === caseStatusFilter;
      return matchesSearch && matchesStatus;
    });
  }, [caseSearch, caseStatusFilter, cases]);
  const supplierCaseSummaries = useMemo(
    () =>
      Object.values(caseFinanceSummaries)
        .filter((item) => item.caseId > 0 && (item.expenseCount > 0 || item.expenseTotal > 0))
        .sort((left, right) => {
          const leftDate = left.caseCreatedAt ? new Date(left.caseCreatedAt).getTime() : 0;
          const rightDate = right.caseCreatedAt ? new Date(right.caseCreatedAt).getTime() : 0;
          return rightDate - leftDate || right.caseId - left.caseId;
        }),
    [caseFinanceSummaries],
  );
  const clientDebtAmount = useMemo(
    () =>
      normalizeMoneyAmount(
        Object.values(caseFinanceSummaries).reduce(
          (sum, item) =>
            sum +
            (isSupplierCard
              ? normalizeMoneyAmount(item.expenseOutstanding)
              : normalizeMoneyAmount(item.incomeOutstanding)),
          0,
        ),
      ),
    [caseFinanceSummaries, isSupplierCard],
  );
  const clientAdvanceAmount = useMemo(
    () => normalizeMoneyAmount(clientBalance?.balance),
    [clientBalance?.balance],
  );
  const clientDebtLabel = clientBalanceLoading || caseFinanceLoading
    ? "..."
    : formatCurrencySmart(clientDebtAmount).full;
  const clientAdvanceLabel = clientBalanceLoading
    ? "..."
    : formatCurrencySmart(clientAdvanceAmount).full;
  const filteredSupplierCaseSummaries = useMemo(() => {
    const query = caseSearch.trim().toLowerCase();
    return supplierCaseSummaries.filter((item) => {
      const matchesSearch =
        query.length === 0 ||
        item.caseTitle?.toLowerCase().includes(query) ||
        item.clientName?.toLowerCase().includes(query);
      const normalizedStatus = normalizeCaseStatusFilter(item.caseStatus ?? "");
      const matchesStatus =
        caseStatusFilter === "all" || normalizedStatus === caseStatusFilter;
      return matchesSearch && matchesStatus;
    });
  }, [caseSearch, caseStatusFilter, supplierCaseSummaries]);
  const selectedSupplierCaseForPayment = useMemo(
    () =>
      selectedCaseId === "all"
        ? null
        : supplierCaseSummaries.find((item) => item.caseId === selectedCaseId) ??
          null,
    [selectedCaseId, supplierCaseSummaries],
  );
  const supplierPaymentCasesPrefetch = useMemo<ClientCase[]>(() => {
    if (!selectedSupplierCaseForPayment?.clientId) {
      return cases;
    }

    return [
      {
        id: selectedSupplierCaseForPayment.caseId,
        clientId: selectedSupplierCaseForPayment.clientId,
        title:
          selectedSupplierCaseForPayment.caseTitle ||
          `Дело #${selectedSupplierCaseForPayment.caseId}`,
        description: "",
        status: selectedSupplierCaseForPayment.caseStatus || "Open",
        createdAt:
          selectedSupplierCaseForPayment.caseCreatedAt ||
          new Date().toISOString(),
      },
    ];
  }, [cases, selectedSupplierCaseForPayment]);
  const paymentModalDefaultExpenseClientId =
    paymentViewMode === "expenseClient"
      ? clientId
      : paymentViewMode === "supplier"
        ? selectedSupplierCaseForPayment?.clientId
        : undefined;
  const paymentModalCasesPrefetch =
    paymentViewMode === "supplier" ? supplierPaymentCasesPrefetch : cases;
  const caseCardsAvailable = isSupplierCard
    ? supplierCaseSummaries.length > 0
    : cases.length > 0;
  const caseCardsCount = isSupplierCard
    ? supplierCaseSummaries.length
    : cases.length;

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

  const paymentViewConfig = useMemo(() => {
    const selected =
      PAYMENT_VIEW_OPTIONS.find((option) => option.key === paymentViewMode) ??
      PAYMENT_VIEW_OPTIONS[0];
    const clientRole: ClientRole | undefined =
      paymentViewMode === "supplier"
        ? "counterparty"
        : paymentViewMode === "expenseClient"
          ? "expenseClient"
          : undefined;
    const type =
      paymentViewMode === "default"
        ? paymentTypeFilter === "all"
          ? undefined
          : paymentTypeFilter
        : "Expense";

    return { ...selected, clientRole, type } as const;
  }, [paymentTypeFilter, paymentViewMode]);

  const {
    payments,
    total: paymentsTotal,
    loading: loadingPayments,
    error: paymentsError,
    refresh: refreshPayments,
  } = usePaymentsPaged({
    enabled: paymentPermissions.canView && activeSection === "payments",
    from: fromDateStr,
    to: toDateStr,
    clientId,
    contractId: relatedContractId,
    invoiceId: relatedInvoiceId,
    actId: relatedActId,
    clientRole: paymentViewConfig.clientRole,
    caseId: caseIdForQuery,
    type: paymentViewConfig.type,
    status: toPaymentStatusFilter(statusFilter),
    search: debouncedPaymentSearch || undefined,
    sortBy: "date",
    sortDir: "asc",
    page: paymentPage,
    pageSize: paymentPageSize,
  });

  const visiblePayments = payments;
  const relatedContractOptions = useMemo(
    () =>
      uniqById([...relatedDocumentContracts, ...clientContracts]).sort((a, b) =>
        (a.number || "").localeCompare(b.number || "", "ru"),
      ),
    [clientContracts, relatedDocumentContracts],
  );
  const relatedInvoiceOptions = useMemo(
    () =>
      uniqById([...relatedDocumentInvoices, ...clientInvoices]).sort(
        (a, b) =>
          new Date(b.date).getTime() - new Date(a.date).getTime() ||
          b.id - a.id,
      ),
    [clientInvoices, relatedDocumentInvoices],
  );
  const relatedActOptions = useMemo(
    () =>
      uniqById([...relatedDocumentActs, ...clientActs]).sort(
        (a, b) =>
          new Date(b.date).getTime() - new Date(a.date).getTime() ||
          b.id - a.id,
      ),
    [clientActs, relatedDocumentActs],
  );
  const relatedPaymentOptions = useMemo(
    () =>
      uniqById([...relatedDocumentPayments, ...visiblePayments])
        .filter((payment) => payment.documentKind !== "Invoice")
        .sort(
          (a, b) =>
            new Date(b.date).getTime() - new Date(a.date).getTime() ||
            b.id - a.id,
        ),
    [relatedDocumentPayments, visiblePayments],
  );
  const relatedContractById = useMemo(
    () => new Map(relatedContractOptions.map((contract) => [contract.id, contract])),
    [relatedContractOptions],
  );
  const relatedInvoiceById = useMemo(
    () => new Map(relatedInvoiceOptions.map((invoice) => [invoice.id, invoice])),
    [relatedInvoiceOptions],
  );
  const relatedInvoiceByEntityId = useMemo(() => {
    const map = new Map<number, Invoice>();
    relatedInvoiceOptions.forEach((invoice) => {
      if (invoice.invoiceEntityId) {
        map.set(invoice.invoiceEntityId, invoice);
      }
    });
    return map;
  }, [relatedInvoiceOptions]);
  const relatedActById = useMemo(
    () => new Map(relatedActOptions.map((act) => [act.id, act])),
    [relatedActOptions],
  );
  const relatedPaymentById = useMemo(
    () => new Map(relatedPaymentOptions.map((payment) => [payment.id, payment])),
    [relatedPaymentOptions],
  );

  const formatContractFilterLabel = useCallback((contract: Contract) => {
    const title = contract.title?.trim();
    return `Договор №${contract.number}${title ? ` · ${title}` : ""}`;
  }, []);
  const formatInvoiceFilterLabel = useCallback(
    (invoice: Invoice) => `Счет №${invoice.number} от ${toRuDate(invoice.date)}`,
    [],
  );
  const formatActFilterLabel = useCallback(
    (act: Act) => `Акт №${act.number} от ${toRuDate(act.date)}`,
    [],
  );
  const formatPaymentFilterLabel = useCallback((payment: Payment) => {
    const description = payment.description?.trim();
    return `${toRuDate(payment.date)} · ${formatCurrencySmart(payment.amount).full}${description ? ` · ${description}` : ""}`;
  }, []);

  const selectedRelatedContract = relatedContractId
    ? relatedContractById.get(relatedContractId)
    : null;
  const selectedRelatedInvoice = relatedInvoiceId
    ? relatedInvoiceById.get(relatedInvoiceId)
    : null;
  const selectedRelatedAct = relatedActId
    ? relatedActById.get(relatedActId)
    : null;
  const selectedRelatedPayment = relatedPaymentId
    ? relatedPaymentById.get(relatedPaymentId)
    : null;

  const getInvoiceActs = useCallback(
    (invoice: Invoice) => {
      const explicitIds = new Set<number>([
        ...(invoice.actIds ?? []),
        ...((invoice.acts ?? []).map((act) => act.id)),
      ]);
      return relatedActOptions.filter(
        (act) =>
          explicitIds.has(act.id) ||
          act.invoiceId === invoice.id ||
          (!act.invoiceId && Boolean(act.invoiceNumber) && act.invoiceNumber === invoice.number),
      );
    },
    [relatedActOptions],
  );

  const getInvoicePayments = useCallback(
    (invoice: Invoice) =>
      relatedPaymentOptions.filter((payment) => {
        if (payment.id === invoice.id) return false;
        if (!payment.invoiceId) return false;
        return (
          payment.invoiceId === invoice.invoiceEntityId ||
          payment.invoiceId === invoice.id
        );
      }),
    [relatedPaymentOptions],
  );

  const getPaymentInvoice = useCallback(
    (payment: Payment) => {
      if (payment.documentKind === "Invoice") {
        return relatedInvoiceById.get(payment.id) ?? null;
      }
      if (!payment.invoiceId) return null;
      return (
        relatedInvoiceByEntityId.get(payment.invoiceId) ??
        relatedInvoiceById.get(payment.invoiceId) ??
        null
      );
    },
    [relatedInvoiceByEntityId, relatedInvoiceById],
  );

  const getActInvoice = useCallback(
    (act: Act) =>
      act.invoiceId
        ? relatedInvoiceById.get(act.invoiceId) ?? null
        : relatedInvoiceOptions.find((invoice) => invoice.number === act.invoiceNumber) ?? null,
    [relatedInvoiceById, relatedInvoiceOptions],
  );

  const getActPayments = useCallback(
    (act: Act) => {
      const invoice = getActInvoice(act);
      return invoice ? getInvoicePayments(invoice) : [];
    },
    [getActInvoice, getInvoicePayments],
  );

  const getContractInvoices = useCallback(
    (contract: Contract) => {
      const requestedIds = new Set(contract.invoiceIds ?? []);
      return uniqById([
        ...Array.from(requestedIds)
          .map((id) => relatedInvoiceById.get(id) ?? relatedInvoiceByEntityId.get(id))
          .filter((item): item is Invoice => Boolean(item)),
        ...relatedInvoiceOptions.filter((invoice) =>
          invoice.contractId === contract.id ||
          requestedIds.has(invoice.id) ||
          (invoice.invoiceEntityId ? requestedIds.has(invoice.invoiceEntityId) : false),
        ),
      ]);
    },
    [relatedInvoiceByEntityId, relatedInvoiceById, relatedInvoiceOptions],
  );

  const getContractActs = useCallback(
    (contract: Contract) => {
      const requestedIds = new Set(contract.actIds ?? []);
      const invoices = getContractInvoices(contract);
      return uniqById([
        ...Array.from(requestedIds)
          .map((id) => relatedActById.get(id))
          .filter((item): item is Act => Boolean(item)),
        ...relatedActOptions.filter((act) => act.contractId === contract.id || requestedIds.has(act.id)),
        ...invoices.flatMap(getInvoiceActs),
      ]);
    },
    [getContractInvoices, getInvoiceActs, relatedActById, relatedActOptions],
  );

  const getContractPayments = useCallback(
    (contract: Contract) => {
      const requestedIds = new Set(contract.paymentIds ?? []);
      const invoiceIds = new Set<number>();
      getContractInvoices(contract).forEach((invoice) => {
        invoiceIds.add(invoice.id);
        if (invoice.invoiceEntityId) invoiceIds.add(invoice.invoiceEntityId);
      });

      return uniqById([
        ...Array.from(requestedIds)
          .map((id) => relatedPaymentById.get(id))
          .filter((item): item is Payment => Boolean(item)),
        ...relatedPaymentOptions.filter((payment) =>
          payment.contractId === contract.id ||
          requestedIds.has(payment.id) ||
          (payment.invoiceId ? invoiceIds.has(payment.invoiceId) : false),
        ),
      ]);
    },
    [getContractInvoices, relatedPaymentById, relatedPaymentOptions],
  );

  const getContractDisplayAmount = useCallback(
    (contract: Contract) => {
      const effectiveAmount = getContractEffectiveAmount(contract);
      if (contract.billingMode !== "Actual") {
        return effectiveAmount;
      }

      const lookupInvoiceAmount = getContractInvoices(contract).reduce(
        (sum, invoice) => sum + (invoice.amount ?? 0),
        0,
      );
      return Math.max(effectiveAmount ?? 0, lookupInvoiceAmount);
    },
    [getContractInvoices],
  );

  type RelatedChip = {
    key: string;
    label: string;
    title?: string;
    tone?: "contract" | "invoice" | "act" | "payment";
  };

  const createContractChip = useCallback(
    (contract: Contract): RelatedChip => ({
      key: `contract-${contract.id}`,
      label: `Договор №${contract.number}`,
      title: formatContractFilterLabel(contract),
      tone: "contract",
    }),
    [formatContractFilterLabel],
  );
  const createInvoiceChip = useCallback(
    (invoice: Invoice): RelatedChip => ({
      key: `invoice-${invoice.id}`,
      label: `Счет №${invoice.number}`,
      title: formatInvoiceFilterLabel(invoice),
      tone: "invoice",
    }),
    [formatInvoiceFilterLabel],
  );
  const createActChip = useCallback(
    (act: Act): RelatedChip => ({
      key: `act-${act.id}`,
      label: `Акт №${act.number}`,
      title: formatActFilterLabel(act),
      tone: "act",
    }),
    [formatActFilterLabel],
  );
  const createPaymentChip = useCallback(
    (payment: Payment): RelatedChip => ({
      key: `payment-${payment.id}`,
      label: `Платеж ${formatCurrencySmart(payment.amount).full}`,
      title: formatPaymentFilterLabel(payment),
      tone: "payment",
    }),
    [formatPaymentFilterLabel],
  );

  const renderRelatedChips = useCallback((chips: RelatedChip[]) => {
    if (chips.length === 0) {
      return <span className="text-xs text-slate-400">—</span>;
    }

    const toneClasses: Record<NonNullable<RelatedChip["tone"]>, string> = {
      contract: "border-blue-100 bg-blue-50 text-blue-700",
      invoice: "border-emerald-100 bg-emerald-50 text-emerald-700",
      act: "border-amber-100 bg-amber-50 text-amber-700",
      payment: "border-slate-200 bg-slate-50 text-slate-700",
    };

    return (
      <div className="flex flex-wrap gap-1.5">
        {chips.map((chip) => (
          <span
            key={chip.key}
            title={chip.title ?? chip.label}
            className={`inline-flex max-w-full items-center rounded-full border px-2 py-0.5 text-[11px] font-medium ${toneClasses[chip.tone ?? "payment"]}`}
          >
            <span className="truncate">{chip.label}</span>
          </span>
        ))}
      </div>
    );
  }, []);

  function renderRelatedFilterSelect<T extends { id: number }>(
    label: string,
    value: string,
    onChange: (value: string) => void,
    options: T[],
    emptyLabel: string,
    getLabel: (item: T) => string,
    className = "w-full sm:w-56",
  ) {
    return (
      <ClientSectionFilterField label={label} className={className}>
        <SystemSelect
          value={value}
          onChange={(event) => {
            onChange(event.target.value);
          }}
          disabled={relatedDocumentLookupLoading && options.length === 0}
          className="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus:border-slate-400 focus:outline-none focus:ring-2 focus:ring-slate-200 disabled:bg-slate-100"
        >
          <option value={RELATED_FILTER_ALL}>{emptyLabel}</option>
          {options.map((item) => (
            <option key={item.id} value={item.id}>
              {getLabel(item)}
            </option>
          ))}
        </SystemSelect>
      </ClientSectionFilterField>
    );
  }

  const {
    fetchInvoice: fetchInvoiceById,
    fetchPayments: fetchInvoicePayments,
  } = useMemo(() => bindClientInvoiceModalApi(apiService), []);
  const loadInvoiceRelatedPayments = useCallback(
    async (invoiceSnapshot: Invoice | null) => {
      if (!invoiceSnapshot) {
        setInvoiceModalRelatedPayments([]);
        setInvoiceModalPaymentsLoading(false);
        return;
      }

      setInvoiceModalPaymentsLoading(true);
      try {
        const relatedPayments = await loadClientInvoiceRelatedPayments({
          invoice: invoiceSnapshot,
          clientId,
          visiblePayments,
          fetchPayments: fetchInvoicePayments,
        });
        setInvoiceModalRelatedPayments(relatedPayments);
      } catch {
        setInvoiceModalRelatedPayments([]);
      } finally {
        setInvoiceModalPaymentsLoading(false);
      }
    },
    [clientId, fetchInvoicePayments, visiblePayments],
  );
  const refreshInvoiceModalData = useCallback(async () => {
    setInvoiceModalPaymentsLoading(true);
    try {
      const refreshed = await refreshClientInvoiceModalDataHelper({
        selectedInvoice,
        clientId,
        visiblePayments,
        refreshInvoices,
        refreshPayments,
        fetchInvoice: fetchInvoiceById,
        fetchPayments: fetchInvoicePayments,
      });
      setSelectedInvoice(refreshed.invoice);
      setInvoiceModalRelatedPayments(refreshed.relatedPayments);
    } finally {
      setInvoiceModalPaymentsLoading(false);
    }
  }, [
    clientId,
    fetchInvoiceById,
    fetchInvoicePayments,
    refreshInvoices,
    refreshPayments,
    selectedInvoice,
    visiblePayments,
  ]);
  useEffect(() => {
    if (!invoiceModalOpen || invoiceModalMode !== "edit") {
      setInvoiceModalRelatedPayments([]);
      setInvoiceModalPaymentsLoading(false);
      return;
    }

    void loadInvoiceRelatedPayments(selectedInvoice);
  }, [
    invoiceModalMode,
    invoiceModalOpen,
    loadInvoiceRelatedPayments,
    selectedInvoice,
  ]);
  const visiblePaymentTableColumns = useMemo(() => {
    const columnMap = new Map(
      PAYMENT_TABLE_COLUMNS.map((column) => [column.key, column]),
    );
    return paymentOrderedVisibleColumns.flatMap((columnKey) => {
      const column = columnMap.get(columnKey);
      return column ? [column] : [];
    });
  }, [paymentOrderedVisibleColumns]);
  const visibleInvoiceTableColumns = useMemo(() => {
    const columnMap = new Map(
      INVOICE_TABLE_COLUMNS.map((column) => [column.key, column]),
    );
    return invoiceOrderedVisibleColumns.flatMap((columnKey) => {
      const column = columnMap.get(columnKey);
      return column ? [column] : [];
    });
  }, [invoiceOrderedVisibleColumns]);
  const visibleActTableColumns = useMemo(() => {
    const columnMap = new Map(
      ACT_TABLE_COLUMNS.map((column) => [column.key, column]),
    );
    return actOrderedVisibleColumns.flatMap((columnKey) => {
      const column = columnMap.get(columnKey);
      return column ? [column] : [];
    });
  }, [actOrderedVisibleColumns]);
  const visibleContractTableColumns = useMemo(() => {
    const columnMap = new Map(
      CONTRACT_TABLE_COLUMNS.map((column) => [column.key, column]),
    );
    return contractOrderedVisibleColumns.flatMap((columnKey) => {
      const column = columnMap.get(columnKey);
      return column ? [column] : [];
    });
  }, [contractOrderedVisibleColumns]);
  const paymentTotalPages = useMemo(() => {
    if (!paymentPageSize) return 1;
    return Math.max(1, Math.ceil((paymentsTotal ?? 0) / paymentPageSize));
  }, [paymentPageSize, paymentsTotal]);

  const caseFilterBadges = useMemo(() => {
    const badges: string[] = [];
    if (caseSearch.trim()) {
      badges.push(`Поиск: ${caseSearch.trim()}`);
    }
    if (caseStatusFilter !== "all") {
      const statusLabels: Record<
        Exclude<ClientCaseListStatus, "all">,
        string
      > = {
        open: "Открыто",
        hold: "Приостановлено",
        closed: "Закрыто",
        lost: "Проиграно",
      };
      badges.push(`Статус: ${statusLabels[caseStatusFilter]}`);
    }
    return badges;
  }, [caseSearch, caseStatusFilter]);

  const paymentFilterBadges = useMemo(() => {
    const badges: string[] = [];
    if (paymentViewMode !== defaultPaymentViewMode) {
      badges.push(`Вид: ${paymentViewConfig.label}`);
    }
    const monthLabel = formatMonthFilterLabel(monthFrom, monthTo);
    if (monthLabel) {
      badges.push(`Период: ${monthLabel}`);
    }
    if (statusFilter !== "all") {
      const statusLabels: Record<NormalizedStatus, string> = {
        pending: "Ожидается",
        completed: "Выполнено",
        overdue: "Просрочено",
      };
      badges.push(`Статус: ${statusLabels[statusFilter]}`);
    }
    if (paymentViewMode === "default" && paymentTypeFilter !== "all") {
      badges.push(
        `Тип: ${paymentTypeFilter === "Income" ? "Доходы" : "Расходы"}`,
      );
    }
    if (selectedCaseId !== "all") {
      const selectedCase = cases.find(
        (caseItem) => caseItem.id === selectedCaseId,
      );
      const selectedSupplierCase = supplierCaseSummaries.find(
        (caseItem) => caseItem.caseId === selectedCaseId,
      );
      const caseTitle = selectedCase?.title ?? selectedSupplierCase?.caseTitle;
      if (caseTitle) {
        badges.push(`Дело: ${caseTitle}`);
      }
    }
    if (relatedContractId) {
      badges.push(
        selectedRelatedContract
          ? formatContractFilterLabel(selectedRelatedContract)
          : `Договор #${relatedContractId}`,
      );
    }
    if (relatedInvoiceId) {
      badges.push(
        selectedRelatedInvoice
          ? formatInvoiceFilterLabel(selectedRelatedInvoice)
          : `Счет #${relatedInvoiceId}`,
      );
    }
    if (relatedActId) {
      badges.push(
        selectedRelatedAct
          ? formatActFilterLabel(selectedRelatedAct)
          : `Акт #${relatedActId}`,
      );
    }
    if (paymentSearch.trim()) {
      badges.push(`Поиск: ${paymentSearch.trim()}`);
    }
    return badges;
  }, [
    cases,
    defaultPaymentViewMode,
    monthFrom,
    monthTo,
    paymentSearch,
    paymentTypeFilter,
    paymentViewConfig.label,
    paymentViewMode,
    relatedContractId,
    relatedInvoiceId,
    relatedActId,
    selectedCaseId,
    selectedRelatedContract,
    selectedRelatedInvoice,
    selectedRelatedAct,
    statusFilter,
    supplierCaseSummaries,
    formatContractFilterLabel,
    formatInvoiceFilterLabel,
    formatActFilterLabel,
  ]);

  const invoiceFilterBadges = useMemo(() => {
    const badges: string[] = [];
    if (invoiceViewMode !== defaultInvoiceViewMode) {
      badges.push(`Вид: ${invoiceViewConfig.label}`);
    }
    const periodLabel = formatPeriodLabel(invoicePeriod, today, "");
    if (periodLabel) {
      badges.push(`Период: ${periodLabel}`);
    }
    if (invoiceStatusFilter !== "all") {
      badges.push(`Статус: ${invoiceStatusLabels[invoiceStatusFilter]}`);
    }
    if (relatedContractId) {
      badges.push(
        selectedRelatedContract
          ? formatContractFilterLabel(selectedRelatedContract)
          : `Договор #${relatedContractId}`,
      );
    }
    if (relatedActId) {
      badges.push(
        selectedRelatedAct
          ? formatActFilterLabel(selectedRelatedAct)
          : `Акт #${relatedActId}`,
      );
    }
    if (relatedPaymentId) {
      badges.push(
        selectedRelatedPayment
          ? formatPaymentFilterLabel(selectedRelatedPayment)
          : `Платеж #${relatedPaymentId}`,
      );
    }
    if (invoiceSearch.trim()) {
      badges.push(`Поиск: ${invoiceSearch.trim()}`);
    }
    return badges;
  }, [
    invoicePeriod,
    invoiceSearch,
    invoiceStatusFilter,
    invoiceStatusLabels,
    invoiceViewConfig.label,
    invoiceViewMode,
    relatedContractId,
    relatedActId,
    relatedPaymentId,
    selectedRelatedContract,
    selectedRelatedAct,
    selectedRelatedPayment,
    defaultInvoiceViewMode,
    today,
    formatContractFilterLabel,
    formatActFilterLabel,
    formatPaymentFilterLabel,
  ]);

  const actFilterBadges = useMemo(() => {
    const badges: string[] = [];
    const statusLabels: Record<ActStatus, string> = {
      Created: "Создан",
      Transferred: "Передано",
      Signed: "Подписано",
      Terminated: "Расторгнуто",
    };
    const periodLabel = formatPeriodLabel(actPeriod, today, "");
    if (periodLabel) {
      badges.push(`Период: ${periodLabel}`);
    }
    if (actStatusFilter !== "all") {
      badges.push(`Статус: ${statusLabels[actStatusFilter]}`);
    }
    if (relatedContractId) {
      badges.push(
        selectedRelatedContract
          ? formatContractFilterLabel(selectedRelatedContract)
          : `Договор #${relatedContractId}`,
      );
    }
    if (relatedInvoiceId) {
      badges.push(
        selectedRelatedInvoice
          ? formatInvoiceFilterLabel(selectedRelatedInvoice)
          : `Счет #${relatedInvoiceId}`,
      );
    }
    if (relatedPaymentId) {
      badges.push(
        selectedRelatedPayment
          ? formatPaymentFilterLabel(selectedRelatedPayment)
          : `Платеж #${relatedPaymentId}`,
      );
    }
    if (actSearch.trim()) {
      badges.push(`Поиск: ${actSearch.trim()}`);
    }
    return badges;
  }, [
    actPeriod,
    actSearch,
    actStatusFilter,
    relatedContractId,
    relatedInvoiceId,
    relatedPaymentId,
    selectedRelatedContract,
    selectedRelatedInvoice,
    selectedRelatedPayment,
    today,
    formatContractFilterLabel,
    formatInvoiceFilterLabel,
    formatPaymentFilterLabel,
  ]);

  const contractFilterBadges = useMemo(() => {
    const badges: string[] = [];
    const periodLabel = formatPeriodLabel(contractPeriod, today, "");
    if (periodLabel) {
      badges.push(`Период: ${periodLabel}`);
    }
    if (contractSearch.trim()) {
      badges.push(`Поиск: ${contractSearch.trim()}`);
    }
    if (relatedInvoiceId) {
      badges.push(
        selectedRelatedInvoice
          ? formatInvoiceFilterLabel(selectedRelatedInvoice)
          : `Счет #${relatedInvoiceId}`,
      );
    }
    if (relatedActId) {
      badges.push(
        selectedRelatedAct
          ? formatActFilterLabel(selectedRelatedAct)
          : `Акт #${relatedActId}`,
      );
    }
    if (relatedPaymentId) {
      badges.push(
        selectedRelatedPayment
          ? formatPaymentFilterLabel(selectedRelatedPayment)
          : `Платеж #${relatedPaymentId}`,
      );
    }
    return badges;
  }, [
    contractPeriod,
    contractSearch,
    relatedInvoiceId,
    relatedActId,
    relatedPaymentId,
    selectedRelatedInvoice,
    selectedRelatedAct,
    selectedRelatedPayment,
    today,
    formatInvoiceFilterLabel,
    formatActFilterLabel,
    formatPaymentFilterLabel,
  ]);

  useEffect(() => {
    if (activeSection !== "payments") setPaymentTableSettingsOpen(false);
    if (activeSection !== "accounts") setInvoiceTableSettingsOpen(false);
    if (activeSection !== "acts") setActTableSettingsOpen(false);
    if (activeSection !== "contracts") setContractTableSettingsOpen(false);
  }, [activeSection]);

  useEffect(() => {
    setPaymentPage(1);
  }, [
    caseIdForQuery,
    fromDateStr,
    paymentPageSize,
    paymentTypeFilter,
    paymentViewMode,
    relatedContractId,
    relatedInvoiceId,
    relatedActId,
    statusFilter,
    toDateStr,
    debouncedPaymentSearch,
  ]);

  const actModalClients = useMemo(() => {
    const normalizedName =
      clientName && clientName !== "..." ? clientName : `Клиент #${clientId}`;
    if (actClients.length === 0) {
      return [
        {
          id: clientId,
          name: normalizedName,
          email: "",
          phone: "",
          company: "",
          address: "",
          notes: "",
          createdAt: "1970-01-01T00:00:00.000Z",
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
          email: "",
          phone: "",
          company: "",
          address: "",
          notes: "",
          createdAt: "1970-01-01T00:00:00.000Z",
          isActive: true,
        } satisfies Client,
      ];
    }

    return actClients;
  }, [
    actClients,
    clientData?.responsibleUserId,
    clientData?.responsibleUserName,
    clientId,
    clientName,
  ]);

  const invoiceModalClients = useMemo(() => {
    const normalizedName =
      clientName && clientName !== "..." ? clientName : `Клиент #${clientId}`;
    if (invoiceClients.length === 0) {
      return [
        {
          id: clientId,
          name: normalizedName,
          email: "",
          phone: "",
          company: "",
          address: "",
          notes: "",
          createdAt: "1970-01-01T00:00:00.000Z",
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
          email: "",
          phone: "",
          company: "",
          address: "",
          notes: "",
          createdAt: "1970-01-01T00:00:00.000Z",
          isActive: true,
          responsibleUserId: clientData?.responsibleUserId ?? null,
          responsibleUserName: clientData?.responsibleUserName ?? null,
        } satisfies Client,
      ];
    }

    return invoiceClients;
  }, [
    clientData?.responsibleUserId,
    clientData?.responsibleUserName,
    invoiceClients,
    clientId,
    clientName,
  ]);

  const contractDefaultClients = useMemo<ContractClient[]>(() => {
    const normalizedName =
      clientName && clientName !== "..."
        ? clientName
        : (clientData?.name ?? `Клиент #${clientId}`);

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
        key: "signed" as const,
        title: t("actSummarySigned") ?? "Подписано",
        bucket: actSummaryBuckets.signed,
        icon: CheckCircle2,
        accent: "text-emerald-700 bg-emerald-50 border border-emerald-100",
      },
      {
        key: "transferred" as const,
        title: t("actSummaryTransferred") ?? "Передано",
        bucket: actSummaryBuckets.transferred,
        icon: Send,
        accent: "text-amber-700 bg-amber-50 border border-amber-100",
      },
      {
        key: "terminated" as const,
        title: t("actSummaryTerminated") ?? "Расторгнуто",
        bucket: actSummaryBuckets.terminated,
        icon: Ban,
        accent: "text-rose-700 bg-rose-50 border border-rose-100",
      },
    ],
    [actSummaryBuckets, t],
  );

  const actStatusLabels = useMemo<Record<ActStatus, string>>(
    () => ({
      Created: t("actStatusCreated") ?? "Создан",
      Transferred: t("actStatusTransferred") ?? "Передано",
      Signed: t("actStatusSigned") ?? "Подписано",
      Terminated: t("actStatusTerminated") ?? "Расторгнуто",
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

  const addCaseLabel = t("addCase") ?? "Добавить дело";

  const sortedClientStatuses = useMemo(
    () =>
      [...clientStatuses].sort((a, b) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: "base" }),
      ),
    [clientStatuses],
  );

  const selectedLegalEntity = useMemo(() => {
    if (!clientFormState.legalEntityId) return null;
    return (
      legalEntityOptions.find(
        (entity) => entity.id === Number(clientFormState.legalEntityId),
      ) ?? null
    );
  }, [clientFormState.legalEntityId, legalEntityOptions]);

  const sortedLegalEntities = legalEntityOptions;

  const selectedClientStatus = useMemo(() => {
    if (!clientFormState.clientStatusId) return null;
    return (
      sortedClientStatuses.find(
        (status) => status.id === Number(clientFormState.clientStatusId),
      ) ?? null
    );
  }, [clientFormState.clientStatusId, sortedClientStatuses]);

  const canManageClientResponsible = userIsAdmin && clientPermissions.canEdit;
  const canEditClientProfile = clientPermissions.canEdit && clientProfileEditing;
  const canManageClientResponsibleInProfile =
    canManageClientResponsible && clientProfileEditing;

  const clientResponsibleOptions = useMemo(() => {
    const options = [...actResponsibles];
    const currentResponsibleId = clientFormState.responsibleUserId
      ? Number(clientFormState.responsibleUserId)
      : null;
    if (
      currentResponsibleId &&
      !options.some((responsible) => responsible.id === currentResponsibleId) &&
      clientData?.responsibleUserName
    ) {
      options.push({
        id: currentResponsibleId,
        fullName: clientData.responsibleUserName,
      });
    }

    return options.sort((a, b) =>
      a.fullName.localeCompare(b.fullName, undefined, { sensitivity: "base" }),
    );
  }, [
    actResponsibles,
    clientData?.responsibleUserName,
    clientFormState.responsibleUserId,
  ]);

  const clientResponsibleLookupOptions = useMemo(
    () =>
      clientResponsibleOptions.map((responsible) => ({
        value: String(responsible.id),
        label: responsible.fullName,
      })),
    [clientResponsibleOptions],
  );

  const ensureActLookups = useCallback(async (options?: { includeClients?: boolean }) => {
    const includeClients = options?.includeClients ?? true;
    if (actLookupsLoading) return;
    const needsClients =
      includeClients && (!actClientsLoaded || Boolean(actLookupsError));
    const needsResponsibles =
      !actResponsiblesLoaded || Boolean(actLookupsError);
    if (!needsClients && !needsResponsibles)
      return;
    setActLookupsLoading(true);
    try {
      const [clientsResponse, responsiblesResponse] = await Promise.all([
        needsClients ? apiService.getClients() : Promise.resolve(actClients),
        needsResponsibles
          ? apiService.getActResponsibles()
          : Promise.resolve(actResponsibles),
      ]);
      if (needsClients) {
        setActClients(clientsResponse ?? []);
        setActClientsLoaded(true);
      }
      if (needsResponsibles) {
        setActResponsibles(responsiblesResponse ?? []);
        setActResponsiblesLoaded(true);
      }
      setActLookupsError(null);
    } catch (error) {
      setActLookupsError(
        error instanceof Error
          ? error.message
          : "Не удалось загрузить справочники",
      );
    } finally {
      setActLookupsLoading(false);
    }
  }, [
    actClients,
    actClientsLoaded,
    actResponsibles,
    actResponsiblesLoaded,
    actLookupsError,
    actLookupsLoading,
  ]);

  useEffect(() => {
    if (actModalOpen) {
      void ensureActLookups({ includeClients: true });
    }
  }, [actModalOpen, ensureActLookups]);

  useEffect(() => {
    if (invoiceModalOpen || canManageClientResponsible) {
      void ensureActLookups({ includeClients: false });
    }
  }, [canManageClientResponsible, ensureActLookups, invoiceModalOpen]);

  useEffect(() => {
    if (caseModalOpen) {
      void ensureActLookups({ includeClients: false });
    }
  }, [caseModalOpen, ensureActLookups]);

  const openAddPayment = () => {
    if (!paymentPermissions.canView || !paymentPermissions.canCreate) {
      showPermissionWarning(
        t("permissionNoAddPayment") ??
          "Недостаточно прав для добавления платежей.",
      );
      return;
    }
    setActiveSection("payments");
    setEditingPayment(null);
    setPayModalOpen(true);
  };
  const openEditPayment = async (p: Payment) => {
    if (!paymentPermissions.canEdit) {
      showPermissionWarning(
        t("permissionNoEditPayment") ??
          "Недостаточно прав для редактирования платежей.",
      );
      return;
    }
    try {
      const detailedPayment = await apiService.getPayment(p.id);
      setActiveSection("payments");
      setEditingPayment(detailedPayment);
      setPayModalOpen(true);
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, "Не удалось загрузить платеж для редактирования."),
        t("payments") ?? "Платежи",
      );
    }
  };
  const openAddAct = () => {
    if (!actPermissions.canCreate) {
      showPermissionWarning("Недостаточно прав для добавления актов.");
      return;
    }

    if (onCreateActPage) {
      setActiveSection("acts");
      onCreateActPage(
        {
          clientId,
          status: "Created",
        },
        { section: "acts" },
      );
      return;
    }

    setActiveSection("acts");
    setActModalMode("create");
    setSelectedAct(null);
    setActModalError(null);
    setActModalOpen(true);
  };
  const openEditAct = (act: Act) => {
    if (!actPermissions.canEdit) {
      showPermissionWarning("Недостаточно прав для редактирования актов.");
      return;
    }

    setActiveSection("acts");
    setActModalMode("edit");
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
    notes: p.notes ?? "",
    systemNotes: p.systemNotes ?? "",
    clientId: p.clientId ?? clientId ?? null,
    clientCaseId: p.clientCaseId ?? caseIdForQuery ?? null,
    dealTypeId: p.dealTypeId ?? null,
    incomeTypeId: p.incomeTypeId ?? null,
    paymentSourceId: p.paymentSourceId ?? null,
    paymentStatusId: p.paymentStatusId ?? null,
    account: p.account ?? null,
    accountDate: p.accountDate ?? null,
  });

  const refreshAfterPaymentSave = async () => {
    try {
      await refreshPayments();
      await refreshClientBalance();
      if (clientBalanceExpanded) await refreshClientBalanceTransactions();
      bumpStats();
    } catch {
      toast.error('Платёж сохранён, но связанные списки пока не удалось обновить.', 'Платежи');
    }
  };

  const submitPayment = async (payload: PaymentUpsert): Promise<Payment> => {
    const isEdit = 'id' in payload;
    if (!(isEdit ? clientPermissions.canEdit : clientPermissions.canCreate)) {
      throw new Error('Недостаточно прав для сохранения платежа.');
    }
    try {
      let saved: Payment;
      if ('id' in payload) {
        const { id, ...rest } = payload;
        saved = editingPayment && isInvoiceManagedPayment(editingPayment)
          ? await apiService.updateInvoiceManagedPayment(id, toBody(rest))
          : await apiService.updatePayment(id, toBody(rest));
      } else {
        saved = await apiService.createPayment(toBody(payload));
      }
      void refreshAfterPaymentSave();
      toast.success(isEdit ? 'Платёж обновлён.' : 'Платёж создан.', 'Платежи');
      return saved;
    } catch (error) {
      if (error instanceof PaymentCreateAdoptedError) throw error;
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить платёж.'), 'Платежи');
      throw error;
    }
  };

  const transferPaymentRemainder = async (
    payload: { id: number } & PaymentPayload,
    remainderDate: string,
  ): Promise<Payment> => {
    if (!clientPermissions.canEdit) throw new Error('Недостаточно прав для редактирования платежа.');
    try {
      const saved = await apiService.transferPaymentRemainder(payload.id, toBody(payload), remainderDate);
      void refreshAfterPaymentSave();
      toast.success('Остаток платежа перенесён.', 'Платежи');
      return saved.payment;
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось перенести остаток платежа'), 'Платежи');
      throw error;
    }
  };

  const rollbackPaymentRemainderTransfer = async (paymentId: number) => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning(
        t("permissionNoEditPayment") ??
          "Недостаточно прав для редактирования платежей.",
      );
      return;
    }

    try {
      await apiService.rollbackPaymentRemainderTransfer(paymentId);
      await refreshPayments();
      await refreshClientBalance();
      if (clientBalanceExpanded) await refreshClientBalanceTransactions();
      bumpStats();
      toast.success("Перенос остатка отменен.", "Платежи");
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, "Не удалось откатить перенос остатка"),
        "Платежи",
      );
      throw error;
    }
  };

  const adjustPaymentRemainderTransfer = async (
    paymentId: number,
    paidAmount: number,
    remainderDate: string,
  ) => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning(
        t("permissionNoEditPayment") ??
          "Недостаточно прав для редактирования платежей.",
      );
      return;
    }

    try {
      await apiService.adjustPaymentRemainderTransfer(paymentId, paidAmount, remainderDate);
      await refreshPayments();
      await refreshClientBalance();
      if (clientBalanceExpanded) await refreshClientBalanceTransactions();
      bumpStats();
      toast.success("Разделение платежа обновлено.", "Платежи");
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, "Не удалось изменить разделение платежа"),
        "Платежи",
      );
      throw error;
    }
  };

  const applyClientBalanceToPayment = async (amount: number, currentPayment?: Payment) => {
    const target = currentPayment ?? editingPayment;
    if (!target?.id || !clientId || !clientPermissions.canEdit) {
      throw new Error('Недостаточно прав или не выбран сохранённый платёж.');
    }
    const result = await apiService.applyClientBalance(clientId, { paymentId: target.id, amount });
    void refreshAfterPaymentSave();
    return result;
  };

  const removePayment = async (target: Payment | number) => {
    const isLockedPayment = typeof target !== "number"
      && (target.documentKind === "Invoice" || isTransferredRemainderPayment(target) || isPartialRemainderSourcePayment(target));
    if (isLockedPayment) {
      toast.error(
        isTransferredRemainderPayment(target as Payment) || isPartialRemainderSourcePayment(target as Payment)
          ? "Перенесенный остаток нельзя удалить отдельно."
          : 'Документ счета удаляется через раздел "Счета".',
        "Платежи",
      );
      return false;
    }

    const id = typeof target === "number" ? target : target.id;
    if (!paymentPermissions.canDelete) {
      showPermissionWarning(
        t("permissionNoDeletePayment") ??
          "Недостаточно прав для удаления платежей.",
      );
      return;
    }
    if (
      !(await confirm({
        title: "Подтверждение удаления",
        description: "Удалить платёж?",
        confirmLabel: "Удалить",
        cancelLabel: "Отмена",
        variant: "danger",
      }))
    )
      return;
    try {
      await apiService.deletePayment(id);
      if (payments.length === 1 && paymentPage > 1) {
        setPaymentPage((currentPage) => Math.max(1, currentPage - 1));
      } else {
        await refreshPayments();
      }
      bumpStats();
      toast.success("Платёж удалён.", "Платежи");
      return true;
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, "Не удалось удалить платёж"),
        "Платежи",
      );
      return false;
    }
  };

  const submitAct = async (payload: ActInput) => {
    const resolvedClientId = payload.clientId ?? clientId;
    if (!resolvedClientId) {
      setActModalError("Клиент не выбран");
      return;
    }

    setActSubmitting(true);
    setActModalError(null);
    const body: ActInput = {
      ...payload,
      clientId: resolvedClientId,
    };

    try {
      const isEdit = actModalMode === "edit" && selectedAct;
      if (isEdit && !actPermissions.canEdit) {
        showPermissionWarning("Недостаточно прав для редактирования актов.");
        setActSubmitting(false);
        return;
      }
      if (!isEdit && !actPermissions.canCreate) {
        showPermissionWarning("Недостаточно прав для добавления актов.");
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
      setActModalError(
        error instanceof Error ? error.message : "Не удалось сохранить акт",
      );
    } finally {
      setActSubmitting(false);
    }
  };

  const removeAct = async (act: Act) => {
    if (!actPermissions.canDelete) {
      showPermissionWarning("Недостаточно прав для удаления актов.");
      return;
    }
    if (
      !(await confirm({
        title: "Подтверждение удаления",
        description: `Удалить акт №${act.number}?`,
        confirmLabel: "Удалить",
        cancelLabel: "Отмена",
        variant: "danger",
      }))
    )
      return;
    try {
      await deleteAct(act.id);
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, "Не удалось удалить акт"),
        "Ошибка удаления",
      );
    }
  };

  const handleActSort = (key: ActsSortKey) => {
    setActSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === "asc" ? "desc" : "asc" };
      }
      return {
        key,
        direction: key === "date" || key === "createdAt" ? "desc" : "asc",
      };
    });
    setActPage(1);
  };

  const resetActFilters = () => {
    setActPeriod({ mode: "preset", preset: "year_to_date" });
    setActStatusFilter("all");
    resetRelatedDocumentFilters();
    setActSearch("");
    setActPage(1);
  };

  const openAddCase = () => {
    if (!casePermissions.canView || !casePermissions.canCreate) {
      showPermissionWarning("Недостаточно прав для добавления дел.");
      return;
    }
    setActiveSection("cases");
    setEditingCase(null);
    setCaseMode("create");
    setCaseModalOpen(true);
  };

  const openPaymentsForCase = (caseId: number, type?: Payment["type"]) => {
    setSelectedCaseId(caseId);
    setPaymentTypeFilter(type ?? "all");
    setPaymentViewMode(type === "Expense" ? "expenseClient" : "default");
    setPaymentViewModeTouched(true);
    setPaymentPage(1);
    setActiveSection("payments");
  };
  const openSupplierPaymentsForCase = (caseId: number) => {
    setSelectedCaseId(caseId);
    setPaymentTypeFilter("Expense");
    setPaymentViewMode("supplier");
    setPaymentViewModeTouched(true);
    setPaymentPage(1);
    setActiveSection("payments");
  };
  const openExpenseDetailsForCase = useCallback(
    async (caseId: number) => {
      const supplierCase = supplierCaseSummaries.find(
        (item) => item.caseId === caseId,
      );
      const clientCase = cases.find((item) => item.id === caseId);

      setExpenseDetailsCase({
        id: caseId,
        title:
          supplierCase?.caseTitle ||
          clientCase?.title ||
          `Дело #${caseId}`,
        clientName: supplierCase?.clientName ?? clientData?.name ?? null,
      });
      setExpenseDetails([]);
      setExpenseDetailsError(null);
      setExpenseDetailsLoading(true);

      try {
        const rows = await apiService.getClientCaseExpenseDetails(clientId, {
          caseId,
          clientRole: isSupplierCard ? "counterparty" : "expenseClient",
        });
        setExpenseDetails(rows ?? []);
      } catch (error) {
        setExpenseDetailsError(
          extractDisplayErrorMessage(
            error,
            "Не удалось загрузить расшифровку расходов по делу",
          ),
        );
      } finally {
        setExpenseDetailsLoading(false);
      }
    },
    [cases, clientData?.name, clientId, isSupplierCard, supplierCaseSummaries],
  );
  const expenseDetailsTotals = useMemo(
    () =>
      expenseDetails.reduce(
        (acc, item) => ({
          amount: acc.amount + (item.amount ?? 0),
          paid: acc.paid + (item.paidAmount ?? 0),
          outstanding: acc.outstanding + (item.outstandingAmount ?? 0),
        }),
        { amount: 0, paid: 0, outstanding: 0 },
      ),
    [expenseDetails],
  );

  const blankCaseDraft = useMemo<ClientCase>(
    () => ({
      id: 0,
      clientId,
      title: "",
      description: "",
      status: "Open",
      createdAt: new Date().toISOString(),
    }),
    [clientId],
  );

  const saveCase = async (patch: Partial<ClientCase>): Promise<void> => {
    if (caseMode === "edit" && editingCase) {
      if (!casePermissions.canEdit) {
        showPermissionWarning("Недостаточно прав для редактирования дел.");
        return;
      }
    } else if (!casePermissions.canView || !casePermissions.canCreate) {
      showPermissionWarning("Недостаточно прав для добавления дел.");
      return;
    }

    const base: Omit<ClientCase, "id" | "createdAt" | "payments"> = {
      clientId,
      title: (patch.title ?? editingCase?.title ?? "").trim(),
      description: (patch.description ?? editingCase?.description ?? "").trim(),
      status: (patch.status ??
        editingCase?.status ??
        "Open") as ClientCase["status"],
      caseNumber: (patch.caseNumber ?? editingCase?.caseNumber ?? "").trim(),
      caseType: (patch.caseType ?? editingCase?.caseType ?? "").trim(),
      stage: (patch.stage ?? editingCase?.stage ?? "").trim(),
      responsibleUserId:
        patch.responsibleUserId === undefined
          ? editingCase?.responsibleUserId ?? null
          : patch.responsibleUserId,
      startDate:
        patch.startDate === undefined
          ? editingCase?.startDate ?? null
          : patch.startDate,
      dueDate:
        patch.dueDate === undefined ? editingCase?.dueDate ?? null : patch.dueDate,
      feeAmount: patch.feeAmount ?? editingCase?.feeAmount ?? 0,
      priority: patch.priority ?? editingCase?.priority ?? "normal",
      courtName: (patch.courtName ?? editingCase?.courtName ?? "").trim(),
      ourCourtRole: patch.ourCourtRole ?? editingCase?.ourCourtRole ?? '',
      courtClaimAmount: patch.courtClaimAmount !== undefined ? patch.courtClaimAmount : editingCase?.courtClaimAmount ?? null,
      courtAwardedAmount: patch.courtAwardedAmount !== undefined ? patch.courtAwardedAmount : editingCase?.courtAwardedAmount ?? null,
      courtStateFeeAmount: patch.courtStateFeeAmount !== undefined ? patch.courtStateFeeAmount : editingCase?.courtStateFeeAmount ?? null,
      clearCourtClaimAmount: patch.clearCourtClaimAmount ?? false,
      clearCourtAwardedAmount: patch.clearCourtAwardedAmount ?? false,
      clearCourtStateFeeAmount: patch.clearCourtStateFeeAmount ?? false,
      courtCaseNumber: (
        patch.courtCaseNumber ??
        editingCase?.courtCaseNumber ??
        ""
      ).trim(),
      courtUrl: (patch.courtUrl ?? editingCase?.courtUrl ?? "").trim(),
      nextHearingAt:
        patch.nextHearingAt === undefined
          ? editingCase?.nextHearingAt ?? null
          : patch.nextHearingAt,
      claimant: patch.claimant ?? editingCase?.claimant ?? '',
      claimantInn: patch.claimantInn ?? editingCase?.claimantInn ?? '',
      claimantOgrn: patch.claimantOgrn ?? editingCase?.claimantOgrn ?? '',
      defendant: patch.defendant ?? editingCase?.defendant ?? '',
      defendantInn: patch.defendantInn ?? editingCase?.defendantInn ?? '',
      defendantOgrn: patch.defendantOgrn ?? editingCase?.defendantOgrn ?? '',
      thirdParties: patch.thirdParties ?? editingCase?.thirdParties ?? '',
      representatives: patch.representatives ?? editingCase?.representatives ?? '',
      judgeName: patch.judgeName ?? editingCase?.judgeName ?? '',
      courtroom: patch.courtroom ?? editingCase?.courtroom ?? '',
      courtSecretaryContacts: patch.courtSecretaryContacts ?? editingCase?.courtSecretaryContacts ?? '',
      claimFiledAt: patch.claimFiledAt ?? editingCase?.claimFiledAt ?? null,
      courtDecisionAt: patch.courtDecisionAt ?? editingCase?.courtDecisionAt ?? null,
      courtDecisionEffectiveAt: patch.courtDecisionEffectiveAt ?? editingCase?.courtDecisionEffectiveAt ?? null,
      clearCourtDecisionEffectiveAt: patch.clearCourtDecisionEffectiveAt ?? false,
      executiveWritReceivedAt: patch.executiveWritReceivedAt ?? editingCase?.executiveWritReceivedAt ?? null,
      clearExecutiveWritReceivedAt: patch.clearExecutiveWritReceivedAt ?? false,
      executiveWritSubmittedAt: patch.executiveWritSubmittedAt !== undefined ? patch.executiveWritSubmittedAt : editingCase?.executiveWritSubmittedAt ?? null,
      executiveWritSubmittedTo: patch.executiveWritSubmittedTo !== undefined ? patch.executiveWritSubmittedTo : editingCase?.executiveWritSubmittedTo ?? '',
      clearExecutiveWritSubmission: patch.clearExecutiveWritSubmission ?? false,
      appealDeadlineAt: patch.appealDeadlineAt ?? editingCase?.appealDeadlineAt ?? null,
      cassationDeadlineAt: patch.cassationDeadlineAt ?? editingCase?.cassationDeadlineAt ?? null,
      motionDeadlineAt: patch.motionDeadlineAt ?? editingCase?.motionDeadlineAt ?? null,
      objectionDeadlineAt: patch.objectionDeadlineAt ?? editingCase?.objectionDeadlineAt ?? null,
      documentsSummary: patch.documentsSummary ?? editingCase?.documentsSummary ?? '',
      eventHistory: patch.eventHistory ?? editingCase?.eventHistory ?? '',
      strategy: patch.strategy ?? editingCase?.strategy ?? '',
    };

    if (caseMode === "edit" && editingCase) {
      const updated = await apiService.updateCase(editingCase.id, base);
      setCasesLocal((prev) =>
        (prev ?? []).map((item) =>
          item.id === updated.id ? { ...item, ...updated } : item,
        ),
      );
      setCaseModalOpen(false);
      setEditingCase(updated);
      setActiveSection("cases");
      bumpStats();
      toast.success("Дело обновлено.", "Дела");
    } else {
      const created = await apiService.createCase(base);
      setCasesLocal((prev) => [created, ...(prev ?? [])]);
      setSelectedCaseId(created.id);
      setCaseModalOpen(false);
      setEditingCase(null);
      setActiveSection("cases");
      bumpStats();
      toast.success("Дело создано.", "Дела");
    }
  };

  const deleteCase = async (): Promise<void> => {
    if (!editingCase) return;
    if (!casePermissions.canDelete) {
      showPermissionWarning("Недостаточно прав для удаления дел.");
      return;
    }
    await apiService.deleteCase(editingCase.id);
    setCasesLocal((prev) =>
      (prev ?? []).filter((item) => item.id !== editingCase.id),
    );
    setCaseModalOpen(false);
    setEditingCase(null);
    setSelectedCaseId("all");
    bumpStats();
    toast.success("Дело удалено.", "Дела");
  };

  const notFound = !clientData && clientName === `Client #${clientId}`;

  const handleClientFormChange = (
    event: React.ChangeEvent<
      HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement
    >,
  ) => {
    const { name, value } = event.target;
    if (name === "legalEntityId") {
      setClientFormState((prev) => ({ ...prev, legalEntityId: value }));
      return;
    }
    if (name === "responsibleUserId") {
      setClientFormState((prev) => ({ ...prev, responsibleUserId: value }));
      return;
    }
    if (name === "clientStatusId") {
      setClientFormState((prev) => ({ ...prev, clientStatusId: value }));
      return;
    }
    const el = event.target as HTMLInputElement;
    setClientFormState((prev) => ({
      ...prev,
      [name]: el.type === "checkbox" ? el.checked : value,
    }));
  };

  const cancelClientProfileEditing = () => {
    if (clientFormBaselineRef.current) {
      setClientFormState(clientFormBaselineRef.current);
    }
    setClientProfileEditing(false);
  };

  const updateListField = (
    field: "phones" | "emails" | "addresses" | ClientDocumentKey,
    index: number,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      [field]: prev[field].map((item, i) => (i === index ? value : item)),
    }));
  };

  const addListFieldRow = (
    field: "phones" | "emails" | "addresses" | ClientDocumentKey,
  ) => {
    setClientFormState((prev) => ({ ...prev, [field]: [...prev[field], ""] }));
  };

  const removeListFieldRow = async (
    field: "phones" | "emails" | "addresses" | ClientDocumentKey,
    index: number,
  ) => {
    const value = clientFormState[field][index]?.trim() ?? "";
    const isPhoneField = field === "phones";
    const fieldLabel = isPhoneField ? "телефон" : "email";

    await confirmProfileDeleteAction({
      title: isPhoneField ? "Удаление телефона" : "Удаление email",
      description: value
        ? `Удалить ${fieldLabel} «${value}»?`
        : `Удалить ${fieldLabel}?`,
      successMessage: isPhoneField ? "Телефон удалён." : "Email удалён.",
      toastTitle: "Контакты",
      onDelete: () => {
        setClientFormState((prev) => {
          const nextValues = prev[field].filter((_, i) => i !== index);
          return { ...prev, [field]: nextValues.length ? nextValues : [""] };
        });
      },
    });
  };

  const handleInvoiceCaseCreated = useCallback(
    (created: ClientCase) => {
      if (created.clientId !== clientId) {
        return;
      }

      setCasesLocal((prev) => {
        const source = prev ?? serverCases;
        return [created, ...source.filter((item) => item.id !== created.id)];
      });
      bumpStats();
    },
    [clientId, serverCases],
  );

  const updateEducation = (
    index: number,
    key: keyof ClientEducationItem,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      education: prev.education.map((item, i) =>
        i === index ? { ...item, [key]: value } : item,
      ),
    }));
  };

  const updateProfession = (
    index: number,
    key: keyof ClientProfessionItem,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      professions: prev.professions.map((item, i) =>
        i === index ? { ...item, [key]: value } : item,
      ),
    }));
  };

  const updateWorkplace = (
    index: number,
    key: keyof ClientWorkplaceItem,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      workplaces: prev.workplaces.map((item, i) =>
        i === index ? { ...item, [key]: value } : item,
      ),
    }));
  };

  const updateRelative = (
    index: number,
    key: keyof ClientRelativeItem,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      relatives: prev.relatives.map((item, i) =>
        i === index ? { ...item, [key]: value } : item,
      ),
    }));
  };

  const removeEducation = async (index: number) => {
    await confirmProfileDeleteAction({
      title: "Удаление образования",
      description: `Удалить образование #${index + 1}?`,
      successMessage: "Образование удалено.",
      toastTitle: "Образование (ОКСО)",
      onDelete: () => {
        setClientFormState((prev) => ({
          ...prev,
          education:
            prev.education.filter((_, i) => i !== index).length > 0
              ? prev.education.filter((_, i) => i !== index)
              : [createEmptyEducation()],
        }));
      },
    });
  };

  const removeProfession = async (index: number) => {
    await confirmProfileDeleteAction({
      title: "Удаление профессии",
      description: `Удалить профессию #${index + 1}?`,
      successMessage: "Профессия удалена.",
      toastTitle: "Профессии (ОКПДТР)",
      onDelete: () => {
        setClientFormState((prev) => ({
          ...prev,
          professions:
            prev.professions.filter((_, i) => i !== index).length > 0
              ? prev.professions.filter((_, i) => i !== index)
              : [createEmptyProfession()],
        }));
      },
    });
  };

  const removeWorkplace = async (index: number) => {
    await confirmProfileDeleteAction({
      title: "Удаление места работы",
      description: `Удалить место работы #${index + 1}?`,
      successMessage: "Место работы удалено.",
      toastTitle: "Работа и должности",
      onDelete: () => {
        setClientFormState((prev) => ({
          ...prev,
          workplaces:
            prev.workplaces.filter((_, i) => i !== index).length > 0
              ? prev.workplaces.filter((_, i) => i !== index)
              : [createEmptyWorkplace()],
        }));
      },
    });
  };

  const removeRelative = async (index: number) => {
    await confirmProfileDeleteAction({
      title: "Удаление родственника",
      description: `Удалить родственника #${index + 1}?`,
      successMessage: "Родственник удалён.",
      toastTitle: "Родственники",
      onDelete: () => {
        setClientFormState((prev) => ({
          ...prev,
          relatives:
            prev.relatives.filter((_, i) => i !== index).length > 0
              ? prev.relatives.filter((_, i) => i !== index)
              : [createEmptyRelative()],
        }));
      },
    });
  };

  const updatePassportEntry = (
    index: number,
    key: keyof ClientPassportItem,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      passportEntries: prev.passportEntries.map((item, i) =>
        i === index ? { ...item, [key]: value } : item,
      ),
    }));
  };

  const addPassportEntry = () => {
    setClientFormState((prev) => ({
      ...prev,
      passportEntries: [...prev.passportEntries, createEmptyPassportEntry()],
    }));
  };

  const removePassportEntry = async (index: number) => {
    await confirmProfileDeleteAction({
      title: "Удаление паспорта",
      description: `Удалить паспорт #${index + 1}?`,
      successMessage: "Паспорт удалён.",
      toastTitle: "Паспорта",
      onDelete: () => {
        setClientFormState((prev) => ({
          ...prev,
          passportEntries:
            prev.passportEntries.filter((_, i) => i !== index).length > 0
              ? prev.passportEntries.filter((_, i) => i !== index)
              : [createEmptyPassportEntry()],
        }));
      },
    });
  };

  const updateAddressEntry = (
    index: number,
    key: keyof ClientAddressItem,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: prev.addressEntries.map((item, i) => {
        if (i !== index) return item;
        const updated = { ...item, [key]: value };
        if (
          [
            "postalCode",
            "region",
            "district",
            "city",
            "settlement",
            "street",
            "house",
            "flat",
          ].includes(key)
        ) {
          return {
            ...updated,
            fullAddress: chooseBestDisplayAddress(
              updated,
              updated.fullAddress,
              updated.sourceQuery,
            ),
          };
        }
        return updated;
      }),
    }));
  };

  const updateAddressFormEntry = (
    index: number,
    key: keyof ClientAddressEntryFormValue,
    value: string,
  ) => {
    updateAddressEntry(index, key as keyof ClientAddressItem, value);
  };

  const setPrimaryAddressFormEntry = (index: number) => {
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: setPrimaryAddressEntry(prev.addressEntries, index),
    }));
  };

  const addAddressEntry = () => {
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: ensureSinglePrimaryAddressEntryList([
        ...prev.addressEntries,
        createEmptyAddressEntry(),
      ]),
    }));
  };

  const removeAddressEntry = async (index: number) => {
    const addressLabel =
      clientFormState.addressEntries[index]?.fullAddress?.trim() ?? "";

    await confirmProfileDeleteAction({
      title: "Удаление адреса",
      description: addressLabel
        ? `Удалить адрес «${addressLabel}»?`
        : `Удалить адрес #${index + 1}?`,
      successMessage: "Адрес удалён.",
      toastTitle: "Адреса",
      onDelete: () => {
        setClientFormState((prev) => ({
          ...prev,
          addressEntries:
            prev.addressEntries.filter((_, i) => i !== index).length > 0
              ? ensureSinglePrimaryAddressEntryList(
                  prev.addressEntries.filter((_, i) => i !== index),
                )
              : [createEmptyAddressEntry(true)],
        }));
      },
    });
  };

  const toggleAddressAdditionalType = (index: number, type: string) => {
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: prev.addressEntries.map((item, i) => {
        if (i !== index) return item;
        const has = item.additionalTypes.includes(type);
        return {
          ...item,
          additionalTypes: has
            ? item.additionalTypes.filter((value) => value !== type)
            : [...item.additionalTypes, type],
        };
      }),
    }));
  };

  const updateBirthCertificateEntry = (
    index: number,
    key: keyof ClientBirthCertificateItem,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      birthCertificateEntries: prev.birthCertificateEntries.map((item, i) =>
        i === index ? { ...item, [key]: value } : item,
      ),
    }));
  };

  const addBirthCertificateEntry = () => {
    setClientFormState((prev) => ({
      ...prev,
      birthCertificateEntries: [
        ...prev.birthCertificateEntries,
        createEmptyBirthCertificateEntry(),
      ],
    }));
  };

  const removeBirthCertificateEntry = async (index: number) => {
    await confirmProfileDeleteAction({
      title: "Удаление свидетельства о рождении",
      description: `Удалить свидетельство о рождении #${index + 1}?`,
      successMessage: "Свидетельство о рождении удалено.",
      toastTitle: "Свидетельства о рождении",
      onDelete: () => {
        setClientFormState((prev) => ({
          ...prev,
          birthCertificateEntries:
            prev.birthCertificateEntries.filter((_, i) => i !== index).length >
            0
              ? prev.birthCertificateEntries.filter((_, i) => i !== index)
              : [createEmptyBirthCertificateEntry()],
        }));
      },
    });
  };

  const updateMilitaryIdEntry = (
    index: number,
    key: keyof ClientMilitaryIdItem,
    value: string,
  ) => {
    setClientFormState((prev) => ({
      ...prev,
      militaryIdEntries: prev.militaryIdEntries.map((item, i) =>
        i === index ? { ...item, [key]: value } : item,
      ),
    }));
  };

  const addMilitaryIdEntry = () => {
    setClientFormState((prev) => ({
      ...prev,
      militaryIdEntries: [
        ...prev.militaryIdEntries,
        createEmptyMilitaryIdEntry(),
      ],
    }));
  };

  const removeMilitaryIdEntry = async (index: number) => {
    await confirmProfileDeleteAction({
      title: "Удаление военного билета",
      description: `Удалить военный билет #${index + 1}?`,
      successMessage: "Военный билет удалён.",
      toastTitle: "Военные билеты",
      onDelete: () => {
        setClientFormState((prev) => ({
          ...prev,
          militaryIdEntries:
            prev.militaryIdEntries.filter((_, i) => i !== index).length > 0
              ? prev.militaryIdEntries.filter((_, i) => i !== index)
              : [createEmptyMilitaryIdEntry()],
        }));
      },
    });
  };

  const loadAddressSuggestions = async (index: number, query: string) => {
    const raw = query.trim();
    if (raw.length < 3) {
      setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: [] }));
      return;
    }
    try {
      const suggestions = await apiService.suggestClientAddresses(raw, 7);
      setAddressSuggestionsByIndex((prev) => ({
        ...prev,
        [index]: suggestions ?? [],
      }));
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(
          error,
          "Не удалось получить подсказки адресов",
        ),
        "DaData",
      );
    }
  };

  const scheduleAddressSuggestions = (index: number, query: string) => {
    const timers = addressSuggestionTimersRef.current;
    if (timers[index]) window.clearTimeout(timers[index]);
    timers[index] = window.setTimeout(() => {
      void loadAddressSuggestions(index, query);
    }, 350);
  };

  const fillAddressEntryByDaData = async (
    index: number,
    rawInput?: string,
    options?: { silent?: boolean },
  ) => {
    const entry = clientFormState.addressEntries[index];
    const raw = (rawInput || entry?.fullAddress || "").trim();
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
            postalCode: cleaned.postalCode || "",
            country: cleaned.country || item.country || "",
            region: cleaned.regionWithType || "",
            district: cleaned.areaWithType || "",
            city: cleaned.cityWithType || "",
            settlement: cleaned.settlementWithType || "",
            street: cleaned.streetWithType || "",
            house: cleaned.house || "",
            flat: cleaned.flat || "",
            fiasId: cleaned.fiasId || "",
            kladrId: cleaned.kladrId || "",
            geoLat: cleaned.geoLat || "",
            geoLon: cleaned.geoLon || "",
          };
          return {
            ...updated,
            fullAddress: chooseBestDisplayAddress(
              updated,
              item.fullAddress,
              cleaned.result,
              raw,
            ),
          };
        }),
      }));
    } catch (error) {
      try {
        const suggestions = await apiService.suggestClientAddresses(raw, 1);
        const fallbackSuggestion = suggestions?.[0];
        if (fallbackSuggestion) {
          const fallbackAddress = (
            fallbackSuggestion.unrestrictedValue ||
            fallbackSuggestion.value ||
            raw
          ).trim();
          setClientFormState((prev) => ({
            ...prev,
            addressEntries: prev.addressEntries.map((item, i) => {
              if (i !== index) return item;
              const updated = {
                ...item,
                sourceQuery: raw,
                postalCode: fallbackSuggestion.postalCode || item.postalCode,
                country: item.country || "",
                region: fallbackSuggestion.regionWithType || item.region,
                district: fallbackSuggestion.areaWithType || item.district,
                city: fallbackSuggestion.cityWithType || item.city,
                settlement:
                  fallbackSuggestion.settlementWithType || item.settlement,
                street: fallbackSuggestion.streetWithType || item.street,
                house: fallbackSuggestion.house || item.house,
                flat: fallbackSuggestion.flat || item.flat,
                fiasId: fallbackSuggestion.fiasId || item.fiasId,
                kladrId: fallbackSuggestion.kladrId || item.kladrId,
                geoLat: fallbackSuggestion.geoLat || item.geoLat,
                geoLon: fallbackSuggestion.geoLon || item.geoLon,
              };
              return {
                ...updated,
                fullAddress: chooseBestDisplayAddress(
                  updated,
                  fallbackAddress,
                  item.fullAddress,
                  raw,
                ),
              };
            }),
          }));
          return;
        }
      } catch {
        // ignore and show main error below
      }

      if (!options?.silent) {
        toast.error(
          extractDisplayErrorMessage(error, "Не удалось найти адрес"),
          "DaData",
        );
        return;
      }
      throw error;
    }
  };

  const applyAddressSuggestion = async (
    index: number,
    suggestion: DadataAddressSuggestion,
  ) => {
    const selected = (
      suggestion.unrestrictedValue ||
      suggestion.value ||
      ""
    ).trim();
    if (!selected) return;

    // Быстро заполняем минимум из suggestion даже если clean endpoint временно недоступен.
    setClientFormState((prev) => ({
      ...prev,
      addressEntries: prev.addressEntries.map((item, i) => {
        if (i !== index) return item;
        const updated = {
          ...item,
          sourceQuery: selected,
          postalCode: suggestion.postalCode || item.postalCode,
          country: item.country || "",
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
        };
        return {
          ...updated,
          fullAddress: chooseBestDisplayAddress(
            updated,
            selected,
            item.fullAddress,
          ),
        };
      }),
    }));

    setAddressSuggestionsByIndex((prev) => ({ ...prev, [index]: [] }));

    try {
      await fillAddressEntryByDaData(index, selected, { silent: true });
    } catch {
      // fallback уже применён выше через suggestion
    }
  };

  const handleClientFormSubmit = async (
    event: React.FormEvent<HTMLFormElement>,
  ) => {
    event.preventDefault();
    if (!canEditClientProfile) {
      showPermissionWarning("Недостаточно прав для редактирования клиента.");
      return;
    }
    setClientFormSaving(true);
    try {
      const primaryPassport =
        clientFormState.passportEntries[0] ?? createEmptyPassportEntry();
      const normalizedAddressEntries = ensureSinglePrimaryAddressEntryList(
        clientFormState.addressEntries,
      );
      const primaryAddressEntry =
        normalizedAddressEntries.find((entry) => entry.isPrimary) ??
        normalizedAddressEntries[0] ??
        createEmptyAddressEntry(true);
      const registrationEntry =
        clientFormState.addressEntries.find((entry) => {
          const type = entry.type.toLowerCase();
          return (
            type.includes("рег") ||
            type.includes("registration") ||
            entry.additionalTypes.includes("registration")
          );
        }) ??
        clientFormState.addressEntries[0] ??
        createEmptyAddressEntry();
      const actualEntry =
        clientFormState.addressEntries.find((entry) => {
          const type = entry.type.toLowerCase();
          return (
            type.includes("факт") ||
            type.includes("actual") ||
            entry.additionalTypes.includes("actual")
          );
        }) ??
        clientFormState.addressEntries[1] ??
        createEmptyAddressEntry();
      const profile: ClientExtendedProfile = {
        kind: clientFormState.kind,
        lastName: clientFormState.lastName,
        firstName: clientFormState.firstName,
        middleName: clientFormState.middleName,
        gender: clientFormState.gender,
        birthDate: clientFormState.birthDate,
        birthPlacePassport:
          primaryPassport.birthPlacePassport ||
          clientFormState.birthPlacePassport,
        birthPlaceFias:
          primaryPassport.birthPlaceFias || clientFormState.birthPlaceFias,
        citizenship: clientFormState.citizenship,
        inn: clientFormState.inn,
        snils: clientFormState.snils,
        passportSeries:
          primaryPassport.series || clientFormState.passportSeries,
        passportNumber:
          primaryPassport.number || clientFormState.passportNumber,
        passportIssuedBy:
          primaryPassport.issuedBy || clientFormState.passportIssuedBy,
        passportIssueDate:
          primaryPassport.issueDate || clientFormState.passportIssueDate,
        passportDepartmentCode:
          primaryPassport.departmentCode ||
          clientFormState.passportDepartmentCode,
        education: clientFormState.education,
        professions: clientFormState.professions,
        workplaces: clientFormState.workplaces,
        maritalStatus: clientFormState.maritalStatus,
        relatives: clientFormState.relatives,
        registrationAddress:
          registrationEntry.fullAddress || clientFormState.registrationAddress,
        registrationAddressPostalCode:
          registrationEntry.postalCode ||
          clientFormState.registrationAddressPostalCode,
        registrationAddressFiasId:
          registrationEntry.fiasId || clientFormState.registrationAddressFiasId,
        registrationAddressKladrId:
          registrationEntry.kladrId ||
          clientFormState.registrationAddressKladrId,
        registrationAddressGeoLat:
          registrationEntry.geoLat || clientFormState.registrationAddressGeoLat,
        registrationAddressGeoLon:
          registrationEntry.geoLon || clientFormState.registrationAddressGeoLon,
        actualAddress: actualEntry.fullAddress || clientFormState.actualAddress,
        actualAddressPostalCode:
          actualEntry.postalCode || clientFormState.actualAddressPostalCode,
        actualAddressFiasId:
          actualEntry.fiasId || clientFormState.actualAddressFiasId,
        actualAddressKladrId:
          actualEntry.kladrId || clientFormState.actualAddressKladrId,
        actualAddressGeoLat:
          actualEntry.geoLat || clientFormState.actualAddressGeoLat,
        actualAddressGeoLon:
          actualEntry.geoLon || clientFormState.actualAddressGeoLon,
        actualAddressMatchesRegistration:
          clientFormState.actualAddressMatchesRegistration,
        phones: clientFormState.phones,
        emails: clientFormState.emails,
        addresses: normalizedAddressEntries.map((entry) => entry.fullAddress),
        passports: clientFormState.passports,
        birthCertificates: clientFormState.birthCertificates,
        militaryIds: clientFormState.militaryIds,
        birthCertificateEntries: clientFormState.birthCertificateEntries,
        militaryIdEntries: clientFormState.militaryIdEntries,
        passportEntries: clientFormState.passportEntries,
        addressEntries: normalizedAddressEntries,
        comment: clientFormState.comment,
        legacyNotes: clientFormState.legacyNotes,
      };
      const normalizedPhones = normalizeList(profile.phones);
      const normalizedEmails = normalizeList(profile.emails);
      const normalizedAddresses = normalizeList(profile.addresses);
      const registrationAddress =
        profile.registrationAddress.trim() || normalizedAddresses[0] || "";
      const actualAddress = profile.actualAddressMatchesRegistration
        ? registrationAddress
        : profile.actualAddress.trim();
      const primaryAddress =
        primaryAddressEntry.fullAddress.trim() ||
        registrationAddress ||
        actualAddress ||
        normalizedAddresses[0] ||
        "";
      if (profile.kind === "legal" && !clientFormState.legalEntityId) {
        toast.error(
          "Для карточки юридического лица выберите юридическое лицо из списка.",
          "Проверка данных",
        );
        return;
      }
      try {
        await handleUpdateClient({
          name: buildClientDisplayName(
            profile,
            selectedLegalEntity?.shortName ??
              selectedLegalEntity?.fullName ??
              undefined,
          ),
          email: normalizedEmails[0] ?? "",
          phone: normalizedPhones[0] ?? "",
          company:
            profile.kind === "legal"
              ? (selectedLegalEntity?.shortName ?? "Юридическое лицо")
              : "",
          address: primaryAddress,
          notes: `${CLIENT_PROFILE_NOTES_PREFIX}${serializeClientExtendedProfile(profile)}`,
          isActive: clientFormState.isActive,
          legalEntityId:
            profile.kind === "legal" && clientFormState.legalEntityId
              ? Number(clientFormState.legalEntityId)
              : null,
          responsibleUserId: clientFormState.responsibleUserId
            ? Number(clientFormState.responsibleUserId)
            : null,
          clientStatusId: clientFormState.clientStatusId
            ? Number(clientFormState.clientStatusId)
            : null,
          isClientAndSupplier: clientFormState.isClientAndSupplier,
        });
        toast.success(
          "Изменения карточки клиента сохранены.",
          "Клиент обновлен",
        );
        setClientProfileEditing(false);
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
          <h1 className="mt-4 text-2xl font-semibold text-red-700">
            Доступ к данным клиента ограничен
          </h1>
          <p className="mt-2 text-sm text-red-600">
            Недостаточно прав для просмотра разделов этой карточки. Обратитесь к
            администратору.
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

  const profileCardTitles = {
    contacts: "Контактные данные",
    documents: "Документы",
  };
  const profileCardGroupValues: Record<string, unknown[]> = {
    main: [...clientFormState.phones, ...clientFormState.emails],
    addresses: clientFormState.addressEntries,
    passports: clientFormState.passportEntries,
    birthCertificates: clientFormState.birthCertificateEntries,
    militaryIds: clientFormState.militaryIdEntries,
  };
  const profileCardRequiredKeys: Record<string, string[]> = {
    main: ["Phones", "Emails"],
    addresses: ["Addresses"],
    passports: ["Passports"],
    birthCertificates: ["BirthCertificates"],
    militaryIds: ["MilitaryIds"],
  };
  const profileCardTone = (groups: string[]) =>
    groups.some((group) =>
      hasMissingRequiredList(profileCardRequiredKeys[group], profileCardGroupValues[group] ?? []),
    )
      ? "text-red-700"
      : "text-gray-900";

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="w-full min-w-0 p-3 lg:p-4">
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
            title={clientName}
          >
            <span className="truncate">{clientName}</span>
            {clientData?.responsibleUserName && (
              <span className="inline-flex items-center rounded-full border border-slate-200 bg-slate-100 px-3 py-1 text-xs font-medium text-slate-700">
                Ответственный: {clientData.responsibleUserName}
              </span>
            )}
            <ClientStatusBadge status={clientData?.clientStatus} />
          </h1>
          {(() => {
            const isIndividual = clientFormState.kind === "individual";
            const birth = isIndividual ? clientFormState.birthDate : "";
            const age = getClientAgeLabel(birth, profileTimeZoneId);
            const items = [
              birth
                ? `${formatClientBirthDate(birth)}${age ? ` (${age})` : ""}`
                : "",
              clientData?.phone ?? "",
              clientData?.email ?? "",
              clientData?.address ?? "",
            ].filter((item) => item.trim());
            return items.length ? (
              <div
                className="basis-full text-sm text-gray-600 flex flex-wrap gap-x-4 gap-y-1"
                aria-label="Краткие сведения о клиенте"
              >
                {items.map((item) => (
                  <span key={item}>{item}</span>
                ))}
              </div>
            ) : null;
          })()}
          <div className="ml-auto flex flex-wrap items-center gap-2">
            {canViewClientFinancialSummary ? (
              <button
                type="button"
                onClick={() => setClientBalanceExpanded(true)}
                className="inline-flex min-h-10 items-center gap-3 rounded-lg border border-blue-200 bg-white px-3 py-1 text-sm font-semibold text-blue-800 shadow-sm transition-colors hover:bg-blue-50"
                title="История баланса"
                aria-label="История баланса"
              >
                <span className="flex flex-col leading-tight">
                  <span className="text-[11px] font-bold uppercase text-rose-500">
                    Долг
                  </span>
                  <span className="text-sm font-semibold text-rose-700">
                    {clientDebtLabel}
                  </span>
                </span>
                <span className="h-6 w-px bg-blue-100" aria-hidden="true" />
                <span className="flex flex-col leading-tight">
                  <span className="text-[11px] font-bold uppercase text-emerald-500">
                    Аванс
                  </span>
                  <span className="text-sm font-semibold text-emerald-700">
                    {clientAdvanceLabel}
                  </span>
                </span>
              </button>
            ) : null}
            {canViewAudit ? (
              <button
                type="button"
                onClick={() => setClientAuditOpen(true)}
                className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 shadow-sm transition-colors hover:bg-slate-50"
                title="История клиента"
                aria-label="История клиента"
              >
                <History className="h-5 w-5" />
              </button>
            ) : null}
          </div>
        </div>

        <div className="flex flex-col gap-3 xl:flex-row">
          <aside className="order-first shrink-0 xl:order-last xl:w-60">
        <div className="flex flex-col gap-3">
          <div className="flex flex-row flex-wrap gap-2 xl:flex-col xl:gap-1" role="navigation" aria-label="Разделы клиента">
            {visibleSections.map((section) => {
              const meta = SECTION_META[section];
              const isActive = activeSection === section;
              return (
                <button
                  key={section}
                  type="button"
                  onClick={() => setActiveSection(section)}
                  aria-current={isActive ? "page" : undefined}
                  className={`px-3 py-2 text-sm font-medium rounded-lg border transition-colors xl:w-full xl:text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 ${
                    isActive
                      ? "bg-emerald-600 text-white border-emerald-600 shadow-sm"
                      : "bg-white text-gray-600 border-gray-200 hover:bg-gray-50"
                  }`}
                >
                  {section === "profile" && clientFormState.kind === "individual"
                    ? "Личные данные"
                    : meta.label}
                </button>
              );
            })}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {casePermissions.canView && casePermissions.canCreate && (
              <div className="flex flex-col items-center gap-1">
                <button
                  type="button"
                  onClick={openAddCase}
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-emerald-600 text-white shadow-sm transition-colors hover:bg-emerald-700"
                  title={addCaseLabel}
                  aria-label={addCaseLabel}
                >
                  <FileCheck2 size={18} />
                </button>
                <span className="text-[11px] font-bold leading-none text-slate-700">
                  {SECTION_META.cases.label}
                </span>
              </div>
            )}
            {paymentPermissions.canView && paymentPermissions.canCreate && (
              <div className="flex flex-col items-center gap-1">
                <button
                  type="button"
                  onClick={openAddPayment}
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-blue-600 text-white shadow-sm transition-colors hover:bg-blue-700"
                  title={SECTION_META.payments.actionLabel}
                  aria-label={SECTION_META.payments.actionLabel}
                >
                  <WalletCards size={18} />
                </button>
                <span className="text-[11px] font-bold leading-none text-slate-700">
                  {SECTION_META.payments.label}
                </span>
              </div>
            )}
            {accountPermissions.canView && accountPermissions.canCreate && (
              <div className="flex flex-col items-center gap-1">
                <button
                  type="button"
                  onClick={openInvoiceCreate}
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-teal-600 text-white shadow-sm transition-colors hover:bg-teal-700"
                  title={SECTION_META.accounts.actionLabel}
                  aria-label={SECTION_META.accounts.actionLabel}
                >
                  <Send size={18} />
                </button>
                <span className="text-[11px] font-bold leading-none text-slate-700">
                  {SECTION_META.accounts.label}
                </span>
              </div>
            )}
            {actPermissions.canView && actPermissions.canCreate && (
              <div className="flex flex-col items-center gap-1">
                <button
                  type="button"
                  onClick={openAddAct}
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-amber-600 text-white shadow-sm transition-colors hover:bg-amber-700"
                  title={SECTION_META.acts.actionLabel}
                  aria-label={SECTION_META.acts.actionLabel}
                >
                  <FileSignature size={18} />
                </button>
                <span className="text-[11px] font-bold leading-none text-slate-700">
                  {SECTION_META.acts.label}
                </span>
              </div>
            )}
            {contractPermissions.canView && contractPermissions.canCreate && (
              <div className="flex flex-col items-center gap-1">
                <button
                  type="button"
                  onClick={openContractCreate}
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-indigo-600 text-white shadow-sm transition-colors hover:bg-indigo-700"
                  title={SECTION_META.contracts.actionLabel}
                  aria-label={SECTION_META.contracts.actionLabel}
                >
                  <Calculator size={18} />
                </button>
                <span className="text-[11px] font-bold leading-none text-slate-700">
                  {SECTION_META.contracts.label}
                </span>
              </div>
            )}
          </div>
                  <dl className="rounded-lg border border-gray-200 bg-white p-3 text-sm text-gray-600 space-y-2" aria-label="Служебные сведения">
            <div>
              <dt className="text-xs text-gray-500">№ клиента</dt>
              <dd className="font-semibold text-gray-900">{clientData?.id ?? clientId}</dd>
            </div>
            {clientData?.createdAt ? (
              <div>
                <dt className="text-xs text-gray-500">Дата создания</dt>
                <dd>{formatClientDate(clientData.createdAt, profileTimeZoneId)}</dd>
              </div>
            ) : null}
            {clientData?.responsibleUserName ? (
              <div>
                <dt className="text-xs text-gray-500">Ответственный</dt>
                <dd>{clientData.responsibleUserName}</dd>
              </div>
            ) : null}
          </dl>
        </div>

          </aside>
          <div className="min-w-0 flex-1">
        {activeSection === "profile" ? (
          <div className="bg-white rounded-xl shadow-sm border border-gray-100 mb-3 p-3">
            <div className="flex flex-wrap items-start justify-between gap-3 mb-4">
              <div>
                <h2 className="text-lg font-semibold text-gray-900">
                  Карточка клиента
                </h2>
                <p className="text-sm text-gray-500">
                  Обновляйте контактные данные клиента и юридическую информацию.
                </p>
              </div>
              <div className="flex flex-wrap items-center gap-2">
                {clientPermissions.canEdit ? (
                  clientProfileEditing ? (
                    <button
                      type="button"
                      onClick={cancelClientProfileEditing}
                      disabled={clientFormSaving}
                      className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 shadow-sm transition-colors hover:bg-slate-50 disabled:opacity-60"
                    >
                      <X className="h-4 w-4" />
                      Отмена
                    </button>
                  ) : (
                    <button
                      type="button"
                      onClick={() => setClientProfileEditing(true)}
                      className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-2 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-emerald-700"
                    >
                      <Edit className="h-4 w-4" />
                      Редактировать
                    </button>
                  )
                ) : null}
              </div>
              <div className="w-full max-w-2xl grid grid-cols-1 gap-4 md:grid-cols-2">
                <div>
                  <LookupSelect
                    label={"Ответственный" + requiredMark("ResponsibleUserId")}
                    value={clientFormState.responsibleUserId}
                    onChange={(value) => {
                      setClientFormState((prev) => ({
                        ...prev,
                        responsibleUserId: value,
                      }));
                    }}
                    options={clientResponsibleLookupOptions}
                    placeholder="Не указан"
                    title="Выбор ответственного"
                    searchPlaceholder="Поиск ответственного"
                    emptyOptionLabel="Не указан"
                    disabled={!canManageClientResponsibleInProfile}
                    buttonClassName={`disabled:bg-gray-100 ${requiredInputClass("ResponsibleUserId", clientFormState.responsibleUserId)}`}
                    helperText={
                      canManageClientResponsible
                        ? "По умолчанию подставляется пользователь, который создал клиента."
                        : "Назначается автоматически при создании клиента. Изменение доступно администратору."
                    }
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-2">
                    {(t("clientStatus") || "Статус клиента") +
                      requiredMark("ClientStatusId")}
                  </label>
                  <SystemSelect
                    name="clientStatusId"
                    value={clientFormState.clientStatusId}
                    onChange={handleClientFormChange}
                    disabled={!canEditClientProfile}
                    className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass("ClientStatusId", clientFormState.clientStatusId)}`}
                  >
                    <option value="">
                      {t("clientStatusNotSelected") || "Без статуса"}
                    </option>
                    {sortedClientStatuses.map((status) => (
                      <option key={status.id} value={status.id}>
                        {status.name}
                      </option>
                    ))}
                  </SystemSelect>
                  {selectedClientStatus?.description && (
                    <p className="mt-2 text-xs text-gray-500">
                      {selectedClientStatus.description}
                    </p>
                  )}
                </div>
                <label className="md:col-span-2 flex items-start gap-3 rounded-lg border border-emerald-100 bg-emerald-50/60 px-3 py-2 text-sm text-gray-700">
                  <SystemCheckbox
                    type="checkbox"
                    name="isClientAndSupplier"
                    checked={clientFormState.isClientAndSupplier}
                    onChange={handleClientFormChange}
                    disabled={!canEditClientProfile}
                    className="mt-1 h-4 w-4 rounded border-gray-300 text-emerald-600 focus:ring-emerald-500 disabled:opacity-60"
                  />
                  <span>
                    <span className="block font-medium text-gray-800">
                      Является одновременно клиентом и поставщиком
                    </span>
                    <span className="mt-0.5 block text-xs text-gray-500">
                      При включении контрагент доступен в обоих списках выбора.
                    </span>
                  </span>
                </label>
              </div>
            </div>
            {!clientPermissions.canEdit && (
              <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                Недостаточно прав для редактирования клиента. Доступен только
                просмотр.
              </div>
            )}
            <form
              onSubmit={handleClientFormSubmit}
              className="grid gap-4 md:grid-cols-2"
            >
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  Тип карточки
                </label>
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                  <button
                    type="button"
                    onClick={() =>
                      setClientFormState((prev) => ({
                        ...prev,
                        kind: "individual",
                        legalEntityId: "",
                      }))
                    }
                    disabled={!canEditClientProfile}
                    className={`rounded-lg border px-3 py-2 text-sm ${clientFormState.kind === "individual" ? "border-emerald-500 bg-emerald-50 text-emerald-700" : "border-gray-300 bg-white text-gray-700"} disabled:bg-gray-100`}
                  >
                    Физическое лицо
                  </button>
                  <button
                    type="button"
                    onClick={() =>
                      setClientFormState((prev) => ({ ...prev, kind: "legal" }))
                    }
                    disabled={!canEditClientProfile}
                    className={`rounded-lg border px-3 py-2 text-sm ${clientFormState.kind === "legal" ? "border-emerald-500 bg-emerald-50 text-emerald-700" : "border-gray-300 bg-white text-gray-700"} disabled:bg-gray-100`}
                  >
                    Юридическое лицо
                  </button>
                </div>
              </div>

              {clientFormState.kind === "individual" ? (
                <>
                  <section
                    className={`md:col-span-2 rounded-xl border ${clientProfileEditing ? "border-gray-200 bg-gray-50/50 p-4" : "border-blue-100 bg-white p-3"}`}
                    aria-labelledby="client-profile-card-main-info"
                  >
                    <h3
                      id="client-profile-card-main-info"
                      className={clientProfileEditing ? "mb-3 text-base font-semibold text-gray-900" : "mb-3 flex items-center gap-2 text-base font-semibold text-blue-700"}
                    >
                      {!clientProfileEditing && <UserRound className="h-5 w-5 shrink-0" aria-hidden="true" />}
                      Основная информация
                    </h3>
                  {clientProfileEditing ? (
                    <>
                  <div className="grid gap-3 grid-cols-[repeat(auto-fit,minmax(min(100%,150px),1fr))]">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {"Фамилия" + requiredMark("LastName")}
                      </label>
                      <input
                        type="text"
                        name="lastName"
                        value={clientFormState.lastName}
                        onChange={handleClientFormChange}
                        required
                        disabled={!canEditClientProfile}
                        className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass("LastName", clientFormState.lastName)}`}
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {"Имя" + requiredMark("FirstName")}
                      </label>
                      <input
                        type="text"
                        name="firstName"
                        value={clientFormState.firstName}
                        onChange={handleClientFormChange}
                        required
                        disabled={!canEditClientProfile}
                        className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass("FirstName", clientFormState.firstName)}`}
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {"Отчество" + requiredMark("MiddleName")}
                      </label>
                      <input
                        type="text"
                        name="middleName"
                        value={clientFormState.middleName}
                        onChange={handleClientFormChange}
                        disabled={!canEditClientProfile}
                        className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass("MiddleName", clientFormState.middleName)}`}
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        Пол
                      </label>
                      <SystemSelect
                        name="gender"
                        value={clientFormState.gender}
                        onChange={handleClientFormChange}
                        disabled={!canEditClientProfile}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100"
                      >
                        <option value="">Не указан</option>
                        <option value="male">Мужской</option>
                        <option value="female">Женский</option>
                      </SystemSelect>
                    </div>
                  </div>

                  <div className="md:col-span-2 grid gap-3 md:grid-cols-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {"ИНН" + requiredMark("Inn")}
                      </label>
                      <input
                        type="text"
                        name="inn"
                        value={clientFormState.inn}
                        onChange={handleClientFormChange}
                        disabled={!canEditClientProfile}
                        className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass("Inn", clientFormState.inn)}`}
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {"СНИЛС" + requiredMark("Snils")}
                      </label>
                      <input
                        type="text"
                        name="snils"
                        value={clientFormState.snils}
                        onChange={handleClientFormChange}
                        disabled={!canEditClientProfile}
                        className={`w-full px-3 py-2 border rounded-lg focus:ring-2 disabled:bg-gray-100 ${requiredInputClass("Snils", clientFormState.snils)}`}
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        Дата рождения
                      </label>
                      <SystemDateInput
                        type="date"
                        name="birthDate"
                        value={clientFormState.birthDate}
                        onChange={handleClientFormChange}
                        disabled={!canEditClientProfile}
                        className={DATE_INPUT_CLASS}
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        Семейное положение
                      </label>
                      <SystemSelect
                        name="maritalStatus"
                        value={clientFormState.maritalStatus}
                        onChange={handleClientFormChange}
                        disabled={!canEditClientProfile}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100"
                      >
                        <option value="">Не указано</option>
                        {MARITAL_STATUS_OPTIONS.map((item) => (
                          <option key={item.value} value={item.value}>
                            {item.label}
                          </option>
                        ))}
                      </SystemSelect>
                    </div>
                  </div>
                    </>
                  ) : (
                    <dl className="grid gap-x-6 md:grid-cols-2">
                      {[
                        {
                          label: "ФИО",
                          value: [clientFormState.lastName, clientFormState.firstName, clientFormState.middleName]
                            .filter((value) => value.trim())
                            .join(" "),
                          fullWidth: true,
                        },
                        {
                          label: "Дата рождения",
                          value: clientFormState.birthDate ? formatClientBirthDate(clientFormState.birthDate) : "",
                        },
                        {
                          label: "Пол",
                          value: clientFormState.gender === "male" ? "Мужской" : clientFormState.gender === "female" ? "Женский" : clientFormState.gender,
                        },
                        {
                          label: "Семейное положение",
                          value: MARITAL_STATUS_OPTIONS.find((option) => option.value === clientFormState.maritalStatus)?.label ?? clientFormState.maritalStatus,
                        },
                        { label: "ИНН", value: clientFormState.inn },
                        { label: "СНИЛС", value: clientFormState.snils },
                      ].map((field) => (
                        <div
                          key={field.label}
                          className={`grid min-w-0 grid-cols-[minmax(0,1fr)_minmax(0,2fr)] items-start gap-3 border-t border-blue-100 py-2 first:border-t-0 ${field.fullWidth ? "md:col-span-2" : ""}`}
                        >
                          <dt className="min-w-0 text-sm text-gray-500">{field.label}</dt>
                          <dd className="min-w-0 text-sm text-gray-900 [overflow-wrap:anywhere]">
                            {field.value || "Не указано"}
                          </dd>
                        </div>
                      ))}
                    </dl>
                  )}
                  </section>
                </>
              ) : (
                <div className="md:col-span-2">
                  <div className="mb-2 flex flex-wrap items-center justify-end gap-2">
                    <button
                      type="button"
                      onClick={openLegalEntityCreateModal}
                      disabled={!canEditClientProfile}
                      className="inline-flex items-center gap-1.5 rounded-lg border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm font-medium text-emerald-700 transition-colors hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      <Plus className="h-4 w-4" />
                      Создать юрлицо
                    </button>
                    <button
                      type="button"
                      onClick={() => void openLegalEntityEditModal()}
                      disabled={!canEditClientProfile || !selectedLegalEntity}
                      className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      <Edit className="h-4 w-4" />
                      Реквизиты
                    </button>
                  </div>
                  <label className="block text-sm font-medium text-gray-700 mb-2">
                    {t("legalEntity") || "Юридическое лицо"}
                  </label>
                  <input
                    type="text"
                    value={legalEntityLookupSearch}
                    onChange={(event) =>
                      setLegalEntityLookupSearch(event.target.value)
                    }
                    disabled={!canEditClientProfile}
                    placeholder="Поиск по названию, ИНН, КПП или ОГРН"
                    aria-label="Поиск юридического лица клиента"
                    className="mb-2 w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100"
                  />
                  <SystemSelect
                    name="legalEntityId"
                    value={clientFormState.legalEntityId}
                    onChange={handleClientFormChange}
                    disabled={!canEditClientProfile}
                    className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100"
                  >
                    <option value="">Выберите юридическое лицо</option>
                    {sortedLegalEntities.map((entity) => (
                      <option key={entity.id} value={entity.id}>
                        {entity.shortName}
                        {entity.inn ? ` · ${entity.inn}` : ""}
                      </option>
                    ))}
                  </SystemSelect>
                  <div className="mt-1 text-xs text-slate-500">
                    {legalEntityLookupLoading
                      ? "Загрузка юрлиц..."
                      : `Найдено: ${legalEntityLookupTotal}`}
                  </div>
                  <div className="mt-2 flex flex-wrap gap-2">
                    <button
                      type="button"
                      onClick={() => {
                        setLegalEntityPromptError(null);
                        setLegalEntityDraft({ shortName: "", inn: "" });
                        setLegalEntityPromptOpen(true);
                      }}
                      disabled={!canEditClientProfile}
                      className="inline-flex items-center justify-center rounded-lg border border-gray-300 px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      Быстрое создание
                    </button>
                  </div>
                  {selectedLegalEntity ? (
                    <div className="mt-3 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700">
                      <div className="font-medium text-slate-900">
                        {selectedLegalEntity.fullName || selectedLegalEntity.shortName}
                      </div>
                      <div className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-600">
                        {selectedLegalEntity.inn ? <span>ИНН: {selectedLegalEntity.inn}</span> : null}
                        {selectedLegalEntity.kpp ? <span>КПП: {selectedLegalEntity.kpp}</span> : null}
                        {selectedLegalEntity.ogrn ? <span>ОГРН: {selectedLegalEntity.ogrn}</span> : null}
                      </div>
                    </div>
                  ) : null}
                </div>
              )}

              {clientFormState.kind === "individual" && (
                <section
                  className="md:col-span-2 rounded-xl border border-gray-200 bg-gray-50/50 p-4"
                  aria-labelledby="client-profile-card-contacts"
                >
                  <h3
                    id="client-profile-card-contacts"
                    className={`mb-3 text-base font-semibold ${profileCardTone(["main", "addresses"])}`}
                  >
                    {profileCardTitles.contacts}
                  </h3>
                  <div className="grid gap-4 md:grid-cols-2">
                      {clientProfileEditing ? (
                          <div className="md:col-span-2 grid gap-4 md:grid-cols-2">
                            {(["phones", "emails"] as const).map((field) => (
                              <div key={field}>
                                <label className="block text-sm font-medium text-gray-700 mb-2">
                                  {field === "phones" ? "Телефоны" : "Email"}
                                </label>
                                <div className="space-y-2">
                                  {clientFormState[field].map((value, index) => (
                                    <div
                                      key={`${field}-${index}`}
                                      className="flex gap-2"
                                    >
                                      <input
                                        type={field === "emails" ? "email" : "text"}
                                        value={value}
                                        onChange={(event) =>
                                          updateListField(
                                            field,
                                            index,
                                            event.target.value,
                                          )
                                        }
                                        disabled={!canEditClientProfile}
                                        className="flex-1 px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 disabled:bg-gray-100"
                                      />
                                      <button
                                        type="button"
                                        onClick={() => {
                                          void removeListFieldRow(field, index);
                                        }}
                                        disabled={!canEditClientProfile}
                                        className={PROFILE_DELETE_ICON_BUTTON_CLASS}
                                        title={
                                          field === "phones"
                                            ? "Удалить телефон"
                                            : "Удалить email"
                                        }
                                        aria-label={
                                          field === "phones"
                                            ? "Удалить телефон"
                                            : "Удалить email"
                                        }
                                      >
                                        <Trash2 className="h-4 w-4" />
                                      </button>
                                    </div>
                                  ))}
                                  <button
                                    type="button"
                                    onClick={() => addListFieldRow(field)}
                                    disabled={!canEditClientProfile}
                                    className="text-sm text-emerald-600"
                                  >
                                    + Добавить
                                  </button>
                                </div>
                              </div>
                            ))}
                          </div>
                        ) : (
                          <dl className="md:col-span-2 grid gap-3 md:grid-cols-2" aria-label="Телефоны и email">
                            {(["phones", "emails"] as const).map((field) => (
                              <div key={field} className="min-w-0">
                                <dt className="mb-2 text-sm text-gray-500">{field === "phones" ? "Телефоны" : "Email"}</dt>
                                <dd className="min-w-0 text-sm text-gray-900">
                                  <ul className="space-y-1">
                                    {(clientFormState[field].length > 0 ? clientFormState[field] : [""]).map((value, index) => (
                                      <li key={`${field}-${index}`} className="[overflow-wrap:anywhere]">{value || "Не указано"}</li>
                                    ))}
                                  </ul>
                                </dd>
                              </div>
                            ))}
                          </dl>
                        )}
                      {clientFormState.kind === "individual" && (
                          <ClientAddressEntriesForm
                            entries={clientFormState.addressEntries}
                            canEdit={canEditClientProfile}
                            textReadOnly={!clientProfileEditing}
                            addressTypeOptions={ADDRESS_TYPE_OPTIONS}
                            suggestionsByIndex={addressSuggestionsByIndex}
                            onUpdateEntry={updateAddressFormEntry}
                            onSetPrimary={setPrimaryAddressFormEntry}
                            onRemoveEntry={removeAddressEntry}
                            onAddEntry={addAddressEntry}
                            onToggleAdditionalType={toggleAddressAdditionalType}
                            onScheduleSuggestions={scheduleAddressSuggestions}
                            onFindAddress={(index) => {
                              void fillAddressEntryByDaData(index);
                            }}
                            onApplySuggestion={applyAddressSuggestion}
                          />
                        )}
                  </div>
                </section>
              )}

              {clientFormState.kind === "individual" && (
                <section
                  className="md:col-span-2 rounded-xl border border-gray-200 bg-gray-50/50 p-4"
                  aria-labelledby="client-profile-card-documents"
                >
                  <h3
                    id="client-profile-card-documents"
                    className={`mb-3 text-base font-semibold ${profileCardTone(["passports", "birthCertificates", "militaryIds"])}`}
                  >
                    {profileCardTitles.documents}
                  </h3>
                  <div className="grid gap-4 md:grid-cols-2">
                      {clientFormState.kind === "individual" && (
                          <ClientPassportEntriesForm
                            entries={clientFormState.passportEntries}
                            canEdit={canEditClientProfile}
                            textReadOnly={!clientProfileEditing}
                            dateInputClassName={DATE_INPUT_CLASS}
                            onUpdateEntry={updatePassportEntry}
                            onRemoveEntry={removePassportEntry}
                            onAddEntry={addPassportEntry}
                          />
                        )}
                      {clientFormState.kind === "individual" && (
                        !clientProfileEditing ? (
                          <ProfileDocumentReadList title="Свидетельства о рождении" entries={clientFormState.birthCertificateEntries.map((doc) => [
                            { label: 'Серия', value: doc.series }, { label: 'Номер', value: doc.number },
                            { label: 'Дата выдачи', value: toRuDate(doc.issueDate) }, { label: '№ актовой записи', value: doc.actRecordNumber },
                            { label: 'ФИО ребёнка', value: doc.childFullName }, { label: 'Кем выдано', value: doc.issuedBy },
                          ])} />
                        ) : (
                          <div className="md:col-span-2 space-y-4">
                            <div>
                              <div className="mb-2 flex items-center justify-between">
                                <label className="block text-sm font-medium text-gray-700">
                                  Свидетельства о рождении
                                </label>
                                <button
                                  type="button"
                                  onClick={addBirthCertificateEntry}
                                  disabled={!canEditClientProfile}
                                  className="text-sm text-emerald-600"
                                >
                                  + Добавить свидетельство
                                </button>
                              </div>
                              <div className="space-y-2">
                                {clientFormState.birthCertificateEntries.map(
                                  (doc, index) => (
                                    <div
                                      key={`birth-certificate-${index}`}
                                      className="rounded-lg border border-gray-200 p-3"
                                    >
                                      <div className="grid items-start gap-2 md:grid-cols-12">
                                        <input
                                          value={doc.series}
                                          onChange={(e) =>
                                            updateBirthCertificateEntry(
                                              index,
                                              "series",
                                              e.target.value,
                                            )
                                          }
                                          placeholder="Серия"
                                          disabled={!canEditClientProfile}
                                          className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3"
                                        />
                                        <input
                                          value={doc.number}
                                          onChange={(e) =>
                                            updateBirthCertificateEntry(
                                              index,
                                              "number",
                                              e.target.value,
                                            )
                                          }
                                          placeholder="Номер"
                                          disabled={!canEditClientProfile}
                                          className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3"
                                        />
                                        <div className="md:col-span-5">
                                          <SystemDateInput
                                            type="date"
                                            value={doc.issueDate}
                                            onChange={(e) =>
                                              updateBirthCertificateEntry(
                                                index,
                                                "issueDate",
                                                e.target.value,
                                              )
                                            }
                                            disabled={!canEditClientProfile}
                                            className={DATE_INPUT_CLASS}
                                          />
                                        </div>
                                        <div className="flex items-center justify-end md:col-span-1">
                                          <button
                                            type="button"
                                            onClick={() => {
                                              void removeBirthCertificateEntry(index);
                                            }}
                                            disabled={!canEditClientProfile}
                                            className={PROFILE_DELETE_ICON_BUTTON_CLASS}
                                            title="Удалить свидетельство о рождении"
                                            aria-label="Удалить свидетельство о рождении"
                                          >
                                            <Trash2 className="h-4 w-4" />
                                          </button>
                                        </div>
                                        <input
                                          value={doc.actRecordNumber}
                                          onChange={(e) =>
                                            updateBirthCertificateEntry(
                                              index,
                                              "actRecordNumber",
                                              e.target.value,
                                            )
                                          }
                                          placeholder="№ актовой записи"
                                          disabled={!canEditClientProfile}
                                          className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4"
                                        />
                                        <input
                                          value={doc.childFullName}
                                          onChange={(e) =>
                                            updateBirthCertificateEntry(
                                              index,
                                              "childFullName",
                                              e.target.value,
                                            )
                                          }
                                          placeholder="ФИО ребенка"
                                          disabled={!canEditClientProfile}
                                          className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-8"
                                        />
                                        <input
                                          value={doc.issuedBy}
                                          onChange={(e) =>
                                            updateBirthCertificateEntry(
                                              index,
                                              "issuedBy",
                                              e.target.value,
                                            )
                                          }
                                          placeholder="Кем выдано"
                                          disabled={!canEditClientProfile}
                                          className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-12"
                                        />
                                      </div>
                                    </div>
                                  ),
                                )}
                              </div>
                            </div>
                          </div>
                        )
                        )}
                      {clientFormState.kind === "individual" && (
                        !clientProfileEditing ? (
                          <ProfileDocumentReadList title="Военные билеты" entries={clientFormState.militaryIdEntries.map((doc) => [
                            { label: 'Серия', value: doc.series }, { label: 'Номер', value: doc.number },
                            { label: 'Дата выдачи', value: toRuDate(doc.issueDate) }, { label: 'Воинское звание', value: doc.rank },
                            { label: 'Категория запаса', value: doc.category }, { label: 'Категория годности', value: doc.fitnessCategory },
                            { label: 'Код ВУС', value: doc.vusCode }, { label: 'Кем выдан', value: doc.issuedBy },
                            { label: 'Военный комиссариат', value: doc.militaryCommissariat },
                          ])} />
                        ) : (
                          <div className="md:col-span-2 space-y-4">
                            <div>
                              <div className="mb-2 flex items-center justify-between">
                                <label className="block text-sm font-medium text-gray-700">
                                  Военные билеты
                                </label>
                                <button
                                  type="button"
                                  onClick={addMilitaryIdEntry}
                                  disabled={!canEditClientProfile}
                                  className="text-sm text-emerald-600"
                                >
                                  + Добавить военный билет
                                </button>
                              </div>
                              <div className="space-y-2">
                                {clientFormState.militaryIdEntries.map((doc, index) => (
                                  <div
                                    key={`military-id-${index}`}
                                    className="rounded-lg border border-gray-200 p-3"
                                  >
                                    <div className="grid items-start gap-2 md:grid-cols-12">
                                      <input
                                        value={doc.series}
                                        onChange={(e) =>
                                          updateMilitaryIdEntry(
                                            index,
                                            "series",
                                            e.target.value,
                                          )
                                        }
                                        placeholder="Серия"
                                        disabled={!canEditClientProfile}
                                        className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3"
                                      />
                                      <input
                                        value={doc.number}
                                        onChange={(e) =>
                                          updateMilitaryIdEntry(
                                            index,
                                            "number",
                                            e.target.value,
                                          )
                                        }
                                        placeholder="Номер"
                                        disabled={!canEditClientProfile}
                                        className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3"
                                      />
                                      <div className="md:col-span-5">
                                        <SystemDateInput
                                          type="date"
                                          value={doc.issueDate}
                                          onChange={(e) =>
                                            updateMilitaryIdEntry(
                                              index,
                                              "issueDate",
                                              e.target.value,
                                            )
                                          }
                                          disabled={!canEditClientProfile}
                                          className={DATE_INPUT_CLASS}
                                        />
                                      </div>
                                      <div className="flex items-center justify-end md:col-span-1">
                                        <button
                                          type="button"
                                          onClick={() => {
                                            void removeMilitaryIdEntry(index);
                                          }}
                                          disabled={!canEditClientProfile}
                                          className={PROFILE_DELETE_ICON_BUTTON_CLASS}
                                          title="Удалить военный билет"
                                          aria-label="Удалить военный билет"
                                        >
                                          <Trash2 className="h-4 w-4" />
                                        </button>
                                      </div>
                                      <input
                                        value={doc.rank}
                                        onChange={(e) =>
                                          updateMilitaryIdEntry(
                                            index,
                                            "rank",
                                            e.target.value,
                                          )
                                        }
                                        placeholder="Воинское звание"
                                        disabled={!canEditClientProfile}
                                        className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4"
                                      />
                                      <input
                                        value={doc.category}
                                        onChange={(e) =>
                                          updateMilitaryIdEntry(
                                            index,
                                            "category",
                                            e.target.value,
                                          )
                                        }
                                        placeholder="Категория запаса"
                                        disabled={!canEditClientProfile}
                                        className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4"
                                      />
                                      <input
                                        value={doc.fitnessCategory}
                                        onChange={(e) =>
                                          updateMilitaryIdEntry(
                                            index,
                                            "fitnessCategory",
                                            e.target.value,
                                          )
                                        }
                                        placeholder="Категория годности"
                                        disabled={!canEditClientProfile}
                                        className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4"
                                      />
                                      <input
                                        value={doc.vusCode}
                                        onChange={(e) =>
                                          updateMilitaryIdEntry(
                                            index,
                                            "vusCode",
                                            e.target.value,
                                          )
                                        }
                                        placeholder="Код ВУС"
                                        disabled={!canEditClientProfile}
                                        className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-12"
                                      />
                                      <input
                                        value={doc.issuedBy}
                                        onChange={(e) =>
                                          updateMilitaryIdEntry(
                                            index,
                                            "issuedBy",
                                            e.target.value,
                                          )
                                        }
                                        placeholder="Кем выдан"
                                        disabled={!canEditClientProfile}
                                        className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-12"
                                      />
                                      <input
                                        value={doc.militaryCommissariat}
                                        onChange={(e) =>
                                          updateMilitaryIdEntry(
                                            index,
                                            "militaryCommissariat",
                                            e.target.value,
                                          )
                                        }
                                        placeholder="Военный комиссариат"
                                        disabled={!canEditClientProfile}
                                        className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-12"
                                      />
                                    </div>
                                  </div>
                                ))}
                              </div>
                            </div>
                          </div>
                        )
                        )}
                  </div>
                </section>
              )}

              {clientFormState.kind === "individual" && (
                <section
                  className="md:col-span-2 rounded-xl border border-gray-200 bg-gray-50/50 p-4"
                  aria-label="Дополнительная информация"
                >
                  <button
                    type="button"
                    onClick={() => {
                      if (profileAdditionalOpen && PROFILE_ADDITIONAL_GROUPS.includes(activeProfileGroup)) {
                        setActiveProfileGroup("main");
                      }
                      setProfileAdditionalOpen((open) => !open);
                    }}
                    aria-expanded={profileAdditionalOpen}
                    className="flex w-full items-center justify-between text-left text-base font-semibold text-gray-900"
                  >
                    <span>{profileAdditionalOpen ? "Скрыть дополнительную информацию" : "Дополнительная информация"}</span>
                    <ChevronDown className={`h-5 w-5 transition-transform ${profileAdditionalOpen ? "rotate-180" : ""}`} />
                  </button>
                  {profileAdditionalOpen ? (
                    <div className="mt-3 grid gap-4 md:grid-cols-2">
                          <div className="md:col-span-2">
                            <div className="mb-3 flex flex-wrap gap-2" role="group" aria-label="Дополнительные группы данных">
                              {[
                                {
                                  id: "main",
                                  label: "Контакты",
                                  requiredKeys: ["Phones", "Emails"],
                                  values: [
                                    ...clientFormState.phones,
                                    ...clientFormState.emails,
                                  ],
                                },
                                {
                                  id: "passports",
                                  label: "Паспорта",
                                  requiredKeys: ["Passports"],
                                  values: clientFormState.passportEntries,
                                },
                                {
                                  id: "addresses",
                                  label: "Адреса",
                                  requiredKeys: ["Addresses"],
                                  values: clientFormState.addressEntries,
                                },
                                {
                                  id: "education",
                                  additional: true,
                                  label: "Образование (ОКСО)",
                                  requiredKeys: ["Education"],
                                  values: clientFormState.education,
                                },
                                {
                                  id: "professions",
                                  additional: true,
                                  label: "Профессии (ОКПДТР)",
                                  requiredKeys: ["Professions"],
                                  values: clientFormState.professions,
                                },
                                {
                                  id: "workplaces",
                                  additional: true,
                                  label: "Работа и должности",
                                  requiredKeys: ["Workplaces"],
                                  values: clientFormState.workplaces,
                                },
                                {
                                  id: "relatives",
                                  additional: true,
                                  label: "Родственники",
                                  requiredKeys: ["Relatives"],
                                  values: clientFormState.relatives,
                                },
                                {
                                  id: "birthCertificates",
                                  label: "Свидетельства о рождении",
                                  requiredKeys: ["BirthCertificates"],
                                  values: clientFormState.birthCertificateEntries,
                                },
                                {
                                  id: "militaryIds",
                                  label: "Военные билеты",
                                  requiredKeys: ["MilitaryIds"],
                                  values: clientFormState.militaryIdEntries,
                                },
                                {
                                  id: "poa",
                                  additional: true,
                                  label: "Доверенности",
                                  requiredKeys: ["PowersOfAttorney"],
                                  values: powersOfAttorney,
                                },
                              ].filter((tab) => tab.additional).map((tab) => (
                                <button
                                  key={tab.id}
                                  type="button"
                                  onClick={() =>
                                    setActiveProfileGroup(
                                      tab.id as typeof activeProfileGroup,
                                    )
                                  }
                                  className={`rounded-lg border px-3 py-1.5 text-sm ${(() => {
                                    const isActive = activeProfileGroup === tab.id;
                                    const missing = hasMissingRequiredList(
                                      tab.requiredKeys,
                                      tab.values ?? [],
                                    );
                                    const requiredAndFilled =
                                      hasAnyRequiredList(tab.requiredKeys) && !missing;
                                    if (isActive && missing)
                                      return "border-red-500 bg-red-50 text-red-700";
                                    if (isActive && requiredAndFilled)
                                      return "border-emerald-500 bg-emerald-50 text-emerald-700";
                                    if (isActive)
                                      return "border-emerald-500 bg-emerald-50 text-emerald-700";
                                    if (missing) return "border-red-300 text-red-700";
                                    if (requiredAndFilled)
                                      return "border-emerald-300 text-emerald-700";
                                    return "border-gray-300 text-gray-700";
                                  })()}`}
                                >
                                  {tab.label}
                                  {hasAnyRequiredList(tab.requiredKeys) ? " *" : ""}
                                </button>
                              ))}
                            </div>
                          </div>
                      {clientFormState.kind === "individual" &&
                        profileAdditionalOpen &&
                        activeProfileGroup === "education" && (
                          <div className="md:col-span-2">
                            <label className="block text-sm font-medium text-gray-700 mb-2">
                              Образование (ОКСО)
                            </label>
                            <div className="space-y-2">
                              {clientFormState.education.map((item, index) => (
                                <div
                                  key={`education-${index}`}
                                  className="rounded-lg border border-gray-200 p-3"
                                >
                                  <div className="grid items-start gap-2 md:grid-cols-12">
                                    <input
                                      value={item.institution}
                                      onChange={(e) =>
                                        updateEducation(
                                          index,
                                          "institution",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="Учебное заведение"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4"
                                    />
                                    <input
                                      value={item.specialty}
                                      onChange={(e) =>
                                        updateEducation(
                                          index,
                                          "specialty",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="Специальность"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3"
                                    />
                                    <input
                                      value={item.diplomaNumber}
                                      onChange={(e) =>
                                        updateEducation(
                                          index,
                                          "diplomaNumber",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="№ диплома"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-2"
                                    />
                                    <div className="md:col-span-2">
                                      <input
                                        value={item.oksoCode}
                                        onFocus={() => setOksoSearch(item.oksoCode)}
                                        onChange={(e) => {
                                          const value = e.target.value;
                                          updateEducation(index, "oksoCode", value);
                                          setOksoSearch(value);
                                        }}
                                        placeholder="Код ОКСО"
                                        list="okso-codes"
                                        aria-busy={oksoLoading}
                                        disabled={!canEditClientProfile}
                                        className="w-full px-3 py-2 border border-gray-300 rounded-lg"
                                      />
                                      {oksoLoading && (
                                        <p className="mt-1 text-xs text-gray-500">Ищем по ОКСО…</p>
                                      )}
                                    </div>
                                    <div className="flex items-center justify-end md:col-span-1">
                                      <button
                                        type="button"
                                        onClick={() => {
                                          void removeEducation(index);
                                        }}
                                        disabled={!canEditClientProfile}
                                        className={PROFILE_DELETE_ICON_BUTTON_CLASS}
                                        title="Удалить образование"
                                        aria-label="Удалить образование"
                                      >
                                        <Trash2 className="h-4 w-4" />
                                      </button>
                                    </div>
                                  </div>
                                </div>
                              ))}
                              <button
                                type="button"
                                onClick={() =>
                                  setClientFormState((prev) => ({
                                    ...prev,
                                    education: [
                                      ...prev.education,
                                      createEmptyEducation(),
                                    ],
                                  }))
                                }
                                disabled={!canEditClientProfile}
                                className="text-sm text-emerald-600"
                              >
                                + Добавить образование
                              </button>
                            </div>
                          </div>
                        )}

                      <datalist id="okso-codes">
                        {oksoOptions.map((item) => (
                          <option key={item.code} value={item.code}>
                            {item.name}
                          </option>
                        ))}
                      </datalist>
                      <datalist id="okpdtr-codes">
                        {okpdtrOptions.map((item) => (
                          <option key={item.code} value={item.code}>
                            {item.name}
                          </option>
                        ))}
                      </datalist>
                      {clientFormState.kind === "individual" &&
                        profileAdditionalOpen &&
                        activeProfileGroup === "professions" && (
                          <div className="md:col-span-2">
                            <label className="block text-sm font-medium text-gray-700 mb-2">
                              Профессии (ОКПДТР)
                            </label>
                            <div className="space-y-2">
                              {clientFormState.professions.map((item, index) => (
                                <div
                                  key={`profession-${index}`}
                                  className="rounded-lg border border-gray-200 p-3"
                                >
                                  <div className="grid items-start gap-2 md:grid-cols-12">
                                    <input
                                      value={item.profession}
                                      onChange={(e) =>
                                        updateProfession(
                                          index,
                                          "profession",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="Профессия"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-7"
                                    />
                                    <div className="md:col-span-4">
                                      <input
                                        value={item.okpdtrCode}
                                        onFocus={() => setOkpdtrSearch(item.okpdtrCode)}
                                        onChange={(e) => {
                                          const value = e.target.value;
                                          updateProfession(index, "okpdtrCode", value);
                                          setOkpdtrSearch(value);
                                        }}
                                        placeholder="Код ОКПДТР"
                                        list="okpdtr-codes"
                                        aria-busy={okpdtrLoading}
                                        disabled={!canEditClientProfile}
                                        className="w-full px-3 py-2 border border-gray-300 rounded-lg"
                                      />
                                      {okpdtrLoading && (
                                        <p className="mt-1 text-xs text-gray-500">Ищем по ОКПДТР…</p>
                                      )}
                                    </div>
                                    <div className="flex items-center justify-end md:col-span-1">
                                      <button
                                        type="button"
                                        onClick={() => {
                                          void removeProfession(index);
                                        }}
                                        disabled={!canEditClientProfile}
                                        className={PROFILE_DELETE_ICON_BUTTON_CLASS}
                                        title="Удалить профессию"
                                        aria-label="Удалить профессию"
                                      >
                                        <Trash2 className="h-4 w-4" />
                                      </button>
                                    </div>
                                  </div>
                                </div>
                              ))}
                              <button
                                type="button"
                                onClick={() =>
                                  setClientFormState((prev) => ({
                                    ...prev,
                                    professions: [
                                      ...prev.professions,
                                      createEmptyProfession(),
                                    ],
                                  }))
                                }
                                disabled={!canEditClientProfile}
                                className="text-sm text-emerald-600"
                              >
                                + Добавить профессию
                              </button>
                            </div>
                          </div>
                        )}
                      {clientFormState.kind === "individual" &&
                        profileAdditionalOpen &&
                        activeProfileGroup === "workplaces" && (
                          <div className="md:col-span-2">
                            <label className="block text-sm font-medium text-gray-700 mb-2">
                              Места работы и должности
                            </label>
                            <div className="space-y-2">
                              {clientFormState.workplaces.map((item, index) => (
                                <div
                                  key={`workplace-${index}`}
                                  className="rounded-lg border border-gray-200 p-3"
                                >
                                  <div className="grid items-start gap-2 md:grid-cols-12">
                                    <input
                                      value={item.organizationName}
                                      onChange={(e) =>
                                        updateWorkplace(
                                          index,
                                          "organizationName",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="Организация"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4"
                                    />
                                    <input
                                      value={item.position}
                                      onChange={(e) =>
                                        updateWorkplace(
                                          index,
                                          "position",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="Должность"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3"
                                    />
                                    <div className="md:col-span-2">
                                      <SystemDateInput
                                        type="date"
                                        value={item.startDate}
                                        onChange={(e) =>
                                          updateWorkplace(
                                            index,
                                            "startDate",
                                            e.target.value,
                                          )
                                        }
                                        disabled={!canEditClientProfile}
                                        className={DATE_INPUT_CLASS}
                                      />
                                    </div>
                                    <div className="md:col-span-2">
                                      <SystemDateInput
                                        type="date"
                                        value={item.endDate}
                                        onChange={(e) =>
                                          updateWorkplace(
                                            index,
                                            "endDate",
                                            e.target.value,
                                          )
                                        }
                                        disabled={!canEditClientProfile}
                                        className={DATE_INPUT_CLASS}
                                      />
                                    </div>
                                    <div className="flex items-center justify-end md:col-span-1">
                                      <button
                                        type="button"
                                        onClick={() => {
                                          void removeWorkplace(index);
                                        }}
                                        disabled={!canEditClientProfile}
                                        className={PROFILE_DELETE_ICON_BUTTON_CLASS}
                                        title="Удалить место работы"
                                        aria-label="Удалить место работы"
                                      >
                                        <Trash2 className="h-4 w-4" />
                                      </button>
                                    </div>
                                  </div>
                                </div>
                              ))}
                              <button
                                type="button"
                                onClick={() =>
                                  setClientFormState((prev) => ({
                                    ...prev,
                                    workplaces: [
                                      ...prev.workplaces,
                                      createEmptyWorkplace(),
                                    ],
                                  }))
                                }
                                disabled={!canEditClientProfile}
                                className="text-sm text-emerald-600"
                              >
                                + Добавить место работы
                              </button>
                              <button
                                type="button"
                                disabled={!canEditClientProfile}
                                onClick={() => {
                                  setLegalEntityPromptError(null);
                                  setLegalEntityDraft({ shortName: "", inn: "" });
                                  setLegalEntityPromptOpen(true);
                                }}
                                className="text-sm text-indigo-600"
                              >
                                + Добавить организацию
                              </button>
                            </div>
                          </div>
                        )}
                      {clientFormState.kind === "individual" &&
                        profileAdditionalOpen &&
                        activeProfileGroup === "relatives" && (
                          <div className="md:col-span-2">
                            <label className="block text-sm font-medium text-gray-700 mb-2">
                              Близкие родственники
                            </label>
                            <div className="space-y-2">
                              {clientFormState.relatives.map((item, index) => (
                                <div
                                  key={`relative-${index}`}
                                  className="rounded-lg border border-gray-200 p-3"
                                >
                                  <div className="grid items-start gap-2 md:grid-cols-12">
                                    <input
                                      value={item.relation}
                                      onChange={(e) =>
                                        updateRelative(
                                          index,
                                          "relation",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="Степень родства"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-2"
                                    />
                                    <input
                                      value={item.fullName}
                                      onChange={(e) =>
                                        updateRelative(
                                          index,
                                          "fullName",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="ФИО"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-4"
                                    />
                                    <input
                                      value={item.birthYear}
                                      onChange={(e) =>
                                        updateRelative(
                                          index,
                                          "birthYear",
                                          e.target.value,
                                        )
                                      }
                                      placeholder="Год рождения"
                                      disabled={!canEditClientProfile}
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-2"
                                    />
                                    <input
                                      value={item.phone}
                                      onChange={(e) =>
                                        updateRelative(index, "phone", e.target.value)
                                      }
                                      placeholder="Номер телефона"
                                      disabled={!canEditClientProfile}
                                      inputMode="tel"
                                      className="px-3 py-2 border border-gray-300 rounded-lg md:col-span-3"
                                    />
                                    <div className="flex items-center justify-end md:col-span-1">
                                      <button
                                        type="button"
                                        onClick={() => {
                                          void removeRelative(index);
                                        }}
                                        disabled={!canEditClientProfile}
                                        className={PROFILE_DELETE_ICON_BUTTON_CLASS}
                                        title="Удалить родственника"
                                        aria-label="Удалить родственника"
                                      >
                                        <Trash2 className="h-4 w-4" />
                                      </button>
                                    </div>
                                  </div>
                                </div>
                              ))}
                              <button
                                type="button"
                                onClick={() =>
                                  setClientFormState((prev) => ({
                                    ...prev,
                                    relatives: [
                                      ...prev.relatives,
                                      createEmptyRelative(),
                                    ],
                                  }))
                                }
                                disabled={!canEditClientProfile}
                                className="text-sm text-emerald-600"
                              >
                                + Добавить родственника
                              </button>
                            </div>
                          </div>
                        )}
                      {clientFormState.kind === "individual" &&
                        profileAdditionalOpen &&
                        activeProfileGroup === "poa" && (
                          <div className="md:col-span-2">
                            <div className="mb-2 flex items-center justify-between">
                              <label className="block text-sm font-medium text-gray-700">
                                Доверенности
                              </label>
                              <button
                                type="button"
                                disabled={!canEditClientProfile}
                                onClick={openCreatePoaPrompt}
                                className="text-sm text-emerald-600"
                              >
                                + Добавить доверенность
                              </button>
                            </div>
                            <div className="rounded-lg border border-gray-200 p-3">
                              {powersOfAttorney.length === 0 ? (
                                <p className="text-sm text-gray-500">
                                  Доверенности не найдены.
                                </p>
                              ) : (
                                <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                                  {powersOfAttorney.map((poa) => (
                                    <div
                                      key={poa.id}
                                      data-poa-card="true"
                                      className="rounded border border-gray-200 px-3 py-3 text-sm"
                                    >
                                      <div className="flex items-start justify-between gap-3">
                                        <div className="min-w-0 space-y-1">
                                          <div className="font-medium text-slate-800">
                                            №{poa.number}
                                          </div>
                                          <div className="text-slate-600">
                                            {toRuDate(poa.issuedAt)}{" "}
                                            {poa.validUntil
                                              ? `— до ${toRuDate(poa.validUntil)}`
                                              : ""}
                                          </div>
                                          {poa.issuer ? (
                                            <div className="text-slate-500">
                                              Кем выдана: {poa.issuer}
                                            </div>
                                          ) : null}
                                        </div>
                                        <div className="flex items-center gap-1">
                                          <button
                                            type="button"
                                            onClick={() => openEditPoaPrompt(poa)}
                                            disabled={!canEditClientProfile}
                                            className={
                                              PROFILE_SECONDARY_ICON_BUTTON_CLASS
                                            }
                                            title="Изменить доверенность"
                                            aria-label="Изменить доверенность"
                                          >
                                            <Edit className="h-4 w-4" />
                                          </button>
                                          <button
                                            type="button"
                                            onClick={() =>
                                              void deletePowerOfAttorney(poa)
                                            }
                                            disabled={!canEditClientProfile}
                                            className={PROFILE_DELETE_ICON_BUTTON_CLASS}
                                            title="Удалить доверенность"
                                            aria-label="Удалить доверенность"
                                          >
                                            <Trash2 className="h-4 w-4" />
                                          </button>
                                        </div>
                                      </div>
                                    </div>
                                  ))}
                                </div>
                              )}
                            </div>
                          </div>
                        )}
                    </div>
                  ) : null}
                </section>
              )}

              {clientFormState.kind === "individual" && !clientProfileEditing ? (
                <dl aria-label="Заметки клиента" className="md:col-span-2 grid min-w-0 gap-3 md:grid-cols-2">
                  <div className="min-w-0">
                    <dt className="mb-2 text-sm text-gray-500">
                      {"Комментарий к расширенной карточке" + requiredMark("Comment")}
                    </dt>
                    <dd className="min-w-0 whitespace-pre-wrap text-sm text-gray-900 [overflow-wrap:anywhere]">
                      {clientFormState.comment || "Не указано"}
                    </dd>
                  </div>
                  <div className="min-w-0">
                    <dt className="mb-2 text-sm text-gray-500">Исторические заметки (не удаляются)</dt>
                    <dd className="min-w-0 whitespace-pre-wrap text-sm text-gray-900 [overflow-wrap:anywhere]">
                      {clientFormState.legacyNotes || "Не указано"}
                    </dd>
                  </div>
                </dl>
              ) : (
                <div className="md:col-span-2 grid gap-3 md:grid-cols-2">
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {"Комментарий к расширенной карточке" +
                        requiredMark("Comment")}
                    </label>
                    <textarea
                      name="comment"
                      value={clientFormState.comment}
                      onChange={handleClientFormChange}
                      rows={3}
                      disabled={!canEditClientProfile}
                      className={`w-full px-3 py-2 border rounded-lg focus:ring-2 ${canEditClientProfile ? "bg-white" : "bg-gray-100"} ${requiredInputClass("Comment", clientFormState.comment)}`}
                      placeholder={t("additionalInfo")}
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      Исторические заметки (не удаляются)
                    </label>
                    <textarea
                      name="legacyNotes"
                      value={clientFormState.legacyNotes}
                      onChange={handleClientFormChange}
                      rows={3}
                      disabled={!canEditClientProfile}
                      className={`w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500 ${canEditClientProfile ? "bg-white" : "bg-gray-100"}`}
                      placeholder={t("notes") || "Заметки"}
                    />
                  </div>
                </div>
              )}
              <div className="md:col-span-2 flex items-center gap-3">
                <SystemCheckbox
                  type="checkbox"
                  name="isActive"
                  checked={clientFormState.isActive}
                  onChange={handleClientFormChange}
                  disabled={!canEditClientProfile}
                  className="h-4 w-4 text-emerald-600 focus:ring-emerald-500 border-gray-300 rounded"
                  id="client-detail-is-active"
                />
                <label
                  htmlFor="client-detail-is-active"
                  className="text-sm text-gray-700"
                >
                  {t("activeClient")}
                </label>
              </div>
              {clientProfileEditing ? (
                <div className="md:col-span-2 flex flex-wrap items-center gap-3">
                <button
                  type="submit"
                  disabled={!canEditClientProfile || clientFormSaving}
                  className="inline-flex items-center justify-center rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700 disabled:opacity-60"
                >
                  {clientFormSaving
                    ? (t("saving") ?? "Сохранение...")
                    : (t("update") ?? "Сохранить")}
                </button>
                <button
                  type="button"
                  onClick={handleDeleteClientCard}
                  disabled={!clientPermissions.canDelete || clientFormSaving}
                  className="inline-flex items-center justify-center rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-red-700 disabled:opacity-60"
                >
                  {t("delete") ?? "Удалить"}
                </button>
                </div>
              ) : null}
            </form>
          </div>
        ) : activeSection === "payments" ? (
          <>
            <TwoTypeStats
              clientId={clientId}
              caseId={caseIdForQuery}
              from={fromDateStr}
              to={toDateStr}
              statusFilter={toStatsStatusFilter(statusFilter)}
              clientRole={paymentViewConfig.clientRole}
              search={debouncedPaymentSearch || undefined}
              reloadToken={statsReloadToken}
              storageKey={`pp.client-detail.stats_panels.${user?.id ?? "guest"}`}
              className="mb-6"
            />

            <div className="bg-white rounded-xl shadow-sm border border-gray-100 mb-6">
              <div className="p-6 flex flex-wrap items-center gap-3">
                <MonthRangePicker
                  value={{
                    from: monthFrom || undefined,
                    to: monthTo || undefined,
                  }}
                  onChange={(r) => {
                    setMonthFrom(r.from ?? "");
                    setMonthTo(r.to ?? "");
                  }}
                  yearsBack={8}
                  yearsForward={1}
                />

                <div className="relative w-full sm:w-56">
                  <SystemSelect
                    aria-label="Вид платежей клиента"
                    value={paymentViewMode}
                    onChange={(event) => {
                      setPaymentViewMode(event.target.value as PaymentViewMode);
                      setPaymentViewModeTouched(true);
                      setPaymentPage(1);
                    }}
                    className="w-full bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                  >
                    {PAYMENT_VIEW_OPTIONS.map((option) => (
                      <option key={option.key} value={option.key}>
                        {option.label}
                      </option>
                    ))}
                  </SystemSelect>
                </div>

                <div className="relative w-full sm:w-56">
                  <SystemSelect
                    aria-label="Статус платежей клиента"
                    value={statusFilter}
                    onChange={(e) =>
                      setStatusFilter(
                        e.target.value as "all" | NormalizedStatus,
                      )
                    }
                    className="w-full bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                  >
                    <option value="all">Все статусы</option>
                    <option value="pending">Ожидается</option>
                    <option value="completed">Выполнено</option>
                    <option value="overdue">Просрочено</option>
                  </SystemSelect>
                </div>

                <div className="relative w-full sm:w-48">
                  <SystemSelect
                    aria-label="Тип платежей клиента"
                    value={paymentTypeFilter}
                    onChange={(event) =>
                      setPaymentTypeFilter(
                        event.target.value as "all" | Payment["type"],
                      )
                    }
                    disabled={paymentViewMode !== "default"}
                    className="w-full bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                  >
                    <option value="all">Все типы</option>
                    <option value="Income">Доходы</option>
                    <option value="Expense">Расходы</option>
                  </SystemSelect>
                </div>

                {renderRelatedFilterSelect(
                  "Договор",
                  relatedContractFilter,
                  setRelatedContractFilter,
                  relatedContractOptions,
                  "Все договоры",
                  formatContractFilterLabel,
                  "w-full sm:w-64",
                )}
                {renderRelatedFilterSelect(
                  "Счет",
                  relatedInvoiceFilter,
                  setRelatedInvoiceFilter,
                  relatedInvoiceOptions,
                  "Все счета",
                  formatInvoiceFilterLabel,
                  "w-full sm:w-64",
                )}
                {renderRelatedFilterSelect(
                  "Акт",
                  relatedActFilter,
                  setRelatedActFilter,
                  relatedActOptions,
                  "Все акты",
                  formatActFilterLabel,
                  "w-full sm:w-64",
                )}

                <div className="relative min-w-[240px] flex-1">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                  <input
                    type="search"
                    aria-label="Поиск платежей клиента"
                    value={paymentSearch}
                    onChange={(event) => setPaymentSearch(event.target.value)}
                    placeholder="Поиск по сумме, комментарию или документу"
                    className="w-full rounded-lg border border-gray-300 bg-white py-2 pl-10 pr-3 text-sm text-gray-700 placeholder:text-gray-400 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                  />
                </div>
              </div>

              <div className="px-6 pb-6 pt-0">
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={() => setSelectedCaseId("all")}
                    className={`px-3 py-1.5 rounded-full text-sm border transition-colors ${
                      selectedCaseId === "all"
                        ? "bg-slate-800 text-white border-slate-800 shadow-sm"
                        : "bg-slate-50 text-slate-700 border-slate-200 hover:bg-slate-100"
                    }`}
                  >
                    Все дела
                  </button>

                  {(isSupplierCard ? supplierCaseSummaries : cases).map((k) => {
                    if (isSupplierCard) {
                      const item = k as ClientCaseFinanceSummary;
                      const active = selectedCaseId === item.caseId;
                      return (
                        <button
                          key={item.caseId}
                          type="button"
                          onClick={() => setSelectedCaseId(item.caseId)}
                          title={item.clientName ?? undefined}
                          className={`px-3 py-1.5 rounded-full text-sm border transition-colors ${
                            active
                              ? "bg-slate-800 text-white border-slate-800 shadow-sm"
                              : "bg-slate-50 text-slate-700 border-slate-200 hover:bg-slate-100"
                          }`}
                        >
                          {item.caseTitle || `Дело #${item.caseId}`}
                        </button>
                      );
                    }
                    const caseItem = k as ClientCase;
                    const active = selectedCaseId === caseItem.id;
                    const s = (caseItem.status ?? "").toLowerCase();
                    const statusClasses = s.includes("open")
                      ? "bg-green-50 text-green-700 border-green-200"
                      : s.includes("hold")
                        ? "bg-amber-50 text-amber-800 border-amber-200"
                        : s.includes("closed")
                          ? "bg-red-50 text-red-700 border-red-200"
                          : "bg-slate-50 text-slate-700 border-slate-200";
                    return (
                      <button
                        key={caseItem.id}
                        type="button"
                        onClick={() => setSelectedCaseId(caseItem.id)}
                        title={caseStatusLabel(caseItem.status)}
                        className={`px-3 py-1.5 rounded-full text-sm border transition-colors ${
                          active
                            ? "bg-slate-800 text-white border-slate-800 shadow-sm"
                            : `${statusClasses} hover:opacity-90`
                        }`}
                      >
                        {caseItem.title}
                      </button>
                    );
                  })}
                </div>
              </div>

              {paymentFilterBadges.length > 0 ? (
                <div className="border-t border-gray-100 px-6 py-4">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-sm font-semibold text-slate-700">
                      Применены фильтры:
                    </span>
                    {paymentFilterBadges.map((badge) => (
                      <span
                        key={badge}
                        className="inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-medium text-slate-600"
                      >
                        {badge}
                      </span>
                    ))}
                    <button
                      type="button"
                      onClick={resetPaymentFilters}
                      className="ml-auto inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                    >
                      <RotateCcw className="h-4 w-4" />
                      Сбросить все
                    </button>
                  </div>
                </div>
              ) : null}
            </div>
          </>
        ) : activeSection === "accounts" ? (
          <>
            <div className="grid gap-4 mb-6 md:grid-cols-4">
              {[
                {
                  key: "pending",
                  title: "Не оплачено всего",
                  value: invoiceSummaryBuckets.pending.amount,
                  count: invoiceSummaryBuckets.pending.count,
                  accent: "border border-amber-100 bg-amber-50 text-amber-700",
                  icon: WalletCards,
                },
                {
                  key: "overdue",
                  title: "Не оплачено в срок",
                  value: invoiceSummaryBuckets.overdue.amount,
                  count: invoiceSummaryBuckets.overdue.count,
                  accent: "border border-rose-100 bg-rose-50 text-rose-700",
                  icon: AlertTriangle,
                },
                {
                  key: "paid",
                  title: "Оплачено",
                  value: invoiceSummaryBuckets.paid.amount,
                  count: invoiceSummaryBuckets.paid.count,
                  accent:
                    "border border-emerald-100 bg-emerald-50 text-emerald-700",
                  icon: CheckCircle2,
                },
              ].map((card) => {
                const Icon = card.icon;
                return (
                  <div
                    key={card.key}
                    className={`rounded-2xl p-5 shadow-sm ${card.accent}`}
                  >
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
                      {(t("invoiceSummaryCount") ?? "Количество счетов") + ": "}{" "}
                      {card.count}
                    </p>
                  </div>
                );
              })}
              <div className="rounded-2xl border border-rose-100 bg-rose-50 p-5 shadow-sm text-rose-700">
                <p className="text-sm font-medium">Просрочено на:</p>
                <div className="mt-3 space-y-1 text-sm">
                  <div className="flex items-center justify-between gap-2">
                    <span>0-10 дней</span>
                    <span>
                      {
                        formatCurrencySmart(
                          invoiceSummaryBuckets.overdueAging?.day0To10 ?? 0,
                        ).full
                      }
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span>11-30 дней</span>
                    <span>
                      {
                        formatCurrencySmart(
                          invoiceSummaryBuckets.overdueAging?.day11To30 ?? 0,
                        ).full
                      }
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span>31-60 дней</span>
                    <span>
                      {
                        formatCurrencySmart(
                          invoiceSummaryBuckets.overdueAging?.day31To60 ?? 0,
                        ).full
                      }
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span>61-90 дней</span>
                    <span>
                      {
                        formatCurrencySmart(
                          invoiceSummaryBuckets.overdueAging?.day61To90 ?? 0,
                        ).full
                      }
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span>Больше 90 дней</span>
                    <span>
                      {
                        formatCurrencySmart(
                          invoiceSummaryBuckets.overdueAging?.day91Plus ?? 0,
                        ).full
                      }
                    </span>
                  </div>
                </div>
                <div className="mt-3 border-t border-rose-200 pt-2 flex items-center justify-between font-semibold">
                  <span>Просрочено итого:</span>
                  <span>
                    {
                      formatCurrencySmart(
                        invoiceSummaryBuckets.overdueAging?.total ?? 0,
                      ).full
                    }
                  </span>
                </div>
              </div>
            </div>

            <ClientSectionFilterPanel
              badges={invoiceFilterBadges}
              onReset={
                invoiceFilterBadges.length > 0 ? resetInvoiceFilters : undefined
              }
            >
              <ClientSectionFilterField
                label="Вид"
                className="w-full min-w-[260px] sm:w-72"
              >
                <SystemSelect
                  aria-label="Вид счетов в карточке"
                  value={invoiceViewMode}
                  onChange={(event) => {
                    setInvoiceViewMode(event.target.value as InvoiceViewMode);
                    setInvoiceViewModeTouched(true);
                    setInvoicePage(1);
                  }}
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus:border-slate-400 focus:outline-none focus:ring-2 focus:ring-slate-200"
                >
                  {INVOICE_VIEW_OPTIONS.map((option) => (
                    <option key={option.key} value={option.key}>
                      {option.label}
                    </option>
                  ))}
                </SystemSelect>
              </ClientSectionFilterField>
              <DatePeriodPicker
                label="Период"
                value={invoicePeriod}
                onChange={setInvoicePeriod}
                className="w-full min-w-[220px] sm:w-auto"
              />
              <ClientSectionFilterField
                label="Статус"
                className="w-full sm:w-56"
              >
                <SystemSelect
                  aria-label="Статус счетов клиента"
                  value={invoiceStatusFilter}
                  onChange={(event) =>
                    setInvoiceStatusFilter(
                      event.target.value as "all" | PaymentStatus,
                    )
                  }
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus:border-slate-400 focus:outline-none focus:ring-2 focus:ring-slate-200"
                >
                  <option value="all">Все статусы</option>
                  <option value="Pending">{invoiceStatusLabels.Pending}</option>
                  <option value="Completed">
                    {invoiceStatusLabels.Completed}
                  </option>
                  <option value="Overdue">{invoiceStatusLabels.Overdue}</option>
                  <option value="Processing">
                    {invoiceStatusLabels.Processing}
                  </option>
                  <option value="Cancelled">
                    {invoiceStatusLabels.Cancelled}
                  </option>
                  </SystemSelect>
                </ClientSectionFilterField>
              {renderRelatedFilterSelect(
                "Договор",
                relatedContractFilter,
                setRelatedContractFilter,
                relatedContractOptions,
                "Все договоры",
                formatContractFilterLabel,
                "w-full sm:w-64",
              )}
              {renderRelatedFilterSelect(
                "Акт",
                relatedActFilter,
                setRelatedActFilter,
                relatedActOptions,
                "Все акты",
                formatActFilterLabel,
                "w-full sm:w-64",
              )}
              {renderRelatedFilterSelect(
                "Платеж",
                relatedPaymentFilter,
                setRelatedPaymentFilter,
                relatedPaymentOptions,
                "Все платежи",
                formatPaymentFilterLabel,
                "w-full sm:w-64",
              )}
              <ClientSectionFilterField
                label="Поиск"
                className="min-w-[260px] flex-1"
              >
                <div className="relative">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="search"
                    aria-label="Поиск счетов клиента"
                    value={invoiceSearch}
                    onChange={(event) => setInvoiceSearch(event.target.value)}
                    placeholder="Номер счета, сумма или комментарий"
                    className="w-full rounded-lg border border-slate-300 bg-white py-2 pl-10 pr-3 text-sm text-slate-700 placeholder:text-slate-400 focus:border-slate-400 focus:outline-none focus:ring-2 focus:ring-slate-200"
                  />
                </div>
              </ClientSectionFilterField>
            </ClientSectionFilterPanel>
            <p className="-mt-4 mb-6 px-4 text-xs text-slate-500">
              {invoiceViewConfig.description}
            </p>

            <div className="mb-6 flex justify-end">
              <button
                type="button"
                onClick={() => refreshInvoices()}
                className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
              >
                <RotateCcw
                  className={
                    invoicesRefreshing ? "h-4 w-4 animate-spin" : "h-4 w-4"
                  }
                />
                {t("invoiceRefresh") ?? "Обновить"}
              </button>
            </div>
          </>
        ) : null}

        {activeSection !== "profile" && (
          <div className="bg-white rounded-xl shadow-sm border border-gray-100">
            <div className="p-6 border-b border-gray-100">
              <div className="flex items-center justify-between">
                <h2 className="text-xl font-semibold text-gray-900">
                  {SECTION_META[activeSection].label}
                </h2>
                {activeSection === "payments" ||
                activeSection === "accounts" ||
                activeSection === "acts" ||
                activeSection === "contracts" ? (
                  <button
                    type="button"
                    onClick={() => {
                      if (activeSection === "payments") {
                        setPaymentTableSettingsOpen((prev) => !prev);
                      } else if (activeSection === "accounts") {
                        setInvoiceTableSettingsOpen((prev) => !prev);
                      } else if (activeSection === "acts") {
                        setActTableSettingsOpen((prev) => !prev);
                      } else if (activeSection === "contracts") {
                        setContractTableSettingsOpen((prev) => !prev);
                      }
                    }}
                    className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
                  >
                    <Settings2 className="h-4 w-4" />
                    {(activeSection === "payments" &&
                      paymentTableSettingsOpen) ||
                    (activeSection === "accounts" &&
                      invoiceTableSettingsOpen) ||
                    (activeSection === "acts" && actTableSettingsOpen) ||
                    (activeSection === "contracts" && contractTableSettingsOpen)
                      ? "Скрыть настройки"
                      : "Настроить таблицу"}
                  </button>
                ) : null}
              </div>
            </div>

            <div className="p-6">
              {relatedDocumentLookupError &&
              (activeSection === "payments" ||
                activeSection === "accounts" ||
                activeSection === "acts" ||
                activeSection === "contracts") ? (
                <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-700">
                  {relatedDocumentLookupError}
                </div>
              ) : null}
              {activeSection === "tasks" ? (
                <TaskJournal
                  embedded
                  initialView="list"
                  contextFilter={taskJournalContext}
                  onOpenClient={(targetClientId, targetCaseId) => {
                    if (
                      targetClientId === clientId &&
                      targetCaseId &&
                      onOpenCasePage
                    ) {
                      onOpenCasePage(targetCaseId, targetClientId);
                    }
                  }}
                />
              ) : activeSection === "cases" ? (
                <div className="space-y-4">
                  {!caseCardsAvailable ? (
                    <div className="rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-6 py-10 text-center">
                      <FileCheck2 className="mx-auto mb-3 h-10 w-10 text-slate-300" />
                      <h3 className="text-lg font-semibold text-slate-900">
                        {isSupplierCard
                          ? "Расходы по клиентским делам пока не привязаны"
                          : "Дела пока не добавлены"}
                      </h3>
                      <p className="mt-2 text-sm text-slate-500">
                        {isSupplierCard
                          ? "Когда расходный счет или платеж поставщика будет связан с делом клиента, он появится здесь."
                          : "Создайте первое дело, чтобы связывать с ним платежи, счета, акты и договоры клиента."}
                      </p>
                      {casePermissions.canView && casePermissions.canCreate && !isSupplierCard ? (
                        <button
                          type="button"
                          onClick={openAddCase}
                          className="mt-5 inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700"
                        >
                          <Plus size={16} /> Добавить дело
                        </button>
                      ) : null}
                    </div>
                  ) : (
                    <>
                      <div className="flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-emerald-100 bg-emerald-50 px-5 py-4">
                        <div>
                          <p className="text-sm font-semibold text-emerald-900">
                            {isSupplierCard ? "Клиентских дел у поставщика" : "Всего дел"}: {caseCardsCount}
                          </p>
                          <p className="mt-1 text-sm text-emerald-700">
                            {isSupplierCard
                              ? "Здесь собраны клиентские дела, по которым есть расходы в пользу этого поставщика."
                              : "Здесь собраны дела клиента. Финансовая сводка помогает видеть доходы, расходы и долги по каждому делу."}
                          </p>
                          {caseFinanceError ? (
                            <p className="mt-2 text-xs font-medium text-rose-700">
                              {caseFinanceError}
                            </p>
                          ) : null}
                        </div>
                      </div>

                      <ClientSectionFilterPanel
                        badges={caseFilterBadges}
                        onReset={
                          caseFilterBadges.length > 0
                            ? resetCaseFilters
                            : undefined
                        }
                      >
                        <ClientSectionFilterField
                          label="Поиск"
                          className="min-w-[260px] flex-1"
                        >
                          <div className="relative">
                            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                            <input
                              type="search"
                              aria-label="Поиск дел клиента"
                              value={caseSearch}
                              onChange={(event) =>
                                setCaseSearch(event.target.value)
                              }
                              placeholder="Название или описание дела"
                              className="w-full rounded-lg border border-slate-300 bg-white py-2 pl-10 pr-3 text-sm text-slate-700 placeholder:text-slate-400 focus:border-slate-400 focus:outline-none focus:ring-2 focus:ring-slate-200"
                            />
                          </div>
                        </ClientSectionFilterField>
                        <ClientSectionFilterField
                          label="Статус"
                          className="w-full sm:w-56"
                        >
                          <SystemSelect
                            aria-label="Статус дел клиента"
                            value={caseStatusFilter}
                            onChange={(event) =>
                              setCaseStatusFilter(
                                event.target.value as ClientCaseListStatus,
                              )
                            }
                            className="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus:border-slate-400 focus:outline-none focus:ring-2 focus:ring-slate-200"
                          >
                            <option value="all">Все статусы</option>
                            <option value="open">Открыто</option>
                            <option value="hold">Приостановлено</option>
                            <option value="closed">Закрыто</option>
                            <option value="lost">Проиграно</option>
                          </SystemSelect>
                        </ClientSectionFilterField>
                      </ClientSectionFilterPanel>

                      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                        {isSupplierCard
                          ? filteredSupplierCaseSummaries.map((finance) => {
                              const statusClass = "bg-slate-50 text-slate-700 border-slate-200";
                              return (
                                <article
                                  key={finance.caseId}
                                  className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm transition-colors hover:border-emerald-200 hover:shadow-md"
                                >
                                  <div className="flex h-full flex-col gap-4">
                                    <div className="min-w-0">
                                      <div className="flex flex-wrap items-center gap-2">
                                        <h3 className="truncate text-base font-semibold text-slate-900">
                                          {finance.caseTitle || `Дело #${finance.caseId}`}
                                        </h3>
                                        <span
                                          className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-medium ${statusClass}`}
                                        >
                                          {caseStatusLabel(finance.caseStatus ?? undefined)}
                                        </span>
                                      </div>
                                      <p className="mt-2 text-sm text-slate-500">
                                        Клиент: {finance.clientName || (finance.clientId ? `#${finance.clientId}` : "не указан")}
                                      </p>
                                      <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-slate-500">
                                        <span>
                                          Создано: {finance.caseCreatedAt ? toRuDate(finance.caseCreatedAt) : "—"}
                                        </span>
                                        <span>Расходов: {finance.expenseCount}</span>
                                      </div>
                                    </div>
                                    <div className="rounded-xl border border-slate-100 bg-slate-50 p-3">
                                      <div className="grid grid-cols-2 gap-2 text-xs">
                                        <div>
                                          <p className="text-slate-500">Начислено</p>
                                          <p className="mt-1 font-semibold text-rose-700">
                                            {formatCurrencySmart(finance.expenseTotal).full}
                                          </p>
                                        </div>
                                        <div>
                                          <p className="text-slate-500">Оплачено</p>
                                          <p className="mt-1 font-semibold text-emerald-700">
                                            {formatCurrencySmart(finance.expensePaid).full}
                                          </p>
                                        </div>
                                        <div>
                                          <p className="text-slate-500">Остаток</p>
                                          <p className="mt-1 font-semibold text-amber-700">
                                            {formatCurrencySmart(finance.expenseOutstanding).full}
                                          </p>
                                        </div>
                                        <div>
                                          <p className="text-slate-500">Платежей</p>
                                          <p className="mt-1 font-semibold text-slate-700">
                                            {finance.expenseCount}
                                          </p>
                                        </div>
                                      </div>
                                    </div>
                                    <div className="mt-auto flex flex-wrap gap-2">
                                      <button
                                        type="button"
                                        onClick={() => void openExpenseDetailsForCase(finance.caseId)}
                                        className="inline-flex h-10 items-center gap-2 rounded-lg border border-rose-200 bg-white px-3 text-sm font-medium text-rose-700 transition-colors hover:border-rose-300 hover:bg-rose-50"
                                      >
                                        <FileCheck2 size={16} /> Расшифровка
                                      </button>
                                      <button
                                        type="button"
                                        onClick={() => openSupplierPaymentsForCase(finance.caseId)}
                                        className="inline-flex h-10 items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 transition-colors hover:border-blue-200 hover:bg-blue-50 hover:text-blue-700"
                                      >
                                        <WalletCards size={16} /> Платежи поставщика
                                      </button>
                                    </div>
                                  </div>
                                </article>
                              );
                            })
                          : filteredCases.map((caseItem) => {
                          const statusValue = (
                            caseItem.status ?? ""
                          ).toLowerCase();
                          const finance = caseFinanceSummaries[caseItem.id];
                          const legacyCasePayments = (caseItem as ClientCase & { payments?: Payment[] }).payments;
                          const casePaymentsCount = finance
                            ? finance.incomeCount + finance.expenseCount
                            : legacyCasePayments?.length ?? 0;
                          const statusClass = statusValue.includes("open")
                            ? "bg-green-50 text-green-700 border-green-200"
                            : statusValue.includes("hold")
                              ? "bg-amber-50 text-amber-800 border-amber-200"
                              : statusValue.includes("closed")
                                ? "bg-red-50 text-red-700 border-red-200"
                                : "bg-slate-50 text-slate-700 border-slate-200";

                          return (
                            <article
                              key={caseItem.id}
                              className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm transition-colors hover:border-emerald-200 hover:shadow-md"
                            >
                              <div className="flex h-full flex-col gap-4">
                                <div className="min-w-0">
                                  <div className="flex flex-wrap items-center gap-2">
                                    <h3 className="truncate text-base font-semibold text-slate-900">
                                      {caseItem.title || `Дело #${caseItem.id}`}
                                    </h3>
                                    <span
                                      className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-medium ${statusClass}`}
                                    >
                                      {caseStatusLabel(caseItem.status)}
                                    </span>
                                  </div>
                                  <p className="mt-2 text-sm text-slate-500">
                                    {caseItem.description?.trim() ||
                                      "Описание дела пока не заполнено."}
                                  </p>
                                  <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-slate-500">
                                    {caseItem.caseNumber ? (
                                      <span>№ {caseItem.caseNumber}</span>
                                    ) : null}
                                    {caseItem.caseType ? (
                                      <span>Тип: {caseItem.caseType}</span>
                                    ) : null}
                                    {caseItem.stage ? (
                                      <span>Этап: {caseItem.stage}</span>
                                    ) : null}
                                    {caseItem.dueDate ? (
                                      <span>Срок: {toRuDate(caseItem.dueDate)}</span>
                                    ) : null}
                                    {caseItem.feeAmount ? (
                                      <span>
                                        Гонорар: {formatCurrencySmart(caseItem.feeAmount).full}
                                      </span>
                                    ) : null}
                                    <span>
                                      Создано: {toRuDate(caseItem.createdAt)}
                                    </span>
                                    <span>
                                      Платежей: {casePaymentsCount}
                                    </span>
                                  </div>
                                  {caseItem.courtName || caseItem.courtCaseNumber || caseItem.nextHearingAt ? (
                                    <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-slate-500">
                                      {caseItem.courtName ? <span>Суд: {caseItem.courtName}</span> : null}
                                      {caseItem.courtCaseNumber ? <span>Судебное № {caseItem.courtCaseNumber}</span> : null}
                                      {caseItem.nextHearingAt ? (
                                        <span>Заседание: {formatDateTimeInTimeZone(caseItem.nextHearingAt, profileTimeZoneId)}</span>
                                      ) : null}
                                    </div>
                                  ) : null}
                                  <div className="mt-2 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 text-xs text-slate-500">
                                    {caseAuditSummary(caseItem)}
                                  </div>
                                </div>
                                <div className="rounded-xl border border-slate-100 bg-slate-50 p-3">
                                  {finance ? (
                                    <div className="grid grid-cols-2 gap-2 text-xs">
                                      <button
                                        type="button"
                                        onClick={() =>
                                          openPaymentsForCase(
                                            caseItem.id,
                                            "Income",
                                          )
                                        }
                                        className="rounded-lg p-2 text-left transition hover:bg-white hover:shadow-sm"
                                      >
                                        <p className="text-slate-500">Доходы</p>
                                        <p className="mt-1 font-semibold text-emerald-700">
                                          {
                                            formatCurrencySmart(
                                              finance.incomeTotal,
                                            ).full
                                          }
                                        </p>
                                      </button>
                                      <button
                                        type="button"
                                        onClick={() =>
                                          void openExpenseDetailsForCase(
                                            caseItem.id,
                                          )
                                        }
                                        className="rounded-lg p-2 text-left transition hover:bg-white hover:shadow-sm"
                                      >
                                        <p className="text-slate-500">
                                          Расходы
                                        </p>
                                        <p className="mt-1 font-semibold text-rose-700">
                                          {
                                            formatCurrencySmart(
                                              finance.expenseTotal,
                                            ).full
                                          }
                                        </p>
                                      </button>
                                      <div>
                                        <p className="text-slate-500">Маржа</p>
                                        <p
                                          className={`mt-1 font-semibold ${
                                            finance.plannedMargin >= 0
                                              ? "text-emerald-700"
                                              : "text-rose-700"
                                          }`}
                                        >
                                          {
                                            formatCurrencySmart(
                                              finance.plannedMargin,
                                            ).full
                                          }
                                        </p>
                                      </div>
                                      <div>
                                        <p className="text-slate-500">Долги</p>
                                        <p className="mt-1 font-semibold text-amber-700">
                                          {
                                            formatCurrencySmart(
                                              finance.incomeOutstanding +
                                                finance.expenseOutstanding,
                                            ).full
                                          }
                                        </p>
                                      </div>
                                    </div>
                                  ) : (
                                    <p className="text-xs text-slate-500">
                                      {caseFinanceLoading
                                        ? "Считаем экономику дела..."
                                        : "Финансы по делу пока не связаны."}
                                    </p>
                                  )}
                                </div>
                                <div className="mt-auto flex items-center justify-between gap-2">
                                  <button
                                    type="button"
                                    onClick={() =>
                                      openPaymentsForCase(caseItem.id)
                                    }
                                    className="inline-flex h-10 items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 transition-colors hover:border-blue-200 hover:bg-blue-50 hover:text-blue-700"
                                  >
                                    <WalletCards size={16} /> Платежи
                                  </button>
                                  {onOpenCasePage ? (
                                    <button
                                      type="button"
                                      onClick={() => onOpenCasePage(caseItem.id, clientId)}
                                      className="inline-flex h-10 items-center gap-2 rounded-lg border border-emerald-200 bg-white px-3 text-sm font-medium text-emerald-700 transition-colors hover:bg-emerald-50"
                                    >
                                      <FileCheck2 size={16} /> Открыть дело
                                    </button>
                                  ) : null}
                                  {clientPermissions.canEdit ? (
                                    <button
                                      type="button"
                                      title="Редактировать дело"
                                      aria-label={`Редактировать дело ${caseItem.title || caseItem.id}`}
                                      onClick={() => {
                                        setEditingCase(caseItem);
                                        setCaseMode("edit");
                                        setCaseModalOpen(true);
                                      }}
                                      className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition-colors hover:border-emerald-200 hover:bg-emerald-50 hover:text-emerald-700"
                                    >
                                      <Edit size={16} />
                                    </button>
                                  ) : null}
                                </div>
                              </div>
                            </article>
                          );
                        })}
                      </div>
                    </>
                  )}
                </div>
              ) : activeSection === "payments" ? (
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
                            <SystemSelect
                              value={paymentPageSize}
                              onChange={(event) =>
                                setPaymentPageSize(
                                  Number(event.target.value) || paymentPageSize,
                                )
                              }
                              className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                            >
                              {DETAIL_PAGE_SIZE_OPTIONS.map((size) => (
                                <option key={size} value={size}>
                                  {size}
                                </option>
                              ))}
                            </SystemSelect>
                          </div>
                        }
                      />
                    </div>
                  ) : null}
                  {loadingPayments ? (
                    <div className="text-center py-8 text-gray-500">
                      Загрузка...
                    </div>
                  ) : paymentsError ? (
                    <div className="text-center py-8 text-red-600">
                      {paymentsError}
                    </div>
                  ) : visiblePayments.length === 0 ? (
                    <div className="text-center py-8">
                      <CalendarIcon
                        size={48}
                        className="mx-auto mb-4 text-gray-300"
                      />
                      <h3 className="text-lg font-medium text-gray-900 mb-2">
                        Платежи не найдены
                      </h3>
                      <p className="text-gray-500">
                        Нет платежей по выбранному фильтру
                      </p>
                    </div>
                  ) : (
                    <>
                      <div className="overflow-x-auto">
                        <table
                          className="min-w-full table-fixed divide-y divide-gray-200 text-sm"
                          style={{
                            width: getPaymentTableWidth(
                              paymentPermissions.canEdit ||
                                paymentPermissions.canDelete
                                ? 144
                                : 0,
                            ),
                            minWidth: "100%",
                          }}
                        >
                          <colgroup>
                            {visiblePaymentTableColumns.map((column) => (
                              <col
                                key={column.key}
                                style={{
                                  width: getPaymentColumnWidth(column.key),
                                }}
                              />
                            ))}
                            {paymentPermissions.canEdit ||
                            paymentPermissions.canDelete ? (
                              <col className="w-36" />
                            ) : null}
                          </colgroup>
                          <thead className="bg-slate-50">
                            <tr className="text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                              {visiblePaymentTableColumns.map((column) => (
                                <th
                                  key={column.key}
                                  scope="col"
                                  style={{
                                    width: getPaymentColumnWidth(column.key),
                                  }}
                                  className={`relative px-4 py-3 ${
                                    draggingPaymentColumn === column.key
                                      ? "bg-blue-50 text-blue-700"
                                      : ""
                                  }`}
                                  onDragOver={handlePaymentColumnDragOver}
                                  onDrop={(event) =>
                                    handlePaymentColumnDrop(column.key, event)
                                  }
                                >
                                  <div
                                    className="inline-flex min-w-0 select-none items-center gap-2"
                                    draggable
                                    onDragStart={(event) =>
                                      handlePaymentColumnDragStart(
                                        column.key,
                                        event,
                                      )
                                    }
                                    onDragEnd={handlePaymentColumnDragEnd}
                                    title="Перетащите, чтобы изменить порядок"
                                  >
                                    <GripVertical className="h-4 w-4 text-slate-400" />
                                    <span className="truncate">
                                      {column.label}
                                    </span>
                                  </div>
                                  <div
                                    role="separator"
                                    aria-label={`Изменить ширину колонки ${column.label}`}
                                    className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                                    onMouseDown={(event) =>
                                      handlePaymentResizeStart(
                                        column.key,
                                        event,
                                      )
                                    }
                                    onDragStart={(event) =>
                                      event.preventDefault()
                                    }
                                  />
                                </th>
                              ))}
                              {(paymentPermissions.canEdit ||
                                paymentPermissions.canDelete) && (
                                <th
                                  scope="col"
                                  className="px-4 py-3 text-right"
                                >
                                  Действия
                                </th>
                              )}
                            </tr>
                          </thead>
                          <tbody className="divide-y divide-gray-100 text-slate-700">
                            {visiblePayments.map((payment) => {
                              const status = normalizeStatus(payment.status);
                              const statusLabel = toRuPaymentStatus(
                                payment.paymentStatusEntity?.name ??
                                  payment.status,
                                payment.paymentStatusEntity?.name ||
                                  payment.status ||
                                  "—",
                              );
                              const statusBadgeClass =
                                status === "completed"
                                  ? "bg-emerald-50 text-emerald-700 border border-emerald-100"
                                  : status === "overdue"
                                    ? "bg-purple-50 text-purple-700 border border-purple-100"
                                    : "bg-amber-50 text-amber-700 border border-amber-100";
                              const StatusIcon =
                                status === "completed"
                                  ? CheckCircle
                                  : status === "overdue"
                                    ? AlertTriangle
                                    : Clock;
                              const directionLabel =
                                payment.dealType?.name ||
                                payment.incomeType?.name ||
                                payment.paymentSource?.name ||
                                "—";
                              const secondaryDirections = [
                                payment.dealType?.name,
                                payment.incomeType?.name,
                                payment.paymentSource?.name,
                              ]
                                .filter((item): item is string =>
                                  Boolean(item && item !== directionLabel),
                                )
                                .filter(
                                  (value, index, self) =>
                                    self.indexOf(value) === index,
                                );
                              const DirectionIcon =
                                payment.type === "Income"
                                  ? TrendingUp
                                  : TrendingDown;
                              const hasProgress =
                                payment.hasPartialPayment &&
                                payment.paidAmount > 0;
                              const paidProgress =
                                payment.amount > 0
                                  ? Math.min(
                                      payment.paidAmount / payment.amount,
                                      1,
                                    )
                                  : 0;
                              const installmentMeta =
                                parseInstallmentPaymentMetadata(
                                  payment.systemNotes,
                                );
                              const visibleSystemNotes = getVisibleSystemNotes(
                                payment.systemNotes,
                              );
                              const visibleNotes = getVisibleNotes(
                                payment.notes,
                              );
                              const systemNoteExpanded =
                                expandedSystemNotePaymentIds.has(payment.id);
                              const systemNoteExpandable =
                                visibleSystemNotes.length > 180 ||
                                visibleSystemNotes.split(/\r?\n/).length > 4;
                              const paymentInvoice = getPaymentInvoice(payment);
                              const paymentActs = paymentInvoice
                                ? getInvoiceActs(paymentInvoice)
                                : [];
                              const paymentContract = payment.contractId
                                ? relatedContractById.get(payment.contractId)
                                : paymentInvoice?.contractId
                                  ? relatedContractById.get(paymentInvoice.contractId)
                                  : null;

                              return (
                                <tr
                                  key={payment.id}
                                  className="align-top transition-colors hover:bg-slate-50/70"
                                >
                                  {visiblePaymentTableColumns.map((column) => (
                                    <td
                                      key={column.key}
                                      style={{
                                        width: getPaymentColumnWidth(
                                          column.key,
                                        ),
                                      }}
                                      className="max-w-0 overflow-hidden px-4 py-4 align-top"
                                    >
                                      {column.key === "period" ? (
                                        <div className="text-slate-600">
                                          <div className="font-medium text-slate-900">
                                            {toRuDate(payment.date)}
                                          </div>
                                          {payment.plannedDate &&
                                          payment.plannedDate !==
                                            payment.date ? (
                                            <div className="mt-1 text-xs text-slate-500">
                                              План:{" "}
                                              {toRuDate(payment.plannedDate)}
                                            </div>
                                          ) : null}
                                          {payment.paidDate ? (
                                            <div className="mt-1 text-xs text-emerald-600">
                                              Оплачен:{" "}
                                              {toRuDate(payment.paidDate)}
                                            </div>
                                          ) : null}
                                          <div className="mt-2 text-xs text-slate-400">
                                            Создан:{" "}
                                            {toRuDate(payment.createdAt)}
                                          </div>
                                          {payment.rescheduleCount > 0 ? (
                                            <div className="mt-2 inline-flex items-center rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-[11px] font-medium text-amber-700">
                                              <RotateCcw
                                                size={12}
                                                className="mr-1"
                                              />{" "}
                                              Переносов:{" "}
                                              {payment.rescheduleCount}
                                            </div>
                                          ) : null}
                                        </div>
                                      ) : column.key === "amountType" ? (
                                        <>
                                          <div
                                            className={`flex items-center gap-2 text-sm font-semibold ${
                                              payment.type === "Income"
                                                ? "text-emerald-600"
                                                : "text-rose-600"
                                            }`}
                                            title={
                                              formatCurrencySmart(
                                                payment.amount,
                                                { alwaysCents: true },
                                              ).full
                                            }
                                          >
                                            <DirectionIcon size={18} />
                                            {
                                              formatCurrencySmart(
                                                payment.amount,
                                              ).full
                                            }
                                          </div>
                                          <div className="mt-2 inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] font-medium text-slate-600">
                                            {payment.type === "Income"
                                              ? "Поступление"
                                              : "Списание"}
                                          </div>
                                          {installmentMeta ? (
                                            <div className="mt-2 inline-flex items-center rounded-full border border-violet-200 bg-violet-50 px-2 py-0.5 text-[11px] font-medium text-violet-700">
                                              Рассрочка{" "}
                                              {installmentMeta.sequence}/
                                              {Math.max(
                                                installmentMeta.months,
                                                installmentMeta.sequence,
                                              )}
                                            </div>
                                          ) : null}
                                          {payment.outstandingAmount > 0 ? (
                                            <div className="mt-2 text-xs text-rose-600">
                                              Осталось оплатить:{" "}
                                              {
                                                formatCurrencySmart(
                                                  payment.outstandingAmount,
                                                ).full
                                              }
                                            </div>
                                          ) : null}
                                          {payment.hasPartialPayment &&
                                          payment.paidAmount > 0 ? (
                                            <div className="mt-2">
                                              <div className="flex items-center justify-between text-xs text-slate-500">
                                                <span>Оплачено</span>
                                                <span className="font-medium text-slate-700">
                                                  {
                                                    formatCurrencySmart(
                                                      payment.paidAmount,
                                                    ).full
                                                  }
                                                </span>
                                              </div>
                                              <div className="mt-1 h-1.5 w-full rounded-full bg-slate-200">
                                                <div
                                                  className={`h-full rounded-full ${
                                                    payment.type === "Income"
                                                      ? "bg-emerald-500"
                                                      : "bg-rose-500"
                                                  }`}
                                                  style={{
                                                    width: `${Math.max(0, Math.min(100, paidProgress * 100))}%`,
                                                  }}
                                                />
                                              </div>
                                            </div>
                                          ) : null}
                                        </>
                                      ) : column.key === "category" ? (
                                        <>
                                          <div className="font-medium text-slate-900">
                                            {directionLabel}
                                          </div>
                                          {secondaryDirections.length ? (
                                            <ul className="mt-1 space-y-0.5 text-xs text-slate-500">
                                              {secondaryDirections.map(
                                                (label) => (
                                                  <li key={label}>{label}</li>
                                                ),
                                              )}
                                            </ul>
                                          ) : null}
                                          {payment.account ? (
                                            <div className="mt-2 text-xs text-slate-500">
                                              Счёт: {payment.account}
                                            </div>
                                          ) : null}
                                          {payment.paymentOrderNumber ? (
                                            <div className="text-xs text-slate-500">
                                              Документ: {payment.paymentOrderNumber}
                                              {payment.paymentDocumentDate
                                                ? ` от ${toRuDate(payment.paymentDocumentDate)}`
                                                : ""}
                                            </div>
                                          ) : null}
                                          {!payment.paymentOrderNumber &&
                                          payment.paymentDocumentDate ? (
                                            <div className="text-xs text-slate-500">
                                              Дата документа:{" "}
                                              {toRuDate(payment.paymentDocumentDate)}
                                            </div>
                                          ) : null}
                                          {payment.accountDate ? (
                                            <div className="text-xs text-slate-400">
                                              Дата счёта:{" "}
                                              {toRuDate(payment.accountDate)}
                                            </div>
                                          ) : null}
                                        </>
                                      ) : column.key === "caseContact" ? (
                                        <>
                                          {payment.clientCase?.title ? (
                                            <div className="font-medium text-slate-900">
                                              {payment.clientCase.title}
                                            </div>
                                          ) : (
                                            <div className="text-xs text-slate-400">
                                              Дело не указано
                                            </div>
                                          )}
                                          {payment.client?.name ? (
                                            <div className="mt-1 text-xs text-slate-500">
                                              {payment.type === "Expense"
                                                ? `Поставщик: ${payment.client.name}`
                                                : payment.client.name}
                                            </div>
                                          ) : null}
                                          {payment.type === "Expense" &&
                                          payment.expenseClient?.name ? (
                                            <div className="mt-1 text-xs text-slate-500">
                                              Клиент расхода:{" "}
                                              {payment.expenseClient.name}
                                            </div>
                                          ) : null}
                                          {payment.clientCase?.status ? (
                                            <div className="mt-1 inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] text-slate-600">
                                              {caseStatusLabel(
                                                payment.clientCase.status,
                                              )}
                                            </div>
                                          ) : null}
                                        </>
                                      ) : column.key === "details" ? (
                                        <>
                                          {payment.description ? (
                                            <div className="text-slate-800">
                                              {payment.description}
                                            </div>
                                          ) : (
                                            <div className="text-xs text-slate-400">
                                              Без описания
                                            </div>
                                          )}
                                          {payment.invoiceId || payment.account ? (
                                            <div className="mt-2 text-xs text-slate-500">
                                              Счёт:{" "}
                                              {payment.account?.trim() ||
                                                `ID ${payment.invoiceId}`}
                                            </div>
                                          ) : null}
                                          {visibleNotes ? (
                                            <div
                                              className="mt-2 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 text-xs text-slate-600"
                                              title={visibleNotes}
                                            >
                                              <span className="font-medium text-slate-700">
                                                Заметка:
                                              </span>{" "}
                                              {visibleNotes}
                                            </div>
                                          ) : null}
                                          {visibleSystemNotes ? (
                                            <div
                                              className="mt-2 rounded-lg border border-slate-100 bg-white px-3 py-2 text-xs text-slate-500"
                                              title={visibleSystemNotes}
                                            >
                                              <div
                                                className={
                                                  systemNoteExpanded
                                                    ? "whitespace-pre-wrap leading-5"
                                                    : "max-h-20 overflow-hidden whitespace-pre-wrap leading-5"
                                                }
                                              >
                                                <span className="font-medium text-slate-600">
                                                  Системно:
                                                </span>{" "}
                                                {visibleSystemNotes}
                                              </div>
                                              {systemNoteExpandable ? (
                                                <button
                                                  type="button"
                                                  className="mt-1 text-[11px] font-medium text-blue-600 hover:text-blue-700"
                                                  onClick={() =>
                                                    togglePaymentSystemNote(
                                                      payment.id,
                                                    )
                                                  }
                                                >
                                                  {systemNoteExpanded
                                                    ? "Свернуть"
                                                    : "Показать полностью"}
                                                </button>
                                              ) : null}
                                            </div>
                                          ) : null}
                                        </>
                                      ) : column.key === "linkedContract" ? (
                                        renderRelatedChips(
                                          paymentContract ? [createContractChip(paymentContract)] : [],
                                        )
                                      ) : column.key === "linkedInvoice" ? (
                                        renderRelatedChips(
                                          paymentInvoice ? [createInvoiceChip(paymentInvoice)] : [],
                                        )
                                      ) : column.key === "linkedActs" ? (
                                        renderRelatedChips(
                                          paymentActs.slice(0, 3).map(createActChip),
                                        )
                                      ) : (
                                        <>
                                          <div
                                            className={`inline-flex items-center gap-2 rounded-full px-3 py-1 text-xs font-medium ${statusBadgeClass}`}
                                          >
                                            <StatusIcon size={14} />
                                            <span className="leading-none">
                                              {statusLabel}
                                            </span>
                                          </div>
                                          {payment.delayDays ? (
                                            <div className="mt-2 inline-flex items-center rounded-full border border-purple-200 bg-purple-50 px-2 py-0.5 text-[11px] font-medium text-purple-700">
                                              <AlertTriangle
                                                size={12}
                                                className="mr-1"
                                              />{" "}
                                              Просрочка: {payment.delayDays} дн.
                                            </div>
                                          ) : null}
                                          {payment.lastPaymentDate ? (
                                            <div className="mt-2 text-xs text-slate-500">
                                              Последний платёж:{" "}
                                              {toRuDate(
                                                payment.lastPaymentDate,
                                              )}
                                            </div>
                                          ) : null}
                                          {payment.hasPartialPayment &&
                                          !hasProgress ? (
                                            <div className="mt-2 text-xs text-slate-500">
                                              Оплачено частично
                                            </div>
                                          ) : null}
                                        </>
                                      )}
                                    </td>
                                  ))}
                                  {(paymentPermissions.canEdit ||
                                    paymentPermissions.canDelete) && (
                                    <td className="px-4 py-4">
                                      <div className="flex items-center justify-end gap-2">
                                        {paymentPermissions.canEdit ? (
                                          <button
                                            type="button"
                                            className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-200 text-blue-600 transition-colors hover:border-blue-300 hover:bg-blue-50"
                                            onClick={() =>
                                              openEditPayment(payment)
                                            }
                                            title="Редактировать"
                                            aria-label="Редактировать"
                                          >
                                            <Edit size={16} />
                                          </button>
                                        ) : null}
                                        {paymentPermissions.canDelete &&
                                        payment.documentKind !== "Invoice" &&
                                        !isTransferredRemainderPayment(payment) &&
                                        !isPartialRemainderSourcePayment(payment) ? (
                                          <button
                                            type="button"
                                            className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-200 text-rose-600 transition-colors hover:border-rose-300 hover:bg-rose-50"
                                            onClick={() =>
                                              removePayment(payment)
                                            }
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
              ) : activeSection === "accounts" ? (
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
                            <SystemSelect
                              value={invoicePageSize}
                              onChange={(event) =>
                                setInvoicePageSize(
                                  Number(event.target.value) || invoicePageSize,
                                )
                              }
                              className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                            >
                              {DETAIL_PAGE_SIZE_OPTIONS.map((size) => (
                                <option key={size} value={size}>
                                  {size}
                                </option>
                              ))}
                            </SystemSelect>
                          </div>
                        }
                      />
                    </div>
                  ) : null}
                  {invoicesLoading ? (
                    <div className="py-10 text-center text-gray-500">
                      <div className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t("loading") ?? "Загрузка..."}
                      </div>
                    </div>
                  ) : clientInvoices.length === 0 ? (
                    <div className="py-10 text-center text-gray-500">
                      {invoicesError ??
                        t("invoicesEmpty") ??
                        "Счета не найдены"}
                    </div>
                  ) : (
                    <div className="overflow-x-auto">
                      <table
                        className="min-w-full table-fixed divide-y divide-gray-200"
                        style={{ width: getInvoiceTableWidth(110), minWidth: "100%" }}
                      >
                        <colgroup>
                          {visibleInvoiceTableColumns.map((column) => (
                            <col
                              key={column.key}
                              style={{
                                width: getInvoiceColumnWidth(column.key),
                              }}
                            />
                          ))}
                          <col style={{ width: 110 }} />
                        </colgroup>
                        <thead className="bg-gray-50">
                          <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                            {visibleInvoiceTableColumns.map((column) => {
                              const sortKey =
                                column.key === "linkedContract" ||
                                column.key === "linkedActs" ||
                                column.key === "linkedPayments" ||
                                column.key === "signingStatus"
                                  ? null
                                  : (column.key as InvoicesSortKey);
                              const isActive = sortKey
                                ? invoiceSort.key === sortKey
                                : false;
                              const direction = isActive
                                ? invoiceSort.direction
                                : undefined;
                              return (
                                <th
                                  key={column.key}
                                  scope="col"
                                  style={{
                                    width: getInvoiceColumnWidth(column.key),
                                  }}
                                  className={`relative px-4 py-3 ${draggingInvoiceColumn === column.key ? "bg-blue-50 text-blue-700" : ""}`}
                                  onDragOver={handleInvoiceColumnDragOver}
                                  onDrop={(event) =>
                                    handleInvoiceColumnDrop(column.key, event)
                                  }
                                >
                                  {sortKey ? (
                                    <button
                                      type="button"
                                      onClick={() => handleInvoiceSort(sortKey)}
                                      className="flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-gray-500"
                                    >
                                      <span
                                        className="inline-flex min-w-0 select-none items-center gap-2"
                                        draggable
                                        onDragStart={(event) =>
                                          handleInvoiceColumnDragStart(
                                            column.key,
                                            event,
                                          )
                                        }
                                        onDragEnd={handleInvoiceColumnDragEnd}
                                        title="Перетащите, чтобы изменить порядок"
                                      >
                                        <GripVertical className="h-4 w-4 text-slate-400" />
                                        <span className="truncate">
                                          {column.label}
                                        </span>
                                      </span>
                                      <span className="text-gray-400">
                                        {isActive
                                          ? direction === "asc"
                                            ? "▲"
                                            : "▼"
                                          : ""}
                                      </span>
                                    </button>
                                  ) : (
                                    <div
                                      className="inline-flex min-w-0 select-none items-center gap-2"
                                      draggable
                                      onDragStart={(event) =>
                                        handleInvoiceColumnDragStart(
                                          column.key,
                                          event,
                                        )
                                      }
                                      onDragEnd={handleInvoiceColumnDragEnd}
                                      title="Перетащите, чтобы изменить порядок"
                                    >
                                      <GripVertical className="h-4 w-4 text-slate-400" />
                                      <span className="truncate">
                                        {column.label}
                                      </span>
                                    </div>
                                  )}
                                  <div
                                    role="separator"
                                    aria-label={`Изменить ширину колонки ${column.label}`}
                                    className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                                    onMouseDown={(event) =>
                                      handleInvoiceResizeStart(
                                        column.key,
                                        event,
                                      )
                                    }
                                    onDragStart={(event) =>
                                      event.preventDefault()
                                    }
                                  />
                                </th>
                              );
                            })}
                            <th scope="col" className="px-4 py-3 text-right">
                              {t("actions") ?? "Действия"}
                            </th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                          {clientInvoices.map((invoice) => (
                            <tr
                              key={invoice.id}
                              className="hover:bg-gray-50/80"
                            >
                              {visibleInvoiceTableColumns.map((column) => (
                                <td
                                  key={column.key}
                                  style={{
                                    width: getInvoiceColumnWidth(column.key),
                                  }}
                                  className="max-w-0 overflow-hidden px-4 py-3 align-top"
                                >
                                  {column.key === "date" ? (
                                    <span className="font-medium text-gray-900">
                                      {toRuDate(invoice.date)}
                                    </span>
                                  ) : column.key === "number" ? (
                                    (() => {
                                      const caseLabels = invoice.clientCases
                                        ?.length
                                        ? invoice.clientCases.map(
                                            (item) =>
                                              item.title?.trim() ||
                                              `Дело #${item.id}`,
                                          )
                                        : invoice.clientCaseTitle
                                          ? [invoice.clientCaseTitle]
                                          : invoice.clientCaseId
                                            ? [`Дело #${invoice.clientCaseId}`]
                                            : [];
                                      const actLabels = invoice.acts?.length
                                        ? invoice.acts.map((item) => {
                                            const parts: string[] = [];
                                            if (item.number)
                                              parts.push(`№${item.number}`);
                                            if (item.title)
                                              parts.push(item.title);
                                            return (
                                              parts.join(" · ") ||
                                              `Акт #${item.id}`
                                            );
                                          })
                                        : invoice.actNumber
                                          ? [
                                              `${t("invoiceAct") ?? "Акт"} №${invoice.actNumber}${invoice.actTitle ? ` · ${invoice.actTitle}` : ""}`,
                                            ]
                                          : [];

                                      return (
                                        <>
                                          {accountPermissions.canView ? (
                                            <a
                                              href={`/invoices/${invoice.id}`}
                                              onClick={(event) =>
                                                handleInvoicePageLinkClick(
                                                  event,
                                                  invoice.id,
                                                )
                                              }
                                              className="font-medium text-blue-700 transition-colors hover:text-blue-800 hover:underline"
                                              title={`Открыть счет №${invoice.number}`}
                                            >
                                              №{invoice.number}
                                            </a>
                                          ) : (
                                            <div className="font-medium text-gray-900">
                                              №{invoice.number}
                                            </div>
                                          )}
                                          {caseLabels.map((label) => (
                                            <div
                                              key={label}
                                              className="text-xs text-gray-500"
                                            >
                                              {label}
                                            </div>
                                          ))}
                                          {actLabels.map((label) => (
                                            <div
                                              key={label}
                                              className="text-xs text-gray-500"
                                            >
                                              {label}
                                            </div>
                                          ))}
                                        </>
                                      );
                                    })()
                                  ) : column.key === "counterparty" ? (
                                    (() => {
                                      const supplierName =
                                        invoice.clientName ||
                                        invoice.clientCompany ||
                                        "—";
                                      const expenseClientName =
                                        invoice.expenseClientName ||
                                        invoice.expenseClientCompany ||
                                        "";

                                      return (
                                        <div className="space-y-1">
                                          <div className="font-medium text-gray-900">
                                            {supplierName}
                                          </div>
                                          <div className="text-xs text-gray-500">
                                            {invoice.type === "Expense"
                                              ? "Поставщик"
                                              : "Клиент"}
                                          </div>
                                          {invoice.type === "Expense" &&
                                          expenseClientName ? (
                                            <div className="text-xs text-gray-500">
                                              Клиент расхода:{" "}
                                              {expenseClientName}
                                            </div>
                                          ) : null}
                                        </div>
                                      );
                                    })()
                                  ) : column.key === "amount" ? (
                                    <span className="font-semibold text-gray-900">
                                      {formatCurrencySmart(invoice.amount).full}
                                    </span>
                                  ) : column.key === "status" ? (
                                    <span
                                      className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${invoiceStatusClasses[invoice.status]}`}
                                    >
                                      {invoiceStatusLabels[invoice.status]}
                                    </span>
                                  ) : column.key === "signingStatus" ? (
                                    (() => {
                                      const workflowStatus =
                                        (invoice.workflowStatus ?? "Draft") as InvoiceWorkflowStatus;
                                      const label =
                                        invoiceWorkflowStatusLabels[workflowStatus] ??
                                        workflowStatus;
                                      const className =
                                        invoiceWorkflowStatusClasses[workflowStatus] ??
                                        invoiceWorkflowStatusClasses.Draft;

                                      return (
                                        <span
                                          className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${className}`}
                                        >
                                          {label}
                                        </span>
                                      );
                                    })()
                                  ) : column.key === "dueDate" ? (
                                    <span className="text-gray-900">
                                      {invoice.dueDate
                                        ? toRuDate(invoice.dueDate)
                                        : "—"}
                                      {invoice.paidDate ? (
                                        <div className="text-xs text-emerald-600">
                                          {(t("invoicePaidOn") ?? "Оплачен") +
                                            ": "}{" "}
                                          {toRuDate(invoice.paidDate)}
                                        </div>
                                      ) : null}
                                    </span>
                                  ) : column.key === "responsible" ? (
                                    <span className="text-gray-900">
                                      {invoice.responsibleName ?? "—"}
                                    </span>
                                  ) : column.key === "linkedContract" ? (
                                    (() => {
                                      const contract = invoice.contractId
                                        ? relatedContractById.get(invoice.contractId)
                                        : null;
                                      return renderRelatedChips(
                                        contract ? [createContractChip(contract)] : [],
                                      );
                                    })()
                                  ) : column.key === "linkedActs" ? (
                                    renderRelatedChips(
                                      getInvoiceActs(invoice).slice(0, 3).map(createActChip),
                                    )
                                  ) : column.key === "linkedPayments" ? (
                                    renderRelatedChips(
                                      getInvoicePayments(invoice).slice(0, 3).map(createPaymentChip),
                                    )
                                  ) : (
                                    <span className="text-gray-900">
                                      {toRuDate(invoice.createdAt)}
                                    </span>
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
                                      title={t("edit") ?? "Редактировать"}
                                      aria-label={t("edit") ?? "Редактировать"}
                                    >
                                      <Edit className="h-4 w-4" />
                                    </button>
                                  )}
                                  {accountPermissions.canDelete && (
                                    <button
                                      type="button"
                                      onClick={() => removeInvoice(invoice)}
                                      className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                      title={t("delete") ?? "Удалить"}
                                      aria-label={t("delete") ?? "Удалить"}
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
                  {!invoicesLoading &&
                  (invoicesPagination.total ?? clientInvoices.length) > 0 ? (
                    <>
                      <TablePagination
                        page={invoicesPagination.page || invoicePage}
                        totalPages={invoiceTotalPages}
                        totalItems={
                          invoicesPagination.total ?? clientInvoices.length
                        }
                        pageSize={
                          invoicesPagination.pageSize || invoicePageSize
                        }
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
              ) : activeSection === "acts" ? (
                <>
                  <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
                    <div>
                      <h3 className="text-lg font-semibold text-gray-900">
                        {t("actSectionTitle") ?? "Сводка и список"}
                      </h3>
                      <p className="text-sm text-gray-500">
                        {t("actSectionDescription") ??
                          "Отслеживайте статусы и суммы по документам клиента."}
                      </p>
                    </div>
                    <div className="flex flex-wrap items-center gap-2">
                      <button
                        type="button"
                        onClick={() => {
                          void refreshActs();
                        }}
                        className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50"
                      >
                        <RotateCcw
                          className={
                            actsRefreshing ? "h-4 w-4 animate-spin" : "h-4 w-4"
                          }
                        />
                        {t("invoiceRefresh") ??
                          t("actFiltersApply") ??
                          "Обновить"}
                      </button>
                    </div>
                  </div>

                  <div className="mb-6 grid gap-4 md:grid-cols-3">
                    {actSummaryCards.map((card) => {
                      const Icon = card.icon;
                      return (
                        <div
                          key={card.key}
                          className={`rounded-2xl border bg-white p-5 shadow-sm ${card.accent}`}
                        >
                          <div className="flex items-center justify-between">
                            <div>
                              <p className="text-sm font-medium text-gray-700">
                                {card.title}
                              </p>
                              <p className="mt-2 text-2xl font-semibold text-gray-900">
                                {formatCurrencySmart(card.bucket.amount).full}
                              </p>
                            </div>
                            <Icon className="h-10 w-10 opacity-80" />
                          </div>
                          <p className="mt-4 text-xs text-gray-500">
                            {t("actsSummaryCount") ?? "Количество"}:{" "}
                            {card.bucket.count}
                          </p>
                        </div>
                      );
                    })}
                  </div>

                  <div className="mb-4 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-gray-50 p-4">
                    <div className="flex items-center gap-2">
                      <CalendarRange className="h-4 w-4 text-gray-500" />
                      <SystemDateInput
                        type="date"
                        value={actFrom}
                        onChange={(event) =>
                          handleActFromChange(event.target.value)
                        }
                        className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                      />
                      <span className="text-gray-500">—</span>
                      <SystemDateInput
                        type="date"
                        value={actTo}
                        onChange={(event) =>
                          handleActToChange(event.target.value)
                        }
                        className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                      />
                    </div>
                    <div>
                      <SystemSelect
                        aria-label="Статус актов клиента"
                        value={actStatusFilter}
                        onChange={(event) =>
                          setActStatusFilter(
                            event.target.value as "all" | ActStatus,
                          )
                        }
                        className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                      >
                        <option value="all">
                          {t("allStatuses") ?? "Все статусы"}
                        </option>
                        {ACT_STATUS_ORDER.map((status) => (
                          <option key={status} value={status}>
                            {actStatusLabels[status]}
                          </option>
                        ))}
                      </SystemSelect>
                    </div>
                    {renderRelatedFilterSelect(
                      "Договор",
                      relatedContractFilter,
                      setRelatedContractFilter,
                      relatedContractOptions,
                      "Все договоры",
                      formatContractFilterLabel,
                      "w-full sm:w-64",
                    )}
                    {renderRelatedFilterSelect(
                      "Счет",
                      relatedInvoiceFilter,
                      setRelatedInvoiceFilter,
                      relatedInvoiceOptions,
                      "Все счета",
                      formatInvoiceFilterLabel,
                      "w-full sm:w-64",
                    )}
                    {renderRelatedFilterSelect(
                      "Платеж",
                      relatedPaymentFilter,
                      setRelatedPaymentFilter,
                      relatedPaymentOptions,
                      "Все платежи",
                      formatPaymentFilterLabel,
                      "w-full sm:w-64",
                    )}
                    <div className="relative flex-1 min-w-[220px]">
                      <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                      <input
                        type="search"
                        aria-label="Поиск актов клиента"
                        value={actSearch}
                        onChange={(event) => setActSearch(event.target.value)}
                        placeholder={
                          t("actSearchPlaceholder") ?? "Номер акта или название"
                        }
                        className="w-full rounded-lg border border-gray-300 pl-9 pr-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                      />
                    </div>
                    <button
                      type="button"
                      onClick={resetActFilters}
                      className="ml-auto inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                    >
                      {t("actFiltersReset") ?? "Сбросить"}
                    </button>
                  </div>

                  {actFilterBadges.length > 0 ? (
                    <div className="mb-6 flex flex-wrap items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
                      <span className="text-sm font-semibold text-slate-700">
                        Применены фильтры:
                      </span>
                      {actFilterBadges.map((badge) => (
                        <span
                          key={badge}
                          className="inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-medium text-slate-600"
                        >
                          {badge}
                        </span>
                      ))}
                      <button
                        type="button"
                        onClick={resetActFilters}
                        className="ml-auto inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                      >
                        <RotateCcw className="h-4 w-4" />
                        Сбросить все
                      </button>
                    </div>
                  ) : null}

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
                            <SystemSelect
                              value={actPageSize}
                              onChange={(event) =>
                                setActPageSize(
                                  Number(event.target.value) || actPageSize,
                                )
                              }
                              className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                            >
                              {DETAIL_PAGE_SIZE_OPTIONS.map((size) => (
                                <option key={size} value={size}>
                                  {size}
                                </option>
                              ))}
                            </SystemSelect>
                          </div>
                        }
                      />
                    </div>
                  ) : null}

                  <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
                    <div className="overflow-x-auto">
                      <table
                        className="min-w-full table-fixed divide-y divide-gray-200"
                        style={{ width: getActTableWidth(110), minWidth: "100%" }}
                      >
                        <colgroup>
                          {visibleActTableColumns.map((column) => (
                            <col
                              key={column.key}
                              style={{ width: getActColumnWidth(column.key) }}
                            />
                          ))}
                          <col style={{ width: 110 }} />
                        </colgroup>
                        <thead className="bg-gray-50">
                          <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                            {visibleActTableColumns.map((column) => {
                              const sortKey =
                                column.key === "title" ||
                                column.key === "linkedContract" ||
                                column.key === "linkedInvoice" ||
                                column.key === "linkedPayments"
                                  ? null
                                  : (column.key as ActsSortKey);
                              const isActive = sortKey
                                ? actSort.key === sortKey
                                : false;
                              const direction = isActive
                                ? actSort.direction
                                : undefined;
                              return (
                                <th
                                  key={column.key}
                                  scope="col"
                                  style={{
                                    width: getActColumnWidth(column.key),
                                  }}
                                  className={`relative px-4 py-3 ${draggingActColumn === column.key ? "bg-blue-50 text-blue-700" : ""}`}
                                  onDragOver={handleActColumnDragOver}
                                  onDrop={(event) =>
                                    handleActColumnDrop(column.key, event)
                                  }
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
                                        onDragStart={(event) =>
                                          handleActColumnDragStart(
                                            column.key,
                                            event,
                                          )
                                        }
                                        onDragEnd={handleActColumnDragEnd}
                                        title="Перетащите, чтобы изменить порядок"
                                      >
                                        <GripVertical className="h-4 w-4 text-slate-400" />
                                        <span className="truncate">
                                          {column.label}
                                        </span>
                                      </span>
                                      <span className="text-gray-400">
                                        {isActive
                                          ? direction === "asc"
                                            ? "▲"
                                            : "▼"
                                          : ""}
                                      </span>
                                    </button>
                                  ) : (
                                    <div
                                      className="inline-flex min-w-0 select-none items-center gap-2"
                                      draggable
                                      onDragStart={(event) =>
                                        handleActColumnDragStart(
                                          column.key,
                                          event,
                                        )
                                      }
                                      onDragEnd={handleActColumnDragEnd}
                                      title="Перетащите, чтобы изменить порядок"
                                    >
                                      <GripVertical className="h-4 w-4 text-slate-400" />
                                      <span className="truncate">
                                        {column.label}
                                      </span>
                                    </div>
                                  )}
                                  <div
                                    role="separator"
                                    aria-label={`Изменить ширину колонки ${column.label}`}
                                    className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                                    onMouseDown={(event) =>
                                      handleActResizeStart(column.key, event)
                                    }
                                    onDragStart={(event) =>
                                      event.preventDefault()
                                    }
                                  />
                                </th>
                              );
                            })}
                            <th scope="col" className="px-4 py-3 text-right">
                              {t("actions") ?? "Действия"}
                            </th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                          {actsLoading ? (
                            <tr>
                              <td
                                colSpan={visibleActTableColumns.length + 1}
                                className="py-10 text-center text-gray-500"
                              >
                                <div className="inline-flex items-center gap-2">
                                  <Loader2 className="h-4 w-4 animate-spin" />
                                  {t("loading") ?? "Загрузка..."}
                                </div>
                              </td>
                            </tr>
                          ) : clientActs.length === 0 ? (
                            <tr>
                              <td
                                colSpan={visibleActTableColumns.length + 1}
                                className="py-10 text-center text-gray-500"
                              >
                                {actsError ??
                                  t("actsEmpty") ??
                                  "Документы не найдены"}
                              </td>
                            </tr>
                          ) : (
                            <>
                              {clientActs.map((act) => {
                                const title = act.title?.trim();
                                const displayTitle =
                                  title && title !== `№${act.number}`
                                    ? title
                                    : `Акт №${act.number}`;
                                return (
                                  <tr
                                    key={act.id}
                                    className="hover:bg-gray-50/80"
                                  >
                                    {visibleActTableColumns.map((column) => (
                                      <td
                                        key={column.key}
                                        style={{
                                          width: getActColumnWidth(column.key),
                                        }}
                                        className="max-w-0 overflow-hidden px-4 py-3 align-top"
                                      >
                                        {column.key === "date" ? (
                                          <span className="font-medium text-gray-900">
                                            {toRuDate(act.date)}
                                          </span>
                                        ) : column.key === "number" ? (
                                          <>
                                            <div className="font-medium text-gray-900">
                                              №{act.number}
                                            </div>
                                            {act.invoiceNumber && (
                                              <div className="text-xs text-gray-500">
                                                {t("actInvoice") ?? "Счёт"}{" "}
                                                {act.invoiceNumber}
                                              </div>
                                            )}
                                          </>
                                        ) : column.key === "title" ? (
                                          <>
                                            <div className="font-medium text-gray-900">
                                              {displayTitle}
                                            </div>
                                            {act.comment && (
                                              <div className="text-xs text-gray-500">
                                                {act.comment}
                                              </div>
                                            )}
                                          </>
                                        ) : column.key === "amount" ? (
                                          <span className="font-semibold text-gray-900">
                                            {
                                              formatCurrencySmart(act.amount)
                                                .full
                                            }
                                          </span>
                                        ) : column.key === "status" ? (
                                          <span
                                            className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${ACT_STATUS_CLASSES[act.status]}`}
                                          >
                                            {actStatusLabels[act.status]}
                                          </span>
                                        ) : column.key === "linkedContract" ? (
                                          (() => {
                                            const invoice = getActInvoice(act);
                                            const contract = act.contractId
                                              ? relatedContractById.get(act.contractId)
                                              : invoice?.contractId
                                                ? relatedContractById.get(invoice.contractId)
                                                : null;
                                            return renderRelatedChips(
                                              contract ? [createContractChip(contract)] : [],
                                            );
                                          })()
                                        ) : column.key === "linkedInvoice" ? (
                                          (() => {
                                            const invoice = getActInvoice(act);
                                            return renderRelatedChips(
                                              invoice ? [createInvoiceChip(invoice)] : [],
                                            );
                                          })()
                                        ) : column.key === "linkedPayments" ? (
                                          renderRelatedChips(
                                            getActPayments(act).slice(0, 3).map(createPaymentChip),
                                          )
                                        ) : column.key === "responsible" ? (
                                          <span className="text-gray-900">
                                            {act.responsibleName ??
                                              t("actResponsibleNotSelected") ??
                                              "—"}
                                          </span>
                                        ) : null}
                                      </td>
                                    ))}
                                    <td className="px-4 py-3">
                                      <div className="flex items-center justify-end gap-2">
                                        {actPermissions.canEdit && (
                                          <button
                                            type="button"
                                            onClick={() => openEditAct(act)}
                                            className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                            title={t("edit") ?? "Редактировать"}
                                            aria-label={
                                              t("edit") ?? "Редактировать"
                                            }
                                          >
                                            <Edit className="h-4 w-4" />
                                          </button>
                                        )}
                                        {actPermissions.canDelete && (
                                          <button
                                            type="button"
                                            onClick={() => removeAct(act)}
                                            className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                            title={t("delete") ?? "Удалить"}
                                            aria-label={
                                              t("delete") ?? "Удалить"
                                            }
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
                                  <td
                                    colSpan={visibleActTableColumns.length + 1}
                                    className="bg-amber-50 px-4 py-3 text-sm text-amber-700"
                                  >
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
              ) : activeSection === "contracts" ? (
                <>
                  <div className="mb-6 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
                    <div className="flex items-center gap-2">
                      <CalendarRange className="h-4 w-4 text-gray-500" />
                      <SystemDateInput
                        type="date"
                        value={contractFrom}
                        onChange={(event) =>
                          handleContractFromChange(event.target.value)
                        }
                        className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                      />
                      <span className="text-gray-500">—</span>
                      <SystemDateInput
                        type="date"
                        value={contractTo}
                        onChange={(event) =>
                          handleContractToChange(event.target.value)
                        }
                        className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                      />
                    </div>
                    {renderRelatedFilterSelect(
                      "Счет",
                      relatedInvoiceFilter,
                      setRelatedInvoiceFilter,
                      relatedInvoiceOptions,
                      "Все счета",
                      formatInvoiceFilterLabel,
                      "w-full sm:w-64",
                    )}
                    {renderRelatedFilterSelect(
                      "Акт",
                      relatedActFilter,
                      setRelatedActFilter,
                      relatedActOptions,
                      "Все акты",
                      formatActFilterLabel,
                      "w-full sm:w-64",
                    )}
                    {renderRelatedFilterSelect(
                      "Платеж",
                      relatedPaymentFilter,
                      setRelatedPaymentFilter,
                      relatedPaymentOptions,
                      "Все платежи",
                      formatPaymentFilterLabel,
                      "w-full sm:w-64",
                    )}
                    <div className="relative flex-1 min-w-[220px]">
                      <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                      <input
                        type="search"
                        aria-label="Поиск договоров клиента"
                        value={contractSearch}
                        onChange={(event) =>
                          setContractSearch(event.target.value)
                        }
                        placeholder={
                          t("contractsSearchPlaceholder") ??
                          "Поиск по номеру, названию, описанию или клиенту"
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
                        <RotateCcw
                          className={
                            contractsRefreshing
                              ? "h-4 w-4 animate-spin"
                              : "h-4 w-4"
                          }
                        />
                        {t("invoiceRefresh") ?? "Обновить"}
                      </button>
                      <button
                        type="button"
                        onClick={resetContractFilters}
                        className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                      >
                        {t("invoiceFiltersReset") ?? "Сбросить"}
                      </button>
                    </div>
                  </div>

                  {contractFilterBadges.length > 0 ? (
                    <div className="mb-6 flex flex-wrap items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
                      <span className="text-sm font-semibold text-slate-700">
                        Применены фильтры:
                      </span>
                      {contractFilterBadges.map((badge) => (
                        <span
                          key={badge}
                          className="inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-medium text-slate-600"
                        >
                          {badge}
                        </span>
                      ))}
                      <button
                        type="button"
                        onClick={resetContractFilters}
                        className="ml-auto inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                      >
                        <RotateCcw className="h-4 w-4" />
                        Сбросить все
                      </button>
                    </div>
                  ) : null}

                  {contractActionError ? (
                    <div className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                      {contractActionError}
                    </div>
                  ) : null}

                  {contractsError ? (
                    <div className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                      {(t("contractsLoadError") ??
                        "Не удалось загрузить договоры") + ": "}
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
                            <SystemSelect
                              value={contractPageSize}
                              onChange={(event) =>
                                setContractPageSize(
                                  Number(event.target.value) ||
                                    contractPageSize,
                                )
                              }
                              className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                            >
                              {DETAIL_PAGE_SIZE_OPTIONS.map((size) => (
                                <option key={size} value={size}>
                                  {size}
                                </option>
                              ))}
                            </SystemSelect>
                          </div>
                        }
                      />
                    </div>
                  ) : null}

                  {contractsLoading ? (
                    <div className="py-10 text-center text-gray-500">
                      <Loader2 className="mx-auto mb-2 h-5 w-5 animate-spin" />
                      {t("loading") ?? "Загрузка..."}
                    </div>
                  ) : clientContracts.length === 0 ? (
                    <div className="py-10 text-center text-gray-500">
                      <div className="text-lg font-semibold text-gray-900">
                        {t("contractsEmptyTitle") ?? "Договоры отсутствуют"}
                      </div>
                      <div className="mt-2 text-sm text-gray-500">
                        {t("contractsEmptyDescription") ??
                          "Создайте первый договор, чтобы отслеживать обязательства и сроки."}
                      </div>
                    </div>
                  ) : (
                    <>
                      <div className="overflow-x-auto">
                        <table
                          className="min-w-full table-fixed divide-y divide-gray-200 text-sm"
                          style={{ width: getContractTableWidth(110), minWidth: "100%" }}
                        >
                          <colgroup>
                            {visibleContractTableColumns.map((column) => (
                              <col
                                key={column.key}
                                style={{
                                  width: getContractColumnWidth(column.key),
                                }}
                              />
                            ))}
                            <col style={{ width: 110 }} />
                          </colgroup>
                          <thead className="bg-gray-50">
                            <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                              {visibleContractTableColumns.map((column) => {
                                const sortKey =
                                  column.key === "clients" ||
                                  column.key === "status" ||
                                  column.key === "validUntil" ||
                                  column.key === "title" ||
                                  column.key === "linkedInvoices" ||
                                  column.key === "linkedActs" ||
                                  column.key === "linkedPayments"
                                    ? null
                                    : (column.key as ContractsSortKey);
                                const isActive = sortKey
                                  ? contractSort.key === sortKey
                                  : false;
                                const direction = isActive
                                  ? contractSort.direction
                                  : undefined;
                                return (
                                  <th
                                    key={column.key}
                                    scope="col"
                                    style={{
                                      width: getContractColumnWidth(column.key),
                                    }}
                                    className={`relative px-4 py-3 ${draggingContractColumn === column.key ? "bg-blue-50 text-blue-700" : ""}`}
                                    onDragOver={handleContractColumnDragOver}
                                    onDrop={(event) =>
                                      handleContractColumnDrop(
                                        column.key,
                                        event,
                                      )
                                    }
                                  >
                                    {sortKey ? (
                                      <button
                                        type="button"
                                        onClick={() =>
                                          handleContractSort(sortKey)
                                        }
                                        className="flex items-center gap-1"
                                      >
                                        <span
                                          className="inline-flex min-w-0 select-none items-center gap-2"
                                          draggable
                                          onDragStart={(event) =>
                                            handleContractColumnDragStart(
                                              column.key,
                                              event,
                                            )
                                          }
                                          onDragEnd={
                                            handleContractColumnDragEnd
                                          }
                                          title="Перетащите, чтобы изменить порядок"
                                        >
                                          <GripVertical className="h-4 w-4 text-slate-400" />
                                          <span className="truncate">
                                            {column.label}
                                          </span>
                                        </span>
                                        <span className="text-gray-400">
                                          {isActive
                                            ? direction === "asc"
                                              ? "▲"
                                              : "▼"
                                            : ""}
                                        </span>
                                      </button>
                                    ) : (
                                      <div
                                        className="inline-flex min-w-0 select-none items-center gap-2"
                                        draggable
                                        onDragStart={(event) =>
                                          handleContractColumnDragStart(
                                            column.key,
                                            event,
                                          )
                                        }
                                        onDragEnd={handleContractColumnDragEnd}
                                        title="Перетащите, чтобы изменить порядок"
                                      >
                                        <GripVertical className="h-4 w-4 text-slate-400" />
                                        <span className="truncate">
                                          {column.label}
                                        </span>
                                      </div>
                                    )}
                                    <div
                                      role="separator"
                                      aria-label={`Изменить ширину колонки ${column.label}`}
                                      className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                                      onMouseDown={(event) =>
                                        handleContractResizeStart(
                                          column.key,
                                          event,
                                        )
                                      }
                                      onDragStart={(event) =>
                                        event.preventDefault()
                                      }
                                    />
                                  </th>
                                );
                              })}
                              <th scope="col" className="px-4 py-3">
                                {t("actions") ?? "Действия"}
                              </th>
                            </tr>
                          </thead>
                          <tbody className="divide-y divide-gray-200 text-gray-700">
                            {clientContracts.map((contract) => (
                              <tr
                                key={contract.id}
                                className="hover:bg-gray-50/80"
                              >
                                {visibleContractTableColumns.map((column) => (
                                  <td
                                    key={column.key}
                                    style={{
                                      width: getContractColumnWidth(column.key),
                                    }}
                                    className="max-w-0 overflow-hidden px-4 py-3 align-top"
                                  >
                                    {column.key === "number" ? (
                                      <span className="font-medium text-gray-900">
                                        №{contract.number}
                                      </span>
                                    ) : column.key === "title" ? (
                                      <span
                                        className="block truncate text-gray-900"
                                        title={
                                          contract.title?.trim() || undefined
                                        }
                                      >
                                        {contract.title?.trim() || "—"}
                                      </span>
                                    ) : column.key === "status" ? (
                                      (() => {
                                        const status = contract.status ?? "Draft";
                                        return (
                                          <span
                                            className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold ${CONTRACT_STATUS_CLASSES[status]}`}
                                          >
                                            {CONTRACT_STATUS_LABELS[status]}
                                          </span>
                                        );
                                      })()
                                    ) : column.key === "date" ? (
                                      <span className="text-gray-900">
                                        {toRuDate(contract.date)}
                                      </span>
                                    ) : column.key === "clients" ? (
                                      contract.clients.length === 0 ? (
                                        <span className="text-xs text-gray-500">
                                          {t("contractClientsRequired") ??
                                            "Клиенты не указаны"}
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
                                    ) : column.key === "amount" ? (
                                      <span className="font-semibold text-gray-900">
                                        {getContractDisplayAmount(contract) !== null
                                          ? formatCurrencySmart(
                                              getContractDisplayAmount(contract) ?? 0,
                                            ).full
                                          : "—"}
                                      </span>
                                    ) : column.key === "linkedInvoices" ? (
                                      (() => {
                                        const invoices = getContractInvoices(contract);
                                        return renderRelatedChips(
                                          invoices.slice(0, 3).map(createInvoiceChip),
                                        );
                                      })()
                                    ) : column.key === "linkedActs" ? (
                                      (() => {
                                        const acts = getContractActs(contract);
                                        return renderRelatedChips(
                                          acts.slice(0, 3).map(createActChip),
                                        );
                                      })()
                                    ) : column.key === "linkedPayments" ? (
                                      (() => {
                                        const contractPayments = getContractPayments(contract);
                                        return renderRelatedChips(
                                          contractPayments.slice(0, 3).map(createPaymentChip),
                                        );
                                      })()
                                    ) : column.key === "validUntil" ? (
                                      <span className="text-gray-900">
                                        {contract.isOpenEnded
                                          ? "До исполнения обязательств"
                                          : contract.validUntil
                                            ? toRuDate(contract.validUntil)
                                            : "—"}
                                      </span>
                                    ) : (
                                      <span className="text-gray-900">
                                        {toRuDate(contract.createdAt)}
                                      </span>
                                    )}
                                  </td>
                                ))}
                                <td className="px-4 py-3">
                                  <div className="flex items-center gap-2">
                                    {contractPermissions.canEdit && (
                                      <button
                                        type="button"
                                        onClick={() =>
                                          openContractEdit(contract)
                                        }
                                        className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                        title={t("edit") ?? "Редактировать"}
                                        aria-label={
                                          t("edit") ?? "Редактировать"
                                        }
                                      >
                                        <Edit className="h-4 w-4" />
                                      </button>
                                    )}
                                    {contractPermissions.canDelete && (
                                      <button
                                        type="button"
                                        onClick={() => removeContract(contract)}
                                        className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                        title={t("delete") ?? "Удалить"}
                                        aria-label={t("delete") ?? "Удалить"}
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
                        totalItems={
                          contractsPagination.total ?? clientContracts.length
                        }
                        pageSize={contractPageSize}
                        pageSizeOptions={DETAIL_PAGE_SIZE_OPTIONS}
                        onPageChange={setContractPage}
                        onPageSizeChange={setContractPageSize}
                      />
                    </>
                  )}
                </>
              ) : activeSection === "settlement" ? (
                <ClientReconciliationAct
                  clientId={clientId}
                  client={clientData}
                  clientName={clientName}
                />
              ) : (
                null
              )}
            </div>
          </div>
        )}

          </div>
        </div>

        {expenseDetailsCase ? (
          <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 px-4 py-6">
            <div
              role="dialog"
              aria-modal="true"
              aria-labelledby="case-expense-details-title"
              className="flex max-h-[90vh] w-full max-w-6xl flex-col overflow-hidden rounded-2xl bg-white shadow-xl"
            >
              <div className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 px-5 py-4">
                <div>
                  <h2
                    id="case-expense-details-title"
                    className="text-lg font-semibold text-slate-900"
                  >
                    Расшифровка расходов: {expenseDetailsCase.title}
                  </h2>
                  <p className="mt-1 text-sm text-slate-500">
                    {expenseDetailsCase.clientName
                      ? `Клиент: ${expenseDetailsCase.clientName}`
                      : "Расходы по выбранному делу"}
                  </p>
                </div>
                <button
                  type="button"
                  onClick={() => {
                    setExpenseDetailsCase(null);
                    setExpenseDetails([]);
                    setExpenseDetailsError(null);
                  }}
                  className="inline-flex h-9 items-center rounded-lg border border-slate-200 px-3 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                >
                  Закрыть
                </button>
              </div>

              <div className="grid gap-3 border-b border-slate-100 bg-slate-50 px-5 py-4 sm:grid-cols-3">
                <div className="rounded-xl bg-white px-4 py-3 shadow-sm">
                  <p className="text-xs uppercase text-slate-500">Начислено</p>
                  <p className="mt-1 text-base font-semibold text-rose-700">
                    {formatCurrencySmart(expenseDetailsTotals.amount).full}
                  </p>
                </div>
                <div className="rounded-xl bg-white px-4 py-3 shadow-sm">
                  <p className="text-xs uppercase text-slate-500">Оплачено</p>
                  <p className="mt-1 text-base font-semibold text-emerald-700">
                    {formatCurrencySmart(expenseDetailsTotals.paid).full}
                  </p>
                </div>
                <div className="rounded-xl bg-white px-4 py-3 shadow-sm">
                  <p className="text-xs uppercase text-slate-500">Остаток</p>
                  <p className="mt-1 text-base font-semibold text-amber-700">
                    {formatCurrencySmart(expenseDetailsTotals.outstanding).full}
                  </p>
                </div>
              </div>

              <div className="min-h-0 flex-1 overflow-auto p-5">
                {expenseDetailsLoading ? (
                  <div className="flex min-h-40 items-center justify-center gap-2 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Загрузка расходов...
                  </div>
                ) : expenseDetailsError ? (
                  <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
                    {expenseDetailsError}
                  </div>
                ) : expenseDetails.length === 0 ? (
                  <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-8 text-center text-sm text-slate-500">
                    По этому делу пока нет расходных строк.
                  </div>
                ) : (
                  <div className="overflow-x-auto rounded-xl border border-slate-200">
                    <table className="min-w-full divide-y divide-slate-200 text-sm">
                      <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                        <tr>
                          <th className="px-3 py-3 text-left">Дата</th>
                          <th className="px-3 py-3 text-left">Поставщик</th>
                          <th className="px-3 py-3 text-left">Счет</th>
                          <th className="px-3 py-3 text-left">Позиция</th>
                          <th className="px-3 py-3 text-right">Сумма</th>
                          <th className="px-3 py-3 text-right">Оплачено</th>
                          <th className="px-3 py-3 text-right">Остаток</th>
                          <th className="px-3 py-3 text-left">Статус</th>
                          <th className="px-3 py-3 text-left">Документ</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100 bg-white">
                        {expenseDetails.map((detail) => {
                          const lineName =
                            detail.lineItemName ||
                            detail.serviceName ||
                            detail.description ||
                            "-";
                          const documentMeta = [
                            detail.paymentOrderNumber,
                            detail.paymentDocumentDate
                              ? toRuDate(detail.paymentDocumentDate)
                              : null,
                          ]
                            .filter(Boolean)
                            .join(" от ");

                          return (
                            <tr
                              key={`${detail.paymentId}:${detail.lineItemId ?? "payment"}`}
                              className="align-top"
                            >
                              <td className="px-3 py-3 text-slate-600">
                                {toRuDate(detail.plannedDate)}
                              </td>
                              <td className="px-3 py-3 font-medium text-slate-800">
                                {detail.supplierName || "-"}
                              </td>
                              <td className="px-3 py-3 text-slate-600">
                                <div className="font-medium text-slate-800">
                                  {detail.invoiceNumber || "-"}
                                </div>
                                {detail.invoiceDate ? (
                                  <div className="text-xs text-slate-500">
                                    {toRuDate(detail.invoiceDate)}
                                  </div>
                                ) : null}
                              </td>
                              <td className="px-3 py-3 text-slate-700">
                                <div>{lineName}</div>
                                {detail.quantity || detail.price ? (
                                  <div className="mt-1 text-xs text-slate-500">
                                    {detail.quantity ?? 1} {detail.unit ?? "шт."} x{" "}
                                    {formatCurrencySmart(detail.price ?? detail.amount).short}
                                  </div>
                                ) : null}
                              </td>
                              <td className="px-3 py-3 text-right font-semibold text-rose-700">
                                {formatCurrencySmart(detail.amount).full}
                              </td>
                              <td className="px-3 py-3 text-right font-semibold text-emerald-700">
                                {formatCurrencySmart(detail.paidAmount).full}
                              </td>
                              <td className="px-3 py-3 text-right font-semibold text-amber-700">
                                {formatCurrencySmart(detail.outstandingAmount).full}
                              </td>
                              <td className="px-3 py-3 text-slate-600">
                                {invoiceStatusLabels[detail.status] ?? detail.status}
                              </td>
                              <td className="px-3 py-3 text-slate-600">
                                {documentMeta || "-"}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            </div>
          </div>
        ) : null}

        {clientBalanceExpanded ? (
          <div className="fixed inset-0 z-[1400] flex items-center justify-center bg-slate-900/50 p-4">
            <div className="flex max-h-[82vh] w-full max-w-3xl flex-col rounded-2xl bg-white shadow-2xl">
              <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-5 py-4">
                <div>
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                    Баланс клиента
                  </p>
                  <div className="mt-2 grid gap-3 sm:grid-cols-2">
                    <div className="rounded-xl border border-rose-100 bg-rose-50 px-4 py-3">
                      <p className="text-xs font-semibold uppercase text-rose-500">
                        Долг
                      </p>
                      <p className="mt-1 text-xl font-semibold text-rose-700">
                        {clientDebtLabel}
                      </p>
                    </div>
                    <div className="rounded-xl border border-emerald-100 bg-emerald-50 px-4 py-3">
                      <p className="text-xs font-semibold uppercase text-emerald-500">
                        Аванс
                      </p>
                      <p className="mt-1 text-xl font-semibold text-emerald-700">
                        {clientAdvanceLabel}
                      </p>
                    </div>
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => setClientBalanceExpanded(false)}
                  className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-500 transition-colors hover:bg-slate-50"
                  title="Закрыть"
                  aria-label="Закрыть"
                >
                  <X className="h-5 w-5" />
                </button>
              </div>
              <div className="overflow-y-auto p-5">
                {clientBalanceTransactions.length === 0 ? (
                  <div className="rounded-xl border border-dashed border-slate-200 px-4 py-8 text-center text-sm text-slate-500">
                    Движений по балансу пока нет.
                  </div>
                ) : (
                  <div className="space-y-3">
                    {clientBalanceTransactions.map((item) => {
                      const isCredit = item.direction === "Credit";
                      const signedAmount = `${isCredit ? "+" : "-"}${formatCurrencySmart(item.amount).full}`;
                      const paymentLabel =
                        item.type === "CreditApplied" ? "Зачтено в платёж" : "Источник";

                      return (
                        <div
                          key={item.id}
                          className="rounded-xl border border-slate-200 bg-white px-4 py-3 shadow-sm"
                        >
                          <div className="flex flex-wrap items-start justify-between gap-3">
                            <div className="min-w-0">
                              <div className="flex flex-wrap items-center gap-2">
                                <span className="font-semibold text-slate-900">
                                  {CLIENT_BALANCE_TRANSACTION_LABELS[item.type] ?? item.type}
                                </span>
                                <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-500">
                                  {new Date(item.createdAt).toLocaleString("ru-RU")}
                                </span>
                              </div>
                              <p className="mt-1 text-sm text-slate-600">
                                {item.description || "Без описания"}
                              </p>
                              {item.createdByUserName ? (
                                <p className="mt-1 text-xs text-slate-400">
                                  Автор: {getObjectAuditActorLabel(item.createdByUserName)}
                                </p>
                              ) : null}
                              {item.paymentId || item.invoiceId ? (
                                <div className="mt-2 flex flex-wrap gap-2 text-xs">
                                  {item.paymentId ? (
                                    <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-slate-600">
                                      {paymentLabel} #{item.paymentId}
                                    </span>
                                  ) : null}
                                  {item.invoiceId ? (
                                    <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-slate-600">
                                      Счёт #{item.invoiceId}
                                    </span>
                                  ) : null}
                                </div>
                              ) : null}
                            </div>
                            <div
                              className={`shrink-0 text-right text-base font-semibold ${
                                isCredit ? "text-emerald-700" : "text-blue-700"
                              }`}
                            >
                              {signedAmount}
                            </div>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          </div>
        ) : null}

        {clientData?.id ? (
          <ObjectAuditModal
            open={clientAuditOpen}
            onClose={() => setClientAuditOpen(false)}
            objectType="client"
            objectId={clientData.id}
            createdAt={clientData.createdAt}
            createdByUserName={clientData.auditCreatedByUserName}
            updatedAt={clientData.auditUpdatedAt}
            updatedByUserName={clientData.auditUpdatedByUserName}
            isDeleted={clientData.auditIsDeleted}
            deletedAt={clientData.auditDeletedAt}
            deletedByUserName={clientData.auditDeletedByUserName}
            title="История клиента"
            subtitle="Паспорт клиента и история изменений."
          />
        ) : null}

        {invoiceModalOpen ? (
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
          defaultClientId={
            invoiceViewMode === "expenseClient" ? undefined : clientId
          }
          defaultExpenseClientId={
            invoiceViewMode === "expenseClient" ? clientId : undefined
          }
          defaultType={invoiceViewConfig.type}
          defaultResponsibleId={clientData?.responsibleUserId ?? user?.id}
          relatedPayments={invoiceModalRelatedPayments}
          paymentsLoading={invoiceModalPaymentsLoading}
          onRefreshRequested={refreshInvoiceModalData}
          onClientCaseCreated={handleInvoiceCaseCreated}
          onCreateActPage={
            onCreateActPage
              ? (initialValues) =>
                  onCreateActPage(initialValues, { section: "accounts" })
              : undefined
          }
          />
        ) : null}

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
          allowedCounterpartyScopes={allowedContractCounterpartyScopes}
        />

        {payModalOpen ? (
          <PaymentModal
          key={editingPayment ? `edit-${editingPayment.id}` : "new"}
          isOpen={payModalOpen}
          onClose={closePayModal}
          payment={editingPayment ?? undefined}
          onSubmit={submitPayment}
          canCreatePayment={clientPermissions.canCreate}
          canEditPayment={clientPermissions.canEdit}
          onTransferRemainder={transferPaymentRemainder}
          onRollbackRemainderTransfer={rollbackPaymentRemainderTransfer}
          onAdjustRemainderTransfer={adjustPaymentRemainderTransfer}
          canViewLinkedInvoices={accountPermissions.canView}
          canCreateLinkedInvoices={accountPermissions.canView && accountPermissions.canCreate}
          availableClientBalance={clientBalance?.balance ?? 0}
          onApplyClientBalance={applyClientBalanceToPayment}
          onBalanceApplied={async () => {
            await refreshPayments();
            await refreshClientBalance();
            if (clientBalanceExpanded) await refreshClientBalanceTransactions();
            bumpStats();
          }}
          onCreateRemainder={async (payload, context) => {
            try {
              if (!clientPermissions.canCreate) throw new Error('Недостаточно прав для создания остатка.');
              const created = await apiService.createPayment(toBody(payload), context);
              void refreshAfterPaymentSave();
              return created;
            } catch (error) {
              if (error instanceof PaymentCreateAdoptedError) throw error;
              toast.error(
                extractDisplayErrorMessage(
                  error,
                  "Не удалось создать остаток платежа",
                ),
                "Платежи",
              );
              throw error;
            }
          }}
          onDelete={
            clientPermissions.canDelete &&
            (!editingPayment || (editingPayment.documentKind !== "Invoice" && !isTransferredRemainderPayment(editingPayment) && !isPartialRemainderSourcePayment(editingPayment)))
              ? async (id: number) => {
                  const removed = await removePayment(editingPayment ?? id);
                  if (removed) {
                    closePayModal();
                  }
                }
              : undefined
          }
          type={editingPayment?.type ?? paymentViewConfig.type ?? "Income"}
          defaultClientId={
            paymentViewMode === "expenseClient" ? undefined : clientId
          }
          defaultExpenseClientId={paymentModalDefaultExpenseClientId ?? undefined}
          defaultClientCaseId={
            selectedCaseId === "all" ? undefined : Number(selectedCaseId)
          }
          casesPrefetch={paymentModalCasesPrefetch}
          />
        ) : null}

        {caseModalOpen && (
          <CaseModal
            isOpen={caseModalOpen}
            onClose={() => setCaseModalOpen(false)}
            caseData={
              caseMode === "edit" && editingCase ? editingCase : blankCaseDraft
            }
            onSave={saveCase}
            onPositionReviewed={(review) => {
              setEditingCase((current) => current ? { ...current, ...review } : current);
              setCasesLocal((current) => (current ?? []).map((item) => item.id === editingCase?.id ? { ...item, ...review } : item));
            }}
            onNextHearingConfirmed={(confirmation) => {
              setEditingCase((current) => current ? { ...current, ...confirmation } : current);
              setCasesLocal((current) => (current ?? []).map((item) => item.id === editingCase?.id ? { ...item, ...confirmation } : item));
            }}
            onDelete={
              caseMode === "edit" && casePermissions.canDelete
                ? deleteCase
                : undefined
            }
            responsibles={actResponsibles}
            allowEdit={caseMode === "edit" ? casePermissions.canEdit : casePermissions.canCreate}
            canViewFinance={paymentPermissions.canView}
            onOpenTask={permissions.tasks.canView ? onOpenTask : undefined}
            canCreateTasks={permissions.tasks.canView && permissions.tasks.canCreate}
          />
        )}

        {legalEntityModalOpen ? (
          <LegalEntityModal
          isOpen={legalEntityModalOpen}
          onClose={closeLegalEntityModal}
          onSubmit={submitLegalEntityModal}
          initial={legalEntityModalInitial}
          suggest={(payload) => apiService.suggestLegalEntities(payload)}
          findById={(payload) => apiService.findLegalEntityById(payload)}
          allowEdit={clientPermissions.canEdit}
          allowDelete={false}
          canViewCases={casePermissions.canView}
          canViewAccounts={accountPermissions.canView}
          canCreateAccounts={accountPermissions.canView && accountPermissions.canCreate}
          canViewActs={actPermissions.canView}
          canViewPayments={paymentPermissions.canView}
          titleOverride={
            legalEntityModalInitial
              ? "Реквизиты юридического лица"
              : "Новое юридическое лицо"
          }
          />
        ) : null}

        <FormPromptModal
          open={legalEntityPromptOpen}
          title="Новая организация"
          description="Быстрое создание юрлица из карточки клиента."
          fields={[
            {
              key: "shortName",
              label: "Название организации",
              value: legalEntityDraft.shortName,
              onChange: (value) => {
                setLegalEntityDraft((prev) => ({ ...prev, shortName: value }));
                if (legalEntityPromptError) {
                  setLegalEntityPromptError(null);
                }
              },
              placeholder: "ООО Ромашка",
              required: true,
              autoFocus: true,
            },
            {
              key: "inn",
              label: "ИНН",
              value: legalEntityDraft.inn,
              onChange: (value) => {
                setLegalEntityDraft((prev) => ({ ...prev, inn: value }));
                if (legalEntityPromptError) {
                  setLegalEntityPromptError(null);
                }
              },
              placeholder: "Опционально",
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
          title={
            isEditingPoa ? "Редактирование доверенности" : "Новая доверенность"
          }
          description={
            isEditingPoa
              ? "Измените реквизиты доверенности прямо из карточки клиента."
              : "Добавление доверенности без перехода в отдельную форму."
          }
          fields={[
            {
              key: "number",
              label: "Номер доверенности",
              value: poaDraft.number,
              onChange: (value) => {
                setPoaDraft((prev) => ({ ...prev, number: value }));
                if (poaPromptError) {
                  setPoaPromptError(null);
                }
              },
              placeholder: "Например 12/24",
              required: true,
              autoFocus: true,
            },
            {
              key: "issuedAt",
              label: "Дата выдачи",
              value: poaDraft.issuedAt,
              onChange: (value) => {
                setPoaDraft((prev) => ({ ...prev, issuedAt: value }));
                if (poaPromptError) {
                  setPoaPromptError(null);
                }
              },
              type: "date",
              required: true,
            },
            {
              key: "validUntil",
              label: "Действует до",
              value: poaDraft.validUntil,
              onChange: (value) => {
                setPoaDraft((prev) => ({ ...prev, validUntil: value }));
                if (poaPromptError) {
                  setPoaPromptError(null);
                }
              },
              type: "date",
            },
            {
              key: "issuer",
              label: "Кем выдана",
              value: poaDraft.issuer,
              onChange: (value) => {
                setPoaDraft((prev) => ({ ...prev, issuer: value }));
                if (poaPromptError) {
                  setPoaPromptError(null);
                }
              },
              placeholder: "Организация или ФИО",
            },
          ]}
          onCancel={closePoaPrompt}
          onConfirm={submitQuickPowerOfAttorney}
          confirmLabel={isEditingPoa ? "Сохранить" : "Создать"}
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
