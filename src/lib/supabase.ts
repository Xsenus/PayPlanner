/**
 * Supabase Client Configuration
 *
 * This module initializes and exports a singleton Supabase client instance
 * for use throughout the application. The client handles authentication,
 * database operations, and real-time subscriptions.
 */

import { createClient } from '@supabase/supabase-js';

// Get Supabase configuration from environment variables
const supabaseUrl = import.meta.env.VITE_SUPABASE_URL;
const supabaseAnonKey = import.meta.env.VITE_SUPABASE_ANON_KEY;

// Validate that required environment variables are present
if (!supabaseUrl || !supabaseAnonKey) {
  throw new Error('Missing Supabase environment variables');
}

/**
 * Singleton Supabase client instance
 * Configured with auto-refresh for authentication tokens
 * and persistent sessions in local storage
 */
export const supabase = createClient(supabaseUrl, supabaseAnonKey, {
  auth: {
    // Store session in local storage for persistence across page reloads
    storage: window.localStorage,
    // Automatically refresh tokens before they expire
    autoRefreshToken: true,
    // Persist session across browser tabs
    persistSession: true,
    // Detect session changes across tabs
    detectSessionInUrl: true,
  },
});
