/**
 * User Menu Component
 *
 * Displays user information and provides logout functionality.
 * Shows the user's name, email, assigned roles, and a sign out button.
 */

import React, { useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { LogOut, User, ChevronDown } from 'lucide-react';

/**
 * UserMenu Component
 *
 * Renders a dropdown menu with user profile information and logout option.
 * Displays user's name, email, roles, and provides sign out functionality.
 */
export const UserMenu: React.FC = () => {
  // Get authentication state and methods from context
  const { authUser, signOut } = useAuth();

  // Dropdown menu open/closed state
  const [isOpen, setIsOpen] = useState(false);

  /**
   * Handle sign out
   * Signs out the user and closes the dropdown
   */
  const handleSignOut = async () => {
    try {
      await signOut();
      setIsOpen(false);
    } catch (error) {
      console.error('Error signing out:', error);
    }
  };

  // Don't render if no user is authenticated
  if (!authUser.user || !authUser.profile) {
    return null;
  }

  return (
    <div className="relative">
      {/* User menu button */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="flex items-center space-x-2 px-4 py-2 rounded-lg hover:bg-gray-100 transition-colors"
      >
        <div className="w-8 h-8 bg-blue-100 rounded-full flex items-center justify-center">
          <User className="w-5 h-5 text-blue-600" />
        </div>
        <div className="text-left hidden md:block">
          <p className="text-sm font-medium text-gray-900">
            {authUser.profile.full_name || 'User'}
          </p>
          <p className="text-xs text-gray-500">{authUser.profile.email}</p>
        </div>
        <ChevronDown
          className={`w-4 h-4 text-gray-500 transition-transform ${isOpen ? 'rotate-180' : ''}`}
        />
      </button>

      {/* Dropdown menu */}
      {isOpen && (
        <>
          {/* Backdrop to close menu when clicking outside */}
          <div className="fixed inset-0 z-10" onClick={() => setIsOpen(false)} />

          {/* Menu content */}
          <div className="absolute right-0 mt-2 w-72 bg-white rounded-lg shadow-lg border border-gray-200 py-2 z-20">
            {/* User info section */}
            <div className="px-4 py-3 border-b border-gray-200">
              <p className="text-sm font-medium text-gray-900">
                {authUser.profile.full_name || 'User'}
              </p>
              <p className="text-xs text-gray-500 mt-1">{authUser.profile.email}</p>

              {/* Display user roles */}
              {authUser.roles.length > 0 && (
                <div className="mt-2 flex flex-wrap gap-1">
                  {authUser.roles.map((role) => (
                    <span
                      key={role}
                      className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-blue-100 text-blue-800"
                    >
                      {role}
                    </span>
                  ))}
                </div>
              )}
            </div>

            {/* Sign out button */}
            <button
              onClick={handleSignOut}
              className="w-full flex items-center space-x-2 px-4 py-2 text-sm text-red-600 hover:bg-red-50 transition-colors"
            >
              <LogOut className="w-4 h-4" />
              <span>Sign Out</span>
            </button>
          </div>
        </>
      )}
    </div>
  );
};
