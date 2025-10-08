// src/services/authService.ts

const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5080/api';

// ==== storage helpers (bkwd-compat) ====
const KEY_MAIN = 'pp_token';
const KEY_FALLBACKS = ['auth_token', 'pp.jwt'];

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

// ==== types ====
export interface Role {
  id: number;
  name: string;
  description?: string | null;
}

export interface User {
  id: number;
  email: string;
  fullName: string;
  role: Role;
  isActive: boolean;
  isApproved: boolean;
  createdAt: string; // ISO
  approvedAt?: string | null;
}

export interface CreateUserDto {
  email: string;
  password: string;
  fullName: string;
  roleId: number;
  isActive: boolean;
}

export interface UpdateUserDto {
  fullName?: string;
  roleId?: number;
  isActive?: boolean;
}

export type ApiErrorCode = 'PendingApproval' | 'UserInactive' | 'InvalidCredentials' | string;
export interface ApiError extends Error {
  code?: ApiErrorCode;
}

function makeError(message: string, code?: ApiErrorCode): ApiError {
  const e = new Error(message) as ApiError;
  e.code = code;
  return e;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

type LoginResponse = { token: string; user?: User };

class AuthApiService {
  private authHeaders(): Record<string, string> {
    const t = readToken();
    return t ? { Authorization: `Bearer ${t}` } : {};
  }

  private async request<T>(endpoint: string, options?: RequestInit): Promise<T> {
    const url = `${API_BASE_URL}${endpoint}`;

    const headers: Record<string, string> = {
      ...(options?.body ? { 'Content-Type': 'application/json' } : {}),
      ...this.authHeaders(),
      ...(options?.headers as Record<string, string> | undefined),
    };

    const res = await fetch(url, { ...options, headers });

    if (res.ok) {
      if (res.status === 204) return undefined as T;
      const ct = res.headers.get('content-type') || '';
      if (!ct.includes('application/json')) {
        return (await res.text()) as unknown as T;
      }
      return (await res.json()) as T;
    }

    // Ошибки — пробуем распарсить тело
    const raw = await res.text().catch(() => '');
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
        throw makeError('Ваш аккаунт ожидает одобрения администратором.', code);
      if (code === 'UserInactive')
        throw makeError('Ваш аккаунт отключён. Обратитесь к администратору.', code);
      if (code === 'InvalidCredentials' || res.status === 401)
        throw makeError('Неверный email или пароль.', 'InvalidCredentials');

      if (detail) throw makeError(detail, code);
      if (message) throw makeError(message, code);
      if (code) throw makeError(code);
    }

    if (res.status === 401) throw makeError('Неверный email или пароль.', 'InvalidCredentials');
    throw makeError(raw || `Ошибка ${res.status} ${res.statusText}`);
  }

  // ==== auth ====
  public isAuthenticated(): boolean {
    return !!readToken();
  }

  public logout(): void {
    clearToken();
  }

  public async getCurrentUser(): Promise<User> {
    return this.request<User>('/auth/me');
  }

  /** login: сохраняем токен и сразу пытаемся получить профиль */
  public async login(email: string, password: string): Promise<LoginResponse> {
    const resp = await this.request<LoginResponse>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    });

    if (resp?.token) {
      writeToken(resp.token);
    }

    // Если сервер не вернул user в login — дотягиваем /auth/me
    if (!resp.user) {
      const me = await this.getCurrentUser();
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

  // ==== users (admin) ====
  public async getUsers(status?: string) {
    const q = status ? `?status=${encodeURIComponent(status)}` : '';
    return this.request<User[]>(`/users${q}`);
  }

  public async getUser(id: number) {
    return this.request<User>(`/users/${id}`);
  }

  public async createUser(data: CreateUserDto) {
    return this.request<User>('/users', { method: 'POST', body: JSON.stringify(data) });
  }

  public async updateUser(id: number, data: UpdateUserDto) {
    return this.request<User>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(data) });
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

  public async createRole(data: { name: string; description: string }) {
    return this.request<Role>('/roles', { method: 'POST', body: JSON.stringify(data) });
  }

  public async updateRole(id: number, data: { name: string; description: string }) {
    return this.request<Role>(`/roles/${id}`, { method: 'PUT', body: JSON.stringify(data) });
  }

  public async deleteRole(id: number) {
    return this.request<void>(`/roles/${id}`, { method: 'DELETE' });
  }
}

export const authService = new AuthApiService();
