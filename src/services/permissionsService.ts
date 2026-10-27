import { apiService } from './api';
import {
  defaultRestrictedRolePermissions,
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
const generations = new Map<number, number>();
const signalRefreshes = new WeakMap<Event, Map<number, Promise<RolePermissions>>>();

function normalizeViewScope(value?: string | null): DataVisibilityScope {
  return value === 'department' || value === 'all' || value === 'specific' ? value : 'own';
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
  const defaults = defaultRestrictedRolePermissions;
  const calendar = normalizeSectionPermissions(defaults.calendar, source.calendar);
  calendar.visibleUserIds = Array.isArray(source.calendar?.visibleUserIds)
    ? source.calendar.visibleUserIds.filter((id): id is number => Number.isInteger(id) && id > 0)
    : [];

  return {
    calendar,
    tasks: normalizeSectionPermissions(defaults.tasks, source.tasks),
    notes: normalizeSectionPermissions(defaults.notes, source.notes),
    reports: normalizeSectionPermissions(defaults.reports, source.reports),
    calculator: normalizeSectionPermissions(defaults.calculator, source.calculator),
    legalEntities: normalizeSectionPermissions(defaults.legalEntities, source.legalEntities),
    companyProfile: normalizeSectionPermissions(defaults.companyProfile, source.companyProfile),
    clients: normalizeSectionPermissions(defaults.clients, source.clients),
    cases: normalizeSectionPermissions(defaults.cases, source.cases),
    requests: normalizeSectionPermissions(defaults.requests, source.requests),
    accounts: normalizeSectionPermissions(defaults.accounts, source.accounts),
    acts: normalizeSectionPermissions(defaults.acts, source.acts),
    payments: normalizeSectionPermissions(defaults.payments, source.payments),
    contracts: normalizeSectionPermissions(defaults.contracts, source.contracts),
    dictionaries: normalizeSectionPermissions(defaults.dictionaries, source.dictionaries),
    notifications: normalizeSectionPermissions(defaults.notifications, source.notifications),
    roles: normalizeSectionPermissions(defaults.roles, source.roles),
    audit: normalizeSectionPermissions(defaults.audit, source.audit),
    integrations: normalizeSectionPermissions(defaults.integrations, source.integrations),
    yandexDisk: normalizeSectionPermissions(defaults.yandexDisk, source.yandexDisk),
    s3Storage: normalizeSectionPermissions(defaults.s3Storage, source.s3Storage),
  };
}

function clonePermissions(perms: RolePermissions): RolePermissions {
  return {
    calendar: { ...perms.calendar, visibleUserIds: [...(perms.calendar.visibleUserIds ?? [])] },
    tasks: { ...perms.tasks },
    notes: { ...perms.notes },
    reports: { ...perms.reports },
    calculator: { ...perms.calculator },
    legalEntities: { ...perms.legalEntities },
    companyProfile: { ...perms.companyProfile },
    clients: { ...perms.clients },
    cases: { ...perms.cases },
    requests: { ...perms.requests },
    accounts: { ...perms.accounts },
    acts: { ...perms.acts },
    payments: { ...perms.payments },
    contracts: { ...perms.contracts },
    dictionaries: { ...perms.dictionaries },
    notifications: { ...perms.notifications },
    roles: { ...perms.roles },
    audit: { ...perms.audit },
    integrations: { ...perms.integrations },
    yandexDisk: { ...perms.yandexDisk },
    s3Storage: { ...perms.s3Storage },
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

  const generation = generations.get(roleId) ?? 0;
  const promise = apiService
    .getRolePermissions(roleId)
    .then((response) => normalizePermissions(response))
    .catch((error) => {
      console.warn('Не удалось загрузить права роли', error);
      return clonePermissions(defaultRestrictedRolePermissions);
    })
    .then((normalized) => {
      if ((generations.get(roleId) ?? 0) !== generation) {
        return clonePermissions(cache.get(roleId) ?? defaultRestrictedRolePermissions);
      }
      cache.set(roleId, normalized);
      notify(roleId);
      return normalized;
    })
    .finally(() => {
      if (loading.get(roleId) === promise) loading.delete(roleId);
    });

  loading.set(roleId, promise);
  return promise;
}

export function getRolePermissions(roleId?: number | null): RolePermissions {
  if (!roleId) {
    return clonePermissions(defaultRestrictedRolePermissions);
  }

  const cached = cache.get(roleId);
  if (cached) {
    return clonePermissions(cached);
  }

  void fetchRolePermissions(roleId);
  return clonePermissions(defaultRestrictedRolePermissions);
}

export function hasLoadedRolePermissions(roleId?: number | null): boolean {
  return !roleId || cache.has(roleId);
}

export async function refreshRolePermissions(roleId: number) {
  await fetchRolePermissions(roleId);
}

export function refreshRolePermissionsAfterSignal(roleId: number, signal: Event) {
  let requests = signalRefreshes.get(signal);
  if (!requests) {
    requests = new Map();
    signalRefreshes.set(signal, requests);
  }
  const existing = requests.get(roleId);
  if (existing) return existing;
  generations.set(roleId, (generations.get(roleId) ?? 0) + 1);
  loading.delete(roleId);
  const request = fetchRolePermissions(roleId);
  requests.set(roleId, request);
  return request;
}

export async function setRolePermissions(roleId: number, permissions: RolePermissions) {
  const normalized = normalizePermissions(permissions);
  await apiService.updateRolePermissions(roleId, normalized);
  generations.set(roleId, (generations.get(roleId) ?? 0) + 1);
  loading.delete(roleId);
  cache.set(roleId, normalized);
  notify(roleId);
}

export async function resetRolePermissions(roleId: number) {
  await apiService.resetRolePermissions(roleId);
  generations.set(roleId, (generations.get(roleId) ?? 0) + 1);
  loading.delete(roleId);
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
