/**
 * Authentication Type Definitions
 *
 * This module defines TypeScript interfaces and types for the authentication
 * system, including user profiles, roles, and session information.
 */

import { User, Session } from '@supabase/supabase-js';

/**
 * User Profile interface
 * Represents extended user information stored in the user_profiles table
 */
export interface UserProfile {
  id: string;
  email: string;
  full_name: string | null;
  created_at: string;
  updated_at: string;
}

/**
 * Role interface
 * Represents a role in the system (e.g., admin, manager, user)
 */
export interface Role {
  id: string;
  name: string;
  description: string | null;
  created_at: string;
}

/**
 * User Role Assignment interface
 * Represents the many-to-many relationship between users and roles
 */
export interface UserRole {
  id: string;
  user_id: string;
  role_id: string;
  assigned_at: string;
}

/**
 * Extended User Context interface
 * Contains all authentication and authorization information for the current user
 */
export interface AuthUser {
  // Supabase auth user object
  user: User | null;
  // Current session information
  session: Session | null;
  // User profile from database
  profile: UserProfile | null;
  // Array of role names assigned to the user
  roles: string[];
  // Loading state during initial auth check
  loading: boolean;
}

/**
 * Login Credentials interface
 * Data required for user login
 */
export interface LoginCredentials {
  email: string;
  password: string;
}

/**
 * Registration Data interface
 * Data required for new user registration
 */
export interface RegisterData {
  email: string;
  password: string;
  full_name?: string;
}

/**
 * Auth Context interface
 * Defines the shape of the authentication context provider
 */
export interface AuthContextType {
  // Current authenticated user and their data
  authUser: AuthUser;
  // Sign in with email and password
  signIn: (credentials: LoginCredentials) => Promise<void>;
  // Sign up new user with email and password
  signUp: (data: RegisterData) => Promise<void>;
  // Sign out current user
  signOut: () => Promise<void>;
  // Check if user has a specific role
  hasRole: (roleName: string) => boolean;
  // Check if user has any of the specified roles
  hasAnyRole: (roleNames: string[]) => boolean;
  // Refresh user profile and roles
  refreshUser: () => Promise<void>;
}
