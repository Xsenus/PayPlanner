const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5080/api';

function getAuthToken(): string | null {
  return (
    localStorage.getItem('auth_token') ||
    localStorage.getItem('pp.jwt') ||
    sessionStorage.getItem('auth_token') ||
    null
  );
}

export type DictKind =
  | 'deal-types'
  | 'income-types'
  | 'payment-sources'
  | 'services'
  | 'client-statuses'
  | 'payment-statuses'
  | 'countries'
  | 'base-currencies'
  | 'service-languages'
  | 'organization-forms'
  | 'timezones'
  | 'tax-systems'
  | 'tax-system-modes'
  | 'invoice-units'
  | 'invoice-item-names';

export type DictItemByKind<K extends DictKind> = K extends 'deal-types'
  ? DealType
  : K extends 'income-types'
  ? IncomeType
  : K extends 'payment-sources'
  ? PaymentSource
  : K extends 'services'
  ? ServiceDictionaryItem
  : K extends 'client-statuses'
  ? ClientStatus
  : K extends 'countries'
  ? BaseDictItem
  : K extends 'base-currencies'
  ? BaseDictItem
  : K extends 'service-languages'
  ? BaseDictItem
  : K extends 'organization-forms'
  ? BaseDictItem
  : K extends 'timezones'
  ? BaseDictItem
  : K extends 'tax-systems'
  ? BaseDictItem
  : K extends 'tax-system-modes'
  ? BaseDictItem
  : K extends 'invoice-units'
  ? BaseDictItem
  : K extends 'invoice-item-names'
  ? BaseDictItem
  : PaymentStatusEntity;

