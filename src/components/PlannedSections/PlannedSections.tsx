import type { LucideIcon } from 'lucide-react';
import {
  BarChart3,
  Briefcase,
  ClipboardList,
  FileSpreadsheet,
  LayoutDashboard,
  ListChecks,
  UserRoundCog,
  UsersRound,
  WalletCards,
  Zap,
} from 'lucide-react';

type PlannedCard = {
  title: string;
  description: string;
  icon: LucideIcon;
};

type PlannedSectionProps = {
  eyebrow: string;
  title: string;
  description: string;
  notice: string;
  icon: LucideIcon;
  cards: PlannedCard[];
};

function PlannedSection({ eyebrow, title, description, notice, icon: SectionIcon, cards }: PlannedSectionProps) {
  return (
    <main className="min-h-full bg-slate-50/70 p-3 lg:p-4" aria-labelledby="planned-section-title">
      <section className="mx-auto flex max-w-7xl flex-col gap-3">
        <header className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="flex items-start gap-3">
            <div className="rounded-xl bg-blue-50 p-2.5 text-blue-700" aria-hidden="true">
              <SectionIcon className="h-6 w-6" />
            </div>
            <div className="min-w-0">
              <p className="text-xs font-semibold uppercase tracking-[0.14em] text-blue-700">{eyebrow}</p>
              <h1 id="planned-section-title" className="mt-1 text-2xl font-semibold text-slate-950">{title}</h1>
              <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-600">{description}</p>
            </div>
          </div>
        </header>

        <div className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-950" role="status">
          {notice}
        </div>

        <section aria-labelledby="planned-areas-title">
          <h2 id="planned-areas-title" className="mb-2 text-sm font-semibold text-slate-800">Запланированные блоки</h2>
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {cards.map(({ title: cardTitle, description: cardDescription, icon: CardIcon }) => (
              <article key={cardTitle} className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
                <div className="flex items-start gap-3">
                  <div className="rounded-lg bg-slate-100 p-2 text-slate-700" aria-hidden="true">
                    <CardIcon className="h-5 w-5" />
                  </div>
                  <div>
                    <h3 className="font-semibold text-slate-900">{cardTitle}</h3>
                    <p className="mt-1 text-sm leading-5 text-slate-600">{cardDescription}</p>
                  </div>
                </div>
              </article>
            ))}
          </div>
        </section>
      </section>
    </main>
  );
}

export function WorkspaceSection() {
  return (
    <PlannedSection
      eyebrow="Новый раздел"
      title="Рабочий стол"
      description="Единая стартовая страница для ежедневной работы в CRM. Состав и приоритет блоков будет уточнён вместе с рабочей структурой."
      notice="Раздел уже доступен в меню. Данные и действия появятся после согласования структуры рабочего стола."
      icon={LayoutDashboard}
      cards={[
        { title: 'Сводка дня', description: 'Ключевые события, сроки и показатели на сегодня.', icon: ClipboardList },
        { title: 'Быстрые действия', description: 'Частые операции без перехода по нескольким разделам.', icon: Zap },
        { title: 'Контроль работы', description: 'Задачи, дела и важные отклонения в одном месте.', icon: ListChecks },
      ]}
    />
  );
}

export function PayrollSection() {
  return (
    <PlannedSection
      eyebrow="Раздел для администратора"
      title="Зарплата"
      description="Отдельное рабочее пространство для штатных и внештатных сотрудников, начислений и зарплатных документов."
      notice="Расчёты пока не выполняются: правила начисления и состав отчётов будут добавлены после согласования методики."
      icon={WalletCards}
      cards={[
        { title: 'Сотрудники', description: 'Данные штатных сотрудников для расчётов.', icon: UsersRound },
        { title: 'Внештатные сотрудники', description: 'Учёт исполнителей и условий вознаграждения.', icon: UserRoundCog },
        { title: 'Начисления', description: 'Расчётные периоды, основания и итоговые суммы.', icon: Briefcase },
        { title: 'Зарплатные ведомости', description: 'Подготовка и хранение ведомостей по зарплате.', icon: FileSpreadsheet },
        { title: 'Отчёты', description: 'Обязательная отчётность и контрольные показатели.', icon: BarChart3 },
      ]}
    />
  );
}
