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
            Registration Received
          </h1>

          <div className="space-y-4 mb-8 text-slate-600">
            <p className="text-center">
              Thank you for registering with PayPlanner!
            </p>

            <div className="bg-slate-50 rounded-lg p-4 space-y-3">
              <div className="flex items-start gap-3">
                <Mail className="w-5 h-5 text-slate-400 mt-0.5 flex-shrink-0" />
                <div className="text-sm">
                  <p className="font-medium text-slate-900 mb-1">Email Confirmation Sent</p>
                  <p>We've sent a confirmation email to your inbox.</p>
                </div>
              </div>

              <div className="flex items-start gap-3">
                <Clock className="w-5 h-5 text-slate-400 mt-0.5 flex-shrink-0" />
                <div className="text-sm">
                  <p className="font-medium text-slate-900 mb-1">Awaiting Admin Approval</p>
                  <p>An administrator will review your registration shortly. You'll receive an email once your account is approved.</p>
                </div>
              </div>
            </div>

            <p className="text-center text-sm">
              You cannot log in until your account has been approved.
            </p>
          </div>

          <button
            onClick={onBackToLogin}
            className="w-full flex items-center justify-center gap-2 py-3 px-4 bg-slate-900 text-white font-medium rounded-lg hover:bg-slate-800 focus:outline-none focus:ring-2 focus:ring-slate-900 focus:ring-offset-2 transition-all"
          >
            <ArrowLeft className="w-5 h-5" />
            Back to Login
          </button>
        </div>

        <div className="mt-6 text-center text-sm text-slate-600">
          <p>If you have questions, please contact your system administrator.</p>
        </div>
      </div>
    </div>
  );
};
