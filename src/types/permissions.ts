export type MenuSectionKey =
  | 'calendar'
  | 'tasks'
  | 'reports'
  | 'calculator'
  | 'legalEntities'
  | 'clients'
  | 'cases'
  | 'accounts'
  | 'acts'
  | 'payments'
  | 'contracts'
  | 'dictionaries'
  | 'notifications'
  | 'roles'
  | 'audit'
  | 'yandexDisk'
  | 's3Storage';

export type MenuPermissionKey =
  | 'canView'
  | 'canCreate'
  | 'canEdit'
  | 'canDelete'
  | 'canExport';

export type DataVisibilityScope = 'own' | 'department' | 'all';

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
}

type OtherSectionKey = Exclude<MenuSectionKey, 'calendar'>;

export type RolePermissions = {
  calendar: CalendarPermissions;
} & {
  [K in OtherSectionKey]: MenuSectionPermissions;
};

export const MENU_SECTION_KEYS: MenuSectionKey[] = [
  'calendar',
  'tasks',
  'reports',
  'calculator',
  'legalEntities',
  'clients',
  'cases',
  'accounts',
  'acts',
  'payments',
  'contracts',
  'dictionaries',
  'notifications',
  'roles',
  'audit',
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
  },
  reports: { ...defaultMenuPermissions },
  tasks: { ...defaultMenuPermissions },
  calculator: { ...defaultMenuPermissions },
  legalEntities: { ...defaultMenuPermissions },
  clients: { ...defaultMenuPermissions },
  cases: { ...defaultMenuPermissions },
  accounts: { ...defaultMenuPermissions },
  acts: { ...defaultMenuPermissions },
  payments: { ...defaultMenuPermissions },
  contracts: { ...defaultMenuPermissions },
  dictionaries: { ...defaultMenuPermissions },
  notifications: { ...defaultRestrictedMenuPermissions },
  roles: { ...defaultRestrictedMenuPermissions },
  audit: { ...defaultRestrictedMenuPermissions },
  yandexDisk: { ...defaultRestrictedMenuPermissions },
  s3Storage: { ...defaultRestrictedMenuPermissions },
};
