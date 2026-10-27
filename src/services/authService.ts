import { createClientRequestId } from '../utils/clientRequestId';
// src/services/authService.ts

import type { PagedResult } from './api';
import { API_BASE_URL } from './apiBaseUrl';
import { apiFetch as fetch } from './apiTransport';

// ==== storage helpers (bkwd-compat) ====
const KEY_MAIN = 'pp_token';
const KEY_FALLBACKS = ['auth_token', 'pp.jwt'];
const SESSION_REFRESH_WINDOW_MS = 24 * 60 * 60 * 1000;
let sessionRefreshJob: { identity: AuthSessionIdentity; promise: Promise<string | null> } | null = null;

// A technical login generation, shared by tabs; no user values or credentials are stored here.
// Refresh of this same session preserves it. Explicit replacement/logout changes it before awaits.
const SESSION_EPOCH_KEY = 'pp.auth-session-epoch.v1';
export function getAuthSessionEpoch(): string | null {
  if (!readToken()) return null;
  let epoch = localStorage.getItem(SESSION_EPOCH_KEY);
  if (!epoch) {
    epoch = createClientRequestId();
    localStorage.setItem(SESSION_EPOCH_KEY, epoch);
    if (localStorage.getItem(SESSION_EPOCH_KEY) !== epoch) throw new Error('Не удалось подтвердить текущую сессию.');
  }
  return epoch;
}
export type AuthSessionIdentity = Readonly<{ epoch: string | null; actor: string | null; authenticated: boolean }>;
export class AuthSessionChangedError extends Error {
  constructor() { super('Сессия изменилась. Действие предыдущего входа не выполнено.'); }
}
function sessionActor(token: string | null): string | null {
  try {
    const part = token?.split('.')[1];
    if (!part) return null;
    const text = part.replace(/-/g, '+').replace(/_/g, '/');
    const value = JSON.parse(atob(text.padEnd(Math.ceil(text.length / 4) * 4, '='))) as Record<string, unknown>;
    const id = value['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'] ?? value.nameid ?? value.sub;
    return typeof id === 'string' || typeof id === 'number' ? JSON.stringify([value.iss ?? '', String(id)]) : null;
  } catch { return null; }
}
export function captureAuthSessionIdentity(): AuthSessionIdentity {
  const token = readToken();
  return Object.freeze({ epoch: getAuthSessionEpoch(), actor: sessionActor(token), authenticated: Boolean(token) });
}
function sameSession(a: AuthSessionIdentity, b: AuthSessionIdentity): boolean {
  return a.epoch === b.epoch && a.actor === b.actor && a.authenticated === b.authenticated;
}
export function isAuthSessionIdentityCurrent(identity: AuthSessionIdentity): boolean {
  try { return sameSession(identity, captureAuthSessionIdentity()); } catch { return false; }
}
export function assertAuthSessionIdentity(identity: AuthSessionIdentity): void {
  if (!isAuthSessionIdentityCurrent(identity)) throw new AuthSessionChangedError();
}

export function getAuthOperationEpoch(): string | null {
  try { return localStorage.getItem(SESSION_EPOCH_KEY); } catch { return null; }
}
function replaceAuthSessionEpoch(): string {
  const epoch = createClientRequestId();
  localStorage.setItem(SESSION_EPOCH_KEY, epoch);
  if (localStorage.getItem(SESSION_EPOCH_KEY) !== epoch) throw new Error('Не удалось подтвердить текущую сессию.');
  return epoch;
}


function readToken(): string | null {
  return (
    localStorage.getItem(KEY_MAIN) ||
    KEY_FALLBACKS.map((k) => localStorage.getItem(k)).find(Boolean) ||
    sessionStorage.getItem('auth_token') ||
    null
  );
}

function writeToken(token: string) {
  try {
    localStorage.setItem(KEY_MAIN, token);
    // совместимость с прежними ключами
    localStorage.setItem('auth_token', token);
    localStorage.setItem('pp.jwt', token);
  } catch {
    /* ignore */
  }
}

function clearToken() {
  try {
    localStorage.removeItem(KEY_MAIN);
    localStorage.removeItem('auth_token');
    localStorage.removeItem('pp.jwt');
    sessionStorage.removeItem('auth_token');
  } catch {
    /* ignore */
  }
}

