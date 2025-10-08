import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { authService, type User } from '../services/authService';

interface AuthContextType {
  user: User | null;
  loading: boolean;
  signIn: (email: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
  isAdmin: () => boolean;
  isManager: () => boolean;
  refreshUser: () => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

// eslint-disable-next-line react-refresh/only-export-components
export const useAuth = () => {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used within AuthProvider');
  return context;
};

export const AuthProvider = ({ children }: { children: ReactNode }) => {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  const fetchUser = async () => {
    try {
      if (!authService.isAuthenticated()) {
        setUser(null);
        return;
      }
      const me = await authService.getCurrentUser();
      setUser(me);
    } catch (error) {
      console.error('Ошибка загрузки профиля:', error);
      authService.logout();
      setUser(null);
    }
  };

  const refreshUser = async () => {
    await fetchUser();
  };

  useEffect(() => {
    fetchUser().finally(() => setLoading(false));
  }, []);

  const signIn = async (email: string, password: string) => {
    const resp = await authService.login(email, password);
    if (resp.user) {
      setUser(resp.user);
    } else {
      const me = await authService.getCurrentUser();
      setUser(me);
    }
  };

  const signOut = async () => {
    authService.logout();
    setUser(null);
  };

  const isAdmin = () => user?.role?.name === 'admin';
  const isManager = () => user?.role?.name === 'manager' || user?.role?.name === 'admin';

  const value: AuthContextType = {
    user,
    loading,
    signIn,
    signOut,
    isAdmin,
    isManager,
    refreshUser,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};
