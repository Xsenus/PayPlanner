import { authApiService } from './authApi';

const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5080/api';

export interface UserDto {
  id: string;
  email: string;
  fullName: string;
  roles: string[];
  createdAt: string;
}

export interface Role {
  id: number;
  name: string;
  description: string;
  createdAt: string;
}

export interface UpdateUserRequest {
  fullName?: string;
  isActive?: boolean;
}

export interface ChangePasswordRequest {
  newPassword: string;
}

class UserApiService {
  private async request<T>(endpoint: string, options?: RequestInit): Promise<T> {
    const url = `${API_BASE_URL}${endpoint}`;
    const token = authApiService.getToken();

    const response = await fetch(url, {
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { 'Authorization': `Bearer ${token}` } : {}),
        ...options?.headers,
      },
      ...options,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(text || `HTTP ${response.status}`);
    }

    if (response.status === 204 || response.status === 200) {
      const contentType = response.headers.get('content-type');
      if (contentType && contentType.includes('application/json')) {
        return (await response.json()) as T;
      }
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  async getAllUsers(): Promise<UserDto[]> {
    return this.request<UserDto[]>('/users');
  }

  async getUser(id: string): Promise<UserDto> {
    return this.request<UserDto>(`/users/${id}`);
  }

  async updateUser(id: string, data: UpdateUserRequest): Promise<void> {
    await this.request<void>(`/users/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  async changePassword(id: string, data: ChangePasswordRequest): Promise<void> {
    await this.request<void>(`/users/${id}/password`, {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  async assignRole(userId: string, roleId: number): Promise<void> {
    await this.request<void>(`/users/${userId}/roles/${roleId}`, {
      method: 'POST',
    });
  }

  async removeRole(userId: string, roleId: number): Promise<void> {
    await this.request<void>(`/users/${userId}/roles/${roleId}`, {
      method: 'DELETE',
    });
  }

  async getAllRoles(): Promise<Role[]> {
    return this.request<Role[]>('/roles');
  }
}

export const userApiService = new UserApiService();