function readTokenExpiresAt(token: string): number | null {
  try {
    const payloadPart = token.split('.')[1];
    if (!payloadPart) return null;

    const normalized = payloadPart.replace(/-/g, '+').replace(/_/g, '/');
    const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, '=');
    const payload = JSON.parse(atob(padded)) as { exp?: unknown };
    return typeof payload.exp === 'number' ? payload.exp * 1000 : null;
  } catch {
    return null;
  }
}

// ==== types ====
export interface Role {
  id: number;
  name: string;
  description?: string | null;
  createdAt?: string | null;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
  isSystemRole?: boolean;
  assignedUsersCount?: number;
  accessibleSectionsCount?: number;
  totalSectionsCount?: number;
  permissionsUpdatedAt?: string | null;
  permissionsUpdatedByUserId?: number | null;
  permissionsUpdatedByUserName?: string | null;
}

export interface ExternalIdentity {
  id: number;
  provider: string;
  providerUserId: string;
  providerEmail?: string | null;
  providerUsername?: string | null;
  displayName?: string | null;
  avatarUrl?: string | null;
  telegramUserId?: string | null;
  chatId?: number | null;
  verifiedAt?: string | null;
  lastUsedAt?: string | null;
  createdAt?: string | null;
}

export interface UserCompanyAccess {
  id: number;
  shortName: string;
  fullName?: string | null;
  inn?: string | null;
  kpp?: string | null;
}

export interface User {
  id: number;
  email: string;
  fullName: string;
  firstName?: string | null;
  lastName?: string | null;
  middleName?: string | null;
  dateOfBirth?: string | null;
  photoUrl?: string | null;
  phoneNumber?: string | null;
  whatsApp?: string | null;
  telegram?: string | null;
  instagram?: string | null;
  messenger?: string | null;
  viber?: string | null;
  isEmployee: boolean;
  isExternalContractor?: boolean;
  departmentId?: number | null;
  department?: string | null;
  requiresFinancialActionReason?: boolean;
  employmentStartDate?: string | null;
  employmentEndDate?: string | null;
  role: Role;
  isActive: boolean;
  isApproved: boolean;
  isProtectedUser?: boolean;
  createdAt: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
  approvedAt?: string | null;
  companyIds?: number[];
  companies?: UserCompanyAccess[];
  externalIdentities?: ExternalIdentity[];
}

export interface AuthProviderPublicSettings {
  googleEnabled: boolean;
  vkEnabled: boolean;
  yandexEnabled: boolean;
  telegramEnabled: boolean;
  telegramWidgetEnabled: boolean;
  telegramBotUsername?: string | null;
}

export interface AuthProviderSettings {
  googleEnabled: boolean;
  googleClientId: string;
  googleClientSecret: string;
  googleReady: boolean;
  googleCallbackUrl?: string | null;
  vkEnabled: boolean;
  vkClientId: string;
  vkClientSecret: string;
  vkReady: boolean;
  vkCallbackUrl?: string | null;
  yandexEnabled: boolean;
  yandexClientId: string;
  yandexClientSecret: string;
  yandexReady: boolean;
  yandexCallbackUrl?: string | null;
  telegramEnabled: boolean;
  telegramWidgetEnabled: boolean;
  telegramBotToken: string;
  telegramBotUsername: string;
  telegramWebhookSecret: string;
  telegramWebhookUrl?: string | null;
  telegramWebhookConfigured: boolean;
  telegramBotReady: boolean;
  telegramWidgetReady: boolean;
  telegramBotInfoSyncedAt?: string | null;
  telegramWebhookSyncedAt?: string | null;
}

export interface AuthProviderSettingsSaveResult {
  settings: AuthProviderSettings;
  message: string;
}

export interface ExternalOAuthProviderSettingsUpdateRequest {
  enabled: boolean;
  clientId: string;
  clientSecret: string;
}

export interface TelegramLoginBotSettingsUpdateRequest {
  enabled: boolean;
  botToken: string;
}

export interface TelegramWidgetSettingsUpdateRequest {
  enabled: boolean;
}

export interface TelegramWebhookSyncResult {
  success: boolean;
  message: string;
  botUsername?: string | null;
  webhookUrl?: string | null;
}

