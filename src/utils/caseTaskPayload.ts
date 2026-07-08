import type { ClientCase } from '../types';
import { fromTimeZoneDateTimeInput, toTimeZoneDateKey } from './timeZoneDate';
import type { TaskJournalPriority, TaskJournalUpsertPayload } from './taskJournalStore';

const ALLOWED_TASK_PRIORITIES = new Set<TaskJournalPriority>(['low', 'normal', 'high', 'urgent']);

export interface CaseStageTaskTemplate {
  key: string;
  stageLabel: string;
  title: string;
  description: string;
  priority: TaskJournalPriority;
  plannedDurationMinutes: number;
  tags: string[];
}

const DEFAULT_STAGE_TASK_TEMPLATES: CaseStageTaskTemplate[] = [
  {
    key: 'current-stage-plan',
    stageLabel: 'Текущий этап',
    title: 'Составить план работ по текущему этапу',
    description: 'Проверьте текущий этап дела, зафиксируйте ближайшие действия, сроки и ответственных.',
    priority: 'normal',
    plannedDurationMinutes: 60,
    tags: ['дело', 'этап'],
  },
  {
    key: 'documents-check',
    stageLabel: 'Документы',
    title: 'Проверить комплект документов по делу',
    description: 'Сверьте документы в карточке дела, отметьте недостающие материалы и создайте дополнительные задачи при необходимости.',
    priority: 'normal',
    plannedDurationMinutes: 60,
    tags: ['дело', 'документы'],
  },
];

const STAGE_TASK_TEMPLATES: Array<CaseStageTaskTemplate & { aliases: string[] }> = [
  {
    key: 'pretrial-position',
    stageLabel: 'Внесудебное',
    title: 'Проверить досудебную позицию и доказательства',
    description: 'Оцените внесудебный сценарий, доказательства, контакт с другой стороной и необходимость претензии или переговоров.',
    priority: 'normal',
    plannedDurationMinutes: 90,
    tags: ['дело', 'внесудебно'],
    aliases: ['внесуд', 'досуд', 'претенз', 'переговор'],
  },
  {
    key: 'court-preparation',
    stageLabel: 'Подготовка к суду',
    title: 'Подготовить материалы для подачи в суд',
    description: 'Проверьте подсудность, госпошлину, комплект документов, доказательства и проект заявления перед подачей в суд.',
    priority: 'high',
    plannedDurationMinutes: 120,
    tags: ['дело', 'подготовка к суду'],
    aliases: ['подготов', 'подач', 'иск'],
  },
  {
    key: 'first-instance-hearing',
    stageLabel: 'Первая инстанция',
    title: 'Подготовиться к заседанию первой инстанции',
    description: 'Проверьте дату заседания, позицию, ходатайства, возражения, доказательства и список участников процесса.',
    priority: 'high',
    plannedDurationMinutes: 120,
    tags: ['дело', 'суд', 'первая инстанция'],
    aliases: ['судеб', 'перв', 'инстанц', 'заседан'],
  },
  {
    key: 'appeal-deadline',
    stageLabel: 'Апелляция',
    title: 'Проверить срок и материалы для апелляции',
    description: 'Сверьте дату решения, срок апелляции, основания жалобы, приложения и ответственного за подготовку документа.',
    priority: 'urgent',
    plannedDurationMinutes: 90,
    tags: ['дело', 'апелляция'],
    aliases: ['апелл'],
  },
  {
    key: 'cassation-deadline',
    stageLabel: 'Кассация',
    title: 'Проверить срок и материалы для кассации',
    description: 'Сверьте срок кассации, судебные акты, основания жалобы, комплект приложений и план подачи.',
    priority: 'urgent',
    plannedDurationMinutes: 90,
    tags: ['дело', 'кассация'],
    aliases: ['кассац'],
  },
  {
    key: 'enforcement-control',
    stageLabel: 'Исполнение',
    title: 'Проверить исполнительные действия по делу',
    description: 'Уточните исполнительный документ, статус направления приставам или контрагенту, ближайший контрольный срок и ожидаемый результат.',
    priority: 'high',
    plannedDurationMinutes: 60,
    tags: ['дело', 'исполнение'],
    aliases: ['исполн', 'пристав'],
  },
];

