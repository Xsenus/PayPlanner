import { useState } from 'react';
import { useTranslation } from '../../hooks/useTranslation';
import { useCompanies } from '../../hooks/useCompanies';
import { useClients as useClientsList } from '../../hooks/useClients';
import { IndividualsSection } from './IndividualsSection';
import { CompaniesSection } from './CompaniesSection';

export function Clients() {
  const { t } = useTranslation();
  const [activeTab, setActiveTab] = useState<'individuals' | 'companies'>('individuals');

  const {
    companies,
    loading: companiesLoading,
    createCompany,
    updateCompany,
    deleteCompany,
    refresh: refreshCompanies,
  } = useCompanies();

  const {
    clients,
    loading: clientsLoading,
    refresh: refreshClients,
  } = useClientsList();

  const handleRelationsChanged = async () => {
    await refreshCompanies();
    await refreshClients();
  };

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-4 sm:p-6">
        <div className="flex flex-wrap items-center gap-4 border-b border-gray-200 pb-4 mb-6">
          <h1 className="text-3xl font-bold text-gray-900">{t('clients')}</h1>
          <div className="flex items-center gap-2 bg-white border border-gray-200 rounded-full p-1">
            <button
              type="button"
              onClick={() => setActiveTab('individuals')}
              className={`px-4 py-1.5 text-sm font-medium rounded-full transition-colors ${
                activeTab === 'individuals'
                  ? 'bg-blue-600 text-white shadow'
                  : 'text-gray-600 hover:text-gray-900'
              }`}>
              {t('individualClients')}
            </button>
            <button
              type="button"
              onClick={() => setActiveTab('companies')}
              className={`px-4 py-1.5 text-sm font-medium rounded-full transition-colors ${
                activeTab === 'companies'
                  ? 'bg-blue-600 text-white shadow'
                  : 'text-gray-600 hover:text-gray-900'
              }`}>
              {t('companyClients')}
            </button>
          </div>
        </div>

        {activeTab === 'individuals' ? (
          <IndividualsSection
            availableCompanies={companies}
            onRelationsChanged={handleRelationsChanged}
          />
        ) : (
          <CompaniesSection
            companies={companies}
            loading={companiesLoading}
            createCompany={createCompany}
            updateCompany={updateCompany}
            deleteCompany={deleteCompany}
            refreshCompanies={refreshCompanies}
            clients={clients}
            clientsLoading={clientsLoading}
            refreshClients={refreshClients}
          />
        )}
      </div>
    </div>
  );
}
