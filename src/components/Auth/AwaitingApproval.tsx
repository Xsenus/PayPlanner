import { Clock, Mail, ArrowLeft } from 'lucide-react';

interface AwaitingApprovalProps {
  onBackToLogin: () => void;
}

export const AwaitingApproval = ({ onBackToLogin }: AwaitingApprovalProps) => {
  return (
    <div className="min-h-screen bg-gradient-to-br from-slate-50 to-slate-100 flex items-center justify-center p-4">
      <div className="w-full max-w-md">
        <div className="bg-white rounded-2xl shadow-xl p-8">
          <div className="flex items-center justify-center mb-8">
            <div className="bg-amber-100 rounded-full p-4">
              <Clock className="w-12 h-12 text-amber-600" />
            </div>
          </div>

          <h1 className="text-3xl font-bold text-center text-slate-900 mb-4">
            Заявка на регистрацию получена
          </h1>

          <div className="space-y-4 mb-8 text-slate-600">
            <p className="text-center">Спасибо за регистрацию в PayPlanner!</p>

            <div className="bg-slate-50 rounded-lg p-4 space-y-3">
              <div className="flex items-start gap-3">
                <Mail className="w-5 h-5 text-slate-400 mt-0.5 flex-shrink-0" />
                <div className="text-sm">
                  <p className="font-medium text-slate-900 mb-1">Письмо отправлено</p>
                  <p>Мы отправили письмо с подтверждением на вашу почту.</p>
                </div>
              </div>

              <div className="flex items-start gap-3">
                <Clock className="w-5 h-5 text-slate-400 mt-0.5 flex-shrink-0" />
                <div className="text-sm">
                  <p className="font-medium text-slate-900 mb-1">
                    Ожидает одобрения администратора
                  </p>
                  <p>
                    Администратор скоро проверит вашу заявку. Вы получите письмо, когда аккаунт
                    будет одобрен.
                  </p>
                </div>
              </div>
            </div>

            <p className="text-center text-sm">
              Вы не сможете войти, пока ваш аккаунт не будет одобрен.
            </p>
          </div>

          <button
            onClick={onBackToLogin}
            className="w-full flex items-center justify-center gap-2 py-3 px-4 bg-slate-900 text-white font-medium rounded-lg hover:bg-slate-800 focus:outline-none focus:ring-2 focus:ring-slate-900 focus:ring-offset-2 transition-all">
            <ArrowLeft className="w-5 h-5" />
            Назад ко входу
          </button>
        </div>

        <div className="mt-6 text-center text-sm text-slate-600">
          <p>Если возникли вопросы — свяжитесь с администратором системы.</p>
        </div>
      </div>
    </div>
  );
};