function pathForKind(kind: DictKind): string {
  return `/dictionaries/${kind}`;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface UserSettingDto {
  key: string;
  value: string;
  updatedAt: string;
}

export interface DatabaseStatusDto {
  provider: string;
  displayName: string;
  supportsSqliteMaintenance: boolean;
  supportsSqliteFileImport: boolean;
}

export interface DatabaseBackupFileDto {
  fileName: string;
  relativePath: string;
  sizeBytes: number;
  createdAt: string;
  trigger: string;
}

export interface DatabaseBackupFormatOptionDto {
  key: string;
  label: string;
  description: string;
  fileExtension: string;
  isDefault: boolean;
}

export interface DatabaseBackupOverviewDto {
  provider: string;
  providerDisplayName: string;
  backupFormat: string;
  fileExtension: string;
  selectedFormat: string;
  availableFormats: DatabaseBackupFormatOptionDto[];
  automaticEnabled: boolean;
  scheduleLocal: string;
  retentionDays: number;
  rootDirectory: string;
  timeZone: string;
  toolCommand?: string | null;
  items: DatabaseBackupFileDto[];
}

export interface DatabaseBackupCreateResultDto {
  backup: DatabaseBackupFileDto;
  downloadPath: string;
}

export interface DatabaseBackupDeleteResultDto {
  deletedCount: number;
  deletedAll: boolean;
  overview: DatabaseBackupOverviewDto;
}

export interface DatabaseImportTableResultDto {
  table: string;
  displayName: string;
  sourceRows: number;
  inserted: number;
  updated: number;
  skipped: number;
  conflicts: number;
  errors: number;
  completed: boolean;
  message?: string | null;
}

export interface DatabaseImportResultDto {
  sourceProvider: string;
  targetProvider: string;
  targetProviderDisplayName: string;
  startedAt: string;
  finishedAt: string;
  success: boolean;
  sourceTablesProcessed: number;
  tablesWithErrors: number;
  tables: DatabaseImportTableResultDto[];
}

export interface ObjectAuditEventDto {
  id: number;
  objectType: string;
  objectId: number;
  action: string;
  description?: string | null;
  actorUserId?: number | null;
  actorUserName?: string | null;
  metadataJson?: string | null;
  createdAt: string;
}

export interface ObjectAuditFeedQuery {
  search?: string;
  objectType?: string;
  action?: string;
  actor?: string;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export interface DeletedObjectAuditItemDto {
  objectType: string;
  objectId: number;
  displayName: string;
  subtitle?: string | null;
  createdAt?: string | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserName?: string | null;
  deletedAt?: string | null;
  deletedByUserName?: string | null;
}

export interface SqliteStorageStatsDto {
  databaseFileSizeBytes: number;
  walFileSizeBytes: number;
  shmFileSizeBytes: number;
  totalSizeBytes: number;
  estimatedReclaimableBytes: number;
}

export interface UserActivityMaintenanceDto {
  isEnabled: boolean;
  retentionDays?: number | null;
  updatedAt?: string | null;
  logEntries: number;
  oldestLogAt?: string | null;
  newestLogAt?: string | null;
  storage: SqliteStorageStatsDto;
}

export interface UserActivityCleanupResultDto {
  deletedLogs: number;
  maintenance: UserActivityMaintenanceDto;
}

export interface UserActivityCompactionResultDto {
  before: SqliteStorageStatsDto;
  after: SqliteStorageStatsDto;
  maintenance: UserActivityMaintenanceDto;
}

export interface InvoiceNumberingSettingsDto {
  startNumber: number;
  allocationStrategy: 'max-plus-one' | 'first-free';
}

export interface InvoiceRulesSettingsDto {
  maxClientCasesPerInvoice: number;
  maxActsPerInvoice: number;
}

export interface NextInvoiceNumberDto extends InvoiceNumberingSettingsDto {
  year: number;
  nextNumber: string;
}

export interface ContractNumberingSettingsDto {
  startNumber: number;
  allocationStrategy: 'max-plus-one' | 'first-free';
  template: string;
}

export interface ContractRulesSettingsDto {
  autoGenerateWhenEmpty: boolean;
  allowManualNumberEdit: boolean;
}

export interface NextContractNumberDto extends ContractNumberingSettingsDto {
  number: string;
  autoGenerateWhenEmpty: boolean;
  allowManualNumberEdit: boolean;
}

export interface FormFieldRequirementDto {
  section: string;
  fieldKey: string;
  isRequired: boolean;
  updatedAt: string;
}

export interface FormRequirementFieldMetadataDto {
  key: string;
  label: string;
  category?: string;
}

export interface FormRequirementSectionMetadataDto {
  key: string;
  title: string;
  fields: FormRequirementFieldMetadataDto[];
}

export type ContactSourceType = 'client' | 'external';
export type ContactScope = 'all' | 'clients' | 'external';

export interface ContactDirectoryItemDto {
  sourceType: ContactSourceType;
  category: ContactScope;
  sourceId: number;
  fullName: string;
  organizationName?: string | null;
  legalEntityId?: number | null;
  position?: string | null;
  powerOfAttorney?: string | null;
  notes?: string | null;
  phone?: string | null;
  email?: string | null;
  phones: string[];
  emails: string[];
  isActive: boolean;
  syncToPhonebook: boolean;
  responsibleUserId?: number | null;
  responsibleUserName?: string | null;
  createdAt?: string | null;
  updatedAt?: string | null;
  auditCreatedByUserId?: number | null;
  auditCreatedByUserName?: string | null;
  auditUpdatedAt?: string | null;
  auditUpdatedByUserId?: number | null;
  auditUpdatedByUserName?: string | null;
  auditIsDeleted?: boolean;
  auditDeletedAt?: string | null;
  auditDeletedByUserId?: number | null;
  auditDeletedByUserName?: string | null;
}

export interface UpdateClientContactRequestDto {
  fullName: string;
  phone?: string | null;
  email?: string | null;
  phones: string[];
  emails: string[];
  position?: string | null;
  powerOfAttorney?: string | null;
  notes?: string | null;
  legalEntityId?: number | null;
  syncToPhonebook: boolean;
}

export interface UpsertExternalContactRequestDto extends UpdateClientContactRequestDto {
  responsibleUserId?: number | null;
  isActive: boolean;
}

export interface YealinkContactImportDetailDto {
  rowId: number;
  name: string;
  group?: string | null;
  phones: string[];
  action: string;
  matchedSourceType?: ContactSourceType | string | null;
  matchedSourceId?: number | null;
  matchedName?: string | null;
  matchedPhones: string[];
  message?: string | null;
}

export interface YealinkContactImportResultDto {
  dryRun: boolean;
  scanned: number;
  withPhones: number;
  created: number;
  updated: number;
  skippedExisting: number;
  skippedNoPhone: number;
  skippedDuplicateInFile: number;
  skippedByUser: number;
  skippedInvalid: number;
  details: YealinkContactImportDetailDto[];
}

export type YealinkContactImportDecisionAction = 'skip' | 'createExternal' | 'createClient' | 'updateExisting';

export interface YealinkContactImportDecisionDto {
  rowId: number;
  action: YealinkContactImportDecisionAction;
}

export interface LegacyAddressMigrationDetail {
  ownerType?: 'legal' | 'client' | string;
  ownerId?: number;
  ownerName?: string;
  legalEntityId?: number | null;
  legalEntityName?: string | null;
  clientId?: number | null;
  clientName?: string | null;
  legacyAddress?: string | null;
  normalizedAddress?: string | null;
  action: 'Added' | 'Updated' | 'DuplicateSkipped' | 'SkippedEmpty' | 'Failed' | string;
  addressRecordId?: number | null;
  dadataMatched?: boolean | null;
  message?: string | null;
}

export interface LegacyAddressMigrationResult {
  scanned: number;
  scannedLegalEntities?: number;
  scannedClients?: number;
  added: number;
  updated: number;
  duplicateSkipped: number;
  emptySkipped: number;
  failed: number;
  dadataMatched: number;
  dadataNotMatched: number;
  validateWithDadata: boolean;
  details: LegacyAddressMigrationDetail[];
}

export interface InvoiceLegacyMigrationSummaryDto {
  scannedPayments: number;
  invoicePayments: number;
  convertedToInvoiceKind: number;
  alreadyLinked: number;
  missingNumberSkipped: number;
  missingClientSkipped: number;
  linkedToExistingInvoice: number;
  createdInvoices: number;
  linkedPayments: number;
  relinkedPayments: number;
  distinctLegacyInvoiceKeys: number;
  distinctLegacyGroups: number;
  groupsWithCase: number;
  groupsWithoutCase: number;
  existingDuplicateInvoiceKeys: number;
  sharedInvoiceEntities: number;
  splitSharedInvoiceGroups: number;
  representativePaymentsUpdated: number;
}

export interface InvoiceCounterpartyCleanupResultDto {
  cleared: number;
  removedRequirements: number;
}

export interface SimpleDictionaryDuplicateItem {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
}

export interface SimpleDictionaryDuplicateGroup {
  canonical: string;
  items: SimpleDictionaryDuplicateItem[];
}

export interface MergeSimpleDictionaryDuplicatesPayload {
  sourceIds: number[];
  preferredId?: number;
  name?: string;
  description?: string;
  colorHex?: string;
  isActive?: boolean;
}

export interface SimpleDictionaryMergeResult {
  kind: string;
  createdId: number;
  name: string;
  replacedInLegalEntities: number;
  removedIds: number[];
}

type SortDir = 'asc' | 'desc';
type ActsSortKey =
  | 'date'
  | 'number'
  | 'amount'
  | 'invoiceNumber'
  | 'counterpartyInn'
  | 'status'
  | 'client'
  | 'responsible'
  | 'createdAt';

type InvoicesSortKey =
  | 'date'
  | 'number'
  | 'amount'
  | 'dueDate'
  | 'status'
  | 'client'
  | 'responsible'
  | 'createdAt';

type ContractsSortKey = 'date' | 'number' | 'amount' | 'createdAt';

function buildQuery(params: Record<string, string | number | boolean | undefined | null>) {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    q.append(k, String(v));
  }
  const s = q.toString();
  return s ? `?${s}` : '';
}

function parseContentDispositionFileName(header: string | null): string | null {
  if (!header) {
    return null;
  }

  const utfMatch = header.match(/filename\*\s*=\s*UTF-8''([^;]+)/i);
  if (utfMatch?.[1]) {
    try {
      return decodeURIComponent(utfMatch[1].trim());
    } catch {
      return utfMatch[1].trim();
    }
  }

  const plainMatch = header.match(/filename\s*=\s*"?([^";]+)"?/i);
  return plainMatch?.[1]?.trim() || null;
}

