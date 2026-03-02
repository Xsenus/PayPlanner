import { useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  Building2,
  Check,
  Globe,
  Landmark,
  Mail,
  Phone,
  Plus,
  X,
  AlertCircle,
  Loader2,
} from 'lucide-react';

import { useLegalEntities } from '../../hooks/useLegalEntities';
import type { LegalEntityInput } from '../../types';

type ProfileTab = 'general' | 'settings' | 'edo';
type FinanceTab = 'bank' | 'cash' | 'cards';
type RequisiteTab = 'base' | 'english' | 'tax' | 'treasury';

const profileTabs: Array<{ id: ProfileTab; label: string }> = [
  { id: 'general', label: 'Общие' },
  { id: 'settings', label: 'Настройки' },
  { id: 'edo', label: 'ЭДО' },
];

const financeTabs: Array<{ id: FinanceTab; label: string }> = [
  { id: 'bank', label: 'Банковские счета' },
  { id: 'cash', label: 'Кассы' },
  { id: 'cards', label: 'Карты' },
];

const requisiteTabs: Array<{ id: RequisiteTab; label: string }> = [
  { id: 'base', label: 'Реквизиты' },
  { id: 'english', label: 'Реквизиты на английском' },
  { id: 'tax', label: 'Реквизиты налоговой' },
  { id: 'treasury', label: 'Реквизиты ЕНС' },
];

function SectionCard({ title, children }: { title?: string; children: ReactNode }) {
  return (
    <section className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
      {title ? <h2 className="text-base font-semibold text-slate-900">{title}</h2> : null}
      <div className={title ? 'mt-4' : ''}>{children}</div>
    </section>
  );
}

function Field({
  label,
  placeholder,
  icon,
  required,
  value,
  onChange,
  disabled,
}: {
  label: string;
  placeholder?: string;
  icon?: ReactNode;
  required?: boolean;
  value?: string;
  onChange?: (value: string) => void;
  disabled?: boolean;
}) {
  return (
    <label className="block text-sm text-slate-600">
      <span className="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-slate-400">
        {icon}
        {label}
        {required ? <span className="text-rose-500">*</span> : null}
      </span>
      <input
        type="text"
        placeholder={placeholder}
        value={value ?? ''}
        onChange={(e) => onChange?.(e.target.value)}
        disabled={disabled}
        className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100"
      />
    </label>
  );
}

function SelectField({
  label,
  options,
  icon,
  required,
  value,
  onChange,
  disabled,
}: {
  label: string;
  options: string[];
  icon?: ReactNode;
  required?: boolean;
  value?: string;
  onChange?: (value: string) => void;
  disabled?: boolean;
}) {
  return (
    <label className="block text-sm text-slate-600">
      <span className="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-slate-400">
        {icon}
        {label}
        {required ? <span className="text-rose-500">*</span> : null}
      </span>
      <select
        value={value ?? ''}
        onChange={(e) => onChange?.(e.target.value)}
        disabled={disabled}
        className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100">
        {!value && <option value="">Не выбрано</option>}
        {options.map((option) => (
          <option key={option} value={option}>{option}</option>
        ))}
      </select>
    </label>
  );
}

function CheckboxField({ label }: { label: string }) {
  return (
    <label className="flex items-center gap-2 text-sm text-slate-600">
      <input type="checkbox" className="h-4 w-4 rounded border-slate-300 text-blue-600" />
      {label}
    </label>
  );
}

function RadioField({
  name,
  label,
  defaultChecked,
}: {
  name: string;
  label: string;
  defaultChecked?: boolean;
}) {
  return (
    <label className="flex items-center gap-2 text-sm text-slate-600">
      <input type="radio" name={name} defaultChecked={defaultChecked} className="h-4 w-4 text-blue-600" />
      {label}
    </label>
  );
}

function StatItem({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">{label}</div>
      <div className="mt-2 text-sm font-semibold text-slate-900">{value}</div>
    </div>
  );
}