export interface ExternalAuthStartResponse {
  provider: string;
  state: string;
  authUrl: string;
  returnUrl: string;
  expiresAt: string;
  pollIntervalMs: number;
  intent?: string;
}

export interface ExternalAuthStatusResponse {
  status: string;
  completed: boolean;
  provider?: string | null;
  detail?: string | null;
  returnUrl?: string | null;
  requiresApproval: boolean;
  isInactive: boolean;
  token?: string | null;
  user?: User | null;
  linked?: boolean;
}

export interface CreateUserDto {
  email: string;
  password: string;
  fullName: string;
  roleId: number;
  isActive: boolean;
  isEmployee?: boolean;
  isExternalContractor?: boolean;
  departmentId?: number | null;
  department?: string;
  requiresFinancialActionReason?: boolean;
  companyIds?: number[];
}

export interface UpdateUserDto {
  email?: string;
  fullName?: string;
  firstName?: string;
  lastName?: string;
  middleName?: string;
  dateOfBirth?: string;
  clearDateOfBirth?: boolean;
  photoUrl?: string;
  phoneNumber?: string;
  whatsApp?: string;
  telegram?: string;
  instagram?: string;
  messenger?: string;
  viber?: string;
  isEmployee?: boolean;
  isExternalContractor?: boolean;
  departmentId?: number | null;
  department?: string;
  requiresFinancialActionReason?: boolean;
  employmentStartDate?: string;
  employmentEndDate?: string;
  clearEmploymentStartDate?: boolean;
  clearEmploymentEndDate?: boolean;
  roleId?: number;
  isActive?: boolean;
  companyIds?: number[];
}

export interface UpdateProfileDto {
  email?: string;
  fullName?: string;
  firstName?: string;
  lastName?: string;
  middleName?: string;
  dateOfBirth?: string;
  clearDateOfBirth?: boolean;
  photoUrl?: string;
  phoneNumber?: string;
  whatsApp?: string;
  telegram?: string;
  instagram?: string;
  messenger?: string;
  viber?: string;
}

export interface ContractorBalanceItem {
  taskId: number;
  taskTitle: string;
  paymentId?: number | null;
  direction: 'toMe' | 'byMe';
  assigneeName: string;
  authorName: string;
  clientId?: number | null;
  clientName?: string | null;
  legalEntityId?: number | null;
  legalEntityName?: string | null;
  clientCaseId?: number | null;
  clientCaseTitle?: string | null;
  amount: number;
  paidAmount: number;
  outstandingAmount: number;
  paymentStatus: string;
  taskStatus: string;
  paymentDueDate?: string | null;
  paymentDate?: string | null;
  paidDate?: string | null;
  paymentSourceName?: string | null;
  paymentAccount?: string | null;
  paymentAccountDate?: string | null;
  paymentOrderNumber?: string | null;
  paymentDocumentDate?: string | null;
}

export interface ContractorBalance {
  owedToMe: number;
  owedByMe: number;
  paidToMe: number;
  paidByMe: number;
  queue: ContractorBalanceItem[];
  paid: ContractorBalanceItem[];
}

export interface ChangePasswordDto {
  currentPassword?: string;
  newPassword: string;
}

export interface MergeUsersDto {
  targetUserId: number;
  sourceUserIds: number[];
  primaryEmail?: string;
  primaryPhoneNumber?: string;
}

export type ApiErrorCode = 'PendingApproval' | 'UserInactive' | 'InvalidCredentials' | string;
export interface ApiError extends Error {
  code?: ApiErrorCode;
  status?: number;
}

function makeError(message: string, code?: ApiErrorCode, status?: number): ApiError {
  const e = new Error(message) as ApiError;
  e.code = code;
  e.status = status;
  return e;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function buildQuery(params: Record<string, string | number | boolean | undefined | null>) {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    q.append(k, String(v));
  }
  const s = q.toString();
  return s ? `?${s}` : '';
}

type LoginResponse = { token: string; user?: User };

class AuthApiService {
  private readonly photoUploadKeys = new WeakMap<File, string>();

  private authHeaders(): Record<string, string> {
    const t = readToken();
    return t ? { Authorization: `Bearer ${t}` } : {};
  }

