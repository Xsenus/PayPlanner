import { useMemo, useState } from 'react';
import { Building2, Plus, Users, RefreshCcw } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import type { Company, CompanyPayload, Client } from '../../types';
import { CompanyModal } from './CompanyModal';

interface CompaniesSectionProps {
  companies: Company[];
  loading: boolean;
  createCompany: (payload: CompanyPayload) => Promise<void>;
  updateCompany: (id: number, payload: CompanyPayload) => Promise<void>;
  deleteCompany: (id: number) => Promise<void>;
  refreshCompanies: () => Promise<void>;
  clients: Client[];
  clientsLoading: boolean;
  refreshClients: () => Promise<void>;
}

export function CompaniesSection({
  companies,
  loading,
  createCompany,
  updateCompany,
  deleteCompany,
  refreshCompanies,
  clients,
  clientsLoading,
  refreshClients,
}: CompaniesSectionProps) {
  const { t } = useTranslation();
  const [query, setQuery] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [editingCompany, setEditingCompany] = useState<Company | null>(null);

  const filteredCompanies = useMemo(() => {
    const search = query.trim().toLowerCase();
    if (!search) return companies;
    return companies.filter((company) => {
      const inFields =
        company.name.toLowerCase().includes(search) ||
        (company.email ?? '').toLowerCase().includes(search) ||
        (company.phone ?? '').toLowerCase().includes(search) ||
        (company.address ?? '').toLowerCase().includes(search);
      const inMembers = (company.members ?? []).some((member) =>
        member.name.toLowerCase().includes(search) || (member.email ?? '').toLowerCase().includes(search),
      );
      return inFields || inMembers;
    });
  }, [companies, query]);

  const sortedCompanies = useMemo(
    () =>
      [...filteredCompanies].sort((a, b) => Number(b.isActive) - Number(a.isActive) || a.name.localeCompare(b.name)),
    [filteredCompanies],
  );

  const handleAdd = () => {
    setEditingCompany(null);
    setIsModalOpen(true);
  };

  const handleEdit = (company: Company) => {
    setEditingCompany(company);
    setIsModalOpen(true);
  };

  const handleSubmit = async (payload: CompanyPayload) => {
    if (editingCompany) {
      await updateCompany(editingCompany.id, payload);
    } else {
      await createCompany(payload);
    }
    await refreshCompanies();
    await refreshClients();
    setIsModalOpen(false);
  };

  const handleDelete = async (id: number) => {
    await deleteCompany(id);
    await refreshCompanies();
    await refreshClients();
  };

  const activeCount = companies.filter((company) => company.isActive).length;

  return (
    <div className="p-6 space-y-6">
      <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold text-gray-900 flex items-center gap-2">
            <Building2 className="h-6 w-6 text-blue-600" />
            {t('legalEntities')}
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            {t('legalEntitiesDescription') || 'Управление юридическими лицами и связанными контактами.'}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex items-center gap-2 text-sm text-gray-600 bg-gray-100 rounded-full px-3 py-1">
            <Users className="h-4 w-4 text-blue-600" />
            <span>
              {activeCount}/{companies.length} {t('activeCompanyCount') || t('activeClient')}
            </span>
          </div>
          <button
            type="button"
            onClick={refreshCompanies}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-gray-600 hover:text-gray-900 border border-gray-200 rounded-lg">
            <RefreshCcw className="h-4 w-4" />
            {t('refresh') || 'Обновить'}
          </button>
          <button
            type="button"
            onClick={handleAdd}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg">
            <Plus className="h-4 w-4" />
            {t('addCompany')}
          </button>
        </div>
      </div>

      <div className="flex flex-col md:flex-row md:items-center gap-4">
        <div className="flex-1">
          <label className="block text-sm font-medium text-gray-700 mb-1">{t('search')}</label>
          <input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('search') || 'Поиск'}
            className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
          />
        </div>
        <div className="text-sm text-gray-500">
          {clientsLoading
            ? t('loading')
            : t('totalClientsForCompanies', { count: clients.length }) || `${clients.length} ${t('clients')}`}
        </div>
      </div>

      {loading ? (
        <div className="flex justify-center py-16">
          <div className="h-10 w-10 border-4 border-blue-200 border-t-blue-600 rounded-full animate-spin" />
        </div>
      ) : sortedCompanies.length === 0 ? (
        <div className="border border-dashed border-gray-300 rounded-xl p-8 text-center">
          <h2 className="text-lg font-medium text-gray-900 mb-2">{t('noCompaniesYet')}</h2>
          <p className="text-sm text-gray-600 mb-4">{t('noCompaniesDescription')}</p>
          <button
            type="button"
            onClick={handleAdd}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg">
            <Plus className="h-4 w-4" />
            {t('addCompany')}
          </button>
        </div>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {sortedCompanies.map((company) => (
            <div
              key={company.id}
              className="border border-gray-200 rounded-xl shadow-sm hover:shadow-md transition-shadow bg-white p-5 flex flex-col">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <h3 className="text-lg font-semibold text-gray-900">{company.name}</h3>
                  <div className="flex items-center gap-2 mt-1 text-xs uppercase tracking-wide">
                    <span
                      className={`px-2 py-0.5 rounded-full ${
                        company.isActive
                          ? 'bg-emerald-100 text-emerald-700 border border-emerald-200'
                          : 'bg-gray-100 text-gray-600 border border-gray-200'
                      }`}>
                      {company.isActive ? t('activeCompanyLabel') || t('activeClient') : t('inactiveCompanyLabel') || t('inactive')}
                    </span>
                    <span className="text-gray-400">•</span>
                    <span className="text-gray-500">{new Date(company.createdAt).toLocaleDateString()}</span>
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => handleEdit(company)}
                  className="text-sm text-blue-600 hover:text-blue-700 font-medium">
                  {t('edit')}
                </button>
              </div>

              <div className="mt-4 space-y-1 text-sm text-gray-600">
                {company.email ? <p>{company.email}</p> : null}
                {company.phone ? <p>{company.phone}</p> : null}
                {company.address ? <p className="text-gray-500">{company.address}</p> : null}
              </div>

              <div className="mt-4">
                <p className="text-xs font-semibold text-gray-500 uppercase mb-1">
                  {t('companyMembers')}
                </p>
                {company.members && company.members.length > 0 ? (
                  <div className="flex flex-wrap gap-2">
                    {company.members.map((member) => (
                      <span
                        key={member.id}
                        className="inline-flex items-center gap-1 px-3 py-1 text-xs bg-blue-50 text-blue-700 rounded-full">
                        {member.name}
                        {member.role ? <span className="text-blue-500">• {member.role}</span> : null}
                      </span>
                    ))}
                  </div>
                ) : (
                  <p className="text-xs text-gray-500">{t('noMembersYet')}</p>
                )}
              </div>

              {company.notes ? (
                <p className="mt-4 text-sm text-gray-500 bg-gray-50 border border-gray-200 rounded-lg p-3">
                  {company.notes}
                </p>
              ) : null}
            </div>
          ))}
        </div>
      )}

      <CompanyModal
        isOpen={isModalOpen}
        onClose={() => setIsModalOpen(false)}
        onSubmit={handleSubmit}
        onDelete={handleDelete}
        company={editingCompany ?? undefined}
        clients={clients}
      />
    </div>
  );
}
