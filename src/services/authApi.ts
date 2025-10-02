const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5080/api';

export interface CSharpAuthUser {
  id: string;
  email: string;
  fullName: string;
  roles: string[];
  createdAt: string;
}

export interface CSharpAuthResponse {
  token: string;
  user: CSharpAuthUser;
}

export interface CSharpLoginRequest {
  email: string;
  password: string;
}

export interface CSharpRegisterRequest {
  email: string;
  password: string;
  fullName: string;
}

class AuthApiService {
  private token: string | null = null;

  constructor() {
    this.token = localStorage.getItem('authToken');
  }

  setToken(token: string | null) {
    this.token = token;
    if (token) {
      localStorage.setItem('authToken', token);
    } else {
      localStorage.removeItem('authToken');
    }
  }

  getToken(): string | null {
    return this.token;
  }

  private async request<T>(endpoint: string, options?: RequestInit): Promise<T> {
    const url = `${API_BASE_URL}${endpoint}`;

    const response = await fetch(url, {
      headers: {
        'Content-Type': 'application/json',
        ...(this.token ? { 'Authorization': `Bearer ${this.token}` } : {}),
        ...options?.headers,
      },
      ...options,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(text || `HTTP ${response.status}`);
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  async login(credentials: CSharpLoginRequest): Promise<CSharpAuthResponse> {
    const response = await this.request<CSharpAuthResponse>('/auth/login', {
      method: 'POST',
      body: JSON.stringify(credentials),
    });
    this.setToken(response.token);
    return response;
  }

  async register(data: CSharpRegisterRequest): Promise<CSharpAuthResponse> {
    const response = await this.request<CSharpAuthResponse>('/auth/register', {
      method: 'POST',
      body: JSON.stringify(data),
    });
    this.setToken(response.token);
    return response;
  }

  async getMe(): Promise<CSharpAuthUser> {
    return this.request<CSharpAuthUser>('/auth/me');
  }

  logout() {
    this.setToken(null);
  }

  async checkApiAvailable(): Promise<boolean> {
    try {
      const response = await fetch(`${API_BASE_URL}/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: '', password: '' }),
      });
      return response.status === 401 || response.status === 400;
    } catch {
      return false;
    }
  }
}

export const authApiService = new AuthApiService();
