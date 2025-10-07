const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5080/api';

export interface Role {
  id: number;
  name: string;
  description: string;
}

export interface User {
  id: number;
  email: string;
  fullName: string;
  role: Role;
  isActive: boolean;
  isApproved: boolean;
  approvedAt?: string | null;
  createdAt: string;
}

export interface LoginResponse {
  token: string;
  user: User;
}

export interface CreateUserRequest {
  email: string;
  password: string;
  fullName: string;
  roleId: number;
  isActive: boolean;
}

export interface UpdateUserRequest {
  fullName: string;
  roleId: number;
  isActive: boolean;
}

class AuthApiService {
  private token: string | null = null;

  constructor() {
    this.token = localStorage.getItem('auth_token');
  }

  private getHeaders(): HeadersInit {
    const headers: HeadersInit = {
      'Content-Type': 'application/json',
    };

    if (this.token) {
      headers['Authorization'] = `Bearer ${this.token}`;
    }

    return headers;
  }

  private async request<T>(endpoint: string, options?: RequestInit): Promise<T> {
    const url = `${API_BASE_URL}${endpoint}`;
    const response = await fetch(url, {
      ...options,
      headers: {
        ...this.getHeaders(),
        ...options?.headers,
      },
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(
        `HTTP ${response.status} ${response.statusText}${text ? `: ${text}` : ''}`,
      );
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  async login(email: string, password: string): Promise<LoginResponse> {
    const response = await this.request<LoginResponse>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    });

    this.token = response.token;
    localStorage.setItem('auth_token', response.token);

    return response;
  }

  async getCurrentUser(): Promise<User> {
    return this.request<User>('/auth/me');
  }

  logout() {
    this.token = null;
    localStorage.removeItem('auth_token');
  }

  isAuthenticated(): boolean {
    return !!this.token;
  }

  getToken(): string | null {
    return this.token;
  }

  async getUsers(status?: 'pending' | 'approved'): Promise<User[]> {
    const query = status ? `?status=${status}` : '';
    return this.request<User[]>(`/users${query}`);
  }

  async getPendingUsers(): Promise<User[]> {
    return this.getUsers('pending');
  }

  async approveUser(id: number): Promise<void> {
    return this.request<void>(`/admin/users/${id}/approve`, {
      method: 'POST',
    });
  }

  async rejectUser(id: number, reason?: string): Promise<void> {
    return this.request<void>(`/admin/users/${id}/reject`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  async getUser(id: number): Promise<User> {
    return this.request<User>(`/users/${id}`);
  }

  async createUser(data: CreateUserRequest): Promise<User> {
    return this.request<User>('/users', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  async updateUser(id: number, data: UpdateUserRequest): Promise<User> {
    return this.request<User>(`/users/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  async deleteUser(id: number): Promise<void> {
    return this.request<void>(`/users/${id}`, {
      method: 'DELETE',
    });
  }

  async getRoles(): Promise<Role[]> {
    return this.request<Role[]>('/roles');
  }
}

export const authService = new AuthApiService();
