import { useMemo, useState } from 'react';
import { apiService } from '../../services/api';
import type { LegacyAddressMigrationResult } from '../../services/api';

type SettingsSection = 'general' | 'import-addresses';

export function AppSettings() {
  const [activeSection, setActiveSection] = useState<SettingsSection>('import-addresses');
  const [loading, setLoading] = useState(false);
  const [validateWithDadata, setValidateWithDadata] = useState(true);
  const [result, setResult] = useState<LegacyAddressMigrationResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  const activeSectionTitle = useMemo(() => {
    if (activeSection === 'general') return 'Основные';
    return 'Импорт · Перенос адресов';
  }, [activeSection]);

  const runMigration = async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await apiService.migrateLegacyLegalEntityAddresses(validateWithDadata);
      setResult(response);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось выполнить перенос адресов');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="p-6 lg:p-8">
      <div className="max-w-7xl mx-auto">
        <div className="grid grid-cols-1 lg:grid-cols-[280px_1fr] gap-6">
          <aside className="bg-white border border-gray-200 rounded-2xl shadow-sm p-4 h-fit">
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Настройки</h2>

            <nav aria-label="Разделы настроек" className="space-y-2">
              <button
                type="button"
                onClick={() => setActiveSection('general')}
                className={`w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                  activeSection === 'general'
                    ? 'bg-blue-50 text-blue-700 border border-blue-200'
                    : 'text-gray-700 hover:bg-gray-50 border border-transparent'
                }`}
              >
                Основные
              </button>

              <div className="rounded-xl border border-gray-200 bg-gray-50/60 p-2">
                <div className="px-2 py-1 text-xs font-semibold uppercase tracking-wide text-gray-500">Импорт</div>
                <button
                  type="button"
                  onClick={() => setActiveSection('import-addresses')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'import-addresses'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Перенос адресов
                </button>
              </div>
            </nav>
          </aside>

          <section className="space-y-6">
            <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6">
              <h3 className="text-lg font-semibold text-gray-900">{activeSectionTitle}</h3>
              <p className="text-sm text-gray-600 mt-1">
                {activeSection === 'general'
                  ? 'Базовые параметры системы. Раздел подготовлен для последующего расширения.'
                  : 'Перенос старых адресов из legacy-поля Address в модель адресов для юр. и физ. лиц.'}
              </p>
            </div>

            {activeSection === 'general' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 text-sm text-gray-600">
                Здесь будут общие настройки приложения.
              </div>
            )}

            {activeSection === 'import-addresses' && (
              <>
                <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                  <label className="flex items-start gap-3 text-sm text-gray-700">
                    <input
                      type="checkbox"
                      checked={validateWithDadata}
                      onChange={(event) => setValidateWithDadata(event.target.checked)}
                      className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    />
                    <span>
                      Переносить <b>с проверкой через DaData</b> (если выключить — перенос будет без DaData)
                    </span>
                  </label>

                  <div className="flex items-center gap-3">
                    <button
                      onClick={runMigration}
                      disabled={loading}
                      className="px-4 py-2 rounded-lg bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60 disabled:cursor-not-allowed"
                    >
                      {loading ? 'Переносим…' : 'Перенести старые адреса'}
                    </button>
                  </div>

                  {error && (
                    <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                      {error}
                    </div>
                  )}

                  {result && (
                    <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                      <p className="font-medium mb-2">Отчёт по переносу</p>
                      <ul className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1">
                        <li>
                          Проверено всего карточек: <b>{result.scanned}</b>
                        </li>
                        <li>
                          Из них юр. лиц: <b>{result.scannedLegalEntities ?? 0}</b>
                        </li>
                        <li>
                          Из них физ. лиц: <b>{result.scannedClients ?? 0}</b>
                        </li>
                        <li>
                          Добавлено новых адресов: <b>{result.added}</b>
                        </li>
                        <li>
                          Обновлено существующих записей: <b>{result.updated}</b>
                        </li>
                        <li>
                          Пропущено дубликатов: <b>{result.duplicateSkipped}</b>
                        </li>
                        <li>
                          Пропущено пустых: <b>{result.emptySkipped}</b>
                        </li>
                        <li>
                          Ошибок: <b>{result.failed}</b>
                        </li>
                        <li>
                          Совпадений DaData: <b>{result.dadataMatched}</b>
                        </li>
                        <li>
                          Без совпадения DaData: <b>{result.dadataNotMatched}</b>
                        </li>
                        <li>
                          Режим: <b>{result.validateWithDadata ? 'с DaData' : 'без DaData'}</b>
                        </li>
                      </ul>
                    </div>
                  )}
                </div>

                {result && result.details.length > 0 && (
                  <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6">
                    <h3 className="text-lg font-semibold text-gray-900 mb-3">Детальный отчёт</h3>
                    <div className="overflow-x-auto">
                      <table className="min-w-full text-sm">
                        <thead>
                          <tr className="text-left text-gray-500 border-b border-gray-200">
                            <th className="py-2 pr-4">Карточка</th>
                            <th className="py-2 pr-4">Legacy адрес</th>
                            <th className="py-2 pr-4">Нормализованный адрес</th>
                            <th className="py-2 pr-4">Действие</th>
                            <th className="py-2 pr-4">DaData</th>
                            <th className="py-2">Комментарий</th>
                          </tr>
                        </thead>
                        <tbody>
                          {result.details.map((item, idx) => (
                            <tr
                              key={`${item.ownerType ?? 'legal'}-${item.ownerId ?? item.legalEntityId ?? item.clientId ?? idx}-${idx}`}
                              className="border-b border-gray-100 align-top"
                            >
                              <td className="py-2 pr-4">
                                <div className="font-medium text-gray-900">{item.ownerName ?? item.legalEntityName ?? item.clientName ?? '—'}</div>
                                <div className="text-xs text-gray-500">
                                  {item.ownerType === 'client' ? 'Физ. лицо' : 'Юр. лицо'} · ID:{' '}
                                  {item.ownerId ?? item.legalEntityId ?? item.clientId ?? '—'}
                                </div>
                              </td>
                              <td className="py-2 pr-4 text-gray-700">{item.legacyAddress || '—'}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.normalizedAddress || '—'}</td>
                              <td className="py-2 pr-4">
                                <span className="inline-flex rounded-md bg-slate-100 px-2 py-1 text-xs font-medium text-slate-700">
                                  {item.action}
                                </span>
                              </td>
                              <td className="py-2 pr-4 text-gray-700">
                                {item.dadataMatched === null || item.dadataMatched === undefined
                                  ? '—'
                                  : item.dadataMatched
                                    ? 'Найдено'
                                    : 'Не найдено'}
                              </td>
                              <td className="py-2 text-gray-700">{item.message || '—'}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </div>
                )}
              </>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
