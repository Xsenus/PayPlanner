import { apiService } from './api';
import {
  defaultRolePermissions,
  type RolePermissions,
} from '../types/permissions';

const CHANGE_EVENT = 'pp.permissions.changed';

type ChangeListener = (roleId?: number) => void;

const cache = new Map<number, RolePermissions>();
const listeners = new Set<ChangeListener>();

function normalizePermissions(perms?: RolePermissions | null): RolePermissions {
  const source = perms ?? defaultRolePermissions;
  return {
    calendar: {
      ...defaultRolePermissions.calendar,
      ...(source.calendar ?? {}),
    },
    reports: {
      ...defaultRolePermissions.reports,
      ...(source.reports ?? {}),
    },
    calculator: {
      ...defaultRolePermissions.calculator,
      ...(source.calculator ?? {}),
    },
    clients: {
      ...defaultRolePermissions.clients,
      ...(source.clients ?? {}),
    },
    accounts: {
      ...defaultRolePermissions.accounts,
      ...(source.accounts ?? {}),
    },
    acts: {
      ...defaultRolePermissions.acts,
      ...(source.acts ?? {}),
    },
    contracts: {
      ...defaultRolePermissions.contracts,
      ...(source.contracts ?? {}),
    },
    dictionaries: {
      ...defaultRolePermissions.dictionaries,
      ...(source.dictionaries ?? {}),
    },
  } satisfies RolePermissions;
}

function clonePermissions(perms: RolePermissions): RolePermissions {
  return {
    calendar: { ...perms.calendar },
    reports: { ...perms.reports },
    calculator: { ...perms.calculator },
    clients: { ...perms.clients },
    accounts: { ...perms.accounts },
    acts: { ...perms.acts },
    contracts: { ...perms.contracts },
    dictionaries: { ...perms.dictionaries },
  } satisfies RolePermissions;
}

function emitChange(roleId?: number) {
  for (const listener of listeners) {
    try {
      listener(roleId);
    } catch (error) {
      console.error('Ошибка обработчика изменения прав ролей', error);
    }
  }

  if (typeof window !== 'undefined') {
    try {
      window.dispatchEvent(new CustomEvent(CHANGE_EVENT, { detail: { roleId } }));
    } catch {
      /* ignore */
    }
  }
}

export function getRolePermissions(roleId?: number | null): RolePermissions {
  if (!roleId) {
    return clonePermissions(defaultRolePermissions);
  }
  const cached = cache.get(roleId);
  if (cached) {
    return clonePermissions(cached);
  }
  return clonePermissions(defaultRolePermissions);
}

export async function fetchRolePermissions(roleId: number): Promise<RolePermissions> {
  const response = await apiService.getRolePermissions(roleId);
  const normalized = normalizePermissions(response);
  cache.set(roleId, normalized);
  emitChange(roleId);
  return clonePermissions(normalized);
}

export async function setRolePermissions(
  roleId: number,
  permissions: RolePermissions,
): Promise<RolePermissions> {
  const payload = normalizePermissions(permissions);
  const response = await apiService.updateRolePermissions(roleId, payload);
  const normalized = normalizePermissions(response);
  cache.set(roleId, normalized);
  emitChange(roleId);
  return clonePermissions(normalized);
}

export async function resetRolePermissions(roleId: number): Promise<RolePermissions> {
  const response = await apiService.resetRolePermissions(roleId);
  const normalized = normalizePermissions(response);
  cache.set(roleId, normalized);
  emitChange(roleId);
  return clonePermissions(normalized);
}

export function subscribeOnPermissionsChange(listener: ChangeListener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