  private async request<T>(endpoint: string, options?: RequestInit): Promise<T> {
    const identity = captureAuthSessionIdentity();
    const url = `${API_BASE_URL}${endpoint}`;
    const isFormDataBody = typeof FormData !== 'undefined' && options?.body instanceof FormData;

    const headers: Record<string, string> = {
      ...(!isFormDataBody && options?.body ? { 'Content-Type': 'application/json' } : {}),
      ...this.authHeaders(),
      ...(options?.headers as Record<string, string> | undefined),
    };

    const res = await fetch(url, { ...options, headers });

    assertAuthSessionIdentity(identity);
    if (res.ok) {
      if (res.status === 204) return undefined as T;
      const ct = res.headers.get('content-type') || '';
      if (!ct.includes('application/json')) {
        const result = await res.text();
        assertAuthSessionIdentity(identity);
        return result as unknown as T;
      }
      const result = (await res.json()) as T;
      assertAuthSessionIdentity(identity);
      return result;
    }

    // Ошибки — пробуем распарсить тело
    const raw = await res.text().catch(() => '');
    assertAuthSessionIdentity(identity);
    let body: unknown = null;
    try {
      body = raw ? JSON.parse(raw) : null;
    } catch {
      /* ignore */
    }

    if (isRecord(body)) {
      const title = typeof body.title === 'string' ? body.title.trim() : undefined;
      const detail = typeof body.detail === 'string' ? body.detail.trim() : undefined;
      const message = typeof body.message === 'string' ? body.message.trim() : undefined;
      const code: ApiErrorCode | undefined =
        (typeof body.code === 'string' ? body.code.trim() : undefined) || title;

      if (code === 'PendingApproval')
        throw makeError('Ваш аккаунт ожидает одобрения администратором.', code, res.status);
      if (code === 'UserInactive')
        throw makeError('Ваш аккаунт отключён. Обратитесь к администратору.', code, res.status);
      if (code === 'InvalidCredentials' || res.status === 401)
        throw makeError('Неверный email или пароль.', 'InvalidCredentials', res.status);

      if (detail) throw makeError(detail, code, res.status);
      if (message) throw makeError(message, code, res.status);
      if (code) throw makeError(code, undefined, res.status);
    }

    if (res.status === 401) throw makeError('Неверный email или пароль.', 'InvalidCredentials', res.status);
    throw makeError(raw || `Ошибка ${res.status} ${res.statusText}`, undefined, res.status);
  }

  // ==== auth ====
  public isAuthenticated(): boolean {
    return !!readToken();
  }

  /** Просит сервер отозвать JWT на всех устройствах до удаления локального токена. */
  public async revokeSessions(): Promise<void> {
    if (!readToken()) {
      return;
    }

    await this.request<void>('/auth/logout', { method: 'POST' });
  }

  /** Удаляет только локальную копию токена; сетевой отзыв выполняется отдельно. */
  public logout(): void {
    try { replaceAuthSessionEpoch(); } catch { /* Clearing the token still invalidates financial ownership. */ } finally { clearToken(); }
  }

  public async ensureFreshSession(force = false): Promise<string | null> {
    const identity = captureAuthSessionIdentity();
    const currentToken = readToken();
    if (!currentToken) return null;
    const expiresAt = readTokenExpiresAt(currentToken);
    if (!force && (expiresAt === null || expiresAt - Date.now() > SESSION_REFRESH_WINDOW_MS)) {
      assertAuthSessionIdentity(identity);
      return currentToken;
    }
    let job = sessionRefreshJob;
    if (!job || !sameSession(job.identity, identity)) {
      const promise = (async () => {
        const response = await this.request<LoginResponse>('/auth/refresh', { method: 'POST' });
        assertAuthSessionIdentity(identity);
        if (!response.token) throw makeError('Сервер не вернул обновлённый токен сессии.');
        const nextActor = sessionActor(response.token);
        if (nextActor !== identity.actor) throw new AuthSessionChangedError();
        writeToken(response.token);
        assertAuthSessionIdentity(identity);
        return response.token;
      })();
      job = { identity, promise };
      sessionRefreshJob = job;
    }
    try {
      const result = await job.promise;
      assertAuthSessionIdentity(identity);
      return result;
    } catch (error) {
      // Neither an old pre-await token nor the replacement login may become this operation's fallback.
      assertAuthSessionIdentity(identity);
      if (force || error instanceof AuthSessionChangedError) throw error;
      return readToken();
    } finally {
      if (sessionRefreshJob === job) sessionRefreshJob = null;
    }
  }

