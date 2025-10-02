import React, { createContext, useContext, useEffect, useState, useCallback } from 'react';
import { User, Session } from '@supabase/supabase-js';
import { supabase } from '../lib/supabase';
import {
  AuthContextType,
  AuthUser,
  LoginCredentials,
  RegisterData,
  UserProfile,
} from '../types/auth';
import { authApiService, CSharpAuthUser } from '../services/authApi';

const AuthContext = createContext<AuthContextType | undefined>(undefined);

const isDevelopment = import.meta.env.DEV;

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [authUser, setAuthUser] = useState<AuthUser>({
    user: null,
    session: null,
    profile: null,
    roles: [],
    loading: true,
  });
  const [useSupabase, setUseSupabase] = useState<boolean>(false);
  const [apiChecked, setApiChecked] = useState(false);

  useEffect(() => {
    const checkApi = async () => {
      if (isDevelopment) {
        const apiAvailable = await authApiService.checkApiAvailable();
        setUseSupabase(!apiAvailable);
      } else {
        setUseSupabase(false);
      }
      setApiChecked(true);
    };
    checkApi();
  }, []);

  const mapCSharpToAuthUser = (csharpUser: CSharpAuthUser): AuthUser => {
    return {
      user: {
        id: csharpUser.id,
        email: csharpUser.email,
        aud: 'authenticated',
        role: 'authenticated',
        created_at: csharpUser.createdAt,
        updated_at: csharpUser.createdAt,
        app_metadata: {},
        user_metadata: { full_name: csharpUser.fullName },
      } as User,
      session: {
        access_token: authApiService.getToken() || '',
        token_type: 'bearer',
        expires_in: 604800,
        expires_at: Date.now() / 1000 + 604800,
        refresh_token: '',
        user: {} as User,
      } as Session,
      profile: {
        id: csharpUser.id,
        email: csharpUser.email,
        full_name: csharpUser.fullName,
        created_at: csharpUser.createdAt,
        updated_at: csharpUser.createdAt,
      },
      roles: csharpUser.roles,
      loading: false,
    };
  };

  const fetchSupabaseUserData = useCallback(async (userId: string) => {
    try {
      const { data: profile, error: profileError } = await supabase
        .from('user_profiles')
        .select('*')
        .eq('id', userId)
        .maybeSingle();

      if (profileError) {
        console.error('Error fetching user profile:', profileError);
        return { profile: null, roles: [] };
      }

      const { data: userRoles, error: rolesError } = await supabase
        .from('user_roles')
        .select('role_id, roles(name)')
        .eq('user_id', userId);

      if (rolesError) {
        console.error('Error fetching user roles:', rolesError);
        return { profile, roles: [] };
      }

      const roles = userRoles?.map((ur: any) => ur.roles.name) || [];

      return { profile: profile as UserProfile | null, roles };
    } catch (error) {
      console.error('Error in fetchUserData:', error);
      return { profile: null, roles: [] };
    }
  }, []);

  const updateSupabaseAuthUser = useCallback(
    async (user: User | null, session: Session | null) => {
      if (user && session) {
        const { profile, roles } = await fetchSupabaseUserData(user.id);
        setAuthUser({
          user,
          session,
          profile,
          roles,
          loading: false,
        });
      } else {
        setAuthUser({
          user: null,
          session: null,
          profile: null,
          roles: [],
          loading: false,
        });
      }
    },
    [fetchSupabaseUserData]
  );

  useEffect(() => {
    if (!apiChecked) return;

    if (useSupabase) {
      supabase.auth.getSession().then(({ data: { session } }) => {
        updateSupabaseAuthUser(session?.user || null, session);
      });

      const {
        data: { subscription },
      } = supabase.auth.onAuthStateChange((event, session) => {
        (async () => {
          console.log('Auth state changed:', event);
          await updateSupabaseAuthUser(session?.user || null, session);
        })();
      });

      return () => {
        subscription.unsubscribe();
      };
    } else {
      const token = authApiService.getToken();
      if (token) {
        authApiService
          .getMe()
          .then((csharpUser) => {
            setAuthUser(mapCSharpToAuthUser(csharpUser));
          })
          .catch(() => {
            authApiService.logout();
            setAuthUser({
              user: null,
              session: null,
              profile: null,
              roles: [],
              loading: false,
            });
          });
      } else {
        setAuthUser({
          user: null,
          session: null,
          profile: null,
          roles: [],
          loading: false,
        });
      }
    }
  }, [updateSupabaseAuthUser, useSupabase, apiChecked]);

  const signIn = async (credentials: LoginCredentials): Promise<void> => {
    if (useSupabase) {
      const { error } = await supabase.auth.signInWithPassword({
        email: credentials.email,
        password: credentials.password,
      });

      if (error) {
        throw error;
      }
    } else {
      const response = await authApiService.login({
        email: credentials.email,
        password: credentials.password,
      });
      setAuthUser(mapCSharpToAuthUser(response.user));
    }
  };

  const signUp = async (data: RegisterData): Promise<void> => {
    if (useSupabase) {
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
    } else {
      const response = await authApiService.register({
        email: data.email,
        password: data.password,
        fullName: data.full_name || '',
      });
      setAuthUser(mapCSharpToAuthUser(response.user));
    }
  };

  const signOut = async (): Promise<void> => {
    if (useSupabase) {
      const { error } = await supabase.auth.signOut();

      if (error) {
        throw error;
      }
    } else {
      authApiService.logout();
      setAuthUser({
        user: null,
        session: null,
        profile: null,
        roles: [],
        loading: false,
      });
    }
  };

  const hasRole = useCallback(
    (roleName: string): boolean => {
      return authUser.roles.includes(roleName);
    },
    [authUser.roles]
  );

  const hasAnyRole = useCallback(
    (roleNames: string[]): boolean => {
      return roleNames.some((role) => authUser.roles.includes(role));
    },
    [authUser.roles]
  );

  const refreshUser = useCallback(async (): Promise<void> => {
    if (useSupabase && authUser.user) {
      const { profile, roles } = await fetchSupabaseUserData(authUser.user.id);
      setAuthUser((prev) => ({
        ...prev,
        profile,
        roles,
      }));
    } else if (!useSupabase) {
      try {
        const csharpUser = await authApiService.getMe();
        setAuthUser(mapCSharpToAuthUser(csharpUser));
      } catch {
        authApiService.logout();
        setAuthUser({
          user: null,
          session: null,
          profile: null,
          roles: [],
          loading: false,
        });
      }
    }
  }, [authUser.user, fetchSupabaseUserData, useSupabase]);

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

export const useAuth = (): AuthContextType => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
};
