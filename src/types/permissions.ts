export type MenuSectionKey =
  | 'calendar'
  | 'reports'
  | 'calculator'
  | 'legalEntities'
  | 'clients'
  | 'accounts'
  | 'acts'
  | 'contracts'
  | 'dictionaries';

export type MenuPermissionKey =
  | 'canView'
  | 'canCreate'
  | 'canEdit'
  | 'canDelete'
  | 'canExport';

export interface MenuSectionPermissions {
  canView: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  canExport: boolean;
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
  'reports',
  'calculator',
  'legalEntities',
  'clients',
  'accounts',
  'acts',
  'contracts',
  'dictionaries',
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
};

export const defaultRolePermissions: RolePermissions = {
  calendar: {
    ...defaultMenuPermissions,
    canViewAnalytics: true,
  },
  reports: { ...defaultMenuPermissions },
  calculator: { ...defaultMenuPermissions },
  legalEntities: { ...defaultMenuPermissions },
  clients: { ...defaultMenuPermissions },
  accounts: { ...defaultMenuPermissions },
  acts: { ...defaultMenuPermissions },
  contracts: { ...defaultMenuPermissions },
  dictionaries: { ...defaultMenuPermissions },
};
