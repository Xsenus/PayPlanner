import { API_BASE_URL } from './apiBaseUrl';
import { authService } from './authService';
import type { TaskStickerStyles } from '../utils/taskStickerStyles';

export const AUTH_SESSION_EXPIRED_EVENT = 'payplanner:auth-session-expired';
const AUTH_SESSION_EXPIRED_MESSAGE = 'Сессия истекла. Войдите снова.';
const AUTH_TOKEN_STORAGE_KEYS = ['pp_token', 'auth_token', 'pp.jwt'];

type ApiRequestOptions = RequestInit & {
  suppressAuthSessionExpired?: boolean;
};

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

export interface InvoiceReferenceDictionaries {
  invoiceUnits: BaseDictItem[];
  services: ServiceDictionaryItem[];
  serviceCategories: ServiceCategoryDictionaryItem[];
}

export interface UserSettingDto {
  key: string;
  value: string;
  updatedAt?: string;
}

export interface NavigationSettingsDto {
  order: string[];
  labels: Record<string, string>;
  groups: NavigationGroupDto[];
  updatedAt?: string | null;
}

export interface NavigationGroupDto {
  id: string;
  name: string;
  itemIds: string[];
}

export interface DadataIntegrationSettingsDto {
  apiKeyConfigured: boolean;
  secretConfigured: boolean;
  apiKeyMasked: string;
  secretMasked: string;
  canEdit: boolean;
  updatedAt?: string | null;
}

export interface UpdateDadataIntegrationSettingsRequest {
  apiKey?: string | null;
  secret?: string | null;
}

export interface AppFeatureFlagsDto {
  legal: {
    externalCourtSync: boolean;
    externalCalendarSync: boolean;
    courtEvents: boolean;
    documentTemplateLibrary: boolean;
    externalWorkOrderApproval: boolean;
    clientPortal: boolean;
    offlineCalendarReadOnly: boolean;
  };
}

export interface ClientPortalLoginPreview { displayName: string; emailHint: string; expiresAt: string }
export interface ClientPortalCaseListItem { publicId: string; title: string; caseNumber: string; caseType: string; status: string; stage: string; dueDate?: string | null }
export interface ClientPortalHome {
  displayName: string; items: ClientPortalCaseListItem[]; total: number; page: number; pageSize: number;
  upcomingActions?: Array<{ casePublicId: string; caseTitle: string; title: string; dueAt: string; status: string }>;
}
export interface ClientPortalCase {
  publicId: string; title: string; caseNumber: string; caseType: string; status: string; stage: string; startDate?: string | null; dueDate?: string | null;
  deadlines: Array<{ title: string; dueAt: string; status: string }>;
  events?: Array<{ title: string; occurredAt: string; eventType: string; status: string }>;
  documents: Array<{ publicId: string; name: string; documentType: string; status: string; version: number; sizeBytes: number; uploadedAt: string }>;
  invoices: Array<{ number: string; date: string; amount: number; status: string; workflowStatus: string }>;
  acts?: Array<{ number: string; date: string; amount: number; status: string; workflowStatus: string }>;
  contracts?: Array<{ number: string; date: string; amount?: number | null; status: string; validUntil?: string | null }>;
  approvals: Array<{ version: number; status: string; createdAt: string; expiresAt: string; decisionAt?: string | null }>;
}
export interface ClientPortalProfile { displayName: string; email: string; accountType: string; securityNotificationsEnabled: boolean; activeSessionCount: number; activeCaseCount: number }
export interface ClientPortalCaseAccess {
  accountId: number; displayName: string; email: string; accountType: 'client' | 'representative'; isActive: boolean;
  canViewCaseSummary: boolean; canViewDeadlines: boolean; canViewDocuments: boolean; canViewFinance: boolean;
  canViewApprovals: boolean; canViewMessages: boolean; documentIds: number[];
}
export interface ClientPortalAudit { eventType: string; targetType: string; targetKey: string; occurredAt: string; displayName: string; email: string }
export interface ClientPortalAccessRequest {
  email: string; displayName: string; accountType: 'client' | 'representative'; clientId?: number | null;
  legalEntityId?: number | null; clientCaseId: number; canViewCaseSummary: boolean; canViewDeadlines: boolean;
  canViewDocuments: boolean; canViewFinance: boolean; canViewApprovals: boolean; canViewMessages: boolean; documentIds: number[];
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
  googleCalendarSyncEnabled?: boolean | null;
  yandexCalendarSyncEnabled?: boolean | null;
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
  externalCalendarSyncEnabled?: boolean | null;
  googleCalendarSyncEnabled?: boolean | null;
  yandexCalendarSyncEnabled?: boolean | null;
  googleSyncIntervalMinutes: number;
  yandexSyncIntervalMinutes: number;
  manualFullSyncCooldownMinutes: number;
  completedTaskCalendarAction: string;
  completedTaskGoogleColorId: string;
  syncMode: string;
  conflictResolutionMode: string;
  userControlMode: string;
  defaultSyncFromDate?: string | null;
  inactiveUserAction: string;
  requireManualConfirmation: boolean;
  dryRunConfirmed: boolean;
  notes: string;
  updatedAt?: string | null;
}

export interface UpdateCalendarSyncSettingsRequest {
  externalCalendarSyncEnabled?: boolean | null;
  googleCalendarSyncEnabled?: boolean | null;
  yandexCalendarSyncEnabled?: boolean | null;
  googleSyncIntervalMinutes?: number;
  yandexSyncIntervalMinutes?: number;
  manualFullSyncCooldownMinutes?: number;
  completedTaskCalendarAction?: string;
  completedTaskGoogleColorId?: string;
  syncMode?: string;
  conflictResolutionMode?: string;
  userControlMode?: string;
  defaultSyncFromDate?: string | null;
  inactiveUserAction?: string;
  requireManualConfirmation?: boolean;
  dryRunConfirmed?: boolean;
  notes?: string;
}

export interface CalendarSyncLogEntryDto {
  provider: string;
  providerLabel: string;
  id: number;
  connectionId?: number | null;
  userId?: number | null;
  userName: string;
  userEmail: string;
  taskJournalItemId?: number | null;
  calendarOwnerUserId?: number | null;
  taskAssigneeUserId?: number | null;
  taskTitle: string;
  taskObjectName: string;
  taskAssigneeName: string;
  calendarName: string;
  calendarOwnerName: string;
  calendarOwnerEmail: string;
  targetType: string;
  targetLabel: string;
  direction: string;
  directionLabel: string;
  operation: string;
  operationLabel: string;
  status: string;
  statusLabel: string;
  message: string;
  details: string;
  changeSummary: string;
  actorLabel: string;
  calendarRoleLabel: string;
  createdAt: string;
}

export interface CalendarSyncLogPageDto {
  items: CalendarSyncLogEntryDto[];
  total: number;
  page: number;
  pageSize: number;
}

export interface GoogleCalendarStartAuthResponseDto {
  state: string;
  authUrl: string;
  pollIntervalMs: number;
}

export interface GoogleCalendarAuthStatusDto {
  state: string;
  status: string;
  completed: boolean;
  detail?: string | null;
  returnUrl?: string | null;
}

export interface GoogleCalendarCalendarDto {
  id: number;
  ownerUserId?: number | null;
  ownerName?: string | null;
  googleCalendarId: string;
  name: string;
  lastSyncedAt?: string | null;
  watchExpiresAt?: string | null;
  lastError?: string | null;
}

export interface GoogleCalendarConnectionDto {
  id: number;
  googleEmail?: string | null;
  displayName?: string | null;
  isEnabled: boolean;
  syncEnabled: boolean;
  calendarMode: string;
  conflictResolutionMode: string;
  defaultCalendarId?: string | null;
  defaultCalendarName?: string | null;
  accessTokenExpiresAt?: string | null;
  lastFullSyncAt?: string | null;
  lastIncrementalSyncAt?: string | null;
  manualFullSyncCooldownMinutes: number;
  manualFullSyncAvailableAt?: string | null;
  syncFromDate?: string | null;
  lastError?: string | null;
  createdAt: string;
  updatedAt: string;
  calendars: GoogleCalendarCalendarDto[];
}

export interface GoogleCalendarSyncLogDto {
  id: number;
  taskJournalItemId?: number | null;
  direction: string;
  operation: string;
  status: string;
  message: string;
  createdAt: string;
}

export interface GoogleCalendarStatusDto {
  isAvailable: boolean;
  googleOAuthReady: boolean;
  callbackUrl: string;
  connection?: GoogleCalendarConnectionDto | null;
  recentLogs: GoogleCalendarSyncLogDto[];
}

export interface UpdateGoogleCalendarSettingsRequest {
  isEnabled?: boolean;
  syncEnabled?: boolean;
  calendarMode?: string;
  conflictResolutionMode?: string;
  syncFromDate?: string | null;
}

export interface GoogleCalendarSyncNowResultDto {
  processedTasks: number;
  importedEvents: number;
  errorCount: number;
  message: string;
  status: GoogleCalendarStatusDto;
}

export interface YandexCalendarStartAuthResponseDto {
  state: string;
  authUrl: string;
  pollIntervalMs: number;
}

export interface YandexCalendarAuthStatusDto {
  state: string;
  status: string;
  completed: boolean;
  detail?: string | null;
  returnUrl?: string | null;
}

export interface YandexCalendarCalendarDto {
  id: number;
  ownerUserId?: number | null;
  ownerName?: string | null;
  yandexCalendarId: string;
  name: string;
  lastSyncedAt?: string | null;
  lastError?: string | null;
}

export interface YandexCalendarConnectionDto {
  id: number;
  yandexEmail?: string | null;
  displayName?: string | null;
  isEnabled: boolean;
  syncEnabled: boolean;
  calendarMode: string;
  conflictResolutionMode: string;
  defaultCalendarId?: string | null;
  defaultCalendarName?: string | null;
  accessTokenExpiresAt?: string | null;
  lastFullSyncAt?: string | null;
  lastIncrementalSyncAt?: string | null;
  manualFullSyncCooldownMinutes: number;
  manualFullSyncAvailableAt?: string | null;
  syncFromDate?: string | null;
  lastError?: string | null;
  createdAt: string;
  updatedAt: string;
  calendars: YandexCalendarCalendarDto[];
}

export interface YandexCalendarSyncLogDto {
  id: number;
  taskJournalItemId?: number | null;
  direction: string;
  operation: string;
  status: string;
  message: string;
  createdAt: string;
}

export interface YandexCalendarStatusDto {
  isAvailable: boolean;
  yandexOAuthReady: boolean;
  callbackUrl: string;
  connection?: YandexCalendarConnectionDto | null;
  recentLogs: YandexCalendarSyncLogDto[];
}

export interface UpdateYandexCalendarSettingsRequest {
  isEnabled?: boolean;
  syncEnabled?: boolean;
  calendarMode?: string;
  conflictResolutionMode?: string;
  syncFromDate?: string | null;
}

export interface YandexCalendarSyncNowResultDto {
  processedTasks: number;
  importedEvents: number;
  errorCount: number;
  message: string;
  status: YandexCalendarStatusDto;
}

export interface YandexDiskSettingsDto {
  isConfigured: boolean;
  requiresReconnect: boolean;
  rootPath: string;
  tokenPreview?: string | null;
  updatedAt?: string | null;
}

export interface YandexDiskConnectionDto {
  id: string;
  name: string;
  isEnabled: boolean;
  isConfigured: boolean;
  requiresReconnect: boolean;
  rootPath: string;
  tokenPreview?: string | null;
  updatedAt?: string | null;
}

export interface YandexDiskConnectionsOverviewDto {
  connections: YandexDiskConnectionDto[];
  oAuthCallbackUrl?: string | null;
}

export interface StartYandexDiskAuthRequest {
  connectionId?: string | null;
  name?: string | null;
  rootPath?: string | null;
  isEnabled?: boolean;
  returnUrl?: string | null;
}

export interface YandexDiskStartAuthResponseDto {
  state: string;
  authUrl: string;
  pollIntervalMs: number;
}

export interface YandexDiskAuthStatusDto {
  state: string;
  status: string;
  completed: boolean;
  detail?: string | null;
  connectionId?: string | null;
  returnUrl?: string | null;
}

export interface UpdateYandexDiskSettingsRequest {
  oAuthToken?: string;
  clearToken?: boolean;
  rootPath?: string;
}

