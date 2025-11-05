import { useMemo, useState } from 'react';
import { Building2, Plus, Users, Search, X } from 'lucide-react';
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
      const nameMatches =
        (company.name ?? '').toLowerCase().includes(search) ||
        (company.shortName ?? '').toLowerCase().includes(search) ||
        (company.fullName ?? '').toLowerCase().includes(search);
      const registryMatches =
        (company.inn ?? '').toLowerCase().includes(search) ||
        (company.kpp ?? '').toLowerCase().includes(search);
      const contactMatches =
        (company.email ?? '').toLowerCase().includes(search) ||
        (company.phone ?? '').toLowerCase().includes(search);
      const addressMatches =
        (company.actualAddress ?? '').toLowerCase().includes(search) ||
        (company.legalAddress ?? '').toLowerCase().includes(search);
      const notesMatch = (company.notes ?? '').toLowerCase().includes(search);
      const inFields = nameMatches || registryMatches || contactMatches || addressMatches || notesMatch;
      const inMembers = (company.members ?? []).some((member) =>
        member.name.toLowerCase().includes(search) || (member.email ?? '').toLowerCase().includes(search),
      );
      return inFields || inMembers;
    });
  }, [companies, query]);

  const sortedCompanies = useMemo(
    () =>
      [...filteredCompanies].sort((a, b) => {
        const order = Number(b.isActive) - Number(a.isActive);
        if (order !== 0) return order;
        const aName = a.shortName || a.name || a.fullName || '';
        const bName = b.shortName || b.name || b.fullName || '';
        return aName.localeCompare(bName, undefined, { sensitivity: 'base' });
      }),
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
    <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-start gap-3">
          <div className="hidden sm:flex h-12 w-12 items-center justify-center rounded-full bg-blue-50 text-blue-600">
            <Building2 className="h-6 w-6" />
          </div>
          <div>
            <div className="flex items-center gap-2">
              <Building2 className="h-6 w-6 text-blue-600 sm:hidden" />
              <h1 className="text-2xl font-semibold text-gray-900">{t('legalEntities')}</h1>
            </div>
            <p className="text-sm text-gray-600 mt-1">
              {t('legalEntitiesDescription') || 'Управление юридическими лицами и связанными контактами.'}
            </p>
            <div className="mt-3 flex flex-wrap items-center gap-2 text-xs uppercase tracking-wide text-gray-500">
              <span className="inline-flex items-center gap-2 rounded-full bg-gray-100 px-3 py-1 text-gray-600">
                <Users className="h-4 w-4 text-blue-600" />
                {activeCount}/{companies.length} {t('activeCompanyCount') || t('activeClient')}
              </span>
            </div>
          </div>
        </div>
        <button
          type="button"
          onClick={handleAdd}
          className="inline-flex items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
        >
          <Plus className="h-4 w-4" />
          {t('addCompany')}
        </button>
      </div>

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="relative w-full sm:w-72">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400" size={16} />
          <input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('search') || 'Поиск по наименованию, ИНН или адресу'}
            className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
          {query && (
            <button
              type="button"
              onClick={() => setQuery('')}
              className="absolute right-2 top-1/2 -translate-y-1/2 rounded-full p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-600"
              aria-label={t('clear') || 'Очистить'}
            >
              <X className="h-4 w-4" />
            </button>
          )}
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
          {sortedCompanies.map((company) => {
            const companyShortName = company.shortName || company.name || company.fullName;
            const showFullName = company.fullName && company.fullName !== companyShortName;
            return (
              <div
                key={company.id}
                className="flex flex-col rounded-xl border border-gray-200 bg-white p-5 shadow-sm transition-shadow hover:shadow-md">
                <div className="flex items-start justify-between gap-3">
                  <div className="space-y-2">
                    <div>
                      <h3 className="text-lg font-semibold text-gray-900">{companyShortName}</h3>
                      {showFullName ? <p className="text-sm text-gray-500">{company.fullName}</p> : null}
                    </div>
                    <div className="flex flex-wrap items-center gap-2 text-xs uppercase tracking-wide">
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
                    className="text-sm font-medium text-blue-600 hover:text-blue-700">
                    {t('edit')}
                  </button>
                </div>

                <div className="mt-4 space-y-3 text-sm text-gray-600">
                  <div className="flex flex-wrap gap-2">
                    {company.inn ? (
                      <span className="rounded-full bg-gray-100 px-3 py-1 text-xs font-medium text-gray-700">ИНН {company.inn}</span>
                    ) : null}
                    {company.kpp ? (
                      <span className="rounded-full bg-gray-100 px-3 py-1 text-xs font-medium text-gray-700">КПП {company.kpp}</span>
                    ) : null}
                  </div>
                  {company.actualAddress ? (
                    <div>
                      <p className="text-xs font-semibold uppercase text-gray-500">{t('actualAddress')}</p>
                      <p className="text-sm text-gray-700">{company.actualAddress}</p>
                    </div>
                  ) : null}
                  {company.legalAddress ? (
                    <div>
                      <p className="text-xs font-semibold uppercase text-gray-500">{t('legalAddress')}</p>
                      <p className="text-sm text-gray-700">{company.legalAddress}</p>
                    </div>
                  ) : null}
                  {company.phone || company.email ? (
                    <div className="flex flex-col gap-1 text-sm text-gray-700">
                      {company.phone ? <span>{company.phone}</span> : null}
                      {company.email ? <span>{company.email}</span> : null}
                    </div>
                  ) : null}
                </div>

                <div className="mt-4">
                  <p className="text-xs font-semibold text-gray-500 uppercase mb-1">{t('companyMembers')}</p>
                  {company.members && company.members.length > 0 ? (
                    <div className="flex flex-wrap gap-2">
                      {company.members.map((member) => (
                        <span
                          key={member.id}
                          className="inline-flex items-center gap-1 rounded-full bg-blue-50 px-3 py-1 text-xs text-blue-700">
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
                  <p className="mt-4 rounded-lg border border-gray-200 bg-gray-50 p-3 text-sm text-gray-600">{company.notes}</p>
                ) : null}
              </div>
            );
          })}
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
