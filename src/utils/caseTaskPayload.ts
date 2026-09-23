import type { ClientCase } from '../types';
import { applyTaskTemplateDefaults } from './taskTemplateApplication';
import { fromTimeZoneDateTimeInput, toTimeZoneDateKey } from './timeZoneDate';
import type {
  TaskJournalChannel,
  TaskJournalParticipant,
  TaskJournalPriority,
  TaskJournalReminder,
  TaskJournalUpsertPayload,
} from './taskJournalStore';

const ALLOWED_TASK_PRIORITIES = new Set<TaskJournalPriority>(['low', 'normal', 'high', 'urgent']);

export interface CaseStageTaskTemplate {
  key: string;
  stageLabel: string;
  title: string;
  description: string;
  priority: TaskJournalPriority;
  plannedDurationMinutes: number;
  tags: string[];
  assigneeUserId?: number | null;
  assigneeName?: string;
  reminders?: TaskJournalReminder[];
  channels?: TaskJournalChannel[];
  participants?: TaskJournalParticipant[];
}

export type CasePreHearingAction = 'authority' | 'travel';

const PRE_HEARING_TEMPLATES: Record<CasePreHearingAction, CaseStageTaskTemplate> = {
  authority: {
    key: 'hearing-authority',
    stageLabel: 'Подготовка к заседанию',
    title: 'Оформить доверенность для участия в заседании',
    description: 'Проверьте, кому нужны полномочия для участия в заседании, и оформите доверенность до его начала.',
    priority: 'high',
    plannedDurationMinutes: 60,
    tags: ['дело', 'суд', 'доверенность'],
  },
  travel: {
    key: 'hearing-travel',
    stageLabel: 'Подготовка к заседанию',
    title: 'Организовать поездку на судебное заседание',
    description: 'Если заседание проходит в другом городе, согласуйте командировку, дорогу и проживание до его начала.',
    priority: 'high',
    plannedDurationMinutes: 60,
    tags: ['дело', 'суд', 'командировка'],
  },
};

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

function stripStageTemplateAliases(template: CaseStageTaskTemplate & { aliases: string[] }): CaseStageTaskTemplate {
  return {
    key: template.key,
    stageLabel: template.stageLabel,
    title: template.title,
    description: template.description,
    priority: template.priority,
    plannedDurationMinutes: template.plannedDurationMinutes,
    tags: template.tags,
  };
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
  const payload: TaskJournalUpsertPayload = {
    clientId: caseItem.clientId ?? null,
    legalEntityId: caseItem.legalEntityId ?? null,
    isInternal: false,
    clientCaseId: caseItem.id,
    objectName: caseItem.title,
    title: truncate(`Задача по делу: ${caseItem.title}`, 200),
    description: `Создано из карточки дела «${caseItem.title}».`,
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
    tags: ['дело'],
    participants: [],
  };

  if (!template) {
    return payload;
  }

  const description = `${template.description}\n\nСоздано из карточки дела «${caseItem.title}». Этап: ${caseItem.stage?.trim() || 'не указан'}.`;

  return applyTaskTemplateDefaults(payload, {
    title: truncate(template.title, 200),
    description,
    assigneeUserId: template.assigneeUserId ?? undefined,
    assigneeName: template.assigneeName,
    plannedDurationMinutes: template.plannedDurationMinutes,
    type: 'task',
    priority: resolveTaskPriority(template.priority),
    reminders: template.reminders,
    channels: template.channels,
    tags: template.tags,
    participants: template.participants,
  }, {
    protectedFields: ['clientId', 'legalEntityId', 'isInternal', 'clientCaseId', 'objectName'],
    replaceFields: ['title', 'description', 'assigneeUserId', 'assigneeName', 'plannedDurationMinutes', 'type', 'priority'],
  });
}

export function buildCasePreHearingTaskPayload(
  caseItem: ClientCase,
  action: CasePreHearingAction,
  timeZoneId?: string | null,
): TaskJournalUpsertPayload {
  const hearingAt = Date.parse(caseItem.nextHearingAt ?? '');
  if (!Number.isFinite(hearingAt) || hearingAt <= Date.now() || !caseItem.responsibleUserId) {
    throw new Error('Для задачи нужны будущее заседание и ответственный по делу.');
  }

  const payload = buildCaseTaskPayload(caseItem, timeZoneId, PRE_HEARING_TEMPLATES[action]);
  const suggestedDueAt = Date.parse(payload.dueAt);
  payload.dueAt = new Date(Math.min(suggestedDueAt, hearingAt)).toISOString();
  payload.description = `${payload.description}\nЗаседание: ${new Date(hearingAt).toLocaleString('ru-RU', { timeZone: timeZoneId || 'UTC' })}. Срок задачи можно уточнить в её карточке.`;
  return payload;
}