export interface UpsertYandexDiskConnectionRequest extends UpdateYandexDiskSettingsRequest {
  id?: string | null;
  name?: string | null;
  isEnabled?: boolean;
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

export interface RoleCalendarVisibleUserOptionDto {
  id: number;
  name: string;
  email: string;
  departmentId?: number | null;
  department?: string | null;
  roleName: string;
}

export type StickyNoteColor = 'yellow' | 'orange' | 'red' | 'pink' | 'lime' | 'green' | 'teal' | 'cyan' | 'blue' | 'indigo' | 'purple' | 'white';
export type StickyNoteBoardVisibility = 'personal' | 'shared';
export type StickyNoteBoardBackground = 'cork' | 'gray';

export interface StickyNoteBoard {
  id: number;
  title: string;
  description?: string | null;
  visibility: StickyNoteBoardVisibility;
  ownerUserId?: number | null;
  ownerUserName: string;
  background: StickyNoteBoardBackground;
  sortOrder: number;
  notesCount: number;
  canEdit: boolean;
  canCreateNotes: boolean;
  canUpdateSettings: boolean;
  canDelete: boolean;
  canDeleteNotes: boolean;
  canManageMembers: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export type StickyNoteBoardMemberRole = 'owner' | 'editor' | 'viewer';

export interface StickyNoteBoardMember {
  id: number;
  boardId: number;
  userId: number;
  displayName: string;
  email: string;
  role: StickyNoteBoardMemberRole;
}

export interface StickyNoteBoardMemberCandidate {
  userId: number;
  displayName: string;
  email: string;
}

export interface StickyNoteItem {
  id: number;
  boardId: number;
  text: string;
  color: StickyNoteColor;
  x: number;
  y: number;
  width: number;
  height: number;
  fontSize?: number;
  isBold?: boolean;
  isItalic?: boolean;
  isUnderline?: boolean;
  opacity?: number;
  zIndex: number;
  version: number;
  isPinned: boolean;
  assignedUserId?: number | null;
  assignedUserName: string;
  reminderAt?: string | null;
  reminderSentAt?: string | null;
  authorUserId?: number | null;
  authorUserName: string;
  lastUpdatedByUserId?: number | null;
  lastUpdatedByUserName: string;
  createdAt: string;
  updatedAt?: string | null;
}

export const STICKY_NOTE_REALTIME_EVENT_TYPES = [
  'noteCreated',
  'noteUpdated',
  'noteDeleted',
  'noteRestored',
  'boardDeleted',
  'boardUpdated',
  'boardMembersChanged',
] as const;

export type StickyNoteRealtimeEventType = typeof STICKY_NOTE_REALTIME_EVENT_TYPES[number];

export interface StickyNoteRealtimeEvent {
  type: StickyNoteRealtimeEventType;
  boardId: number;
  note?: StickyNoteItem | null;
  noteId?: number | null;
  actorUserId: number;
  actorUserName: string;
  createdAt: string;
}

export class StickyNoteConflictError extends Error {
  constructor(public readonly serverNote: StickyNoteItem) {
    super('Sticky note version conflict');
    this.name = 'StickyNoteConflictError';
  }
}

export interface CreateStickyNoteBoardPayload {
  title: string;
  description?: string | null;
  visibility?: StickyNoteBoardVisibility;
  background?: StickyNoteBoardBackground;
}

export interface UpdateStickyNoteBoardPayload {
  title: string;
  description?: string | null;
  background?: StickyNoteBoardBackground;
}

export interface CreateStickyNotePayload {
  text?: string | null;
  color?: StickyNoteColor;
  x?: number;
  y?: number;
  width?: number;
  height?: number;
  fontSize?: number;
  isBold?: boolean;
  isItalic?: boolean;
  isUnderline?: boolean;
  opacity?: number;
  assignedUserId?: number | null;
  reminderAt?: string | null;
}

export interface UpdateStickyNotePayload {
  text: string;
  color: StickyNoteColor;
  x: number;
  y: number;
  width: number;
  height: number;
  fontSize?: number;
  isBold?: boolean;
  isItalic?: boolean;
  isUnderline?: boolean;
  opacity?: number;
  zIndex: number;
  isPinned: boolean;
  assignedUserId?: number | null;
  reminderAt?: string | null;
  version?: number;
}

export interface UpdateStickyNoteLayoutPayload {
  x: number;
  y: number;
  width: number;
  height: number;
  zIndex: number;
  version?: number;
}

export type NotificationChannel = 'email' | 'telegram' | 'sms';
export type UserNotificationDeliveryChannel = 'inApp' | 'toast' | 'forcedModal' | 'webPush' | 'email' | 'telegram' | 'sms' | 'max';

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
  channels?: NotificationChannel[];
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

export interface SmsNotificationSettingsDto {
  enabled: boolean;
  ready: boolean;
  providerConfigured: boolean;
  adapterConfigured: boolean;
  apiTokenSet: boolean;
  providerKey: string;
  providerAccount: string;
  senderName: string;
  providers: SmsProviderOptionDto[];
  dailyLimit: number;
  recipientDailyLimit: number;
  burstLimitPerMinute: number;
  maxMessageLength: number;
  lowBalanceThreshold: number;
  emergencyStop: boolean;
  eventTypes: SmsEventTypeSettingDto[];
  quietHoursEnabled: boolean;
  quietHoursStart: string;
  quietHoursEnd: string;
  quietHoursTimeZone: string;
  notes: string;
  statusText: string;
  diagnostics: SmsNotificationDiagnosticsDto;
}

export interface SmsProviderOptionDto {
  key: string;
  label: string;
  requiresAccount: boolean;
}

export interface SmsEventTypeSettingDto {
  key: string;
  label: string;
  description: string;
  enabled: boolean;
}

export interface SmsNotificationDiagnosticsDto {
  lastCheckedAt?: string | null;
  providerHealthy?: boolean | null;
  balance?: number | null;
  balanceCurrency: string;
  issueCode?: string | null;
  summary?: string | null;
  lastSuccessfulDeliveryAt?: string | null;
  lastFailedDeliveryAt?: string | null;
  lastDeliveryError?: string | null;
  pendingCount: number;
  failedCount: number;
  deliveredCount: number;
  deliverySampleSize: number;
  deliveredRatePercent?: number | null;
}

export interface MaxNotificationDiagnosticsDto {
  enabled: boolean;
  ready: boolean;
  apiConfirmed: boolean;
  botConfigured: boolean;
  botTokenSet: boolean;
  webhookSecretSet: boolean;
  scenariosConfirmed: boolean;
  emergencyStop: boolean;
  notificationsEnabled: boolean;
  documentsEnabled: boolean;
  correspondenceEnabled: boolean;
  confirmationsEnabled: boolean;
  quietHoursEnabled: boolean;
  quietHoursStart: string;
  quietHoursEnd: string;
  quietHoursTimeZone: string;
  dailyLimitPerRecipient: number;
  technicalPayloadRetentionDays: number;
  scenarios: MaxScenarioDto[];
  allowedEventTypes: string[];
  lastSuccessfulDeliveryAt?: string | null;
  lastFailedDeliveryAt?: string | null;
  lastDeliveryError?: string | null;
  statusText: string;
}

export interface MaxScenarioDto {
  key: string;
  label: string;
  description: string;
  enabled: boolean;
  requiresVerifiedLink: boolean;
}

export interface WebPushPublicKeyDto {
  enabled: boolean;
  ready: boolean;
  publicKey: string;
}

export interface UserPushSubscriptionDto {
  id: number;
  endpoint: string;
  browser: string;
  platform: string;
  deviceName: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  lastSeenAt?: string | null;
  revokedAt?: string | null;
  lastErrorAt?: string | null;
  lastError?: string | null;
}

export interface WebPushDiagnosticsDto {
  enabled: boolean;
  ready: boolean;
  publicKeyConfigured: boolean;
  privateKeyConfigured: boolean;
  subjectConfigured: boolean;
  totalSubscriptionsCount: number;
  activeSubscriptionsCount: number;
  revokedSubscriptionsCount: number;
  failedSubscriptionsCount: number;
  usersWithActiveSubscriptionsCount: number;
  lastSuccessfulDeliveryAt?: string | null;
  lastFailedDeliveryAt?: string | null;
  lastDeliveryError?: string | null;
}

export interface UpsertPushSubscriptionRequest {
  endpoint: string;
  keys: {
    p256dh: string;
    auth: string;
  };
  browser?: string | null;
  platform?: string | null;
  deviceName?: string | null;
  userAgent?: string | null;
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
  sms: SmsNotificationSettingsDto;
  max: MaxNotificationDiagnosticsDto;
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

export interface UpdateSmsNotificationSettingsRequest {
  enabled: boolean;
  providerKey?: string | null;
  providerAccount?: string | null;
  senderName?: string | null;
  apiToken?: string | null;
  keepExistingApiToken?: boolean;
  dailyLimit?: number | null;
  recipientDailyLimit?: number | null;
  burstLimitPerMinute?: number | null;
  maxMessageLength?: number | null;
  lowBalanceThreshold?: number | null;
  emergencyStop?: boolean | null;
  disabledEventTypes?: string[] | null;
  quietHoursEnabled: boolean;
  quietHoursStart?: string | null;
  quietHoursEnd?: string | null;
  quietHoursTimeZone?: string | null;
  notes?: string | null;
}

export interface UpdateMaxNotificationSettingsRequest {
  enabled: boolean;
  botToken?: string | null;
  keepExistingBotToken?: boolean;
  webhookSecret?: string | null;
  keepExistingWebhookSecret?: boolean;
  notificationsEnabled: boolean;
  documentsEnabled: boolean;
  correspondenceEnabled: boolean;
  confirmationsEnabled: boolean;
  emergencyStop: boolean;
  quietHoursEnabled: boolean;
  quietHoursStart?: string | null;
  quietHoursEnd?: string | null;
  quietHoursTimeZone?: string | null;
  dailyLimitPerRecipient?: number | null;
  technicalPayloadRetentionDays?: number | null;
}

export interface MaxOutboxMessageDto {
  publicId: string;
  eventKey: string;
  scenario: string;
  recipientUserId?: number | null;
  bodyPreview: string;
  status: string;
  attemptCount: number;
  maxAttempts: number;
  nextAttemptAt?: string | null;
  lastErrorCode?: string | null;
  createdAt: string;
  sentAt?: string | null;
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
  toastEnabled: boolean;
  forcedModalEnabled?: boolean;
  objectType: string;
  objectId?: number | null;
  taskId?: number | null;
  metadataJson?: string | null;
  requiresAction?: boolean;
  isRead: boolean;
  readAt?: string | null;
  isArchived: boolean;
  archivedAt?: string | null;
  snoozedUntil?: string | null;
  createdAt: string;
}

export interface UserNotificationTypeDto {
  key: string;
  label: string;
  description: string;
  tone: string;
  priority: string;
  domain: string;
  domainLabel: string;
  channels: UserNotificationDeliveryChannel[];
  deduplicate: boolean;
  digestible: boolean;
  showToastByDefault: boolean;
  toastPriority: string;
  pushEligible: boolean;
  pushPriority: string;
  groupingWindowSeconds: number;
  requiresAction: boolean;
}

export interface UserNotificationBulkActionResultDto {
  updatedCount: number;
}

export interface UserNotificationPreferenceDto {
  disabledTypes: string[];
  disabledChannels: Record<string, string[]>;
  useDefault?: boolean;
}

export interface NotificationRolePreferenceDto {
  roleId: number;
  roleName: string;
  isSystemRole: boolean;
  hasCustomPreferences: boolean;
  defaultProfileKey: string;
  defaultProfileLabel: string;
  defaultProfileDescription: string;
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
  domain?: 'task' | 'calendar' | 'note' | 'finance' | 'case' | 'system' | string;
  requiresAction?: boolean;
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
  providerKey?: string | null;
  providerStatus?: string | null;
  providerStatusCheckedAt?: string | null;
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

export interface DatabaseBackupS3UploadSettingsDto {
  enabled: boolean;
  connectionIds: string[];
  prefixTemplate: string;
}

export interface DatabaseBackupS3UploadResultDto {
  id: string;
  backupRelativePath: string;
  backupFileName: string;
  connectionId: string;
  status: string;
  targetPath: string;
  targetKey?: string | null;
  sizeBytes: number;
  error?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface DatabaseBackupYandexDiskUploadSettingsDto {
  enabled: boolean;
  prefixTemplate: string;
}

export interface DatabaseBackupYandexDiskUploadResultDto {
  id: string;
  backupRelativePath: string;
  backupFileName: string;
  status: string;
  targetPath: string;
  sizeBytes: number;
  error?: string | null;
  createdAt: string;
  updatedAt: string;
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
  s3Upload?: DatabaseBackupS3UploadSettingsDto;
  s3UploadResults?: DatabaseBackupS3UploadResultDto[];
  yandexDiskUpload?: DatabaseBackupYandexDiskUploadSettingsDto;
  yandexDiskUploadResults?: DatabaseBackupYandexDiskUploadResultDto[];
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
  lastSuccessAt?: string | null;
  lastRunDurationMs?: number | null;
  lastRunError?: string | null;
  manualRunEnabled?: boolean;
  manualRunHint?: string | null;
  courtSyncMetrics?: CourtSyncMetricsDto | null;
}

export interface CourtSyncMetricsDto {
  startedAt: string;
  requestCount: number;
  failedRequestCount: number;
  averageLatencyMs: number;
  maxLatencyMs: number;
  duplicateSkipCount: number;
  documentCount: number;
  documentBytes: number;
  retained?: CourtSyncRetainedMetricsDto | null;
}

export interface CourtSyncRetainedMetricsDto {
  retentionDays: number;
  periodStartedOn?: string | null;
  periodEndedOn?: string | null;
  requestCount: number;
  failedRequestCount: number;
  averageLatencyMs: number;
  maxLatencyMs: number;
  duplicateSkipCount: number;
  documentCount: number;
  documentBytes: number;
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
  s3UploadResults?: DatabaseBackupS3UploadResultDto[];
  yandexDiskUploadResults?: DatabaseBackupYandexDiskUploadResultDto[];
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
  operationsTotal?: number;
  operationsPage?: number;
  operationsPageSize?: number;
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
  searchAliases?: string[];
  objectType?: string;
  action?: string;
  actor?: string;
  from?: string;
  to?: string;
  fromUtc?: string;
  toUtcExclusive?: string;
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

export interface DocumentTemplateVersionDto {
  id: number;
  revision: number;
  documentType: 'invoice' | 'contract' | 'act' | string;
  activeTemplateKey: string;
  templateNames: string[];
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  createdAt: string;
  restoredFromVersionId?: number | null;
}

export interface TaskJournalSettingsDto {
  restrictExecutorEditing: boolean;
  allowExecutorDeadlineChangeRequests: boolean;
  executorCompletionAllowedUserIds: number[];
  requireCompletionResult: boolean;
  dailyOverdueReminderEnabled: boolean;
  allowAdminCompleteAnyTask: boolean;
  approvalDueDays: number;
  taskCardWindowMode: 'legacy' | 'modern';
  approvalQuickActionMode: 'completionButton' | 'separateButton';
  caseInactivityAutomationEnabled: boolean;
  caseInactivityDays: number;
  caseInactivityLimit: number;
  caseInactivityDefaultAssigneeUserId?: number | null;
  caseInactivityEscalationDays: number;
  caseInactivityEscalationUserId?: number | null;
  caseInactivityFirstRepeatAfterDays: number;
  caseInactivityCriticalRepeatAfterDays: number;
  caseInactivityRunLocalTime?: string;
  caseInactivityScheduleTimeZone?: string;
  stickerStyles: TaskStickerStyles;
}

/** Параметры серверного списка журнала задач. Все фильтры применяются до пагинации. */
export interface TaskJournalListParams {
  search?: string;
  status?: string;
  type?: string;
  priority?: string;
  underControl?: boolean;
  assigneeUserId?: number;
  authorUserId?: number;
  approvalScope?: boolean;
  metric?: 'active' | 'overdue' | 'today' | 'highPriority';
  clientCaseId?: number;
  clientId?: number;
  legalEntityId?: number;
  from?: string;
  to?: string;
  todayFrom?: string;
  todayTo?: string;
  calendarView?: boolean;
  includeDeadlineChangeRequests?: boolean;
  page?: number;
  pageSize?: number;
  sortBy?: TaskJournalSortKey;
  sortDir?: SortDir;
}

/** Компактные счётчики журнала, рассчитанные сервером без выгрузки карточек. */
export interface TaskJournalListSummary {
  active: number;
  overdue: number;
  today: number;
  highPriority: number;
  unbilled: number;
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

export interface TaskJournalAutomationPreviewDto {
  inactivityDays: number;
  escalationDays: number;
  candidateCount: number;
  escalationCandidateCount: number;
}

export interface UpdateTaskJournalSettingsRequest {
  restrictExecutorEditing: boolean;
  allowExecutorDeadlineChangeRequests: boolean;
  executorCompletionAllowedUserIds: number[];
  requireCompletionResult: boolean;
  dailyOverdueReminderEnabled: boolean;
  allowAdminCompleteAnyTask: boolean;
  approvalDueDays: number;
  taskCardWindowMode: 'legacy' | 'modern';
  approvalQuickActionMode: 'completionButton' | 'separateButton';
  caseInactivityAutomationEnabled: boolean;
  caseInactivityDays: number;
  caseInactivityLimit: number;
  caseInactivityDefaultAssigneeUserId?: number | null;
  caseInactivityEscalationDays: number;
  caseInactivityEscalationUserId?: number | null;
  caseInactivityRunLocalTime?: string;
  caseInactivityScheduleTimeZone?: string;
  stickerStyles: TaskStickerStyles;
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

export type ContactSourceType = 'client' | 'legal' | 'external';
export type ContactScope = 'all' | 'people' | 'organizations' | 'clients' | 'external';
export type ContactType = 'Person' | 'Organization';
export type ContactRole = 'Contact' | 'Client' | 'Supplier' | 'Agent';

export interface MessengerDocumentDeliveryConfiguration {
  enabled: boolean;
  linkLifetimeHours: number;
  providers: Array<{ channel: string; enabled: boolean }>;
}

export interface MessengerRecipientBindingDto {
  id: number;
  clientId: number;
  clientPortalAccountId?: number | null;
  channel: string;
  provider: string;
  displayName: string;
  maskedAddress: string;
  verificationStatus: string;
  verificationMethod: string;
  verifiedAt?: string | null;
}

export interface MessengerDocumentDeliveryDto {
  id: number;
  publicId: string;
  clientId: number;
  clientCaseId?: number | null;
  documentType: string;
  documentId: number;
  documentVersion: number;
  originalFileName: string;
  checksumSha256: string;
  sizeBytes: number;
  channel: string;
  provider: string;
  status: string;
  attemptCount: number;
  linkExpiresAt: string;
  firstOpenedAt?: string | null;
  downloadCount: number;
  createdAt: string;
  recipient: string;
}

export interface PreparedMessengerDocumentDeliveryDto {
  id: number;
  publicId: string;
  documentType: string;
  documentId: number;
  documentVersion: number;
  originalFileName: string;
  checksumSha256: string;
  sizeBytes: number;
  channel: string;
  provider: string;
  status: string;
  linkExpiresAt: string;
}

export interface ElectronicSignatureConfigurationDto {
  enabled: boolean;
  preparationEnabled: boolean;
  defaultSignatureType: string;
  defaultSigningOrder: string;
  defaultArchiveRetentionYears: number;
  providers: Array<{ name: string; enabled: boolean }>;
}

export interface ElectronicSignatureRecipientDto {
  id: number;
  displayName: string;
  accountType: string;
  email: string;
  isRepresentative: boolean;
}

export interface ElectronicSignatureEnvelopeDto {
  id: number;
  publicId: string;
  documentType: string;
  documentId: number;
  documentVersion: number;
  signatureType: string;
  signingOrder: string;
  provider: string;
  status: string;
  originalFileName: string;
  sizeBytes: number;
  checksumSha256: string;
  archiveRetentionYears: number;
  requiresMachineReadablePowerOfAttorney: boolean;
  createdAt: string;
  sentAt?: string | null;
  completedAt?: string | null;
  cancelledAt?: string | null;
  signers: Array<{
    id: number;
    order: number;
    displayName: string;
    maskedContact: string;
    partyType: string;
    status: string;
    requiresMachineReadablePowerOfAttorney: boolean;
    powerOfAttorneyReference?: string | null;
  }>;
  events: Array<{ eventType: string; status: string; message: string; occurredAt: string }>;
}

export interface ContactDirectoryItemDto {
  sourceType: ContactSourceType;
  category: ContactScope;
  sourceId: number;
  contactType: ContactType;
  inn?: string | null;
  roles: ContactRole[];
  fullName: string;
  organizationName?: string | null;
  legalEntityId?: number | null;
  parentContactId?: number | null;
  parentContactName?: string | null;
  linkedClientId?: number | null;
  linkedLegalEntityId?: number | null;
  hasFullProfile: boolean;
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
  contactType: ContactType;
  inn?: string | null;
  roles: ContactRole[];
  parentContactId?: number | null;
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

export type SortDir = 'asc' | 'desc';
type PaymentSortKey =
  | 'date'
  | 'plannedDate'
  | 'client'
  | 'description'
  | 'type'
  | 'status'
  | 'category'
  | 'amount'
  | 'paidDate'
  | 'createdAt';
export type TaskJournalSortKey =
  | 'counterparty'
  | 'title'
  | 'assignee'
  | 'author'
  | 'dueAt'
  | 'type'
  | 'result'
  | 'status';
type PaymentsExportParams = {
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
  advanceReviewStatus?: PaymentAdvanceReviewStatus;
  minAmount?: number;
  maxAmount?: number;
  search?: string;
  sortBy?: PaymentSortKey;
  sortDir?: SortDir;
};
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
        | {
            detail?: string;
            title?: string;
            message?: string;
            missingFields?: string[];
            errors?: Record<string, string[] | string>;
          }
        | null;