  public async completeSignIn(token: string, user?: User | null): Promise<User> {
    const epoch = replaceAuthSessionEpoch();
    writeToken(token);
    if (user) {
      return user;
    }

    const current = await this.getCurrentUser();
    if (getAuthOperationEpoch() !== epoch) throw new AuthSessionChangedError();
    return current;
  }

  public async getCurrentUser(): Promise<User> {
    const identity = captureAuthSessionIdentity();
    await this.ensureFreshSession();
    assertAuthSessionIdentity(identity);
    const user = await this.request<User>('/profile');
    assertAuthSessionIdentity(identity);
    return user;
  }

  /** login: сохраняем токен и сразу пытаемся получить профиль */
  public async login(email: string, password: string): Promise<LoginResponse> {
    const loginEpoch = replaceAuthSessionEpoch();
    const resp = await this.request<LoginResponse>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    });

    if (localStorage.getItem(SESSION_EPOCH_KEY) !== loginEpoch) throw makeError('Сессия изменилась во время входа.');
    if (resp?.token) {
      writeToken(resp.token);
    }

    // Если сервер не вернул user в login — дотягиваем /auth/me
    if (!resp.user) {
      const me = await this.getCurrentUser();
      if (getAuthOperationEpoch() !== loginEpoch) throw new AuthSessionChangedError();
      return { token: resp.token, user: me };
    }

    return resp;
  }

  /** alias, если где-то уже использовано */
  public me() {
    return this.getCurrentUser();
  }

  public async register(data: { fullName: string; email: string; password: string }) {
    return this.request<unknown>('/auth/register', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  public async getAuthProviders() {
    return this.request<AuthProviderPublicSettings>('/auth/providers');
  }

  public async startExternalAuth(provider: string, returnUrl = '/', intent = 'signin') {
    return this.request<ExternalAuthStartResponse>('/auth/external/start', {
      method: 'POST',
      body: JSON.stringify({ provider, returnUrl, intent }),
    });
  }

  public async getExternalAuthStatus(state: string) {
    return this.request<ExternalAuthStatusResponse>(`/auth/external/status/${encodeURIComponent(state)}`);
  }

  public async completeTelegramWidget(payload: Record<string, unknown>) {
    return this.request<ExternalAuthStatusResponse>('/auth/telegram/widget', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  public async startTelegramBotAuth(returnUrl = '/', intent = 'signin') {
    return this.request<ExternalAuthStartResponse>('/auth/telegram/bot/start', {
      method: 'POST',
      body: JSON.stringify({ returnUrl, intent }),
    });
  }

  public async getTelegramBotAuthStatus(state: string) {
    return this.request<ExternalAuthStatusResponse>(`/auth/telegram/bot/status/${encodeURIComponent(state)}`);
  }

  public async getAuthProviderSettings() {
    return this.request<AuthProviderSettings>('/auth/admin/providers');
  }

  public async updateAuthProviderSettings(data: {
    googleEnabled: boolean;
    googleClientId: string;
    googleClientSecret: string;
    vkEnabled: boolean;
    vkClientId: string;
    vkClientSecret: string;
    yandexEnabled: boolean;
    yandexClientId: string;
    yandexClientSecret: string;
    telegramEnabled: boolean;
    telegramWidgetEnabled: boolean;
    telegramBotToken: string;
  }) {
    return this.request<AuthProviderSettingsSaveResult>('/auth/admin/providers', {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  public async updateGoogleAuthProviderSettings(data: ExternalOAuthProviderSettingsUpdateRequest) {
    return this.request<AuthProviderSettingsSaveResult>('/auth/admin/providers/google', {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  public async updateVkAuthProviderSettings(data: ExternalOAuthProviderSettingsUpdateRequest) {
    return this.request<AuthProviderSettingsSaveResult>('/auth/admin/providers/vk', {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  public async updateYandexAuthProviderSettings(data: ExternalOAuthProviderSettingsUpdateRequest) {
    return this.request<AuthProviderSettingsSaveResult>('/auth/admin/providers/yandex', {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  public async updateTelegramLoginBotSettings(data: TelegramLoginBotSettingsUpdateRequest) {
    return this.request<AuthProviderSettingsSaveResult>('/auth/admin/providers/telegram/bot', {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  public async updateTelegramWidgetSettings(data: TelegramWidgetSettingsUpdateRequest) {
    return this.request<AuthProviderSettingsSaveResult>('/auth/admin/providers/telegram/widget', {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  public async syncTelegramWebhook() {
    return this.request<TelegramWebhookSyncResult>('/auth/admin/providers/telegram/sync', {
      method: 'POST',
    });
  }

  // ==== users (admin) ====
  public async getUsers(status?: string) {
    const q = status ? `?status=${encodeURIComponent(status)}` : '';
    return this.request<User[]>(`/users${q}`);
  }

  public async getUsersPaged(params?: { status?: string; page?: number; pageSize?: number }) {
    const q = buildQuery({
      status: params?.status,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<User>>(`/users/paged${q}`);
  }

  public async getUser(id: number) {
    return this.request<User>(`/users/${id}`);
  }

  public async getUserContractorBalance(id: number) {
    return this.request<ContractorBalance>(`/users/${id}/contractor-balance`);
  }

  public async createUser(data: CreateUserDto) {
    return this.request<User>('/users', { method: 'POST', body: JSON.stringify(data) });
  }

  public async updateUser(id: number, data: UpdateUserDto) {
    return this.request<User>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(data) });
  }

  public async mergeUsers(data: MergeUsersDto) {
    return this.request<User>('/users/merge', { method: 'POST', body: JSON.stringify(data) });
  }

  public async deleteUser(id: number) {
    return this.request<void>(`/users/${id}`, { method: 'DELETE' });
  }

  public async approveUser(id: number) {
    return this.request<{ approved: boolean }>(`/users/${id}/approve`, { method: 'POST' });
  }

  public async rejectUser(id: number, reason?: string) {
    return this.request<{ rejected: boolean }>(`/users/${id}/reject`, {
      method: 'POST',
      body: JSON.stringify(reason ? { reason } : {}),
    });
  }

  // ==== roles (admin) ====
  public async getRoles() {
    return this.request<Role[]>('/roles');
  }

  public async getRolesPaged(params?: { page?: number; pageSize?: number }) {
    const q = buildQuery({
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 20,
    });
    return this.request<PagedResult<Role>>(`/roles/paged${q}`);
  }

  public async createRole(data: { name: string; description: string }) {
    return this.request<Role>('/roles', { method: 'POST', body: JSON.stringify(data) });
  }

  public async copyRole(id: number, data: { name: string; description: string }) {
    return this.request<Role>(`/roles/${id}/copy`, { method: 'POST', body: JSON.stringify(data) });
  }

  public async updateRole(id: number, data: { name: string; description: string }) {
    return this.request<Role>(`/roles/${id}`, { method: 'PUT', body: JSON.stringify(data) });
  }

  public async deleteRole(id: number) {
    return this.request<void>(`/roles/${id}`, { method: 'DELETE' });
  }

  public async getProfile() {
    return this.request<User>('/profile');
  }

  public async getContractorBalance() {
    return this.request<ContractorBalance>('/profile/contractor-balance');
  }

  public async updateProfile(data: UpdateProfileDto) {
    return this.request<User>('/profile', {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  public async changePassword(data: ChangePasswordDto) {
    return this.request<User>('/profile/password', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  public async uploadProfilePhoto(file: File) {
    const formData = new FormData();
    formData.append('file', file);
    const uploadKey = this.photoUploadKeys.get(file) ?? createClientRequestId();
    this.photoUploadKeys.set(file, uploadKey);

    return this.request<User>('/profile/photo', {
      method: 'POST',
      body: formData,
      headers: { 'X-Upload-Key': uploadKey },
    });
  }

  public async detachExternalIdentity(provider: string) {
    return this.request<{ detached: boolean; user?: User | null }>(
      `/profile/external/${encodeURIComponent(provider)}`,
      { method: 'DELETE' },
    );
  }
}

export const authService = new AuthApiService();
