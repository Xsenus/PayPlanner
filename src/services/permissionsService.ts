import { apiService } from './api';
import {
  defaultRolePermissions,
  type RolePermissions,
} from '../types/permissions';

const CHANGE_EVENT = 'pp.permissions.changed';

type ChangeListener = (roleId?: number) => void;

type PermissionsCache = Map<number, RolePermissions>;

type LoadingCache = Map<number, Promise<RolePermissions>>;

const cache: PermissionsCache = new Map();
const loading: LoadingCache = new Map();

function normalizePermissions(perms?: Partial<RolePermissions> | null): RolePermissions {
  const source = perms ?? {};
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
    legalEntities: {
      ...defaultRolePermissions.legalEntities,
      ...(source.legalEntities ?? {}),
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
  };
}

function clonePermissions(perms: RolePermissions): RolePermissions {
  return {
    calendar: { ...perms.calendar },
    reports: { ...perms.reports },
    calculator: { ...perms.calculator },
    clients: { ...perms.clients },
    legalEntities: { ...perms.legalEntities },
    accounts: { ...perms.accounts },
    acts: { ...perms.acts },
    contracts: { ...perms.contracts },
    dictionaries: { ...perms.dictionaries },
  };
}

function notify(roleId?: number) {
  if (typeof window === 'undefined') {
    return;
  }
  try {
    window.dispatchEvent(new CustomEvent(CHANGE_EVENT, { detail: { roleId } }));
  } catch (error) {
    console.warn('Не удалось уведомить об изменении прав', error);
  }
}

async function fetchRolePermissions(roleId: number): Promise<RolePermissions> {
  if (loading.has(roleId)) {
    return loading.get(roleId)!;
  }

  const promise = apiService
    .getRolePermissions(roleId)
    .then((response) => normalizePermissions(response))
    .catch((error) => {
      console.warn('Не удалось загрузить права роли', error);
      return clonePermissions(defaultRolePermissions);
    })
    .then((normalized) => {
      cache.set(roleId, normalized);
      notify(roleId);
      return normalized;
    })
    .finally(() => {
      loading.delete(roleId);
    });

  loading.set(roleId, promise);
  return promise;
}

export function getRolePermissions(roleId?: number | null): RolePermissions {
  if (!roleId) {
    return clonePermissions(defaultRolePermissions);
  }

  const cached = cache.get(roleId);
  if (cached) {
    return clonePermissions(cached);
  }

  void fetchRolePermissions(roleId);
  return clonePermissions(defaultRolePermissions);
}

export async function refreshRolePermissions(roleId: number) {
  await fetchRolePermissions(roleId);
}

export async function setRolePermissions(roleId: number, permissions: RolePermissions) {
  const normalized = normalizePermissions(permissions);
  await apiService.updateRolePermissions(roleId, normalized);
  cache.set(roleId, normalized);
  notify(roleId);
}

export async function resetRolePermissions(roleId: number) {
  await apiService.resetRolePermissions(roleId);
  cache.delete(roleId);
  notify(roleId);
}

export function subscribeOnPermissionsChange(listener: ChangeListener) {
  if (typeof window === 'undefined') {
    return () => undefined;
  }
  const handler = (event: Event) => {
    const detail = (event as CustomEvent<{ roleId?: number }>).detail;
    listener(detail?.roleId);
  };
  window.addEventListener(CHANGE_EVENT, handler as EventListener);
  return () => window.removeEventListener(CHANGE_EVENT, handler as EventListener);
}
