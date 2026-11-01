import { PaymentCreateAdoptedError } from '../../services/paymentCreateAttempts';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent } from '../../services/authService';
import { useAsyncOperationOwner } from '../../hooks/useAsyncOperationOwner';
import { useDocumentDeliveryOwner, type DocumentDeliveryRead } from '../../hooks/useDocumentDeliveryOwner';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import {
  ArrowLeft,
  CheckCircle2,
  FileCheck2,
  FileText,
  Loader2,
  Pencil,
  Search,
  Plus,
  Trash2,
  WalletCards,
  X,
} from "lucide-react";
import { useTranslation } from "../../hooks/useTranslation";
import type { RolePermissions } from "../../types/permissions";
import { useDebouncedValue } from "../../hooks/useDebouncedValue";
import type {
  Act,
  ActInput,
  ActResponsible,
  Client,
  ClientCase,
  Invoice,
  InvoiceInput,
  Payment,
  PaymentPayload,
  ClientCaseFinanceSummary,
  LegalEntityBankAccount,
  LegalEntityCard,
  LegalEntityCashRegister,
  LegalEntityDetail,
  LegalEntityInput,
  LegalEntitySuggestion,
  LegalEntityDadataDetails,
  CompanyProfileStatus,
} from "../../types";
import { ClientStatusBadge } from "../Clients/ClientStatusBadge";
import {
  ClientAddressEntriesForm,
  type ClientAddressEntryFormValue,
} from "../Clients/ClientAddressEntriesForm";
import {
  ensureSinglePrimaryAddressEntryList,
  setPrimaryAddressEntry,
} from "../Clients/addressEntryPersistence";
import { chooseBestDisplayAddress } from "../Clients/addressEntryUtils";
import { apiService } from "../../services/api";
import { createClientRequestId } from '../../utils/clientRequestId';
import { useCompanyAddressAutofill } from './useCompanyAddressAutofill';
import { useActs } from "../../hooks/useActs";
import { useClients } from "../../hooks/useClients";
import { useClientCases } from "../../hooks/useClientCases";
import { useClientStatuses } from "../../hooks/useClientStatuses";
import { useDialog } from "../../hooks/useDialog";
import { useDictionaries } from "../../hooks/useDictionaries";
import { useFormRequirements } from "../../hooks/useFormRequirements";
import { useInvoices } from "../../hooks/useInvoices";
import { useLegalEntities } from "../../hooks/useLegalEntities";
import { usePaymentsPaged } from "../../hooks/usePaymentsPaged";
import { useToast } from "../../hooks/useToast";
import { useUserTimeZone } from "../../hooks/useUserTimeZone";
import { formatCurrencySmart } from "../../utils/formatters";
import { extractDisplayErrorMessage } from "../../utils/errorUtils";
import { formatDateTimeInTimeZone } from "../../utils/timeZoneDate";
import { ActModal } from "../Acts/ActModal";
import { InvoiceModal } from "../Accounts/InvoiceModal";
import { PaymentModal } from "../Calendar/PaymentModal";
import { CaseModal } from "../Clients/CaseModal";
import DictionariesModal from "../Dictionaries/DictionariesModal";
import { TablePagination } from "../ui/TablePagination";
import { ObjectAuditLauncher } from "../ui/ObjectAuditModal";
import {
  dedupeLegalEntityAddressInputs,
  findMatchingLegalEntityAddressEntryIndex,
  mergeLegalEntityAddressEntry,
  upsertLegalEntityAddressEntry,
} from "./legalEntityFormUtils";
import {
  dedupeLegalEntitySuggestions,
  getPreferredLegalEntityAddress,
  resolveInitialLegalEntityDadataQuery,
} from "./legalEntityDadataUtils";
import {
  getLegalEntityImageProfile,
  encodeLegalEntityImageCanvas,
  getLegalEntityImageAccept,
  getLegalEntityImageSourceHint,
  isLegalEntityInlineAssetPayloadSafe,
  isLegalEntityRequestBodyLengthSafe,
  type LegalEntityImageKind,
  type LegalEntityImageUploadProfile,
} from "./legalEntityImageUtils";
import { inspectLegalEntityImageSource, validateLegalEntityImageDimensions } from "./legalEntityImageSourceUtils";
import { validateLegalEntityImageTransparency } from "./legalEntityImageTransparencyUtils";
import { getLegalEntityImageFieldState } from "./legalEntityImageFieldUtils";
import { CompanyBankConnectionsSection } from "./CompanyBankConnectionsSection";
import { CompanyBranchesSection } from "./CompanyBranchesSection";
import { CompanyRequisitesCheck } from "./CompanyRequisitesCheck";
import { CompanyTaxRegisterSection } from "./CompanyTaxRegisterSection";
import { LegalEntityFinanceSection } from "./LegalEntityFinanceSection";
import { CompanyImagePrintDefaultsSection } from './CompanyImagePrintDefaultsSection';
import { companyImagePrintDraft, companyImagePrintPayload } from './companyImagePrintDefaults';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';

interface LegalEntityModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (payload: LegalEntityInput) => Promise<LegalEntityDetail>;
  onDelete?: () => Promise<void>;
  deleteOwnerScope?: string;
  initial?: LegalEntityDetail | null;
  suggest: (payload: {
    query?: string;
    inn?: string;
    limit?: number;
  }) => Promise<LegalEntitySuggestion[]>;
  findById: (payload: {
    query: string;
    kpp?: string;
  }) => Promise<LegalEntityDadataDetails>;
  allowEdit: boolean;
  allowDelete: boolean;
  mode?: "modal" | "page";
  onBack?: () => void;
  titleOverride?: string;
  hidePageNavigation?: boolean;
  hideFormActions?: boolean;
  hideDadataSection?: boolean;
  hideMetaInfo?: boolean;
  hideAuditSection?: boolean;
  hideDictionaryButtons?: boolean;
  compactReadOnlyView?: boolean;
  canManageOwnCompanySettings?: boolean;
  canManageCompanyBranches?: boolean;
  canViewCases?: boolean;
  canViewAccounts?: boolean;
  canCreateAccounts?: boolean;
  canViewActs?: boolean;
  canViewPayments?: boolean;
  relatedRecordPermissions?: Partial<Pick<RolePermissions, 'cases' | 'accounts' | 'acts' | 'payments'>>;
}

interface LegalEntityImageFieldProps {
  kind: LegalEntityImageKind;
  title: string;
  alt: string;
  value: string;
  persistedValue: string;
  changed: boolean;
  readOnly: boolean;
  compactDisplay?: boolean;
  onUpload: (file: File | null) => Promise<void>;
  onRemove: () => Promise<void>;
  onRestore: () => void;
}

type ClientAssociationItem = Pick<
  Client,
  | "id"
  | "name"
  | "company"
  | "phone"
  | "email"
  | "clientStatus"
  | "clientStatusId"
>;

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
  clientStatusId: string;
  isClientAndSupplier: boolean;
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
  profileStatus: CompanyProfileStatus;
}

type BankAccountItem = LegalEntityBankAccount;
type CashRegisterItem = LegalEntityCashRegister;
type CardItem = LegalEntityCard;

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

type RequisitesTab =
  | "base"
  | "tax"
  | "codes"
  | "treasury"
  | "english"
  | "addresses"
  | "contacts";
type LegalEntityDictTab =
  | "countries"
  | "base-currencies"
  | "service-languages"
  | "organization-forms"
  | "timezones"
  | "tax-systems"
  | "tax-system-modes";
type LegalEntityCaseFilter = "all" | number;
type LegalEntityCasePayload = Omit<ClientCase, "id" | "createdAt">;
type LegalEntityInvoiceModalMode = "create" | "edit";
type LegalEntityActModalMode = "create" | "edit";
type LegalEntityPaymentModalMode = "create" | "edit";
type LegalEntityCaseStatusFilter = "all" | "open" | "hold" | "closed" | "lost";

const BACKEND_IMAGE_DATA_URL_MAX_LENGTH = 350_000;
const IMAGE_PROCESSING_SAVE_MESSAGE = "Дождитесь окончания обработки изображений перед сохранением.";
const LEGAL_ENTITY_REQUEST_BODY_MAX_LENGTH = 900_000;

const EMPTY_PERSON_DOC: PersonDocInfo = {
  position: "",
  lastName: "",
  firstName: "",
  middleName: "",
  noMiddleName: false,
};

const EMPTY_ENGLISH_REQUISITES: EnglishRequisites = {
  organizationName: "",
  fullName: "",
  form: "",
  legalAddress: "",
  actualAddress: "",
  position: "",
  lastName: "",
  firstName: "",
  middleName: "",
};

const ADDRESS_TYPE_OPTIONS = [
  { value: "registration", label: "Адрес регистрации" },
  { value: "actual", label: "Фактический адрес" },
  { value: "legal", label: "Юридический адрес" },
  { value: "other", label: "Иной адрес" },
] as const;

const CLIENT_PAGE_SIZE_OPTIONS = [10, 25, 50];

const CASE_STATUS_META: Record<
  string,
  { label: string; className: string }
> = {
  Open: {
    label: "Открыто",
    className: "border-emerald-200 bg-emerald-50 text-emerald-700",
  },
  OnHold: {
    label: "Приостановлено",
    className: "border-amber-200 bg-amber-50 text-amber-700",
  },
  Closed: {
    label: "Закрыто",
    className: "border-slate-200 bg-slate-50 text-slate-600",
  },
  Lost: {
    label: "Проиграно",
    className: "border-rose-200 bg-rose-50 text-rose-700",
  },
};

const INVOICE_STATUS_LABELS = {
  Pending: "Ожидает",
  Completed: "Оплачено",
  Overdue: "Просрочено",
  Cancelled: "Аннулировано",
  Processing: "Частично оплачено",
} as const;

const ACT_STATUS_LABELS = {
  Created: "Создан",
  Transferred: "Передан",
  Signed: "Подписан",
  Terminated: "Аннулирован",
} as const;

function formatDocumentDate(value?: string | null) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleDateString("ru-RU");
}

function renderCaseStatusBadge(status?: string | null) {
  const meta =
    CASE_STATUS_META[status ?? ""] ?? {
      label: status || "—",
      className: "border-slate-200 bg-slate-50 text-slate-600",
    };

  return (
    <span
      className={`inline-flex rounded-full border px-2 py-0.5 text-xs font-semibold ${meta.className}`}
    >
      {meta.label}
    </span>
  );
}

function normalizeCaseStatusFilter(status?: string | null): LegalEntityCaseStatusFilter {
  const value = (status ?? "").toLowerCase();
  if (value.includes("lost") || value.includes("проигр")) return "lost";
  if (value.includes("closed")) return "closed";
  if (value.includes("hold")) return "hold";
  if (value.includes("open")) return "open";
  return "open";
}

function caseStatusClassName(status?: string | null) {
  return (
    CASE_STATUS_META[status ?? ""]?.className ??
    "border-slate-200 bg-slate-50 text-slate-600"
  );
}

function LegalEntityImageField({
  kind,
  title,
  alt,
  value,
  persistedValue,
  changed,
  readOnly,
  compactDisplay = false,
  onUpload,
  onRemove,
  onRestore,
}: LegalEntityImageFieldProps) {
  const inputId = useId();
  const {
    hasImage,
    removalPending,
    canUpload,
    canRemove,
    showReplaceHint,
    showMissingPlaceholder,
  } = getLegalEntityImageFieldState({
    value,
    persistedValue,
    changed,
    readOnly,
  });

  return (
    <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
      <div className="text-sm font-semibold text-slate-700">{title}</div>
      <div
        className={`relative mt-4 flex items-center justify-center overflow-hidden rounded-2xl ${
          hasImage
            ? "border border-slate-200 bg-slate-50/80"
            : "border border-dashed border-slate-300 bg-slate-50/40"
        } ${compactDisplay ? "min-h-[260px] p-3" : "min-h-[220px] px-4 py-5"}`}
      >
        {hasImage ? (
          <>
            {canRemove ? (
              <button
                type="button"
                onClick={() => {
                  void onRemove();
                }}
                className="absolute right-3 top-3 inline-flex h-9 w-9 items-center justify-center rounded-full border border-rose-200 bg-white text-rose-500 shadow-sm transition hover:bg-rose-50 hover:text-rose-600"
                title="Удалить изображение"
                aria-label="Удалить изображение"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            ) : null}
            <div className="flex w-full flex-col items-center justify-center gap-4">
              <img
                src={value}
                alt={alt}
                className={`w-full rounded-2xl border border-slate-200 bg-white object-contain shadow-sm ${
                  compactDisplay ? "h-[230px] p-1" : "max-h-36 p-3"
                }`}
              />
              {showReplaceHint ? (
                <p className="text-center text-xs text-slate-500">
                  Чтобы заменить файл, сначала удалите текущее изображение.
                </p>
              ) : null}
            </div>
          </>
        ) : showMissingPlaceholder ? (
          <div className="text-center text-sm text-slate-400">
            Изображение не загружено.
          </div>
        ) : canUpload ? (
          <div className="flex w-full flex-col items-center gap-3 text-center">
            <input
              id={inputId}
              type="file"
              accept={getLegalEntityImageAccept(kind)}
              className="hidden"
              onChange={(event) => {
                const input = event.currentTarget;
                const file = input.files?.[0] ?? null;
                void onUpload(file).finally(() => {
                  input.value = "";
                });
              }}
            />
            <label
              htmlFor={inputId}
              className="inline-flex cursor-pointer items-center gap-2 rounded-xl border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-medium text-blue-700 transition hover:bg-blue-100"
            >
              <Plus className="h-4 w-4" />
              Выбрать файл
            </label>
            <p role={removalPending ? "status" : undefined} className="max-w-xs text-xs leading-5 text-slate-500">
              {removalPending
                ? "Изображение помечено на удаление. Сохраните карточку, чтобы удалить его, или выберите новый файл вместо него."
                : getLegalEntityImageSourceHint(kind)}
            </p>
            {removalPending ? (
              <button
                type="button"
                onClick={onRestore}
                className="text-xs font-medium text-slate-600 transition hover:text-slate-900"
              >
                Вернуть текущее изображение
              </button>
            ) : null}
          </div>
        ) : null}
      </div>
    </div>
  );
}

function toClientAssociationItem(
  client: Partial<ClientAssociationItem> &
    Pick<ClientAssociationItem, "id" | "name">,
): ClientAssociationItem {
  return {
    id: client.id,
    name: client.name,
    company: client.company ?? "",
    phone: client.phone ?? "",
    email: client.email ?? "",
    clientStatus: client.clientStatus ?? null,
    clientStatusId: client.clientStatusId ?? null,
  };
}

function mergeClientAssociationItems(
  primary: ClientAssociationItem[],
  fallback: ClientAssociationItem[],
): ClientAssociationItem[] {
  const seen = new Set<number>();
  const merged: ClientAssociationItem[] = [];

  for (const item of [...primary, ...fallback]) {
    if (seen.has(item.id)) continue;
    seen.add(item.id);
    merged.push(item);
  }

  return merged;
}

const createEmptyLegalEntityAddressEntry = (
  isPrimary = false,
): ClientAddressEntryFormValue => ({
  uiKey: createClientRequestId(),
  type: "legal",
  isPrimary,
  additionalTypes: [],
  fullAddress: "",
  postalCode: "",
  country: "",
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

const mapAddressTypeToForm = (type: number): string => {
  if (type == 1) return "registration";
  if (type == 2) return "actual";
  if (type == 3) return "legal";
  return "other";
};

const mapAddressTypeFromForm = (type: string): number => {
  if (type == "registration") return 1;
  if (type == "actual") return 2;
  if (type == "legal") return 3;
  return 9;
};

function readFileAsDataUrl(file: File, signal?: AbortSignal): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    const cleanup = () => {
      signal?.removeEventListener("abort", abort);
      reader.onload = null;
      reader.onerror = null;
    };
    const abort = () => {
      cleanup();
      reader.abort();
      reject(new DOMException("Обработка изображения отменена.", "AbortError"));
    };
    reader.onload = () => {
      cleanup();
      resolve(typeof reader.result === "string" ? reader.result : "");
    };
    reader.onerror = () => {
      cleanup();
      reject(new Error("Не удалось прочитать файл изображения."));
    };
    if (signal?.aborted) { abort(); return; }
    signal?.addEventListener("abort", abort, { once: true });
    reader.readAsDataURL(file);
  });
}

function loadImageElement(dataUrl: string, signal?: AbortSignal): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    const cleanup = () => {
      signal?.removeEventListener("abort", abort);
      image.onload = null;
      image.onerror = null;
    };
    const abort = () => {
      cleanup();
      image.removeAttribute("src");
      reject(new DOMException("Обработка изображения отменена.", "AbortError"));
    };
    image.onload = () => { cleanup(); resolve(image); };
    image.onerror = () => { cleanup(); reject(new Error("Не удалось обработать изображение.")); };
    if (signal?.aborted) { abort(); return; }
    signal?.addEventListener("abort", abort, { once: true });
    image.src = dataUrl;
  });
}

// eslint-disable-next-line react-refresh/only-export-components
export async function convertImageToFittingDataUrl(
  file: File,
  maxLength: number,
): Promise<string> {
  const original = await readFileAsDataUrl(file);
  if (original.length <= maxLength) return original;

  const image = await loadImageElement(original);
  const baseMaxSide = 1600;
  const maxSide = Math.max(image.width, image.height) || 1;
  const firstScale = Math.min(1, baseMaxSide / maxSide);
  const scales = [
    firstScale,
    firstScale * 0.85,
    firstScale * 0.72,
    firstScale * 0.6,
    firstScale * 0.5,
  ].map((value) => Math.max(0.2, value));
  const qualities = [0.9, 0.82, 0.74, 0.66, 0.58, 0.5, 0.42];

  const canvas = document.createElement("canvas");
  const context = canvas.getContext("2d");
  if (!context)
    throw new Error("Браузер не поддерживает обработку изображений.");

  for (const scale of scales) {
    canvas.width = Math.max(1, Math.round(image.width * scale));
    canvas.height = Math.max(1, Math.round(image.height * scale));
    context.clearRect(0, 0, canvas.width, canvas.height);
    context.drawImage(image, 0, 0, canvas.width, canvas.height);

    for (const quality of qualities) {
      const candidate = canvas.toDataURL("image/jpeg", quality);
      if (candidate.length <= maxLength) {
        return candidate;
      }
    }
  }

  throw new Error(
    "Изображение слишком большое даже после сжатия. Выберите файл меньшего размера.",
  );
}

function parseJsonOrDefault<T>(
  json: string | null | undefined,
  fallback: T,
): T {
  if (!json) return fallback;
  try {
    return JSON.parse(json) as T;
  } catch {
    return fallback;
  }
}

function fitImageIntoBox(
  width: number,
  height: number,
  maxWidth: number,
  maxHeight: number,
): { width: number; height: number } {
  const safeWidth = Math.max(1, width || 1);
  const safeHeight = Math.max(1, height || 1);
  const scale = Math.min(1, maxWidth / safeWidth, maxHeight / safeHeight);

  return {
    width: Math.max(1, Math.round(safeWidth * scale)),
    height: Math.max(1, Math.round(safeHeight * scale)),
  };
}

async function convertImageToUploadProfileDataUrl(
  file: File,
  profile: LegalEntityImageUploadProfile,
  signal: AbortSignal,
): Promise<string> {
  await inspectLegalEntityImageSource(file, profile.kind, signal);
  const original = await readFileAsDataUrl(file, signal);
  const image = await loadImageElement(original, signal);
  signal.throwIfAborted();
  validateLegalEntityImageDimensions(image.naturalWidth || image.width, image.naturalHeight || image.height, profile.kind);
  const canvas = document.createElement("canvas");
  const context = canvas.getContext("2d");

  if (!context) {
    throw new Error("Браузер не поддерживает обработку изображений.");
  }

  const fitted = fitImageIntoBox(
    image.width,
    image.height,
    profile.maxWidth,
    profile.maxHeight,
  );
  canvas.width = fitted.width;
  canvas.height = fitted.height;
  context.clearRect(0, 0, canvas.width, canvas.height);
  context.drawImage(image, 0, 0, canvas.width, canvas.height);

  validateLegalEntityImageTransparency(canvas, context, profile);
  return encodeLegalEntityImageCanvas(canvas, context, profile);
}

const DEFAULT_STATE: FormState = {
  shortName: "",
  fullName: "",
  inn: "",
  kpp: "",
  ogrn: "",
  address: "",
  phone: "",
  email: "",
  director: "",
  responsibleUserId: "",
  clientStatusId: "",
  isClientAndSupplier: false,
  country: "",
  baseCurrency: "",
  serviceLanguage: "",
  organizationForm: "",
  timezone: "",
  taxSystem: "",
  taxSystemMode: "",
  incomeTaxRate: "",
  incomeExpenseTaxRate: "",
  vatWithoutVat: false,
  okved: "",
  okpo: "",
  okato: "",
  oktmo: "",
  okogu: "",
  okfs: "",
  okopf: "",
  pfr: "",
  fss: "",
  charterCapital: "",
  taxRegistrationDate: "",
  taxOfficeCode: "",
  taxRegion: "",
  ifnsName: "",
  taxOfficePhone: "",
  treasuryInn: "",
  treasuryKpp: "",
  treasuryRecipientName: "",
  treasuryBankBik: "",
  treasuryBankName: "",
  treasuryAccountNumber: "",
  treasuryCorrAccountNumber: "",
  treasuryKbk: "",
  notes: "",
  profileStatus: "Active",
};

