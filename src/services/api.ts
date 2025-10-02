/**
 * API Service
 *
 * Centralized service for making HTTP requests to the backend API.
 * Handles request formatting, authentication headers, and error handling.
 * All API calls are authenticated with the user's session token from Supabase.
 */

import { supabase } from '../lib/supabase';

const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5080/api';

export type DictKind = 'deal-types' | 'income-types' | 'payment-sources' | 'payment-statuses';

export type DictItemByKind<K extends DictKind> = K extends 'deal-types'
  ? DealType
  : K extends 'income-types'
  ? IncomeType
  : K extends 'payment-sources'
  ? PaymentSource
  : PaymentStatusEntity;

/**
 * Get API path for dictionary kind
 *
 * @param kind - Dictionary kind identifier
 * @returns API path for the dictionary endpoint
 */
function pathForKind(kind: DictKind): string {
  return `/dictionaries/${kind}`;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

type SortDir = 'asc' | 'desc';

/**
 * Build query string from parameters object
 * Filters out undefined, null, and empty string values
 *
 * @param params - Object with query parameters
 * @returns Query string with leading '?' or empty string
 */
function buildQuery(params: Record<string, string | number | boolean | undefined | null>) {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    q.append(k, String(v));
  }
  const s = q.toString();
  return s ? `?${s}` : '';
}

/**
 * ApiService Class
 *
 * Provides methods for interacting with the backend API.
 * Automatically includes authentication headers from Supabase session.
 */
