/**
 * Authentication Context Provider
 *
 * This module provides a React context for managing authentication state
 * throughout the application. It handles user sessions, profile data,
 * role-based permissions, and provides methods for sign in/up/out.
 *
 * Features:
 * - Persistent session management across page reloads
 * - Automatic token refresh
 * - Role-based access control
 * - Real-time session state updates
 */

import React, { createContext, useContext, useEffect, useState, useCallback } from 'react';
import { User, Session, AuthError } from '@supabase/supabase-js';
import { supabase } from '../lib/supabase';
import {
  AuthContextType,
  AuthUser,
  LoginCredentials,
  RegisterData,
  UserProfile,
} from '../types/auth';

// Create the authentication context
const AuthContext = createContext<AuthContextType | undefined>(undefined);

/**
 * AuthProvider Component
 *
 * Wraps the application to provide authentication context to all child components.
 * Manages session state, user profiles, and roles.
 *
 * @param children - React child components that will have access to auth context
 */
export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  // Authentication state
  const [authUser, setAuthUser] = useState<AuthUser>({
    user: null,
    session: null,
    profile: null,
    roles: [],
    loading: true,
  });

  /**
   * Fetch user profile and roles from the database
   *
   * @param userId - The user's unique identifier
   * @returns Promise resolving to profile and roles array
   */
  const fetchUserData = useCallback(async (userId: string) => {
    try {
      // Fetch user profile
      const { data: profile, error: profileError } = await supabase
        .from('user_profiles')
        .select('*')
        .eq('id', userId)
        .maybeSingle();

      if (profileError) {
        console.error('Error fetching user profile:', profileError);
        return { profile: null, roles: [] };
      }

      // Fetch user roles
      const { data: userRoles, error: rolesError } = await supabase
        .from('user_roles')
        .select('role_id, roles(name)')
        .eq('user_id', userId);

      if (rolesError) {
        console.error('Error fetching user roles:', rolesError);
        return { profile, roles: [] };
      }

      // Extract role names from the joined query result
      const roles = userRoles?.map((ur: any) => ur.roles.name) || [];

      return { profile: profile as UserProfile | null, roles };
    } catch (error) {
      console.error('Error in fetchUserData:', error);
      return { profile: null, roles: [] };
    }
  }, []);

  /**
   * Update authentication state with user, session, profile, and roles
   *
   * @param user - Supabase auth user object
   * @param session - Current session object
   */
  const updateAuthUser = useCallback(
    async (user: User | null, session: Session | null) => {
      if (user && session) {
        // User is authenticated, fetch their profile and roles
        const { profile, roles } = await fetchUserData(user.id);
        setAuthUser({
          user,
          session,
          profile,
          roles,
          loading: false,
        });
      } else {
        // User is not authenticated
        setAuthUser({
          user: null,
          session: null,
          profile: null,
          roles: [],
          loading: false,
        });
      }
    },
    [fetchUserData]
  );

  /**
   * Initialize authentication state on component mount
   * Checks for existing session and sets up auth state listener
   */
  useEffect(() => {
    // Check for existing session
    supabase.auth.getSession().then(({ data: { session } }) => {
      updateAuthUser(session?.user || null, session);
    });

    // Listen for authentication state changes
    // Using an async block inside the callback to avoid deadlocks
    const {
      data: { subscription },
    } = supabase.auth.onAuthStateChange((event, session) => {
      (async () => {
        console.log('Auth state changed:', event);
        await updateAuthUser(session?.user || null, session);
      })();
    });

    // Cleanup subscription on unmount
    return () => {
      subscription.unsubscribe();
    };
  }, [updateAuthUser]);

  /**
   * Sign in with email and password
   *
   * @param credentials - User email and password
   * @throws AuthError if sign in fails
   */
  const signIn = async (credentials: LoginCredentials): Promise<void> => {
    const { error } = await supabase.auth.signInWithPassword({
      email: credentials.email,
      password: credentials.password,
    });

    if (error) {
      throw error;
    }
  };

  /**
   * Sign up new user with email and password
   *
   * @param data - Registration data including email, password, and optional full_name
   * @throws AuthError if registration fails
   */
  const signUp = async (data: RegisterData): Promise<void> => {
    const { error } = await supabase.auth.signUp({
      email: data.email,
      password: data.password,
      options: {
        data: {
          full_name: data.full_name || '',
        },
      },
    });

    if (error) {
      throw error;
    }
  };

  /**
   * Sign out current user
   *
   * @throws AuthError if sign out fails
   */
  const signOut = async (): Promise<void> => {
    const { error } = await supabase.auth.signOut();

    if (error) {
      throw error;
    }
  };

  /**
   * Check if user has a specific role
   *
   * @param roleName - Name of the role to check
   * @returns true if user has the role, false otherwise
   */
  const hasRole = useCallback(
    (roleName: string): boolean => {
      return authUser.roles.includes(roleName);
    },
    [authUser.roles]
  );

  /**
   * Check if user has any of the specified roles
   *
   * @param roleNames - Array of role names to check
   * @returns true if user has at least one of the roles, false otherwise
   */
  const hasAnyRole = useCallback(
    (roleNames: string[]): boolean => {
      return roleNames.some((role) => authUser.roles.includes(role));
    },
    [authUser.roles]
  );

  /**
   * Refresh user profile and roles
   * Useful after role assignments change or profile updates
   */
  const refreshUser = useCallback(async (): Promise<void> => {
    if (authUser.user) {
      const { profile, roles } = await fetchUserData(authUser.user.id);
      setAuthUser((prev) => ({
        ...prev,
        profile,
        roles,
      }));
    }
  }, [authUser.user, fetchUserData]);

  // Context value provided to all child components
  const value: AuthContextType = {
    authUser,
    signIn,
    signUp,
    signOut,
    hasRole,
    hasAnyRole,
    refreshUser,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};

/**
 * Custom hook to access authentication context
 *
 * @returns Authentication context value
 * @throws Error if used outside of AuthProvider
 */
export const useAuth = (): AuthContextType => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
};
