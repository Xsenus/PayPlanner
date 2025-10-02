/**
 * Protected Route Component
 *
 * A wrapper component that restricts access to authenticated users only.
 * Optionally enforces role-based access control.
 * Displays loading state during authentication check and redirects
 * unauthenticated users to the login page.
 */

import React from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { AuthPage } from './AuthPage';

/**
 * Props interface for ProtectedRoute component
 */
interface ProtectedRouteProps {
  // Child components to render if user is authenticated
  children: React.ReactNode;
  // Optional: Array of role names required to access this route
  requiredRoles?: string[];
}

/**
 * ProtectedRoute Component
 *
 * Wraps components that require authentication. Checks if user is logged in
 * and optionally verifies they have required roles before rendering children.
 *
 * @param children - Components to render when user is authenticated and authorized
 * @param requiredRoles - Optional array of role names required for access
 */
export const ProtectedRoute: React.FC<ProtectedRouteProps> = ({ children, requiredRoles }) => {
  // Get authentication state and role checking methods from context
  const { authUser, hasAnyRole } = useAuth();

  // Show loading spinner while checking authentication state
  if (authUser.loading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="text-center">
          {/* Loading spinner */}
          <div className="inline-block animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  // If user is not authenticated, show auth page (login/register)
  if (!authUser.user) {
    return <AuthPage />;
  }

  // If specific roles are required, check if user has any of them
  if (requiredRoles && requiredRoles.length > 0) {
    if (!hasAnyRole(requiredRoles)) {
      // User is authenticated but doesn't have required roles
      return (
        <div className="min-h-screen flex items-center justify-center bg-gray-50">
          <div className="max-w-md w-full bg-white rounded-lg shadow-lg p-8 text-center">
            <div className="w-16 h-16 bg-red-100 rounded-full mx-auto mb-4 flex items-center justify-center">
              <svg
                className="w-8 h-8 text-red-600"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"
                />
              </svg>
            </div>
            <h2 className="text-2xl font-bold text-gray-900 mb-2">Access Denied</h2>
            <p className="text-gray-600">
              You don't have permission to access this page. Required roles:{' '}
              {requiredRoles.join(', ')}
            </p>
          </div>
        </div>
      );
    }
  }

  // User is authenticated and authorized, render protected content
  return <>{children}</>;
};
