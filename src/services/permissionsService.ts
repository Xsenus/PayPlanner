import { apiService } from './api';
import {
  defaultRolePermissions,
  type DataVisibilityScope,
  type MenuSectionPermissions,
  type RolePermissions,
} from '../types/permissions';

const CHANGE_EVENT = 'pp.permissions.changed';

type ChangeListener = (roleId?: number) => void;
type PermissionsCache = Map<number, RolePermissions>;
type LoadingCache = Map<number, Promise<RolePermissions>>;

const cache: PermissionsCache = new Map();
const loading: LoadingCache = new Map();

function normalizeViewScope(value?: string | null): DataVisibilityScope {
  return value === 'department' || value === 'all' ? value : 'own';
}

function normalizeSectionPermissions<T extends MenuSectionPermissions>(
  defaults: T,
  source?: Partial<T> | null,
): T {
  return {
    ...defaults,
    ...(source ?? {}),
    viewScope: normalizeViewScope(source?.viewScope ?? defaults.viewScope),
  };
}

function normalizePermissions(perms?: Partial<RolePermissions> | null): RolePermissions {
  const source = perms ?? {};

  return {
    calendar: normalizeSectionPermissions(defaultRolePermissions.calendar, source.calendar),
    tasks: normalizeSectionPermissions(defaultRolePermissions.tasks, source.tasks),
    reports: normalizeSectionPermissions(defaultRolePermissions.reports, source.reports),
    calculator: normalizeSectionPermissions(defaultRolePermissions.calculator, source.calculator),
    legalEntities: normalizeSectionPermissions(defaultRolePermissions.legalEntities, source.legalEntities),
    clients: normalizeSectionPermissions(defaultRolePermissions.clients, source.clients),
    cases: normalizeSectionPermissions(defaultRolePermissions.cases, source.cases),
    accounts: normalizeSectionPermissions(defaultRolePermissions.accounts, source.accounts),
    acts: normalizeSectionPermissions(defaultRolePermissions.acts, source.acts),
    payments: normalizeSectionPermissions(defaultRolePermissions.payments, source.payments),
    contracts: normalizeSectionPermissions(defaultRolePermissions.contracts, source.contracts),
    dictionaries: normalizeSectionPermissions(defaultRolePermissions.dictionaries, source.dictionaries),
    notifications: normalizeSectionPermissions(defaultRolePermissions.notifications, source.notifications),
    roles: normalizeSectionPermissions(defaultRolePermissions.roles, source.roles),
    audit: normalizeSectionPermissions(defaultRolePermissions.audit, source.audit),
  };
}

function clonePermissions(perms: RolePermissions): RolePermissions {
  return {
    calendar: { ...perms.calendar },
    tasks: { ...perms.tasks },
    reports: { ...perms.reports },
    calculator: { ...perms.calculator },
    legalEntities: { ...perms.legalEntities },
    clients: { ...perms.clients },
    cases: { ...perms.cases },
    accounts: { ...perms.accounts },
    acts: { ...perms.acts },
    payments: { ...perms.payments },
    contracts: { ...perms.contracts },
    dictionaries: { ...perms.dictionaries },
    notifications: { ...perms.notifications },
    roles: { ...perms.roles },
    audit: { ...perms.audit },
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
