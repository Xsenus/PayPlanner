/**
 * Role-Based Access Control Helper Functions
 *
 * Utility functions for checking user roles and permissions throughout the application.
 * These functions provide a consistent way to implement role-based access control.
 */

/**
 * Check if a user has a specific role
 *
 * @param userRoles - Array of role names assigned to the user
 * @param requiredRole - The role name to check for
 * @returns true if the user has the required role, false otherwise
 */
export const hasRole = (userRoles: string[], requiredRole: string): boolean => {
  return userRoles.includes(requiredRole);
};

/**
 * Check if a user has any of the specified roles
 *
 * @param userRoles - Array of role names assigned to the user
 * @param requiredRoles - Array of role names to check
 * @returns true if the user has at least one of the required roles, false otherwise
 */
export const hasAnyRole = (userRoles: string[], requiredRoles: string[]): boolean => {
  return requiredRoles.some((role) => userRoles.includes(role));
};

/**
 * Check if a user has all of the specified roles
 *
 * @param userRoles - Array of role names assigned to the user
 * @param requiredRoles - Array of role names to check
 * @returns true if the user has all of the required roles, false otherwise
 */
export const hasAllRoles = (userRoles: string[], requiredRoles: string[]): boolean => {
  return requiredRoles.every((role) => userRoles.includes(role));
};

/**
 * Check if a user is an administrator
 *
 * @param userRoles - Array of role names assigned to the user
 * @returns true if the user has the 'admin' role, false otherwise
 */
export const isAdmin = (userRoles: string[]): boolean => {
  return hasRole(userRoles, 'admin');
};

/**
 * Check if a user is a manager or higher (manager or admin)
 *
 * @param userRoles - Array of role names assigned to the user
 * @returns true if the user has 'manager' or 'admin' role, false otherwise
 */
export const isManagerOrHigher = (userRoles: string[]): boolean => {
  return hasAnyRole(userRoles, ['manager', 'admin']);
};

/**
 * Get the highest priority role for display purposes
 * Priority order: admin > manager > user
 *
 * @param userRoles - Array of role names assigned to the user
 * @returns The highest priority role name, or 'user' if no roles found
 */
export const getHighestRole = (userRoles: string[]): string => {
  if (hasRole(userRoles, 'admin')) return 'admin';
  if (hasRole(userRoles, 'manager')) return 'manager';
  if (hasRole(userRoles, 'user')) return 'user';
  return 'user';
};

/**
 * Format role name for display
 * Capitalizes first letter and replaces underscores with spaces
 *
 * @param role - The role name to format
 * @returns Formatted role name
 */
export const formatRoleName = (role: string): string => {
  return role
    .split('_')
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
};