const PRETRIAL_STAGE_ALIASES = ['внесуд', 'досуд', 'претенз', 'переговор'];

function truncate(value: string, maxLength: number) {
  return value.length > maxLength ? value.slice(0, maxLength - 1).trimEnd() : value;
}

function resolveTaskPriority(value?: string | null): TaskJournalPriority {
  return ALLOWED_TASK_PRIORITIES.has(value as TaskJournalPriority) ? value as TaskJournalPriority : 'normal';
}

function addDaysToDateKey(value: string, days: number) {
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day + days));
  return [
    date.getUTCFullYear(),
    String(date.getUTCMonth() + 1).padStart(2, '0'),
    String(date.getUTCDate()).padStart(2, '0'),
  ].join('-');
}

function buildDefaultDueAt(timeZoneId?: string | null) {
  const todayKey = toTimeZoneDateKey(new Date(), timeZoneId);
  const tomorrowKey = todayKey ? addDaysToDateKey(todayKey, 1) : addDaysToDateKey(new Date().toISOString().slice(0, 10), 1);
  return fromTimeZoneDateTimeInput(`${tomorrowKey}T10:00`, timeZoneId);
}

function normalizeStageText(value?: string | null) {
  return (value ?? '').trim().toLocaleLowerCase('ru');
}

function stripStageTemplateAliases({ aliases: _aliases, ...template }: CaseStageTaskTemplate & { aliases: string[] }) {
  return template;
}

export function isPretrialCaseStage(caseItem: Pick<ClientCase, 'stage' | 'caseType'>): boolean {
  const haystack = [
    normalizeStageText(caseItem.stage),
    normalizeStageText(caseItem.caseType),
  ].filter(Boolean).join(' ');

  return PRETRIAL_STAGE_ALIASES.some((alias) => haystack.includes(alias));
}

export function getCaseStageTaskTemplates(caseItem: ClientCase): CaseStageTaskTemplate[] {
  const haystack = [
    normalizeStageText(caseItem.stage),
    normalizeStageText(caseItem.caseType),
  ].filter(Boolean).join(' ');

  if (isPretrialCaseStage(caseItem)) {
    const pretrialTemplate = STAGE_TASK_TEMPLATES.find((template) => template.key === 'pretrial-position');
    return pretrialTemplate ? [stripStageTemplateAliases(pretrialTemplate)] : DEFAULT_STAGE_TASK_TEMPLATES;
  }

  const matched = STAGE_TASK_TEMPLATES
    .filter((template) => template.aliases.some((alias) => haystack.includes(alias)))
    .map(stripStageTemplateAliases);

  if (matched.length > 0) {
    return matched;
  }

  return DEFAULT_STAGE_TASK_TEMPLATES;
}

export function buildCaseTaskPayload(
  caseItem: ClientCase,
  timeZoneId?: string | null,
  template?: CaseStageTaskTemplate | null,
): TaskJournalUpsertPayload {
  const tags = template?.tags?.length ? template.tags : ['дело'];
  const title = template?.title ?? `Задача по делу: ${caseItem.title}`;
  const description = template
    ? `${template.description}\n\nСоздано из карточки дела «${caseItem.title}». Этап: ${caseItem.stage?.trim() || 'не указан'}.`
    : `Создано из карточки дела «${caseItem.title}».`;

  return {
    clientId: caseItem.clientId ?? null,
    legalEntityId: caseItem.legalEntityId ?? null,
    isInternal: false,
    clientCaseId: caseItem.id,
    objectName: caseItem.title,
    title: truncate(title, 200),
    description,
    assigneeUserId: caseItem.responsibleUserId ?? null,
    assigneeName: '',
    dueAt: buildDefaultDueAt(timeZoneId),
    approvalDueAt: null,
    plannedDurationMinutes: template?.plannedDurationMinutes ?? 60,
    contractorPaymentEnabled: false,
    contractorPaymentAmount: null,
    contractorPaymentDueDate: null,
    contractorPaymentOnResult: false,
    type: 'task',
    priority: resolveTaskPriority(template?.priority ?? caseItem.priority),
    status: 'new',
    result: '',
    reminders: ['atDue'],
    channels: ['inApp'],
    tags,
    participants: [],
  };
}