function UploadCard({
  title,
  description,
  accent,
}: {
  title: string;
  description: string;
  accent?: boolean;
}) {
  return (
    <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
      <div className="text-sm font-semibold text-slate-700">{title}</div>
      <div
        className={[
          'mt-4 rounded-xl border-2 border-dashed px-4 py-6 text-center text-sm',
          accent ? 'border-blue-500 text-blue-600' : 'border-slate-200 text-slate-400',
        ].join(' ')}>
        <div className="font-semibold">Выберите файлы</div>
        <div className="text-xs text-slate-400">или перетащите сюда.</div>
      </div>
      <div className="mt-4 text-xs text-slate-400">{description}</div>
    </div>
  );
}

function ModalShell({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 px-4 py-8">
      <div className="w-full max-w-3xl rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-slate-200 px-6 py-4">
          <h3 className="text-lg font-semibold text-slate-900">{title}</h3>
          <button
            type="button"
            onClick={onClose}
            className="rounded-full border border-slate-200 p-2 text-slate-400 hover:text-slate-600">
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="px-6 py-5">{children}</div>
      </div>
    </div>
  );
}

export function CompanyProfile() {
  const [activeTab, setActiveTab] = useState<ProfileTab>('general');
  const [financeTab, setFinanceTab] = useState<FinanceTab>('bank');
  const [requisiteTab, setRequisiteTab] = useState<RequisiteTab>('base');
  const [bankModalOpen, setBankModalOpen] = useState(false);
  const [cashModalOpen, setCashModalOpen] = useState(false);
  const [cardModalOpen, setCardModalOpen] = useState(false);
  const {
    legalEntities,
    loading: legalEntitiesLoading,
    error: legalEntitiesError,
    getLegalEntity,
    updateLegalEntity,
  } = useLegalEntities();
  const [profileLegalEntityId, setProfileLegalEntityId] = useState<number | null>(null);
  const [legalEntityForm, setLegalEntityForm] = useState<LegalEntityInput>({ shortName: '', clientIds: [] });
  const [profileSaving, setProfileSaving] = useState(false);
  const [profileMessage, setProfileMessage] = useState<string | null>(null);

  const sanitizeEmail = (value?: string | null): string | null => {
    const normalized = value?.trim();
    if (!normalized) {
      return null;
    }

    const emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
    return emailPattern.test(normalized) ? normalized : null;
  };

  useEffect(() => {
    if (legalEntities.length === 0) return;
    if (profileLegalEntityId !== null) return;
    setProfileLegalEntityId(legalEntities[0].id);
  }, [legalEntities, profileLegalEntityId]);

  useEffect(() => {
    if (profileLegalEntityId === null) return;
    void (async () => {
      try {
        const detail = await getLegalEntity(profileLegalEntityId);
        setLegalEntityForm({
          shortName: detail.shortName ?? '',
          fullName: detail.fullName ?? '',
          inn: detail.inn ?? '',
          kpp: detail.kpp ?? '',
          ogrn: detail.ogrn ?? '',
          address: detail.address ?? '',
          phone: detail.phone ?? '',
          email: detail.email ?? '',
          director: detail.director ?? '',
          country: detail.country ?? '',
          baseCurrency: detail.baseCurrency ?? '',
          serviceLanguage: detail.serviceLanguage ?? '',
          organizationForm: detail.organizationForm ?? '',
          timezone: detail.timezone ?? '',
          taxSystem: detail.taxSystem ?? '',
          notes: detail.notes ?? '',
          clientIds: (detail.clients ?? []).map((item) => item.id),
        });
        setProfileMessage(null);
      } catch (error) {
        setProfileMessage(error instanceof Error ? error.message : 'Не удалось загрузить юридическое лицо.');
      }
    })();
  }, [profileLegalEntityId, getLegalEntity]);

  const setLegalEntityField = (key: keyof LegalEntityInput, value: string) => {
    setLegalEntityForm((prev) => ({ ...prev, [key]: value }));
  };

  const handleProfileSave = async () => {
    if (profileLegalEntityId === null) {
      setProfileMessage('Для сохранения выберите юридическое лицо.');
      return;
    }
    try {
      setProfileSaving(true);
      setProfileMessage(null);
      await updateLegalEntity(profileLegalEntityId, {
        ...legalEntityForm,
        email: sanitizeEmail(legalEntityForm.email),
      });
      setProfileMessage('Профиль компании синхронизирован с юридическим лицом.');
    } catch (error) {
      setProfileMessage(error instanceof Error ? error.message : 'Не удалось сохранить профиль компании.');
    } finally {
      setProfileSaving(false);
    }
  };

  const financeActionLabel = useMemo(() => {
    switch (financeTab) {
      case 'bank':
        return 'Добавить банковский счет';
      case 'cash':
        return 'Добавить кассу';
      case 'cards':
        return 'Добавить карту';
      default:
        return 'Добавить';
    }
  }, [financeTab]);

  return (
    <div className="px-6 pb-10 pt-6">
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold text-slate-900">Профиль компании</h1>
          <p className="text-sm text-slate-500">
            Основные реквизиты, настройки документов и данные для интеграций.
          </p>
        </div>
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={() => setProfileMessage(null)}
            className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">
            Сбросить сообщение
          </button>
          <button
            type="button"
            onClick={handleProfileSave}
            disabled={profileSaving || profileLegalEntityId === null}
            className="rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-700 disabled:bg-blue-300">
            {profileSaving ? "Сохраняем..." : "Сохранить"}
          </button>
        </div>
      </div>

      <SectionCard title="Связь профиля с юридическим лицом">
        <div className="space-y-4">
          <label className="block text-sm text-slate-600">
            <span className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-400">Юридическое лицо</span>
            <select
              value={profileLegalEntityId ?? ''}
              onChange={(e) => setProfileLegalEntityId(e.target.value ? Number(e.target.value) : null)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 shadow-sm">
              <option value="">Выберите юридическое лицо</option>
              {legalEntities.map((entity) => (
                <option key={entity.id} value={entity.id}>{entity.shortName}</option>
              ))}
            </select>
          </label>

          {legalEntitiesLoading && <div className="flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 p-3 text-sm text-blue-700"><Loader2 className="h-4 w-4 animate-spin" />Загрузка юридических лиц...</div>}
          {legalEntitiesError && <div className="flex items-center gap-2 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700"><AlertCircle className="h-4 w-4" />{legalEntitiesError}</div>}
          {profileMessage && <div className="rounded-lg border border-blue-200 bg-blue-50 p-3 text-sm text-blue-700">{profileMessage}</div>}

          <div className="grid gap-4 md:grid-cols-4">
            <Field
              label="ИНН"
              value={legalEntityForm.inn ?? ''}
              onChange={(value) => setLegalEntityField('inn', value)}
              icon={<Building2 className="h-4 w-4" />}
              required
              disabled={legalEntitiesLoading || profileLegalEntityId === null}
            />
            <Field
              label="КПП"
              value={legalEntityForm.kpp ?? ''}
              onChange={(value) => setLegalEntityField('kpp', value)}
              disabled={legalEntitiesLoading || profileLegalEntityId === null}
            />
            <Field
              label="ОГРН"
              value={legalEntityForm.ogrn ?? ''}
              onChange={(value) => setLegalEntityField('ogrn', value)}
              disabled={legalEntitiesLoading || profileLegalEntityId === null}
            />
            <Field
              label="Руководитель"
              value={legalEntityForm.director ?? ''}
              onChange={(value) => setLegalEntityField('director', value)}
              disabled={legalEntitiesLoading || profileLegalEntityId === null}
            />
            <SelectField
              label="Страна"
              options={['Россия', 'Казахстан']}
              value={legalEntityForm.country ?? ''}
              onChange={(value) => setLegalEntityField('country', value)}
              icon={<Globe className="h-4 w-4" />}
              disabled={legalEntitiesLoading || profileLegalEntityId === null}
            />
            <SelectField
              label="Базовая валюта"
              options={['Рубль', 'USD', 'EUR']}
              value={legalEntityForm.baseCurrency ?? ''}
              onChange={(value) => setLegalEntityField('baseCurrency', value)}
              icon={<Landmark className="h-4 w-4" />}
              disabled={legalEntitiesLoading || profileLegalEntityId === null}
            />
            <SelectField
              label="Язык сервиса"
              options={['Русский', 'English']}
              value={legalEntityForm.serviceLanguage ?? ''}
              onChange={(value) => setLegalEntityField('serviceLanguage', value)}
              disabled={legalEntitiesLoading || profileLegalEntityId === null}
            />
          </div>
        </div>
      </SectionCard>

      <div className="mt-6 rounded-2xl border border-slate-200 bg-white shadow-sm">
        <div className="flex flex-wrap gap-4 border-b border-slate-200 px-6 pt-4">
          {profileTabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActiveTab(tab.id)}
              className={`pb-3 text-sm font-semibold transition ${
                activeTab === tab.id
                  ? 'border-b-2 border-blue-600 text-blue-600'
                  : 'text-slate-500 hover:text-slate-700'
              }`}>
              {tab.label}
            </button>
          ))}
        </div>

        <div className="p-6">
          {activeTab === 'general' && (
            <div className="space-y-6">
              <div className="grid gap-4 md:grid-cols-3">
                <Field label="Название организации" value={legalEntityForm.shortName} onChange={(value) => setLegalEntityField('shortName', value)} disabled={profileLegalEntityId === null} />
                <Field label="Полное наименование организации" value={legalEntityForm.fullName ?? ''} onChange={(value) => setLegalEntityField('fullName', value)} disabled={profileLegalEntityId === null} />
                <SelectField
                  label="Форма"
                  options={['АО', 'ООО', 'ИП']}
                  value={legalEntityForm.organizationForm ?? ''}
                  onChange={(value) => setLegalEntityField('organizationForm', value)}
                  disabled={profileLegalEntityId === null}
                />
              </div>

              <div className="grid gap-6 md:grid-cols-2">
                <div>
                  <div className="mb-4">
                    <SelectField
                      label="Система налогообложения"
                      options={['ОСНО', 'УСН Доход', 'УСН Доход-Расход', 'Патент']}
                      value={legalEntityForm.taxSystem ?? ''}
                      onChange={(value) => setLegalEntityField('taxSystem', value)}
                      disabled={profileLegalEntityId === null}
                    />
                  </div>
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">
                    Система налогообложения
                  </p>
                  <div className="mt-3 flex flex-wrap gap-4">
                    <CheckboxField label="ОСНО" />
                    <CheckboxField label="УСН" />
                  </div>
                  <div className="mt-4 grid gap-3 md:grid-cols-2">
                    <div className="rounded-xl border border-slate-200 p-3">
                      <RadioField name="usn" label="Доход" defaultChecked />
                      <div className="mt-2 flex items-center gap-2">
                        <input
                          type="text"
                          defaultValue="4"
                          className="w-16 rounded-lg border border-slate-200 px-2 py-1 text-sm"
                        />
                        <span className="text-sm text-slate-500">%</span>
                      </div>
                      <div className="mt-3">
                        <RadioField name="vat" label="22%" />
                        <RadioField name="vat" label="20%" />
                        <RadioField name="vat" label="10%" />
                        <RadioField name="vat" label="7%" />
                        <RadioField name="vat" label="5%" />
                        <RadioField name="vat" label="Без НДС" defaultChecked />
                      </div>
                    </div>
                    <div className="rounded-xl border border-slate-200 p-3">
                      <RadioField name="usn" label="Доход-Расход" />
                      <div className="mt-2 flex items-center gap-2">
                        <input
                          type="text"
                          defaultValue="15"
                          className="w-16 rounded-lg border border-slate-200 px-2 py-1 text-sm"
                        />
                        <span className="text-sm text-slate-500">%</span>
                      </div>
                      <div className="mt-4">
                        <CheckboxField label="Патент" />
                      </div>
                    </div>
                  </div>
                </div>

                <div className="grid gap-4">
                  <Field label="Телефон" value={legalEntityForm.phone ?? ''} onChange={(value) => setLegalEntityField('phone', value)} icon={<Phone className="h-4 w-4" />} disabled={profileLegalEntityId === null} />
                  <Field label="Электронная почта" value={legalEntityForm.email ?? ''} onChange={(value) => setLegalEntityField('email', value)} icon={<Mail className="h-4 w-4" />} disabled={profileLegalEntityId === null} />
                  <SelectField
                    label="Часовой пояс"
                    options={['(UTC+03:00) Москва', '(UTC+05:00) Екатеринбург']}
                    value={legalEntityForm.timezone ?? ''}
                    onChange={(value) => setLegalEntityField('timezone', value)}
                    disabled={profileLegalEntityId === null}
                  />
                </div>
              </div>
            </div>
          )}

          {activeTab === 'settings' && (
            <div className="space-y-6">
              <div className="grid gap-6 md:grid-cols-2">
                <div className="space-y-3">
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">
                    Формировать PDF с печатью и подписью
                  </p>
                  <div className="grid gap-2">
                    <CheckboxField label="Для счета" />
                    <CheckboxField label="Для акта" />
                    <CheckboxField label="Для товарной накладной" />
                    <CheckboxField label="Для счет-фактуры" />
                    <CheckboxField label="Для УПД" />
                  </div>
                </div>
                <div className="space-y-3">
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">
                    Отправлять с печатью и подписью
                  </p>
                  <div className="grid gap-2">
                    <CheckboxField label="Счет" />
                    <CheckboxField label="Акт" />
                  </div>
                </div>
              </div>

              <div className="grid gap-6 md:grid-cols-2">
                <div>
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">Шаблон СФ и УПД</p>
                  <div className="mt-3 space-y-2">
                    <RadioField name="invoiceTemplate" label="Вариант 1" defaultChecked />
                    <RadioField name="invoiceTemplate" label="Вариант 2" />
                  </div>
                </div>
                <div>
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">
                    К номеру счета доп номер
                  </p>
                  <div className="mt-3 space-y-2">
                    <RadioField name="invoiceNumbering" label="После номера счета: № 123-В" defaultChecked />
                    <RadioField name="invoiceNumbering" label="Перед номером счета: № В-123" />
                  </div>
                </div>
              </div>

              <div className="grid gap-6 md:grid-cols-2">
                <div className="space-y-3">
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">В счетах покупателей</p>
                  <CheckboxField label="Вывести кнопку ТТН" />
                  <CheckboxField label="Добавлять QR-код" />
                  <div className="flex items-center gap-2">
                    <CheckboxField label="В документах (Счет, Акт, ТТН, УПД)" />
                  </div>
                </div>
                <div className="space-y-3">
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">Отсрочка платежа</p>
                  <div className="grid gap-2 sm:grid-cols-2">
                    <Field label="Для покупателей" placeholder="10" />
                    <Field label="Для поставщиков" placeholder="10" />
                  </div>
                </div>
              </div>
            </div>
          )}

          {activeTab === 'edo' && (
            <div className="space-y-6">
              <div className="grid gap-4 md:grid-cols-2">
                <Field label="ID ЭДО" placeholder="XXX-YYYYYYYY-AA..." />
              </div>
              <div className="grid gap-4 md:grid-cols-4">
                <Field label="Почтовый индекс" placeholder="Обязательное поле" required />
                <Field label="Код региона" placeholder="Обязательное поле" required />
                <Field label="Район" placeholder="Желательное поле" />
                <Field label="Город (населенный пункт)" placeholder="Желательное поле" />
                <Field label="Населенный пункт" placeholder="Желательное поле" />
                <Field label="Улица" placeholder="Желательное поле" />
                <Field label="Дом" placeholder="Обязательное поле" required />
                <Field label="Корпус" placeholder="Желательное поле" />
                <Field label="Офис / Квартира" placeholder="Желательное поле" />
              </div>
            </div>
          )}
        </div>
      </div>

      <div className="mt-6 rounded-2xl border border-slate-200 bg-white shadow-sm">
        <div className="flex flex-wrap gap-4 border-b border-slate-200 px-6 pt-4">
          {financeTabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setFinanceTab(tab.id)}
              className={`pb-3 text-sm font-semibold transition ${
                financeTab === tab.id
                  ? 'border-b-2 border-blue-600 text-blue-600'
                  : 'text-slate-500 hover:text-slate-700'
              }`}>
              {tab.label}
            </button>
          ))}
        </div>

        <div className="p-6">
          <div className="rounded-xl border border-slate-200">
            <div className="grid grid-cols-6 gap-4 border-b border-slate-200 px-4 py-3 text-xs font-semibold uppercase tracking-wide text-slate-400">
              <span>Банк</span>
              <span>Название</span>
              <span>БИК</span>
              <span>Валюта</span>
              <span>Р/с</span>
              <span>Тип</span>
            </div>
            <div className="px-4 py-6 text-sm text-slate-500">Ничего не найдено.</div>
          </div>
          <button
            type="button"
            onClick={() => {
              if (financeTab === 'bank') setBankModalOpen(true);
              if (financeTab === 'cash') setCashModalOpen(true);
              if (financeTab === 'cards') setCardModalOpen(true);
            }}
            className="mt-4 inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-700">
            <Plus className="h-4 w-4" />
            {financeActionLabel}
          </button>
        </div>
      </div>

      <div className="mt-6 rounded-2xl border border-slate-200 bg-white shadow-sm">
        <div className="flex flex-wrap gap-4 border-b border-slate-200 px-6 pt-4">
          {requisiteTabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setRequisiteTab(tab.id)}
              className={`pb-3 text-sm font-semibold transition ${
                requisiteTab === tab.id
                  ? 'border-b-2 border-blue-600 text-blue-600'
                  : 'text-slate-500 hover:text-slate-700'
              }`}>
              {tab.label}
            </button>
          ))}
        </div>
        <div className="p-6">
          {requisiteTab === 'base' && (
            <div className="space-y-6">
              <div className="grid gap-6 md:grid-cols-5">
                <StatItem label="ИНН" value="7722011847" />
                <StatItem label="КПП" value="771501001" />
                <StatItem label="ОГРН" value="1027700538403" />
                <StatItem label="ОКВЭД" value="62.02" />
                <StatItem label="ОКПО" value="12345678" />
              </div>
              <div className="grid gap-6 md:grid-cols-2">
                <StatItem label="Юридический адрес" value="127566, г. Москва, Алтуфьевское шоссе, д 44, помещ 8" />
                <StatItem label="Фактический адрес" value="127566, г. Москва, Алтуфьевское шоссе, д 44, помещ 8" />
              </div>
              <div className="grid gap-6 md:grid-cols-5">
                <StatItem label="ОКАТО" value="45 286 555 000" />
                <StatItem label="ОКТМО" value="45382000" />
                <StatItem label="ОКОГУ" value="4210014" />
                <StatItem label="ОКФС" value="16" />
                <StatItem label="ОКОПФ" value="12247" />
              </div>
              <div className="grid gap-6 md:grid-cols-4">
                <StatItem label="ПФР" value="087-123-000000" />
                <StatItem label="ФСС" value="7712345678" />
                <StatItem label="Уставный капитал" value="0,00" />
                <StatItem label="Дата постановки на учет в налоговом органе" value="02.07.1996" />
              </div>
            </div>
          )}

          {requisiteTab === 'english' && (
            <div className="grid gap-6 md:grid-cols-3">
              <StatItem label="Название организации" value="ALAN-89 JSC" />
              <StatItem label="Полное наименование" value="Joint Stock Company ALAN-89" />
              <StatItem label="Форма" value="JSC" />
              <StatItem label="Юридический адрес" value="Altyufyevskoye highway, 44, Moscow, 127566" />
              <StatItem label="Фактический адрес" value="Altyufyevskoye highway, 44, Moscow, 127566" />
              <StatItem label="Должность" value="General Director" />
              <StatItem label="Фамилия" value="Shvydchenko" />
              <StatItem label="Имя" value="Sergey" />
              <StatItem label="Отчество" value="Nikolaevich" />
            </div>
          )}

          {requisiteTab === 'tax' && (
            <div className="grid gap-6 md:grid-cols-4">
              <StatItem label="ИНН" value="7722011847" />
              <StatItem label="КПП" value="771501001" />
              <StatItem label="ОГРН" value="1027700538403" />
              <StatItem label="ОКВЭД" value="62.02" />
              <StatItem label="ОКПО" value="12345678" />
              <StatItem label="Код налоговой" value="7701" />
              <StatItem label="Регион" value="Москва" />
              <StatItem label="ИНФНС" value="ИФНС России № 1 по г. Москве" />
              <StatItem label="Телефон налоговой" value="+7 (495) 000-00-00" />
              <StatItem label="ПФР" value="087-123-000000" />
              <StatItem label="ФСС" value="7712345678" />
              <StatItem label="ОКТМО" value="45382000" />
            </div>
          )}

          {requisiteTab === 'treasury' && (
            <div className="grid gap-6 md:grid-cols-3">
              <StatItem label="ИНН получателя" value="7727406020" />
              <StatItem label="КПП получателя" value="770701001" />
              <StatItem label="Полное наименование" value="Казначейство России (ФНС России)" />
              <StatItem label="БИК банка получателя" value="017003983" />
              <StatItem label="Банк получателя" value="Отделение Тула Банка России // УФК по Тульской области" />
              <StatItem label="Номер счета получателя" value="03100643000000018500" />
              <StatItem label="Кор. счет получателя" value="40102810445370000059" />
              <StatItem label="КБК" value="18201061201010000510" />
            </div>
          )}
        </div>
      </div>

      <div className="mt-6 grid gap-6 lg:grid-cols-3">
        <UploadCard
          title="Логотип компании"
          description="Рекомендуемый размер для загрузки: 545x185. Форматы: JPG, JPEG, PNG."
          accent
        />
        <UploadCard
          title="Печать компании"
          description="Рекомендуемый размер для загрузки: 200x200. Форматы: JPG, JPEG, PNG."
        />
        <UploadCard
          title="Подпись руководителя"
          description="Рекомендуемый размер для загрузки: 165x50. Форматы: JPG, JPEG, PNG."
        />
      </div>

      <SectionCard>
        <p className="text-sm text-slate-600">
          Не получается загрузить логотип, печать или подпись?{' '}
          <span className="text-blue-600">Посмотрите видеоинструкцию</span> или пришлите нам ваши файлы на{' '}
          <span className="text-blue-600">help@kub-24.ru</span> и мы самостоятельно загрузим их.
        </p>
      </SectionCard>

      <div className="mt-6 grid gap-6 lg:grid-cols-2">
        <SectionCard title="Руководитель (для документов)">
          <div className="grid gap-4">
            <Field label="Должность" placeholder="Генеральный директор" />
            <Field label="Фамилия" placeholder="Швыдченко" />
            <Field label="Имя" placeholder="Сергей" />
            <Field label="Отчество" placeholder="Николаевич" />
            <CheckboxField label="Нет отчества" />
          </div>
        </SectionCard>

        <SectionCard title="Главный бухгалтер (для документов)">
          <div className="flex items-center justify-between text-sm text-slate-600">
            <span>Совпадает с руководителем</span>
            <label className="inline-flex items-center gap-2 text-sm text-slate-600">
              <input type="checkbox" className="h-4 w-4 rounded border-slate-300 text-blue-600" defaultChecked />
              <span className="flex items-center gap-1 text-slate-500">
                <Check className="h-4 w-4" />
                Активно
              </span>
            </label>
          </div>
          <div className="mt-4 grid gap-4 opacity-70">
            <Field label="Фамилия" placeholder="Швыдченко" />
            <Field label="Имя" placeholder="Сергей" />
            <Field label="Отчество" placeholder="Николаевич" />
            <CheckboxField label="Нет отчества" />
          </div>
        </SectionCard>
      </div>

      <div className="mt-6 flex items-center justify-between">
        <button className="rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-700">
          Сохранить
        </button>
        <button className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">
          Отменить
        </button>
      </div>

      {bankModalOpen && (
        <ModalShell title="Добавить банковский счет" onClose={() => setBankModalOpen(false)}>
          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Счет №" required />
            <SelectField label="Валюта" options={['RUB', 'USD', 'EUR']} required />
            <Field label="БИК" />
            <Field label="Банк" />
            <Field label="Название, которое будет отображаться" />
            <Field label="Корреспондентский счет" />
            <Field label="Город" />
            <div className="space-y-2">
              <CheckboxField label="Иностранный банк" />
              <CheckboxField label="Указать начальный остаток" />
              <CheckboxField label="Основной" />
            </div>
          </div>
          <div className="mt-6 flex items-center justify-between">
            <button className="rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-700">
              Сохранить
            </button>
            <button
              type="button"
              onClick={() => setBankModalOpen(false)}
              className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">
              Отменить
            </button>
          </div>
        </ModalShell>
      )}

      {cashModalOpen && (
        <ModalShell title="Добавить кассу" onClose={() => setCashModalOpen(false)}>
          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Название, которое будет отображаться" required />
            <SelectField label="Валюта" options={['RUB', 'USD', 'EUR']} required />
            <SelectField label="Доступна" options={['Руководитель', 'Учредитель', 'ФинДиректор']} />
            <CheckboxField label="Основная" />
            <CheckboxField label="Указать начальный остаток" />
            <CheckboxField label="Для учета в бухгалтерии" />
          </div>
          <div className="mt-6 flex items-center justify-between">
            <button className="rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-700">
              Сохранить
            </button>
            <button
              type="button"
              onClick={() => setCashModalOpen(false)}
              className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">
              Отменить
            </button>
          </div>
        </ModalShell>
      )}

      {cardModalOpen && (
        <ModalShell title="Добавить карту" onClose={() => setCardModalOpen(false)}>
          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Счет или номер карты" required />
            <SelectField label="Валюта" options={['RUB', 'USD', 'EUR']} required />
            <Field label="БИК" />
            <Field label="Банк" />
            <Field label="Название, которое будет отображаться" placeholder="Карта" />
            <Field label="Корреспондентский счет" />
            <Field label="Город" />
            <SelectField
              label="Доступна"
              options={['Руководитель, Учредитель, ФинДиректор', 'Для всех']}
            />
            <CheckboxField label="Для учета в бухгалтерии" />
          </div>
          <div className="mt-6 flex items-center justify-between">
            <button className="rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-700">
              Сохранить
            </button>
            <button
              type="button"
              onClick={() => setCardModalOpen(false)}
              className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">
              Отменить
            </button>
          </div>
        </ModalShell>
      )}
    </div>
  );
}
