import { useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { LogIn, Eye, EyeOff, UserPlus } from 'lucide-react';
import type { ApiError } from '../../services/authService';

interface LoginProps {
  onShowRegister?: () => void;
  onPendingApproval?: () => void;
}

function hasCode(e: unknown): e is ApiError {
  return e instanceof Error && 'code' in e;
}

export const Login = ({ onShowRegister, onPendingApproval }: LoginProps) => {
  const { signIn } = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      await signIn(email, password);
    } catch (err: unknown) {
      if (hasCode(err)) {
        if (err.code === 'PendingApproval') {
          onPendingApproval?.();
          setLoading(false);
          return;
        }
        if (err.code === 'UserInactive') {
          setError('Ваш аккаунт отключён. Обратитесь к администратору.');
          setLoading(false);
          return;
        }
      }

      if (err instanceof Error) {
        const msg = err.message?.trim();

        // fallback: if backend error accidentally passed as JSON string
        if (msg && msg.startsWith('{')) {
          try {
            const p = JSON.parse(msg) as Record<string, unknown>;
            const code =
              (typeof p.code === 'string' ? p.code.trim() : undefined) ||
              (typeof p.title === 'string' ? p.title.trim() : undefined);
            const detail = typeof p.detail === 'string' ? p.detail.trim() : undefined;
            if (code === 'PendingApproval') {
              onPendingApproval?.();
              setLoading(false);
              return;
            }
            if (code === 'UserInactive') {
              setError('Ваш аккаунт отключён. Обратитесь к администратору.');
              setLoading(false);
              return;
            }
            setError(detail || code || 'Не удалось войти. Проверьте email и пароль.');
            setLoading(false);
            return;
          } catch {
            // ignore and show plain message below
          }
        }

        setError(msg || 'Не удалось войти. Проверьте email и пароль.');
      } else {
        setError('Не удалось войти. Проверьте email и пароль.');
      }
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="min-h-screen bg-gradient-to-br from-slate-50 to-slate-100 flex items-center justify-center p-4">
      <div className="w-full max-w-md">
        <div className="bg-white rounded-2xl shadow-xl p-8">
          <div className="flex items-center justify-center mb-8">
            <div className="bg-slate-900 rounded-full p-4">
              <LogIn className="w-8 h-8 text-white" />
            </div>
          </div>

          <h1 className="text-3xl font-bold text-center text-slate-900 mb-2">Добро пожаловать</h1>
          <p className="text-center text-slate-600 mb-8">Войдите в аккаунт, чтобы продолжить</p>

          {error && (
            <div className="mb-6 p-4 bg-red-50 border border-red-200 rounded-lg text-red-700 text-sm">
              {error}
            </div>
          )}

          <form onSubmit={handleSubmit} className="space-y-6">
            <div>
              <label htmlFor="email" className="block text-sm font-medium text-slate-700 mb-2">
                Email
              </label>
              <input
                id="email"
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className="w-full px-4 py-3 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900 focus:border-transparent transition-all"
                placeholder="you@example.com"
                required
                autoComplete="email"
              />
            </div>

            <div>
              <label htmlFor="password" className="block text-sm font-medium text-slate-700 mb-2">
                Пароль
              </label>
              <div className="relative">
                <input
                  id="password"
                  type={showPassword ? 'text' : 'password'}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="w-full px-4 py-3 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900 focus:border-transparent transition-all pr-12"
                  placeholder="Введите пароль"
                  required
                  autoComplete="current-password"
                />
                <button
                  type="button"
                  onClick={() => setShowPassword(!showPassword)}
                  className="absolute right-3 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600 transition-colors">
                  {showPassword ? <EyeOff className="w-5 h-5" /> : <Eye className="w-5 h-5" />}
                </button>
              </div>
            </div>

            <button
              type="submit"
              disabled={loading}
              className="w-full py-3 px-4 bg-slate-900 text-white font-medium rounded-lg hover:bg-slate-800 focus:outline-none focus:ring-2 focus:ring-slate-900 focus:ring-offset-2 transition-all disabled:opacity-50 disabled:cursor-not-allowed">
              {loading ? 'Входим…' : 'Войти'}
            </button>
          </form>

          {onShowRegister && (
            <div className="mt-6 text-center">
              <button
                onClick={onShowRegister}
                className="inline-flex items-center gap-2 text-sm text-slate-600 hover:text-slate-900 transition-colors">
                <UserPlus className="w-4 h-4" />
                <span>
                  Нет аккаунта? <span className="font-medium">Зарегистрироваться</span>
                </span>
              </button>
            </div>
          )}
        </div>

        <div className="mt-6 text-center text-sm text-slate-600">
          <p>PayPlanner — система управления платежами</p>
        </div>
      </div>
    </div>
  );
};
