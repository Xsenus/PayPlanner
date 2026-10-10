export type MenuSectionKey =
  | 'calendar'
  | 'tasks'
  | 'notes'
  | 'reports'
  | 'calculator'
  | 'legalEntities'
  | 'clients'
  | 'cases'
  | 'requests'
  | 'accounts'
  | 'acts'
  | 'payments'
  | 'contracts'
  | 'dictionaries'
  | 'notifications'
  | 'roles'
  | 'audit'
  | 'integrations'
  | 'yandexDisk'
  | 's3Storage';

export type MenuPermissionKey =
  | 'canView'
  | 'canCreate'
  | 'canEdit'
  | 'canDelete'
  | 'canExport';

export type DataVisibilityScope = 'own' | 'department' | 'all' | 'specific';

export interface MenuSectionPermissions {
  canView: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  canExport: boolean;
  viewScope?: DataVisibilityScope;
}

export interface CalendarPermissions extends MenuSectionPermissions {
  canViewAnalytics: boolean;
  visibleUserIds?: number[];
}

export interface TaskSectionPermissions extends MenuSectionPermissions {
  canEditOthers: boolean;
  canCompleteOthers: boolean;
  canDeleteOthers: boolean;
}

type OtherSectionKey = Exclude<MenuSectionKey, 'calendar' | 'tasks'>;

export type RolePermissions = {
  calendar: CalendarPermissions;
  tasks: TaskSectionPermissions;
} & {
  [K in OtherSectionKey]: MenuSectionPermissions;
};

export const MENU_SECTION_KEYS: MenuSectionKey[] = [
  'calendar',
  'tasks',
  'notes',
  'reports',
  'calculator',
  'legalEntities',
  'clients',
  'cases',
  'requests',
  'accounts',
  'acts',
  'payments',
  'contracts',
  'dictionaries',
  'notifications',
  'roles',
  'audit',
  'integrations',
  'yandexDisk',
  's3Storage',
];

export const MENU_PERMISSION_KEYS: MenuPermissionKey[] = [
  'canView',
  'canCreate',
  'canEdit',
  'canDelete',
  'canExport',
];

export const defaultMenuPermissions: MenuSectionPermissions = {
  canView: true,
  canCreate: true,
  canEdit: true,
  canDelete: true,
  canExport: true,
  viewScope: 'all',
};

export const defaultRestrictedMenuPermissions: MenuSectionPermissions = {
  canView: false,
  canCreate: false,
  canEdit: false,
  canDelete: false,
  canExport: false,
  viewScope: 'own',
};

export const defaultRolePermissions: RolePermissions = {
  calendar: {
    ...defaultMenuPermissions,
    canViewAnalytics: true,
    visibleUserIds: [],
  },
  reports: { ...defaultMenuPermissions },
  tasks: {
    ...defaultMenuPermissions,
    canEditOthers: true,
    canCompleteOthers: true,
    canDeleteOthers: true,
  },
  notes: { ...defaultMenuPermissions, canExport: false },
  calculator: { ...defaultMenuPermissions },
  legalEntities: { ...defaultMenuPermissions },
  clients: { ...defaultMenuPermissions },
  cases: { ...defaultMenuPermissions },
  requests: { ...defaultMenuPermissions },
  accounts: { ...defaultMenuPermissions },
  acts: { ...defaultMenuPermissions },
  payments: { ...defaultMenuPermissions },
  contracts: { ...defaultMenuPermissions },
  dictionaries: { ...defaultMenuPermissions },
  notifications: { ...defaultRestrictedMenuPermissions },
  roles: { ...defaultRestrictedMenuPermissions },
  audit: { ...defaultRestrictedMenuPermissions },
  integrations: { ...defaultRestrictedMenuPermissions },
  yandexDisk: { ...defaultRestrictedMenuPermissions },
  s3Storage: { ...defaultRestrictedMenuPermissions },
};

export const defaultRestrictedRolePermissions: RolePermissions = {
  calendar: {
    ...defaultRestrictedMenuPermissions,
    canViewAnalytics: false,
    visibleUserIds: [],
  },
  reports: { ...defaultRestrictedMenuPermissions },
  tasks: {
    ...defaultRestrictedMenuPermissions,
    canEditOthers: false,
    canCompleteOthers: false,
    canDeleteOthers: false,
  },
  notes: { ...defaultRestrictedMenuPermissions },
  calculator: { ...defaultRestrictedMenuPermissions },
  legalEntities: { ...defaultRestrictedMenuPermissions },
  clients: { ...defaultRestrictedMenuPermissions },
  cases: { ...defaultRestrictedMenuPermissions },
  requests: { ...defaultRestrictedMenuPermissions },
  accounts: { ...defaultRestrictedMenuPermissions },
  acts: { ...defaultRestrictedMenuPermissions },
  payments: { ...defaultRestrictedMenuPermissions },
  contracts: { ...defaultRestrictedMenuPermissions },
  dictionaries: { ...defaultRestrictedMenuPermissions },
  notifications: { ...defaultRestrictedMenuPermissions },
  roles: { ...defaultRestrictedMenuPermissions },
  audit: { ...defaultRestrictedMenuPermissions },
  integrations: { ...defaultRestrictedMenuPermissions },
  yandexDisk: { ...defaultRestrictedMenuPermissions },
  s3Storage: { ...defaultRestrictedMenuPermissions },
};