export class ApiService {
  /**
   * Make an authenticated HTTP request
   * Automatically adds Authorization header with user's session token
   *
   * @param endpoint - API endpoint path
   * @param options - Fetch options (method, body, headers, etc.)
   * @returns Promise resolving to the response data
   * @throws Error if request fails or returns non-OK status
   */
  private async request<T>(endpoint: string, options?: RequestInit): Promise<T> {
    const url = `${API_BASE_URL}${endpoint}`;

    // Get the current session to include auth token
    let session = null;
    if (supabase) {
      const { data } = await supabase.auth.getSession();
      session = data.session;
    }

    const response = await fetch(url, {
      headers: {
        ...(options?.body ? { 'Content-Type': 'application/json' } : {}),
        // Include Authorization header if user is authenticated
        ...(session?.access_token ? { 'Authorization': `Bearer ${session.access_token}` } : {}),
        ...options?.headers,
      },
      ...options,
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

  // ===== Payments (back-compat + extended variants) =====

  /**
   * Get payments with optional filters
   * Supports multiple overload signatures for backwards compatibility
   */
  async getPayments(): Promise<Payment[]>;
  async getPayments(from?: string, to?: string, clientId?: number): Promise<Payment[]>;
  async getPayments(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    caseId?: number;
  }): Promise<Payment[]>;
  async getPayments(
    a?: string | { from?: string; to?: string; clientId?: number; caseId?: number },
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
      });
    } else {
      // Old signature style: (from?, to?, clientId?)
      q = buildQuery({ from: a, to: b, clientId: c });
    }
    return this.request<Payment[]>(`/payments${q}`);
  }

  /**
   * Get a single payment by ID
   */
  async getPayment(id: number) {
    return this.request<Payment>(`/payments/${id}`);
  }

  /**
   * Create a new payment
   */
  async createPayment(payment: Omit<Payment, 'id' | 'createdAt'>) {
    return this.request<Payment>('/payments', { method: 'POST', body: JSON.stringify(payment) });
  }

  /**
   * Update an existing payment
   */
  async updatePayment(id: number, payment: Omit<Payment, 'id' | 'createdAt'>) {
    return this.request<Payment>(`/payments/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payment),
    });
  }

  /**
   * Delete a payment
   */
  async deletePayment(id: number) {
    return this.request<void>(`/payments/${id}`, { method: 'DELETE' });
  }

  /**
   * V1: Get payments with filters and sorting, no pagination
   */
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

  /**
   * V2: Get payments with pagination support
   */
  async getPaymentsV2(params?: {
    from?: string;
    to?: string;
    clientId?: number;
    caseId?: number;
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
    return this.request<PagedResult<Payment>>(`/v2/payments${q}`);
  }

  // ===== Clients =====

  /**
   * Get all clients
   */
  async getClients() {
    return this.request<Client[]>('/clients');
  }

  /**
   * Get a single client by ID
   */
  async getClient(id: number) {
    return this.request<Client>(`/clients/${id}`);
  }

  /**
   * Get client statistics with optional case filter
   */
  async getClientStats(id: number): Promise<ClientStats>;
  async getClientStats(id: number, params?: { caseId?: number }): Promise<ClientStats>;
  async getClientStats(id: number, params?: { caseId?: number }) {
    const q = buildQuery({ caseId: params?.caseId });
    return this.request<ClientStats>(`/clients/${id}/stats${q}`);
  }

  /**
   * Create a new client
   */
  async createClient(client: Omit<Client, 'id' | 'createdAt'>) {
    return this.request<Client>('/clients', { method: 'POST', body: JSON.stringify(client) });
  }

  /**
   * Update an existing client
   */
  async updateClient(id: number, client: Omit<Client, 'id' | 'createdAt'>) {
    return this.request<Client>(`/clients/${id}`, { method: 'PUT', body: JSON.stringify(client) });
  }

  /**
   * Delete a client
   */
  async deleteClient(id: number) {
    return this.request<void>(`/clients/${id}`, { method: 'DELETE' });
  }

  /**
   * V1: Get clients with filters and sorting
   */
  async getClientsV1(params?: {
    search?: string;
    isActive?: boolean;
    sortBy?: 'name' | 'createdAt';
    sortDir?: SortDir;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<Client[]>(`/v1/clients${q}`);
  }

  /**
   * V2: Get clients with pagination
   */
  async getClientsV2(params?: {
    search?: string;
    isActive?: boolean;
    sortBy?: 'name' | 'createdAt';
    sortDir?: SortDir;
    page?: number;
    pageSize?: number;
  }) {
    const q = buildQuery({
      ...(params ?? {}),
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
    });
    return this.request<PagedResult<Client>>(`/v2/clients${q}`);
  }

  // ===== Cases (combining old and new call patterns) =====

  /**
   * Get cases with optional client filter
   * Supports multiple overload signatures for backwards compatibility
   */
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

  /**
   * Get a single case by ID
   */
  async getCase(id: number) {
    return this.request<ClientCase>(`/cases/${id}`);
  }

  /**
   * Create a new case
   */
  async createCase(model: Omit<ClientCase, 'id' | 'createdAt' | 'payments'>) {
    return this.request<ClientCase>('/cases', { method: 'POST', body: JSON.stringify(model) });
  }

  /**
   * Update an existing case
   */
  async updateCase(id: number, model: Omit<ClientCase, 'id' | 'createdAt' | 'payments'>) {
    return this.request<ClientCase>(`/cases/${id}`, { method: 'PUT', body: JSON.stringify(model) });
  }

  /**
   * Delete a case
   */
  async deleteCase(id: number) {
    return this.request<void>(`/cases/${id}`, { method: 'DELETE' });
  }

  /**
   * V1: Get cases with filters and sorting
   */
  async getCasesV1(params?: {
    clientId?: number;
    status?: string;
    search?: string;
    sortBy?: 'createdAt' | 'title' | 'status';
    sortDir?: SortDir;
  }) {
    const q = buildQuery(params ?? {});
    return this.request<ClientCase[]>(`/v1/cases${q}`);
  }

  /**
   * V2: Get cases with pagination
   */
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

  /**
   * Get deal types dictionary
   */
  async getDealTypes() {
    return this.getDict('deal-types');
  }

  /**
   * Get income types dictionary with optional filters
   */
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

  /**
   * Get payment sources dictionary
   */
  async getPaymentSources() {
    return this.getDict('payment-sources');
  }

  /**
   * Get payment statuses dictionary
   */
  async getPaymentStatuses() {
    return this.getDict('payment-statuses');
  }

  // ===== Statistics =====

  /**
   * Get monthly statistics for a specific month
   */
  async getMonthlyStats(year: number, month: number) {
    return this.request<MonthlyStats>(`/stats/month?year=${year}&month=${month}`);
  }

  /**
   * Get statistics for a range of months
   */
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

  /**
   * Calculate installment payment schedule
   */
  async calculateInstallment(request: InstallmentRequest) {
    return this.request('/installments/calc', { method: 'POST', body: JSON.stringify(request) });
  }

  /**
   * Create a new income type (specialized CRUD with paymentType field)
   */
  async createIncomeType(data: Omit<IncomeType, 'id' | 'createdAt'>): Promise<IncomeType> {
    return this.request<IncomeType>(pathForKind('income-types'), {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  /**
   * Update an income type
   */
  async updateIncomeType(
    id: number,
    data: Omit<IncomeType, 'id' | 'createdAt'>,
  ): Promise<IncomeType> {
    return this.request<IncomeType>(`${pathForKind('income-types')}/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  // ===== Dictionaries (universal CRUD) =====

  /**
   * Get dictionary items by kind
   */
  async getDict<K extends DictKind>(kind: K): Promise<DictItemByKind<K>[]> {
    return this.request<DictItemByKind<K>[]>(pathForKind(kind));
  }

  /**
   * Create a dictionary item
   */
  async createDict<K extends DictKind>(
    kind: K,
    data: Omit<BaseDictItem, 'id'>,
  ): Promise<DictItemByKind<K>> {
    return this.request<DictItemByKind<K>>(pathForKind(kind), {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  /**
   * Update a dictionary item
   */
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

  /**
   * Delete a dictionary item
   */
  async deleteDict(kind: DictKind, id: number): Promise<void> {
    await this.request<void>(`${pathForKind(kind)}/${id}`, { method: 'DELETE' });
  }

  // ===== Accounts (suggestions for account field) =====

  /**
   * Get account suggestions with various filter options
   * Supports returning either simple string array or objects with dates
   */
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

  /**
   * Get account suggestions (convenience method)
   */
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
   * Get summary statistics with server-side calculations
   * Supports filters: status and q (search)
   * Parameter r is a cache-buster (pass reloadToken)
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

  /**
   * Simplified call: get summary for a client
   */
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

  /**
   * Simplified call: get summary for a case
   */
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
}

// Export singleton instance
export const apiService = new ApiService();

// ---- Type imports ----
import type {
  Payment,
  Client,
  InstallmentRequest,
  ClientStats,
  DealType,
  IncomeType,
  PaymentSource,
  PaymentStatusEntity,
  MonthlyStats,
  ClientCase,
  AccountSuggestion,
  SummaryStats,
  PeriodKey,
  SummaryStatus,
} from '../types';
import { BaseDictItem } from '../types/dictionaries';