export class ApiService {
  private async request<T>(endpoint: string, options?: RequestInit): Promise<T> {
    const url = `${API_BASE_URL}${endpoint}`;
    const token = getAuthToken();
    const isFormData = typeof FormData !== 'undefined' && options?.body instanceof FormData;
    const headers: Record<string, string> = {
      ...(!isFormData && options?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(options?.headers as Record<string, string> | undefined),
    };
    if (token) headers['Authorization'] = `Bearer ${token}`;

    const response = await fetch(url, {
      ...options,
      headers,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${url} ${text ? `- ${text}` : ''}`,
      );
    }

    if (response.status === 204) {
      return undefined as T;
    }

    const contentType = response.headers.get('content-type') || '';
    if (!contentType.includes('application/json')) {
      const text = await response.text();
      return text as unknown as T;
    }

    return (await response.json()) as T;
  }

  // ===== Acts =====
  async getActs(params?: {
    from?: string;
    to?: string;
    status?: ActStatus;
    clientId?: number;
    clientIds?: string;
    responsibleId?: number;
    search?: string;
    sortBy?: ActsSortKey;
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<Act>>(`/acts${q}`);
  }

  async getActsSummary(params?: {
    from?: string;
    to?: string;
    status?: ActStatus;
    clientId?: number;
    responsibleId?: number;
    search?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<ActsSummary>(`/acts/summary${q}`);
  }

  async getActResponsibles() {
    return this.request<ActResponsible[]>('/acts/responsibles');
  }

  async createAct(payload: ActInput) {
    return this.request<Act>('/acts', { method: 'POST', body: JSON.stringify(payload) });
  }

  async updateAct(id: number, payload: ActInput) {
    return this.request<Act>(`/acts/${id}`, { method: 'PUT', body: JSON.stringify(payload) });
  }

  async deleteAct(id: number) {
    return this.request<void>(`/acts/${id}`, { method: 'DELETE' });
  }

  // ===== Contracts =====
  async getContracts(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    search?: string;
    sortBy?: ContractsSortKey;
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<Contract>>(`/contracts${q}`);
  }

  async getContract(id: number) {
    return this.request<Contract>(`/contracts/${id}`);
  }

  async getNextContractNumber(date: string, excludeId?: number) {
    const q = buildQuery({ date, excludeId });
    return this.request<NextContractNumberDto>(`/contracts/next-number${q}`);
  }

  async getContractNumberingSettings() {
    return this.request<ContractNumberingSettingsDto>('/contracts/numbering-settings');
  }

  async updateContractNumberingSettings(payload: ContractNumberingSettingsDto) {
    return this.request<ContractNumberingSettingsDto>('/contracts/numbering-settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getContractRulesSettings() {
    return this.request<ContractRulesSettingsDto>('/contracts/rules-settings');
  }

  async updateContractRulesSettings(payload: ContractRulesSettingsDto) {
    return this.request<ContractRulesSettingsDto>('/contracts/rules-settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async createContract(payload: ContractInput) {
    return this.request<Contract>('/contracts', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateContract(id: number, payload: ContractInput) {
    return this.request<Contract>(`/contracts/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteContract(id: number) {
    return this.request<void>(`/contracts/${id}`, { method: 'DELETE' });
  }

  // ===== User settings =====
  async getUserSetting(key: string) {
    return this.request<UserSettingDto>(`/user-settings/${encodeURIComponent(key)}`);
  }

  async upsertUserSetting(key: string, value: string) {
    return this.request<UserSettingDto>(`/user-settings/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ value }),
    });
  }

  async getDatabaseStatus() {
    return this.request<DatabaseStatusDto>('/database/status');
  }

  async getDatabaseBackupOverview() {
    return this.request<DatabaseBackupOverviewDto>('/database/backups');
  }

  async updateDatabaseBackupSettings(payload: {
    automaticEnabled: boolean;
    scheduleLocal: string;
    retentionDays: number;
    backupFormat?: string;
  }) {
    return this.request<DatabaseBackupOverviewDto>('/database/backups/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async createDatabaseBackup() {
    return this.request<DatabaseBackupCreateResultDto>('/database/backups', {
      method: 'POST',
    });
  }

  async deleteDatabaseBackup(relativePath: string) {
    return this.request<DatabaseBackupDeleteResultDto>(`/database/backups/file?relativePath=${encodeURIComponent(relativePath)}`, {
      method: 'DELETE',
    });
  }

  async deleteAllDatabaseBackups() {
    return this.request<DatabaseBackupDeleteResultDto>('/database/backups/all', {
      method: 'DELETE',
    });
  }

  async downloadDatabaseBackup(relativePath: string) {
    const token = getAuthToken();
    const response = await fetch(
      `${API_BASE_URL}/database/backups/download?relativePath=${encodeURIComponent(relativePath)}`,
      {
        method: 'GET',
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      },
    );

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(`HTTP ${response.status} ${response.statusText}${text ? ` - ${text}` : ''}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')),
    };
  }

  async importSqliteDatabase(file: File) {
    const payload = new FormData();
    payload.append('file', file);
    return this.request<DatabaseImportResultDto>('/database/import/sqlite', {
      method: 'POST',
      body: payload,
    });
  }

  async getFormRequirements(section?: string) {
    const q = buildQuery({ section });
    return this.request<FormFieldRequirementDto[]>(`/form-requirements${q}`);
  }

  async getFormRequirementSections() {
    return this.request<FormRequirementSectionMetadataDto[]>('/form-requirements/sections');
  }

  async upsertFormRequirement(payload: { section: string; fieldKey: string; isRequired: boolean }) {
    return this.request<FormFieldRequirementDto>('/form-requirements', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getObjectAuditHistory(objectType: string, objectId: number) {
    return this.request<ObjectAuditEventDto[]>(
      `/object-audit/${encodeURIComponent(objectType.trim().toLowerCase())}/${objectId}`,
    );
  }

  async getObjectAuditEvents(params?: ObjectAuditFeedQuery) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 25,
    });
    return this.request<PagedResult<ObjectAuditEventDto>>(`/object-audit${q}`);
  }

  async getDeletedObjectAuditItems(objectType: string, search?: string) {
    const query = buildQuery({ search: search?.trim() || undefined });
    return this.request<DeletedObjectAuditItemDto[]>(
      `/object-audit/deleted/${encodeURIComponent(objectType.trim().toLowerCase())}${query}`,
    );
  }

  async restoreDeletedObject(objectType: string, objectId: number) {
    return this.request<void>(
      `/object-audit/${encodeURIComponent(objectType.trim().toLowerCase())}/${objectId}/restore`,
      {
        method: 'POST',
      },
    );
  }

  // ===== Contacts =====
  async getContacts(params?: {
    scope?: ContactScope;
    search?: string;
    legalEntityId?: number;
    includeInactive?: boolean;
    syncOnly?: boolean;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<ContactDirectoryItemDto[]>(`/contacts${q}`);
  }

  async updateClientContact(id: number, payload: UpdateClientContactRequestDto) {
    return this.request<ContactDirectoryItemDto>(`/contacts/clients/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async createExternalContact(payload: UpsertExternalContactRequestDto) {
    return this.request<ContactDirectoryItemDto>('/contacts/external', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateExternalContact(id: number, payload: UpsertExternalContactRequestDto) {
    return this.request<ContactDirectoryItemDto>(`/contacts/external/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteExternalContact(id: number) {
    return this.request<void>(`/contacts/external/${id}`, { method: 'DELETE' });
  }

  async downloadYealinkContactsXml() {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/contacts/exports/yealink`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(`HTTP ${response.status} ${response.statusText}${text ? ` - ${text}` : ''}`);
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'RemotePhonebook.xml',
    };
  }

  async importYealinkContactsXml(
    file: File,
    options: boolean | { dryRun?: boolean; decisions?: YealinkContactImportDecisionDto[] } = false,
  ) {
    const payload = new FormData();
    payload.append('file', file);

    const dryRun = typeof options === 'boolean' ? options : options.dryRun ?? false;
    const decisions = typeof options === 'boolean' ? undefined : options.decisions;
    if (decisions?.length) {
      payload.append('decisionsJson', JSON.stringify(decisions));
    }

    const query = dryRun ? '?dryRun=true' : '';

    return this.request<YealinkContactImportResultDto>(`/contacts/imports/yealink${query}`, {
      method: 'POST',
      body: payload,
    });
  }

  // ===== Invoices =====
  async getInvoices(params?: {
    from?: string;
    to?: string;
    status?: PaymentStatus;
    clientId?: number;
    clientIds?: string;
    caseId?: number;
    responsibleId?: number;
    search?: string;
    type?: 'Income' | 'Expense';
    sortBy?: InvoicesSortKey;
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<Invoice>>(`/invoices${q}`);
  }

  async getInvoice(id: number) {
    return this.request<Invoice>(`/invoices/${id}`);
  }

  async getInvoicesSummary(params?: {
    from?: string;
    to?: string;
    status?: PaymentStatus;
    clientId?: number;
    caseId?: number;
    responsibleId?: number;
    search?: string;
    type?: 'Income' | 'Expense';
  }) {
    const q = buildQuery(params ?? {});
    return this.request<InvoiceSummary>(`/invoices/summary${q}`);
  }

  async getInvoiceNumberingSettings() {
    return this.request<InvoiceNumberingSettingsDto>('/invoices/numbering-settings');
  }

  async updateInvoiceNumberingSettings(payload: InvoiceNumberingSettingsDto) {
    return this.request<InvoiceNumberingSettingsDto>('/invoices/numbering-settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getInvoiceRulesSettings() {
    return this.request<InvoiceRulesSettingsDto>('/invoices/rules-settings');
  }

  async updateInvoiceRulesSettings(payload: InvoiceRulesSettingsDto) {
    return this.request<InvoiceRulesSettingsDto>('/invoices/rules-settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getNextInvoiceNumber(date?: string) {
    const q = buildQuery({ date });
    return this.request<NextInvoiceNumberDto>(`/invoices/next-number${q}`);
  }

  async migrateLegacyInvoiceLinks() {
    return this.request<InvoiceLegacyMigrationSummaryDto>('/invoices/migrate-legacy-links', {
      method: 'POST',
    });
  }

  async cleanupInvoiceCounterpartyNames() {
    return this.request<InvoiceCounterpartyCleanupResultDto>('/invoices/cleanup-counterparty-names', {
      method: 'POST',
    });
  }

  async createInvoice(payload: InvoiceInput) {
    return this.request<Invoice>('/invoices', { method: 'POST', body: JSON.stringify(payload) });
  }

  async updateInvoice(id: number, payload: InvoiceInput) {
    return this.request<Invoice>(`/invoices/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteInvoice(id: number) {
    return this.request<void>(`/invoices/${id}`, { method: 'DELETE' });
  }

  async deleteInvoices(ids: number[]) {
    const normalized = Array.from(new Set(ids.filter((id) => Number.isFinite(id) && id > 0)));
    if (normalized.length === 1) {
      await this.deleteInvoice(normalized[0]);
      return { deleted: 1 };
    }

    return this.request<{ deleted: number }>('/invoices/bulk-delete', {
      method: 'POST',
      body: JSON.stringify({ ids: normalized }),
    });
  }

  async changeInvoicesResponsible(ids: number[], responsibleUserId: number) {
    return this.request<{ updated: number; responsibleUserId: number }>('/invoices/change-responsible', {
      method: 'POST',
      body: JSON.stringify({ ids, responsibleUserId }),
    });
  }

  async getAccountsRegistry(params?: {
    from?: string;
    to?: string;
    status?: 'created' | 'paid' | 'partial' | 'overdue' | 'cancelled';
    search?: string;
    type?: 'Income' | 'Expense';
    sortBy?: 'date' | 'number' | 'amount' | 'counterparty' | 'dueDate' | 'responsible';
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<AccountRegistryRow> & { summary: AccountsSummary }>(`/accounts/registry${q}`);
  }

  async downloadInvoiceDocument(id: number, format: 'word' | 'pdf' = 'word') {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/invoices/${id}/document?format=${format}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(`HTTP ${response.status} ${response.statusText}${text ? ` - ${text}` : ''}`);
    }

    return response.blob();
  }

  async sendInvoiceByEmail(
    id: number,
    payload?: { email?: string; subject?: string; message?: string; format?: 'word' | 'pdf' },
  ) {
    return this.request<{ sent: boolean; recipient: string; fileName: string; format: string }>(
      `/invoices/${id}/send-email`,
      {
        method: 'POST',
        body: JSON.stringify(payload ?? {}),
      },
    );
  }

  // ===== Role permissions =====
  async getRolePermissions(roleId: number) {
    return this.request<RolePermissions>(`/role-permissions/${roleId}`);
  }

  async updateRolePermissions(roleId: number, permissions: RolePermissions) {
    return this.request<void>(`/role-permissions/${roleId}`, {
      method: 'PUT',
      body: JSON.stringify(permissions),
    });
  }

  async resetRolePermissions(roleId: number) {
    return this.request<void>(`/role-permissions/${roleId}`, { method: 'DELETE' });
  }

  // ===== Payments (back-compat + расширенные варианты) =====

  // Перегрузки — чтобы не ломать старые вызовы
  async getPayments(): Promise<Payment[]>;
  async getPayments(from?: string, to?: string, clientId?: number): Promise<Payment[]>;
  async getPayments(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    caseId?: number;
    excludeInstallmentParents?: boolean;
  }): Promise<Payment[]>;
  async getPayments(
    a?: string | { from?: string; to?: string; clientId?: number; caseId?: number; excludeInstallmentParents?: boolean },
    b?: string,
    c?: number,
  ): Promise<Payment[]> {
    let q = '';
    if (typeof a === 'object') {
      q = buildQuery({
        from: a?.from,
        to: a?.to,
        clientId: a?.clientId,
        caseId: a?.caseId,
        excludeInstallmentParents: a?.excludeInstallmentParents,
      });
    } else {
      // старый сигнатурный стиль: (from?, to?, clientId?)
      q = buildQuery({ from: a, to: b, clientId: c });
    }
    return this.request<Payment[]>(`/payments${q}`);
  }

  async getPayment(id: number) {
    return this.request<Payment>(`/payments/${id}`);
  }

  async createPayment(payment: PaymentPayload) {
    return this.request<Payment>('/payments', { method: 'POST', body: JSON.stringify(payment) });
  }

  async updatePayment(id: number, payment: PaymentPayload) {
    return this.request<Payment>(`/payments/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payment),
    });
  }

  async deletePayment(id: number) {
    return this.request<void>(`/payments/${id}`, { method: 'DELETE' });
  }

  // V1: фильтры/сорт без пагинации
  async getPaymentsV1(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    caseId?: number;
    search?: string;
    sortBy?: 'date' | 'amount' | 'createdAt';
    sortDir?: SortDir;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<Payment[]>(`/v1/payments${q}`);
  }

  // V2: пагинация
  async getPaymentsV2(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    caseId?: number;
    clientIds?: string;
    categoryTokens?: string;
    type?: PaymentKind;
    status?: PaymentStatus;
    minAmount?: number;
    maxAmount?: number;
    search?: string;
    sortBy?: 'date' | 'amount' | 'createdAt';
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PagedResult<Payment> & { summary: PaymentsListSummary }>(`/v2/payments${q}`);
  }

  // ===== Clients =====
  async getClients() {
    return this.request<Client[]>('/clients');
  }

  async getClient(id: number) {
    return this.request<Client>(`/clients/${id}`);
  }

  async getClientStats(id: number): Promise<ClientStats>;
  async getClientStats(id: number, params?: { caseId?: number }): Promise<ClientStats>;
  async getClientStats(id: number, params?: { caseId?: number }) {
    const q = buildQuery({ caseId: params?.caseId });
    return this.request<ClientStats>(`/clients/${id}/stats${q}`);
  }

  async getClientCaseFinanceSummaries(id: number) {
    return this.request<ClientCaseFinanceSummary[]>(`/clients/${id}/case-finance-summaries`);
  }

  async createClient(client: ClientInput) {
    return this.request<Client>('/clients', { method: 'POST', body: JSON.stringify(client) });
  }

  async updateClient(id: number, client: ClientInput) {
    return this.request<Client>(`/clients/${id}`, { method: 'PUT', body: JSON.stringify(client) });
  }

  async deleteClient(id: number) {
    return this.request<void>(`/clients/${id}`, { method: 'DELETE' });
  }

  async deleteClients(ids: number[]) {
    const normalized = Array.from(new Set(ids.filter((id) => Number.isFinite(id) && id > 0)));
    if (normalized.length === 1) {
      await this.deleteClient(normalized[0]);
      return { deleted: 1 };
    }

    return this.request<{ deleted: number }>('/clients/bulk-delete', {
      method: 'POST',
      body: JSON.stringify({ ids: normalized }),
    });
  }

  async changeClientsResponsible(ids: number[], responsibleUserId: number) {
    return this.request<{ updated: number; responsibleUserId: number }>('/clients/change-responsible', {
      method: 'POST',
      body: JSON.stringify({ ids, responsibleUserId }),
    });
  }

  async getClientPassport(clientId: number) {
    return this.request<ClientPassport | null>(`/clients/${clientId}/passport`, { method: 'GET' });
  }

  async upsertClientPassport(clientId: number, payload: ClientPassportInput) {
    return this.request<ClientPassport>(`/clients/${clientId}/passport`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getClientPowersOfAttorney(clientId: number) {
    return this.request<PowerOfAttorney[]>(`/clients/${clientId}/powers-of-attorney`);
  }

  async createClientPowerOfAttorney(clientId: number, payload: PowerOfAttorneyInput) {
    return this.request<PowerOfAttorney>(`/clients/${clientId}/powers-of-attorney`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateClientPowerOfAttorney(clientId: number, id: number, payload: PowerOfAttorneyInput) {
    return this.request<PowerOfAttorney>(`/clients/${clientId}/powers-of-attorney/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteClientPowerOfAttorney(clientId: number, id: number) {
    return this.request<void>(`/clients/${clientId}/powers-of-attorney/${id}`, { method: 'DELETE' });
  }

  async getOkinDictionary() {
    return this.request<ClientReferenceItem[]>('/client-reference-data/okin');
  }

  async getOksoDictionary() {
    return this.request<ClientReferenceItem[]>('/client-reference-data/okso');
  }

  async getOkpdtrDictionary() {
    return this.request<ClientReferenceItem[]>('/client-reference-data/okpdtr');
  }

  // ===== Legal entities =====
  async getLegalEntities(params?: { search?: string }) {
    const q = buildQuery({ search: params?.search });
    return this.request<LegalEntitySummary[]>(`/legal-entities${q}`);
  }

  async getLegalEntitiesPaged(params?: { search?: string; page?: number; pageSize?: number }) {
    const q = buildQuery({
      search: params?.search,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<LegalEntitySummary>>(`/legal-entities/paged${q}`);
  }

  async getLegalEntity(id: number) {
    return this.request<LegalEntityDetail>(`/legal-entities/${id}`);
  }

  async getCompanyProfileLegalEntity() {
    return this.request<LegalEntityDetail>('/legal-entities/company-profile');
  }

  async setCompanyProfileLegalEntity(id: number) {
    return this.request<LegalEntityDetail>(`/legal-entities/company-profile/main/${id}`, {
      method: 'PUT',
    });
  }

  async createLegalEntity(payload: LegalEntityInput) {
    return this.request<LegalEntityDetail>('/legal-entities', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateLegalEntity(id: number, payload: LegalEntityInput) {
    return this.request<LegalEntityDetail>(`/legal-entities/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteLegalEntity(id: number) {
    return this.request<void>(`/legal-entities/${id}`, { method: 'DELETE' });
  }

  async suggestLegalEntities(payload: {
    query?: string;
    inn?: string;
    limit?: number;
  }) {
    return this.request<LegalEntitySuggestion[]>(`/legal-entities/suggest`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async findLegalEntityById(payload: { query: string; kpp?: string }) {
    return this.request<LegalEntityDadataDetails>(`/legal-entities/find-by-id`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }


  async migrateLegacyLegalEntityAddresses(validateWithDadata = true) {
    return this.request<LegacyAddressMigrationResult>('/legal-entities/migrate-legacy-addresses', {
      method: 'POST',
      body: JSON.stringify({ validateWithDadata }),
    });
  }

  async getSimpleDictionaryDuplicates(kind: 'countries' | 'base-currencies' | 'service-languages' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes' | 'invoice-units' | 'invoice-item-names') {
    return this.request<SimpleDictionaryDuplicateGroup[]>(`/dictionaries/simple/${kind}/duplicates`);
  }

  async mergeSimpleDictionaryDuplicates(
    kind: 'countries' | 'base-currencies' | 'service-languages' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes' | 'invoice-units' | 'invoice-item-names',
    payload: MergeSimpleDictionaryDuplicatesPayload,
  ) {
    return this.request<SimpleDictionaryMergeResult>(`/dictionaries/simple/${kind}/merge`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }


  async cleanAddress(address: string) {
    const endpoints = [
      '/clients/dadata/clean-address',
      '/legal-entities/clean/address',
      '/legalentities/clean/address',
    ] as const;

    let lastError: Error | null = null;
    for (const endpoint of endpoints) {
      try {
        return await this.request<CleanedAddress>(endpoint, {
          method: 'POST',
          body: JSON.stringify({ address }),
        });
      } catch (error) {
        if (!(error instanceof Error)) throw error;
        lastError = error;
        if (!error.message.includes('404')) {
          throw error;
        }
      }
    }

    throw lastError ?? new Error('Не удалось найти endpoint стандартизации адреса');
  }


  async suggestClientAddresses(query: string, limit = 7) {
    return this.request<DadataAddressSuggestion[]>(`/clients/dadata/suggest-address`, {
      method: 'POST',
      body: JSON.stringify({ query, limit }),
    });
  }

  // Доп: V1/V2 клиенты (на будущее, не ломает текущее)
  async getClientsV1(params?: {
    search?: string;
    isActive?: boolean;
    sortBy?: 'name' | 'createdAt';
    sortDir?: SortDir;
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<Client[]>(`/v1/clients${q}`);
  }

  async getClientsV2(params?: {
    search?: string;
    isActive?: boolean;
    sortBy?: 'name' | 'createdAt';
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
    [key: string]: string | number | boolean | undefined;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PagedResult<Client>>(`/v2/clients${q}`);
  }

  // ===== Cases (совмещение старого и нового вызова) =====

  // Перегрузки совместимости
  async getCases(): Promise<ClientCase[]>;
  async getCases(clientId?: number): Promise<ClientCase[]>;
  async getCases(params?: { clientId?: number }): Promise<ClientCase[]>;
  async getCases(a?: number | { clientId?: number }) {
    let q = '';
    if (typeof a === 'object') {
      q = buildQuery({ clientId: a?.clientId });
    } else {
      q = buildQuery({ clientId: a });
    }
    return this.request<ClientCase[]>(`/cases${q}`);
  }

  async getCase(id: number) {
    return this.request<ClientCase>(`/cases/${id}`);
  }

  async createCase(model: Omit<ClientCase, 'id' | 'createdAt' | 'payments'>) {
    return this.request<ClientCase>('/cases', { method: 'POST', body: JSON.stringify(model) });
  }

  async updateCase(id: number, model: Omit<ClientCase, 'id' | 'createdAt' | 'payments'>) {
    return this.request<ClientCase>(`/cases/${id}`, { method: 'PUT', body: JSON.stringify(model) });
  }

  async deleteCase(id: number) {
    return this.request<void>(`/cases/${id}`, { method: 'DELETE' });
  }

  // V1/V2 для дел (опционально)
  async getCasesV1(params?: {
    clientId?: number;
    status?: string; // если у тебя есть строгий тип, замени тут string на него
    search?: string;
    sortBy?: 'createdAt' | 'title' | 'status';
    sortDir?: SortDir;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<ClientCase[]>(`/v1/cases${q}`);
  }

  async getCasesV2(params?: {
    clientId?: number;
    status?: string;
    search?: string;
    sortBy?: 'createdAt' | 'title' | 'status';
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PagedResult<ClientCase>>(`/v2/cases${q}`);
  }

  // ===== Dictionaries =====
  // async getDealTypes() {
  //   return this.request<DealType[]>('/dictionaries/deal-types');
  // }

  // async getIncomeTypes() {
  //   return this.request<IncomeType[]>('/dictionaries/income-types');
  // }

  // async getPaymentSources() {
  //   return this.request<PaymentSource[]>('/dictionaries/payment-sources');
  // }

  // async getPaymentStatuses() {
  //   return this.request<PaymentStatusEntity[]>('/dictionaries/payment-statuses');
  // }

  async getDealTypes() {
    return this.getDict('deal-types');
  }
  async getIncomeTypes(): Promise<IncomeType[]>;
  async getIncomeTypes(
    paymentType?: 'Income' | 'Expense',
    isActive?: boolean,
  ): Promise<IncomeType[]>;
  async getIncomeTypes(
    paymentType?: 'Income' | 'Expense',
    isActive?: boolean,
  ): Promise<IncomeType[]> {
    const q = buildQuery({ paymentType, isActive });
    return this.request<IncomeType[]>(`${pathForKind('income-types')}${q}`);
  }

  async getPaymentSources() {
    return this.getDict('payment-sources');
  }
  async getServices() {
    return this.getDict('services');
  }
  async getClientStatuses() {
    return this.getDict('client-statuses');
  }
  async getPaymentStatuses() {
    return this.getDict('payment-statuses');
  }

  // ===== Statistics =====
  async getMonthlyStats(year: number, month: number) {
    return this.request<MonthlyStats>(`/stats/month?year=${year}&month=${month}`);
  }

  // Расширенная статистика по диапазону месяцев (если на бэке включено /v2/stats/months)
  async getMonthlyStatsRange(params: {
    startYear: number;
    startMonth: number;
    endYear: number;
    endMonth: number;
  }) {
    const q = buildQuery(params);
    return this.request<{
      start: { year: number; month: number };
      end: { year: number; month: number };
      items: Array<{
        year: number;
        month: number;
        period: string;
        income: number;
        expense: number;
        profit: number;
        completionRate: number;
        counts: { completed: number; pending: number; overdue: number; total: number };
      }>;
    }>(`/v2/stats/months${q}`);
  }

  // ===== Installments =====
  async calculateInstallment(request: InstallmentRequest) {
    return this.request('/installments/calc', { method: 'POST', body: JSON.stringify(request) });
  }

  // Специализированные CRUD для IncomeType (с полем paymentType)
  async createIncomeType(data: Omit<IncomeType, 'id' | 'createdAt'>): Promise<IncomeType> {
    return this.request<IncomeType>(pathForKind('income-types'), {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  async updateIncomeType(
    id: number,
    data: Omit<IncomeType, 'id' | 'createdAt'>,
  ): Promise<IncomeType> {
    return this.request<IncomeType>(`${pathForKind('income-types')}/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  // ===== Dictionaries (универсальный CRUD) =====
  async getDict<K extends DictKind>(kind: K): Promise<DictItemByKind<K>[]> {
    return this.request<DictItemByKind<K>[]>(pathForKind(kind));
  }

  async createDict<K extends DictKind>(
    kind: K,
    data: Omit<BaseDictItem, 'id'>, // можно передавать и description/colorHex
  ): Promise<DictItemByKind<K>> {
    return this.request<DictItemByKind<K>>(pathForKind(kind), {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  async updateDict<K extends DictKind>(
    kind: K,
    id: number,
    data: Partial<Omit<BaseDictItem, 'id'>>,
  ): Promise<DictItemByKind<K>> {
    return this.request<DictItemByKind<K>>(`${pathForKind(kind)}/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  async deleteDict(kind: DictKind, id: number): Promise<void> {
    await this.request<void>(`${pathForKind(kind)}/${id}`, { method: 'DELETE' });
  }

  // ===== Accounts (подсказки для поля "Счёт") =====
  async getAccounts(params?: {
    clientId?: number;
    caseId?: number;
    q?: string;
    take?: number;
    withDate?: false;
    dedupe?: boolean;
  }): Promise<string[]>;

  async getAccounts(params: {
    clientId?: number;
    caseId?: number;
    q?: string;
    take?: number;
    withDate: true;
    dedupe?: boolean;
  }): Promise<AccountSuggestion[]>;

  async getAccounts(params?: {
    clientId?: number;
    caseId?: number;
    q?: string;
    take?: number;
    withDate?: boolean;
    dedupe?: boolean;
  }): Promise<string[] | AccountSuggestion[]> {
    const q = buildQuery({
      clientId: params?.clientId,
      caseId: params?.caseId,
      q: params?.q?.trim(),
      take: params?.take,
      withDate: params?.withDate,
      dedupe: params?.dedupe,
    });

    if (params?.withDate) {
      return this.request<AccountSuggestion[]>(`/accounts${q}`);
    }
    return this.request<string[]>(`/accounts${q}`);
  }

  async getAccountSuggestions(
    q?: string,
    opts?: {
      clientId?: number;
      caseId?: number;
      take?: number;
      withDate?: false;
      dedupe?: boolean;
    },
  ): Promise<string[]>;

  async getAccountSuggestions(
    q?: string,
    opts?: { clientId?: number; caseId?: number; take?: number; withDate: true; dedupe?: boolean },
  ): Promise<AccountSuggestion[]>;

  async getAccountSuggestions(
    q?: string,
    opts?: {
      clientId?: number;
      caseId?: number;
      take?: number;
      withDate?: boolean;
      dedupe?: boolean;
    },
  ): Promise<string[] | AccountSuggestion[]> {
    const base = {
      q,
      clientId: opts?.clientId,
      caseId: opts?.caseId,
      take: opts?.take ?? 10,
      dedupe: opts?.dedupe ?? true,
    };

    if (opts?.withDate === false) {
      return this.getAccounts({ ...base, withDate: false });
    }
    return this.getAccounts({ ...base, withDate: true });
  }

  /**
   * Сводная статистика по доходам/расходам с серверными расчётами.
   * Поддерживает фильтры: status и q (поиск).
   * Параметр r — cache-buster (передаём reloadToken).
   */
  async getSummaryStats(params?: {
    clientId?: number;
    caseId?: number;
    from?: string;
    to?: string;
    period?: PeriodKey;
    type?: 'Income' | 'Expense';
    status?: SummaryStatus;
    q?: string;
    r?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<SummaryStats>(`/v2/stats/summary${q}`);
  }

  /** Упрощённый вызов: сводка по клиенту. */
  async getClientSummaryStats(
    clientId: number,
    opts?: {
      from?: string;
      to?: string;
      period?: PeriodKey;
      type?: 'Income' | 'Expense';
      status?: SummaryStatus;
      q?: string;
    },
  ) {
    const q = buildQuery({ clientId, ...opts });
    return this.request<SummaryStats>(`/v2/stats/summary${q}`);
  }

  /** Упрощённый вызов: сводка по делу. */
  async getCaseSummaryStats(
    caseId: number,
    opts?: {
      from?: string;
      to?: string;
      period?: PeriodKey;
      type?: 'Income' | 'Expense';
      status?: SummaryStatus;
      q?: string;
    },
  ) {
    const q = buildQuery({ caseId, ...opts });
    return this.request<SummaryStats>(`/v2/stats/summary${q}`);
  }

  // ===== Money operations =====
  async getMoneyOperations(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    direction?: MoneyOperationDirection;
    accountingType?: MoneyOperationAccountingType;
    search?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<MoneyOperation[]>(`/money-operations${q}`);
  }

  async getMoneyOperationsPaged(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    direction?: MoneyOperationDirection;
    accountingType?: MoneyOperationAccountingType;
    search?: string;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PagedResult<MoneyOperation> & { summary: MoneyOperationsSummary }>(`/money-operations/paged${q}`);
  }

  async createMoneyOperation(payload: MoneyOperation) {
    return this.request<MoneyOperation>('/money-operations', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateMoneyOperation(id: number, payload: MoneyOperation) {
    return this.request<MoneyOperation>(`/money-operations/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteMoneyOperation(id: number) {
    return this.request<void>(`/money-operations/${id}`, { method: 'DELETE' });
  }

  async getUserActivityLogs(params?: {
    page?: number;
    pageSize?: number;
    from?: string;
    to?: string;
    userId?: number;
    category?: string;
    action?: string;
    section?: string;
    httpMethod?: string;
    status?: UserActivityStatus;
    search?: string;
  }) {
    const q = buildQuery({
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
      from: params?.from,
      to: params?.to,
      userId: params?.userId,
      category: params?.category,
      action: params?.action,
      section: params?.section,
      httpMethod: params?.httpMethod,
      status: params?.status,
      search: params?.search,
    });
    return this.request<UserActivityLogResponse>(`/user-activity${q}`);
  }

  async getUserActivityFilters() {
    return this.request<UserActivityFiltersResponse>('/user-activity/filters');
  }

  async getUserActivityMaintenance() {
    return this.request<UserActivityMaintenanceDto>('/user-activity/admin/maintenance');
  }

  async updateUserActivitySettings(payload: { isEnabled: boolean; retentionDays?: number | null }) {
    return this.request<UserActivityMaintenanceDto>('/user-activity/admin/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async logUserActivity(payload: CreateUserActivityInput) {
    return this.request<UserActivityLogItem>('/user-activity', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async deleteUserActivityLogs() {
    return this.request<UserActivityCleanupResultDto>('/user-activity/admin/cleanup', {
      method: 'POST',
    });
  }

  async compactUserActivityDatabase() {
    return this.request<UserActivityCompactionResultDto>('/user-activity/admin/compact', {
      method: 'POST',
    });
  }
}

export const apiService = new ApiService();

// ---- types import ----
import type {
  AccountRegistryRow,
  AccountsSummary,
  Act,
  ActInput,
  ActResponsible,
  ActStatus,
  ActsSummary,
  Contract,
  ContractInput,
  Payment,
  PaymentPayload,
  Invoice,
  InvoiceInput,
  InvoiceSummary,
  PaymentStatus,
  Client,
  ClientInput,
  ClientCaseFinanceSummary,
  LegalEntityDetail,
  LegalEntityInput,
  LegalEntityDadataDetails,
  LegalEntitySuggestion,
  LegalEntitySummary,
  InstallmentRequest,
  ClientStats,
  DealType,
  IncomeType,
  PaymentSource,
  ServiceDictionaryItem,
  PaymentStatusEntity,
  ClientStatus,
  MonthlyStats,
  ClientCase,
  AccountSuggestion,
  SummaryStats,
  PeriodKey,
  SummaryStatus,
  MoneyOperation,
  MoneyOperationAccountingType,
  MoneyOperationDirection,
  MoneyOperationSource,
  MoneyOperationsSummary,
  PaymentsListSummary,
  CleanedAddress,
  DadataAddressSuggestion,
  ClientReferenceItem,
  ClientPassport,
  ClientPassportInput,
  PowerOfAttorney,
  PowerOfAttorneyInput,
} from '../types';
import { BaseDictItem } from '../types/dictionaries';
import type { RolePermissions } from '../types/permissions';
import type { CreateUserActivityInput, UserActivityFiltersResponse, UserActivityLogItem, UserActivityLogResponse, UserActivityStatus } from '../types/userActivity';
