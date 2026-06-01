import { API_BASE_URL } from './apiBaseUrl';

export const AUTH_SESSION_EXPIRED_EVENT = 'payplanner:auth-session-expired';
const AUTH_SESSION_EXPIRED_MESSAGE = 'Сессия истекла. Войдите снова.';
const AUTH_TOKEN_STORAGE_KEYS = ['pp_token', 'auth_token', 'pp.jwt'];

export function getApiBaseUrl(): string {
  return API_BASE_URL;
}

export function getAuthToken(): string | null {
  return (
    AUTH_TOKEN_STORAGE_KEYS.map((key) => localStorage.getItem(key)).find(Boolean) ||
    sessionStorage.getItem('auth_token') ||
    null
  );
}

function notifyAuthSessionExpired() {
  if (typeof window === 'undefined') {
    return;
  }

  window.dispatchEvent(new CustomEvent(AUTH_SESSION_EXPIRED_EVENT));
}

export type DictKind =
  | 'deal-types'
  | 'income-types'
  | 'payment-sources'
  | 'departments'
  | 'services'
  | 'service-categories'
  | 'client-statuses'
  | 'payment-statuses'
  | 'document-statuses'
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
  : K extends 'departments'
  ? BaseDictItem
  : K extends 'services'
  ? ServiceDictionaryItem
  : K extends 'service-categories'
  ? ServiceCategoryDictionaryItem
  : K extends 'client-statuses'
  ? ClientStatus
  : K extends 'document-statuses'
  ? DocumentStatusEntity
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

export interface AppFeatureFlagsDto {
  legal: {
    externalCourtSync: boolean;
    externalCalendarSync: boolean;
    courtEvents: boolean;
    documentTemplateLibrary: boolean;
  };
}

export type AppReleaseItemType = 'new' | 'improved' | 'fixed' | 'important';

export interface AppReleaseItemDto {
  type: AppReleaseItemType | string;
  text: string;
}

export interface AppReleaseDto {
  id: number;
  releaseId: string;
  version: string;
  releasedAt: string;
  title: string;
  summary: string;
  items: AppReleaseItemDto[];
}

export interface AppReleaseAdminDto extends AppReleaseDto {
  isActive: boolean;
  source: string;
  createdAt: string;
  updatedAt: string;
}

export interface SaveAppReleaseRequest {
  releaseId: string;
  version: string;
  releasedAt: string;
  title: string;
  summary: string;
  isActive: boolean;
  items: AppReleaseItemDto[];
}

export interface AppVersionLatestDto {
  currentVersion: string;
  latestRelease?: AppReleaseDto | null;
  seenByCurrentUser: boolean;
}

export interface LegalIntegrationDiagnosticsDto {
  generatedAt: string;
  hasEnabledExternalLegalFlags: boolean;
  items: LegalIntegrationDiagnosticItemDto[];
}

export interface LegalIntegrationDiagnosticItemDto {
  key: string;
  label: string;
  featureFlagPath: string;
  enabled: boolean;
  status: string;
  safeToRun: boolean;
  message: string;
}

export interface CalendarSyncProviderDto {
  key: string;
  label: string;
  kind: string;
  status: string;
  isAvailable: boolean;
  featureFlagEnabled: boolean;
  requiresOAuth: boolean;
  supportsManualExport: boolean;
  supportsBackgroundSync: boolean;
  supportsTwoWaySync: boolean;
  supportedTargets: string[];
  message: string;
}

export interface CalendarSyncReadinessDto {
  generatedAt: string;
  externalCalendarSyncEnabled: boolean;
  readyToConnect: boolean;
  items: CalendarSyncReadinessItemDto[];
}

export interface CalendarSyncReadinessItemDto {
  key: string;
  label: string;
  status: string;
  isReady: boolean;
  isRequired: boolean;
  severity: string;
  message: string;
}

export interface CalendarSyncSettingsDto {
  syncMode: string;
  conflictResolutionMode: string;
  requireManualConfirmation: boolean;
  dryRunConfirmed: boolean;
  notes: string;
  updatedAt?: string | null;
}

export interface UpdateCalendarSyncSettingsRequest {
  syncMode?: string;
  conflictResolutionMode?: string;
  requireManualConfirmation?: boolean;
  dryRunConfirmed?: boolean;
  notes?: string;
}

export interface YandexDiskSettingsDto {
  isConfigured: boolean;
  rootPath: string;
  tokenPreview?: string | null;
  updatedAt?: string | null;
}

export interface UpdateYandexDiskSettingsRequest {
  oAuthToken?: string;
  clearToken?: boolean;
  rootPath?: string;
}

export interface YandexDiskResourceDto {
  name: string;
  path: string;
  fullPath: string;
  type: string;
  mimeType?: string | null;
  size?: number | null;
  created?: string | null;
  modified?: string | null;
  publicUrl?: string | null;
}

export interface YandexDiskListResponseDto {
  current: YandexDiskResourceDto;
  items: YandexDiskResourceDto[];
  limit: number;
  offset: number;
  total: number;
}

export interface YandexDiskOperationResultDto {
  success: boolean;
  path: string;
}

export interface YandexDiskConnectionTestStepDto {
  key: string;
  name: string;
  success: boolean;
  message: string;
}

export interface YandexDiskConnectionTestDto {
  success: boolean;
  rootPath: string;
  testFolderPath: string;
  testFilePath: string;
  checkedAt: string;
  steps: YandexDiskConnectionTestStepDto[];
}

export interface YandexDiskTextFileDto {
  path: string;
  content: string;
}

export interface YandexDiskCrmSyncSettingsDto {
  enabled: boolean;
  syncInvoices: boolean;
  syncActs: boolean;
  syncContracts: boolean;
  syncCaseDocuments: boolean;
  syncTaskFiles: boolean;
  overwriteExisting: boolean;
  conflictResolutionMode: string;
  conflictResolutionModeText: string;
  folderTemplate: string;
  fileNameTemplate: string;
  availableTokens: string[];
  updatedAt?: string | null;
}

export interface YandexDiskCrmSyncPreviewItemDto {
  documentKind: string;
  documentType: string;
  enabled: boolean;
  originalFileName: string;
  targetPath: string;
  fullPath: string;
  operation: string;
  operationText: string;
}

export interface YandexDiskCrmSyncPreviewDto {
  syncEnabled: boolean;
  overwriteExisting: boolean;
  conflictResolutionMode: string;
  conflictResolutionModeText: string;
  rootPath: string;
  folderTemplate: string;
  fileNameTemplate: string;
  items: YandexDiskCrmSyncPreviewItemDto[];
}

export interface YandexDiskCrmSyncStatusItemDto {
  documentKind: string;
  objectType: string;
  objectId: number;
  parentObjectId?: number | null;
  fileName?: string | null;
  yandexDiskPath?: string | null;
  status: string;
  statusText: string;
  lastError?: string | null;
  attemptCount: number;
  lastSyncedAt?: string | null;
  updatedAt: string;
}

export interface YandexDiskCrmSyncStatusReportDto {
  pendingCount: number;
  failedCount: number;
  successCount: number;
  lastSuccessfulSyncAt?: string | null;
  recentItems: YandexDiskCrmSyncStatusItemDto[];
}

export interface UpdateYandexDiskCrmSyncSettingsRequest {
  enabled: boolean;
  syncInvoices: boolean;
  syncActs: boolean;
  syncContracts: boolean;
  syncCaseDocuments: boolean;
  syncTaskFiles: boolean;
  overwriteExisting: boolean;
  conflictResolutionMode?: string;
  folderTemplate?: string;
  fileNameTemplate?: string;
}

export interface S3StorageProviderPresetDto {
  key: string;
  name: string;
  endpoint: string;
  region: string;
  forcePathStyle: boolean;
}