      const detail = payload?.detail?.trim();
      if (detail) {
        return detail;
      }

      const message = payload?.message?.trim();
      const missingFields = Array.isArray(payload?.missingFields)
        ? payload.missingFields.map((field) => `${field}`.trim()).filter(Boolean)
        : [];
      if (message && missingFields.length > 0) {
        return JSON.stringify({ message, missingFields });
      }
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
  private taskJournalSettingsRequest: Promise<TaskJournalSettingsDto> | null = null;

  private async fetchWithSessionRefresh(
    url: string,
    options: RequestInit,
    suppressAuthSessionExpired = false,
  ): Promise<{ response: Response; sessionRefreshed: boolean }> {
    let token = await authService.ensureFreshSession().catch(() => getAuthToken());

    const execute = (authToken: string | null) => {
      const headers: Record<string, string> = {
        ...(options.headers as Record<string, string> | undefined),
      };
      if (authToken) {
        headers.Authorization = `Bearer ${authToken}`;
      }

      return fetch(url, { ...options, headers });
    };

    let response = await execute(token);
    let sessionRefreshed = false;

    if (response.status === 401 && token && !suppressAuthSessionExpired) {
      try {
        token = await authService.ensureFreshSession(true);
        if (token) {
          sessionRefreshed = true;
          response = await execute(token);
        }
      } catch {
        // The common error path below expires the local session only when refresh also failed.
      }
    }

    return { response, sessionRefreshed };
  }

