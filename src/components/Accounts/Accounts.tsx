import { WalletCards } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';

export function Accounts() {
  const { t } = useTranslation();

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-4xl mx-auto p-4 sm:p-6">
        <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-8 text-center">
          <WalletCards className="h-12 w-12 mx-auto mb-4 text-blue-500" />
          <h1 className="text-2xl font-bold text-gray-900 mb-2">{t('accounts') ?? 'Счета'}</h1>
          <p className="text-gray-600 mb-1">
            {t('sectionUnderConstruction') ?? 'Раздел в разработке'}
          </p>
          <p className="text-gray-500">
            {t('sectionUnderConstructionDescription') ?? 'Скоро здесь появится функционал.'}
          </p>
        </div>
      </div>
    </div>
  );
}