export interface S3StorageConnectionDto {
  id: string;
  name: string;
  provider: string;
  providerName: string;
  endpoint: string;
  region: string;
  bucket: string;
  rootPrefix: string;
  isEnabled: boolean;
  isConfigured: boolean;
  accessKeyPreview?: string | null;
  secretKeyPreview?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface S3StorageConnectionsOverviewDto {
  providerPresets: S3StorageProviderPresetDto[];
  connections: S3StorageConnectionDto[];
}

export interface UpsertS3StorageConnectionRequest {
  id?: string | null;
  name: string;
  provider: string;
  endpoint: string;
  region: string;
  bucket: string;
  rootPrefix: string;
  accessKeyId: string;
  secretAccessKey: string;
  clearSecret: boolean;
  isEnabled: boolean;
}

export interface S3StorageConnectionTestStepDto {
  key: string;
  name: string;
  success: boolean;
  message: string;
}

export interface S3StorageConnectionTestDto {
  success: boolean;
  connectionId: string;
  endpoint: string;
  region: string;
  bucket: string;
  testKey: string;
  checkedAt: string;
  steps: S3StorageConnectionTestStepDto[];
}

export interface S3StorageObjectDto {
  name: string;
  path: string;
  key: string;
  type: string;
  contentType?: string | null;
  size?: number | null;
  lastModified?: string | null;
}

export interface S3StorageListResponseDto {
  current: S3StorageObjectDto;
  items: S3StorageObjectDto[];
  limit: number;
  offset: number;
  total: number;
}

export interface S3StorageOperationResultDto {
  success: boolean;
  path: string;
  key: string;
}

export interface S3StorageTextFileDto {
  path: string;
  content: string;
}

export interface RolePermissionAuditRoleDto {
  roleId: number;
  roleName: string;
  isSystemRole: boolean;
  assignedUsersCount: number;
  configuredSectionsCount: number;
  totalSectionsCount: number;
  hasIssues: boolean;
  missingSections: string[];
  unknownSections: string[];
  duplicateSections: string[];
  invalidViewScopeSections: string[];
  capabilityWithoutViewSections: string[];
  restrictedCapabilitySections: string[];
  broadDataScopeSections: string[];
}

export interface RolePermissionAuditReportDto {
  generatedAtUtc: string;
  totalRoles: number;
  readyRoles: number;
  rolesWithIssues: number;
  rolesWithBroadDataScope: number;
  catalogSections: string[];
  dataScopedSections: string[];
  roles: RolePermissionAuditRoleDto[];
}

export type NotificationChannel = 'email' | 'telegram';

export interface NotificationChannelDto {
  key: string;
  label: string;
  description: string;
  status: string;
  isEnabled: boolean;
  isSendable: boolean;
  supportsUserPreferences: boolean;
  supportsDeliveryLog: boolean;
  requiresProvider: boolean;
  isFuture: boolean;
}

export interface NotificationPurposeDto {
  key: string;
  label: string;
  description: string;
}

export interface NotificationTemplateVariableDto {
  key: string;
  label: string;
}

export interface NotificationTemplateDto {
  key: string;
  label: string;
  description: string;
  isEnabled: boolean;
  subject: string;
  body: string;
  defaultSubject: string;
  defaultBody: string;
  variables: NotificationTemplateVariableDto[];
}

export interface EmailNotificationSettingsDto {
  enabled: boolean;
  ready: boolean;
  contractTerminationClientEmailsEnabled: boolean;
  smtpHost: string;
  smtpPort: number;
  smtpUsername: string;
  smtpPasswordSet: boolean;
  fromEmail: string;
  fromName: string;
  enableSsl: boolean;
}

export interface TelegramNotificationSettingsDto {
  enabled: boolean;
  ready: boolean;
  botTokenSet: boolean;
  botUsername: string;
  linkedUsersCount: number;
}

export interface EmailNotificationSenderDto {
  smtpHost: string;
  smtpPort: number;
  smtpUsername: string;
  smtpPasswordSet: boolean;
  fromEmail: string;
  fromName: string;
  enableSsl: boolean;
}

export interface TelegramNotificationSenderDto {
  botTokenSet: boolean;
  botUsername: string;
  updateMode: 'disabled' | 'webhook' | 'polling' | string;
  webhookSecretSet: boolean;
  webhookUrl: string;
  webhookSyncedAt?: string | null;
  lastPollingAt?: string | null;
  subscriptionsCount: number;
  proxyUrl: string;
  proxyUsername: string;
  proxyPasswordSet: boolean;
  botId?: number | null;
  botFirstName: string;
  profileName: string;
  profileDescription: string;
  profileShortDescription: string;
  profileCommands: TelegramBotCommandDto[];
  hasProfilePhoto: boolean;
  profileSyncedAt?: string | null;
  canJoinGroups: boolean;
  canReadAllGroupMessages: boolean;
  supportsInlineQueries: boolean;
}

export interface TelegramBotCommandDto {
  command: string;
  description: string;
}

export interface NotificationSenderDto {
  id: number;
  channel: NotificationChannel;
  name: string;
  isEnabled: boolean;
  ready: boolean;
  purposes: string[];
  sortOrder: number;
  email?: EmailNotificationSenderDto | null;
  telegram?: TelegramNotificationSenderDto | null;
  createdAt: string;
  updatedAt?: string | null;
}

export interface NotificationSettingsDto {
  email: EmailNotificationSettingsDto;
  telegram: TelegramNotificationSettingsDto;
  senders: NotificationSenderDto[];
  purposes: NotificationPurposeDto[];
  templates: NotificationTemplateDto[];
}

export interface UpdateEmailNotificationSettingsRequest {
  enabled: boolean;
  contractTerminationClientEmailsEnabled: boolean;
  smtpHost?: string | null;
  smtpPort?: number | null;
  smtpUsername?: string | null;
  smtpPassword?: string | null;
  keepExistingPassword?: boolean;
  fromEmail?: string | null;
  fromName?: string | null;
  enableSsl: boolean;
}

export interface UpdateNotificationTemplateRequest {
  isEnabled: boolean;
  subject?: string | null;
  body?: string | null;
}

export interface UserNotificationDto {
  id: number;
  type: string;
  typeLabel: string;
  eventKey: string;
  title: string;
  bodyPreview: string;
  tone: 'rose' | 'amber' | 'blue' | 'emerald' | string;
  objectType: string;
  objectId?: number | null;
  taskId?: number | null;
  isRead: boolean;
  readAt?: string | null;
  isArchived: boolean;
  archivedAt?: string | null;
  createdAt: string;
}

export interface UserNotificationTypeDto {
  key: string;
  label: string;
  description: string;
  tone: string;
  priority: string;
  channels: string[];
  deduplicate: boolean;
  digestible: boolean;
}

export interface UserNotificationBulkActionResultDto {
  updatedCount: number;
}

export interface UserNotificationPreferenceDto {
  disabledTypes: string[];
  disabledChannels: Record<string, string[]>;
}

export interface NotificationRolePreferenceDto {
  roleId: number;
  roleName: string;
  isSystemRole: boolean;
  preferences: UserNotificationPreferenceDto;
}

export interface NotificationRolePreferencesDto {
  roles: NotificationRolePreferenceDto[];
}

export interface UserNotificationsQuery {
  status?: 'active' | 'unread' | 'read' | 'archived' | 'all' | string;
  type?: string;
  search?: string;
  dateFrom?: string;
  dateTo?: string;
  employeeId?: number;
  clientId?: number;
  taskRole?: 'assignee' | 'author' | 'observer' | 'approver' | string;
  page?: number;
  pageSize?: number;
}

export interface UpsertNotificationSenderRequest {
  channel: NotificationChannel;
  name: string;
  isEnabled: boolean;
  purposes: string[];
  sortOrder: number;
  email?: {
    smtpHost?: string | null;
    smtpPort?: number | null;
    smtpUsername?: string | null;
    smtpPassword?: string | null;
    keepExistingPassword?: boolean;
    fromEmail?: string | null;
    fromName?: string | null;
    enableSsl?: boolean;
  } | null;
  telegram?: {
    botToken?: string | null;
    keepExistingToken?: boolean;
    botUsername?: string | null;
    updateMode?: string | null;
    webhookSecret?: string | null;
    keepExistingWebhookSecret?: boolean;
    proxyUrl?: string | null;
    proxyUsername?: string | null;
    proxyPassword?: string | null;
    keepExistingProxyPassword?: boolean;
    profileName?: string | null;
    profileDescription?: string | null;
    profileShortDescription?: string | null;
    profileCommands?: TelegramBotCommandDto[] | null;
  } | null;
}

export interface SendNotificationTestResponseDto {
  success: boolean;
  channel: string;
  recipient: string;
  status: string;
  error?: string | null;
}

export interface NotificationDeliveryDto {
  id: number;
  eventKey: string;
  channel: string;
  senderId?: number | null;
  senderName: string;
  recipientUserId?: number | null;
  recipientAddress: string;
  subject: string;
  status: string;
  error?: string | null;
  createdAt: string;
  sentAt?: string | null;
}

export interface TelegramWebhookSyncResultDto {
  success: boolean;
  message: string;
  webhookUrl: string;
  botUsername: string;
}

export interface TelegramBotProfileSyncResultDto {
  success: boolean;
  message: string;
  profile?: {
    botId?: number | null;
    botUsername: string;
    botFirstName: string;
    name: string;
    description: string;
    shortDescription: string;
    commands: TelegramBotCommandDto[];
    hasProfilePhoto: boolean;
    syncedAt?: string | null;
    canJoinGroups: boolean;
    canReadAllGroupMessages: boolean;
    supportsInlineQueries: boolean;
  } | null;
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

export interface SystemJobDiagnosticsItemDto {
  key: string;
  name: string;
  description: string;
  enabled: boolean;
  status: string;
  statusText: string;
  intervalText: string;
  pendingCount?: number | null;
  lastProcessedCount?: number | null;
  lastActivityAt?: string | null;
  lastActivityDescription?: string | null;
  nextRunHint?: string | null;
  lastRunStatus?: string | null;
  lastRunStatusText?: string | null;
  lastRunStartedAt?: string | null;
  lastRunFinishedAt?: string | null;
  lastRunError?: string | null;
  manualRunEnabled?: boolean;
  manualRunHint?: string | null;
}

export interface SystemJobDiagnosticsDto {
  generatedAt: string;
  items: SystemJobDiagnosticsItemDto[];
}

export interface SystemJobManualRunResultDto {
  jobKey: string;
  startedAt: string;
  finishedAt: string;
  processedCount?: number | null;
  skippedCount?: number | null;
  summary: string;
  diagnostics: SystemJobDiagnosticsDto;
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

export interface SpreadsheetImportPreviewCellDto {
  key: string;
  label: string;
  value?: string | null;
}

export interface SpreadsheetImportPreviewChangeDto {
  fieldKey: string;
  fieldLabel: string;
  kind: string;
  currentValue?: string | null;
  incomingValue?: string | null;
}

export interface SpreadsheetImportPreviewRowDto {
  previewId: string;
  sourceFileName: string;
  sourceFileHash: string;
  sourceRowNumber: number;
  action: string;
  isSelectable: boolean;
  isDerived: boolean;
  title: string;
  subtitle?: string | null;
  message?: string | null;
  dependencyPreviewIds: string[];
  dependencyTitles: string[];
  issues: string[];
  counterpartyImportMode?: string | null;
  counterpartyImportModeHint?: string | null;
  availableCounterpartyImportModes: string[];
  changes: SpreadsheetImportPreviewChangeDto[];
  cells: SpreadsheetImportPreviewCellDto[];
}

export interface SpreadsheetImportPreviewSectionDto {
  key: string;
  label: string;
  totalRows: number;
  createRows: number;
  updateRows: number;
  skipRows: number;
  errorRows: number;
  selectableRows: number;
  rows: SpreadsheetImportPreviewRowDto[];
}

export interface SpreadsheetImportPreviewFileDto {
  fileName: string;
  fileHash: string;
  detectedKind: string;
  detectedLabel: string;
  sheetName?: string | null;
  rowCount: number;
  message?: string | null;
}

export interface SpreadsheetImportPreviewDto {
  startedAt: string;
  totalSelectableRows: number;
  files: SpreadsheetImportPreviewFileDto[];
  sections: SpreadsheetImportPreviewSectionDto[];
  messages: string[];
}

export interface SpreadsheetImportBatchFileDto {
  fileName: string;
  fileHash: string;
  detectedKind: string;
  sheetName?: string | null;
  rowCount: number;
  createdAt: string;
}

export interface SpreadsheetImportBatchOperationDto {
  id: number;
  sectionKey: string;
  sourceFileName: string;
  sourceFileHash: string;
  sourceRowId: string;
  sourceRowNumber: number;
  sourceKey: string;
  sourceTitle: string;
  action: string;
  entityType: string;
  entityId?: number | null;
  message?: string | null;
  metadataJson?: string | null;
  rollbackAction?: string | null;
  rollbackMessage?: string | null;
  createdAt: string;
  rolledBackAt?: string | null;
}

export interface SpreadsheetImportBatchSummaryDto {
  id: number;
  title: string;
  status: string;
  totalRows: number;
  createdRows: number;
  updatedRows: number;
  skippedRows: number;
  errorRows: number;
  rollbackRows: number;
  createdAt: string;
  appliedAt?: string | null;
  rolledBackAt?: string | null;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  files: SpreadsheetImportBatchFileDto[];
}

export interface SpreadsheetImportBatchDetailDto extends SpreadsheetImportBatchSummaryDto {
  operations: SpreadsheetImportBatchOperationDto[];
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

export interface InvoiceDocumentTemplateDto {
  key: string;
  name: string;
  description?: string | null;
  html: string;
}

export interface InvoiceDocumentTemplateTokenDto {
  key: string;
  category: string;
  description: string;
  isRepeating: boolean;
}

export interface InvoiceDocumentTemplateSettingsDto {
  activeTemplateKey: string;
  printWithSignatureAndSealByDefault: boolean;
  templates: InvoiceDocumentTemplateDto[];
  tokens: InvoiceDocumentTemplateTokenDto[];
  defaultTemplateHtml: string;
}

export interface TaskJournalSettingsDto {
  restrictExecutorEditing: boolean;
  allowExecutorDeadlineChangeRequests: boolean;
  caseInactivityAutomationEnabled: boolean;
  caseInactivityDays: number;
  caseInactivityLimit: number;
  caseInactivityDefaultAssigneeUserId?: number | null;
  caseInactivityEscalationDays: number;
  caseInactivityEscalationUserId?: number | null;
}

export interface TaskJournalAutomationDiagnosticsDto {
  enabled: boolean;
  inactivityDays: number;
  escalationDays: number;
  limit: number;
  defaultAssigneeUserId?: number | null;
  defaultAssigneeName: string;
  escalationUserId?: number | null;
  escalationUserName: string;
  visibleCandidateCount: number;
  visibleEscalationCandidateCount: number;
  createdTaskCountLast7Days: number;
  lastCreatedAt?: string | null;
  lastCreatedDescription?: string | null;
}

export interface UpdateTaskJournalSettingsRequest {
  restrictExecutorEditing: boolean;
  allowExecutorDeadlineChangeRequests: boolean;
  caseInactivityAutomationEnabled: boolean;
  caseInactivityDays: number;
  caseInactivityLimit: number;
  caseInactivityDefaultAssigneeUserId?: number | null;
  caseInactivityEscalationDays: number;
  caseInactivityEscalationUserId?: number | null;
}

export interface MainCompanySettingsDto {
  defaultCompanyId?: number | null;
  companies: LegalEntitySummary[];
}

export interface UpdateMainCompanySettingsRequest {
  companyIds: number[];
  defaultCompanyId?: number | null;
}

export interface BankDto {
  id: number;
  name: string;
  bik?: string | null;
  correspondentAccount?: string | null;
  city?: string | null;
  isForeign: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export interface BankUpsertRequest {
  name: string;
  bik?: string | null;
  correspondentAccount?: string | null;
  city?: string | null;
  isForeign: boolean;
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

export interface InvoiceOrphanCleanupItemDto {
  id: number;
  number: string;
  date: string;
}

export interface InvoiceOrphanCleanupResultDto {
  deleted: number;
  items: InvoiceOrphanCleanupItemDto[];
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
type CasesRegistryExportParams = {
  clientId?: number;
  legalEntityId?: number;
  status?: string;
  search?: string;
  responsibleUserId?: number;
  caseType?: string;
  priority?: string;
  from?: string;
  to?: string;
  minFee?: number;
  maxFee?: number;
  scope?: 'all' | 'active' | 'archive' | 'mine' | string;
  activity?: 'all' | 'active' | 'inactive' | string;
  inactiveDays?: number;
  sortBy?:
    | 'createdAt'
    | 'title'
    | 'status'
    | 'caseNumber'
    | 'client'
    | 'caseType'
    | 'stage'
    | 'responsible'
    | 'startDate'
    | 'dueDate'
    | 'feeAmount'
    | 'priority'
    | 'court'
    | 'nextHearingAt'
    | 'lastViewedAt';
  sortDir?: SortDir;
};
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
  | 'counterparty'
  | 'responsible'
  | 'createdAt';

type ContractsSortKey = 'date' | 'number' | 'amount' | 'createdAt';

function buildQuery(params: Record<string, string | number | boolean | ReadonlyArray<string | number | boolean> | undefined | null>) {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    if (Array.isArray(v)) {
      v.forEach((item) => {
        if (item !== undefined && item !== null && item !== '') {
          q.append(k, String(item));
        }
      });
      continue;
    }
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

export async function extractResponseErrorMessage(response: Response): Promise<string> {
  const contentType = response.headers.get('content-type') || '';

  if (contentType.includes('application/json') || contentType.includes('application/problem+json')) {
    try {
      const payload = await response.json() as
        | { detail?: string; title?: string; message?: string; errors?: Record<string, string[] | string> }
        | null;

      const detail = payload?.detail?.trim();
      if (detail) {
        return detail;
      }

      const message = payload?.message?.trim();
      if (message) {
        return message;
      }

      const firstError = payload?.errors
        ? Object.values(payload.errors)
            .flatMap((value) => Array.isArray(value) ? value : [value])
            .map((value) => `${value}`.trim())
            .find(Boolean)
        : '';

      if (firstError) {
        return firstError;
      }

      const title = payload?.title?.trim();
      if (title) {
        return title;
      }
    } catch {
      // no-op, fall back to text
    }
  }

  const text = await response.text().catch(() => '');
  return text.trim();
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
      if (response.status === 401) {
        notifyAuthSessionExpired();
        throw new Error(`HTTP 401 Unauthorized at ${url} - ${AUTH_SESSION_EXPIRED_MESSAGE}`);
      }

      const text = await extractResponseErrorMessage(response);
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

  async getFeatureFlags() {
    return this.request<AppFeatureFlagsDto>('/features');
  }

  async getLatestAppVersion() {
    return this.request<AppVersionLatestDto>('/app-version/latest');
  }

  async getAppVersionHistory() {
    return this.request<AppReleaseDto[]>('/app-version/history');
  }

  async getAdminAppReleases() {
    return this.request<AppReleaseAdminDto[]>('/app-version/admin/releases');
  }

  async createAdminAppRelease(payload: SaveAppReleaseRequest) {
    return this.request<AppReleaseAdminDto>('/app-version/admin/releases', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateAdminAppRelease(id: number, payload: SaveAppReleaseRequest) {
    return this.request<AppReleaseAdminDto>(`/app-version/admin/releases/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteAdminAppRelease(id: number) {
    return this.request<void>(`/app-version/admin/releases/${id}`, {
      method: 'DELETE',
    });
  }

  async markAppVersionSeen(releaseId: string) {
    return this.request<void>('/app-version/mark-seen', {
      method: 'POST',
      body: JSON.stringify({ releaseId }),
    });
  }

  async getLegalIntegrationDiagnostics() {
    return this.request<LegalIntegrationDiagnosticsDto>('/features/legal-integrations/diagnostics');
  }

  async getCalendarSyncProviders() {
    return this.request<CalendarSyncProviderDto[]>('/calendar-sync/providers');
  }

  async getCalendarSyncReadiness() {
    return this.request<CalendarSyncReadinessDto>('/calendar-sync/readiness');
  }

  async getCalendarSyncSettings() {
    return this.request<CalendarSyncSettingsDto>('/calendar-sync/settings');
  }

  async updateCalendarSyncSettings(payload: UpdateCalendarSyncSettingsRequest) {
    return this.request<CalendarSyncSettingsDto>('/calendar-sync/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getYandexDiskSettings() {
    return this.request<YandexDiskSettingsDto>('/yandex-disk/settings');
  }

  async updateYandexDiskSettings(payload: UpdateYandexDiskSettingsRequest) {
    return this.request<YandexDiskSettingsDto>('/yandex-disk/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async testYandexDiskConnection(testFolderPath?: string) {
    return this.request<YandexDiskConnectionTestDto>('/yandex-disk/connection-test', {
      method: 'POST',
      body: JSON.stringify({ testFolderPath }),
    });
  }

  async listYandexDiskResources(path = '/') {
    return this.request<YandexDiskListResponseDto>(`/yandex-disk/resources?path=${encodeURIComponent(path)}`);
  }

  async createYandexDiskFolder(path: string) {
    return this.request<YandexDiskOperationResultDto>('/yandex-disk/folders', {
      method: 'POST',
      body: JSON.stringify({ path }),
    });
  }

  async copyYandexDiskResource(fromPath: string, toPath: string, overwrite = false) {
    return this.request<YandexDiskOperationResultDto>('/yandex-disk/copy', {
      method: 'POST',
      body: JSON.stringify({ fromPath, toPath, overwrite }),
    });
  }

  async moveYandexDiskResource(fromPath: string, toPath: string, overwrite = false) {
    return this.request<YandexDiskOperationResultDto>('/yandex-disk/move', {
      method: 'POST',
      body: JSON.stringify({ fromPath, toPath, overwrite }),
    });
  }

  async deleteYandexDiskResource(path: string, permanently = false) {
    return this.request<YandexDiskOperationResultDto>(
      `/yandex-disk/resources?path=${encodeURIComponent(path)}&permanently=${permanently}`,
      { method: 'DELETE' },
    );
  }

  async uploadYandexDiskFile(path: string, file: File, overwrite = true) {
    const form = new FormData();
    form.append('file', file);
    form.append('path', path);
    form.append('overwrite', String(overwrite));
    return this.request<YandexDiskOperationResultDto>('/yandex-disk/upload', {
      method: 'POST',
      body: form,
    });
  }

  async downloadYandexDiskFile(path: string) {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/yandex-disk/download?path=${encodeURIComponent(path)}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      if (response.status === 401) {
        notifyAuthSessionExpired();
      }
      const text = await extractResponseErrorMessage(response);
      throw new Error(`HTTP ${response.status} ${response.statusText} - ${text}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')) || path.split('/').pop() || 'yandex-disk-file',
    };
  }

  async getYandexDiskTextFile(path: string) {
    return this.request<YandexDiskTextFileDto>(`/yandex-disk/text?path=${encodeURIComponent(path)}`);
  }

  async saveYandexDiskTextFile(path: string, content: string, overwrite = true) {
    return this.request<YandexDiskOperationResultDto>('/yandex-disk/text', {
      method: 'PUT',
      body: JSON.stringify({ path, content, overwrite }),
    });
  }

  async getYandexDiskCrmSyncSettings() {
    return this.request<YandexDiskCrmSyncSettingsDto>('/yandex-disk/crm-sync/settings');
  }

  async getYandexDiskCrmSyncPreview() {
    return this.request<YandexDiskCrmSyncPreviewDto>('/yandex-disk/crm-sync/preview');
  }

  async getYandexDiskCrmSyncStatus() {
    return this.request<YandexDiskCrmSyncStatusReportDto>('/yandex-disk/crm-sync/status');
  }

  async updateYandexDiskCrmSyncSettings(payload: UpdateYandexDiskCrmSyncSettingsRequest) {
    return this.request<YandexDiskCrmSyncSettingsDto>('/yandex-disk/crm-sync/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getS3StorageConnections() {
    return this.request<S3StorageConnectionsOverviewDto>('/s3-storages');
  }

  async upsertS3StorageConnection(payload: UpsertS3StorageConnectionRequest) {
    return this.request<S3StorageConnectionDto>('/s3-storages', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async deleteS3StorageConnection(id: string) {
    return this.request<void>(`/s3-storages/${encodeURIComponent(id)}`, {
      method: 'DELETE',
    });
  }

  async testS3StorageConnection(id: string, testPrefix = '_payplanner-test') {
    return this.request<S3StorageConnectionTestDto>(`/s3-storages/${encodeURIComponent(id)}/connection-test`, {
      method: 'POST',
      body: JSON.stringify({ testPrefix }),
    });
  }

  async listS3StorageObjects(id: string, path = '/', limit = 100, offset = 0) {
    const query = new URLSearchParams({
      path,
      limit: String(limit),
      offset: String(offset),
    });
    return this.request<S3StorageListResponseDto>(`/s3-storages/${encodeURIComponent(id)}/objects?${query}`);
  }

  async createS3StorageFolder(id: string, path: string) {
    return this.request<S3StorageOperationResultDto>(`/s3-storages/${encodeURIComponent(id)}/folders`, {
      method: 'POST',
      body: JSON.stringify({ path }),
    });
  }

  async uploadS3StorageFile(id: string, path: string, file: File, overwrite = true) {
    const form = new FormData();
    form.append('file', file);
    form.append('path', path);
    form.append('overwrite', String(overwrite));
    return this.request<S3StorageOperationResultDto>(`/s3-storages/${encodeURIComponent(id)}/upload`, {
      method: 'POST',
      body: form,
    });
  }

  async downloadS3StorageFile(id: string, path: string) {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/s3-storages/${encodeURIComponent(id)}/download?path=${encodeURIComponent(path)}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      if (response.status === 401) {
        notifyAuthSessionExpired();
      }
      const text = await extractResponseErrorMessage(response);
      throw new Error(`HTTP ${response.status} ${response.statusText} - ${text}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')) || path.split('/').pop() || 's3-file',
    };
  }

  async deleteS3StorageObject(id: string, path: string, recursive = false) {
    return this.request<S3StorageOperationResultDto>(
      `/s3-storages/${encodeURIComponent(id)}/objects?path=${encodeURIComponent(path)}&recursive=${recursive}`,
      { method: 'DELETE' },
    );
  }

  async copyS3StorageObject(id: string, fromPath: string, toPath: string, overwrite = false) {
    return this.request<S3StorageOperationResultDto>(`/s3-storages/${encodeURIComponent(id)}/copy`, {
      method: 'POST',
      body: JSON.stringify({ fromPath, toPath, overwrite }),
    });
  }

  async moveS3StorageObject(id: string, fromPath: string, toPath: string, overwrite = false) {
    return this.request<S3StorageOperationResultDto>(`/s3-storages/${encodeURIComponent(id)}/move`, {
      method: 'POST',
      body: JSON.stringify({ fromPath, toPath, overwrite }),
    });
  }

  async getS3StorageTextFile(id: string, path: string) {
    return this.request<S3StorageTextFileDto>(`/s3-storages/${encodeURIComponent(id)}/text?path=${encodeURIComponent(path)}`);
  }

  async saveS3StorageTextFile(id: string, path: string, content: string, overwrite = true) {
    return this.request<S3StorageOperationResultDto>(`/s3-storages/${encodeURIComponent(id)}/text`, {
      method: 'PUT',
      body: JSON.stringify({ path, content, overwrite }),
    });
  }

  async uploadCaseDocumentToYandexDisk(documentId: number, overwrite?: boolean) {
    const query = overwrite === undefined ? '' : `?overwrite=${overwrite}`;
    return this.request<YandexDiskOperationResultDto>(`/yandex-disk/crm/case-documents/${documentId}/upload${query}`, {
      method: 'POST',
    });
  }

  async uploadTaskJournalFileToYandexDisk(taskId: number, fileId: number, overwrite?: boolean) {
    const query = overwrite === undefined ? '' : `?overwrite=${overwrite}`;
    return this.request<YandexDiskOperationResultDto>(`/yandex-disk/crm/tasks/${taskId}/files/${fileId}/upload${query}`, {
      method: 'POST',
    });
  }

  // ===== Acts =====
  async getActs(params?: {
    from?: string;
    to?: string;
    status?: ActStatus;
    workflowStatus?: ActWorkflowStatus;
    type?: PaymentKind;
    clientId?: number;
    legalEntityId?: number;
    caseId?: number;
    contractId?: number;
    invoiceId?: number;
    paymentId?: number;
    clientIds?: string;
    responsibleId?: number;
    search?: string;
    sortBy?: ActsSortKey;
    sortDir?: SortDir;
    includeLineItems?: boolean;
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
    workflowStatus?: ActWorkflowStatus;
    type?: PaymentKind;
    clientId?: number;
    legalEntityId?: number;
    caseId?: number;
    contractId?: number;
    invoiceId?: number;
    paymentId?: number;
    responsibleId?: number;
    search?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<ActsSummary>(`/acts/summary${q}`);
  }

  async getActResponsibles() {
    return this.request<ActResponsible[]>('/acts/responsibles');
  }

  async getAct(id: number) {
    return this.request<Act>(`/acts/${id}`);
  }

  async createAct(payload: ActInput) {
    return this.request<Act>('/acts', { method: 'POST', body: JSON.stringify(payload) });
  }

  async updateAct(id: number, payload: ActInput) {
    return this.request<Act>(`/acts/${id}`, { method: 'PUT', body: JSON.stringify(payload) });
  }

  async sendActToSigning(id: number) {
    return this.request<Act>(`/acts/${id}/send-to-signing`, { method: 'POST' });
  }

  async markActSigned(id: number) {
    return this.request<Act>(`/acts/${id}/mark-signed`, { method: 'POST' });
  }

  async markActNotSigned(id: number) {
    return this.request<Act>(`/acts/${id}/mark-not-signed`, { method: 'POST' });
  }

  async updateActWorkflowStatus(id: number, status: ActWorkflowStatus, reason?: string | null) {
    return this.request<Act>(`/acts/${id}/workflow-status`, {
      method: 'POST',
      body: JSON.stringify({ status, reason }),
    });
  }

  async cancelAct(id: number, reason: string) {
    return this.request<Act>(`/acts/${id}/cancel`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  async deleteAct(id: number) {
    return this.request<void>(`/acts/${id}`, { method: 'DELETE' });
  }

  // ===== Contracts =====
  async getContracts(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    legalEntityId?: number;
    invoiceId?: number;
    actId?: number;
    paymentId?: number;
    status?: ContractStatus;
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

  async getContractDocumentTemplateSettings() {
    return this.request<InvoiceDocumentTemplateSettingsDto>('/contracts/document-template-settings');
  }

  async updateContractDocumentTemplateSettings(payload: Pick<InvoiceDocumentTemplateSettingsDto, 'activeTemplateKey' | 'templates' | 'printWithSignatureAndSealByDefault'>) {
    return this.request<InvoiceDocumentTemplateSettingsDto>('/contracts/document-template-settings', {
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

  async sendContractToSigning(id: number) {
    return this.request<Contract>(`/contracts/${id}/send-to-signing`, { method: 'POST' });
  }

  async markContractConcluded(id: number) {
    return this.request<Contract>(`/contracts/${id}/mark-concluded`, { method: 'POST' });
  }

  async markContractNotSigned(id: number) {
    return this.request<Contract>(`/contracts/${id}/mark-not-signed`, { method: 'POST' });
  }

  async getContractTerminationCheck(id: number) {
    return this.request<ContractTerminationCheck>(`/contracts/${id}/termination-check`);
  }

  async updateContractStatus(id: number, status: ContractStatus, reason?: string | null) {
    return this.request<Contract>(`/contracts/${id}/status`, {
      method: 'POST',
      body: JSON.stringify({ status, reason }),
    });
  }

  async terminateContract(
    id: number,
    terminationDate: string,
    terms: string,
    reason?: string | null,
    financialTerms?: string | null,
  ) {
    return this.request<Contract>(`/contracts/${id}/terminate`, {
      method: 'POST',
      body: JSON.stringify({ terminationDate, terms, reason, financialTerms }),
    });
  }

  async cancelContract(id: number, reason: string) {
    return this.request<Contract>(`/contracts/${id}/cancel`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  async downloadContractDocument(id: number, format: 'word' | 'pdf' | 'html' = 'word') {
    const token = getAuthToken();
    const query = buildQuery({ format });
    const response = await fetch(`${API_BASE_URL}/contracts/${id}/document${query}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return response.blob();
  }

  async uploadContractDocumentToYandexDisk(id: number, format: 'word' | 'pdf' = 'pdf', overwrite?: boolean) {
    const query = buildQuery({ format, overwrite });
    return this.request<YandexDiskOperationResultDto>(`/contracts/${id}/yandex-disk${query}`, {
      method: 'POST',
    });
  }

  async downloadContractTerminationAgreement(id: number, format: 'word' | 'pdf' | 'html' = 'word') {
    const token = getAuthToken();
    const query = buildQuery({ format });
    const response = await fetch(`${API_BASE_URL}/contracts/${id}/termination-agreement${query}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return response.blob();
  }

  async sendContractByEmail(
    id: number,
    payload?: { email?: string; subject?: string; message?: string; format?: 'word' | 'pdf' },
  ) {
    return this.request<{ sent: boolean; recipient: string; fileName: string; format: string }>(
      `/contracts/${id}/send-email`,
      {
        method: 'POST',
        body: JSON.stringify(payload ?? {}),
      },
    );
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

  async getUserSettingForUser(userId: number, key: string) {
    return this.request<UserSettingDto>(`/user-settings/users/${userId}/${encodeURIComponent(key)}`);
  }

  async upsertUserSettingForUser(userId: number, key: string, value: string) {
    return this.request<UserSettingDto>(`/user-settings/users/${userId}/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ value }),
    });
  }

  // ===== Notifications =====
  async getNotificationSettings() {
    return this.request<NotificationSettingsDto>('/notifications/settings');
  }

  async getNotificationChannels() {
    return this.request<NotificationChannelDto[]>('/notifications/channels');
  }

  async updateEmailNotificationSettings(payload: UpdateEmailNotificationSettingsRequest) {
    return this.request<NotificationSettingsDto>('/notifications/settings/email', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getNotificationTemplates() {
    return this.request<NotificationTemplateDto[]>('/notifications/templates');
  }

  async updateNotificationTemplate(key: string, payload: UpdateNotificationTemplateRequest) {
    return this.request<NotificationTemplateDto>(`/notifications/templates/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async resetNotificationTemplate(key: string) {
    return this.request<NotificationTemplateDto>(`/notifications/templates/${encodeURIComponent(key)}/reset`, {
      method: 'POST',
    });
  }

  async getNotificationRolePreferences() {
    return this.request<NotificationRolePreferencesDto>('/notifications/role-preferences');
  }

  async updateNotificationRolePreferences(roleId: number, payload: UserNotificationPreferenceDto) {
    return this.request<NotificationRolePreferenceDto>(`/notifications/role-preferences/${roleId}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async createNotificationSender(payload: UpsertNotificationSenderRequest) {
    return this.request<NotificationSettingsDto>('/notifications/senders', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateNotificationSender(id: number, payload: UpsertNotificationSenderRequest) {
    return this.request<NotificationSettingsDto>(`/notifications/senders/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteNotificationSender(id: number) {
    return this.request<void>(`/notifications/senders/${id}`, {
      method: 'DELETE',
    });
  }

  async syncTelegramNotificationWebhook(id: number) {
    return this.request<TelegramWebhookSyncResultDto>(`/notifications/senders/${id}/telegram/webhook/sync`, {
      method: 'POST',
    });
  }

  async deleteTelegramNotificationWebhook(id: number) {
    return this.request<TelegramWebhookSyncResultDto>(`/notifications/senders/${id}/telegram/webhook`, {
      method: 'DELETE',
    });
  }

  async syncTelegramBotProfile(id: number) {
    return this.request<TelegramBotProfileSyncResultDto>(`/notifications/senders/${id}/telegram/profile/sync`, {
      method: 'POST',
    });
  }

  async applyTelegramBotProfile(
    id: number,
    payload: {
      name?: string | null;
      description?: string | null;
      shortDescription?: string | null;
      commands?: TelegramBotCommandDto[] | null;
    },
  ) {
    return this.request<TelegramBotProfileSyncResultDto>(`/notifications/senders/${id}/telegram/profile`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getTelegramBotProfilePhotoBlob(id: number) {
    const url = `${API_BASE_URL}/notifications/senders/${id}/telegram/photo`;
    const token = getAuthToken();
    const response = await fetch(url, {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(`HTTP ${response.status} ${response.statusText} at ${url} ${text ? `- ${text}` : ''}`);
    }

    return response.blob();
  }

  async sendNotificationTest(payload: {
    channel: NotificationChannel;
    senderId?: number | null;
    userId?: number | null;
    email?: string | null;
    telegramChatId?: number | null;
    subject?: string | null;
    message?: string | null;
  }) {
    return this.request<SendNotificationTestResponseDto>('/notifications/test', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getNotificationDeliveries(params?: {
    channel?: string | null;
    status?: string | null;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      channel: params?.channel,
      status: params?.status,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<NotificationDeliveryDto>>(`/notifications/deliveries${q}`);
  }

  async retryNotificationDelivery(id: number) {
    return this.request<NotificationDeliveryDto[]>(`/notifications/deliveries/${id}/retry`, {
      method: 'POST',
    });
  }

  async getDatabaseStatus() {
    return this.request<DatabaseStatusDto>('/database/status');
  }

  async getDatabaseBackupOverview() {
    return this.request<DatabaseBackupOverviewDto>('/database/backups');
  }

  async getSystemJobDiagnostics() {
    return this.request<SystemJobDiagnosticsDto>('/system/jobs/diagnostics');
  }

  async runSystemJob(jobKey: string) {
    return this.request<SystemJobManualRunResultDto>(`/system/jobs/${encodeURIComponent(jobKey)}/run`, {
      method: 'POST',
    });
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
      const text = await extractResponseErrorMessage(response);
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

  async previewSpreadsheetImport(files: File[]) {
    const payload = new FormData();
    files.forEach((file) => payload.append('files', file));
    return this.request<SpreadsheetImportPreviewDto>('/database/import/spreadsheets/preview', {
      method: 'POST',
      body: payload,
    });
  }

  async applySpreadsheetImport(
    files: File[],
    selectedPreviewIds: string[],
    counterpartyOptions: Array<{ previewId: string; importMode: string }> = [],
    options: { allowUpdates?: boolean; allowClearingExistingValues?: boolean } = {},
  ) {
    const payload = new FormData();
    files.forEach((file) => payload.append('files', file));
      payload.append('requestJson', JSON.stringify({
        SelectedPreviewIds: selectedPreviewIds,
        AllowUpdates: options.allowUpdates ?? false,
        AllowClearingExistingValues: options.allowClearingExistingValues ?? false,
        CounterpartyOptions: counterpartyOptions.map((option) => ({
        PreviewId: option.previewId,
        ImportMode: option.importMode,
      })),
    }));
    return this.request<SpreadsheetImportBatchDetailDto>('/database/import/spreadsheets/apply', {
      method: 'POST',
      body: payload,
    });
  }

  async getSpreadsheetImportBatches() {
    return this.request<SpreadsheetImportBatchSummaryDto[]>('/database/import/spreadsheets/batches');
  }

  async getSpreadsheetImportBatch(batchId: number) {
    return this.request<SpreadsheetImportBatchDetailDto>(`/database/import/spreadsheets/batches/${batchId}`);
  }

  async rollbackSpreadsheetImportBatch(batchId: number) {
    return this.request<SpreadsheetImportBatchDetailDto>(`/database/import/spreadsheets/batches/${batchId}/rollback`, {
      method: 'POST',
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
      const text = await extractResponseErrorMessage(response);
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
    workflowStatus?: InvoiceWorkflowStatus;
    clientId?: number;
    legalEntityId?: number;
    clientIds?: string;
    clientRole?: InvoiceClientRole;
    contractId?: number;
    actId?: number;
    paymentId?: number;
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
    workflowStatus?: InvoiceWorkflowStatus;
    clientId?: number;
    clientRole?: InvoiceClientRole;
    legalEntityId?: number;
    contractId?: number;
    actId?: number;
    paymentId?: number;
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

  async getInvoiceDocumentTemplateSettings() {
    return this.request<InvoiceDocumentTemplateSettingsDto>('/invoices/document-template-settings');
  }

  async updateInvoiceDocumentTemplateSettings(payload: Pick<InvoiceDocumentTemplateSettingsDto, 'activeTemplateKey' | 'templates' | 'printWithSignatureAndSealByDefault'>) {
    return this.request<InvoiceDocumentTemplateSettingsDto>('/invoices/document-template-settings', {
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

  async cleanupOrphanInvoices() {
    return this.request<InvoiceOrphanCleanupResultDto>('/invoices/cleanup-orphan-invoices', {
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

  async sendInvoiceToSigning(id: number) {
    return this.request<Invoice>(`/invoices/${id}/send-to-signing`, { method: 'POST' });
  }

  async markInvoiceSigned(id: number) {
    return this.request<Invoice>(`/invoices/${id}/mark-signed`, { method: 'POST' });
  }

  async markInvoiceNotSigned(id: number) {
    return this.request<Invoice>(`/invoices/${id}/mark-not-signed`, { method: 'POST' });
  }

  async updateInvoiceWorkflowStatus(id: number, status: InvoiceWorkflowStatus, reason?: string | null) {
    return this.request<Invoice>(`/invoices/${id}/workflow-status`, {
      method: 'POST',
      body: JSON.stringify({ status, reason }),
    });
  }

  async cancelInvoice(
    id: number,
    reason: string,
    receivedMoneyAction: 'Reject' | 'LeaveAsAdvance' | 'Refund' | 'Reallocate' = 'Reject',
    targetPaymentId?: number | null,
    refundSource?: 'Bank' | 'Cash' | 'Card' | 'EMoney' | null,
    refundDate?: string | null,
    cancellationScope: 'All' | 'UnpaidInstallmentsOnly' = 'All',
  ) {
    return this.request<Invoice>(`/invoices/${id}/cancel`, {
      method: 'POST',
      body: JSON.stringify({ reason, cancellationScope, receivedMoneyAction, targetPaymentId, refundSource, refundDate }),
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
    clientId?: number;
    legalEntityId?: number;
    clientIds?: string;
    contractId?: number;
    clientRole?: ClientRole;
    payablesOnly?: boolean;
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

  async getPayablesDashboard(params?: {
    from?: string;
    to?: string;
    search?: string;
    clientId?: number;
    legalEntityId?: number;
    clientIds?: string;
    clientRole?: ClientRole;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PayablesDashboard>(`/accounts/payables-dashboard${q}`);
  }

  async downloadInvoiceDocument(
    id: number,
    format: 'word' | 'pdf' | 'html' = 'word',
    options?: { includeSignatureAndSeal?: boolean },
  ) {
    const token = getAuthToken();
    const query = buildQuery({
      format,
      includeSignatureAndSeal: options?.includeSignatureAndSeal,
    });
    const response = await fetch(`${API_BASE_URL}/invoices/${id}/document${query}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return response.blob();
  }

  async uploadInvoiceDocumentToYandexDisk(
    id: number,
    format: 'word' | 'pdf' = 'pdf',
    options?: { includeSignatureAndSeal?: boolean; overwrite?: boolean },
  ) {
    const query = buildQuery({
      format,
      includeSignatureAndSeal: options?.includeSignatureAndSeal,
      overwrite: options?.overwrite,
    });
    return this.request<YandexDiskOperationResultDto>(`/invoices/${id}/yandex-disk${query}`, {
      method: 'POST',
    });
  }

  async downloadActDocument(
    id: number,
    format: 'word' | 'pdf' | 'html' = 'word',
    options?: { includeSignatureAndSeal?: boolean },
  ) {
    const token = getAuthToken();
    const query = buildQuery({
      format,
      includeSignatureAndSeal: options?.includeSignatureAndSeal,
    });
    const response = await fetch(`${API_BASE_URL}/acts/${id}/document${query}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return response.blob();
  }

  async uploadActDocumentToYandexDisk(
    id: number,
    format: 'word' | 'pdf' = 'pdf',
    options?: { includeSignatureAndSeal?: boolean; overwrite?: boolean },
  ) {
    const query = buildQuery({
      format,
      includeSignatureAndSeal: options?.includeSignatureAndSeal,
      overwrite: options?.overwrite,
    });
    return this.request<YandexDiskOperationResultDto>(`/acts/${id}/yandex-disk${query}`, {
      method: 'POST',
    });
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

  async sendActByEmail(
    id: number,
    payload?: { email?: string; subject?: string; message?: string; format?: 'word' | 'pdf' },
  ) {
    return this.request<{ sent: boolean; recipient: string; fileName: string; format: string }>(
      `/acts/${id}/send-email`,
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

  async getRolePermissionsAudit() {
    return this.request<RolePermissionAuditReportDto>('/role-permissions/audit');
  }

  async exportRolePermissionsAuditCsv() {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/role-permissions/audit.csv`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/role-permissions/audit.csv ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'role-permissions-audit.csv',
    };
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

  // ===== Task journal =====
  async getTaskJournalItems(params?: {
    search?: string;
    status?: string;
    type?: string;
    priority?: string;
    assigneeUserId?: number;
    clientCaseId?: number;
    clientId?: number;
    legalEntityId?: number;
    from?: string;
    to?: string;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      search: params?.search,
      status: params?.status,
      type: params?.type,
      priority: params?.priority,
      assigneeUserId: params?.assigneeUserId,
      clientCaseId: params?.clientCaseId,
      clientId: params?.clientId,
      legalEntityId: params?.legalEntityId,
      from: params?.from,
      to: params?.to,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 100,
    });
    return this.request<PagedResult<TaskJournalItem>>(`/tasks${q}`);
  }

  async getTaskJournalAssignees() {
    return this.request<TaskJournalAssignee[]>('/tasks/assignees');
  }

  async getTaskJournalSettings() {
    return this.request<TaskJournalSettingsDto>('/tasks/settings');
  }

  async updateTaskJournalSettings(payload: UpdateTaskJournalSettingsRequest) {
    return this.request<TaskJournalSettingsDto>('/tasks/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getTaskJournalAutomationDiagnostics() {
    return this.request<TaskJournalAutomationDiagnosticsDto>('/tasks/automation/case-control/diagnostics');
  }

  async getTaskJournalCaseAutomationCandidates(params?: { inactiveDays?: number; limit?: number; escalationDays?: number }) {
    const q = buildQuery({
      inactiveDays: params?.inactiveDays,
      limit: params?.limit,
      escalationDays: params?.escalationDays,
    });
    return this.request<TaskJournalCaseAutomationCandidate[]>(`/tasks/automation/case-candidates${q}`);
  }

  async createTaskJournalCaseAutomationTask(clientCaseId: number) {
    return this.request<TaskJournalItem>(`/tasks/automation/case-candidates/${clientCaseId}/task`, {
      method: 'POST',
    });
  }

  async createTaskJournalCaseAutomationTasks(params?: { limit?: number }) {
    const q = buildQuery({
      limit: params?.limit,
    });
    return this.request<TaskJournalAutomationCreateResult>(`/tasks/automation/case-candidates/tasks${q}`, {
      method: 'POST',
    });
  }

  async downloadTaskJournalCalendar(params?: { from?: string; to?: string; reminderMinutes?: number[] }) {
    const q = buildQuery({
      from: params?.from,
      to: params?.to,
      reminderMinutes: params?.reminderMinutes,
    });
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/tasks/calendar.ics${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')) || 'task-calendar.ics',
    };
  }

  async downloadTaskPlanningReport(params?: {
    format?: 'html' | 'word' | 'pdf';
    search?: string;
    status?: string;
    type?: string;
    priority?: string;
    assigneeUserId?: number;
    from?: string;
    to?: string;
    clientCaseId?: number;
    clientId?: number;
    legalEntityId?: number;
  }) {
    const format = params?.format ?? 'html';
    const q = buildQuery({
      search: params?.search,
      status: params?.status,
      type: params?.type,
      priority: params?.priority,
      assigneeUserId: params?.assigneeUserId,
      from: params?.from,
      to: params?.to,
      clientCaseId: params?.clientCaseId,
      clientId: params?.clientId,
      legalEntityId: params?.legalEntityId,
    });
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/tasks/planning-report.${format}${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')) || `task-planning-report.${format === 'word' ? 'doc' : format}`,
    };
  }

  async getTaskJournalNotifications() {
    return this.request<TaskJournalNotification[]>('/tasks/notifications');
  }

  async getUserNotificationTypes() {
    return this.request<UserNotificationTypeDto[]>('/notifications/inbox/types');
  }

  async getUserNotifications(params?: UserNotificationsQuery) {
    const q = buildQuery({
      status: params?.status ?? 'active',
      type: params?.type,
      search: params?.search,
      dateFrom: params?.dateFrom,
      dateTo: params?.dateTo,
      employeeId: params?.employeeId,
      clientId: params?.clientId,
      taskRole: params?.taskRole,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 25,
    });
    return this.request<PagedResult<UserNotificationDto>>(`/notifications/inbox${q}`);
  }

  async getUnreadUserNotificationCount() {
    return this.request<number>('/notifications/inbox/unread-count');
  }

  async markUserNotificationRead(id: number) {
    return this.request<UserNotificationDto>(`/notifications/inbox/${id}/read`, {
      method: 'PATCH',
    });
  }

  async markUserNotificationUnread(id: number) {
    return this.request<UserNotificationDto>(`/notifications/inbox/${id}/unread`, {
      method: 'PATCH',
    });
  }

  async markAllUserNotificationsRead(params?: Pick<UserNotificationsQuery, 'status' | 'type' | 'search' | 'dateFrom' | 'dateTo' | 'employeeId' | 'clientId' | 'taskRole'>) {
    const q = buildQuery({
      status: params?.status,
      type: params?.type,
      search: params?.search,
      dateFrom: params?.dateFrom,
      dateTo: params?.dateTo,
      employeeId: params?.employeeId,
      clientId: params?.clientId,
      taskRole: params?.taskRole,
    });
    return this.request<UserNotificationBulkActionResultDto>(`/notifications/inbox/read-all${q}`, {
      method: 'POST',
    });
  }

  async archiveUserNotification(id: number) {
    return this.request<UserNotificationDto>(`/notifications/inbox/${id}/archive`, {
      method: 'PATCH',
    });
  }

  async deleteUserNotification(id: number) {
    return this.request<void>(`/notifications/inbox/${id}`, {
      method: 'DELETE',
    });
  }

  async getTaskJournalItem(id: number) {
    return this.request<TaskJournalItem>(`/tasks/${id}`);
  }

  async createTaskJournalItem(payload: TaskJournalUpsertPayload) {
    return this.request<TaskJournalItem>('/tasks', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateTaskJournalItem(id: number, payload: TaskJournalUpsertPayload) {
    return this.request<TaskJournalItem>(`/tasks/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async updateTaskJournalStatus(id: number, status: TaskJournalStatus, result?: string) {
    return this.request<TaskJournalItem>(`/tasks/${id}/status`, {
      method: 'PATCH',
      body: JSON.stringify({ status, result }),
    });
  }

  async bulkUpdateTaskJournalItems(payload: TaskJournalBulkUpdatePayload) {
    return this.request<TaskJournalBulkUpdateResult>('/tasks/bulk', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async bulkDeleteTaskJournalItems(ids: number[]) {
    return this.request<TaskJournalBulkUpdateResult>('/tasks/bulk/delete', {
      method: 'POST',
      body: JSON.stringify({ ids }),
    });
  }

  async updateTaskJournalApproval(id: number, decision: 'approved' | 'revisionRequested', resolution?: string, participantId?: number) {
    return this.request<TaskJournalItem>(`/tasks/${id}/approval`, {
      method: 'PATCH',
      body: JSON.stringify({ decision, resolution, participantId }),
    });
  }

  async getTaskJournalComments(id: number) {
    return this.request<TaskJournalComment[]>(`/tasks/${id}/comments`);
  }

  async createTaskJournalComment(id: number, text: string) {
    return this.request<TaskJournalComment>(`/tasks/${id}/comments`, {
      method: 'POST',
      body: JSON.stringify({ text }),
    });
  }

  async createTaskJournalDeadlineChangeRequest(id: number, payload: { requestedDueAt: string; reason?: string | null }) {
    return this.request<TaskJournalComment>(`/tasks/${id}/deadline-change-requests`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getTaskJournalFiles(id: number) {
    return this.request<TaskJournalFile[]>(`/tasks/${id}/files`);
  }

  async uploadTaskJournalFile(id: number, file: File, documentKey?: string) {
    const form = new FormData();
    form.append('file', file);
    if (documentKey) {
      form.append('documentKey', documentKey);
    }

    return this.request<TaskJournalFile>(`/tasks/${id}/files`, {
      method: 'POST',
      body: form,
    });
  }

  async downloadTaskJournalFile(taskId: number, fileId: number) {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/tasks/${taskId}/files/${fileId}/download`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')),
    };
  }

  async deleteTaskJournalFile(taskId: number, fileId: number) {
    return this.request<void>(`/tasks/${taskId}/files/${fileId}`, { method: 'DELETE' });
  }

  async deleteTaskJournalItem(id: number) {
    return this.request<void>(`/tasks/${id}`, { method: 'DELETE' });
  }

  // ===== Payments (back-compat + расширенные варианты) =====

  // Перегрузки — чтобы не ломать старые вызовы
  async getPayments(): Promise<Payment[]>;
  async getPayments(from?: string, to?: string, clientId?: number): Promise<Payment[]>;
  async getPayments(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    legalEntityId?: number;
    caseId?: number;
    clientRole?: ClientRole;
    excludeInstallmentParents?: boolean;
  }): Promise<Payment[]>;
  async getPayments(
    a?: string | { from?: string; to?: string; clientId?: number; legalEntityId?: number; caseId?: number; clientRole?: ClientRole; excludeInstallmentParents?: boolean },
    b?: string,
    c?: number,
  ): Promise<Payment[]> {
    let q = '';
    if (typeof a === 'object') {
      q = buildQuery({
        from: a?.from,
        to: a?.to,
        clientId: a?.clientId,
        legalEntityId: a?.legalEntityId,
        caseId: a?.caseId,
        clientRole: a?.clientRole,
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

  async updateInvoiceManagedPayment(id: number, payment: PaymentPayload) {
    return this.request<Payment>(`/payments/${id}/invoice-managed`, {
      method: 'PUT',
      body: JSON.stringify(payment),
    });
  }

  async updatePaymentStatus(id: number, status: PaymentStatus, paidDate?: string | null, reason?: string | null) {
    return this.request<Payment>(`/payments/${id}/status`, {
      method: 'POST',
      body: JSON.stringify({ status, paidDate, reason }),
    });
  }

  async transferPaymentRemainder(id: number, payment: PaymentPayload, remainderDate: string) {
    return this.request<TransferPaymentRemainderResponse>(`/payments/${id}/partial-remainder`, {
      method: 'PUT',
      body: JSON.stringify({
        payment,
        remainderDate,
      }),
    });
  }

  async getPartialPaymentRemainderCandidates(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    legalEntityId?: number;
    caseId?: number;
    clientRole?: ClientRole;
    type?: PaymentKind;
    includeFuture?: boolean;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PartialPaymentRemainderCandidatesResponse>(`/payments/partial-remainder-candidates${q}`);
  }

  async transferExistingPaymentRemainder(id: number, remainderDate: string) {
    return this.request<TransferPaymentRemainderResponse>(`/payments/${id}/partial-remainder/from-existing`, {
      method: 'POST',
      body: JSON.stringify({ remainderDate }),
    });
  }

  async rollbackPaymentRemainderTransfer(id: number) {
    return this.request<RollbackPaymentRemainderTransferResponse>(`/payments/${id}/partial-remainder/rollback`, {
      method: 'POST',
    });
  }

  async adjustPaymentRemainderTransfer(id: number, paidAmount: number, remainderDate: string) {
    return this.request<TransferPaymentRemainderResponse>(`/payments/${id}/partial-remainder/adjust`, {
      method: 'PUT',
      body: JSON.stringify({
        paidAmount,
        remainderDate,
      }),
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
    legalEntityId?: number;
    caseId?: number;
    clientRole?: ClientRole;
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
    legalEntityId?: number;
    caseId?: number;
    contractId?: number;
    invoiceId?: number;
    actId?: number;
    clientIds?: string;
    clientRole?: ClientRole;
    excludeInvoiceDocuments?: boolean;
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
  async getClientStats(id: number, params?: { caseId?: number; clientRole?: ClientRole }): Promise<ClientStats>;
  async getClientStats(id: number, params?: { caseId?: number; clientRole?: ClientRole }) {
    const q = buildQuery({ caseId: params?.caseId, clientRole: params?.clientRole });
    return this.request<ClientStats>(`/clients/${id}/stats${q}`);
  }

  async getClientCaseFinanceSummaries(id: number, params?: { clientRole?: ClientRole }) {
    const q = buildQuery(params ?? {});
    return this.request<ClientCaseFinanceSummary[]>(`/clients/${id}/case-finance-summaries${q}`);
  }

  async getLegalEntityCaseFinanceSummaries(id: number) {
    return this.request<ClientCaseFinanceSummary[]>(`/legal-entities/${id}/case-finance-summaries`);
  }

  async getClientBalance(id: number) {
    return this.request<ClientBalanceSummary>(`/clients/${id}/balance`);
  }

  async getClientBalanceTransactions(id: number) {
    return this.request<ClientBalanceTransaction[]>(`/clients/${id}/balance/transactions`);
  }

  async applyClientBalance(id: number, payload: { paymentId: number; amount: number; description?: string | null }) {
    return this.request<ApplyClientBalanceResult>(`/clients/${id}/balance/apply`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getClientCaseExpenseDetails(
    id: number,
    params?: { caseId?: number; clientRole?: ClientRole },
  ) {
    const q = buildQuery(params ?? {});
    return this.request<ClientCaseExpenseDetail[]>(`/clients/${id}/case-expense-details${q}`);
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
  async getLegalEntities(params?: { search?: string; counterpartyRole?: 'client' | 'supplier' }) {
    const q = buildQuery({
      search: params?.search,
      counterpartyRole: params?.counterpartyRole,
    });
    return this.request<LegalEntitySummary[]>(`/legal-entities${q}`);
  }

  async getLegalEntitiesPaged(params?: { search?: string; page?: number; pageSize?: number; counterpartyRole?: 'client' | 'supplier' }) {
    const q = buildQuery({
      search: params?.search,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
      counterpartyRole: params?.counterpartyRole,
    });
    return this.request<PagedResult<LegalEntitySummary>>(`/legal-entities/paged${q}`);
  }

  async getLegalEntity(id: number) {
    return this.request<LegalEntityDetail>(`/legal-entities/${id}`);
  }

  async getCompanyProfileLegalEntity() {
    return this.request<LegalEntityDetail>('/legal-entities/company-profile');
  }

  async getMainCompanySettings() {
    return this.request<MainCompanySettingsDto>('/legal-entities/main-companies/settings');
  }

  async updateMainCompanySettings(payload: UpdateMainCompanySettingsRequest) {
    return this.request<MainCompanySettingsDto>('/legal-entities/main-companies/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async setCompanyProfileLegalEntity(id: number) {
    return this.request<LegalEntityDetail>(`/legal-entities/company-profile/main/${id}`, {
      method: 'PUT',
    });
  }

  async getBanks(params?: { search?: string }) {
    const q = buildQuery({ search: params?.search });
    return this.request<BankDto[]>(`/banks${q}`);
  }

  async getBank(id: number) {
    return this.request<BankDto>(`/banks/${id}`);
  }

  async createBank(payload: BankUpsertRequest) {
    return this.request<BankDto>('/banks', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateBank(id: number, payload: BankUpsertRequest) {
    return this.request<BankDto>(`/banks/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteBank(id: number) {
    return this.request<void>(`/banks/${id}`, { method: 'DELETE' });
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

  async getSimpleDictionaryDuplicates(kind: 'countries' | 'base-currencies' | 'service-languages' | 'service-categories' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes' | 'invoice-units' | 'invoice-item-names') {
    return this.request<SimpleDictionaryDuplicateGroup[]>(`/dictionaries/simple/${kind}/duplicates`);
  }

  async mergeSimpleDictionaryDuplicates(
    kind: 'countries' | 'base-currencies' | 'service-languages' | 'service-categories' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes' | 'invoice-units' | 'invoice-item-names',
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
    counterpartyRole?: 'client' | 'supplier';
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
  async getCases(params?: { clientId?: number; legalEntityId?: number }): Promise<ClientCase[]>;
  async getCases(a?: number | { clientId?: number; legalEntityId?: number }) {
    let q = '';
    if (typeof a === 'object') {
      q = buildQuery({ clientId: a?.clientId, legalEntityId: a?.legalEntityId });
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

  async createCaseV2(model: Omit<ClientCase, 'id' | 'createdAt' | 'payments'>) {
    return this.request<ClientCase>('/v2/cases', { method: 'POST', body: JSON.stringify(model) });
  }

  async getCaseV2(id: number) {
    return this.request<ClientCase>(`/v2/cases/${id}`);
  }

  async updateCaseV2(id: number, model: Omit<ClientCase, 'id' | 'createdAt' | 'payments'>) {
    return this.request<ClientCase>(`/v2/cases/${id}`, { method: 'PUT', body: JSON.stringify(model) });
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
    legalEntityId?: number;
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
    legalEntityId?: number;
    status?: string;
    search?: string;
    responsibleUserId?: number;
    caseType?: string;
    priority?: string;
    from?: string;
    to?: string;
    minFee?: number;
    maxFee?: number;
    scope?: 'all' | 'active' | 'archive' | 'mine' | string;
    activity?: 'all' | 'active' | 'inactive' | string;
    inactiveDays?: number;
    sortBy?:
      | 'createdAt'
      | 'title'
      | 'status'
      | 'caseNumber'
      | 'client'
      | 'caseType'
      | 'stage'
      | 'responsible'
      | 'startDate'
      | 'dueDate'
      | 'feeAmount'
      | 'priority'
      | 'court'
      | 'nextHearingAt'
      | 'lastViewedAt';
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

  async exportCasesRegistryCsv(params?: CasesRegistryExportParams) {
    const q = buildQuery(params ?? {});
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/v2/cases/export.csv${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/v2/cases/export.csv ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'cases-registry.csv',
    };
  }

  async exportCasesRegistryExcel(params?: CasesRegistryExportParams) {
    const q = buildQuery(params ?? {});
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/v2/cases/export.xls${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/v2/cases/export.xls ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'cases-registry.xls',
    };
  }

  async exportCasesRegistryPdf(params?: CasesRegistryExportParams) {
    const q = buildQuery(params ?? {});
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/v2/cases/export.pdf${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/v2/cases/export.pdf ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'cases-registry.pdf',
    };
  }

  async getCasesRegistryPrintHtml(params?: CasesRegistryExportParams) {
    const q = buildQuery(params ?? {});
    return this.request<string>(`/v2/cases/print${q}`);
  }

  async bulkUpdateCases(payload: {
    ids: number[];
    status?: string;
    responsibleUserId?: number;
    stage?: string;
    priority?: string;
    isHiddenFromExecutor?: boolean;
    inactivityRemindersEnabled?: boolean;
  }) {
    return this.request<{ updated: number; requested: number }>('/v2/cases/bulk-update', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async requestCaseAccess(caseId: number, payload?: { reason?: string }) {
    return this.request<{
      clientCaseId: number;
      requestedAt: string;
      requestedByUserId: number;
      requestedByUserName: string;
      reason: string;
      isHiddenFromExecutor: boolean;
    }>(`/v2/cases/${caseId}/access-request`, {
      method: 'POST',
      body: JSON.stringify(payload ?? {}),
    });
  }

  async getCaseAccessRequests() {
    return this.request<ClientCaseAccessRequest[]>('/v2/cases/access-requests');
  }

  async resolveCaseAccessRequest(caseId: number, payload: { grant: boolean; comment?: string }) {
    return this.request<ClientCaseAccessRequestResolution>(`/v2/cases/${caseId}/access-request/resolve`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async markCaseActivity(caseId: number, payload?: { confirmed?: boolean }) {
    return this.request<ClientCaseActivity>(`/v2/cases/${caseId}/activity`, {
      method: 'POST',
      body: JSON.stringify(payload ?? {}),
    });
  }

  async addCaseHistoryEntry(caseId: number, payload: { text: string }) {
    return this.request<ClientCaseActivity>(`/v2/cases/${caseId}/history`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getCaseTimeline(caseId: number) {
    return this.request<ClientCaseTimelineEntry[]>(`/v2/cases/${caseId}/timeline`);
  }

  async getCaseConversation(caseId: number) {
    return this.request<ClientCaseConversationEntry[]>(`/v2/cases/${caseId}/conversation`);
  }

  async addCaseConversationEntry(caseId: number, payload: { text: string }) {
    return this.request<ClientCaseConversationEntry[]>(`/v2/cases/${caseId}/conversation`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getCaseSummary(caseId: number) {
    return this.request<ClientCaseSummary>(`/v2/cases/${caseId}/summary`);
  }

  async getCaseFinance(caseId: number) {
    return this.request<ClientCaseFinanceDetail>(`/v2/cases/${caseId}/finance`);
  }

  async getCaseWorkOrder(caseId: number) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order`);
  }

  async createCaseWorkOrder(caseId: number, payload: {
    title?: string;
    status?: string;
    currency?: string;
    notes?: string;
  }) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateCaseWorkOrder(caseId: number, payload: {
    title?: string;
    status?: string;
    currency?: string;
    notes?: string;
  }) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order`, {
      method: 'PATCH',
      body: JSON.stringify(payload),
    });
  }

  async deleteCaseWorkOrder(caseId: number) {
    return this.request<void>(`/v2/cases/${caseId}/work-order`, { method: 'DELETE' });
  }

  async addCaseWorkOrderItem(caseId: number, payload: {
    serviceName: string;
    description?: string;
    executorUserId?: number | null;
    executorName?: string;
    dueDate?: string | null;
    quantity?: number;
    unitPrice?: number;
  }) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order/items`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateCaseWorkOrderItem(caseId: number, itemId: number, payload: {
    serviceName?: string;
    description?: string;
    executorUserId?: number | null;
    executorName?: string;
    dueDate?: string | null;
    quantity?: number;
    unitPrice?: number;
    sortOrder?: number;
  }) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order/items/${itemId}`, {
      method: 'PATCH',
      body: JSON.stringify(payload),
    });
  }

  async deleteCaseWorkOrderItem(caseId: number, itemId: number) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order/items/${itemId}`, { method: 'DELETE' });
  }

  async createCaseWorkOrderInvoice(caseId: number, payload: {
    number?: string;
    date?: string | null;
    dueDate?: string | null;
    description?: string;
  } = {}) {
    return this.request<ClientCaseWorkOrderInvoice>(`/v2/cases/${caseId}/work-order/create-invoice`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async submitCaseWorkOrderApproval(caseId: number, payload: { comment?: string } = {}) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order/submit-approval`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async approveCaseWorkOrder(caseId: number, payload: { comment?: string } = {}) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order/approve`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async requestCaseWorkOrderRevision(caseId: number, payload: { comment?: string } = {}) {
    return this.request<ClientCaseWorkOrder>(`/v2/cases/${caseId}/work-order/request-revision`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getCaseWorkOrderPrintHtml(caseId: number) {
    return this.request<string>(`/v2/cases/${caseId}/work-order/print`);
  }

  async generateCaseWorkOrderDocument(caseId: number) {
    return this.request<ClientCaseDocument>(`/v2/cases/${caseId}/work-order/generate-document`, {
      method: 'POST',
    });
  }

  async runCaseCourtSync(caseId: number, payload?: ClientCaseCourtSyncRunRequest) {
    return this.request<ClientCaseCourtSyncRun>(`/v2/cases/${caseId}/court-sync/run`, {
      method: 'POST',
      ...(payload ? { body: JSON.stringify(payload) } : {}),
    });
  }

  async getCaseCourtSyncLogs(caseId: number) {
    return this.request<ClientCaseCourtSyncLog[]>(`/v2/cases/${caseId}/court-sync/logs`);
  }

  async getCourtSyncJournal(params: { take?: number; status?: string; search?: string } = {}) {
    const query = new URLSearchParams();
    if (params.take) query.set('take', String(params.take));
    if (params.status) query.set('status', params.status);
    if (params.search) query.set('search', params.search);
    const suffix = query.toString() ? `?${query.toString()}` : '';
    return this.request<ClientCaseCourtSyncJournalEntry[]>(`/v2/cases/court-sync/logs${suffix}`);
  }

  async getCourtSyncSettings() {
    return this.request<ClientCaseCourtSyncSettings>('/v2/cases/court-sync/settings');
  }

  async getCourtSyncProviders() {
    return this.request<ClientCaseCourtSyncProvider[]>('/v2/cases/court-sync/providers');
  }

  async updateCourtSyncSettings(payload: Pick<ClientCaseCourtSyncSettings, 'isEnabled' | 'intervalMinutes' | 'batchSize'>) {
    return this.request<ClientCaseCourtSyncSettings>('/v2/cases/court-sync/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getCaseCourtDirectory(includeInactive = false) {
    const query = includeInactive ? '?includeInactive=true' : '';
    return this.request<ClientCaseCourt[]>(`/v2/cases/court-directory${query}`);
  }

  async createCaseCourtDirectoryItem(payload: UpsertClientCaseCourtPayload) {
    return this.request<ClientCaseCourt>('/v2/cases/court-directory', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateCaseCourtDirectoryItem(courtId: number, payload: UpsertClientCaseCourtPayload) {
    return this.request<ClientCaseCourt>(`/v2/cases/court-directory/${courtId}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getCaseCourtInstances(caseId: number) {
    return this.request<ClientCaseCourtInstance[]>(`/v2/cases/${caseId}/court-instances`);
  }

  async updateCaseCourtInstance(caseId: number, instance: string, payload: UpsertClientCaseCourtInstancePayload) {
    return this.request<ClientCaseCourtInstance>(`/v2/cases/${caseId}/court-instances/${encodeURIComponent(instance)}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async createCaseCourtComplaint(caseId: number, instance: string, payload: UpsertClientCaseCourtComplaintPayload) {
    return this.request<ClientCaseCourtComplaint>(`/v2/cases/${caseId}/court-instances/${encodeURIComponent(instance)}/complaints`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateCaseCourtComplaint(caseId: number, instance: string, complaintId: number, payload: UpsertClientCaseCourtComplaintPayload) {
    return this.request<ClientCaseCourtComplaint>(`/v2/cases/${caseId}/court-instances/${encodeURIComponent(instance)}/complaints/${complaintId}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async archiveCaseCourtComplaint(caseId: number, instance: string, complaintId: number) {
    return this.request<void>(`/v2/cases/${caseId}/court-instances/${encodeURIComponent(instance)}/complaints/${complaintId}`, { method: 'DELETE' });
  }

  async getCaseCourtEvents(caseId: number) {
    return this.request<ClientCaseCourtEvent[]>(`/v2/cases/${caseId}/court-events`);
  }

  async createCaseCourtEvent(caseId: number, payload: UpsertClientCaseCourtEventPayload) {
    return this.request<ClientCaseCourtEvent>(`/v2/cases/${caseId}/court-events`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateCaseCourtEvent(caseId: number, courtEventId: number, payload: UpsertClientCaseCourtEventPayload) {
    return this.request<ClientCaseCourtEvent>(`/v2/cases/${caseId}/court-events/${courtEventId}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getCaseCourtEventHistory(caseId: number, courtEventId: number) {
    return this.request<ClientCaseCourtEventHistory[]>(`/v2/cases/${caseId}/court-events/${courtEventId}/history`);
  }

  async getCaseCourtParties(caseId: number, includeArchived = false) {
    const query = includeArchived ? '?includeArchived=true' : '';
    return this.request<ClientCaseCourtParty[]>(`/v2/cases/${caseId}/court-parties${query}`);
  }

  async createCaseCourtParty(caseId: number, payload: UpsertClientCaseCourtPartyPayload) {
    return this.request<ClientCaseCourtParty>(`/v2/cases/${caseId}/court-parties`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async archiveCaseCourtParty(caseId: number, courtPartyId: number) {
    return this.request<void>(`/v2/cases/${caseId}/court-parties/${courtPartyId}`, { method: 'DELETE' });
  }

  async getCaseRelatedParties(caseId: number, includeArchived = false) {
    const query = includeArchived ? '?includeArchived=true' : '';
    return this.request<ClientCaseRelatedParty[]>(`/v2/cases/${caseId}/related-parties${query}`);
  }

  async addCaseRelatedParty(caseId: number, payload: { sourceType: string; sourceId: number; role?: string; notes?: string }) {
    return this.request<ClientCaseRelatedParty>(`/v2/cases/${caseId}/related-parties`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async deleteCaseRelatedParty(caseId: number, relatedPartyId: number) {
    return this.request<void>(`/v2/cases/${caseId}/related-parties/${relatedPartyId}`, { method: 'DELETE' });
  }

  async getCaseDocuments(caseId: number) {
    return this.request<ClientCaseDocument[]>(`/v2/cases/${caseId}/documents`);
  }

  async getCaseDocumentRegistry(params?: {
    clientCaseId?: number;
    search?: string;
    documentType?: string;
    status?: string;
    sourceTaskId?: number;
    sourceEventId?: number;
    sourceWorkOrderId?: number;
    sourceInvoiceId?: number;
    sourceStage?: string;
    latestOnly?: boolean;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      clientCaseId: params?.clientCaseId,
      search: params?.search,
      documentType: params?.documentType,
      status: params?.status,
      sourceTaskId: params?.sourceTaskId,
      sourceEventId: params?.sourceEventId,
      sourceWorkOrderId: params?.sourceWorkOrderId,
      sourceInvoiceId: params?.sourceInvoiceId,
      sourceStage: params?.sourceStage,
      latestOnly: params?.latestOnly,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PagedResult<ClientCaseDocument>>(`/case-documents${q}`);
  }

  async getCaseDocumentTemplates() {
    return this.request<ClientCaseDocumentTemplate[]>('/v2/cases/document-templates');
  }

  async getCaseDocumentTemplateTokens() {
    return this.request<ClientCaseDocumentTemplateToken[]>('/v2/cases/document-template-tokens');
  }

  async createCaseDocumentTemplate(payload: Partial<ClientCaseDocumentTemplate> & { key?: string; label?: string; name?: string; content?: string; html?: string }) {
    return this.request<ClientCaseDocumentTemplate>('/document-templates', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateCaseDocumentTemplate(templateKey: string, payload: Partial<ClientCaseDocumentTemplate> & { label?: string; name?: string; content?: string; html?: string }) {
    return this.request<ClientCaseDocumentTemplate>(`/document-templates/${encodeURIComponent(templateKey)}`, {
      method: 'PATCH',
      body: JSON.stringify(payload),
    });
  }

  async renderCaseDocumentTemplate(templateKey: string, payload: { clientCaseId: number; documentType?: string }) {
    return this.request<RenderedCaseDocumentTemplate>(`/document-templates/${encodeURIComponent(templateKey)}/render`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getCaseDocumentTypes() {
    return this.request<string[]>('/v2/cases/document-types');
  }

  async getCaseCalendarEvents(params?: {
    from?: string;
    to?: string;
    clientCaseId?: number;
    responsibleUserId?: number;
    includeTasks?: boolean;
    reminderMinutes?: number[];
  }) {
    const q = buildQuery({
      from: params?.from,
      to: params?.to,
      clientCaseId: params?.clientCaseId,
      responsibleUserId: params?.responsibleUserId,
      includeTasks: params?.includeTasks,
      reminderMinutes: params?.reminderMinutes,
    });
    return this.request<CaseCalendarEvent[]>(`/case-calendar/events${q}`);
  }

  async getCaseCalendarSummary(params?: {
    from?: string;
    to?: string;
    clientCaseId?: number;
    responsibleUserId?: number;
    includeTasks?: boolean;
  }) {
    const q = buildQuery({
      from: params?.from,
      to: params?.to,
      clientCaseId: params?.clientCaseId,
      responsibleUserId: params?.responsibleUserId,
      includeTasks: params?.includeTasks,
    });
    return this.request<CaseCalendarSummary>(`/case-calendar/summary${q}`);
  }

  async downloadCaseCalendarIcs(params?: {
    from?: string;
    to?: string;
    clientCaseId?: number;
    responsibleUserId?: number;
    includeTasks?: boolean;
    reminderMinutes?: number[];
  }) {
    const q = buildQuery({
      from: params?.from,
      to: params?.to,
      clientCaseId: params?.clientCaseId,
      responsibleUserId: params?.responsibleUserId,
      includeTasks: params?.includeTasks,
      reminderMinutes: params?.reminderMinutes,
    });
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/case-calendar/events.ics${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')) || 'case-calendar.ics',
    };
  }

  async getCaseProceduralDeadlines(caseId: number) {
    return this.request<ClientCaseProceduralDeadline[]>(`/v2/cases/${caseId}/procedural-deadlines`);
  }

  async createCaseProceduralDeadline(caseId: number, payload: UpsertClientCaseProceduralDeadlinePayload) {
    return this.request<ClientCaseProceduralDeadline>(`/v2/cases/${caseId}/procedural-deadlines`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateCaseProceduralDeadline(caseId: number, deadlineId: number, payload: UpsertClientCaseProceduralDeadlinePayload) {
    return this.request<ClientCaseProceduralDeadline>(`/v2/cases/${caseId}/procedural-deadlines/${deadlineId}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async closeCaseProceduralDeadline(caseId: number, deadlineId: number, reason?: string) {
    return this.request<ClientCaseProceduralDeadline>(`/v2/cases/${caseId}/procedural-deadlines/${deadlineId}/close`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  async calculateCaseProceduralDeadline(payload: ProceduralDeadlineCalculationPayload) {
    return this.request<ProceduralDeadlineCalculationResult>('/v2/cases/procedural-deadlines/calculate', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getProceduralDeadlineCalendarSettings() {
    return this.request<ProceduralDeadlineCalendarSettings>('/v2/cases/procedural-deadlines/calendar-settings');
  }

  async updateProceduralDeadlineCalendarSettings(payload: ProceduralDeadlineCalendarSettings) {
    return this.request<ProceduralDeadlineCalendarSettings>('/v2/cases/procedural-deadlines/calendar-settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getCaseDocument(documentId: number) {
    return this.request<ClientCaseDocument>(`/case-documents/${documentId}`);
  }

  async uploadCaseDocument(caseId: number, file: File, payload?: { documentType?: string; status?: string; documentKey?: string }) {
    const form = new FormData();
    form.append('file', file);
    if (payload?.documentType) {
      form.append('documentType', payload.documentType);
    }
    if (payload?.status) {
      form.append('status', payload.status);
    }
    if (payload?.documentKey) {
      form.append('documentKey', payload.documentKey);
    }

    return this.request<ClientCaseDocument>(`/v2/cases/${caseId}/documents`, {
      method: 'POST',
      body: form,
    });
  }

  async uploadCaseDocumentVersion(documentId: number, file: File, payload?: { documentType?: string; status?: string }) {
    const form = new FormData();
    form.append('file', file);
    if (payload?.documentType) {
      form.append('documentType', payload.documentType);
    }
    if (payload?.status) {
      form.append('status', payload.status);
    }

    return this.request<ClientCaseDocument>(`/case-documents/${documentId}/versions`, {
      method: 'POST',
      body: form,
    });
  }

  async generateCaseDocumentTemplate(caseId: number, payload: { templateKey?: string; documentType?: string; status?: string }) {
    return this.request<ClientCaseDocument>(`/v2/cases/${caseId}/documents/generate-template`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async downloadCaseDocument(caseId: number, documentId: number) {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/v2/cases/${caseId}/documents/${documentId}/download`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')),
    };
  }

  async downloadCaseDocumentById(documentId: number) {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/case-documents/${documentId}/download`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')),
    };
  }

  async updateCaseDocument(caseId: number, documentId: number, payload: { documentType?: string; status?: string }) {
    return this.request<ClientCaseDocument>(`/v2/cases/${caseId}/documents/${documentId}`, {
      method: 'PATCH',
      body: JSON.stringify(payload),
    });
  }

  async updateCaseDocumentById(documentId: number, payload: { documentType?: string; status?: string }) {
    return this.request<ClientCaseDocument>(`/case-documents/${documentId}`, {
      method: 'PATCH',
      body: JSON.stringify(payload),
    });
  }

  async deleteCaseDocument(caseId: number, documentId: number) {
    return this.request<void>(`/v2/cases/${caseId}/documents/${documentId}`, { method: 'DELETE' });
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
  async getDocumentStatuses() {
    return this.getDict('document-statuses');
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
    clientRole?: ClientRole;
    q?: string;
    r?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<SummaryStats>(`/v2/stats/summary${q}`);
  }

  async getCashFlowReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<CashFlowReport>(`/reports/cash-flow${q}`);
  }

  async getMoneyCategoryReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<MoneyCategoryReport>(`/reports/categories${q}`);
  }

  async getCounterpartyReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<CounterpartyReport>(`/reports/counterparties${q}`);
  }

  async getProjectReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<ProjectReport>(`/reports/projects${q}`);
  }

  async getLegalWorkReport(params?: {
    from?: string;
    to?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<LegalWorkReport>(`/reports/legal-work${q}`);
  }

  async downloadLegalWorkReport(params?: {
    format?: 'csv' | 'excel' | 'pdf';
    from?: string;
    to?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
  }) {
    const { format = 'csv', ...filters } = params ?? {};
    const extension = format === 'excel' ? 'xls' : format;
    const q = buildQuery(filters);
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/reports/legal-work.${extension}${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/reports/legal-work.${extension} ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        `legal-work-report.${extension}`,
    };
  }

  async downloadManagementReport(params: {
    report: 'case-finance' | 'case-success' | 'current-cases' | 'lawyer-workload' | 'overdue-deadlines' | 'case-activity';
    format: 'excel' | 'pdf';
    from?: string;
    to?: string;
    asOf?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
    lawyerUserId?: number;
    horizonDays?: number;
    deadlineType?: string;
    activity?: 'all' | 'inactive' | 'active' | 'escalated' | 'disabled';
    inactiveDays?: number;
    escalationDays?: number;
    limit?: number;
  }) {
    const { format, ...filters } = params;
    const extension = format === 'excel' ? 'xls' : 'pdf';
    const q = buildQuery(filters);
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/reports/management.${extension}${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/reports/management.${extension} ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        `${filters.report}-report.${extension}`,
    };
  }

  async getCaseActivityReport(params?: {
    inactiveDays?: number;
    escalationDays?: number;
    activity?: 'all' | 'inactive' | 'active' | 'escalated' | 'disabled';
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<CaseActivityReport>(`/reports/case-activity${q}`);
  }

  async getCurrentCasesReport(params?: {
    asOf?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<CurrentCasesReport>(`/reports/current-cases${q}`);
  }

  async getCaseFinanceReport(params?: {
    from?: string;
    to?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<CaseFinanceReport>(`/reports/case-finance${q}`);
  }

  async getCaseSuccessReport(params?: {
    from?: string;
    to?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<CaseSuccessReport>(`/reports/case-success${q}`);
  }

  async getLawyerWorkloadReport(params?: {
    asOf?: string;
    horizonDays?: number;
    lawyerUserId?: number;
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<LawyerWorkloadReport>(`/reports/lawyer-workload${q}`);
  }

  async getOverdueProceduralDeadlinesReport(params?: {
    asOf?: string;
    deadlineType?: string;
    responsibleUserId?: number;
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<OverdueProceduralDeadlinesReport>(`/reports/overdue-deadlines${q}`);
  }

  async getTaskReport(params?: {
    asOf?: string;
    scope?: 'open' | 'closed' | 'all';
    assigneeUserId?: number;
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<TaskReport>(`/reports/tasks${q}`);
  }

  async getDirectionReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<DirectionReport>(`/reports/directions${q}`);
  }

  async getSalesPointReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<SalesPointReport>(`/reports/sales-points${q}`);
  }

  async getVatReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<VatReport>(`/reports/vat${q}`);
  }

  async getPlanFactReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PlanFactReport>(`/reports/plan-fact${q}`);
  }

  async getAdvanceReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<AdvanceReport>(`/reports/advances${q}`);
  }

  async getPaymentPriorityReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PaymentPriorityReport>(`/reports/payment-priorities${q}`);
  }

  async getPaidAccountReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PaidAccountReport>(`/reports/paid-accounts${q}`);
  }

  async getPaidInvoiceReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PaidInvoiceReport>(`/reports/paid-invoices${q}`);
  }

  async getInvoiceStatusReport(params?: {
    from?: string;
    to?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<InvoiceStatusReport>(`/reports/invoice-statuses${q}`);
  }

  async getActStatusReport(params?: {
    from?: string;
    to?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<ActStatusReport>(`/reports/act-statuses${q}`);
  }

  async getContractStatusReport(params?: {
    from?: string;
    to?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<ContractStatusReport>(`/reports/contract-statuses${q}`);
  }

  async getPaymentOrderReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PaymentOrderReport>(`/reports/payment-orders${q}`);
  }

  async getPaymentPurposeReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PaymentPurposeReport>(`/reports/payment-purposes${q}`);
  }

  async getMatchStatusReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<MatchStatusReport>(`/reports/match-statuses${q}`);
  }

  async getJournalStatusReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<JournalStatusReport>(`/reports/journal-statuses${q}`);
  }

  async getCurrencyReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<CurrencyReport>(`/reports/currencies${q}`);
  }

  async getCashOrderReport(params?: {
    from?: string;
    to?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<CashOrderReport>(`/reports/cash-orders${q}`);
  }

  async getBankStatementImportReport(params?: {
    from?: string;
    to?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<BankStatementImportReport>(`/reports/bank-statement-imports${q}`);
  }

  async getSpreadsheetImportReport(params?: {
    from?: string;
    to?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<SpreadsheetImportReport>(`/reports/spreadsheet-imports${q}`);
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
      clientRole?: ClientRole;
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
      clientRole?: ClientRole;
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
    moneyAccountId?: number;
    accountName?: string;
    categoryName?: string;
    linkedInvoiceId?: number;
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
    moneyAccountId?: number;
    accountName?: string;
    categoryName?: string;
    linkedInvoiceId?: number;
    search?: string;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PagedResult<MoneyOperation> & { summary: MoneyOperationsSummary }>(`/money-operations/paged${q}`);
  }

  async exportMoneyOperationsCsv(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    direction?: MoneyOperationDirection;
    accountingType?: MoneyOperationAccountingType;
    moneyAccountId?: number;
    accountName?: string;
    categoryName?: string;
    linkedInvoiceId?: number;
    search?: string;
  }) {
    const q = buildQuery(params ?? {});
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/money-operations/export.csv${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/money-operations/export.csv ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'money-operations.csv',
    };
  }

  async exportMoneyOperationsPdf(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    direction?: MoneyOperationDirection;
    accountingType?: MoneyOperationAccountingType;
    moneyAccountId?: number;
    accountName?: string;
    categoryName?: string;
    linkedInvoiceId?: number;
    search?: string;
  }) {
    const q = buildQuery(params ?? {});
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/money-operations/export.pdf${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/money-operations/export.pdf ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'money-operations.pdf',
    };
  }

  async exportMoneyOperationsExcel(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    direction?: MoneyOperationDirection;
    accountingType?: MoneyOperationAccountingType;
    moneyAccountId?: number;
    accountName?: string;
    categoryName?: string;
    linkedInvoiceId?: number;
    search?: string;
  }) {
    const q = buildQuery(params ?? {});
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/money-operations/export.xls${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/money-operations/export.xls ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'money-operations.xls',
    };
  }

  async getMoneyOperationsPrintHtml(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    direction?: MoneyOperationDirection;
    accountingType?: MoneyOperationAccountingType;
    moneyAccountId?: number;
    accountName?: string;
    categoryName?: string;
    linkedInvoiceId?: number;
    search?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<string>(`/money-operations/print${q}`);
  }

  async getMoneyOperationAccounts(params?: { source?: MoneyOperationSource }) {
    const q = buildQuery(params ?? {});
    return this.request<string[]>(`/money-operations/accounts${q}`);
  }

  async getMoneyAccounts(params?: { source?: MoneyOperationSource; activeOnly?: boolean }) {
    const q = buildQuery(params ?? {});
    return this.request<MoneyAccount[]>(`/money-accounts${q}`);
  }

  async createMoneyAccount(payload: MoneyAccount) {
    return this.request<MoneyAccount>('/money-accounts', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateMoneyAccount(id: number, payload: MoneyAccount) {
    return this.request<MoneyAccount>(`/money-accounts/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteMoneyAccount(id: number) {
    return this.request<void>(`/money-accounts/${id}`, { method: 'DELETE' });
  }

  async getMoneyOperationAccountSummaries(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<MoneyOperationAccountSummary[]>(`/money-operations/account-summaries${q}`);
  }

  async getMoneyOperationDailyBalances(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<MoneyOperationDailyBalance[]>(`/money-operations/daily-balances${q}`);
  }

  async getMoneyBalanceReconciliationResults(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<MoneyBalanceReconciliationResult[]>(`/money-operations/balance-reconciliation${q}`);
  }

  async runMoneyBalanceReconciliation(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<MoneyBalanceReconciliationRunResult>(`/money-operations/balance-reconciliation/run${q}`, {
      method: 'POST',
    });
  }

  async getMoneyOperation(id: number) {
    return this.request<MoneyOperation>(`/money-operations/${id}`);
  }

  async getMoneyOperationInvoiceCandidates(id: number) {
    return this.request<MoneyOperationInvoiceCandidate[]>(`/money-operations/${id}/invoice-candidates`);
  }

  async createMoneyOperation(payload: MoneyOperation) {
    return this.request<MoneyOperation>('/money-operations', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async createMoneyOperationTransfer(payload: MoneyOperationTransferRequest) {
    return this.request<MoneyOperationTransferResponse>('/money-operations/transfer', {
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

  async ignoreMoneyOperation(id: number) {
    return this.request<MoneyOperation>(`/money-operations/${id}/ignore`, { method: 'POST' });
  }

  async linkMoneyOperationInvoice(id: number, payload: { invoiceId: number; comment?: string | null }) {
    return this.request<MoneyOperation>(`/money-operations/${id}/link-invoice`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async deleteMoneyOperation(id: number) {
    return this.request<void>(`/money-operations/${id}`, { method: 'DELETE' });
  }

  async importBankStatement(file: File) {
    const payload = new FormData();
    payload.append('file', file);
    return this.request<BankStatementImportResult>('/money-operations/bank-statement/import', {
      method: 'POST',
      body: payload,
    });
  }

  async runBankStatementAutoImport() {
    return this.request<BankStatementAutoImportRunResult>('/money-operations/bank-statement/auto-import/run', {
      method: 'POST',
    });
  }

  async getBankStatementImports(params?: { page?: number; pageSize?: number }) {
    const q = buildQuery(params ?? {});
    return this.request<PagedResult<BankStatementImportBatch>>(`/money-operations/bank-statement/imports${q}`);
  }

  async getBankStatementImport(id: number) {
    return this.request<BankStatementImportBatchDetail>(`/money-operations/bank-statement/imports/${id}`);
  }

  async rollbackBankStatementImport(id: number) {
    return this.request<BankStatementImportRollbackResult>(`/money-operations/bank-statement/imports/${id}/rollback`, {
      method: 'POST',
    });
  }

  async reprocessBankStatementImport(id: number) {
    return this.request<BankStatementImportResult>(`/money-operations/bank-statement/imports/${id}/reprocess`, {
      method: 'POST',
    });
  }

  async createCashOrderFromPayment(paymentId: number) {
    return this.request<CashOrder>(`/cash-orders/from-payment/${paymentId}`, { method: 'POST' });
  }

  async createCashOrderFromMoneyOperation(operationId: number) {
    return this.request<CashOrder>(`/cash-orders/from-money-operation/${operationId}`, { method: 'POST' });
  }

  async getNextCashOrderNumber(type: CashOrder['type'], date: string) {
    const q = buildQuery({ type, date });
    return this.request<NextCashOrderNumber>(`/cash-orders/next-number${q}`);
  }

  async createCashOrder(payload: CashOrder) {
    return this.request<CashOrder>('/cash-orders', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getCashOrdersPaged(params?: {
    from?: string;
    to?: string;
    type?: 'Incoming' | 'Outgoing';
    status?: 'Draft' | 'Posted' | 'Cancelled';
    search?: string;
    accountName?: string;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 25,
    });
    return this.request<PagedResult<CashOrder>>(`/cash-orders/paged${q}`);
  }

  async getCashBook(params?: { from?: string; to?: string; accountName?: string }) {
    const q = buildQuery(params ?? {});
    return this.request<CashBookSummary>(`/cash-orders/cashbook${q}`);
  }

  async getCashBookPrintHtml(params?: { from?: string; to?: string; accountName?: string }) {
    const q = buildQuery(params ?? {});
    return this.request<string>(`/cash-orders/cashbook/print${q}`);
  }

  async getCashOrderRegister(params?: {
    from?: string;
    to?: string;
    type?: 'Incoming' | 'Outgoing';
    status?: 'Draft' | 'Posted' | 'Cancelled';
    search?: string;
    accountName?: string;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 25,
    });
    return this.request<CashOrderRegister>(`/cash-orders/register${q}`);
  }

  async getCashOrderRegisterPrintHtml(params?: {
    from?: string;
    to?: string;
    type?: 'Incoming' | 'Outgoing';
    status?: 'Draft' | 'Posted' | 'Cancelled';
    search?: string;
    accountName?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<string>(`/cash-orders/register/print${q}`);
  }

  async getCashInventories(params?: {
    from?: string;
    to?: string;
    accountName?: string;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 10,
    });
    return this.request<PagedResult<CashInventory>>(`/cash-orders/inventories${q}`);
  }

  async createCashInventory(payload: CashInventoryCreateRequest) {
    return this.request<CashInventory>('/cash-orders/inventories', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getCashOrder(id: number) {
    return this.request<CashOrder>(`/cash-orders/${id}`);
  }

  async updateCashOrder(id: number, payload: CashOrder) {
    return this.request<CashOrder>(`/cash-orders/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async cancelCashOrder(id: number, reason?: string) {
    return this.request<CashOrder>(`/cash-orders/${id}/cancel`, {
      method: 'POST',
      body: reason ? JSON.stringify({ reason }) : undefined,
    });
  }

  async postCashOrder(id: number) {
    return this.request<CashOrder>(`/cash-orders/${id}/post`, { method: 'POST' });
  }

  async deleteCashOrder(id: number) {
    return this.request<void>(`/cash-orders/${id}`, { method: 'DELETE' });
  }

  async copyCashOrder(id: number) {
    return this.request<CashOrder>(`/cash-orders/${id}/copy`, { method: 'POST' });
  }

  async getCashOrderPrintHtml(id: number) {
    return this.request<string>(`/cash-orders/${id}/print`);
  }

  async downloadCashOrderPdf(id: number) {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/cash-orders/${id}/pdf`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return response.blob();
  }

  getCashOrderPrintUrl(id: number) {
    return `${API_BASE_URL}/cash-orders/${id}/print`;
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

  async getUsersOnline() {
    return this.request<UserOnlineResponse>('/user-activity/online');
  }

  async sendUserActivityHeartbeat() {
    return this.request<void>('/user-activity/heartbeat', {
      method: 'POST',
    });
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
  PayablesDashboard,
  Act,
  ActInput,
  ActResponsible,
  ActStatus,
  ActWorkflowStatus,
  ActsSummary,
  Contract,
  ContractInput,
  ContractStatus,
  ContractTerminationCheck,
  ClientRole,
  Payment,
  PaymentKind,
  PaymentPayload,
  Invoice,
  InvoiceClientRole,
  InvoiceInput,
  InvoiceWorkflowStatus,
  InvoiceSummary,
  PaymentStatus,
  Client,
  ClientInput,
  ClientCaseFinanceSummary,
  ClientCaseExpenseDetail,
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
  ServiceCategoryDictionaryItem,
  PaymentStatusEntity,
  DocumentStatusEntity,
  CaseCalendarEvent,
  CaseCalendarSummary,
  ClientCaseCourt,
  ClientCaseCourtComplaint,
  ClientCaseCourtEvent,
  ClientCaseCourtEventHistory,
  ClientCaseCourtInstance,
  ClientCaseCourtParty,
  ClientCaseProceduralDeadline,
  ProceduralDeadlineCalendarSettings,
  ProceduralDeadlineCalculationPayload,
  ProceduralDeadlineCalculationResult,
  UpsertClientCaseCourtComplaintPayload,
  UpsertClientCaseCourtEventPayload,
  UpsertClientCaseCourtInstancePayload,
  UpsertClientCaseCourtPayload,
  UpsertClientCaseCourtPartyPayload,
  UpsertClientCaseProceduralDeadlinePayload,
  ClientStatus,
  MonthlyStats,
  ClientCase,
  ClientCaseAccessRequest,
  ClientCaseAccessRequestResolution,
  ClientCaseActivity,
  ClientCaseConversationEntry,
  ClientCaseCourtSyncJournalEntry,
  ClientCaseCourtSyncLog,
  ClientCaseCourtSyncProvider,
  ClientCaseCourtSyncRun,
  ClientCaseCourtSyncRunRequest,
  ClientCaseCourtSyncSettings,
  ClientCaseDocument,
  ClientCaseDocumentTemplate,
  ClientCaseDocumentTemplateToken,
  RenderedCaseDocumentTemplate,
  ClientCaseFinanceDetail,
  ClientCaseRelatedParty,
  ClientCaseSummary,
  ClientCaseTimelineEntry,
  ClientCaseWorkOrder,
  ClientCaseWorkOrderInvoice,
  AccountSuggestion,
  SummaryStats,
  PeriodKey,
  SummaryStatus,
  BankStatementAutoImportRunResult,
  BankStatementImportResult,
  BankStatementImportReport,
  SpreadsheetImportReport,
  CashBookSummary,
  CashInventory,
  CashInventoryCreateRequest,
  CashOrder,
  CashOrderRegister,
  NextCashOrderNumber,
  BankStatementImportBatch,
  BankStatementImportBatchDetail,
  BankStatementImportRollbackResult,
  MoneyAccount,
  MoneyOperation,
  MoneyOperationAccountingType,
  MoneyOperationDirection,
  MoneyOperationSource,
  MoneyOperationTransferRequest,
  MoneyOperationTransferResponse,
  CashFlowReport,
  MoneyCategoryReport,
  CounterpartyReport,
  ProjectReport,
  LegalWorkReport,
  CaseFinanceReport,
  CaseSuccessReport,
  CaseActivityReport,
  CurrentCasesReport,
  LawyerWorkloadReport,
  OverdueProceduralDeadlinesReport,
  TaskReport,
  DirectionReport,
  SalesPointReport,
  VatReport,
  PlanFactReport,
  AdvanceReport,
  PaymentPriorityReport,
  PaidAccountReport,
  PaidInvoiceReport,
  InvoiceStatusReport,
  ActStatusReport,
  ContractStatusReport,
  PaymentOrderReport,
  PaymentPurposeReport,
  MatchStatusReport,
  JournalStatusReport,
  CurrencyReport,
  CashOrderReport,
  MoneyOperationAccountSummary,
  MoneyBalanceReconciliationResult,
  MoneyBalanceReconciliationRunResult,
  MoneyOperationDailyBalance,
  MoneyOperationInvoiceCandidate,
  MoneyOperationsSummary,
  PaymentsListSummary,
  ClientBalanceSummary,
  ClientBalanceTransaction,
  ApplyClientBalanceResult,
  TransferPaymentRemainderResponse,
  RollbackPaymentRemainderTransferResponse,
  PartialPaymentRemainderCandidatesResponse,
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
import type { CreateUserActivityInput, UserActivityFiltersResponse, UserActivityLogItem, UserActivityLogResponse, UserActivityStatus, UserOnlineResponse } from '../types/userActivity';
import type {
  TaskJournalAssignee,
  TaskJournalAutomationCreateResult,
  TaskJournalCaseAutomationCandidate,
  TaskJournalComment,
  TaskJournalFile,
  TaskJournalBulkUpdatePayload,
  TaskJournalBulkUpdateResult,
  TaskJournalItem,
  TaskJournalNotification,
  TaskJournalStatus,
  TaskJournalUpsertPayload,
} from '../utils/taskJournalStore';