  private async request<T>(endpoint: string, options?: ApiRequestOptions): Promise<T> {
    const { suppressAuthSessionExpired = false, ...fetchOptions } = options ?? {};
    const url = `${API_BASE_URL}${endpoint}`;
    const isFormData = typeof FormData !== 'undefined' && fetchOptions.body instanceof FormData;
    const headers: Record<string, string> = {
      ...(!isFormData && fetchOptions.body ? { 'Content-Type': 'application/json' } : {}),
      ...(fetchOptions.headers as Record<string, string> | undefined),
    };

    const { response, sessionRefreshed } = await this.fetchWithSessionRefresh(url, {
      ...fetchOptions,
      headers,
    }, suppressAuthSessionExpired);

    if (!response.ok) {
      if (response.status === 401 && !suppressAuthSessionExpired && !sessionRefreshed) {
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

  private async requestStickyNote<T>(endpoint: string, options?: RequestInit): Promise<T> {
    const url = `${API_BASE_URL}${endpoint}`;
    const headers: Record<string, string> = {
      ...(options?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(options?.headers as Record<string, string> | undefined),
    };

    const { response, sessionRefreshed } = await this.fetchWithSessionRefresh(url, {
      ...options,
      headers,
    });

    if (response.status === 409) {
      const contentType = response.headers.get('content-type') || '';
      if (contentType.includes('application/json')) {
        try {
          throw new StickyNoteConflictError(await response.json() as StickyNoteItem);
        } catch (error) {
          if (error instanceof StickyNoteConflictError) {
            throw error;
          }
        }
      }
    }

    if (!response.ok) {
      if (response.status === 401 && !sessionRefreshed) {
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

    return await response.json() as T;
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

  async getAdminAppReleasesPaged(params?: { page?: number; pageSize?: number; search?: string }) {
    const query = new URLSearchParams();
    if (params?.page) query.set('page', String(params.page));
    if (params?.pageSize) query.set('pageSize', String(params.pageSize));
    if (params?.search?.trim()) query.set('search', params.search.trim());
    const suffix = query.toString();
    return this.request<PagedResult<AppReleaseAdminDto>>(
      `/app-version/admin/releases/paged${suffix ? `?${suffix}` : ''}`,
    );
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

  async getCalendarSyncLogs(params?: {
    provider?: string;
    status?: string;
    operation?: string;
    userId?: number;
    createdFrom?: string;
    createdTo?: string;
    search?: string;
    page?: number;
    pageSize?: number;
  }) {
    const searchParams = new URLSearchParams();
    if (params?.provider) searchParams.set('provider', params.provider);
    if (params?.status) searchParams.set('status', params.status);
    if (params?.operation) searchParams.set('operation', params.operation);
    if (params?.userId) searchParams.set('userId', params.userId.toString());
    if (params?.createdFrom) searchParams.set('createdFrom', params.createdFrom);
    if (params?.createdTo) searchParams.set('createdTo', params.createdTo);
    if (params?.search) searchParams.set('search', params.search);
    if (params?.page) searchParams.set('page', params.page.toString());
    if (params?.pageSize) searchParams.set('pageSize', params.pageSize.toString());
    const query = searchParams.toString();
    return this.request<CalendarSyncLogPageDto>(`/calendar-sync/logs${query ? `?${query}` : ''}`);
  }

  async getGoogleCalendarStatus() {
    return this.request<GoogleCalendarStatusDto>('/google-calendar/status');
  }

  async startGoogleCalendarAuth(returnUrl = '/?ppTab=profile') {
    return this.request<GoogleCalendarStartAuthResponseDto>('/google-calendar/auth/start', {
      method: 'POST',
      body: JSON.stringify({ returnUrl }),
    });
  }

  async getGoogleCalendarAuthStatus(state: string) {
    return this.request<GoogleCalendarAuthStatusDto>(`/google-calendar/auth/status/${encodeURIComponent(state)}`);
  }

  async updateGoogleCalendarSettings(payload: UpdateGoogleCalendarSettingsRequest) {
    return this.request<GoogleCalendarStatusDto>('/google-calendar/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async syncGoogleCalendarNow() {
    return this.request<GoogleCalendarSyncNowResultDto>('/google-calendar/sync-now', {
      method: 'POST',
    });
  }

  async disconnectGoogleCalendar() {
    return this.request<GoogleCalendarStatusDto>('/google-calendar/connection', {
      method: 'DELETE',
    });
  }

  async getYandexCalendarStatus() {
    return this.request<YandexCalendarStatusDto>('/yandex-calendar/status');
  }

  async startYandexCalendarAuth(returnUrl = '/?ppTab=profile') {
    return this.request<YandexCalendarStartAuthResponseDto>('/yandex-calendar/auth/start', {
      method: 'POST',
      body: JSON.stringify({ returnUrl }),
    });
  }

  async getYandexCalendarAuthStatus(state: string) {
    return this.request<YandexCalendarAuthStatusDto>(`/yandex-calendar/auth/status/${encodeURIComponent(state)}`);
  }

  async updateYandexCalendarSettings(payload: UpdateYandexCalendarSettingsRequest) {
    return this.request<YandexCalendarStatusDto>('/yandex-calendar/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async syncYandexCalendarNow() {
    return this.request<YandexCalendarSyncNowResultDto>('/yandex-calendar/sync-now', {
      method: 'POST',
    });
  }

  async disconnectYandexCalendar() {
    return this.request<YandexCalendarStatusDto>('/yandex-calendar/connection', {
      method: 'DELETE',
    });
  }

  async getYandexDiskSettings() {
    return this.request<YandexDiskSettingsDto>('/yandex-disk/settings');
  }

  async getYandexDiskConnections() {
    return this.request<YandexDiskConnectionsOverviewDto>('/yandex-disk/connections');
  }

  async upsertYandexDiskConnection(payload: UpsertYandexDiskConnectionRequest) {
    return this.request<YandexDiskConnectionDto>('/yandex-disk/connections', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async startYandexDiskAuth(payload: StartYandexDiskAuthRequest) {
    return this.request<YandexDiskStartAuthResponseDto>('/yandex-disk/auth/start', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getYandexDiskAuthStatus(state: string) {
    return this.request<YandexDiskAuthStatusDto>(`/yandex-disk/auth/status/${encodeURIComponent(state)}`);
  }

  async deleteYandexDiskConnection(id: string) {
    return this.request<void>(`/yandex-disk/connections/${encodeURIComponent(id)}`, {
      method: 'DELETE',
    });
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

  async testYandexDiskConnectionById(id: string, testFolderPath?: string) {
    return this.request<YandexDiskConnectionTestDto>(`/yandex-disk/connections/${encodeURIComponent(id)}/connection-test`, {
      method: 'POST',
      body: JSON.stringify({ testFolderPath }),
    });
  }

  async listYandexDiskResources(path = '/', connectionId?: string | null) {
    const base = connectionId
      ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/resources`
      : '/yandex-disk/resources';
    return this.request<YandexDiskListResponseDto>(`${base}?path=${encodeURIComponent(path)}`);
  }

  async createYandexDiskFolder(path: string, connectionId?: string | null) {
    const url = connectionId ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/folders` : '/yandex-disk/folders';
    return this.request<YandexDiskOperationResultDto>(url, {
      method: 'POST',
      body: JSON.stringify({ path }),
    });
  }

  async copyYandexDiskResource(fromPath: string, toPath: string, overwrite = false, connectionId?: string | null) {
    const url = connectionId ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/copy` : '/yandex-disk/copy';
    return this.request<YandexDiskOperationResultDto>(url, {
      method: 'POST',
      body: JSON.stringify({ fromPath, toPath, overwrite }),
    });
  }

  async moveYandexDiskResource(fromPath: string, toPath: string, overwrite = false, connectionId?: string | null) {
    const url = connectionId ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/move` : '/yandex-disk/move';
    return this.request<YandexDiskOperationResultDto>(url, {
      method: 'POST',
      body: JSON.stringify({ fromPath, toPath, overwrite }),
    });
  }

  async deleteYandexDiskResource(path: string, permanently = false, connectionId?: string | null) {
    const base = connectionId
      ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/resources`
      : '/yandex-disk/resources';
    return this.request<YandexDiskOperationResultDto>(
      `${base}?path=${encodeURIComponent(path)}&permanently=${permanently}`,
      { method: 'DELETE' },
    );
  }

  async uploadYandexDiskFile(path: string, file: File, overwrite = true, connectionId?: string | null) {
    const form = new FormData();
    form.append('file', file);
    form.append('path', path);
    form.append('overwrite', String(overwrite));
    const url = connectionId ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/upload` : '/yandex-disk/upload';
    return this.request<YandexDiskOperationResultDto>(url, {
      method: 'POST',
      body: form,
    });
  }

  async downloadYandexDiskFile(path: string, connectionId?: string | null) {
    const base = connectionId
      ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/download`
      : '/yandex-disk/download';
    const url = `${API_BASE_URL}${base}?path=${encodeURIComponent(path)}`;
    const { response, sessionRefreshed } = await this.fetchWithSessionRefresh(url, {});

    if (!response.ok) {
      if (response.status === 401 && !sessionRefreshed) {
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

  async getYandexDiskTextFile(path: string, connectionId?: string | null) {
    const base = connectionId ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/text` : '/yandex-disk/text';
    return this.request<YandexDiskTextFileDto>(`${base}?path=${encodeURIComponent(path)}`);
  }

  async saveYandexDiskTextFile(path: string, content: string, overwrite = true, connectionId?: string | null) {
    const url = connectionId ? `/yandex-disk/connections/${encodeURIComponent(connectionId)}/text` : '/yandex-disk/text';
    return this.request<YandexDiskOperationResultDto>(url, {
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
    const url = `${API_BASE_URL}/s3-storages/${encodeURIComponent(id)}/download?path=${encodeURIComponent(path)}`;
    const { response, sessionRefreshed } = await this.fetchWithSessionRefresh(url, {});

    if (!response.ok) {
      if (response.status === 401 && !sessionRefreshed) {
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
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<Act>>(`/acts${q}`, { signal: options?.signal });
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
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<ActsSummary>(`/acts/summary${q}`, { signal: options?.signal });
  }

  async getActResponsibles() {
    return this.request<ActResponsible[]>('/acts/responsibles');
  }

  async getAct(id: number) {
    return this.request<Act>(`/acts/${id}`);
  }

  async getActDocumentTemplateSettings() {
    return this.request<InvoiceDocumentTemplateSettingsDto>('/acts/document-template-settings');
  }

  async updateActDocumentTemplateSettings(payload: Pick<InvoiceDocumentTemplateSettingsDto, 'activeTemplateKey' | 'templates' | 'printWithSignatureAndSealByDefault'>) {
    return this.request<InvoiceDocumentTemplateSettingsDto>('/acts/document-template-settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getActDocumentTemplateHistory() {
    return this.request<DocumentTemplateVersionDto[]>('/acts/document-template-settings/history');
  }

  async getActDocumentTemplateVersion(versionId: number) {
    return this.request<InvoiceDocumentTemplateSettingsDto>(`/acts/document-template-settings/history/${versionId}`);
  }

  async restoreActDocumentTemplateVersion(versionId: number) {
    return this.request<InvoiceDocumentTemplateSettingsDto>(`/acts/document-template-settings/history/${versionId}/restore`, {
      method: 'POST',
    });
  }

  async createAct(payload: ActInput) {
    return this.request<Act>('/acts', { method: 'POST', body: JSON.stringify(payload) });
  }

  async updateAct(id: number, payload: ActInput) {
    return this.request<Act>(`/acts/${id}`, { method: 'PUT', body: JSON.stringify(payload) });
  }

  async createActCorrection(id: number, reason?: string | null) {
    return this.request<Act>(`/acts/${id}/correction`, {
      method: 'POST',
      body: JSON.stringify({ reason: reason?.trim() ? reason.trim() : null }),
    });
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
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<Contract>>(`/contracts${q}`, { signal: options?.signal });
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

  async getContractDocumentTemplateHistory() {
    return this.request<DocumentTemplateVersionDto[]>('/contracts/document-template-settings/history');
  }

  async getContractDocumentTemplateVersion(versionId: number) {
    return this.request<InvoiceDocumentTemplateSettingsDto>(`/contracts/document-template-settings/history/${versionId}`);
  }

  async restoreContractDocumentTemplateVersion(versionId: number) {
    return this.request<InvoiceDocumentTemplateSettingsDto>(`/contracts/document-template-settings/history/${versionId}/restore`, {
      method: 'POST',
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
  async getUserSetting(key: string): Promise<UserSettingDto | null> {
    try {
      return await this.request<UserSettingDto | undefined>(`/user-settings/${encodeURIComponent(key)}`) ?? null;
    } catch (error) {
      if (error instanceof Error && /\bHTTP 404\b/.test(error.message)) {
        return null;
      }

      throw error;
    }
  }

  async upsertUserSetting(key: string, value: string) {
    return this.request<UserSettingDto>(`/user-settings/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ value }),
    });
  }

  async getUserSettingForUser(userId: number, key: string): Promise<UserSettingDto | null> {
    return this.request<UserSettingDto>(`/user-settings/users/${userId}/${encodeURIComponent(key)}`);
  }

  async upsertUserSettingForUser(userId: number, key: string, value: string) {
    return this.request<UserSettingDto>(`/user-settings/users/${userId}/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ value }),
    });
  }

  async getNavigationSettings() {
    return this.request<NavigationSettingsDto>('/navigation-settings');
  }

  async updateNavigationSettings(order: string[], labels: Record<string, string>, groups: NavigationGroupDto[]) {
    return this.request<NavigationSettingsDto>('/navigation-settings', {
      method: 'PUT',
      body: JSON.stringify({ order, labels, groups }),
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

  async updateSmsNotificationSettings(payload: UpdateSmsNotificationSettingsRequest) {
    return this.request<NotificationSettingsDto>('/notifications/settings/sms', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async updateMaxNotificationSettings(payload: UpdateMaxNotificationSettingsRequest) {
    return this.request<NotificationSettingsDto>('/notifications/settings/max', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async getMaxOutboxMessages(take = 20) {
    return this.request<MaxOutboxMessageDto[]>(`/max/messages?take=${Math.max(1, Math.min(100, take))}`);
  }

  async retryMaxOutboxMessage(publicId: string) {
    return this.request<void>(`/max/messages/${encodeURIComponent(publicId)}/retry`, { method: 'POST' });
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
    phoneNumber?: string | null;
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
    s3UploadEnabled?: boolean;
    s3ConnectionIds?: string[];
    s3PrefixTemplate?: string;
    yandexDiskUploadEnabled?: boolean;
    yandexDiskPrefixTemplate?: string;
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

  async retryDatabaseBackupS3Upload(uploadResultId: string) {
    return this.request<DatabaseBackupOverviewDto>(`/database/backups/s3-uploads/${encodeURIComponent(uploadResultId)}/retry`, {
      method: 'POST',
    });
  }

  async retryDatabaseBackupYandexDiskUpload(uploadResultId: string) {
    return this.request<DatabaseBackupOverviewDto>(`/database/backups/yandex-disk-uploads/${encodeURIComponent(uploadResultId)}/retry`, {
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

  async getSpreadsheetImportBatch(batchId: number, page = 1, pageSize = 25) {
    const query = buildQuery({ page, pageSize });
    return this.request<SpreadsheetImportBatchDetailDto>(`/database/import/spreadsheets/batches/${batchId}${query}`);
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

  async getObjectAuditEvents(
    params?: ObjectAuditFeedQuery,
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 25,
    });
    return this.request<PagedResult<ObjectAuditEventDto>>(`/object-audit${q}`, {
      signal: options?.signal,
    });
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
  async getContacts(
    params?: {
      scope?: ContactScope;
      search?: string;
      legalEntityId?: number;
      includeInactive?: boolean;
      syncOnly?: boolean;
    },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery(params ?? {});
    return this.request<ContactDirectoryItemDto[]>(`/contacts${q}`, {
      signal: options?.signal,
    });
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

  async promoteExternalContact(id: number, payload: { target: 'client' | 'legalEntity'; role: ContactRole }) {
    return this.request<ContactDirectoryItemDto>(`/contacts/external/${id}/promote`, {
      method: 'POST',
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
    installmentsOnly?: boolean;
    sortBy?: InvoicesSortKey;
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<Invoice>>(`/invoices${q}`, { signal: options?.signal });
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
    installmentsOnly?: boolean;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<InvoiceSummary>(`/invoices/summary${q}`, { signal: options?.signal });
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

  async getInvoiceDocumentTemplateHistory() {
    return this.request<DocumentTemplateVersionDto[]>('/invoices/document-template-settings/history');
  }

  async getInvoiceDocumentTemplateVersion(versionId: number) {
    return this.request<InvoiceDocumentTemplateSettingsDto>(`/invoices/document-template-settings/history/${versionId}`);
  }

  async restoreInvoiceDocumentTemplateVersion(versionId: number) {
    return this.request<InvoiceDocumentTemplateSettingsDto>(`/invoices/document-template-settings/history/${versionId}/restore`, {
      method: 'POST',
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

  async getAccountsRegistry(
    params?: {
      from?: string;
      to?: string;
      status?: 'created' | 'paid' | 'partial' | 'overdue' | 'cancelled';
      search?: string;
      type?: 'Income' | 'Expense';
      clientId?: number;
      legalEntityId?: number;
      clientIds?: string;
      contractId?: number;
      invoiceId?: number;
      clientRole?: ClientRole;
      payablesOnly?: boolean;
      sortBy?: 'date' | 'number' | 'amount' | 'counterparty' | 'dueDate' | 'responsible';
      sortDir?: SortDir;
      page?: number;
      pageSize?: number;
  },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<AccountRegistryRow> & { summary: AccountsSummary }>(
      `/accounts/registry${q}`,
      { signal: options?.signal },
    );
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
    options?: { includeSignatureAndSeal?: boolean; templateKey?: string },
  ) {
    const token = getAuthToken();
    const query = buildQuery({
      format,
      templateKey: options?.templateKey,
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

  async getRoleCalendarVisibleUserOptions() {
    return this.request<RoleCalendarVisibleUserOptionDto[]>('/role-permissions/calendar-users');
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

  // ===== Sticky notes =====
  async getStickyNoteBoards() {
    return this.request<StickyNoteBoard[]>('/sticky-notes/boards');
  }

  async createStickyNoteBoard(payload: CreateStickyNoteBoardPayload) {
    return this.request<StickyNoteBoard>('/sticky-notes/boards', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateStickyNoteBoard(id: number, payload: UpdateStickyNoteBoardPayload) {
    return this.request<StickyNoteBoard>(`/sticky-notes/boards/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteStickyNoteBoard(id: number) {
    return this.request<void>(`/sticky-notes/boards/${id}`, {
      method: 'DELETE',
    });
  }

  async getStickyNoteBoardMembers(boardId: number) {
    return this.request<StickyNoteBoardMember[]>(`/sticky-notes/boards/${boardId}/members`);
  }

  async getStickyNoteMemberCandidates() {
    return this.request<StickyNoteBoardMemberCandidate[]>('/sticky-notes/member-candidates');
  }

  async upsertStickyNoteBoardMember(boardId: number, userId: number, payload: { role: Exclude<StickyNoteBoardMemberRole, 'owner'> }) {
    return this.request<StickyNoteBoardMember>(`/sticky-notes/boards/${boardId}/members/${userId}`, {
      method: 'PUT',
      body: JSON.stringify({ userId, ...payload }),
    });
  }

  async deleteStickyNoteBoardMember(boardId: number, userId: number) {
    return this.request<void>(`/sticky-notes/boards/${boardId}/members/${userId}`, {
      method: 'DELETE',
    });
  }

  async getStickyNotes(boardId: number) {
    return this.request<StickyNoteItem[]>(`/sticky-notes/boards/${boardId}/notes`);
  }

  async createStickyNote(boardId: number, payload: CreateStickyNotePayload) {
    return this.request<StickyNoteItem>(`/sticky-notes/boards/${boardId}/notes`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateStickyNote(id: number, payload: UpdateStickyNotePayload) {
    return this.requestStickyNote<StickyNoteItem>(`/sticky-notes/notes/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async updateStickyNoteLayout(id: number, payload: UpdateStickyNoteLayoutPayload) {
    return this.requestStickyNote<StickyNoteItem>(`/sticky-notes/notes/${id}/layout`, {
      method: 'PATCH',
      body: JSON.stringify(payload),
    });
  }

  async deleteStickyNote(id: number) {
    return this.request<void>(`/sticky-notes/notes/${id}`, {
      method: 'DELETE',
    });
  }

  async restoreStickyNote(id: number) {
    return this.request<StickyNoteItem>(`/sticky-notes/notes/${id}/restore`, {
      method: 'POST',
    });
  }

  // ===== Task journal =====
  /** Загружает одну страницу задач; сервер применяет фильтры до COUNT и LIMIT. */
  async getTaskJournalItems(params?: TaskJournalListParams, options?: Pick<RequestInit, 'signal'>) {
    const q = buildQuery({
      search: params?.search,
      status: params?.status,
      type: params?.type,
      priority: params?.priority,
      underControl: params?.underControl,
      assigneeUserId: params?.assigneeUserId,
      authorUserId: params?.authorUserId,
      approvalScope: params?.approvalScope,
      metric: params?.metric,
      clientCaseId: params?.clientCaseId,
      clientId: params?.clientId,
      legalEntityId: params?.legalEntityId,
      from: params?.from,
      to: params?.to,
      todayFrom: params?.todayFrom,
      todayTo: params?.todayTo,
      calendarView: params?.calendarView,
      includeDeadlineChangeRequests: params?.includeDeadlineChangeRequests,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 100,
      sortBy: params?.sortBy,
      sortDir: params?.sortDir,
    });
    return this.request<PagedResult<TaskJournalItem>>(`/tasks${q}`, options);
  }

  /** Получает счётчики для карточек журнала без передачи полных задач. */
  async getTaskJournalSummary(
    params?: Omit<TaskJournalListParams, 'metric' | 'page' | 'pageSize' | 'sortBy' | 'sortDir' | 'calendarView' | 'includeDeadlineChangeRequests'>,
    options?: Pick<RequestInit, 'signal'>,
  ) {
    const q = buildQuery({
      search: params?.search,
      status: params?.status,
      type: params?.type,
      priority: params?.priority,
      underControl: params?.underControl,
      assigneeUserId: params?.assigneeUserId,
      authorUserId: params?.authorUserId,
      approvalScope: params?.approvalScope,
      clientCaseId: params?.clientCaseId,
      clientId: params?.clientId,
      legalEntityId: params?.legalEntityId,
      from: params?.from,
      to: params?.to,
      todayFrom: params?.todayFrom,
      todayTo: params?.todayTo,
    });
    return this.request<TaskJournalListSummary>(`/tasks/summary${q}`, options);
  }

  async getTaskJournalAssignees(params?: { calendarView?: boolean }) {
    const q = buildQuery({
      calendarView: params?.calendarView,
    });
    return this.request<TaskJournalAssignee[]>(`/tasks/assignees${q}`);
  }

  async getTaskBillingContext(params: { clientId?: number; legalEntityId?: number; clientCaseId: number }) {
    const q = buildQuery(params);
    return this.request<TaskBillingContext>(`/tasks/billing-context${q}`);
  }

  async getTaskJournalSettings() {
    if (!this.taskJournalSettingsRequest) {
      const request = this.request<TaskJournalSettingsDto>('/tasks/settings');
      this.taskJournalSettingsRequest = request;
      request.then(
        () => {
          if (this.taskJournalSettingsRequest === request) {
            this.taskJournalSettingsRequest = null;
          }
        },
        () => {
          if (this.taskJournalSettingsRequest === request) {
            this.taskJournalSettingsRequest = null;
          }
        },
      );
    }

    return this.taskJournalSettingsRequest;
  }

  async getTaskJournalOverdueSummary() {
    return this.request<{ count: number }>('/tasks/overdue-summary');
  }

  async updateTaskJournalSettings(payload: UpdateTaskJournalSettingsRequest) {
    const response = await this.request<TaskJournalSettingsDto>('/tasks/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
    this.taskJournalSettingsRequest = null;
    return response;
  }

  async getTaskJournalAutomationDiagnostics() {
    return this.request<TaskJournalAutomationDiagnosticsDto>('/tasks/automation/case-control/diagnostics');
  }

  async previewTaskJournalCaseAutomation(inactivityDays: number, escalationDays: number) {
    const q = buildQuery({ inactivityDays, escalationDays });
    return this.request<TaskJournalAutomationPreviewDto>(`/tasks/automation/case-control/preview${q}`);
  }

  async getTaskJournalCaseAutomationCandidates(
    params?: { inactiveDays?: number; limit?: number; escalationDays?: number; search?: string },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery({
      inactiveDays: params?.inactiveDays,
      limit: params?.limit,
      escalationDays: params?.escalationDays,
      search: params?.search,
    });
    return this.request<TaskJournalCaseAutomationCandidate[]>(`/tasks/automation/case-candidates${q}`, {
      signal: options?.signal,
    });
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

  async downloadTaskJournalCalendar(params?: {
    from?: string;
    to?: string;
    clientCaseId?: number;
    clientId?: number;
    legalEntityId?: number;
    reminderMinutes?: number[];
  }) {
    const q = buildQuery({
      from: params?.from,
      to: params?.to,
      clientCaseId: params?.clientCaseId,
      clientId: params?.clientId,
      legalEntityId: params?.legalEntityId,
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
      domain: params?.domain,
      requiresAction: params?.requiresAction,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 25,
    });
    return this.request<PagedResult<UserNotificationDto>>(`/notifications/inbox${q}`);
  }

  async getUnreadUserNotificationCount() {
    return this.request<number>('/notifications/inbox/unread-count');
  }

  async markUserNotificationRead(id: number, options?: { suppressAuthSessionExpired?: boolean }) {
    return this.request<UserNotificationDto>(`/notifications/inbox/${id}/read`, {
      method: 'PATCH',
      suppressAuthSessionExpired: options?.suppressAuthSessionExpired,
    });
  }

  async markUserNotificationUnread(id: number) {
    return this.request<UserNotificationDto>(`/notifications/inbox/${id}/unread`, {
      method: 'PATCH',
    });
  }

  async markAllUserNotificationsRead(params?: Pick<UserNotificationsQuery, 'status' | 'type' | 'search' | 'dateFrom' | 'dateTo' | 'employeeId' | 'clientId' | 'taskRole' | 'domain' | 'requiresAction'>) {
    const q = buildQuery({
      status: params?.status,
      type: params?.type,
      search: params?.search,
      dateFrom: params?.dateFrom,
      dateTo: params?.dateTo,
      employeeId: params?.employeeId,
      clientId: params?.clientId,
      taskRole: params?.taskRole,
      domain: params?.domain,
      requiresAction: params?.requiresAction,
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

  async unarchiveUserNotification(id: number) {
    return this.request<UserNotificationDto>(`/notifications/inbox/${id}/unarchive`, {
      method: 'PATCH',
    });
  }

  async snoozeUserNotification(id: number, minutes = 30) {
    return this.request<UserNotificationDto>(`/notifications/inbox/${id}/snooze`, {
      method: 'PATCH',
      body: JSON.stringify({ minutes }),
    });
  }

  async archiveUserNotifications(ids: number[]) {
    return this.request<UserNotificationBulkActionResultDto>('/notifications/inbox/bulk/archive', {
      method: 'POST',
      body: JSON.stringify({ ids }),
    });
  }

  async unarchiveUserNotifications(ids: number[]) {
    return this.request<UserNotificationBulkActionResultDto>('/notifications/inbox/bulk/unarchive', {
      method: 'POST',
      body: JSON.stringify({ ids }),
    });
  }

  async deleteUserNotification(id: number) {
    return this.request<void>(`/notifications/inbox/${id}`, {
      method: 'DELETE',
    });
  }

  async deleteUserNotifications(ids: number[]) {
    return this.request<UserNotificationBulkActionResultDto>('/notifications/inbox/bulk/delete', {
      method: 'POST',
      body: JSON.stringify({ ids }),
    });
  }

  async getWebPushPublicKey() {
    return this.request<WebPushPublicKeyDto>('/notifications/inbox/push/public-key');
  }

  async getUserPushSubscriptions() {
    return this.request<UserPushSubscriptionDto[]>('/notifications/inbox/push/subscriptions');
  }

  async getWebPushDiagnostics(options?: { suppressAuthSessionExpired?: boolean }) {
    return this.request<WebPushDiagnosticsDto>('/notifications/inbox/push/diagnostics', {
      suppressAuthSessionExpired: options?.suppressAuthSessionExpired,
    });
  }

  async upsertUserPushSubscription(payload: UpsertPushSubscriptionRequest) {
    return this.request<UserPushSubscriptionDto>('/notifications/inbox/push/subscriptions', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async revokeUserPushSubscription(id: number) {
    return this.request<void>(`/notifications/inbox/push/subscriptions/${id}`, {
      method: 'DELETE',
    });
  }

  async revokeUserPushSubscriptionByEndpoint(endpoint: string) {
    return this.request<void>('/notifications/inbox/push/subscriptions/revoke', {
      method: 'POST',
      body: JSON.stringify({ endpoint }),
    });
  }

  async getTaskJournalTemplates(params?: {
    search?: string;
    active?: boolean;
    type?: string;
    priority?: string;
    bindingType?: TaskJournalTemplateBindingType | 'all';
    assigneeUserId?: number;
  }) {
    const q = buildQuery({
      search: params?.search,
      active: params?.active,
      type: params?.type,
      priority: params?.priority,
      bindingType: params?.bindingType,
      assigneeUserId: params?.assigneeUserId,
    });
    return this.request<TaskJournalTemplate[]>(`/tasks/templates${q}`);
  }

  async getTaskJournalTemplate(id: number) {
    return this.request<TaskJournalTemplate>(`/tasks/templates/${id}`);
  }

  async createTaskJournalTemplate(payload: TaskJournalTemplateUpsertPayload) {
    return this.request<TaskJournalTemplate>('/tasks/templates', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateTaskJournalTemplate(id: number, payload: TaskJournalTemplateUpsertPayload) {
    return this.request<TaskJournalTemplate>(`/tasks/templates/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async updateTaskJournalTemplateActive(id: number, isActive: boolean) {
    return this.request<TaskJournalTemplate>(`/tasks/templates/${id}/active`, {
      method: 'PATCH',
      body: JSON.stringify({ isActive }),
    });
  }

  async deleteTaskJournalTemplate(id: number) {
    return this.request<void>(`/tasks/templates/${id}`, { method: 'DELETE' });
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

  async updateTaskJournalStatus(id: number, status: TaskJournalStatus, result?: string, dueAt?: string) {
    return this.request<TaskJournalItem>(`/tasks/${id}/status`, {
      method: 'PATCH',
      body: JSON.stringify({ status, result, dueAt }),
    });
  }

  async updateTaskJournalControl(id: number, isUnderControl: boolean) {
    return this.request<TaskJournalItem>(`/tasks/${id}/control`, {
      method: 'PATCH',
      body: JSON.stringify({ isUnderControl }),
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

  async updateTaskJournalApproval(
    id: number,
    decision: 'approved' | 'revisionRequested',
    resolution?: string,
    participantId?: number,
    finalAction?: 'complete' | 'returnToAssignee',
    revisionDueAt?: string,
  ) {
    return this.request<TaskJournalItem>(`/tasks/${id}/approval`, {
      method: 'PATCH',
      body: JSON.stringify({ decision, resolution, participantId, finalAction, revisionDueAt }),
    });
  }

  async getTaskJournalComments(id: number) {
    return this.request<TaskJournalComment[]>(`/tasks/${id}/comments`);
  }

  async createTaskJournalComment(id: number, text: string, isExternalCalendarVisible = false) {
    return this.request<TaskJournalComment>(`/tasks/${id}/comments`, {
      method: 'POST',
      body: JSON.stringify({ text, isExternalCalendarVisible }),
    });
  }

  async createTaskJournalDeadlineChangeRequest(id: number, payload: { requestedDueAt: string; reason?: string | null }) {
    return this.request<TaskJournalComment>(`/tasks/${id}/deadline-change-requests`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getTaskJournalDeadlineChangeRequests(id: number) {
    return this.request<TaskJournalDeadlineChangeRequest[]>(`/tasks/${id}/deadline-change-requests`);
  }

  async getTaskJournalDeadlineChangeRequest(id: number, requestId: number) {
    return this.request<TaskJournalDeadlineChangeRequest>(`/tasks/${id}/deadline-change-requests/${requestId}`);
  }

  async approveTaskJournalDeadlineChangeRequest(id: number, requestId: number, payload: { approvedDueAt: string; comment?: string | null }) {
    return this.request<TaskJournalItem>(`/tasks/${id}/deadline-change-requests/${requestId}/approve`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async rejectTaskJournalDeadlineChangeRequest(id: number, requestId: number, payload: { comment: string }) {
    return this.request<TaskJournalItem>(`/tasks/${id}/deadline-change-requests/${requestId}/reject`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getTaskJournalFiles(id: number, options: { latestOnly?: boolean } = {}) {
    const query = options.latestOnly ? '?latestOnly=true' : '';
    return this.request<TaskJournalFile[]>(`/tasks/${id}/files${query}`);
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
    sortBy?: PaymentSortKey;
    sortDir?: SortDir;
  }, options?: { signal?: AbortSignal }): Promise<Payment[]>;
  async getPayments(
    a?: string | {
      from?: string;
      to?: string;
      clientId?: number;
      legalEntityId?: number;
      caseId?: number;
      clientRole?: ClientRole;
      excludeInstallmentParents?: boolean;
      sortBy?: PaymentSortKey;
      sortDir?: SortDir;
    },
    b?: string | { signal?: AbortSignal },
    c?: number,
  ): Promise<Payment[]> {
    let q = '';
    let signal: AbortSignal | undefined;
    if (typeof a === 'object') {
      q = buildQuery({
        from: a?.from,
        to: a?.to,
        clientId: a?.clientId,
        legalEntityId: a?.legalEntityId,
        caseId: a?.caseId,
        clientRole: a?.clientRole,
        excludeInstallmentParents: a?.excludeInstallmentParents,
        sortBy: a?.sortBy,
        sortDir: a?.sortDir,
      });
      signal = typeof b === 'object' ? b.signal : undefined;
    } else {
      // старый сигнатурный стиль: (from?, to?, clientId?)
      q = buildQuery({ from: a, to: typeof b === 'string' ? b : undefined, clientId: c });
    }
    return this.request<Payment[]>(`/payments${q}`, { signal });
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

  async acceptPaymentAdvance(id: number, reason?: string | null) {
    return this.request<Payment>(`/payments/${id}/advance-review/accept`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  async ignorePaymentAdvance(id: number, reason?: string | null) {
    return this.request<Payment>(`/payments/${id}/advance-review/ignore`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  async refundPaymentAdvance(id: number, reason?: string | null) {
    return this.request<Payment>(`/payments/${id}/advance-review/refund`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  async getPaymentAdvanceDiagnostics(params?: {
    q?: string;
    from?: string;
    to?: string;
    amount?: number;
    amountTolerance?: number;
    limit?: number;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<PaymentAdvanceReviewDiagnosticsResponse>(`/payments/advance-review/diagnostics${q}`);
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
  async getPaymentsV2(
    params?: {
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
      advanceReviewStatus?: PaymentAdvanceReviewStatus;
      minAmount?: number;
      maxAmount?: number;
      search?: string;
      sortBy?: 'date' | 'amount' | 'createdAt';
      sortDir?: SortDir;
      page?: number;
      pageSize?: number;
    },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PagedResult<Payment> & { summary: PaymentsListSummary }>(
      `/v2/payments${q}`,
      { signal: options?.signal },
    );
  }

  async exportPaymentsCsv(params?: PaymentsExportParams) {
    const q = buildQuery(params ?? {});
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/v2/payments/export.csv${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/v2/payments/export.csv ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'payments-export.csv',
    };
  }

  async exportPaymentsExcel(params?: PaymentsExportParams) {
    const q = buildQuery(params ?? {});
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/v2/payments/export.xls${q}`, {
      method: 'GET',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });

    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(
        `HTTP ${response.status} ${response.statusText} at ${API_BASE_URL}/v2/payments/export.xls ${text ? `- ${text}` : ''}`,
      );
    }

    return {
      blob: await response.blob(),
      fileName:
        parseContentDispositionFileName(response.headers.get('content-disposition')) ??
        'payments-export.xls',
    };
  }

  // ===== Clients =====
  async getClients(options?: { signal?: AbortSignal }) {
    return this.request<Client[]>('/clients', {
      signal: options?.signal,
    });
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

  async applyClientBalance(id: number, payload: { paymentId: number; amount: number; description?: string | null; sourcePaymentId?: number | null }) {
    return this.request<ApplyClientBalanceResult>(`/clients/${id}/balance/apply`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getLegalEntityBalance(id: number) {
    return this.request<ClientBalanceSummary>(`/legal-entities/${id}/balance`);
  }

  async getLegalEntityBalanceTransactions(id: number) {
    return this.request<ClientBalanceTransaction[]>(`/legal-entities/${id}/balance/transactions`);
  }

  async applyLegalEntityBalance(id: number, payload: { paymentId: number; amount: number; description?: string | null; sourcePaymentId?: number | null }) {
    return this.request<ApplyClientBalanceResult>(`/legal-entities/${id}/balance/apply`, {
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

  async getOkinDictionary(
    params?: { search?: string; limit?: number },
    options?: { signal?: AbortSignal },
  ) {
    const query = buildQuery({ search: params?.search, limit: params?.limit ?? 30 });
    return this.request<ClientReferenceItem[]>(`/client-reference-data/okin${query}`, {
      signal: options?.signal,
    });
  }

  async getOksoDictionary(
    params?: { search?: string; limit?: number },
    options?: { signal?: AbortSignal },
  ) {
    const query = buildQuery({ search: params?.search, limit: params?.limit ?? 30 });
    return this.request<ClientReferenceItem[]>(`/client-reference-data/okso${query}`, {
      signal: options?.signal,
    });
  }

  async getOkpdtrDictionary(
    params?: { search?: string; limit?: number },
    options?: { signal?: AbortSignal },
  ) {
    const query = buildQuery({ search: params?.search, limit: params?.limit ?? 30 });
    return this.request<ClientReferenceItem[]>(`/client-reference-data/okpdtr${query}`, {
      signal: options?.signal,
    });
  }

  // ===== Legal entities =====
  async getLegalEntities(params?: { search?: string; counterpartyRole?: 'client' | 'supplier' }) {
    const q = buildQuery({
      search: params?.search,
      counterpartyRole: params?.counterpartyRole,
    });
    return this.request<LegalEntitySummary[]>(`/legal-entities${q}`);
  }

  async getLegalEntitiesPaged(
    params?: { search?: string; page?: number; pageSize?: number; counterpartyRole?: 'client' | 'supplier' },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery({
      search: params?.search,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
      counterpartyRole: params?.counterpartyRole,
    });
    return this.request<PagedResult<LegalEntitySummary>>(`/legal-entities/paged${q}`, {
      signal: options?.signal,
    });
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
    return this.request<CleanedAddress>('/clients/dadata/clean-address', {
      method: 'POST',
      body: JSON.stringify({ address }),
    });
  }


  async getDadataIntegrationSettings() {
    return this.request<DadataIntegrationSettingsDto>('/integrations/dadata/settings');
  }


  async updateDadataIntegrationSettings(payload: UpdateDadataIntegrationSettingsRequest) {
    return this.request<DadataIntegrationSettingsDto>('/integrations/dadata/settings', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
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

  async getClientsV2(
    params?: {
      search?: string;
      isActive?: boolean;
      sortBy?: 'name' | 'createdAt';
      sortDir?: SortDir;
      page?: number;
      pageSize?: number;
      counterpartyRole?: 'client' | 'supplier';
      [key: string]: string | number | boolean | undefined;
    },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PagedResult<Client>>(`/v2/clients${q}`, {
      signal: options?.signal,
    });
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

  async updateCaseControl(id: number, isUnderControl: boolean) {
    return this.request<ClientCase>(`/v2/cases/${id}/control`, {
      method: 'PATCH',
      body: JSON.stringify({ isUnderControl }),
    });
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
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<ClientCase[]>(`/v1/cases${q}`, { signal: options?.signal });
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
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PagedResult<ClientCase>>(`/v2/cases${q}`, {
      signal: options?.signal,
    });
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
    inactivityRemindersDisabledReason?: string;
    isUnderControl?: boolean;
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

  async getCaseAccessRequests(options?: { signal?: AbortSignal }) {
    return this.request<ClientCaseAccessRequest[]>('/v2/cases/access-requests', {
      signal: options?.signal,
    });
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

  async addCaseConversationEntry(caseId: number, payload: { text: string; files?: File[] }) {
    if (payload.files?.length) {
      const form = new FormData();
      form.append('text', payload.text);
      payload.files.forEach((file) => form.append('files', file));
      return this.request<ClientCaseConversationEntry[]>(`/v2/cases/${caseId}/conversation`, {
        method: 'POST',
        body: form,
      });
    }

    return this.request<ClientCaseConversationEntry[]>(`/v2/cases/${caseId}/conversation`, {
      method: 'POST',
      body: JSON.stringify({ text: payload.text }),
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

  async createCaseWorkOrderAct(caseId: number, payload: {
    number?: string;
    date?: string | null;
    title?: string;
    comment?: string;
  } = {}) {
    return this.request<ClientCaseWorkOrderAct>(`/v2/cases/${caseId}/work-order/create-act`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async createCaseWorkOrderContract(caseId: number, payload: {
    number?: string;
    date?: string | null;
    startDate?: string | null;
    validUntil?: string | null;
    isOpenEnded?: boolean | null;
    title?: string;
    description?: string;
  } = {}) {
    return this.request<ClientCaseWorkOrderContract>(`/v2/cases/${caseId}/work-order/create-contract`, {
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

  async getExternalWorkOrderApproval(caseId: number) {
    return this.request<ExternalWorkOrderApprovalStatus | undefined>(
      `/v2/cases/${caseId}/work-order/external-approval`,
    );
  }

  async createExternalWorkOrderApproval(caseId: number, payload: {
    recipientName: string;
    recipientEmail: string;
    recipientType: 'client' | 'representative';
    documentIds?: number[];
    deliveryChannel?: 'email' | 'email_and_max';
  }) {
    return this.request<ExternalWorkOrderApprovalStatus>(
      `/v2/cases/${caseId}/work-order/external-approval`,
      { method: 'POST', body: JSON.stringify(payload) },
    );
  }

  async createPublicWorkOrderAttachmentGrant(publicId: string, attachmentPublicId: string, token: string) {
    return this.request<{ grantToken: string; expiresAt: string }>(
      `/public/work-order-approvals/${encodeURIComponent(publicId)}/attachments/${encodeURIComponent(attachmentPublicId)}/grant`,
      {
        method: 'POST',
        body: JSON.stringify({ token }),
        suppressAuthSessionExpired: true,
      },
    );
  }

  async downloadPublicWorkOrderAttachment(publicId: string, attachmentPublicId: string, grantToken: string) {
    const response = await fetch(
      `${API_BASE_URL}/public/work-order-approvals/${encodeURIComponent(publicId)}/attachments/${encodeURIComponent(attachmentPublicId)}/download`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ grantToken }),
        cache: 'no-store',
        referrerPolicy: 'no-referrer',
      },
    );
    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }
    return response.blob();
  }

  async revokeExternalWorkOrderApproval(caseId: number) {
    return this.request<void>(`/v2/cases/${caseId}/work-order/external-approval`, { method: 'DELETE' });
  }

  async openPublicWorkOrderApproval(publicId: string, token: string) {
    return this.request<PublicWorkOrderApproval>(
      `/public/work-order-approvals/${encodeURIComponent(publicId)}/open`,
      {
        method: 'POST',
        body: JSON.stringify({ token }),
        suppressAuthSessionExpired: true,
      },
    );
  }

  async decidePublicWorkOrderApproval(publicId: string, payload: {
    token: string;
    otp: string;
    decision: 'approved' | 'revision_requested';
    comment?: string;
  }) {
    return this.request<PublicWorkOrderApprovalDecision>(
      `/public/work-order-approvals/${encodeURIComponent(publicId)}/decision`,
      {
        method: 'POST',
        body: JSON.stringify(payload),
        suppressAuthSessionExpired: true,
      },
    );
  }

  async previewClientPortalLogin(challengeId: string, token: string) {
    return this.request<ClientPortalLoginPreview>(
      `/portal/auth/${encodeURIComponent(challengeId)}/preview`,
      { method: 'POST', body: JSON.stringify({ token }), suppressAuthSessionExpired: true },
    );
  }

  async verifyClientPortalLogin(challengeId: string, token: string, otp: string) {
    return this.request<{ expiresAt: string }>(
      `/portal/auth/${encodeURIComponent(challengeId)}/verify`,
      { method: 'POST', body: JSON.stringify({ token, otp }), suppressAuthSessionExpired: true },
    );
  }

  async getClientPortalHome(page = 1, pageSize = 20, search = '', status = '') {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (search.trim()) params.set('search', search.trim());
    if (status.trim()) params.set('status', status.trim());
    return this.request<ClientPortalHome>(
      `/portal/home?${params.toString()}`,
      { suppressAuthSessionExpired: true },
    );
  }

  async getClientPortalProfile() {
    return this.request<ClientPortalProfile>('/portal/profile', { suppressAuthSessionExpired: true });
  }

  async createClientPortalMaxLink() {
    return this.request<{ linkId: string; payload: string; deepLinkUrl?: string | null; expiresAt: string }>(
      '/portal/max/link',
      { method: 'POST', headers: { 'X-Portal-Request': '1' }, suppressAuthSessionExpired: true },
    );
  }

  async deleteClientPortalMaxLink() {
    return this.request<void>('/portal/max/link', {
      method: 'DELETE', headers: { 'X-Portal-Request': '1' }, suppressAuthSessionExpired: true,
    });
  }

  async getClientPortalCase(publicId: string) {
    return this.request<ClientPortalCase>(
      `/portal/cases/${encodeURIComponent(publicId)}`,
      { suppressAuthSessionExpired: true },
    );
  }

  async createClientPortalDocumentGrant(publicId: string) {
    return this.request<{ grantToken: string; expiresAt: string }>(
      `/portal/documents/${encodeURIComponent(publicId)}/grant`,
      {
        method: 'POST',
        headers: { 'X-Portal-Request': '1' },
        suppressAuthSessionExpired: true,
      },
    );
  }

  async downloadClientPortalDocument(publicId: string, grantToken: string) {
    const response = await fetch(`${API_BASE_URL}/portal/documents/${encodeURIComponent(publicId)}/download`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json', 'X-Portal-Request': '1' },
      body: JSON.stringify({ grantToken }),
      cache: 'no-store',
      referrerPolicy: 'no-referrer',
    });
    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }
    return response.blob();
  }

  async logoutClientPortal(all = false) {
    return this.request<void>(
      `/portal/logout?all=${all}`,
      { method: 'POST', headers: { 'X-Portal-Request': '1' }, suppressAuthSessionExpired: true },
    );
  }

  async getClientPortalCaseAccess(caseId: number) {
    return this.request<ClientPortalCaseAccess[]>(`/portal/admin/cases/${caseId}/accounts`);
  }

  async getClientPortalAudit(caseId: number) {
    return this.request<ClientPortalAudit[]>(`/portal/admin/cases/${caseId}/audit`);
  }

  async saveClientPortalAccess(payload: ClientPortalAccessRequest) {
    return this.request<{ id: number }>('/portal/admin/access', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async sendClientPortalLogin(accountId: number) {
    return this.request<{ challengeId: string; expiresAt: string; emailHint: string }>(
      `/portal/admin/accounts/${accountId}/login`,
      { method: 'POST' },
    );
  }

  async revokeClientPortalCaseAccess(accountId: number, caseId: number, reason?: string) {
    return this.request<void>(`/portal/admin/accounts/${accountId}/cases/${caseId}`, {
      method: 'DELETE',
      body: JSON.stringify({ reason }),
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

  async searchCaseCourtSyncCases(caseId: number, payload: ClientCaseCourtSyncSearchRequest) {
    return this.request<ClientCaseCourtSyncSearchResult[]>(`/v2/cases/${caseId}/court-sync/search`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async bindCaseCourtSyncCase(caseId: number, payload: ClientCaseCourtSyncBindRequest) {
    return this.request<ClientCaseCourtSyncBinding>(`/v2/cases/${caseId}/court-sync/bind`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async getCaseCourtSyncLogs(caseId: number) {
    return this.request<ClientCaseCourtSyncLog[]>(`/v2/cases/${caseId}/court-sync/logs`);
  }

  async getCourtSyncJournal(params: {
    take?: number;
    status?: string;
    search?: string;
    provider?: string;
    hasError?: boolean;
    maxAgeDays?: number;
    minAgeDays?: number;
  } = {}) {
    const query = new URLSearchParams();
    if (params.take) query.set('take', String(params.take));
    if (params.status) query.set('status', params.status);
    if (params.search) query.set('search', params.search);
    if (params.provider) query.set('provider', params.provider);
    if (params.hasError !== undefined) query.set('hasError', String(params.hasError));
    if (params.maxAgeDays !== undefined) query.set('maxAgeDays', String(params.maxAgeDays));
    if (params.minAgeDays !== undefined) query.set('minAgeDays', String(params.minAgeDays));
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

  async getCaseDocumentTemplateHistory() {
    return this.request<DocumentTemplateVersionDto[]>('/v2/cases/document-templates/history');
  }

  async getCaseDocumentTemplateVersion(versionId: number) {
    return this.request<ClientCaseDocumentTemplate[]>(`/v2/cases/document-templates/history/${versionId}`);
  }

  async restoreCaseDocumentTemplateVersion(versionId: number) {
    return this.request<ClientCaseDocumentTemplate[]>(`/v2/cases/document-templates/history/${versionId}/restore`, {
      method: 'POST',
    });
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

  async getMessengerDocumentDeliveryConfiguration() {
    return this.request<MessengerDocumentDeliveryConfiguration>('/messenger-document-deliveries/configuration');
  }

  async getMessengerRecipients(clientId: number) {
    const query = buildQuery({ clientId });
    return this.request<MessengerRecipientBindingDto[]>(`/messenger-document-deliveries/recipients${query}`);
  }

  async createMessengerRecipient(payload: {
    clientId: number;
    clientPortalAccountId?: number | null;
    channel: string;
    provider: string;
    displayName: string;
    address: string;
    verificationMethod: string;
    confirmContactOwnership: boolean;
  }) {
    return this.request<MessengerRecipientBindingDto>('/messenger-document-deliveries/recipients', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async prepareCaseDocumentMessengerDelivery(documentId: number, payload: {
    recipientBindingId: number;
    message?: string;
    confirmRecipientAndDocument: boolean;
  }) {
    return this.request<PreparedMessengerDocumentDeliveryDto>(
      `/messenger-document-deliveries/case-documents/${documentId}/prepare`,
      { method: 'POST', body: JSON.stringify(payload) },
    );
  }

  async prepareFinancialDocumentMessengerDelivery(
    documentType: 'invoice' | 'act' | 'contract',
    documentId: number,
    pdf: Blob,
    payload: {
      recipientBindingId: number;
      message?: string;
      confirmRecipientAndDocument: boolean;
    },
  ) {
    const form = new FormData();
    form.append('file', pdf, `${documentType}-${documentId}.pdf`);
    form.append('recipientBindingId', String(payload.recipientBindingId));
    form.append('confirmRecipientAndDocument', String(payload.confirmRecipientAndDocument));
    if (payload.message) form.append('message', payload.message);
    return this.request<PreparedMessengerDocumentDeliveryDto>(
      `/messenger-document-deliveries/financial-documents/${documentType}/${documentId}/prepare`,
      { method: 'POST', body: form },
    );
  }

  async queueMessengerDocumentDelivery(deliveryId: number) {
    return this.request<{ id: number; status: string; queuedAt?: string | null; nextAttemptAt?: string | null }>(
      `/messenger-document-deliveries/${deliveryId}/queue`,
      { method: 'POST' },
    );
  }

  async getMessengerDocumentDeliveryHistory(clientId: number) {
    const query = buildQuery({ clientId });
    return this.request<MessengerDocumentDeliveryDto[]>(`/messenger-document-deliveries${query}`);
  }

  /**
   * Скачивает одноразовый публичный документ. Секрет передаётся только в теле
   * POST и не попадает в адресную строку, referrer или журналы веб-сервера.
   */
  async downloadPublicMessengerDocument(publicId: string, token: string) {
    const response = await fetch(
      `${API_BASE_URL}/public/messenger-document-deliveries/${encodeURIComponent(publicId)}/download`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token }),
        cache: 'no-store',
        referrerPolicy: 'no-referrer',
      },
    );
    if (!response.ok) {
      const text = await extractResponseErrorMessage(response);
      throw new Error(text || `HTTP ${response.status} ${response.statusText}`);
    }

    return {
      blob: await response.blob(),
      fileName: parseContentDispositionFileName(response.headers.get('content-disposition')) || 'document.pdf',
    };
  }

  async getElectronicSignatureConfiguration() {
    return this.request<ElectronicSignatureConfigurationDto>('/electronic-signatures/configuration');
  }

  async getElectronicSignatureRecipients(clientId: number) {
    const query = buildQuery({ clientId });
    return this.request<ElectronicSignatureRecipientDto[]>(`/electronic-signatures/recipients${query}`);
  }

  async getElectronicSignatureHistory(documentType: 'invoice' | 'act' | 'contract', documentId: number) {
    return this.request<ElectronicSignatureEnvelopeDto[]>(`/electronic-signatures/financial-documents/${documentType}/${documentId}`);
  }

  async prepareElectronicSignatureEnvelope(
    documentType: 'invoice' | 'act' | 'contract',
    documentId: number,
    pdf: Blob,
    payload: {
      clientId: number;
      signerAccountIds: number[];
      signatureType: string;
      signingOrder: string;
      provider: string;
      archiveRetentionYears: number;
      requiresMachineReadablePowerOfAttorney: boolean;
      powerOfAttorneyReference?: string;
      confirmSnapshotAndSigners: boolean;
    },
  ) {
    const form = new FormData();
    form.append('file', pdf, `${documentType}-${documentId}.pdf`);
    form.append('clientId', String(payload.clientId));
    payload.signerAccountIds.forEach((id) => form.append('signerAccountIds', String(id)));
    form.append('signatureType', payload.signatureType);
    form.append('signingOrder', payload.signingOrder);
    form.append('provider', payload.provider);
    form.append('archiveRetentionYears', String(payload.archiveRetentionYears));
    form.append('requiresMachineReadablePowerOfAttorney', String(payload.requiresMachineReadablePowerOfAttorney));
    form.append('confirmSnapshotAndSigners', String(payload.confirmSnapshotAndSigners));
    if (payload.powerOfAttorneyReference) form.append('powerOfAttorneyReference', payload.powerOfAttorneyReference);
    return this.request<ElectronicSignatureEnvelopeDto>(
      `/electronic-signatures/financial-documents/${documentType}/${documentId}/prepare`,
      { method: 'POST', body: form },
    );
  }

  async cancelElectronicSignatureEnvelope(publicId: string, reason?: string) {
    return this.request<ElectronicSignatureEnvelopeDto>(`/electronic-signatures/${publicId}/cancel`, {
      method: 'POST',
      body: JSON.stringify({ reason: reason?.trim() || null }),
    });
  }

  async downloadElectronicSignatureEvidencePackage(publicId: string) {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/electronic-signatures/${encodeURIComponent(publicId)}/evidence-package`, {
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
  async getInvoiceReferenceDictionaries() {
    return this.request<InvoiceReferenceDictionaries>('/invoices/reference-dictionaries');
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
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<SummaryStats>(`/v2/stats/summary${q}`, { signal: options?.signal });
  }

  async getCashFlowReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CashFlowReport>(`/reports/cash-flow${q}`, { signal: options?.signal });
  }

  async getCashFlowForecastReport(params?: {
    from?: string;
    to?: string;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CashFlowForecastReport>(`/reports/cash-flow-forecast${q}`, { signal: options?.signal });
  }

  async getMoneyCategoryReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<MoneyCategoryReport>(`/reports/categories${q}`, { signal: options?.signal });
  }

  async getCounterpartyReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CounterpartyReport>(`/reports/counterparties${q}`, { signal: options?.signal });
  }

  async getProjectReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<ProjectReport>(`/reports/projects${q}`, { signal: options?.signal });
  }

  async getLegalWorkReport(params?: {
    from?: string;
    to?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<LegalWorkReport>(`/reports/legal-work${q}`, { signal: options?.signal });
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

  async getLegalWorkReportPrintHtml(params?: {
    from?: string;
    to?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<string>(`/reports/legal-work.print${q}`);
  }

  async downloadManagementReport(params: {
    report: 'case-finance' | 'case-success' | 'current-cases' | 'tasks' | 'lawyer-workload' | 'overdue-deadlines' | 'case-activity' | 'cash-flow' | 'cash-flow-forecast' | 'cash' | 'bank' | 'plan-fact' | 'advances' | 'payment-priorities' | 'paid-accounts' | 'paid-invoices' | 'invoice-statuses' | 'act-statuses' | 'contract-statuses' | 'categories' | 'counterparties' | 'projects' | 'directions' | 'sales-points' | 'vat' | 'payment-orders' | 'payment-purposes' | 'match-statuses' | 'journal-statuses' | 'currencies' | 'cash-orders' | 'bank-statement-imports' | 'spreadsheet-imports';
    format: 'csv' | 'excel' | 'pdf';
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
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
    const extension = format === 'excel' ? 'xls' : format;
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

  async getManagementReportPrintHtml(params: {
    report: 'case-finance' | 'case-success' | 'current-cases' | 'tasks' | 'lawyer-workload' | 'overdue-deadlines' | 'case-activity' | 'cash-flow' | 'cash-flow-forecast' | 'cash' | 'bank' | 'plan-fact' | 'advances' | 'payment-priorities' | 'paid-accounts' | 'paid-invoices' | 'invoice-statuses' | 'act-statuses' | 'contract-statuses' | 'categories' | 'counterparties' | 'projects' | 'directions' | 'sales-points' | 'vat' | 'payment-orders' | 'payment-purposes' | 'match-statuses' | 'journal-statuses' | 'currencies' | 'cash-orders' | 'bank-statement-imports' | 'spreadsheet-imports';
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
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
    const q = buildQuery(params);
    return this.request<string>(`/reports/management.print${q}`);
  }

  async getCaseActivityReport(params?: {
    inactiveDays?: number;
    escalationDays?: number;
    activity?: 'all' | 'inactive' | 'active' | 'escalated' | 'disabled';
    limit?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CaseActivityReport>(`/reports/case-activity${q}`, { signal: options?.signal });
  }

  async getCurrentCasesReport(params?: {
    asOf?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
    limit?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CurrentCasesReport>(`/reports/current-cases${q}`, { signal: options?.signal });
  }

  async getCaseFinanceReport(params?: {
    from?: string;
    to?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
    limit?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CaseFinanceReport>(`/reports/case-finance${q}`, { signal: options?.signal });
  }

  async getCaseSuccessReport(params?: {
    from?: string;
    to?: string;
    status?: string;
    responsibleUserId?: number;
    caseType?: string;
    limit?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CaseSuccessReport>(`/reports/case-success${q}`, { signal: options?.signal });
  }

  async getLawyerWorkloadReport(params?: {
    asOf?: string;
    horizonDays?: number;
    lawyerUserId?: number;
    limit?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<LawyerWorkloadReport>(`/reports/lawyer-workload${q}`, { signal: options?.signal });
  }

  async getOverdueProceduralDeadlinesReport(params?: {
    asOf?: string;
    deadlineType?: string;
    responsibleUserId?: number;
    limit?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<OverdueProceduralDeadlinesReport>(`/reports/overdue-deadlines${q}`, { signal: options?.signal });
  }

  async getTaskReport(params?: {
    asOf?: string;
    scope?: 'open' | 'closed' | 'all';
    assigneeUserId?: number;
    limit?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<TaskReport>(`/reports/tasks${q}`, { signal: options?.signal });
  }

  async getDirectionReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<DirectionReport>(`/reports/directions${q}`, { signal: options?.signal });
  }

  async getSalesPointReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<SalesPointReport>(`/reports/sales-points${q}`, { signal: options?.signal });
  }

  async getVatReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<VatReport>(`/reports/vat${q}`, { signal: options?.signal });
  }

  async getPlanFactReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<PlanFactReport>(`/reports/plan-fact${q}`, { signal: options?.signal });
  }

  async getAdvanceReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<AdvanceReport>(`/reports/advances${q}`, { signal: options?.signal });
  }

  async getPaymentPriorityReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<PaymentPriorityReport>(`/reports/payment-priorities${q}`, { signal: options?.signal });
  }

  async getPaidAccountReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<PaidAccountReport>(`/reports/paid-accounts${q}`, { signal: options?.signal });
  }

  async getPaidInvoiceReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<PaidInvoiceReport>(`/reports/paid-invoices${q}`, { signal: options?.signal });
  }

  async getInvoiceStatusReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<InvoiceStatusReport>(`/reports/invoice-statuses${q}`, { signal: options?.signal });
  }

  async getActStatusReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<ActStatusReport>(`/reports/act-statuses${q}`, { signal: options?.signal });
  }

  async getContractStatusReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<ContractStatusReport>(`/reports/contract-statuses${q}`, { signal: options?.signal });
  }

  async getPaymentOrderReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<PaymentOrderReport>(`/reports/payment-orders${q}`, { signal: options?.signal });
  }

  async getPaymentPurposeReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<PaymentPurposeReport>(`/reports/payment-purposes${q}`, { signal: options?.signal });
  }

  async getMatchStatusReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<MatchStatusReport>(`/reports/match-statuses${q}`, { signal: options?.signal });
  }

  async getJournalStatusReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<JournalStatusReport>(`/reports/journal-statuses${q}`, { signal: options?.signal });
  }

  async getCurrencyReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CurrencyReport>(`/reports/currencies${q}`, { signal: options?.signal });
  }

  async getCashOrderReport(params?: {
    from?: string;
    to?: string;
    source?: MoneyOperationSource;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<CashOrderReport>(`/reports/cash-orders${q}`, { signal: options?.signal });
  }

  async getBankStatementImportReport(params?: {
    from?: string;
    to?: string;
    accountName?: string;
    counterpartyName?: string;
    clientId?: number;
    caseId?: number;
    contractId?: number;
    incomeTypeId?: number;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<BankStatementImportReport>(`/reports/bank-statement-imports${q}`, { signal: options?.signal });
  }

  async getSpreadsheetImportReport(params?: {
    from?: string;
    to?: string;
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery(params ?? {});
    return this.request<SpreadsheetImportReport>(`/reports/spreadsheet-imports${q}`, { signal: options?.signal });
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

  async getMoneyOperationsPaged(
    params?: {
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
    },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery(params ?? {});
    return this.request<PagedResult<MoneyOperation> & { summary: MoneyOperationsSummary }>(
      `/money-operations/paged${q}`,
      { signal: options?.signal },
    );
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

  async getCashBook(
    params?: {
      from?: string;
      to?: string;
      accountName?: string;
      counterpartyName?: string;
      clientId?: number;
      caseId?: number;
      contractId?: number;
      incomeTypeId?: number;
    },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery(params ?? {});
    return this.request<CashBookSummary>(`/cash-orders/cashbook${q}`, { signal: options?.signal });
  }

  async getCashBookPrintHtml(params?: { from?: string; to?: string; accountName?: string }) {
    const q = buildQuery(params ?? {});
    return this.request<string>(`/cash-orders/cashbook/print${q}`);
  }

  async getCashOrderRegister(
    params?: {
      from?: string;
      to?: string;
      type?: 'Incoming' | 'Outgoing';
      status?: 'Draft' | 'Posted' | 'Cancelled';
      search?: string;
      accountName?: string;
      page?: number;
      pageSize?: number;
    },
    options?: { signal?: AbortSignal },
  ) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 25,
    });
    return this.request<CashOrderRegister>(`/cash-orders/register${q}`, { signal: options?.signal });
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

  async getCashOrderPrintHtmlForPayment(paymentId: number) {
    return this.request<string>(`/cash-orders/payment/${paymentId}/print`);
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

  async downloadCashOrderWord(id: number) {
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/cash-orders/${id}/word`, {
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
    fromUtc?: string;
    toUtc?: string;
    userId?: number;
    category?: string;
    action?: string;
    section?: string;
    httpMethod?: string;
    status?: UserActivityStatus;
    search?: string;
    searchAliases?: readonly string[];
  }, options?: { signal?: AbortSignal }) {
    const q = buildQuery({
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
      from: params?.from,
      to: params?.to,
      fromUtc: params?.fromUtc,
      toUtc: params?.toUtc,
      userId: params?.userId,
      category: params?.category,
      action: params?.action,
      section: params?.section,
      httpMethod: params?.httpMethod,
      status: params?.status,
      search: params?.search,
      searchAliases: params?.searchAliases,
    });
    return this.request<UserActivityLogResponse>(`/user-activity${q}`, {
      signal: options?.signal,
    });
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
  ClientCaseCourtSyncBinding,
  ClientCaseCourtSyncBindRequest,
  ClientCaseCourtSyncLog,
  ClientCaseCourtSyncProvider,
  ClientCaseCourtSyncRun,
  ClientCaseCourtSyncRunRequest,
  ClientCaseCourtSyncSearchRequest,
  ClientCaseCourtSyncSearchResult,
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
  ClientCaseWorkOrderAct,
  ClientCaseWorkOrderContract,
  ClientCaseWorkOrderInvoice,
  ExternalWorkOrderApprovalStatus,
  PublicWorkOrderApproval,
  PublicWorkOrderApprovalDecision,
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
  CashFlowForecastReport,
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
  PaymentAdvanceReviewDiagnosticsResponse,
  PaymentAdvanceReviewStatus,
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
  TaskBillingContext,
  TaskJournalComment,
  TaskJournalDeadlineChangeRequest,
  TaskJournalFile,
  TaskJournalBulkUpdatePayload,
  TaskJournalBulkUpdateResult,
  TaskJournalItem,
  TaskJournalNotification,
  TaskJournalStatus,
  TaskJournalTemplate,
  TaskJournalTemplateBindingType,
  TaskJournalTemplateUpsertPayload,
  TaskJournalUpsertPayload,
} from '../utils/taskJournalStore';
