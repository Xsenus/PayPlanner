import { useMemo, useState, type ReactNode } from 'react';
import { Building2, Check, Globe, Landmark, Mail, Phone, Plus } from 'lucide-react';

type ProfileTab = 'general' | 'settings' | 'edo';
type FinanceTab = 'bank' | 'cash' | 'cards' | 'emoney' | 'warehouses' | 'points' | 'directions';

const profileTabs: Array<{ id: ProfileTab; label: string }> = [
  { id: 'general', label: 'Общие' },
  { id: 'settings', label: 'Настройки' },
  { id: 'edo', label: 'ЭДО' },
];

const financeTabs: Array<{ id: FinanceTab; label: string }> = [
  { id: 'bank', label: 'Банковские счета' },
  { id: 'cash', label: 'Кассы' },
  { id: 'cards', label: 'Карты' },
  { id: 'emoney', label: 'E-money' },
  { id: 'warehouses', label: 'Склады' },
  { id: 'points', label: 'Точки продаж' },
  { id: 'directions', label: 'Направления' },
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
}: {
  label: string;
  placeholder?: string;
  icon?: React.ReactNode;
  required?: boolean;
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
        className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
      />
    </label>
  );
}

function SelectField({
  label,
  options,
  icon,
  required,
}: {
  label: string;
  options: string[];
  icon?: React.ReactNode;
  required?: boolean;
}) {
  return (
    <label className="block text-sm text-slate-600">
      <span className="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-slate-400">
        {icon}
        {label}
        {required ? <span className="text-rose-500">*</span> : null}
      </span>
      <select className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200">
        {options.map((option) => (
          <option key={option}>{option}</option>
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

export function CompanyProfile() {
  const [activeTab, setActiveTab] = useState<ProfileTab>('general');
  const [financeTab, setFinanceTab] = useState<FinanceTab>('bank');

  const financeActionLabel = useMemo(() => {
    switch (financeTab) {
      case 'bank':
        return 'Добавить банковский счет';
      case 'cash':
        return 'Добавить кассу';
      case 'cards':
        return 'Добавить карту';
      case 'emoney':
        return 'Добавить E-money';
      case 'warehouses':
        return 'Добавить склад';
      case 'points':
        return 'Добавить точку продаж';
      case 'directions':
        return 'Добавить направление';
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
          <button className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">
            Отменить
          </button>
          <button className="rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-700">
            Сохранить
          </button>
        </div>
      </div>

      <SectionCard>
        <div className="grid gap-4 md:grid-cols-4">
          <Field label="ИНН" placeholder="7722011847" icon={<Building2 className="h-4 w-4" />} required />
          <SelectField label="Страна" options={['Россия', 'Казахстан']} icon={<Globe className="h-4 w-4" />} />
          <SelectField label="Базовая валюта" options={['Рубль', 'USD', 'EUR']} icon={<Landmark className="h-4 w-4" />} />
          <SelectField label="Язык сервиса" options={['Русский', 'English']} />
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
                <Field label="Название организации" placeholder='АО "АЛАН-89"' />
                <Field label="Полное наименование организации" placeholder="Акционерное общество" />
                <SelectField label="Форма" options={['АО', 'ООО', 'ИП']} />
              </div>

              <div className="grid gap-6 md:grid-cols-2">
                <div>
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
                  <Field label="Телефон" placeholder="+7 (913) 014-93-49" icon={<Phone className="h-4 w-4" />} />
                  <Field label="Электронная почта" placeholder="ilel@list.ru" icon={<Mail className="h-4 w-4" />} />
                  <SelectField
                    label="Часовой пояс"
                    options={['(UTC+03:00) Москва', '(UTC+05:00) Екатеринбург']}
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
          <button className="mt-4 inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-700">
            <Plus className="h-4 w-4" />
            {financeActionLabel}
          </button>
        </div>
      </div>

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
    </div>
  );
}
