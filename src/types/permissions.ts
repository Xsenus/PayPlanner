export type CalendarPermissionKey =
  | 'canAddPayments'
  | 'canEditPayments'
  | 'canDeletePayments'
  | 'canViewAnalytics';

export interface CalendarPermissions {
  canAddPayments: boolean;
  canEditPayments: boolean;
  canDeletePayments: boolean;
  canViewAnalytics: boolean;
}

export interface RolePermissions {
  calendar: CalendarPermissions;
}

export const defaultRolePermissions: RolePermissions = {
  calendar: {
    canAddPayments: true,
    canEditPayments: true,
    canDeletePayments: true,
    canViewAnalytics: true,
  },
};