export function LegalEntityModal({
  isOpen,
  onClose,
  onSubmit,
  onDelete,
  deleteOwnerScope,
  initial,
  suggest,
  findById,
  allowEdit,
  allowDelete,
  mode = "modal",
  onBack,
  titleOverride,
  hidePageNavigation = false,
  hideFormActions = false,
  hideDadataSection = false,
  hideMetaInfo = false,
  hideAuditSection = false,
  hideDictionaryButtons = false,
  compactReadOnlyView = false,
  canManageOwnCompanySettings = false,
  canManageCompanyBranches = false,
  canViewCases = true,
  canViewAccounts = true,
  canCreateAccounts = true,
  canViewActs = true,
  canViewPayments = true,
  relatedRecordPermissions,
}: LegalEntityModalProps) {
  const { t } = useTranslation();
  const { confirm } = useDialog();
  const toast = useToast();
  const profileTimeZoneId = useUserTimeZone();

  const legalEntityFieldLabels = useMemo<Record<string, string>>(
    () => ({
      ShortName: t("legalEntityShortName") || "Краткое название",
      FullName: t("legalEntityFullName") || "Полное название",
      Inn: t("legalEntityInn") || "ИНН",
      Kpp: t("legalEntityKpp") || "КПП",
      Ogrn: t("legalEntityOgrn") || "ОГРН",
      Address: t("legalEntityAddress") || "Адрес",
      Director: t("legalEntityDirector") || "Руководитель",
      Phone: t("legalEntityPhone") || "Телефон",
      Email: t("legalEntityEmail") || "Email",
      Country: "Страна",
      BaseCurrency: "Базовая валюта",
      ServiceLanguage: "Язык сервиса",
      OrganizationForm: "Форма организации",
      Timezone: "Часовой пояс",
      Oktmo: "ОКТМО",
    }),
    [t],
  );

  const formatValidationMessage = useCallback(
    (payload: { message?: string; missingFields?: string[] }): string => {
      const base = payload.message?.trim() || "Не удалось сохранить";
      const missing = Array.isArray(payload.missingFields)
        ? payload.missingFields
        : [];
      if (missing.length === 0) return base;
      const fieldNames = missing
        .map((field) => legalEntityFieldLabels[field] ?? field)
        .join(", ");
      return `${base}: ${fieldNames}`;
    },
    [legalEntityFieldLabels],
  );
  const formatValidationMessageRef = useRef(formatValidationMessage);
  useEffect(() => {
    formatValidationMessageRef.current = formatValidationMessage;
  }, [formatValidationMessage]);

  const extractErrorMessage = useCallback((error: unknown, fallback: string) => {
    const raw = error instanceof Error ? error.message : String(error);
    if (/failed to fetch|networkerror|load failed|net::err_failed/i.test(raw)) {
      return "Сервер не вернул ответ при сохранении. Проверьте соединение и попробуйте ещё раз.";
    }
    const marker = " - ";
    const markerIndex = raw.indexOf(marker);
    if (markerIndex >= 0) {
      const serverMessage = raw.slice(markerIndex + marker.length).trim();
      if (serverMessage) {
        try {
          return formatValidationMessageRef.current(
            JSON.parse(serverMessage) as {
              message?: string;
              missingFields?: string[];
            },
          );
        } catch {
          return serverMessage;
        }
      }
    }

    try {
      return formatValidationMessageRef.current(
        JSON.parse(raw) as { message?: string; missingFields?: string[] },
      );
    } catch {
      // noop
    }

    return raw || fallback;
  }, []);
  const {
    countries,
    baseCurrencies,
    serviceLanguages,
    organizationForms,
    timezones,
    taxSystems,
    taxSystemModes,
  } = useDictionaries();
  const { isRequired } = useFormRequirements("legal-entities", isOpen);
  const { statuses: clientStatuses } = useClientStatuses();
  const [formState, setFormState] = useState<FormState>(DEFAULT_STATE);
  const [profileEditVersion, setProfileEditVersion] = useState(initial?.profileEditVersion ?? null);
  const [selectedClients, setSelectedClients] = useState<
    ClientAssociationItem[]
  >([]);
  const [loading, setLoading] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerSearch, setPickerSearch] = useState("");
  const debouncedPickerSearch = useDebouncedValue(pickerSearch.trim(), 250);
  const [pickerClients, setPickerClients] = useState<ClientAssociationItem[]>(
    [],
  );
  const [pickerLoading, setPickerLoading] = useState(false);
  const [pickerError, setPickerError] = useState<string | null>(null);
  const [pickerPage, setPickerPage] = useState(1);
  const [pickerPageSize, setPickerPageSize] = useState(
    CLIENT_PAGE_SIZE_OPTIONS[0],
  );
  const [pickerTotal, setPickerTotal] = useState(0);
  const [selectedClientsPage, setSelectedClientsPage] = useState(1);
  const [selectedClientsPageSize, setSelectedClientsPageSize] = useState(
    CLIENT_PAGE_SIZE_OPTIONS[0],
  );
  const [hydratedSelectedClientIds, setHydratedSelectedClientIds] = useState<
    number[]
  >([]);
  const [dadataQuery, setDadataQuery] = useState("");
  const [suggestions, setSuggestions] = useState<LegalEntitySuggestion[]>([]);
  const [suggestLoading, setSuggestLoading] = useState(false);
  const [suggestError, setSuggestError] = useState<string | null>(null);
  const [bankAccounts, setBankAccounts] = useState<BankAccountItem[]>([]);
  const [savedBankBindingScope, setSavedBankBindingScope] = useState<string | null>(null);
  const bankBindingScope = useMemo(() => JSON.stringify([bankAccounts, formState.profileStatus]), [bankAccounts, formState.profileStatus]);
  const [cashRegisters, setCashRegisters] = useState<CashRegisterItem[]>([]);
  const [cards, setCards] = useState<CardItem[]>([]);
  const [englishRequisites, setEnglishRequisites] = useState<EnglishRequisites>(
    EMPTY_ENGLISH_REQUISITES,
  );
  const [documentDirector, setDocumentDirector] =
    useState<PersonDocInfo>(EMPTY_PERSON_DOC);
  const [chiefAccountant, setChiefAccountant] =
    useState<PersonDocInfo>(EMPTY_PERSON_DOC);
  const [logoDataUrl, setLogoDataUrl] = useState<string>("");
  const [sealDataUrl, setSealDataUrl] = useState<string>("");
  const [directorSignatureDataUrl, setDirectorSignatureDataUrl] =
    useState<string>("");
  const [persistedLogoDataUrl, setPersistedLogoDataUrl] = useState<string>("");
  const [persistedSealDataUrl, setPersistedSealDataUrl] = useState<string>("");
  const [
    persistedDirectorSignatureDataUrl,
    setPersistedDirectorSignatureDataUrl,
  ] = useState<string>("");
  const [logoChanged, setLogoChanged] = useState(false);
  const [printDefaultsDraft, setPrintDefaultsDraft] = useState(() => companyImagePrintDraft(initial));
  const [sealChanged, setSealChanged] = useState(false);
  const [directorSignatureChanged, setDirectorSignatureChanged] =
    useState(false);
  const imageUploadsRef = useRef(new Map<LegalEntityImageKind, AbortController>());
  const imageFormGenerationRef = useRef(0);
  const [imageProcessing, setImageProcessing] = useState(false);
  const [requisitesTab, setRequisitesTab] = useState<RequisitesTab>("base");
  const [addressEntries, setAddressEntries] = useState<
    ClientAddressEntryFormValue[]
  >([]);
  const [contactPhones, setContactPhones] = useState<string[]>([]);
  const [contactEmails, setContactEmails] = useState<string[]>([]);
  const [dictModalOpen, setDictModalOpen] = useState(false);
  const [dictModalTab, setDictModalTab] =
    useState<LegalEntityDictTab>("countries");
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [selectedLegalEntityCaseId, setSelectedLegalEntityCaseId] =
    useState<LegalEntityCaseFilter>("all");
  const [caseSearch, setCaseSearch] = useState("");
  const [caseStatusFilter, setCaseStatusFilter] =
    useState<LegalEntityCaseStatusFilter>("all");
  const [caseFinanceState, setCaseFinanceState] = useState<{
    scope: string; items: Record<number, ClientCaseFinanceSummary>; loading: boolean; error: string | null;
  } | null>(null);
  const [caseFinanceReloadToken, setCaseFinanceReloadToken] = useState(0);
  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [caseMode, setCaseMode] = useState<"create" | "edit">("create");
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [invoiceModalOpen, setInvoiceModalOpen] = useState(false);
  const [invoiceModalMode, setInvoiceModalMode] =
    useState<LegalEntityInvoiceModalMode>("create");
  const [selectedInvoice, setSelectedInvoice] = useState<Invoice | null>(null);
  const [invoiceModalError, setInvoiceModalError] = useState<{ owner: DocumentDeliveryRead; message: string } | null>(
    null,
  );
  const [actModalOpen, setActModalOpen] = useState(false);
  const [actModalMode, setActModalMode] =
    useState<LegalEntityActModalMode>("create");
  const [selectedAct, setSelectedAct] = useState<Act | null>(null);
  const [actModalError, setActModalError] = useState<{ owner: DocumentDeliveryRead; message: string } | null>(null);
  const [paymentModalOpen, setPaymentModalOpen] = useState(false);
  const [paymentModalMode, setPaymentModalMode] =
    useState<LegalEntityPaymentModalMode>("create");
  const [selectedPayment, setSelectedPayment] = useState<Payment | null>(null);
  const [mainCompanyState, setMainCompanyState] = useState<{ owner: number; ids: number[]; error: string | null } | null>(null);
  const [classificationRetry, setClassificationRetry] = useState(0);
  const clientPickerOwner = useRef<number | null>(null);
  const selectedLegalEntityClientStatus = useMemo(
    () =>
      formState.clientStatusId
        ? clientStatuses.find(
            (status) => status.id === Number(formState.clientStatusId),
          ) ?? null
        : null,
    [clientStatuses, formState.clientStatusId],
  );

  const readOnly = !allowEdit && !!initial;
  const deleteSession = captureAuthSessionIdentity();
  const { capture: captureDeleteOwner, isCurrent: isCurrentDeleteOwner } = useAsyncOperationOwner(JSON.stringify([
    initial?.id, isOpen, readOnly, allowDelete, mode, deleteSession, deleteOwnerScope,
  ]));
  const deleteOwnerAtRender = captureDeleteOwner();
  const deleteContext = useRef({ initial, isOpen, readOnly, allowDelete, mode, onDelete, onClose });
  deleteContext.current = { initial, isOpen, readOnly, allowDelete, mode, onDelete, onClose };
  const deleteAttempt = useRef<{ isCurrent: () => boolean; cancel: () => void } | null>(null);
  const activeSave = useRef<{ isCurrent: () => boolean } | null>(null);
  useEffect(() => {
    const pending = deleteAttempt;
    setDeleting(false);
    return () => { const attempt = pending.current; if (attempt && !attempt.isCurrent()) attempt.cancel(); };
  }, [initial, isOpen, readOnly, allowDelete, mode, deleteSession.epoch, deleteSession.actor, deleteSession.authenticated, deleteOwnerScope]);
  const saveLifetime = useRef({ epoch: 0 });
  const saveContext = useRef({ id: initial?.id, isOpen, readOnly, mode, initial });
  saveContext.current = { id: initial?.id, isOpen, readOnly, mode, initial };
  useEffect(() => {
    const lifetime = saveLifetime.current;
    setLoading(false);
    return () => { ++lifetime.epoch; };
  }, [initial?.id, isOpen, readOnly, mode]);
  useEffect(() => {
    const uploads = imageUploadsRef.current;
    setImageProcessing(false);
    setError((current) => current === IMAGE_PROCESSING_SAVE_MESSAGE ? null : current);
    return () => {
      imageFormGenerationRef.current += 1;
      for (const controller of uploads.values()) controller.abort();
      uploads.clear();
    };
  }, [initial, isOpen, readOnly]);
  const canMutateRelated = (
    section: 'cases' | 'accounts' | 'acts' | 'payments',
    permission: 'canCreate' | 'canEdit' | 'canDelete',
  ) => !readOnly
    && ({ cases: canViewCases, accounts: canViewAccounts, acts: canViewActs, payments: canViewPayments })[section]
    && (section !== 'accounts' || permission !== 'canCreate' || canCreateAccounts)
    && (relatedRecordPermissions === undefined || relatedRecordPermissions[section]?.[permission] === true);
  const isPageMode = mode === "page";
  const currentLegalEntityId = initial?.id ?? null;
  const classificationScope = JSON.stringify([currentLegalEntityId, isOpen, mode, readOnly, deleteOwnerScope,
    canManageOwnCompanySettings, initial?.profileEditVersion, deleteSession]);
  const { capture: captureClassificationOwner, isCurrent: isClassificationOwnerCurrent } = useAsyncOperationOwner(classificationScope);
  const classificationOwner = captureClassificationOwner();
  const companyAutofillOwner = useDocumentDeliveryOwner(JSON.stringify([classificationScope, loading, hideDadataSection]));
  const companyAutofillRequests = useRef({ search: 0, details: 0 });
  const [companyAutofillResultScope, setCompanyAutofillResultScope] = useState<string | null>(null);
  const scopedSuggestions = companyAutofillResultScope === companyAutofillOwner.scope ? suggestions : [];
  const scopedSuggestError = companyAutofillResultScope === companyAutofillOwner.scope ? suggestError : null;
  const scopedSuggestLoading = companyAutofillResultScope === companyAutofillOwner.scope && suggestLoading;
  useLayoutEffect(() => {
    const requests = companyAutofillRequests.current;
    requests.search++; requests.details++;
    setSuggestions([]); setSuggestError(null); setSuggestLoading(false); setCompanyAutofillResultScope(null);
    return () => { requests.search++; requests.details++; };
  }, [initial, isOpen, companyAutofillOwner.scope]);
  const invalidateCompanyDetails = () => { companyAutofillRequests.current.details++; };
  const canStartCompanyAutofill = () => isOpen && !readOnly && !loading && !hideDadataSection;
  const addressAutofill = useCompanyAddressAutofill({ entries: addressEntries, setEntries: setAddressEntries,
    scope: JSON.stringify([classificationScope, loading]), enabled: isOpen && !readOnly && !loading,
    resetKey: initial, onUseAddress: invalidateCompanyDetails });
  const { capture: captureInvoiceCaseOwner, isCurrent: isInvoiceCaseOwnerCurrent } = useAsyncOperationOwner(JSON.stringify([
    classificationScope, invoiceModalOpen, invoiceModalMode, selectedInvoice?.id, canViewCases,
    canMutateRelated('accounts', invoiceModalMode === 'edit' ? 'canEdit' : 'canCreate'),
  ]));
  const invoiceCaseOwner = captureInvoiceCaseOwner();
  const { capture: capturePaymentReadOwner, isCurrent: isPaymentReadOwnerCurrent } = useAsyncOperationOwner(JSON.stringify([
    classificationScope, canMutateRelated('payments', 'canEdit'), paymentModalOpen, paymentModalMode, selectedPayment?.id, selectedLegalEntityCaseId,
  ]));
  const paymentReadOwner = capturePaymentReadOwner();
  const paymentReadAttempt = useRef<{ id: number; isCurrent: () => boolean } | null>(null);
  const relatedCaseOwner = useDocumentDeliveryOwner(JSON.stringify([classificationScope, caseModalOpen, caseMode, editingCase?.id,
    canMutateRelated('cases', caseMode === 'edit' ? 'canEdit' : 'canCreate'), canMutateRelated('cases', 'canDelete'), canViewPayments, relatedRecordPermissions?.cases?.viewScope]));
  const relatedInvoiceOwner = useDocumentDeliveryOwner(JSON.stringify([classificationScope, invoiceModalOpen, invoiceModalMode, selectedInvoice?.id,
    selectedLegalEntityCaseId, canMutateRelated('accounts', invoiceModalMode === 'edit' ? 'canEdit' : 'canCreate'), canViewPayments, canViewActs, relatedRecordPermissions?.accounts?.viewScope]));
  const relatedActOwner = useDocumentDeliveryOwner(JSON.stringify([classificationScope, actModalOpen, actModalMode, selectedAct?.id,
    selectedLegalEntityCaseId, canMutateRelated('acts', actModalMode === 'edit' ? 'canEdit' : 'canCreate'), canViewAccounts, relatedRecordPermissions?.acts?.viewScope]));
  const relatedPaymentOwner = useDocumentDeliveryOwner(JSON.stringify([classificationScope, paymentModalOpen, paymentModalMode, selectedPayment?.id,
    selectedLegalEntityCaseId, canMutateRelated('payments', 'canCreate'), canMutateRelated('payments', 'canEdit'),
    relatedRecordPermissions?.payments?.viewScope, canViewCases, canViewAccounts, canViewActs]));
  const relatedReadScope = JSON.stringify([currentLegalEntityId, isOpen, deleteOwnerScope]);
  const casesReadOwner = useDocumentDeliveryOwner(JSON.stringify([relatedReadScope, canViewCases, relatedRecordPermissions?.cases?.viewScope]));
  const invoicesReadOwner = useDocumentDeliveryOwner(JSON.stringify([relatedReadScope, canViewAccounts, relatedRecordPermissions?.accounts?.viewScope]));
  const actsReadOwner = useDocumentDeliveryOwner(JSON.stringify([relatedReadScope, canViewActs, relatedRecordPermissions?.acts?.viewScope]));
  const paymentsReadOwner = useDocumentDeliveryOwner(JSON.stringify([relatedReadScope, canViewPayments, relatedRecordPermissions?.payments?.viewScope]));
  const { capture: captureFinanceReadOwner, scope: financeReadScope } = useDocumentDeliveryOwner(JSON.stringify([casesReadOwner.scope, paymentsReadOwner.scope]));
  const visibleCaseFinance = caseFinanceState?.scope === financeReadScope ? caseFinanceState : null;
  const caseFinanceSummaries = visibleCaseFinance?.items ?? {};
  const caseFinanceLoading = visibleCaseFinance?.loading ?? false;
  const caseFinanceError = visibleCaseFinance?.error ?? null;
  const previousRelatedReadScopes = useRef({ cases: casesReadOwner.scope, invoices: invoicesReadOwner.scope, acts: actsReadOwner.scope, payments: paymentsReadOwner.scope });
  useLayoutEffect(() => {
    const previous = previousRelatedReadScopes.current;
    previousRelatedReadScopes.current = { cases: casesReadOwner.scope, invoices: invoicesReadOwner.scope, acts: actsReadOwner.scope, payments: paymentsReadOwner.scope };
    if (previous.cases !== casesReadOwner.scope && editingCase) { setCaseModalOpen(false); setEditingCase(null); }
    if (previous.invoices !== invoicesReadOwner.scope && selectedInvoice) { setInvoiceModalOpen(false); setSelectedInvoice(null); }
    if (previous.acts !== actsReadOwner.scope && selectedAct) { setActModalOpen(false); setSelectedAct(null); }
    if (previous.payments !== paymentsReadOwner.scope && selectedPayment) { setPaymentModalOpen(false); setSelectedPayment(null); }
  }, [casesReadOwner.scope, invoicesReadOwner.scope, actsReadOwner.scope, paymentsReadOwner.scope, editingCase, selectedInvoice, selectedAct, selectedPayment]);
  const legacyClassification = mainCompanyState?.owner === classificationOwner ? mainCompanyState : null;
  const ownCompanyClassification = currentLegalEntityId === null ? false
    : typeof initial?.isOwnCompany === 'boolean' ? initial.isOwnCompany
      : legacyClassification && !legacyClassification.error ? legacyClassification.ids.includes(currentLegalEntityId) : null;
  const isOwnLegalEntity = ownCompanyClassification === true;
  const canViewClientLinks = ownCompanyClassification !== null && (!isOwnLegalEntity || canManageOwnCompanySettings);
  const canEditClientLinksNow = () => canViewClientLinks && isOpen && !readOnly
    && isClassificationOwnerCurrent(classificationOwner) && isAuthSessionIdentityCurrent(deleteSession);
  const clientPickerActive = pickerOpen && clientPickerOwner.current === classificationOwner && canViewClientLinks && !readOnly;
  const selectedCaseId =
    selectedLegalEntityCaseId === "all" ? undefined : selectedLegalEntityCaseId;
  const showLegacyImageFields = false;
  const shouldHidePageNavigation = isPageMode && hidePageNavigation;
  const shouldShowFormActions = !hideFormActions;
  const shouldShowDadataSection = !hideDadataSection;
  const canViewLegalEntityDocuments = canViewAccounts || canViewActs || canViewPayments;
  const canViewLegalEntityWorkSection = canViewCases || canViewLegalEntityDocuments;
  const resolvedTitle =
    titleOverride ??
    (initial
      ? t("editLegalEntity") || "Редактирование юридического лица"
      : t("addLegalEntity") || "Добавление юридического лица");

  const documentClientsNeeded = invoiceModalOpen && canMutateRelated('accounts', invoiceModalMode === 'edit' ? 'canEdit' : 'canCreate')
    || actModalOpen && canMutateRelated('acts', actModalMode === 'edit' ? 'canEdit' : 'canCreate');
  const clientsLookupOptions = useMemo(
    () =>
      ({
        mode: "paged" as const,
        enabled: isOpen && documentClientsNeeded,
        page: 1,
        pageSize: 200,
        isActive: true,
        sortBy: "name" as const,
        sortDir: "asc" as const,
      }) satisfies Parameters<typeof useClients>[0],
    [isOpen, documentClientsNeeded],
  );
  const {
    clients: documentClients,
    loading: documentClientsLoading,
    refreshing: documentClientsRefreshing,
    error: documentClientsError,
  } = useClients(clientsLookupOptions);
  const {
    legalEntities,
    loading: legalEntitiesLoading,
    error: legalEntitiesError,
  } = useLegalEntities({ enabled: isOpen && documentClientsNeeded,
    ownerScope: JSON.stringify([classificationScope, relatedInvoiceOwner.scope, relatedActOwner.scope]) });
  const {
    cases: legalEntityCases,
    loading: legalEntityCasesLoading,
    error: legalEntityCasesError,
    createCase: createLegalEntityCase,
    updateCase: updateLegalEntityCase,
    deleteCase: deleteLegalEntityCase,
    setCases: setLegalEntityCases,
  } = useClientCases(undefined, {
    enabled: deleteSession.authenticated && canViewCases && isOpen && Boolean(currentLegalEntityId),
    legalEntityId: currentLegalEntityId ?? undefined,
  }, casesReadOwner);
  const legalEntityInvoicesResult = useInvoices({
    legalEntityId: currentLegalEntityId ?? undefined,
    caseId: selectedCaseId,
    page: 1,
    pageSize: 5,
    sortBy: "date",
    sortDir: "desc",
    enabled: deleteSession.authenticated && canViewAccounts && isOpen && Boolean(currentLegalEntityId),
  }, invoicesReadOwner);
  const legalEntityActsResult = useActs({
    legalEntityId: currentLegalEntityId ?? undefined,
    caseId: selectedCaseId,
    page: 1,
    pageSize: 5,
    sortBy: "date",
    sortDir: "desc",
    enabled: deleteSession.authenticated && canViewActs && isOpen && Boolean(currentLegalEntityId),
  }, actsReadOwner);
  const legalEntityPaymentsResult = usePaymentsPaged({
    legalEntityId: currentLegalEntityId ?? undefined,
    caseId: selectedCaseId,
    page: 1,
    pageSize: 5,
    sortBy: "date",
    sortDir: "desc",
    enabled: deleteSession.authenticated && canViewPayments && isOpen && Boolean(currentLegalEntityId),
  }, paymentsReadOwner);

  useEffect(() => {
    if (!isOpen || currentLegalEntityId === null || typeof initial?.isOwnCompany === 'boolean') return;
    const owner = captureClassificationOwner();
    const session = captureAuthSessionIdentity();
    let cancelled = false;
    const current = () => !cancelled && isClassificationOwnerCurrent(owner) && isAuthSessionIdentityCurrent(session);
    setMainCompanyState(null);
    apiService
      .getMainCompanySettings()
      .then((settings) => {
        if (current()) {
          if (!Array.isArray(settings.companies) || !settings.companies.every(company => Number.isInteger(company.id) && company.id > 0))
            throw new Error('Не удалось подтвердить принадлежность компании.');
          setMainCompanyState({ owner, ids: settings.companies.map(company => company.id), error: null });
        }
      })
      .catch((error) => {
        if (current()) {
          setMainCompanyState({ owner, ids: [], error: extractDisplayErrorMessage(error, 'Не удалось подтвердить принадлежность компании.') });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [isOpen, currentLegalEntityId, initial?.isOwnCompany, captureClassificationOwner, isClassificationOwnerCurrent, classificationScope, classificationRetry]);

  const legalEntityCaseIdsKey = useMemo(
    () => legalEntityCases.map((item) => item.id).join(","),
    [legalEntityCases],
  );

  useEffect(() => {
    if (!deleteSession.authenticated || !canViewCases || !canViewPayments || !isOpen || !currentLegalEntityId || !legalEntityCaseIdsKey) {
      setCaseFinanceState({ scope: financeReadScope, items: {}, error: null, loading: false });
      return;
    }

    const owner = captureFinanceReadOwner();
    if (!owner) return;
    let alive = true;
    const current = () => alive && owner.isCurrent();
    setCaseFinanceState(previous => ({ scope: financeReadScope, items: previous?.scope === financeReadScope ? previous.items : {}, loading: true, error: null }));

    apiService
      .getLegalEntityCaseFinanceSummaries(currentLegalEntityId)
      .then((items) => {
        if (!current()) return;
        setCaseFinanceState({ scope: financeReadScope, items: Object.fromEntries((items ?? []).map((item) => [item.caseId, item])), loading: false, error: null });
      })
      .catch((err) => {
        if (!current()) return;
        setCaseFinanceState({ scope: financeReadScope, items: {}, loading: false, error: extractErrorMessage(
            err,
            "Не удалось загрузить экономику дел юридического лица",
          ) });
      });

    return () => {
      alive = false;
    };
  }, [
    canViewPayments,
    canViewCases,
    deleteSession.authenticated,
    financeReadScope,
    captureFinanceReadOwner,
    caseFinanceReloadToken,
    currentLegalEntityId,
    extractErrorMessage,
    isOpen,
    legalEntityCaseIdsKey,
  ]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        onClose();
      }
    };
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [isOpen, onClose]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    setPrintDefaultsDraft(companyImagePrintDraft(initial));
    setProfileEditVersion(initial?.profileEditVersion ?? null);
    if (initial) {
      setFormState({
        shortName: initial.shortName ?? "",
        fullName: initial.fullName ?? "",
        inn: initial.inn ?? "",
        kpp: initial.kpp ?? "",
        ogrn: initial.ogrn ?? "",
        address: initial.address ?? "",
        phone: initial.phone ?? "",
        email: initial.email ?? "",
        director: initial.director ?? "",
        responsibleUserId: initial.responsibleUserId
          ? String(initial.responsibleUserId)
          : "",
        clientStatusId: initial.clientStatusId
          ? String(initial.clientStatusId)
          : "",
        isClientAndSupplier: Boolean(initial.isClientAndSupplier),
        country: initial.country ?? "",
        baseCurrency: initial.baseCurrency ?? "",
        serviceLanguage: initial.serviceLanguage ?? "",
        organizationForm: initial.organizationForm ?? "",
        timezone: initial.timezone ?? "",
        taxSystem: initial.taxSystem ?? "",
        taxSystemMode: initial.taxSystemMode ?? "",
        incomeTaxRate: initial.incomeTaxRate ?? "",
        incomeExpenseTaxRate: initial.incomeExpenseTaxRate ?? "",
        vatWithoutVat: initial.vatWithoutVat ?? false,
        okved: initial.okved ?? "",
        okpo: initial.okpo ?? "",
        okato: initial.okato ?? "",
        oktmo: initial.oktmo ?? "",
        okogu: initial.okogu ?? "",
        okfs: initial.okfs ?? "",
        okopf: initial.okopf ?? "",
        pfr: initial.pfr ?? "",
        fss: initial.fss ?? "",
        charterCapital: initial.charterCapital ?? "",
        taxRegistrationDate: initial.taxRegistrationDate ?? "",
        taxOfficeCode: initial.taxOfficeCode ?? "",
        taxRegion: initial.taxRegion ?? "",
        ifnsName: initial.ifnsName ?? "",
        taxOfficePhone: initial.taxOfficePhone ?? "",
        treasuryInn: initial.treasuryInn ?? "",
        treasuryKpp: initial.treasuryKpp ?? "",
        treasuryRecipientName: initial.treasuryRecipientName ?? "",
        treasuryBankBik: initial.treasuryBankBik ?? "",
        treasuryBankName: initial.treasuryBankName ?? "",
        treasuryAccountNumber: initial.treasuryAccountNumber ?? "",
        treasuryCorrAccountNumber: initial.treasuryCorrAccountNumber ?? "",
        treasuryKbk: initial.treasuryKbk ?? "",
        notes: initial.notes ?? "",
        profileStatus: initial.profileStatus ?? "Active",
      });
      setSelectedClients(
        (initial.clients ?? []).map((client) =>
          toClientAssociationItem({
            id: client.id,
            name: client.name,
            phone: client.phone ?? undefined,
            email: client.email ?? undefined,
          }),
        ),
      );
      setBankAccounts(
        parseJsonOrDefault<BankAccountItem[]>(initial.bankAccountsJson, []),
      );
      setSavedBankBindingScope(JSON.stringify([parseJsonOrDefault<BankAccountItem[]>(initial.bankAccountsJson, []), initial.profileStatus ?? 'Active']));
      setCashRegisters(
        parseJsonOrDefault<CashRegisterItem[]>(initial.cashRegistersJson, []),
      );
      setCards(parseJsonOrDefault<CardItem[]>(initial.cardsJson, []));
      setEnglishRequisites(
        parseJsonOrDefault<EnglishRequisites>(
          initial.requisitesEnglishJson,
          EMPTY_ENGLISH_REQUISITES,
        ),
      );
      setDocumentDirector(
        parseJsonOrDefault<PersonDocInfo>(
          initial.documentDirectorJson,
          EMPTY_PERSON_DOC,
        ),
      );
      setChiefAccountant(
        parseJsonOrDefault<PersonDocInfo>(
          initial.chiefAccountantJson,
          EMPTY_PERSON_DOC,
        ),
      );
      setLogoDataUrl(initial.logoDataUrl ?? "");
      setSealDataUrl(initial.sealDataUrl ?? "");
      setDirectorSignatureDataUrl(initial.directorSignatureDataUrl ?? "");
      setPersistedLogoDataUrl(initial.logoDataUrl ?? "");
      setPersistedSealDataUrl(initial.sealDataUrl ?? "");
      setPersistedDirectorSignatureDataUrl(
        initial.directorSignatureDataUrl ?? "",
      );
      setLogoChanged(false);
      setSealChanged(false);
      setDirectorSignatureChanged(false);
      setAddressEntries(
        ensureSinglePrimaryAddressEntryList(
          (initial.addresses ?? []).reduce<ClientAddressEntryFormValue[]>(
            (acc, address) => {
              const nextEntry: ClientAddressEntryFormValue = {
                ...createEmptyLegalEntityAddressEntry(),
                type: mapAddressTypeToForm(address.type),
                fullAddress: chooseBestDisplayAddress(
                  {
                    postalCode: address.postalCode,
                    country: address.country,
                    region: address.regionWithType,
                    district: address.areaWithType,
                    city: address.cityWithType,
                    settlement: address.settlementWithType,
                    street: address.streetWithType,
                    house: address.house,
                    flat: address.flat,
                  },
                  getPreferredLegalEntityAddress(
                    address.value,
                    address.unrestrictedValue,
                  ),
                ),
                postalCode: address.postalCode ?? "",
                country: address.country ?? "",
                region: address.regionWithType ?? "",
                district: address.areaWithType ?? "",
                city: address.cityWithType ?? "",
                settlement: address.settlementWithType ?? "",
                street: address.streetWithType ?? "",
                house: address.house ?? "",
                flat: address.flat ?? "",
                fiasId: address.fiasId ?? "",
                kladrId: address.kladrId ?? "",
                geoLat: address.geoLat ?? "",
                geoLon: address.geoLon ?? "",
                isPrimary: address.isPrimary,
                additionalTypes: [],
              };

              const matchIndex = findMatchingLegalEntityAddressEntryIndex(
                acc,
                nextEntry,
              );
              if (matchIndex < 0) {
                acc.push(nextEntry);
                return acc;
              }

              acc[matchIndex] = mergeLegalEntityAddressEntry(
                acc[matchIndex],
                nextEntry,
              );
              return acc;
            },
            [],
          ),
        ),
      );
      setContactPhones(
        parseJsonOrDefault<string[]>(
          initial.contactPhonesJson,
          initial.phone ? [initial.phone] : [],
        ),
      );
      setContactEmails(
        parseJsonOrDefault<string[]>(
          initial.contactEmailsJson,
          initial.email ? [initial.email] : [],
        ),
      );
    } else {
      setFormState(DEFAULT_STATE);
      setSelectedClients([]);
      setBankAccounts([]);
      setCashRegisters([]);
      setCards([]);
      setEnglishRequisites(EMPTY_ENGLISH_REQUISITES);
      setDocumentDirector(EMPTY_PERSON_DOC);
      setChiefAccountant(EMPTY_PERSON_DOC);
      setLogoDataUrl("");
      setSealDataUrl("");
      setDirectorSignatureDataUrl("");
      setPersistedLogoDataUrl("");
      setPersistedSealDataUrl("");
      setPersistedDirectorSignatureDataUrl("");
      setLogoChanged(false);
      setSealChanged(false);
      setDirectorSignatureChanged(false);
      setAddressEntries([createEmptyLegalEntityAddressEntry(true)]);
      setContactPhones([]);
      setContactEmails([]);
    }
    setError(null);
    setSuggestions([]);
    setSuggestError(null);
    setDadataQuery(resolveInitialLegalEntityDadataQuery(initial?.inn));
    setPickerOpen(false);
    setPickerSearch("");
    setPickerClients([]);
    setPickerError(null);
    setPickerPage(1);
    setPickerTotal(0);
    setSelectedClientsPage(1);
    setHydratedSelectedClientIds([]);
    setSelectedLegalEntityCaseId("all");
    setCaseModalOpen(false);
    setEditingCase(null);
    setInvoiceModalOpen(false);
    setSelectedInvoice(null);
    setActModalOpen(false);
    setSelectedAct(null);
  }, [initial, isOpen]);

  useEffect(() => {
    if (!isOpen) return;
    void apiService
      .getActResponsibles()
      .then((items) => setResponsibles(items ?? []))
      .catch(() => setResponsibles([]));
  }, [isOpen]);

  useEffect(() => {
    if (
      selectedLegalEntityCaseId !== "all" &&
      !legalEntityCases.some((item) => item.id === selectedLegalEntityCaseId)
    ) {
      setSelectedLegalEntityCaseId("all");
    }
  }, [legalEntityCases, selectedLegalEntityCaseId]);

  useEffect(() => {
    if (!isOpen || !canViewClientLinks || selectedClients.length === 0) return;

    const owner = captureClassificationOwner();
    const session = captureAuthSessionIdentity();

    const idsToHydrate = selectedClients
      .map((client) => client.id)
      .filter((id) => !hydratedSelectedClientIds.includes(id));

    if (idsToHydrate.length === 0) return;

    let cancelled = false;

    const loadSelectedClients = async () => {
      try {
        const responses = await Promise.all(
          idsToHydrate.map((id) =>
            apiService
              .getClient(id)
              .then((client) => toClientAssociationItem(client))
              .catch(() => null),
          ),
        );

        if (cancelled || !isClassificationOwnerCurrent(owner) || !isAuthSessionIdentityCurrent(session)) return;

        const detailedClients = responses.filter(
          (client): client is ClientAssociationItem => client !== null,
        );
        if (detailedClients.length > 0) {
          setSelectedClients((prev) => {
            const detailedById = new Map(
              detailedClients.map((client) => [client.id, client]),
            );
            return prev.map((client) => detailedById.get(client.id) ?? client);
          });
        }
      } finally {
        if (!cancelled && isClassificationOwnerCurrent(owner) && isAuthSessionIdentityCurrent(session)) {
          setHydratedSelectedClientIds((prev) => [
            ...new Set([...prev, ...idsToHydrate]),
          ]);
        }
      }
    };

    void loadSelectedClients();

    return () => {
      cancelled = true;
    };
  }, [canViewClientLinks, hydratedSelectedClientIds, isOpen, selectedClients, classificationScope, captureClassificationOwner, isClassificationOwnerCurrent]);

  useEffect(() => {
    if (!clientPickerActive) return;
    const owner = captureClassificationOwner();
    const session = captureAuthSessionIdentity();
    let cancelled = false;
    const current = () => !cancelled && isClassificationOwnerCurrent(owner) && isAuthSessionIdentityCurrent(session);

    const fetchPickerClients = async () => {
      setPickerLoading(true);
      setPickerError(null);
      try {
        const response = await apiService.getClientsV2({
          search: debouncedPickerSearch || undefined,
          sortBy: "name",
          sortDir: "asc",
          page: pickerPage,
          pageSize: pickerPageSize,
        });

        if (!current()) return;

        setPickerClients(
          (response.items ?? []).map((client) =>
            toClientAssociationItem(client),
          ),
        );
        setPickerTotal(response.total ?? 0);
      } catch (err) {
        if (!current()) return;
        const message =
          err instanceof Error ? err.message : "Не удалось загрузить клиентов";
        setPickerClients([]);
        setPickerTotal(0);
        setPickerError(message);
        toast.error(message, "Ошибка загрузки");
      } finally {
        if (current()) {
          setPickerLoading(false);
        }
      }
    };

    void fetchPickerClients();

    return () => {
      cancelled = true;
    };
  }, [debouncedPickerSearch, clientPickerActive, pickerPage, pickerPageSize, toast, classificationScope, captureClassificationOwner, isClassificationOwnerCurrent]);

  useEffect(() => {
    setPickerPage(1);
  }, [debouncedPickerSearch, pickerPageSize]);

  const clientIds = useMemo(
    () => selectedClients.map((client) => client.id),
    [selectedClients],
  );
  const selectedClientIds = useMemo(() => new Set(clientIds), [clientIds]);
  const availableClients = useMemo(
    () => pickerClients.filter((client) => !selectedClientIds.has(client.id)),
    [pickerClients, selectedClientIds],
  );
  const selectedClientsTotalPages = useMemo(
    () =>
      Math.max(1, Math.ceil(selectedClients.length / selectedClientsPageSize)),
    [selectedClients.length, selectedClientsPageSize],
  );
  const pagedSelectedClients = useMemo(() => {
    const offset = (selectedClientsPage - 1) * selectedClientsPageSize;
    return selectedClients.slice(offset, offset + selectedClientsPageSize);
  }, [selectedClients, selectedClientsPage, selectedClientsPageSize]);
  const pickerTotalPages = useMemo(
    () => Math.max(1, Math.ceil(pickerTotal / pickerPageSize)),
    [pickerPageSize, pickerTotal],
  );

  useEffect(() => {
    setSelectedClientsPage((prev) => Math.min(prev, selectedClientsTotalPages));
  }, [selectedClientsTotalPages]);

  const handleInputChange = (
    e: React.ChangeEvent<
      HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement
    >,
  ) => {
    invalidateCompanyDetails();
    const { name, value, type } = e.target;
    const parsed =
      type === "checkbox" ? (e.target as HTMLInputElement).checked : value;
    setFormState((prev) => ({ ...prev, [name]: parsed }));
  };

  const handleAddClient = (id: number) => {
    if (!clientPickerActive || !canEditClientLinksNow()) return;
    const client = pickerClients.find((item) => item.id === id);
    if (!client) return;

    setSelectedClients((prev) =>
      prev.some((item) => item.id === id) ? prev : [...prev, client],
    );
    setHydratedSelectedClientIds((prev) =>
      prev.includes(id) ? prev : [...prev, id],
    );
  };

  const handleRemoveClient = (id: number) => {
    if (!canEditClientLinksNow()) return;
    setSelectedClients((prev) => prev.filter((client) => client.id !== id));
    setHydratedSelectedClientIds((prev) =>
      prev.filter((clientId) => clientId !== id),
    );
  };

  const updateImageProcessingState = () => {
    const pending = imageUploadsRef.current.size > 0;
    setImageProcessing(pending);
    if (!pending) setError((current) => current === IMAGE_PROCESSING_SAVE_MESSAGE ? null : current);
  };

  const handleImageUpload = async (
    file: File | null,
    setter: (value: string) => void,
    markChanged: (value: boolean) => void,
    kind: LegalEntityImageKind,
  ) => {
    if (!file || readOnly || loading) return;
    imageUploadsRef.current.get(kind)?.abort();
    const controller = new AbortController();
    imageUploadsRef.current.set(kind, controller);
    setImageProcessing(true);
    setError(null);
    try {
      const optimized = await convertImageToUploadProfileDataUrl(
        file,
        getLegalEntityImageProfile(kind),
        controller.signal,
      );
      if (imageUploadsRef.current.get(kind) !== controller || controller.signal.aborted) return;
      setter(optimized);
      markChanged(true);
    } catch (err) {
      if (imageUploadsRef.current.get(kind) !== controller || controller.signal.aborted) return;
      setError(
        err instanceof Error
          ? err.message
          : "Не удалось загрузить изображение.",
      );
    } finally {
      if (imageUploadsRef.current.get(kind) === controller) {
        imageUploadsRef.current.delete(kind);
        updateImageProcessingState();
      }
    }
  };

  const cancelImageUpload = (kind: LegalEntityImageKind) => {
    imageUploadsRef.current.get(kind)?.abort();
    imageUploadsRef.current.delete(kind);
    updateImageProcessingState();
  };

  const restoreImageValue = (
    kind: LegalEntityImageKind,
    persistedValue: string,
    setter: (value: string) => void,
    markChanged: (value: boolean) => void,
  ) => {
    if (readOnly || loading) return;
    cancelImageUpload(kind);
    setter(persistedValue);
    markChanged(false);
  };

  const requestImageRemoval = async (
    kind: LegalEntityImageKind,
    hasPersistedValue: boolean,
    setter: (value: string) => void,
    markChanged: (value: boolean) => void,
  ) => {
    if (readOnly || loading) return;
    const generation = imageFormGenerationRef.current;
    const approved = await confirm({
      title: "Удалить изображение",
      description: hasPersistedValue
        ? "Изображение будет удалено только после сохранения карточки. Продолжить?"
        : "Изображение будет убрано из формы. Продолжить?",
      confirmLabel: "Удалить",
      cancelLabel: "Отмена",
      variant: "danger",
    });

    if (!approved || generation !== imageFormGenerationRef.current) {
      return;
    }

    cancelImageUpload(kind);
    setter("");
    markChanged(true);
  };

  const clearImageValue = (
    kind: LegalEntityImageKind,
    setter: (value: string) => void,
    markChanged: (value: boolean) => void,
  ) => {
    if (readOnly || loading) return;
    cancelImageUpload(kind);
    setter("");
    markChanged(true);
  };

  const updateEnglishRequisites = (
    key: keyof EnglishRequisites,
    value: string,
  ) => {
    setEnglishRequisites((prev) => ({ ...prev, [key]: value }));
  };

  const updatePersonInfo = (
    setter: (value: (prev: PersonDocInfo) => PersonDocInfo) => void,
    key: keyof PersonDocInfo,
    value: string | boolean,
  ) => {
    setter((prev) => ({ ...prev, [key]: value }));
  };

  const updateAddressFormEntry = (
    index: number,
    key: keyof ClientAddressEntryFormValue,
    value: string,
  ) => {
    invalidateCompanyDetails();
    addressAutofill.invalidate(addressEntries[index]?.uiKey);
    setAddressEntries((prev) =>
      prev.map((entry, i) =>
        i === index ? { ...entry, [key]: value } : entry,
      ),
    );
  };

  const setPrimaryAddressFormEntry = (index: number) => {
    invalidateCompanyDetails();
    addressAutofill.invalidate(addressEntries[index]?.uiKey);
    setAddressEntries((prev) => setPrimaryAddressEntry(prev, index));
  };

  const toggleAdditionalAddressType = (index: number, type: string) => {
    invalidateCompanyDetails();
    addressAutofill.invalidate(addressEntries[index]?.uiKey);
    setAddressEntries((prev) =>
      prev.map((entry, i) => {
        if (i !== index) return entry;
        const exists = entry.additionalTypes.includes(type);
        return {
          ...entry,
          additionalTypes: exists
            ? entry.additionalTypes.filter((x) => x !== type)
            : [...entry.additionalTypes, type],
        };
      }),
    );
  };

  const handleSuggest = async () => {
    if (!canStartCompanyAutofill()) return;
    const owner = companyAutofillOwner.capture(); if (!owner) return;
    const sequence = ++companyAutofillRequests.current.search;
    invalidateCompanyDetails();
    const current = () => owner.isCurrent() && sequence === companyAutofillRequests.current.search;
    setCompanyAutofillResultScope(companyAutofillOwner.scope);
    setSuggestions([]);
    setSuggestError(null);
    const query = dadataQuery.trim();
    if (!query) {
      setSuggestError(
        t("legalEntityDadataEmptyQuery") || "Введите запрос для поиска",
      );
      return;
    }
    try {
      setSuggestLoading(true);
      const results = dedupeLegalEntitySuggestions(
        await suggest({ query, limit: 6 }),
        6,
      );
      if (!current()) return;
      setSuggestions(results);
      if (results.length === 0) {
        setSuggestError(t("legalEntityDadataNoResults") || "Ничего не найдено");
      }
    } catch (err) {
      if (!current()) return;
      setSuggestError(
        err instanceof Error
          ? err.message
          : t("legalEntityDadataError") || "Не удалось получить подсказки",
      );
    } finally {
      if (current()) setSuggestLoading(false);
    }
  };

  const applySuggestion = async (suggestion: LegalEntitySuggestion) => {
    if (!canStartCompanyAutofill() || !scopedSuggestions.includes(suggestion)) return;
    const owner = companyAutofillOwner.capture(); if (!owner) return;
    const sequence = ++companyAutofillRequests.current.details;
    const current = () => owner.isCurrent() && sequence === companyAutofillRequests.current.details;
    const baseAddress = suggestion.address?.trim();
    const applyFallbackAddress = (address: string) => {
      setAddressEntries((prev) =>
        upsertLegalEntityAddressEntry(prev, {
          ...createEmptyLegalEntityAddressEntry(),
          type: "legal",
          fullAddress: address,
        }),
      );
    };

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

    const query = (suggestion.inn || suggestion.ogrn || dadataQuery).trim();
    if (!query) {
      if (baseAddress) {
        applyFallbackAddress(baseAddress);
      }
      return;
    }

    try {
      const details = await findById({
        query,
        kpp: suggestion.kpp ?? undefined,
      });
      if (!current()) return;
      const normalizedAddress = chooseBestDisplayAddress(
        {
          country: details.country,
          postalCode: details.postalCode,
          region: details.regionWithType,
          district: details.areaWithType,
          city: details.cityWithType,
          settlement: details.settlementWithType,
          street: details.streetWithType,
          house: details.house,
          flat: details.flat,
        },
        details.addressUnrestricted,
        details.address,
        baseAddress,
      );

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
        organizationForm:
          details.opfShort ?? details.opfFull ?? prev.organizationForm,
        taxSystem: details.taxSystem ?? prev.taxSystem,
        okved: details.okved ?? prev.okved,
        okpo: details.okpo ?? prev.okpo,
        okato: details.okato ?? prev.okato,
        oktmo: details.oktmo ?? prev.oktmo,
        okogu: details.okogu ?? prev.okogu,
        okfs: details.okfs ?? prev.okfs,
      }));

      if (details.phones && details.phones.length > 0) {
        setContactPhones((prev) =>
          Array.from(
            new Set([...prev, ...details.phones!.filter((x) => x.trim())]),
          ),
        );
      }
      if (details.emails && details.emails.length > 0) {
        setContactEmails((prev) =>
          Array.from(
            new Set([...prev, ...details.emails!.filter((x) => x.trim())]),
          ),
        );
      }

      if (normalizedAddress) {
        setAddressEntries((prev) => {
          const nextEntry: ClientAddressEntryFormValue = {
            ...createEmptyLegalEntityAddressEntry(),
            type: "legal",
            fullAddress: normalizedAddress,
            postalCode: details.postalCode ?? "",
            country: details.country ?? "",
            region: details.regionWithType ?? "",
            district: details.areaWithType ?? "",
            city: details.cityWithType ?? "",
            settlement: details.settlementWithType ?? "",
            street: details.streetWithType ?? "",
            house: details.house ?? "",
            flat: details.flat ?? "",
            fiasId: details.fiasId ?? "",
            kladrId: details.kladrId ?? "",
            geoLat: details.geoLat ?? "",
            geoLon: details.geoLon ?? "",
          };
          const existingIndex = findMatchingLegalEntityAddressEntryIndex(
            prev,
            nextEntry,
          );

          if (existingIndex >= 0) {
            return prev.map((entry, idx) =>
              idx === existingIndex
                ? mergeLegalEntityAddressEntry(entry, nextEntry)
                : entry,
            );
          }

          return [nextEntry, ...prev];
        });
      } else if (baseAddress) {
        applyFallbackAddress(baseAddress);
      }
    } catch {
      if (current() && baseAddress) {
        applyFallbackAddress(baseAddress);
      }
    }
  };

  const updateDadataQuery = (value: string) => {
    if (!canStartCompanyAutofill() || !companyAutofillOwner.capture()) return;
    companyAutofillRequests.current.search++; invalidateCompanyDetails();
    setDadataQuery(value); setSuggestions([]); setSuggestError(null); setSuggestLoading(false);
  };

  const requiredFieldKey = (name: string) =>
    `${name.charAt(0).toUpperCase()}${name.slice(1)}`;
  const isFilledValue = (value: string | boolean | null | undefined) => {
    if (typeof value === "boolean") return value;
    return String(value ?? "").trim().length > 0;
  };
  const requiredInputClass = (
    name: string,
    value: string | boolean | null | undefined,
  ) => {
    if (!isRequired(requiredFieldKey(name))) {
      return "w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500";
    }

    const filled = isFilledValue(value);
    return `w-full rounded-lg border px-3 py-2 focus:outline-none focus:ring-2 ${
      filled
        ? "border-emerald-400 focus:border-emerald-500 focus:ring-emerald-200"
        : "border-red-400 focus:border-red-500 focus:ring-red-200"
    }`;
  };

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (deleteAttempt.current?.isCurrent()) return;
    if (loading || !isOpen) return;
    if (readOnly) {
      onClose();
      return;
    }
    if (!isAuthSessionIdentityCurrent(deleteSession)) return;
    if (ownCompanyClassification === null) {
      setError('Не удалось подтвердить принадлежность компании. Повторите проверку или откройте карточку заново. Введённые данные остаются в форме.');
      return;
    }
    if (imageUploadsRef.current.size > 0) {
      setError(IMAGE_PROCESSING_SAVE_MESSAGE);
      return;
    }
    const lifetime = saveLifetime.current.epoch;
    const context = saveContext.current;
    const isCurrentSave = () => lifetime === saveLifetime.current.epoch
      && saveContext.current.id === context.id && saveContext.current.isOpen
      && saveContext.current.readOnly === context.readOnly && saveContext.current.mode === context.mode;
    const saveAttempt = { isCurrent: () => isCurrentSave() && saveContext.current.initial === context.initial };
    activeSave.current = saveAttempt;
    setError(null);
    setLoading(true);
    try {
      if (initial?.id && isOwnLegalEntity && !/^[a-f\d]{64}$/i.test(profileEditVersion ?? "")) {
        setError("Для безопасного сохранения откройте карточку компании заново. Введённые данные пока остаются на экране.");
        return;
      }
      const shortName = formState.shortName.trim();
      const normalize = (value: string) => {
        const trimmed = value.trim();
        return trimmed || null;
      };
      if (
        logoDataUrl.length > BACKEND_IMAGE_DATA_URL_MAX_LENGTH ||
        sealDataUrl.length > BACKEND_IMAGE_DATA_URL_MAX_LENGTH ||
        directorSignatureDataUrl.length > BACKEND_IMAGE_DATA_URL_MAX_LENGTH
      ) {
        setError(
          "Изображение превышает допустимый размер на сервере. Выберите файл меньшего размера.",
        );
        setLoading(false);
        return;
      }

      const dedupedAddressInputs = dedupeLegalEntityAddressInputs(
        addressEntries
          .filter((address) => address.fullAddress.trim())
          .map((address) => ({
            type: mapAddressTypeFromForm(address.type),
            value: address.fullAddress.trim(),
            unrestrictedValue: address.fullAddress.trim(),
            postalCode: normalize(address.postalCode),
            country: normalize(address.country ?? ""),
            regionWithType: normalize(address.region),
            areaWithType: normalize(address.district),
            cityWithType: normalize(address.city),
            settlementWithType: normalize(address.settlement),
            streetWithType: normalize(address.street),
            house: normalize(address.house),
            flat: normalize(address.flat),
            fiasId: normalize(address.fiasId ?? ""),
            kladrId: normalize(address.kladrId ?? ""),
            geoLat: normalize(address.geoLat ?? ""),
            geoLon: normalize(address.geoLon ?? ""),
            isPrimary: address.isPrimary,
          })),
      );
      const primaryAddressInput =
        dedupedAddressInputs.find((address) => address.isPrimary) ??
        dedupedAddressInputs[0];

      const payload: LegalEntityInput = {
        ...(initial?.id && /^[a-f\d]{64}$/i.test(profileEditVersion ?? "") ? { profileEditVersion } : {}),
        ...(isOwnLegalEntity ? companyImagePrintPayload(printDefaultsDraft) : {}),
        shortName,
        fullName: normalize(formState.fullName),
        inn: normalize(formState.inn),
        kpp: normalize(formState.kpp),
        ogrn: normalize(formState.ogrn),
        address: primaryAddressInput
          ? normalize(
              primaryAddressInput.unrestrictedValue ??
                primaryAddressInput.value,
            )
          : normalize(formState.address),
        phone: normalize(formState.phone),
        email: normalize(formState.email),
        director: normalize(formState.director),
        responsibleUserId: formState.responsibleUserId
          ? Number(formState.responsibleUserId)
          : null,
        clientStatusId: formState.clientStatusId
          ? Number(formState.clientStatusId)
          : null,
        isClientAndSupplier: formState.isClientAndSupplier,
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
        treasuryCorrAccountNumber: normalize(
          formState.treasuryCorrAccountNumber,
        ),
        treasuryKbk: normalize(formState.treasuryKbk),
        bankAccountsJson:
          bankAccounts.length > 0 ? JSON.stringify(bankAccounts) : null,
        cashRegistersJson:
          cashRegisters.length > 0 ? JSON.stringify(cashRegisters) : null,
        cardsJson: cards.length > 0 ? JSON.stringify(cards) : null,
        requisitesEnglishJson: JSON.stringify(englishRequisites),
        documentDirectorJson: JSON.stringify(documentDirector),
        chiefAccountantJson: JSON.stringify(chiefAccountant),
        contactPhonesJson: JSON.stringify(
          contactPhones.filter((x) => x.trim()),
        ),
        contactEmailsJson: JSON.stringify(
          contactEmails.filter((x) => x.trim()),
        ),
        notes: normalize(formState.notes),
        profileStatus: isOwnLegalEntity && !canManageOwnCompanySettings ? initial?.profileStatus ?? 'Active' : formState.profileStatus,
        addresses: dedupedAddressInputs,
        clientIds: canViewClientLinks ? clientIds : (initial?.clients ?? []).map(client => client.id),
      };

      if (initial) {
        payload.logoChanged = logoChanged;
        payload.sealChanged = sealChanged;
        payload.directorSignatureChanged = directorSignatureChanged;
      }

      if (!initial || logoChanged) {
        payload.logoDataUrl = logoDataUrl || null;
      }

      if (!initial || sealChanged) {
        payload.sealDataUrl = sealDataUrl || null;
      }

      if (!initial || directorSignatureChanged) {
        payload.directorSignatureDataUrl = directorSignatureDataUrl || null;
      }
      if (!isLegalEntityInlineAssetPayloadSafe(payload)) {
        setError(
          "Суммарный размер логотипа, печати и подписи слишком большой для безопасного сохранения. Уменьшите изображения и попробуйте ещё раз.",
        );
        setLoading(false);
        return;
      }
      const serializedPayload = JSON.stringify(payload);
      if (
        !isLegalEntityRequestBodyLengthSafe(serializedPayload.length) ||
        serializedPayload.length > LEGAL_ENTITY_REQUEST_BODY_MAX_LENGTH
      ) {
        setError(
          "Данные юридического лица слишком большие для сохранения одним запросом. Уменьшите размер картинок или лишних вложенных данных.",
        );
        setLoading(false);
        return;
      }
      if (!payload.shortName) {
        setError(
          t("legalEntityShortNameRequired") || "Укажите краткое название",
        );
        setLoading(false);
        return;
      }
      const saved = await onSubmit(payload);
      if (!isCurrentSave()) return;
      if (initial && saved.id !== initial.id) throw new Error('Сервер вернул другую карточку юридического лица. Обновите карточку и повторите сохранение.');
      const latestInitial = saveContext.current.initial;
      if (latestInitial !== context.initial && latestInitial !== saved
        && latestInitial?.profileEditVersion !== context.initial?.profileEditVersion
        && latestInitial?.profileEditVersion !== saved.profileEditVersion) return;
      if (latestInitial !== context.initial && latestInitial !== saved
        && latestInitial?.documentImagePrintDefaultsVersion !== context.initial?.documentImagePrintDefaultsVersion
        && latestInitial?.documentImagePrintDefaultsVersion !== saved.documentImagePrintDefaultsVersion) return;
      setPrintDefaultsDraft(companyImagePrintDraft(saved));
      setProfileEditVersion(saved.profileEditVersion ?? null);
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
        responsibleUserId: saved.responsibleUserId
          ? String(saved.responsibleUserId)
          : prev.responsibleUserId,
        clientStatusId: saved.clientStatusId
          ? String(saved.clientStatusId)
          : "",
        isClientAndSupplier: Boolean(saved.isClientAndSupplier),
        country: saved.country ?? prev.country,
        baseCurrency: saved.baseCurrency ?? prev.baseCurrency,
        serviceLanguage: saved.serviceLanguage ?? prev.serviceLanguage,
        organizationForm: saved.organizationForm ?? prev.organizationForm,
        timezone: saved.timezone ?? prev.timezone,
        taxSystem: saved.taxSystem ?? prev.taxSystem,
        taxSystemMode: saved.taxSystemMode ?? prev.taxSystemMode,
        incomeTaxRate: saved.incomeTaxRate ?? prev.incomeTaxRate,
        incomeExpenseTaxRate:
          saved.incomeExpenseTaxRate ?? prev.incomeExpenseTaxRate,
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
        taxRegistrationDate:
          saved.taxRegistrationDate ?? prev.taxRegistrationDate,
        taxOfficeCode: saved.taxOfficeCode ?? prev.taxOfficeCode,
        taxRegion: saved.taxRegion ?? prev.taxRegion,
        ifnsName: saved.ifnsName ?? prev.ifnsName,
        taxOfficePhone: saved.taxOfficePhone ?? prev.taxOfficePhone,
        treasuryInn: saved.treasuryInn ?? prev.treasuryInn,
        treasuryKpp: saved.treasuryKpp ?? prev.treasuryKpp,
        treasuryRecipientName:
          saved.treasuryRecipientName ?? prev.treasuryRecipientName,
        treasuryBankBik: saved.treasuryBankBik ?? prev.treasuryBankBik,
        treasuryBankName: saved.treasuryBankName ?? prev.treasuryBankName,
        treasuryAccountNumber:
          saved.treasuryAccountNumber ?? prev.treasuryAccountNumber,
        treasuryCorrAccountNumber:
          saved.treasuryCorrAccountNumber ?? prev.treasuryCorrAccountNumber,
        treasuryKbk: saved.treasuryKbk ?? prev.treasuryKbk,
        notes: saved.notes ?? prev.notes,
        profileStatus: saved.profileStatus ?? prev.profileStatus,
      }));
      setSelectedClients((prev) =>
        mergeClientAssociationItems(
          prev.filter((client) =>
            (saved.clients ?? []).some(
              (savedClient) => savedClient.id === client.id,
            ),
          ),
          (saved.clients ?? []).map((client) =>
            toClientAssociationItem({
              id: client.id,
              name: client.name,
              phone: client.phone ?? undefined,
              email: client.email ?? undefined,
            }),
          ),
        ),
      );
      setHydratedSelectedClientIds(
        (saved.clients ?? []).map((client) => client.id),
      );
      setBankAccounts(
        parseJsonOrDefault<BankAccountItem[]>(
          saved.bankAccountsJson,
          bankAccounts,
        ),
      );
      setSavedBankBindingScope(JSON.stringify([parseJsonOrDefault<BankAccountItem[]>(saved.bankAccountsJson, bankAccounts), saved.profileStatus ?? formState.profileStatus]));
      setCashRegisters(
        parseJsonOrDefault<CashRegisterItem[]>(
          saved.cashRegistersJson,
          cashRegisters,
        ),
      );
      setCards(parseJsonOrDefault<CardItem[]>(saved.cardsJson, cards));
      setEnglishRequisites(
        parseJsonOrDefault<EnglishRequisites>(
          saved.requisitesEnglishJson,
          englishRequisites,
        ),
      );
      setDocumentDirector(
        parseJsonOrDefault<PersonDocInfo>(
          saved.documentDirectorJson,
          documentDirector,
        ),
      );
      setChiefAccountant(
        parseJsonOrDefault<PersonDocInfo>(
          saved.chiefAccountantJson,
          chiefAccountant,
        ),
      );
      setLogoDataUrl(saved.logoDataUrl ?? logoDataUrl);
      setSealDataUrl(saved.sealDataUrl ?? sealDataUrl);
      setDirectorSignatureDataUrl(
        saved.directorSignatureDataUrl ?? directorSignatureDataUrl,
      );
      setPersistedLogoDataUrl(saved.logoDataUrl ?? logoDataUrl);
      setPersistedSealDataUrl(saved.sealDataUrl ?? sealDataUrl);
      setPersistedDirectorSignatureDataUrl(
        saved.directorSignatureDataUrl ?? directorSignatureDataUrl,
      );
      setLogoChanged(false);
      setSealChanged(false);
      setDirectorSignatureChanged(false);
      setDadataQuery(
        (prev) =>
          prev.trim() || resolveInitialLegalEntityDadataQuery(saved.inn),
      );
      setAddressEntries(
        ensureSinglePrimaryAddressEntryList(
          (saved.addresses ?? []).reduce<ClientAddressEntryFormValue[]>(
            (acc, address) => {
              const nextEntry: ClientAddressEntryFormValue = {
                ...createEmptyLegalEntityAddressEntry(),
                type: mapAddressTypeToForm(address.type),
                fullAddress: chooseBestDisplayAddress(
                  {
                    postalCode: address.postalCode,
                    country: address.country,
                    region: address.regionWithType,
                    district: address.areaWithType,
                    city: address.cityWithType,
                    settlement: address.settlementWithType,
                    street: address.streetWithType,
                    house: address.house,
                    flat: address.flat,
                  },
                  getPreferredLegalEntityAddress(
                    address.value,
                    address.unrestrictedValue,
                  ),
                ),
                postalCode: address.postalCode ?? "",
                country: address.country ?? "",
                region: address.regionWithType ?? "",
                district: address.areaWithType ?? "",
                city: address.cityWithType ?? "",
                settlement: address.settlementWithType ?? "",
                street: address.streetWithType ?? "",
                house: address.house ?? "",
                flat: address.flat ?? "",
                fiasId: address.fiasId ?? "",
                kladrId: address.kladrId ?? "",
                geoLat: address.geoLat ?? "",
                geoLon: address.geoLon ?? "",
                isPrimary: address.isPrimary,
              };

              const matchIndex = findMatchingLegalEntityAddressEntryIndex(
                acc,
                nextEntry,
              );
              if (matchIndex < 0) {
                acc.push(nextEntry);
                return acc;
              }

              acc[matchIndex] = mergeLegalEntityAddressEntry(
                acc[matchIndex],
                nextEntry,
              );
              return acc;
            },
            [],
          ),
        ),
      );
      setContactPhones(
        parseJsonOrDefault<string[]>(saved.contactPhonesJson, contactPhones),
      );
      setContactEmails(
        parseJsonOrDefault<string[]>(saved.contactEmailsJson, contactEmails),
      );
      if (!isPageMode) {
        onClose();
      }
    } catch (err) {
      if (err instanceof StaleUiOperationError) return;
      if (!isCurrentSave()) return;
      if (saveContext.current.initial?.documentImagePrintDefaultsVersion !== context.initial?.documentImagePrintDefaultsVersion) return;
      if (saveContext.current.initial?.profileEditVersion !== context.initial?.profileEditVersion) return;
      const message = extractErrorMessage(
        err,
        t("saveError") || "Не удалось сохранить",
      );
      setError(message);
      toast.error(message, "Ошибка сохранения");
    } finally {
      if (activeSave.current === saveAttempt) activeSave.current = null;
      if (isCurrentSave()) setLoading(false);
    }
  };

  const handleDelete = async () => {
    const context = deleteContext.current;
    const current = () => isCurrentDeleteOwner(deleteOwnerAtRender)
      && isAuthSessionIdentityCurrent(deleteSession)
      && deleteContext.current.initial === context.initial && deleteContext.current.isOpen
      && deleteContext.current.allowDelete && deleteContext.current.readOnly === context.readOnly
      && deleteContext.current.mode === context.mode;
    if (!current() || !deleteSession.authenticated || !context.onDelete || !context.initial
      || deleteAttempt.current?.isCurrent() || activeSave.current?.isCurrent() || loading || imageUploadsRef.current.size > 0) return;
    deleteAttempt.current?.cancel();
    const controller = new AbortController();
    const attempt = { isCurrent: current, cancel: () => controller.abort() };
    deleteAttempt.current = attempt;
    setDeleting(true);
    setError(null);
    try {
      const approved = await awaitOwnedUiResult(() => confirm({
        signal: controller.signal,
        title: "Подтверждение удаления",
        description:
          context.initial!.clientsCount > 0
            ? `Удалить юридическое лицо? Карточки связанных клиентов (${context.initial!.clientsCount}) сохранятся, связь с этой организацией будет снята.`
            : t("deleteLegalEntityConfirm") || "Удалить юридическое лицо?",
        confirmLabel: "Удалить",
        cancelLabel: "Отмена",
        variant: "danger",
      }), current);
      if (!approved) return;
      await awaitOwnedUiResult(context.onDelete, current);
      context.onClose();
    } catch (err) {
      if (err instanceof StaleUiOperationError || !current()) return;
      const message = extractErrorMessage(
        err,
        t("legalEntityDeleteError") || "Не удалось удалить запись",
      );
      setError(message);
      toast.error(message, "Удаление недоступно");
    } finally {
      if (deleteAttempt.current === attempt) {
        deleteAttempt.current = null;
        controller.abort();
        if (current()) setDeleting(false);
      }
    }
  };

  const openCreateCase = () => {
    if (!canMutateRelated('cases', 'canCreate') || !currentLegalEntityId) return;
    setCaseMode("create");
    setEditingCase(null);
    setCaseModalOpen(true);
  };

  const openEditCase = (clientCase: ClientCase) => {
    if (!canMutateRelated('cases', 'canEdit')) return;
    setCaseMode("edit");
    setEditingCase(clientCase);
    setCaseModalOpen(true);
  };

  const saveLegalEntityCase = async (patch: Partial<ClientCase>) => {
    const operation = isOpen && caseModalOpen && deleteSession.authenticated && currentLegalEntityId
      && canMutateRelated('cases', caseMode === 'edit' ? 'canEdit' : 'canCreate') ? relatedCaseOwner.begin('save') : null;
    if (!operation) throw new StaleUiOperationError();
    try {

      const payload: LegalEntityCasePayload = {
        clientId: null,
        legalEntityId: currentLegalEntityId,
        title: (patch.title ?? editingCase?.title ?? "").trim(),
        description: patch.description ?? editingCase?.description ?? "",
        status: patch.status ?? editingCase?.status ?? "Open",
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
      };

      if (caseMode === "edit" && editingCase) {
        await updateLegalEntityCase(editingCase.id, payload, operation.isCurrent);
        if (!operation.isCurrent()) throw new StaleUiOperationError();
        setCaseFinanceReloadToken((value) => value + 1);
        return;
      }

      const created = await createLegalEntityCase(payload, operation.isCurrent);
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      setSelectedLegalEntityCaseId(created.id);
      setCaseFinanceReloadToken((value) => value + 1);
    } finally { operation.finish(); }
  };

  const removeLegalEntityCase = async () => {
    const operation = isOpen && caseModalOpen && deleteSession.authenticated && editingCase
      && canMutateRelated('cases', 'canDelete') ? relatedCaseOwner.begin('delete') : null;
    if (!operation || !editingCase) throw new StaleUiOperationError();
    try {
      await deleteLegalEntityCase(editingCase.id, operation.isCurrent);
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      if (selectedLegalEntityCaseId === editingCase.id) {
        setSelectedLegalEntityCaseId("all");
      }
      setCaseFinanceReloadToken((value) => value + 1);
    } finally { operation.finish(); }
  };

  const closeCaseModal = () => {
    if (!relatedCaseOwner.capture()) return;
    relatedCaseOwner.invalidate();
    setCaseModalOpen(false);
  };

  const openCreateInvoice = () => {
    if (!canMutateRelated('accounts', 'canCreate') || !currentLegalEntityId) return;
    setInvoiceModalMode("create");
    setSelectedInvoice(null);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const openEditInvoice = (invoice: Invoice) => {
    if (!canMutateRelated('accounts', 'canEdit')) return;
    setInvoiceModalMode("edit");
    setSelectedInvoice(invoice);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const closeInvoiceModal = () => {
    if (!relatedInvoiceOwner.capture()) return;
    relatedInvoiceOwner.invalidate();
    setInvoiceModalOpen(false);
    setSelectedInvoice(null);
    setInvoiceModalError(null);
  };

  const submitInvoice = async (payload: InvoiceInput) => {
    const errorOwner = relatedInvoiceOwner.capture();
    const operation = isOpen && invoiceModalOpen && deleteSession.authenticated
      && canMutateRelated('accounts', invoiceModalMode === 'edit' ? 'canEdit' : 'canCreate') ? relatedInvoiceOwner.begin('save') : null;
    if (!operation || !errorOwner) throw new StaleUiOperationError();
    try {
      setInvoiceModalError(null);
      if (invoiceModalMode === "edit" && selectedInvoice) {
        await legalEntityInvoicesResult.updateInvoice(selectedInvoice.id, payload, operation.isCurrent);
      } else {
        await legalEntityInvoicesResult.createInvoice(payload, operation.isCurrent);
      }
      if (canViewPayments) await legalEntityPaymentsResult.refreshOwned(operation.isCurrent);
      if (canViewActs) await legalEntityActsResult.refreshOwned(operation.isCurrent);
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      setCaseFinanceReloadToken((value) => value + 1);
      closeInvoiceModal();
    } catch (err) {
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      const message = extractErrorMessage(err, "Не удалось сохранить счёт");
      setInvoiceModalError({ owner: errorOwner, message });
      throw err;
    } finally {
      operation.finish();
    }
  };

  const openCreateAct = () => {
    if (!canMutateRelated('acts', 'canCreate') || !currentLegalEntityId) return;
    setActModalMode("create");
    setSelectedAct(null);
    setActModalError(null);
    setActModalOpen(true);
  };

  const openEditAct = (act: Act) => {
    if (!canMutateRelated('acts', 'canEdit')) return;
    setActModalMode("edit");
    setSelectedAct(act);
    setActModalError(null);
    setActModalOpen(true);
  };

  const closeActModal = () => {
    if (!relatedActOwner.capture()) return;
    relatedActOwner.invalidate();
    setActModalOpen(false);
    setSelectedAct(null);
    setActModalError(null);
  };

  const submitAct = async (payload: ActInput) => {
    const errorOwner = relatedActOwner.capture();
    const operation = isOpen && actModalOpen && deleteSession.authenticated
      && canMutateRelated('acts', actModalMode === 'edit' ? 'canEdit' : 'canCreate') ? relatedActOwner.begin('save') : null;
    if (!operation || !errorOwner) throw new StaleUiOperationError();
    try {
      setActModalError(null);
      if (actModalMode === "edit" && selectedAct) {
        await legalEntityActsResult.updateAct(selectedAct.id, payload, operation.isCurrent);
      } else {
        await legalEntityActsResult.createAct(payload, operation.isCurrent);
      }
      if (canViewAccounts) await legalEntityInvoicesResult.refreshOwned(operation.isCurrent);
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      setCaseFinanceReloadToken((value) => value + 1);
      closeActModal();
    } catch (err) {
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      const message = extractErrorMessage(err, "Не удалось сохранить акт");
      setActModalError({ owner: errorOwner, message });
      throw err;
    } finally {
      operation.finish();
    }
  };

  const handleInvoiceCaseCreated = (clientCase: ClientCase) => {
    if (!isOpen || !invoiceModalOpen || !canViewCases || !currentLegalEntityId
      || !isInvoiceCaseOwnerCurrent(invoiceCaseOwner) || !isAuthSessionIdentityCurrent(deleteSession)
      || !canMutateRelated('accounts', invoiceModalMode === 'edit' ? 'canEdit' : 'canCreate')) return;
    if (!clientCase || !Number.isInteger(clientCase.id) || clientCase.id <= 0 || clientCase.legalEntityId !== currentLegalEntityId) return;
    setLegalEntityCases((prev) =>
      prev.some((item) => item.id === clientCase.id)
        ? prev
        : [clientCase, ...prev],
    );
    // The invoice owns its selected cases; changing the parent's default resets its unsaved form.
    setCaseFinanceReloadToken((value) => value + 1);
  };

  const openCreatePayment = (caseId?: number) => {
    if (!canMutateRelated('payments', 'canCreate') || !currentLegalEntityId) return;
    paymentReadAttempt.current = null;
    if (caseId) {
      setSelectedLegalEntityCaseId(caseId);
    }
    setPaymentModalMode("create");
    setSelectedPayment(null);
    setPaymentModalOpen(true);
  };

  const openEditPayment = async (payment: Payment) => {
    if (!isOpen || !currentLegalEntityId || !deleteSession.authenticated || !isAuthSessionIdentityCurrent(deleteSession)
      || !isPaymentReadOwnerCurrent(paymentReadOwner) || !canMutateRelated('payments', 'canEdit')
      || !Number.isInteger(payment.id) || payment.id <= 0) return;
    if (paymentReadAttempt.current?.id === payment.id && paymentReadAttempt.current.isCurrent()) return;
    const attempt: { id: number; isCurrent: () => boolean } = { id: payment.id, isCurrent: () => paymentReadAttempt.current === attempt
      && isPaymentReadOwnerCurrent(paymentReadOwner) && isAuthSessionIdentityCurrent(deleteSession) };
    paymentReadAttempt.current = attempt;
    try {
      const detailedPayment = await apiService.getPayment(payment.id);
      if (!attempt.isCurrent()) return;
      if (!detailedPayment || detailedPayment.id !== payment.id)
        throw new Error('Не удалось подтвердить выбранный платёж. Повторите открытие.');
      setPaymentModalMode("edit");
      setSelectedPayment(detailedPayment);
      setPaymentModalOpen(true);
    } catch (error) {
      if (!attempt.isCurrent()) return;
      toast.error(
        extractDisplayErrorMessage(error, "Не удалось загрузить платеж для редактирования."),
        "Платежи",
      );
    } finally {
      if (paymentReadAttempt.current === attempt) paymentReadAttempt.current = null;
    }
  };

  const closePaymentModal = () => {
    if (!relatedPaymentOwner.capture()) return;
    relatedPaymentOwner.invalidate();
    paymentReadAttempt.current = null;
    setPaymentModalOpen(false);
    setSelectedPayment(null);
  };

  const normalizeLegalEntityPaymentPayload = (payload: PaymentPayload): PaymentPayload => {
    const legalEntityId = payload.legalEntityId ?? (payload.clientId != null ? null : currentLegalEntityId);
    return {
      ...payload,
      notes: payload.notes ?? "",
      systemNotes: payload.systemNotes ?? "",
      clientId: legalEntityId ? null : payload.clientId ?? null,
      legalEntityId: legalEntityId ?? null,
      expenseClientId: payload.expenseLegalEntityId ? null : payload.expenseClientId ?? null,
      expenseLegalEntityId: payload.expenseLegalEntityId ?? null,
      clientCaseId: payload.clientCaseId ?? selectedCaseId ?? null,
      dealTypeId: payload.dealTypeId ?? null,
      incomeTypeId: payload.incomeTypeId ?? null,
      paymentSourceId: payload.paymentSourceId ?? null,
      paymentStatusId: payload.paymentStatusId ?? null,
      account: payload.account ?? null,
      accountDate: payload.accountDate ?? null,
    };
  };

  const refreshAfterPaymentSave = async (owner: DocumentDeliveryRead) => {
    try {
      if (canViewPayments) await legalEntityPaymentsResult.refreshOwned(owner.isCurrent);
      if (canViewAccounts) await legalEntityInvoicesResult.refreshOwned(owner.isCurrent);
      if (canViewActs) await legalEntityActsResult.refreshOwned(owner.isCurrent);
      if (!owner.isCurrent()) return;
      setCaseFinanceReloadToken((value) => value + 1);
    } catch {
      if (owner.isCurrent()) toast.error('Платёж сохранён, но не удалось обновить связанные списки.', 'Платежи');
    }
  };

  const beginPaymentWrite = (permission: 'canCreate' | 'canEdit', kind: string) => {
    const owner = relatedPaymentOwner.capture();
    const operation = isOpen && paymentModalOpen && deleteSession.authenticated && currentLegalEntityId
      && canMutateRelated('payments', permission) ? relatedPaymentOwner.begin(kind) : null;
    if (!owner || !operation) throw new StaleUiOperationError();
    return { owner, operation };
  };

  const submitPayment = async (
    payload: PaymentPayload | ({ id: number } & PaymentPayload),
  ): Promise<Payment> => {
    const { owner, operation } = beginPaymentWrite('id' in payload ? 'canEdit' : 'canCreate', 'save');
    let savedPayment: Payment;
    try {
      if ("id" in payload) {
        const { id, ...rest } = payload;
        savedPayment = await apiService.updatePayment(id, normalizeLegalEntityPaymentPayload(rest));
      } else {
        savedPayment = await apiService.createPayment(normalizeLegalEntityPaymentPayload(payload));
      }
      // Preserve the exact receipt-bearing result even if its UI was replaced.
      // PaymentModal decides adoption; a dispatched financial write is not undone.
      if (operation.isCurrent()) void refreshAfterPaymentSave(owner);
      return savedPayment;
    } catch (error) {
      if (error instanceof PaymentCreateAdoptedError) throw error;
      if (operation.isCurrent()) toast.error(
        extractErrorMessage(error, "Не удалось сохранить платёж"),
        "Платежи",
      );
      throw error;
    } finally {
      operation.finish();
    }
  };

  const transferPaymentRemainder = async (
    payload: { id: number } & PaymentPayload,
    remainderDate: string,
  ): Promise<Payment> => {
    const { owner, operation } = beginPaymentWrite('canEdit', 'transfer');
    let savedPayment: Payment;
    try {
      const { id, ...rest } = payload;
      const result = await apiService.transferPaymentRemainder(
        id,
        normalizeLegalEntityPaymentPayload(rest),
        remainderDate,
      );
      savedPayment = result.payment;
      if (operation.isCurrent()) {
        void refreshAfterPaymentSave(owner);
        toast.success("Остаток платежа перенесён.", "Платежи");
      }
      return savedPayment;
    } catch (error) {
      if (operation.isCurrent()) toast.error(
        extractErrorMessage(error, "Не удалось перенести остаток платежа"),
        "Платежи",
      );
      throw error;
    } finally {
      operation.finish();
    }
  };

  const rollbackPaymentRemainderTransfer = async (paymentId: number) => {
    const { owner, operation } = beginPaymentWrite('canEdit', 'rollback');
    try {
      await apiService.rollbackPaymentRemainderTransfer(paymentId);
      if (!operation.isCurrent()) return;
      await refreshAfterPaymentSave(owner);
      if (!operation.isCurrent()) return;
      closePaymentModal();
      toast.success("Перенос остатка отменен.", "Платежи");
    } catch (error) {
      if (operation.isCurrent()) toast.error(
        extractErrorMessage(error, "Не удалось откатить перенос остатка"),
        "Платежи",
      );
      throw error;
    } finally {
      operation.finish();
    }
  };

  const adjustPaymentRemainderTransfer = async (
    paymentId: number,
    paidAmount: number,
    remainderDate: string,
  ) => {
    const { owner, operation } = beginPaymentWrite('canEdit', 'adjust');
    try {
      await apiService.adjustPaymentRemainderTransfer(paymentId, paidAmount, remainderDate);
      if (!operation.isCurrent()) return;
      await refreshAfterPaymentSave(owner);
      if (!operation.isCurrent()) return;
      closePaymentModal();
      toast.success("Разделение платежа обновлено.", "Платежи");
    } catch (error) {
      if (operation.isCurrent()) toast.error(
        extractErrorMessage(error, "Не удалось изменить разделение платежа"),
        "Платежи",
      );
      throw error;
    } finally {
      operation.finish();
    }
  };

  const clientsSection = (
    <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
      <div className="flex items-center justify-between gap-2 border-b border-gray-100 pb-3">
        <div>
          <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">
            {t("legalEntityClients") || "Клиенты юридического лица"}
          </h3>
          <p className="text-xs text-gray-500">
            {t("legalEntityClientsHint") ||
              "Выберите клиентов, которые относятся к этому юридическому лицу."}
          </p>
        </div>
        {!compactReadOnlyView ? (
          <button
            type="button"
            onClick={() => {
              if (!canEditClientLinksNow()) return;
              clientPickerOwner.current = classificationOwner;
              setPickerClients([]); setPickerTotal(0); setPickerError(null); setPickerLoading(true);
              setPickerPage(1); setPickerOpen(true);
            }}
            disabled={readOnly}
            className="inline-flex items-center gap-2 rounded border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <Plus className="h-4 w-4" />
            {t("addClient") || "Добавить клиента"}
          </button>
        ) : null}
      </div>

      <div className="mt-3 overflow-x-auto">
        <table className="min-w-full table-fixed divide-y text-sm">
          <colgroup>
            <col style={{ width: "28%" }} />
            <col style={{ width: "20%" }} />
            <col style={{ width: "36%" }} />
            {!compactReadOnlyView ? <col style={{ width: "16%" }} /> : null}
          </colgroup>
          <thead className="bg-slate-50 text-xs uppercase text-slate-500">
            <tr>
              <th className="px-3 py-2 text-left">ФИО</th>
              <th className="px-3 py-2 text-left">Компания</th>
              <th className="px-3 py-2 text-left">Контакты</th>
              {!compactReadOnlyView ? (
                <th className="px-3 py-2 text-right">Действия</th>
              ) : null}
            </tr>
          </thead>
          <tbody className="divide-y">
            {pagedSelectedClients.map((client) => (
              <tr key={client.id}>
                <td className="px-3 py-2">
                  <div className="font-medium text-gray-900">{client.name}</div>
                  <div className="mt-1">
                    <ClientStatusBadge status={client.clientStatus} />
                  </div>
                </td>
                <td className="px-3 py-2">{client.company || "—"}</td>
                <td className="px-3 py-2 text-xs text-gray-600">
                  <div>{client.phone || "—"}</div>
                  <div>{client.email || "—"}</div>
                </td>
                {!compactReadOnlyView ? (
                  <td className="px-3 py-2 text-right">
                    <button
                      type="button"
                      onClick={() => handleRemoveClient(client.id)}
                      disabled={readOnly}
                      className="rounded border border-rose-200 p-1 text-rose-600 disabled:opacity-60"
                    >
                      <X className="h-4 w-4" />
                    </button>
                  </td>
                ) : null}
              </tr>
            ))}
            {selectedClients.length === 0 && (
              <tr>
                <td
                  colSpan={compactReadOnlyView ? 3 : 4}
                  className="px-3 py-6 text-center text-slate-500"
                >
                  {t("nothingFound") || "Ничего не найдено"}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      <TablePagination
        page={selectedClientsPage}
        totalPages={selectedClientsTotalPages}
        totalItems={selectedClients.length}
        pageSize={selectedClientsPageSize}
        pageSizeOptions={CLIENT_PAGE_SIZE_OPTIONS}
        onPageChange={setSelectedClientsPage}
        onPageSizeChange={(value) => {
          setSelectedClientsPageSize(value);
          setSelectedClientsPage(1);
        }}
      />
    </div>
  );

  const blankLegalEntityCase: ClientCase = useMemo(() => ({
    id: 0,
    clientId: null,
    legalEntityId: currentLegalEntityId,
    title: "",
    description: "",
    status: "Open",
    createdAt: new Date().toISOString(),
  }), [currentLegalEntityId]);
  const initialRelatedActValues = useMemo(() => actModalMode === 'create' && currentLegalEntityId
    ? { legalEntityId: currentLegalEntityId } : undefined, [actModalMode, currentLegalEntityId]);

  const documentsLookupsLoading =
    documentClientsLoading || documentClientsRefreshing || legalEntitiesLoading;
  const documentsLookupsError = [
    documentClientsError,
    legalEntitiesError,
  ]
    .filter(Boolean)
    .join("; ");
  const filteredLegalEntityCases = useMemo(() => {
    const query = caseSearch.trim().toLowerCase();
    return legalEntityCases.filter((clientCase) => {
      const matchesSearch =
        query.length === 0 ||
        clientCase.title?.toLowerCase().includes(query) ||
        clientCase.description?.toLowerCase().includes(query) ||
        clientCase.caseNumber?.toLowerCase().includes(query) ||
        clientCase.caseType?.toLowerCase().includes(query) ||
        clientCase.stage?.toLowerCase().includes(query) ||
        clientCase.courtName?.toLowerCase().includes(query) ||
        clientCase.courtCaseNumber?.toLowerCase().includes(query);
      const matchesStatus =
        caseStatusFilter === "all" ||
        normalizeCaseStatusFilter(clientCase.status) === caseStatusFilter;
      return matchesSearch && matchesStatus;
    });
  }, [caseSearch, caseStatusFilter, legalEntityCases]);
  const caseStatusCounts = useMemo(
    () =>
      legalEntityCases.reduce<Record<LegalEntityCaseStatusFilter, number>>(
        (acc, clientCase) => {
          const key = normalizeCaseStatusFilter(clientCase.status);
          acc[key] += 1;
          return acc;
        },
        { all: legalEntityCases.length, open: 0, hold: 0, closed: 0, lost: 0 },
      ),
    [legalEntityCases],
  );
  const caseFilterBadges = useMemo(() => {
    const badges: string[] = [];
    if (caseSearch.trim()) badges.push(`Поиск: ${caseSearch.trim()}`);
    if (caseStatusFilter !== "all") {
      const labels: Record<LegalEntityCaseStatusFilter, string> = {
        all: "Все статусы",
        open: "Открыто",
        hold: "Приостановлено",
        closed: "Закрыто",
        lost: "Проиграно",
      };
      badges.push(`Статус: ${labels[caseStatusFilter]}`);
    }
    return badges;
  }, [caseSearch, caseStatusFilter]);
  const resetCaseFilters = () => {
    setCaseSearch("");
    setCaseStatusFilter("all");
  };

  const legacyLegalEntityWorkSection = (
    <section className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
      <div className="flex flex-col gap-3 border-b border-slate-100 pb-4 xl:flex-row xl:items-center xl:justify-between">
        <div className="flex items-center gap-3">
          <div className="inline-flex h-10 w-10 items-center justify-center rounded-xl bg-blue-50 text-blue-600">
            <FileCheck2 className="h-5 w-5" />
          </div>
          <div>
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">
              Дела и документы
            </h3>
            <div className="mt-1 flex flex-wrap gap-2 text-xs text-slate-500">
              <span>Дел: {legalEntityCases.length}</span>
              <span>Счетов: {legalEntityInvoicesResult.pagination.total}</span>
              <span>Актов: {legalEntityActsResult.pagination.total}</span>
            </div>
          </div>
        </div>
        {currentLegalEntityId && !readOnly ? (
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={openCreateCase}
              disabled={!canMutateRelated('cases', 'canCreate')}
              className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-700 transition hover:bg-blue-100"
            >
              <Plus className="h-4 w-4" />
              Дело
            </button>
            <button
              type="button"
              onClick={openCreateInvoice}
              disabled={!canMutateRelated('accounts', 'canCreate')}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
            >
              <FileText className="h-4 w-4" />
              Счёт
            </button>
            <button
              type="button"
              onClick={openCreateAct}
              disabled={!canMutateRelated('acts', 'canCreate')}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
            >
              <FileCheck2 className="h-4 w-4" />
              Акт
            </button>
          </div>
        ) : null}
      </div>

      {!currentLegalEntityId ? (
        <div className="mt-5 rounded-xl border border-dashed border-slate-200 px-4 py-6 text-center text-sm text-slate-500">
          Сохраните карточку, чтобы добавить дела, счета и акты.
        </div>
      ) : (
        <div className="mt-5 space-y-6">
          <div>
            <div className="mb-3 flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={() => setSelectedLegalEntityCaseId("all")}
                className={`rounded-full border px-3 py-1.5 text-xs font-semibold transition ${
                  selectedLegalEntityCaseId === "all"
                    ? "border-blue-200 bg-blue-50 text-blue-700"
                    : "border-slate-200 text-slate-600 hover:bg-slate-50"
                }`}
              >
                Все дела
              </button>
              {legalEntityCases.map((clientCase) => (
                <button
                  key={clientCase.id}
                  type="button"
                  onClick={() => setSelectedLegalEntityCaseId(clientCase.id)}
                  className={`max-w-full rounded-full border px-3 py-1.5 text-xs font-semibold transition ${
                    selectedLegalEntityCaseId === clientCase.id
                      ? "border-blue-200 bg-blue-50 text-blue-700"
                      : "border-slate-200 text-slate-600 hover:bg-slate-50"
                  }`}
                >
                  <span className="block max-w-[16rem] truncate">
                    {clientCase.title || "Без названия"}
                  </span>
                </button>
              ))}
            </div>

            {legalEntityCasesError ? (
              <div className="mb-3 rounded-xl border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {extractErrorMessage(
                  legalEntityCasesError,
                  "Не удалось загрузить дела",
                )}
              </div>
            ) : null}

            <div className="overflow-x-auto rounded-xl border border-slate-200">
              <table className="min-w-full table-fixed text-sm">
                <colgroup>
                  <col style={{ width: "42%" }} />
                  <col style={{ width: "18%" }} />
                  <col style={{ width: "18%" }} />
                  {!readOnly ? <col style={{ width: "22%" }} /> : null}
                </colgroup>
                <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                  <tr>
                    <th className="px-3 py-2 text-left">Дело</th>
                    <th className="px-3 py-2 text-left">Статус</th>
                    <th className="px-3 py-2 text-left">Создано</th>
                    {!readOnly ? (
                      <th className="px-3 py-2 text-right">Действия</th>
                    ) : null}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {legalEntityCasesLoading ? (
                    <tr>
                      <td
                        colSpan={readOnly ? 3 : 4}
                        className="px-3 py-6 text-center text-slate-500"
                      >
                        Загрузка...
                      </td>
                    </tr>
                  ) : legalEntityCases.length === 0 ? (
                    <tr>
                      <td
                        colSpan={readOnly ? 3 : 4}
                        className="px-3 py-6 text-center text-slate-500"
                      >
                        Дел пока нет.
                      </td>
                    </tr>
                  ) : (
                    legalEntityCases.map((clientCase) => (
                      <tr
                        key={clientCase.id}
                        className={
                          selectedLegalEntityCaseId === clientCase.id
                            ? "bg-blue-50/40"
                            : "bg-white"
                        }
                      >
                        <td className="px-3 py-3 align-top">
                          <button
                            type="button"
                            onClick={() =>
                              setSelectedLegalEntityCaseId(clientCase.id)
                            }
                            className="text-left font-medium text-slate-900 transition hover:text-blue-700"
                          >
                            {clientCase.title || "Без названия"}
                          </button>
                          {clientCase.description ? (
                            <div className="mt-1 line-clamp-2 text-xs text-slate-500">
                              {clientCase.description}
                            </div>
                          ) : null}
                        </td>
                        <td className="px-3 py-3 align-top">
                          {renderCaseStatusBadge(clientCase.status)}
                        </td>
                        <td className="px-3 py-3 align-top text-slate-600">
                          {formatDocumentDate(clientCase.createdAt)}
                        </td>
                        {!readOnly ? (
                          <td className="px-3 py-3 text-right align-top">
                            <button
                              type="button"
                              onClick={() => openEditCase(clientCase)}
                              disabled={!canMutateRelated('cases', 'canEdit')}
                              className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-2 py-1 text-xs font-medium text-slate-600 transition hover:bg-slate-50"
                            >
                              <Pencil className="h-3.5 w-3.5" />
                              Изменить
                            </button>
                          </td>
                        ) : null}
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>

          <div className="grid gap-5 xl:grid-cols-2">
            <div className="rounded-xl border border-slate-200">
              <div className="flex items-center justify-between gap-3 border-b border-slate-100 px-4 py-3">
                <h4 className="text-sm font-semibold text-slate-800">Счета</h4>
                {legalEntityInvoicesResult.refreshing ? (
                  <span className="text-xs text-slate-400">Обновление...</span>
                ) : null}
              </div>
              <div className="divide-y divide-slate-100">
                {legalEntityInvoicesResult.loading ? (
                  <div className="px-4 py-6 text-center text-sm text-slate-500">
                    Загрузка...
                  </div>
                ) : legalEntityInvoicesResult.error ? (
                  <div className="px-4 py-4 text-sm text-rose-700">
                    {legalEntityInvoicesResult.error}
                  </div>
                ) : legalEntityInvoicesResult.invoices.length === 0 ? (
                  <div className="px-4 py-6 text-center text-sm text-slate-500">
                    Счетов пока нет.
                  </div>
                ) : (
                  legalEntityInvoicesResult.invoices.map((invoice) => (
                    <div key={invoice.id} className="px-4 py-3">
                      <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0">
                          <button
                            type="button"
                            onClick={() => openEditInvoice(invoice)}
                            disabled={!canMutateRelated('accounts', 'canEdit')}
                            className="truncate text-left font-medium text-slate-900 transition hover:text-blue-700 disabled:hover:text-slate-900"
                          >
                            № {invoice.number || invoice.id}
                          </button>
                          <div className="mt-1 text-xs text-slate-500">
                            {formatDocumentDate(invoice.date)} ·{" "}
                            {INVOICE_STATUS_LABELS[invoice.status] ??
                              invoice.status}
                          </div>
                        </div>
                        <div className="shrink-0 text-right text-sm font-semibold text-slate-900">
                          {formatCurrencySmart(invoice.amount).short}
                        </div>
                      </div>
                    </div>
                  ))
                )}
              </div>
            </div>

            <div className="rounded-xl border border-slate-200">
              <div className="flex items-center justify-between gap-3 border-b border-slate-100 px-4 py-3">
                <h4 className="text-sm font-semibold text-slate-800">Акты</h4>
                {legalEntityActsResult.refreshing ? (
                  <span className="text-xs text-slate-400">Обновление...</span>
                ) : null}
              </div>
              <div className="divide-y divide-slate-100">
                {legalEntityActsResult.loading ? (
                  <div className="px-4 py-6 text-center text-sm text-slate-500">
                    Загрузка...
                  </div>
                ) : legalEntityActsResult.error ? (
                  <div className="px-4 py-4 text-sm text-rose-700">
                    {legalEntityActsResult.error}
                  </div>
                ) : legalEntityActsResult.acts.length === 0 ? (
                  <div className="px-4 py-6 text-center text-sm text-slate-500">
                    Актов пока нет.
                  </div>
                ) : (
                  legalEntityActsResult.acts.map((act) => (
                    <div key={act.id} className="px-4 py-3">
                      <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0">
                          <button
                            type="button"
                            onClick={() => openEditAct(act)}
                            disabled={!canMutateRelated('acts', 'canEdit')}
                            className="truncate text-left font-medium text-slate-900 transition hover:text-blue-700 disabled:hover:text-slate-900"
                          >
                            № {act.number || act.id}
                          </button>
                          <div className="mt-1 text-xs text-slate-500">
                            {formatDocumentDate(act.date)} ·{" "}
                            {ACT_STATUS_LABELS[act.status] ?? act.status}
                          </div>
                        </div>
                        <div className="shrink-0 text-right text-sm font-semibold text-slate-900">
                          {formatCurrencySmart(act.amount).short}
                        </div>
                      </div>
                    </div>
                  ))
                )}
              </div>
            </div>
          </div>
        </div>
      )}
    </section>
  );

  void legacyLegalEntityWorkSection;

  const legalEntityWorkSection = canViewLegalEntityWorkSection ? (
    <section className="rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div className="flex flex-col gap-3 border-b border-slate-100 px-5 py-4 xl:flex-row xl:items-center xl:justify-between">
        <div>
          <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">
            Дела и документы
          </h3>
          <div className="mt-2 flex flex-wrap gap-2 text-xs text-slate-500">
            {canViewCases ? <span>Дел: {legalEntityCases.length}</span> : null}
            {canViewCases ? <span>Открыто: {caseStatusCounts.open}</span> : null}
            {canViewPayments ? <span>Платежей: {legalEntityPaymentsResult.total}</span> : null}
            {canViewAccounts ? <span>Счетов: {legalEntityInvoicesResult.pagination.total}</span> : null}
            {canViewActs ? <span>Актов: {legalEntityActsResult.pagination.total}</span> : null}
          </div>
        </div>
        {currentLegalEntityId && !readOnly ? (
          <div className="flex flex-wrap gap-2">
            {canViewCases ? (
              <button
                type="button"
                onClick={openCreateCase}
                disabled={!canMutateRelated('cases', 'canCreate')}
                className="inline-flex h-10 items-center gap-2 rounded-lg bg-emerald-600 px-3 text-sm font-medium text-white shadow-sm transition hover:bg-emerald-700"
              >
                <Plus className="h-4 w-4" />
                Дело
              </button>
            ) : null}
            {canViewPayments ? (
              <button
                type="button"
                onClick={() => openCreatePayment()}
                disabled={!canMutateRelated('payments', 'canCreate')}
                className="inline-flex h-10 items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 transition hover:border-blue-200 hover:bg-blue-50 hover:text-blue-700"
              >
                <WalletCards className="h-4 w-4" />
                Платёж
              </button>
            ) : null}
            {canViewAccounts ? (
              <button
                type="button"
                onClick={openCreateInvoice}
                disabled={!canMutateRelated('accounts', 'canCreate')}
                className="inline-flex h-10 items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 transition hover:border-blue-200 hover:bg-blue-50 hover:text-blue-700"
              >
                <FileText className="h-4 w-4" />
                Счёт
              </button>
            ) : null}
            {canViewActs ? (
              <button
                type="button"
                onClick={openCreateAct}
                disabled={!canMutateRelated('acts', 'canCreate')}
                className="inline-flex h-10 items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 transition hover:border-blue-200 hover:bg-blue-50 hover:text-blue-700"
              >
                <FileCheck2 className="h-4 w-4" />
                Акт
              </button>
            ) : null}
          </div>
        ) : null}
      </div>

      {!currentLegalEntityId ? (
        <div className="m-5 rounded-xl border border-dashed border-slate-200 px-4 py-6 text-center text-sm text-slate-500">
          Сохраните карточку, чтобы добавить доступные связанные данные.
        </div>
      ) : (
        <div className="space-y-5 p-5">
          <div className="rounded-2xl border border-emerald-100 bg-emerald-50 px-5 py-4">
            {canViewCases ? (
              <p className="text-sm font-semibold text-emerald-900">
                Всего дел: {legalEntityCases.length}
              </p>
            ) : null}
            {canViewPayments ? (
              <p className="mt-1 text-sm text-emerald-700">
                Доходы {formatCurrencySmart(legalEntityPaymentsResult.summary.income).full}, расходы{" "}
                {formatCurrencySmart(legalEntityPaymentsResult.summary.expense).full}, баланс{" "}
                {formatCurrencySmart(legalEntityPaymentsResult.summary.balance).full}.
              </p>
            ) : null}
            {canViewPayments && caseFinanceError ? (
              <p className="mt-2 text-xs font-medium text-rose-700">
                {caseFinanceError}
              </p>
            ) : null}
          </div>

          {canViewCases ? (
          <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4">
            <div className="grid gap-3 lg:grid-cols-[minmax(260px,1fr)_220px]">
              <label className="block text-xs font-semibold uppercase tracking-wide text-slate-500">
                Поиск
                <div className="relative mt-1">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="search"
                    value={caseSearch}
                    onChange={(event) => setCaseSearch(event.target.value)}
                    placeholder="Название или описание дела"
                    className="w-full rounded-lg border border-slate-300 bg-white py-2 pl-10 pr-3 text-sm text-slate-700 placeholder:text-slate-400 focus:border-slate-400 focus:outline-none focus:ring-2 focus:ring-slate-200"
                  />
                </div>
              </label>
              <label className="block text-xs font-semibold uppercase tracking-wide text-slate-500">
                Статус
                <SystemSelect
                  value={caseStatusFilter}
                  onChange={(event) =>
                    setCaseStatusFilter(
                      event.target.value as LegalEntityCaseStatusFilter,
                    )
                  }
                  className="mt-1 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700 focus:border-slate-400 focus:outline-none focus:ring-2 focus:ring-slate-200"
                >
                  <option value="all">Все статусы</option>
                  <option value="open">Открыто</option>
                  <option value="hold">Приостановлено</option>
                  <option value="closed">Закрыто</option>
                  <option value="lost">Проиграно</option>
                </SystemSelect>
              </label>
            </div>
            {caseFilterBadges.length > 0 ? (
              <div className="mt-3 flex flex-wrap items-center gap-2">
                {caseFilterBadges.map((badge) => (
                  <span
                    key={badge}
                    className="rounded-full border border-slate-200 bg-white px-3 py-1 text-xs font-medium text-slate-600"
                  >
                    {badge}
                  </span>
                ))}
                <button
                  type="button"
                  onClick={resetCaseFilters}
                  className="text-xs font-medium text-blue-600 hover:text-blue-700"
                >
                  Сбросить
                </button>
              </div>
            ) : null}
          </div>
          ) : null}

          {canViewCases ? (
          <>
          {legalEntityCasesError ? (
            <div className="rounded-xl border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              {extractErrorMessage(
                legalEntityCasesError,
                "Не удалось загрузить дела",
              )}
            </div>
          ) : null}

          {legalEntityCasesLoading ? (
            <div className="rounded-2xl border border-slate-200 bg-slate-50 px-6 py-10 text-center text-sm text-slate-500">
              Загрузка дел...
            </div>
          ) : legalEntityCases.length === 0 ? (
            <div className="rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-6 py-10 text-center">
              <FileCheck2 className="mx-auto mb-3 h-10 w-10 text-slate-300" />
              <h4 className="text-lg font-semibold text-slate-900">
                Дела пока не добавлены
              </h4>
              <p className="mt-2 text-sm text-slate-500">
                Создайте первое дело, чтобы привязывать к нему платежи, счета и акты юридического лица.
              </p>
              {!readOnly ? (
                <button
                  type="button"
                  onClick={openCreateCase}
                  disabled={!canMutateRelated('cases', 'canCreate')}
                  className="mt-5 inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition hover:bg-emerald-700"
                >
                  <Plus className="h-4 w-4" />
                  Добавить дело
                </button>
              ) : null}
            </div>
          ) : filteredLegalEntityCases.length === 0 ? (
            <div className="rounded-2xl border border-slate-200 bg-slate-50 px-6 py-8 text-center text-sm text-slate-500">
              По выбранным фильтрам дел нет.
            </div>
          ) : (
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              {filteredLegalEntityCases.map((caseItem) => {
                const finance = canViewPayments ? caseFinanceSummaries[caseItem.id] : undefined;
                const legacyCasePayments = canViewPayments ? (caseItem as ClientCase & { payments?: Payment[] }).payments : undefined;
                const paymentsCount = finance
                  ? finance.incomeCount + finance.expenseCount
                  : legacyCasePayments?.length ?? 0;
                const debt = finance
                  ? finance.incomeOutstanding + finance.expenseOutstanding
                  : 0;

                return (
                  <article
                    key={caseItem.id}
                    className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm transition-colors hover:border-emerald-200 hover:shadow-md"
                  >
                    <div className="flex h-full flex-col gap-4">
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <h4 className="truncate text-base font-semibold text-slate-900">
                            {caseItem.title || `Дело #${caseItem.id}`}
                          </h4>
                          <span
                            className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-medium ${caseStatusClassName(caseItem.status)}`}
                          >
                            {CASE_STATUS_META[caseItem.status ?? ""]?.label ??
                              caseItem.status}
                          </span>
                        </div>
                        <p className="mt-2 text-sm text-slate-500">
                          {caseItem.description?.trim() ||
                            "Описание дела пока не заполнено."}
                        </p>
                        <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-slate-500">
                          {caseItem.caseNumber ? <span>№ {caseItem.caseNumber}</span> : null}
                          {caseItem.caseType ? <span>Тип: {caseItem.caseType}</span> : null}
                          {caseItem.stage ? <span>Этап: {caseItem.stage}</span> : null}
                          {caseItem.dueDate ? <span>Срок: {formatDocumentDate(caseItem.dueDate)}</span> : null}
                          <span>Создано: {formatDocumentDate(caseItem.createdAt)}</span>
                          {canViewPayments ? <span>Платежей: {paymentsCount}</span> : null}
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
                      </div>

                      {canViewPayments ? (
                      <div className="rounded-xl border border-slate-100 bg-slate-50 p-3">
                        {finance ? (
                          <div className="grid grid-cols-2 gap-2 text-xs">
                            <button
                              type="button"
                              onClick={() => setSelectedLegalEntityCaseId(caseItem.id)}
                              className="rounded-lg p-2 text-left transition hover:bg-white hover:shadow-sm"
                            >
                              <p className="text-slate-500">Доходы</p>
                              <p className="mt-1 font-semibold text-emerald-700">
                                {formatCurrencySmart(finance.incomeTotal).full}
                              </p>
                            </button>
                            <button
                              type="button"
                              onClick={() => setSelectedLegalEntityCaseId(caseItem.id)}
                              className="rounded-lg p-2 text-left transition hover:bg-white hover:shadow-sm"
                            >
                              <p className="text-slate-500">Расходы</p>
                              <p className="mt-1 font-semibold text-rose-700">
                                {formatCurrencySmart(finance.expenseTotal).full}
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
                                {formatCurrencySmart(finance.plannedMargin).full}
                              </p>
                            </div>
                            <div>
                              <p className="text-slate-500">Долги</p>
                              <p className="mt-1 font-semibold text-amber-700">
                                {formatCurrencySmart(debt).full}
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
                      ) : null}

                      <div className="mt-auto flex flex-wrap items-center gap-2">
                        {canViewPayments ? (
                          <button
                            type="button"
                            onClick={() => setSelectedLegalEntityCaseId(caseItem.id)}
                            className="inline-flex h-10 items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700 transition-colors hover:border-blue-200 hover:bg-blue-50 hover:text-blue-700"
                          >
                            <WalletCards className="h-4 w-4" />
                            Платежи
                          </button>
                        ) : null}
                        {!readOnly ? (
                          <>
                            {canViewPayments ? (
                              <button
                                type="button"
                                onClick={() => openCreatePayment(caseItem.id)}
                                disabled={!canMutateRelated('payments', 'canCreate')}
                                className="inline-flex h-10 items-center gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-3 text-sm font-medium text-emerald-700 transition-colors hover:bg-emerald-100"
                              >
                                <Plus className="h-4 w-4" />
                                Платёж
                              </button>
                            ) : null}
                            <button
                              type="button"
                              onClick={() => {
                                setSelectedLegalEntityCaseId(caseItem.id);
                                openEditCase(caseItem);
                              }}
                              title="Редактировать дело"
                              className="ml-auto inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition-colors hover:border-emerald-200 hover:bg-emerald-50 hover:text-emerald-700"
                            >
                              <Pencil className="h-4 w-4" />
                            </button>
                          </>
                        ) : null}
                      </div>
                    </div>
                  </article>
                );
              })}
            </div>
          )}
          </>
          ) : null}

          <div className="space-y-3">
            {canViewCases ? (
              <div className="flex flex-wrap gap-2">
                <button
                  type="button"
                  onClick={() => setSelectedLegalEntityCaseId("all")}
                  className={`rounded-full border px-3 py-1.5 text-sm transition-colors ${
                    selectedLegalEntityCaseId === "all"
                      ? "border-slate-800 bg-slate-800 text-white shadow-sm"
                      : "border-slate-200 bg-slate-50 text-slate-700 hover:bg-slate-100"
                  }`}
                >
                  Все дела
                </button>
                {legalEntityCases.map((clientCase) => (
                <button
                  key={clientCase.id}
                  type="button"
                  onClick={() => setSelectedLegalEntityCaseId(clientCase.id)}
                  title={CASE_STATUS_META[clientCase.status ?? ""]?.label}
                  className={`max-w-full rounded-full border px-3 py-1.5 text-sm transition-colors ${
                    selectedLegalEntityCaseId === clientCase.id
                      ? "border-slate-800 bg-slate-800 text-white shadow-sm"
                      : `${caseStatusClassName(clientCase.status)} hover:opacity-90`
                  }`}
                >
                  <span className="block max-w-[16rem] truncate">
                    {clientCase.title || "Без названия"}
                  </span>
                </button>
                ))}
              </div>
            ) : null}

            {canViewLegalEntityDocuments ? (
            <div className="grid gap-5 xl:grid-cols-3">
              {canViewPayments ? (
                <div className="rounded-xl border border-slate-200">
                  <div className="flex items-center justify-between gap-3 border-b border-slate-100 px-4 py-3">
                    <h4 className="text-sm font-semibold text-slate-800">Платежи</h4>
                    {legalEntityPaymentsResult.refreshing ? (
                      <span className="text-xs text-slate-400">Обновление...</span>
                    ) : null}
                  </div>
                  <div className="divide-y divide-slate-100">
                    {legalEntityPaymentsResult.loading ? (
                      <div className="px-4 py-6 text-center text-sm text-slate-500">
                        Загрузка...
                      </div>
                    ) : legalEntityPaymentsResult.error ? (
                      <div className="px-4 py-4 text-sm text-rose-700">
                        {legalEntityPaymentsResult.error}
                      </div>
                    ) : legalEntityPaymentsResult.payments.length === 0 ? (
                      <div className="px-4 py-6 text-center text-sm text-slate-500">
                        Платежей пока нет.
                      </div>
                    ) : (
                      legalEntityPaymentsResult.payments.map((payment) => (
                        <div key={payment.id} className="px-4 py-3">
                          <div className="flex items-start justify-between gap-3">
                            <div className="min-w-0">
                              <button
                                type="button"
                                onClick={() => openEditPayment(payment)}
                                disabled={!canMutateRelated('payments', 'canEdit')}
                                className="truncate text-left font-medium text-slate-900 transition hover:text-blue-700 disabled:hover:text-slate-900"
                              >
                                {payment.description || `Платёж #${payment.id}`}
                              </button>
                              <div className="mt-1 text-xs text-slate-500">
                                {formatDocumentDate(payment.date)} ·{" "}
                                {INVOICE_STATUS_LABELS[payment.status] ??
                                  payment.status}
                              </div>
                            </div>
                            <div
                              className={`shrink-0 text-right text-sm font-semibold ${
                                payment.type === "Expense"
                                  ? "text-rose-700"
                                  : "text-emerald-700"
                              }`}
                            >
                              {formatCurrencySmart(payment.amount).short}
                            </div>
                          </div>
                        </div>
                      ))
                    )}
                  </div>
                </div>
              ) : null}

              {canViewAccounts ? (
              <div className="rounded-xl border border-slate-200">
                <div className="flex items-center justify-between gap-3 border-b border-slate-100 px-4 py-3">
                  <h4 className="text-sm font-semibold text-slate-800">Счета</h4>
                  {legalEntityInvoicesResult.refreshing ? (
                    <span className="text-xs text-slate-400">Обновление...</span>
                  ) : null}
                </div>
                <div className="divide-y divide-slate-100">
                  {legalEntityInvoicesResult.loading ? (
                    <div className="px-4 py-6 text-center text-sm text-slate-500">
                      Загрузка...
                    </div>
                  ) : legalEntityInvoicesResult.error ? (
                    <div className="px-4 py-4 text-sm text-rose-700">
                      {legalEntityInvoicesResult.error}
                    </div>
                  ) : legalEntityInvoicesResult.invoices.length === 0 ? (
                    <div className="px-4 py-6 text-center text-sm text-slate-500">
                      Счетов пока нет.
                    </div>
                  ) : (
                    legalEntityInvoicesResult.invoices.map((invoice) => (
                      <div key={invoice.id} className="px-4 py-3">
                        <div className="flex items-start justify-between gap-3">
                          <div className="min-w-0">
                            <button
                              type="button"
                              onClick={() => openEditInvoice(invoice)}
                              disabled={!canMutateRelated('accounts', 'canEdit')}
                              className="truncate text-left font-medium text-slate-900 transition hover:text-blue-700 disabled:hover:text-slate-900"
                            >
                              № {invoice.number || invoice.id}
                            </button>
                            <div className="mt-1 text-xs text-slate-500">
                              {formatDocumentDate(invoice.date)} ·{" "}
                              {INVOICE_STATUS_LABELS[invoice.status] ??
                                invoice.status}
                            </div>
                          </div>
                          <div className="shrink-0 text-right text-sm font-semibold text-slate-900">
                            {formatCurrencySmart(invoice.amount).short}
                          </div>
                        </div>
                      </div>
                    ))
                  )}
                </div>
              </div>
              ) : null}

              {canViewActs ? (
              <div className="rounded-xl border border-slate-200">
                <div className="flex items-center justify-between gap-3 border-b border-slate-100 px-4 py-3">
                  <h4 className="text-sm font-semibold text-slate-800">Акты</h4>
                  {legalEntityActsResult.refreshing ? (
                    <span className="text-xs text-slate-400">Обновление...</span>
                  ) : null}
                </div>
                <div className="divide-y divide-slate-100">
                  {legalEntityActsResult.loading ? (
                    <div className="px-4 py-6 text-center text-sm text-slate-500">
                      Загрузка...
                    </div>
                  ) : legalEntityActsResult.error ? (
                    <div className="px-4 py-4 text-sm text-rose-700">
                      {legalEntityActsResult.error}
                    </div>
                  ) : legalEntityActsResult.acts.length === 0 ? (
                    <div className="px-4 py-6 text-center text-sm text-slate-500">
                      Актов пока нет.
                    </div>
                  ) : (
                    legalEntityActsResult.acts.map((act) => (
                      <div key={act.id} className="px-4 py-3">
                        <div className="flex items-start justify-between gap-3">
                          <div className="min-w-0">
                            <button
                              type="button"
                              onClick={() => openEditAct(act)}
                              disabled={!canMutateRelated('acts', 'canEdit')}
                              className="truncate text-left font-medium text-slate-900 transition hover:text-blue-700 disabled:hover:text-slate-900"
                            >
                              № {act.number || act.id}
                            </button>
                            <div className="mt-1 text-xs text-slate-500">
                              {formatDocumentDate(act.date)} ·{" "}
                              {ACT_STATUS_LABELS[act.status] ?? act.status}
                            </div>
                          </div>
                          <div className="shrink-0 text-right text-sm font-semibold text-slate-900">
                            {formatCurrencySmart(act.amount).short}
                          </div>
                        </div>
                      </div>
                    ))
                  )}
                </div>
              </div>
              ) : null}
            </div>
            ) : null}
          </div>
        </div>
      )}
    </section>
  ) : null;

  const dadataSection = (
    <div className="rounded-xl border border-dashed border-blue-200 bg-blue-50/50 p-4">
      <h3 className="text-sm font-semibold text-blue-900">
        {t("legalEntityDadataTitle") || "Заполнить по данным DaData"}
      </h3>
      <p className="mt-1 text-xs text-blue-700/80">
        {t("legalEntityDadataDescription") ||
          "Введите ИНН, название или адрес компании, чтобы автоматически заполнить основные поля."}
      </p>
      <div className="mt-3 flex items-center gap-2">
        <div className="relative flex-1">
          <Search className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-blue-400" />
          <input
            type="text"
            value={dadataQuery}
            onChange={(e) => updateDadataQuery(e.target.value)}
            disabled={readOnly || loading}
            placeholder={
              t("legalEntityDadataPlaceholder") ||
              "Например: 7707083893 или ООО Пример"
            }
            className="w-full rounded-lg border border-blue-200 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-400"
          />
        </div>
        <button
          type="button"
          onClick={handleSuggest}
          disabled={scopedSuggestLoading || readOnly || loading}
          className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300"
        >
          {scopedSuggestLoading && <Loader2 className="h-4 w-4 animate-spin" />}
          {t("legalEntityDadataSearch") || "Найти"}
        </button>
      </div>
      {scopedSuggestError && (
        <div className="mt-2 rounded-lg border border-amber-200 bg-amber-50 p-2 text-xs text-amber-700">
          {scopedSuggestError}
        </div>
      )}
      <div className="mt-3 space-y-2">
        {scopedSuggestions.map((suggestion, idx) => (
          <button
            key={`${suggestion.inn ?? suggestion.shortName}-${idx}`}
            type="button"
            disabled={readOnly || loading}
            onClick={() => {
              void applySuggestion(suggestion);
            }}
            className="w-full rounded-lg border border-blue-200 bg-white px-4 py-3 text-left text-sm shadow-sm transition hover:border-blue-400 hover:shadow"
          >
            <div className="flex items-start justify-between gap-2">
              <div>
                <div className="font-medium text-blue-900">
                  {suggestion.shortName}
                </div>
                {suggestion.fullName && (
                  <div className="text-xs text-blue-700">
                    {suggestion.fullName}
                  </div>
                )}
                <div className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-blue-700/80">
                  {suggestion.inn && <span>ИНН: {suggestion.inn}</span>}
                  {suggestion.kpp && <span>КПП: {suggestion.kpp}</span>}
                  {suggestion.ogrn && <span>ОГРН: {suggestion.ogrn}</span>}
                </div>
                {suggestion.address && (
                  <div className="mt-1 text-xs text-blue-700/80">
                    {suggestion.address}
                  </div>
                )}
              </div>
              <CheckCircle2 className="h-5 w-5 text-blue-500" />
            </div>
          </button>
        ))}
        {!scopedSuggestLoading && scopedSuggestions.length === 0 && !scopedSuggestError && (
          <div className="rounded-lg border border-dashed border-blue-200 p-3 text-center text-xs text-blue-700/80">
            {t("legalEntityDadataHint") ||
              "Результаты появятся после выполнения поиска."}
          </div>
        )}
      </div>
    </div>
  );

  if (!isOpen) return null;

  const content = (
    <div
      className={
        isPageMode
          ? "rounded-2xl border border-gray-200 bg-white shadow-sm"
          : "w-full max-w-5xl max-h-[90vh] overflow-y-auto rounded-2xl bg-white shadow-2xl"
      }
    >
      <div className="flex items-start justify-between border-b border-gray-200 px-6 py-4">
        <div>
          {isPageMode && onBack && !shouldHidePageNavigation && (
            <button
              type="button"
              onClick={onBack}
              className="mb-3 inline-flex items-center gap-2 rounded border px-3 py-2 text-sm"
            >
              <ArrowLeft size={16} />
              {t("previous") || "Назад"}
            </button>
          )}
          <h2 className="text-xl font-semibold text-gray-900">
            {resolvedTitle}
          </h2>
          {initial && !hideMetaInfo && (
            <p className="mt-1 text-sm text-gray-500">
              {t("createdAt") || "Создано"}:{" "}
              {new Date(initial.createdAt).toLocaleString()}
              {initial.updatedAt ? (
                <>
                  {" "}
                  • {t("updatedAt") || "Обновлено"}:{" "}
                  {new Date(initial.updatedAt).toLocaleString()}
                </>
              ) : null}
            </p>
          )}
        </div>
        {!shouldHidePageNavigation ? (
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg p-2 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
            aria-label={
              isPageMode ? t("previous") || "Назад" : t("cancel") || "Закрыть"
            }
          >
            <X size={20} />
          </button>
        ) : null}
      </div>

      {error && (
        <div className="mx-6 mt-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          {error}
        </div>
      )}

      {initial && !hideAuditSection ? (
        <div className="mx-6 mt-4">
          <ObjectAuditLauncher
            objectType="legal-entity"
            objectId={initial.id}
            createdAt={initial.createdAt}
            createdByUserName={initial.auditCreatedByUserName}
            updatedAt={initial.auditUpdatedAt}
            updatedByUserName={initial.auditUpdatedByUserName}
            isDeleted={initial.auditIsDeleted}
            deletedAt={initial.auditDeletedAt}
            deletedByUserName={initial.auditDeletedByUserName}
            triggerTitle="Аудит юрлица"
            triggerDescription="Паспорт юрлица и история изменений открываются в отдельном окне."
            title="История юрлица"
          />
        </div>
      ) : null}

      <form onSubmit={handleSubmit} className="px-6 pb-6 pt-4 space-y-6">
        <div
          className={`grid gap-6 ${shouldShowDadataSection ? "md:grid-cols-2" : ""}`}
        >
          <div className="space-y-4">
            <div>
              <label className="mb-2 block text-sm font-medium text-gray-700">
                {t("legalEntityShortName") || "Краткое название"}
                {isRequired("ShortName") ? "*" : ""}
              </label>
              <input
                type="text"
                name="shortName"
                value={formState.shortName}
                onChange={handleInputChange}
                required
                disabled={readOnly}
                className={requiredInputClass("shortName", formState.shortName)}
              />
            </div>
            <div>
              <label className="mb-2 block text-sm font-medium text-gray-700">
                {t("legalEntityFullName") || "Полное название"}
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
                  {t("legalEntityInn") || "ИНН"}
                  {isRequired("Inn") ? "*" : ""}
                </label>
                <input
                  type="text"
                  name="inn"
                  value={formState.inn}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className={requiredInputClass("inn", formState.inn)}
                />
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t("legalEntityKpp") || "КПП"}
                  {isRequired("Kpp") ? "*" : ""}
                </label>
                <input
                  type="text"
                  name="kpp"
                  value={formState.kpp}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className={requiredInputClass("kpp", formState.kpp)}
                />
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t("legalEntityOgrn") || "ОГРН"}
                  {isRequired("Ogrn") ? "*" : ""}
                </label>
                <input
                  type="text"
                  name="ogrn"
                  value={formState.ogrn}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className={requiredInputClass("ogrn", formState.ogrn)}
                />
              </div>
            </div>
            <div className="grid gap-4 md:grid-cols-2">
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t("legalEntityDirector") || "Руководитель"}
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
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  Ответственный
                </label>
                <SystemSelect
                  name="responsibleUserId"
                  value={formState.responsibleUserId}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value="">Не выбрано</option>
                  {responsibles.map((item) => (
                    <option key={item.id} value={item.id}>
                      {item.fullName}
                    </option>
                  ))}
                </SystemSelect>
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  Статус принадлежности
                </label>
                <SystemSelect
                  name="clientStatusId"
                  value={formState.clientStatusId}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value="">Без статуса</option>
                  {clientStatuses.map((status) => (
                    <option key={status.id} value={status.id}>
                      {status.name}
                    </option>
                  ))}
                </SystemSelect>
                {selectedLegalEntityClientStatus?.description ? (
                  <p className="mt-2 text-xs text-gray-500">
                    {selectedLegalEntityClientStatus.description}
                  </p>
                ) : null}
              </div>
              <label className="flex items-start gap-3 rounded-lg border border-emerald-100 bg-emerald-50/60 px-3 py-2 text-sm text-gray-700">
                <SystemCheckbox
                  type="checkbox"
                  name="isClientAndSupplier"
                  checked={formState.isClientAndSupplier}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className="mt-1 h-4 w-4 rounded border-gray-300 text-emerald-600 focus:ring-emerald-500 disabled:opacity-60"
                />
                <span>
                  <span className="block font-medium text-gray-800">
                    Является одновременно клиентом и поставщиком
                  </span>
                  <span className="mt-0.5 block text-xs text-gray-500">
                    Организация будет доступна в обоих списках выбора.
                  </span>
                </span>
              </label>
            </div>
          </div>
          {shouldShowDadataSection ? dadataSection : null}
        </div>
        <div className="rounded-2xl border border-slate-200 bg-white shadow-sm overflow-hidden">
          <div className="border-b border-slate-200 px-6 pt-4">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-700">
              Реквизиты компании
            </h3>
            <div className="mt-3 flex flex-wrap gap-5">
              {(
                [
                  { id: "base", label: "Основные" },
                  { id: "tax", label: "Налогообложение" },
                  { id: "codes", label: "Коды и фонды" },
                  { id: "treasury", label: "Реквизиты ЕНС" },
                  { id: "english", label: "Реквизиты на английском" },
                  { id: "addresses", label: "Адреса" },
                  { id: "contacts", label: "Контакты" },
                ] as Array<{ id: RequisitesTab; label: string }>
              ).map((tab) => (
                <button
                  key={tab.id}
                  type="button"
                  onClick={() => setRequisitesTab(tab.id)}
                  className={`pb-3 text-sm font-semibold transition ${
                    requisitesTab === tab.id
                      ? "border-b-2 border-blue-600 text-blue-600"
                      : "text-slate-500 hover:text-slate-700"
                  }`}
                >
                  {tab.label}
                </button>
              ))}
            </div>
          </div>
          <div className="p-6">
            {requisitesTab === "base" && (
              <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                {[
                  {
                    key: "country",
                    label: "Страна",
                    dict: "countries",
                    options: countries,
                  },
                  {
                    key: "baseCurrency",
                    label: "Базовая валюта",
                    dict: "base-currencies",
                    options: baseCurrencies,
                  },
                  {
                    key: "serviceLanguage",
                    label: "Язык сервиса",
                    dict: "service-languages",
                    options: serviceLanguages,
                  },
                  {
                    key: "organizationForm",
                    label: "Форма организации",
                    dict: "organization-forms",
                    options: organizationForms,
                  },
                  {
                    key: "timezone",
                    label: "Часовой пояс",
                    dict: "timezones",
                    options: timezones,
                  },
                ].map((item) => {
                  const value = (
                    formState as unknown as Record<string, string | boolean>
                  )[item.key] as string;
                  const hasCurrent =
                    !!value && item.options.some((opt) => opt.name === value);
                  return (
                    <div key={item.key}>
                      <div className="mb-2 flex items-center justify-between gap-2">
                        <label className="block text-sm font-medium text-gray-700">
                          {item.label}
                          {isRequired(requiredFieldKey(item.key)) ? "*" : ""}
                        </label>
                        {!hideDictionaryButtons ? (
                          <button
                            type="button"
                            onClick={() => {
                              setDictModalTab(item.dict as LegalEntityDictTab);
                              setDictModalOpen(true);
                            }}
                            className="text-xs font-medium text-blue-600 hover:text-blue-700"
                          >
                            Справочник
                          </button>
                        ) : null}
                      </div>
                      <SystemSelect
                        name={item.key}
                        value={value}
                        onChange={handleInputChange}
                        disabled={readOnly}
                        className={requiredInputClass(item.key, value)}
                      >
                        <option value="">Не выбрано</option>
                        {!hasCurrent && value ? (
                          <option value={value}>{value}</option>
                        ) : null}
                        {item.options.map((opt) => (
                          <option key={opt.id} value={opt.name}>
                            {opt.name}
                          </option>
                        ))}
                      </SystemSelect>
                    </div>
                  );
                })}
              </div>
            )}

            {requisitesTab === "tax" && (
              <div className="grid gap-4 md:grid-cols-3">
                <div>
                  <div className="mb-2 flex items-center justify-between gap-2">
                    <label className="block text-sm font-medium text-gray-700">
                      Система налогообложения
                    </label>
                    {!hideDictionaryButtons ? (
                      <button
                        type="button"
                        onClick={() => {
                          setDictModalTab("tax-systems");
                          setDictModalOpen(true);
                        }}
                        className="text-xs font-medium text-blue-600 hover:text-blue-700"
                      >
                        Справочник
                      </button>
                    ) : null}
                  </div>
                  <SystemSelect
                    name="taxSystem"
                    value={formState.taxSystem}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  >
                    <option value="">Не выбрано</option>
                    {!taxSystems.some((x) => x.name === formState.taxSystem) &&
                    formState.taxSystem ? (
                      <option value={formState.taxSystem}>
                        {formState.taxSystem}
                      </option>
                    ) : null}
                    {taxSystems.map((x) => (
                      <option key={x.id} value={x.name}>
                        {x.name}
                      </option>
                    ))}
                  </SystemSelect>
                </div>
                <div>
                  <div className="mb-2 flex items-center justify-between gap-2">
                    <label className="block text-sm font-medium text-gray-700">
                      Режим налогообложения
                    </label>
                    {!hideDictionaryButtons ? (
                      <button
                        type="button"
                        onClick={() => {
                          setDictModalTab("tax-system-modes");
                          setDictModalOpen(true);
                        }}
                        className="text-xs font-medium text-blue-600 hover:text-blue-700"
                      >
                        Справочник
                      </button>
                    ) : null}
                  </div>
                  <SystemSelect
                    name="taxSystemMode"
                    value={formState.taxSystemMode}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  >
                    <option value="">Не выбрано</option>
                    {!taxSystemModes.some(
                      (x) => x.name === formState.taxSystemMode,
                    ) && formState.taxSystemMode ? (
                      <option value={formState.taxSystemMode}>
                        {formState.taxSystemMode}
                      </option>
                    ) : null}
                    {taxSystemModes.map((x) => (
                      <option key={x.id} value={x.name}>
                        {x.name}
                      </option>
                    ))}
                  </SystemSelect>
                </div>
                {[
                  ["incomeTaxRate", "Ставка (доход)"],
                  ["incomeExpenseTaxRate", "Ставка (доход-расход)"],
                  ["charterCapital", "Уставный капитал"],
                  ["taxRegistrationDate", "Дата постановки на учет"],
                  ["taxOfficeCode", "Код налоговой"],
                  ["taxRegion", "Регион налоговой"],
                  ["ifnsName", "Наименование ИФНС"],
                  ["taxOfficePhone", "Телефон налоговой"],
                ].map(([name, label]) => (
                  <div key={name as string}>
                    <label className="mb-2 block text-sm font-medium text-gray-700">
                      {label}
                      {isRequired(requiredFieldKey(name as string)) ? "*" : ""}
                    </label>
                    <input
                      type="text"
                      name={name as string}
                      value={
                        (
                          formState as unknown as Record<
                            string,
                            string | boolean
                          >
                        )[name as string] as string
                      }
                      onChange={handleInputChange}
                      disabled={readOnly}
                      className={requiredInputClass(
                        name as string,
                        (
                          formState as unknown as Record<
                            string,
                            string | boolean
                          >
                        )[name as string],
                      )}
                    />
                  </div>
                ))}
                <label className="flex items-center gap-2 text-sm text-gray-700">
                  <SystemCheckbox
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
            )}

            {requisitesTab === "codes" && (
              <div className="grid gap-4 md:grid-cols-3">
                {[
                  ["okved", "ОКВЭД"],
                  ["okpo", "ОКПО"],
                  ["okato", "ОКАТО"],
                  ["oktmo", "ОКТМО"],
                  ["okogu", "ОКОГУ"],
                  ["okfs", "ОКФС"],
                  ["okopf", "ОКОПФ"],
                  ["pfr", "ПФР"],
                  ["fss", "ФСС"],
                ].map(([name, label]) => (
                  <div key={name as string}>
                    <label className="mb-2 block text-sm font-medium text-gray-700">
                      {label}
                      {isRequired(requiredFieldKey(name as string)) ? "*" : ""}
                    </label>
                    <input
                      type="text"
                      name={name as string}
                      value={
                        (
                          formState as unknown as Record<
                            string,
                            string | boolean
                          >
                        )[name as string] as string
                      }
                      onChange={handleInputChange}
                      disabled={readOnly}
                      className={requiredInputClass(
                        name as string,
                        (
                          formState as unknown as Record<
                            string,
                            string | boolean
                          >
                        )[name as string],
                      )}
                    />
                  </div>
                ))}
              </div>
            )}

            {requisitesTab === "treasury" && (
              <div className="grid gap-4 md:grid-cols-3">
                {[
                  ["treasuryInn", "ИНН получателя (ЕНС)"],
                  ["treasuryKpp", "КПП получателя (ЕНС)"],
                  ["treasuryRecipientName", "Получатель (ЕНС)"],
                  ["treasuryBankBik", "БИК банка (ЕНС)"],
                  ["treasuryBankName", "Банк (ЕНС)"],
                  ["treasuryAccountNumber", "Счет получателя (ЕНС)"],
                  ["treasuryCorrAccountNumber", "Кор. счет (ЕНС)"],
                  ["treasuryKbk", "КБК (ЕНС)"],
                ].map(([name, label]) => (
                  <div key={name as string}>
                    <label className="mb-2 block text-sm font-medium text-gray-700">
                      {label}
                    </label>
                    <input
                      type="text"
                      name={name as string}
                      value={
                        (
                          formState as unknown as Record<
                            string,
                            string | boolean
                          >
                        )[name as string] as string
                      }
                      onChange={handleInputChange}
                      disabled={readOnly}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                  </div>
                ))}
              </div>
            )}

            {requisitesTab === "addresses" && (
              <ClientAddressEntriesForm
                entries={addressEntries}
                canEdit={!readOnly}
                compactReadOnly={compactReadOnlyView}
                addressTypeOptions={[...ADDRESS_TYPE_OPTIONS]}
                suggestionsByIndex={addressAutofill.suggestionsByIndex}
                errorsByIndex={addressAutofill.errorsByIndex}
                onUpdateEntry={updateAddressFormEntry}
                onSetPrimary={setPrimaryAddressFormEntry}
                onRemoveEntry={(index) => {
                  invalidateCompanyDetails();
                  addressAutofill.invalidate(addressEntries[index]?.uiKey);
                  setAddressEntries((prev) =>
                    prev.length > 1
                      ? ensureSinglePrimaryAddressEntryList(
                          prev.filter((_, i) => i !== index),
                        )
                      : [createEmptyLegalEntityAddressEntry(true)],
                  );
                }}
                onAddEntry={() => {
                  invalidateCompanyDetails();
                  setAddressEntries((prev) =>
                    ensureSinglePrimaryAddressEntryList([
                      ...prev,
                      createEmptyLegalEntityAddressEntry(),
                    ]),
                  );
                }}
                onToggleAdditionalType={toggleAdditionalAddressType}
                onScheduleSuggestions={(index, query) => addressAutofill.schedule(addressEntries[index]?.uiKey, query)}
                onFindAddress={(index) => {
                  void addressAutofill.clean(addressEntries[index]?.uiKey);
                }}
                onApplySuggestion={(index, suggestion) => addressAutofill.apply(addressEntries[index]?.uiKey, suggestion)}
              />
            )}

            {requisitesTab === "contacts" && (
              <div className="grid gap-6 md:grid-cols-2">
                <div>
                  <div className="mb-2 text-sm font-medium text-gray-700">
                    Телефоны
                  </div>
                  {!compactReadOnlyView ? (
                    <button
                      type="button"
                      onClick={() => { invalidateCompanyDetails(); setContactPhones((prev) => [...prev, ""]); }}
                      disabled={readOnly}
                      className="mb-2 rounded-xl border border-blue-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50"
                    >
                      + Добавить телефон
                    </button>
                  ) : null}
                  <div className="space-y-2">
                    {contactPhones.map((v, idx) => (
                      <div key={`ph-${idx}`} className="flex gap-2">
                        <input
                          value={v}
                          disabled={readOnly}
                          onChange={(e) => {
                            invalidateCompanyDetails();
                            setContactPhones((prev) =>
                              prev.map((x, i) =>
                                i === idx ? e.target.value : x,
                              ),
                            );
                          }}
                          className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                        />
                        {!compactReadOnlyView ? (
                          <button
                            type="button"
                            disabled={readOnly}
                            onClick={() => {
                              invalidateCompanyDetails();
                              setContactPhones((prev) =>
                                prev.filter((_, i) => i !== idx),
                              );
                            }}
                            className="rounded-xl border border-rose-200 px-3 text-rose-600"
                          >
                            ✕
                          </button>
                        ) : null}
                      </div>
                    ))}
                  </div>
                </div>
                <div>
                  <div className="mb-2 text-sm font-medium text-gray-700">
                    Email
                  </div>
                  {!compactReadOnlyView ? (
                    <button
                      type="button"
                      onClick={() => { invalidateCompanyDetails(); setContactEmails((prev) => [...prev, ""]); }}
                      disabled={readOnly}
                      className="mb-2 rounded-xl border border-blue-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50"
                    >
                      + Добавить email
                    </button>
                  ) : null}
                  <div className="space-y-2">
                    {contactEmails.map((v, idx) => (
                      <div key={`em-${idx}`} className="flex gap-2">
                        <input
                          value={v}
                          disabled={readOnly}
                          onChange={(e) => {
                            invalidateCompanyDetails();
                            setContactEmails((prev) =>
                              prev.map((x, i) =>
                                i === idx ? e.target.value : x,
                              ),
                            );
                          }}
                          className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                        />
                        {!compactReadOnlyView ? (
                          <button
                            type="button"
                            disabled={readOnly}
                            onClick={() => {
                              invalidateCompanyDetails();
                              setContactEmails((prev) =>
                                prev.filter((_, i) => i !== idx),
                              );
                            }}
                            className="rounded-xl border border-rose-200 px-3 text-rose-600"
                          >
                            ✕
                          </button>
                        ) : null}
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            )}

            {requisitesTab === "english" && (
              <div className="grid gap-4 md:grid-cols-3">
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Название организации"
                  value={englishRequisites.organizationName}
                  onChange={(e) =>
                    updateEnglishRequisites("organizationName", e.target.value)
                  }
                  disabled={readOnly}
                />
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Полное наименование"
                  value={englishRequisites.fullName}
                  onChange={(e) =>
                    updateEnglishRequisites("fullName", e.target.value)
                  }
                  disabled={readOnly}
                />
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Форма"
                  value={englishRequisites.form}
                  onChange={(e) =>
                    updateEnglishRequisites("form", e.target.value)
                  }
                  disabled={readOnly}
                />
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm md:col-span-2"
                  placeholder="Юридический адрес"
                  value={englishRequisites.legalAddress}
                  onChange={(e) =>
                    updateEnglishRequisites("legalAddress", e.target.value)
                  }
                  disabled={readOnly}
                />
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Фактический адрес"
                  value={englishRequisites.actualAddress}
                  onChange={(e) =>
                    updateEnglishRequisites("actualAddress", e.target.value)
                  }
                  disabled={readOnly}
                />
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Должность"
                  value={englishRequisites.position}
                  onChange={(e) =>
                    updateEnglishRequisites("position", e.target.value)
                  }
                  disabled={readOnly}
                />
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Фамилия"
                  value={englishRequisites.lastName}
                  onChange={(e) =>
                    updateEnglishRequisites("lastName", e.target.value)
                  }
                  disabled={readOnly}
                />
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Имя"
                  value={englishRequisites.firstName}
                  onChange={(e) =>
                    updateEnglishRequisites("firstName", e.target.value)
                  }
                  disabled={readOnly}
                />
                <input
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Отчество"
                  value={englishRequisites.middleName}
                  onChange={(e) =>
                    updateEnglishRequisites("middleName", e.target.value)
                  }
                  disabled={readOnly}
                />
              </div>
            )}
          </div>
        </div>

        {!compactReadOnlyView ? legalEntityWorkSection : null}

        {canViewClientLinks ? clientsSection : null}

        {ownCompanyClassification === null ? <div role="status" className="rounded-xl border border-slate-200 p-3 text-sm text-slate-600">
          {legacyClassification?.error ? <><p>{legacyClassification.error}</p><p>Принадлежность компании не подтверждена. Введённые данные остаются в форме.</p>
            <button type="button" className="mt-1 text-blue-700 underline" onClick={() => setClassificationRetry(value => value + 1)}>Повторить проверку принадлежности</button></>
            : 'Проверяем принадлежность компании…'}
        </div> : null}

        <div className="mt-6">
          <LegalEntityFinanceSection
            readOnly={readOnly}
            compactReadOnly={compactReadOnlyView}
            bankAccounts={bankAccounts}
            cashRegisters={cashRegisters}
            cards={cards}
            onBankAccountsChange={setBankAccounts}
            onCashRegistersChange={setCashRegisters}
            onCardsChange={setCards}
          />
        </div>

        {isOwnLegalEntity && currentLegalEntityId !== null && canManageOwnCompanySettings ? (
          <>
            <div className="mt-4 flex flex-wrap items-center gap-3">
              <label className="text-sm font-medium text-slate-700" htmlFor="company-profile-status">
                Состояние профиля компании
              </label>
              <SystemSelect
                id="company-profile-status"
                value={formState.profileStatus}
                disabled={readOnly}
                onChange={(event) =>
                  setFormState((prev) => ({
                    ...prev,
                    profileStatus: event.target.value as CompanyProfileStatus,
                  }))
                }
                className="rounded-xl border border-slate-300 px-3 py-2 text-sm"
              >
                <option value="Draft">Черновик</option>
                <option value="Active">Активна</option>
                <option value="Archived">В архиве</option>
              </SystemSelect>
            </div>
            <CompanyTaxRegisterSection companyId={currentLegalEntityId} readOnly={readOnly} ownerScope={classificationScope} />
          </>
        ) : null}

        {ownCompanyClassification !== null && currentLegalEntityId !== null && canManageOwnCompanySettings ? (
          <CompanyBankConnectionsSection companyId={currentLegalEntityId} readOnly={readOnly} ownerScope={classificationScope}
            profileDirty={loading || bankBindingScope !== savedBankBindingScope} />
        ) : null}

        {isOwnLegalEntity && currentLegalEntityId !== null ? (
          <CompanyBranchesSection companyId={currentLegalEntityId} bankAccounts={bankAccounts}
            readOnly={readOnly || !canManageOwnCompanySettings || !canManageCompanyBranches}
            ownerScope={JSON.stringify([isOpen, mode, deleteOwnerScope, canManageOwnCompanySettings, canManageCompanyBranches, initial?.profileEditVersion])} />
        ) : null}

        {isOwnLegalEntity ? (
          <CompanyImagePrintDefaultsSection
            draft={printDefaultsDraft}
            disabled={readOnly || loading}
            onChange={value => setPrintDefaultsDraft(previous => ({ ...previous, value, touched: true }))}
          />
        ) : null}

        {isOwnLegalEntity && currentLegalEntityId !== null ? (
          <CompanyRequisitesCheck companyId={currentLegalEntityId} refreshKey={initial?.updatedAt} />
        ) : null}

        {isOwnLegalEntity ? (
        <div className="mt-6 grid gap-6 lg:grid-cols-3" aria-busy={imageProcessing}>
          {imageProcessing ? (
            <p role="status" className="flex items-center gap-2 text-sm text-slate-600 lg:col-span-3">
              <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
              Обрабатываем изображения. Дождитесь окончания перед сохранением.
            </p>
          ) : null}
          <LegalEntityImageField
            kind="logo"
            title="Логотип компании"
            alt="Логотип компании"
            value={logoDataUrl}
            persistedValue={persistedLogoDataUrl}
            changed={logoChanged}
            readOnly={readOnly || loading}
            compactDisplay={compactReadOnlyView}
            onUpload={async (file) => {
              await handleImageUpload(
                file,
                setLogoDataUrl,
                setLogoChanged,
                "logo",
              );
            }}
            onRemove={async () => {
              await requestImageRemoval(
                "logo",
                persistedLogoDataUrl.trim().length > 0,
                setLogoDataUrl,
                setLogoChanged,
              );
            }}
            onRestore={() =>
              restoreImageValue(
                "logo",
                persistedLogoDataUrl,
                setLogoDataUrl,
                setLogoChanged,
              )
            }
          />
          <LegalEntityImageField
            kind="seal"
            title="Печать компании"
            alt="Печать компании"
            value={sealDataUrl}
            persistedValue={persistedSealDataUrl}
            changed={sealChanged}
            readOnly={readOnly || loading}
            compactDisplay={compactReadOnlyView}
            onUpload={async (file) => {
              await handleImageUpload(
                file,
                setSealDataUrl,
                setSealChanged,
                "seal",
              );
            }}
            onRemove={async () => {
              await requestImageRemoval(
                "seal",
                persistedSealDataUrl.trim().length > 0,
                setSealDataUrl,
                setSealChanged,
              );
            }}
            onRestore={() =>
              restoreImageValue(
                "seal",
                persistedSealDataUrl,
                setSealDataUrl,
                setSealChanged,
              )
            }
          />
          <LegalEntityImageField
            kind="signature"
            title="Подпись руководителя"
            alt="Подпись руководителя"
            value={directorSignatureDataUrl}
            persistedValue={persistedDirectorSignatureDataUrl}
            changed={directorSignatureChanged}
            readOnly={readOnly || loading}
            compactDisplay={compactReadOnlyView}
            onUpload={async (file) => {
              await handleImageUpload(
                file,
                setDirectorSignatureDataUrl,
                setDirectorSignatureChanged,
                "signature",
              );
            }}
            onRemove={async () => {
              await requestImageRemoval(
                "signature",
                persistedDirectorSignatureDataUrl.trim().length > 0,
                setDirectorSignatureDataUrl,
                setDirectorSignatureChanged,
              );
            }}
            onRestore={() =>
              restoreImageValue(
                "signature",
                persistedDirectorSignatureDataUrl,
                setDirectorSignatureDataUrl,
                setDirectorSignatureChanged,
              )
            }
          />
          {showLegacyImageFields ? (
            <>
              <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="text-sm font-semibold text-slate-700">
                  Логотип компании
                </div>
                <div className="mt-4 rounded-xl border-2 border-dashed border-blue-400 px-4 py-6 text-center text-sm text-blue-600">
                  <input
                    type="file"
                    accept={getLegalEntityImageAccept('logo')}
                    className="mx-auto block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-blue-50 file:px-3 file:py-2 file:text-blue-700"
                    disabled={readOnly || loading}
                    onChange={(e) => {
                      void handleImageUpload(
                        e.target.files?.[0] ?? null,
                        setLogoDataUrl,
                        setLogoChanged,
                        "logo",
                      );
                    }}
                  />
                  {logoDataUrl ? (
                    <>
                      <img
                        src={logoDataUrl}
                        alt="Логотип"
                        className="mx-auto mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1"
                      />
                      {!readOnly && !loading ? (
                        <button
                          type="button"
                          onClick={() =>
                            clearImageValue("logo", setLogoDataUrl, setLogoChanged)
                          }
                          className="mt-3 rounded-lg border border-rose-200 px-3 py-2 text-xs font-medium text-rose-600 transition hover:bg-rose-50"
                        >
                          Убрать изображение
                        </button>
                      ) : null}
                    </>
                  ) : (
                    <>
                      <div className="mt-2 font-semibold">Выберите файлы</div>
                      <div className="text-xs text-slate-400">
                        или перетащите сюда.
                      </div>
                    </>
                  )}
                </div>
                <div className="mt-4 text-xs text-slate-400">
                  Рекомендуемый размер для загрузки: 545x185. Форматы: JPG,
                  JPEG, PNG.
                </div>
              </div>
              <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="text-sm font-semibold text-slate-700">
                  Печать компании
                </div>
                <div className="mt-4 rounded-xl border-2 border-dashed border-slate-200 px-4 py-6 text-center text-sm text-slate-400">
                  <input
                    type="file"
                    accept={getLegalEntityImageAccept('seal')}
                    className="mx-auto block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-slate-700"
                    disabled={readOnly || loading}
                    onChange={(e) => {
                      void handleImageUpload(
                        e.target.files?.[0] ?? null,
                        setSealDataUrl,
                        setSealChanged,
                        "seal",
                      );
                    }}
                  />
                  {sealDataUrl ? (
                    <>
                      <img
                        src={sealDataUrl}
                        alt="Печать"
                        className="mx-auto mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1"
                      />
                      {!readOnly && !loading ? (
                        <button
                          type="button"
                          onClick={() =>
                            clearImageValue("seal", setSealDataUrl, setSealChanged)
                          }
                          className="mt-3 rounded-lg border border-rose-200 px-3 py-2 text-xs font-medium text-rose-600 transition hover:bg-rose-50"
                        >
                          Убрать изображение
                        </button>
                      ) : null}
                    </>
                  ) : (
                    <>
                      <div className="mt-2 font-semibold">Выберите файлы</div>
                      <div className="text-xs text-slate-400">
                        или перетащите сюда.
                      </div>
                    </>
                  )}
                </div>
                <div className="mt-4 text-xs text-slate-400">
                  Рекомендуемый размер для загрузки: 200x200. Форматы: JPG,
                  JPEG, PNG.
                </div>
              </div>
              <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="text-sm font-semibold text-slate-700">
                  Подпись руководителя
                </div>
                <div className="mt-4 rounded-xl border-2 border-dashed border-slate-200 px-4 py-6 text-center text-sm text-slate-400">
                  <input
                    type="file"
                    accept={getLegalEntityImageAccept('signature')}
                    className="mx-auto block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-slate-700"
                    disabled={readOnly || loading}
                    onChange={(e) => {
                      void handleImageUpload(
                        e.target.files?.[0] ?? null,
                        setDirectorSignatureDataUrl,
                        setDirectorSignatureChanged,
                        "signature",
                      );
                    }}
                  />
                  {directorSignatureDataUrl ? (
                    <>
                      <img
                        src={directorSignatureDataUrl}
                        alt="Подпись"
                        className="mx-auto mt-3 h-16 w-auto rounded-xl border border-slate-200 bg-slate-50 p-1"
                      />
                      {!readOnly && !loading ? (
                        <button
                          type="button"
                          onClick={() =>
                            clearImageValue(
                              "signature",
                              setDirectorSignatureDataUrl,
                              setDirectorSignatureChanged,
                            )
                          }
                          className="mt-3 rounded-lg border border-rose-200 px-3 py-2 text-xs font-medium text-rose-600 transition hover:bg-rose-50"
                        >
                          Убрать изображение
                        </button>
                      ) : null}
                    </>
                  ) : (
                    <>
                      <div className="mt-2 font-semibold">Выберите файлы</div>
                      <div className="text-xs text-slate-400">
                        или перетащите сюда.
                      </div>
                    </>
                  )}
                </div>
                <div className="mt-4 text-xs text-slate-400">
                  Рекомендуемый размер для загрузки: 165x50. Форматы: JPG, JPEG,
                  PNG.
                </div>
              </div>
            </>
          ) : null}
        </div>
        ) : null}

        <div className="mt-6 grid gap-6 lg:grid-cols-2">
          <div className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
            <h3 className="text-2xl font-semibold text-slate-900">
              Руководитель (для документов)
            </h3>
            <div className="mt-4 grid gap-4">
              <label className="block text-sm text-slate-600">
                <span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">
                  Должность
                </span>
                <input
                  className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Генеральный директор"
                  value={documentDirector.position}
                  onChange={(e) =>
                    updatePersonInfo(
                      setDocumentDirector,
                      "position",
                      e.target.value,
                    )
                  }
                  disabled={readOnly}
                />
              </label>
              <label className="block text-sm text-slate-600">
                <span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">
                  Фамилия
                </span>
                <input
                  className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Швыдченко"
                  value={documentDirector.lastName}
                  onChange={(e) =>
                    updatePersonInfo(
                      setDocumentDirector,
                      "lastName",
                      e.target.value,
                    )
                  }
                  disabled={readOnly}
                />
              </label>
              <label className="block text-sm text-slate-600">
                <span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">
                  Имя
                </span>
                <input
                  className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Сергей"
                  value={documentDirector.firstName}
                  onChange={(e) =>
                    updatePersonInfo(
                      setDocumentDirector,
                      "firstName",
                      e.target.value,
                    )
                  }
                  disabled={readOnly}
                />
              </label>
              <label className="block text-sm text-slate-600">
                <span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">
                  Отчество
                </span>
                <input
                  className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Николаевич"
                  value={documentDirector.middleName}
                  onChange={(e) =>
                    updatePersonInfo(
                      setDocumentDirector,
                      "middleName",
                      e.target.value,
                    )
                  }
                  disabled={readOnly}
                />
              </label>
            </div>
          </div>
          <div className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
            <h3 className="text-2xl font-semibold text-slate-900">
              Главный бухгалтер (для документов)
            </h3>
            <div className="mt-4 grid gap-4">
              <label className="block text-sm text-slate-600">
                <span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">
                  Фамилия
                </span>
                <input
                  className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Швыдченко"
                  value={chiefAccountant.lastName}
                  onChange={(e) =>
                    updatePersonInfo(
                      setChiefAccountant,
                      "lastName",
                      e.target.value,
                    )
                  }
                  disabled={readOnly}
                />
              </label>
              <label className="block text-sm text-slate-600">
                <span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">
                  Имя
                </span>
                <input
                  className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Сергей"
                  value={chiefAccountant.firstName}
                  onChange={(e) =>
                    updatePersonInfo(
                      setChiefAccountant,
                      "firstName",
                      e.target.value,
                    )
                  }
                  disabled={readOnly}
                />
              </label>
              <label className="block text-sm text-slate-600">
                <span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">
                  Отчество
                </span>
                <input
                  className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  placeholder="Николаевич"
                  value={chiefAccountant.middleName}
                  onChange={(e) =>
                    updatePersonInfo(
                      setChiefAccountant,
                      "middleName",
                      e.target.value,
                    )
                  }
                  disabled={readOnly}
                />
              </label>
            </div>
          </div>
        </div>

        {shouldShowFormActions ? (
          <div className="flex flex-col gap-3 border-t border-gray-200 pt-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="text-xs text-gray-500">
              {t("legalEntityFormHint") ||
                "Поля, помеченные * обязательны для заполнения."}
            </div>
            <div className="flex flex-wrap items-center gap-3">
              {allowDelete && initial && onDelete && (
                <button
                  type="button"
                  onClick={handleDelete}
                  disabled={deleting || loading || imageProcessing}
                  className="inline-flex items-center gap-2 rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-600 hover:bg-red-100"
                >
                  {deleting && <Loader2 className="h-4 w-4 animate-spin" />}
                  {t("delete") || "Удалить"}
                </button>
              )}
              <div className="flex items-center gap-3">
                <button
                  type="button"
                  onClick={onClose}
                  className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-600 hover:border-gray-400 hover:text-gray-700"
                >
                  {isPageMode
                    ? t("previous") || "Назад"
                    : t("cancel") || "Отмена"}
                </button>
                <button
                  type="submit"
                  disabled={loading || deleting || readOnly || imageProcessing || ownCompanyClassification === null}
                  className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300"
                >
                  {loading && <Loader2 className="h-4 w-4 animate-spin" />}
                  {initial ? t("save") || "Сохранить" : t("add") || "Добавить"}
                </button>
              </div>
            </div>
          </div>
        ) : null}
      </form>

      {caseModalOpen && canMutateRelated('cases', caseMode === 'edit' ? 'canEdit' : 'canCreate') ? (
        <CaseModal
          key={relatedCaseOwner.scope}
          isOpen={caseModalOpen}
          onClose={closeCaseModal}
          caseData={
            caseMode === "edit" && editingCase
              ? editingCase
              : blankLegalEntityCase
          }
          onSave={saveLegalEntityCase}
          onNextHearingConfirmed={(confirmation) => {
            if (!relatedCaseOwner.capture() || !canMutateRelated('cases', 'canEdit') || !editingCase) return;
            setEditingCase((current) => current ? { ...current, ...confirmation } : current);
            setLegalEntityCases((current) => current.map((item) => item.id === editingCase?.id ? { ...item, ...confirmation } : item));
          }}
          onDelete={caseMode === "edit" && canMutateRelated('cases', 'canDelete') ? removeLegalEntityCase : undefined}
          responsibles={responsibles}
          canViewFinance={canViewPayments}
        />
      ) : null}

      {invoiceModalOpen && canMutateRelated('accounts', invoiceModalMode === 'edit' ? 'canEdit' : 'canCreate') ? (
        <InvoiceModal
          key={relatedInvoiceOwner.scope}
          mode={invoiceModalMode}
          open={invoiceModalOpen}
          invoice={selectedInvoice}
          onClose={closeInvoiceModal}
          onSubmit={submitInvoice}
          submitting={relatedInvoiceOwner.busy}
          errorMessage={invoiceModalError?.owner.isCurrent() ? invoiceModalError.message : null}
          clients={documentClients}
          legalEntities={legalEntities}
          legalEntitiesLoading={legalEntitiesLoading}
          legalEntitiesError={legalEntitiesError ?? undefined}
          responsibles={responsibles}
          lookupsLoading={documentsLookupsLoading}
          lookupsError={documentsLookupsError || undefined}
          defaultLegalEntityId={currentLegalEntityId ?? undefined}
          defaultClientCaseId={selectedCaseId}
          defaultResponsibleId={
            formState.responsibleUserId
              ? Number(formState.responsibleUserId)
              : undefined
          }
          defaultType="Income"
          onClientCaseCreated={handleInvoiceCaseCreated}
        />
      ) : null}

      {actModalOpen && canMutateRelated('acts', actModalMode === 'edit' ? 'canEdit' : 'canCreate') ? (
        <ActModal
          key={relatedActOwner.scope}
          open={actModalOpen}
          mode={actModalMode}
          act={selectedAct}
          onClose={closeActModal}
          onSubmit={submitAct}
          submitting={relatedActOwner.busy}
          errorMessage={actModalError?.owner.isCurrent() ? actModalError.message : null}
          clients={documentClients}
          responsibles={responsibles}
          lookupsLoading={documentsLookupsLoading}
          lookupsError={documentsLookupsError || undefined}
          defaultResponsibleId={
            formState.responsibleUserId
              ? Number(formState.responsibleUserId)
              : undefined
          }
          initialValues={initialRelatedActValues}
        />
      ) : null}

      {paymentModalOpen && canMutateRelated('payments', paymentModalMode === 'edit' ? 'canEdit' : 'canCreate') ? (
        <PaymentModal
          key={relatedPaymentOwner.scope}
          isOpen={paymentModalOpen}
          onClose={closePaymentModal}
          payment={selectedPayment ?? undefined}
          onSubmit={submitPayment}
          canCreatePayment={canMutateRelated('payments', 'canCreate')}
          canEditPayment={canMutateRelated('payments', 'canEdit')}
          onTransferRemainder={transferPaymentRemainder}
          onRollbackRemainderTransfer={rollbackPaymentRemainderTransfer}
          onAdjustRemainderTransfer={adjustPaymentRemainderTransfer}
          canViewLinkedInvoices={canViewAccounts}
          canCreateLinkedInvoices={canMutateRelated('accounts', 'canCreate')}
          defaultLegalEntityId={currentLegalEntityId ?? undefined}
          defaultClientCaseId={selectedCaseId}
          casesPrefetch={canViewCases ? legalEntityCases : []}
        />
      ) : null}

      <DictionariesModal
        open={dictModalOpen}
        onClose={() => setDictModalOpen(false)}
        initialTab={dictModalTab}
        onlyTab={dictModalTab}
        onPickItem={(kind, item) => {
          const map: Record<string, keyof FormState> = {
            countries: "country",
            "base-currencies": "baseCurrency",
            "service-languages": "serviceLanguage",
            "organization-forms": "organizationForm",
            timezones: "timezone",
            "tax-systems": "taxSystem",
            "tax-system-modes": "taxSystemMode",
          };
          const target = map[kind];
          if (target) {
            setFormState((prev) => ({ ...prev, [target]: item.name }));
          }
          setDictModalOpen(false);
        }}
      />

      {clientPickerActive && (
        <div className="fixed inset-0 z-[1100] flex items-center justify-center bg-black/50 p-4">
          <div className="w-full max-w-3xl rounded-xl bg-white shadow-2xl">
            <div className="flex items-center justify-between border-b px-4 py-3">
              <h3 className="text-lg font-semibold">Выбор клиента</h3>
              <button
                className="rounded border px-2 py-1 text-sm"
                onClick={() => setPickerOpen(false)}
              >
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
                  placeholder={
                    t("searchClientsPlaceholder") || "Поиск клиентов"
                  }
                />
              </div>

              {pickerError ? (
                <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                  {pickerError}
                </div>
              ) : null}

              <div className="max-h-80 overflow-auto rounded border">
                <table className="min-w-full table-fixed divide-y text-sm">
                  <colgroup>
                    <col style={{ width: "44%" }} />
                    <col style={{ width: "36%" }} />
                    <col style={{ width: "20%" }} />
                  </colgroup>
                  <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                    <tr>
                      <th className="px-3 py-2 text-left">ФИО</th>
                      <th className="px-3 py-2 text-left">Компания</th>
                      <th className="px-3 py-2 text-right">Действие</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {pickerLoading ? (
                      <tr>
                        <td
                          colSpan={3}
                          className="px-3 py-8 text-center text-slate-500"
                        >
                          Загрузка...
                        </td>
                      </tr>
                    ) : (
                      availableClients.map((client) => (
                        <tr key={client.id}>
                          <td className="px-3 py-2">{client.name}</td>
                          <td className="px-3 py-2">{client.company || "—"}</td>
                          <td className="px-3 py-2 text-right">
                            <button
                              type="button"
                              className="rounded border border-emerald-200 bg-emerald-50 px-2 py-1 text-xs text-emerald-700"
                              onClick={() => handleAddClient(client.id)}
                            >
                              Добавить
                            </button>
                          </td>
                        </tr>
                      ))
                    )}
                    {!pickerLoading && availableClients.length === 0 && (
                      <tr>
                        <td
                          colSpan={3}
                          className="px-3 py-6 text-center text-slate-500"
                        >
                          Нет доступных клиентов
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
              <TablePagination
                page={pickerPage}
                totalPages={pickerTotalPages}
                totalItems={pickerTotal}
                pageSize={pickerPageSize}
                pageSizeOptions={CLIENT_PAGE_SIZE_OPTIONS}
                onPageChange={setPickerPage}
                onPageSizeChange={setPickerPageSize}
              />
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
