export type PaymentStatus = 'Pending' | 'Completed' | 'Overdue' | 'Cancelled' | 'Processing';
export type PaymentOverpaymentHandling = 'Advance' | 'NextInstallment' | 'Refund';
export type InvoiceWorkflowStatus =
  | 'Draft'
  | 'NotSigned'
  | 'RequiresClientSignature'
  | 'OnApproval'
  | 'Approved'
  | 'ApprovalRejected'
  | 'NeedsCorrection'
  | 'OnSigning'
  | 'SignedByClient'
  | 'Paid'
  | 'PartiallyPaid'
  | 'Overdue'
  | 'Cancelled';

export type AddressType = 'Registration' | 'Actual' | 'Legal' | 'Other';

export interface AddressRecord {
  id: number;
  type: number;
  value: string;
  unrestrictedValue?: string | null;
  postalCode?: string | null;
  country?: string | null;
  regionWithType?: string | null;
  areaWithType?: string | null;
  cityWithType?: string | null;
  settlementWithType?: string | null;
  streetWithType?: string | null;
  house?: string | null;
  flat?: string | null;
  fiasId?: string | null;
  kladrId?: string | null;
  geoLat?: string | null;
  geoLon?: string | null;
  isPrimary: boolean;
  clientId?: number | null;
  legalEntityId?: number | null;
  createdAt: string;
  updatedAt?: string | null;
}

export interface ClientReferenceItem {
  id: number;
  code: string;
  name: string;
}

export interface ClientPassport {
  id: number;
  clientId: number;
  series?: string | null;
  number?: string | null;
  issuedBy?: string | null;
  issuedAt?: string | null;
  departmentCode?: string | null;
  createdAt: string;
  updatedAt?: string | null;
}

export interface ClientPassportInput {
  series?: string | null;
  number?: string | null;
  issuedBy?: string | null;
  issuedAt?: string | null;
  departmentCode?: string | null;
}

export interface PowerOfAttorney {
  id: number;
  clientId: number;
  number: string;
  issuedAt: string;
  validUntil?: string | null;
  issuer?: string | null;
  notes?: string | null;
  isActive: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export interface PowerOfAttorneyInput {
  number: string;
  issuedAt: string;
  validUntil?: string | null;
  issuer?: string | null;
  notes?: string | null;
  isActive: boolean;
}

export interface LegalEntityClientLink {
  id: number;
  name: string;
  phone?: string | null;
  email?: string | null;
  isActive: boolean;
}

export interface RelatedDocumentLink {
  id: number;
  label: string;
  secondaryLabel?: string | null;
  clientId?: number | null;
  legalEntityId?: number | null;
  type?: PaymentKind | string | null;
  date?: string | null;
  amount?: number | null;
}

export interface LegalEntitySummary {
  id: number;
  shortName: string;
  fullName?: string | null;
  inn?: string | null;
  kpp?: string | null;
  ogrn?: string | null;
  address?: string | null;
  phone?: string | null;
  email?: string | null;
  director?: string | null;
  responsibleUserId?: number | null;
  responsibleUserName?: string | null;
  clientStatusId?: number | null;
  clientStatus?: ClientStatus | null;
  isClientAndSupplier?: boolean;
  country?: string | null;
  baseCurrency?: string | null;
  serviceLanguage?: string | null;
  organizationForm?: string | null;
  timezone?: string | null;
  taxSystem?: string | null;
  taxSystemMode?: string | null;
  incomeTaxRate?: string | null;
  incomeExpenseTaxRate?: string | null;
  vatWithoutVat?: boolean | null;
  okved?: string | null;
  okpo?: string | null;
  okato?: string | null;
  oktmo?: string | null;
  okogu?: string | null;
  okfs?: string | null;
  okopf?: string | null;
  pfr?: string | null;
  fss?: string | null;
  charterCapital?: string | null;
  taxRegistrationDate?: string | null;
  taxOfficeCode?: string | null;
  taxRegion?: string | null;
  ifnsName?: string | null;
  taxOfficePhone?: string | null;
  treasuryInn?: string | null;
  treasuryKpp?: string | null;
  treasuryRecipientName?: string | null;
  treasuryBankBik?: string | null;
  treasuryBankName?: string | null;
  treasuryAccountNumber?: string | null;
  treasuryCorrAccountNumber?: string | null;
  treasuryKbk?: string | null;
  bankAccountsJson?: string | null;
  cashRegistersJson?: string | null;
  cardsJson?: string | null;
  requisitesEnglishJson?: string | null;
  logoDataUrl?: string | null;
  sealDataUrl?: string | null;
  directorSignatureDataUrl?: string | null;
  documentDirectorJson?: string | null;
  chiefAccountantJson?: string | null;
  contactPhonesJson?: string | null;
  contactEmailsJson?: string | null;
  notes?: string | null;
  clientsCount: number;
  casesCount?: number;
  contractsCount?: number;
  invoicesCount?: number;
  actsCount?: number;
  paymentsCount?: number;
  relatedCases?: RelatedDocumentLink[];
  relatedContracts?: RelatedDocumentLink[];
  relatedInvoices?: RelatedDocumentLink[];
  relatedActs?: RelatedDocumentLink[];
  relatedPayments?: RelatedDocumentLink[];
  createdAt: string;
  updatedAt?: string | null;
  auditCreatedByUserId?: number | null;
  auditCreatedByUserName?: string | null;
  auditUpdatedAt?: string | null;
  auditUpdatedByUserId?: number | null;
  auditUpdatedByUserName?: string | null;
  auditIsDeleted?: boolean;
  auditDeletedAt?: string | null;
  auditDeletedByUserId?: number | null;
  auditDeletedByUserName?: string | null;
  clients?: LegalEntityClientLink[];
  addresses?: AddressRecord[];
}

export interface LegalEntityDetail extends LegalEntitySummary {
  clients: LegalEntityClientLink[];
}

export interface LegalEntitySuggestion {
  shortName: string;
  fullName?: string | null;
  inn?: string | null;
  kpp?: string | null;
  ogrn?: string | null;
  address?: string | null;
  phone?: string | null;
  email?: string | null;
  director?: string | null;
}

export interface LegalEntityDadataDetails {
  shortName: string;
  fullName?: string | null;
  inn?: string | null;
  kpp?: string | null;
  ogrn?: string | null;
  opfShort?: string | null;
  opfFull?: string | null;
  status?: string | null;
  registrationDate?: string | null;
  liquidationDate?: string | null;
  okved?: string | null;
  okvedType?: string | null;
  branchCount?: number | null;
  director?: string | null;
  directorPost?: string | null;
  address?: string | null;
  addressUnrestricted?: string | null;
  country?: string | null;
  regionWithType?: string | null;
  areaWithType?: string | null;
  cityWithType?: string | null;
  settlementWithType?: string | null;
  streetWithType?: string | null;
  house?: string | null;
  flat?: string | null;
  postalCode?: string | null;
  fiasId?: string | null;
  kladrId?: string | null;
  geoLat?: string | null;
  geoLon?: string | null;
  okato?: string | null;
  oktmo?: string | null;
  okpo?: string | null;
  okogu?: string | null;
  okfs?: string | null;
  taxSystem?: string | null;
  phones?: string[];
  emails?: string[];
}

export interface BankDirectoryItem {
  id: number;
  name: string;
  bik?: string | null;
  correspondentAccount?: string | null;
  city?: string | null;
  isForeign: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export interface LegalEntityBankAccount {
  bankId?: number | null;
  bank: string;
  name: string;
  bik: string;
  currency: string;
  accountNumber: string;
  accountType: string;
  corrAccountNumber?: string | null;
  city?: string | null;
  isForeignBank?: boolean | null;
}

export interface LegalEntityCashRegister {
  name: string;
  currency: string;
  access?: string | null;
  accountType?: string | null;
  isPrimary?: boolean | null;
  isClosed?: boolean | null;
  accountingEnabled?: boolean | null;
}

export interface LegalEntityCard {
  bankId?: number | null;
  number: string;
  bank: string;
  currency: string;
  holder: string;
  name?: string | null;
  bik?: string | null;
  corrAccountNumber?: string | null;
  city?: string | null;
  access?: string | null;
  accountingEnabled?: boolean | null;
}

export interface LegalEntityAddressInput {
  type: string | number;
  value: string;
  unrestrictedValue?: string | null;
  postalCode?: string | null;
  country?: string | null;
  regionWithType?: string | null;
  areaWithType?: string | null;
  cityWithType?: string | null;
  settlementWithType?: string | null;
  streetWithType?: string | null;
  house?: string | null;
  flat?: string | null;
  fiasId?: string | null;
  kladrId?: string | null;
  geoLat?: string | null;
  geoLon?: string | null;
  isPrimary?: boolean;
}

export interface LegalEntityInput {
  formRequirementsSection?: string | null;
  shortName: string;
  fullName?: string | null;
  inn?: string | null;
  kpp?: string | null;
  ogrn?: string | null;
  address?: string | null;
  phone?: string | null;
  email?: string | null;
  director?: string | null;
  responsibleUserId?: number | null;
  clientStatusId?: number | null;
  isClientAndSupplier?: boolean;
  country?: string | null;
  baseCurrency?: string | null;
  serviceLanguage?: string | null;
  organizationForm?: string | null;
  timezone?: string | null;
  taxSystem?: string | null;
  taxSystemMode?: string | null;
  incomeTaxRate?: string | null;
  incomeExpenseTaxRate?: string | null;
  vatWithoutVat?: boolean | null;
  okved?: string | null;
  okpo?: string | null;
  okato?: string | null;
  oktmo?: string | null;
  okogu?: string | null;
  okfs?: string | null;
  okopf?: string | null;
  pfr?: string | null;
  fss?: string | null;
  charterCapital?: string | null;
  taxRegistrationDate?: string | null;
  taxOfficeCode?: string | null;
  taxRegion?: string | null;
  ifnsName?: string | null;
  taxOfficePhone?: string | null;
  treasuryInn?: string | null;
  treasuryKpp?: string | null;
  treasuryRecipientName?: string | null;
  treasuryBankBik?: string | null;
  treasuryBankName?: string | null;
  treasuryAccountNumber?: string | null;
  treasuryCorrAccountNumber?: string | null;
  treasuryKbk?: string | null;
  bankAccountsJson?: string | null;
  cashRegistersJson?: string | null;
  cardsJson?: string | null;
  requisitesEnglishJson?: string | null;
  logoDataUrl?: string | null;
  sealDataUrl?: string | null;
  directorSignatureDataUrl?: string | null;
  logoChanged?: boolean | null;
  sealChanged?: boolean | null;
  directorSignatureChanged?: boolean | null;
  documentDirectorJson?: string | null;
  chiefAccountantJson?: string | null;
  contactPhonesJson?: string | null;
  contactEmailsJson?: string | null;
  notes?: string | null;
  addresses?: LegalEntityAddressInput[];
  clientIds: number[];
}

export interface Client {
  id: number;
  name: string;
  email: string;
  phone: string;
  company: string;
  address: string;
  notes: string;
  createdAt: string;
  auditCreatedByUserId?: number | null;
  auditCreatedByUserName?: string | null;
  auditUpdatedAt?: string | null;
  auditUpdatedByUserId?: number | null;
  auditUpdatedByUserName?: string | null;
  auditIsDeleted?: boolean;
  auditDeletedAt?: string | null;
  auditDeletedByUserId?: number | null;
  auditDeletedByUserName?: string | null;
  isActive: boolean;
  legalEntityId?: number | null;
  legalEntity?: LegalEntitySummary | null;
  responsibleUserId?: number | null;
  responsibleUserName?: string | null;
  clientStatusId?: number | null;
  clientStatus?: ClientStatus | null;
  isClientAndSupplier?: boolean;
  cases?: ClientCase[];
  contracts?: Contract[];
  casesCount?: number;
  contractsCount?: number;
  invoicesCount?: number;
  actsCount?: number;
  paymentsCount?: number;
  relatedCases?: RelatedDocumentLink[];
  relatedContracts?: RelatedDocumentLink[];
  relatedInvoices?: RelatedDocumentLink[];
  relatedActs?: RelatedDocumentLink[];
  relatedPayments?: RelatedDocumentLink[];
  addresses?: AddressRecord[];
}

export interface ClientInput {
  name: string;
  email: string;
  phone: string;
  company: string;
  address: string;
  notes: string;
  isActive: boolean;
  legalEntityId?: number | null;
  responsibleUserId?: number | null;
  clientStatusId?: number | null;
  isClientAndSupplier?: boolean;
}

export interface DadataAddressSuggestion {
  value: string;
  unrestrictedValue?: string | null;
  postalCode?: string | null;
  fiasId?: string | null;
  kladrId?: string | null;
  geoLat?: string | null;
  geoLon?: string | null;
  regionWithType?: string | null;
  areaWithType?: string | null;
  cityWithType?: string | null;
  settlementWithType?: string | null;
  streetWithType?: string | null;
  house?: string | null;
  flat?: string | null;
}

export interface CleanedAddress {
  source?: string | null;
  result: string;
  postalCode?: string | null;
  country?: string | null;
  countryIsoCode?: string | null;
  federalDistrict?: string | null;
  regionWithType?: string | null;
  areaWithType?: string | null;
  cityWithType?: string | null;
  settlementWithType?: string | null;
  streetWithType?: string | null;
  house?: string | null;
  block?: string | null;
  flat?: string | null;
  fiasId?: string | null;
  fiasLevel?: string | null;
  kladrId?: string | null;
  okato?: string | null;
  oktmo?: string | null;
  taxOffice?: string | null;
  taxOfficeLegal?: string | null;
  geoLat?: string | null;
  geoLon?: string | null;
  qc?: number | null;
  qcComplete?: number | null;
  qcHouse?: number | null;
  qcGeo?: number | null;
  unparsedParts?: string | null;
  timezone?: string | null;
}

export interface ClientCase {
  id: number;
  clientId?: number | null;
  legalEntityId?: number | null;
  client?: Client | null;
  legalEntity?: LegalEntitySummary | null;
  caseNumber?: string | null;
  caseType?: string | null;
  stage?: string | null;
  responsibleUserId?: number | null;
  responsibleUserName?: string | null;
  responsibleUser?: { id: number; fullName?: string | null; email?: string | null } | null;
  startDate?: string | null;
  dueDate?: string | null;
  feeAmount?: number;
  priority?: string | null;
  courtName?: string | null;
  courtCaseNumber?: string | null;
  courtUrl?: string | null;
  nextHearingAt?: string | null;
  claimant?: string | null;
  defendant?: string | null;
  thirdParties?: string | null;
  representatives?: string | null;
  judgeName?: string | null;
  courtroom?: string | null;
  courtSecretaryContacts?: string | null;
  claimFiledAt?: string | null;
  courtDecisionAt?: string | null;
  appealDeadlineAt?: string | null;
  cassationDeadlineAt?: string | null;
  motionDeadlineAt?: string | null;
  objectionDeadlineAt?: string | null;
  documentsSummary?: string | null;
  eventHistory?: string | null;
  strategy?: string | null;
  incomeTotal?: number;
  incomePaid?: number;
  incomeOutstanding?: number;
  incomeCount?: number;
  expenseTotal?: number;
  expensePaid?: number;
  expenseOutstanding?: number;
  expenseCount?: number;
  openTaskCount?: number;
  completedTaskCount?: number;
  overdueTaskCount?: number;
  taskProgressPercent?: number;
  lastTaskAt?: string | null;
  lastViewedAt?: string | null;
  lastViewedByUserId?: number | null;
  lastViewedByUserName?: string | null;
  lastViewedByResponsibleAt?: string | null;
  lastViewedByResponsibleUserId?: number | null;
  lastViewedByResponsibleUserName?: string | null;
  activityConfirmedAt?: string | null;
  activityConfirmedByUserId?: number | null;
  activityConfirmedByUserName?: string | null;
  inactivityRemindersEnabled?: boolean;
  isHiddenFromExecutor?: boolean;
  hiddenFromExecutorAt?: string | null;
  hiddenFromExecutorByUserId?: number | null;
  hiddenFromExecutorByUserName?: string | null;
  accessRequestedAt?: string | null;
  accessRequestedByUserId?: number | null;
  accessRequestedByUserName?: string | null;
  accessRequestReason?: string | null;
  canRevealSensitiveData?: boolean;
  title: string;
  description?: string;
  status: string;
  createdAt: string;
  auditCreatedByUserId?: number | null;
  auditCreatedByUserName?: string | null;
  auditUpdatedAt?: string | null;
  auditUpdatedByUserId?: number | null;
  auditUpdatedByUserName?: string | null;
  auditIsDeleted?: boolean;
  auditDeletedAt?: string | null;
  auditDeletedByUserId?: number | null;
  auditDeletedByUserName?: string | null;
}

export interface CaseCalendarEvent {
  id: string;
  kind: 'task' | 'case-deadline' | 'procedural-deadline' | 'court' | string;
  sourceType: string;
  sourceId?: number | null;
  title: string;
  startAt: string;
  endAt: string;
  allDay: boolean;
  clientCaseId: number;
  caseTitle: string;
  clientName?: string | null;
  legalEntityName?: string | null;
  responsibleUserId?: number | null;
  responsibleName: string;
  status: string;
  priority: string;
  deadlineType?: string | null;
  isOverdue: boolean;
  isWarning: boolean;
  courtName?: string | null;
  courtCaseNumber?: string | null;
}

export interface CaseCalendarSummary {
  from: string;
  to: string;
  totalEventCount: number;
  overdueCount: number;
  warningCount: number;
  courtEventCount: number;
  proceduralDeadlineCount: number;
  caseDeadlineCount: number;
  taskCount: number;
  nearestEvent?: CaseCalendarEvent | null;
}

export interface ClientCaseProceduralDeadline {
  id: number;
  clientCaseId: number;
  deadlineType: string;
  deadlineTypeLabel: string;
  title: string;
  description?: string | null;
  dueAt: string;
  allDay: boolean;
  status: string;
  statusLabel: string;
  warningDaysBefore: number;
  taskJournalItemId?: number | null;
  clientCaseDocumentId?: number | null;
  sourceType: string;
  sourceId?: number | null;
  notes?: string | null;
  createdAt: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
  closedAt?: string | null;
  closedByUserId?: number | null;
  closedByUserName?: string | null;
  closeReason?: string | null;
  isOverdue: boolean;
  isWarning: boolean;
}

export interface UpsertClientCaseProceduralDeadlinePayload {
  deadlineType?: string;
  title?: string;
  description?: string;
  dueAt?: string;
  allDay?: boolean;
  status?: string;
  warningDaysBefore?: number;
  taskJournalItemId?: number | null;
  clientCaseDocumentId?: number | null;
  sourceType?: string;
  sourceId?: number | null;
  notes?: string;
  closeReason?: string;
}

export interface ProceduralDeadlineCalculationPayload {
  startAt?: string;
  rule?: 'calendarMonths' | 'calendarDays' | 'workingDays' | string;
  monthsToAdd?: number;
  daysToAdd?: number;
  rollForwardToWorkingDay?: boolean;
}

export interface ProceduralDeadlineCalculationResult {
  deadlineAt?: string | null;
  rule: string;
  monthsToAdd: number;
  daysToAdd: number;
  rollForwardToWorkingDay: boolean;
  startAt?: string | null;
  rawDeadlineAt?: string | null;
  skippedNonWorkingDays?: number;
  rollForwardDays?: number;
  explanation?: string;
  calendarOverrides?: ProceduralDeadlineCalendarSettings | null;
}

export interface ProceduralDeadlineCalendarDate {
  date: string;
  note?: string | null;
}

export interface ProceduralDeadlineCalendarSettings {
  workingDates: ProceduralDeadlineCalendarDate[];
  nonWorkingDates: ProceduralDeadlineCalendarDate[];
  updatedAt?: string | null;
}

export interface ClientCaseCourtEvent {
  id: number;
  clientCaseId: number;
  eventType: string;
  eventTypeLabel: string;
  title: string;
  description?: string | null;
  scheduledAt: string;
  allDay: boolean;
  courtInstance: string;
  courtInstanceLabel: string;
  courtId?: number | null;
  courtName?: string | null;
  courtCaseNumber?: string | null;
  courtroom?: string | null;
  judgeName?: string | null;
  secretaryName?: string | null;
  movementStatus: string;
  movementStatusLabel: string;
  resultSummary?: string | null;
  sourceType: string;
  clientCaseDocumentId?: number | null;
  proceduralDeadlineId?: number | null;
  createdAt: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
  isOverdue: boolean;
}

export interface ClientCaseCourtEventHistory {
  id: number;
  clientCaseCourtEventId: number;
  clientCaseId: number;
  action: string;
  summary: string;
  occurredAt: string;
  actorUserId?: number | null;
  actorUserName?: string | null;
}

export interface UpsertClientCaseCourtEventPayload {
  eventType?: string;
  title?: string;
  description?: string;
  scheduledAt?: string;
  allDay?: boolean;
  courtInstance?: string;
  courtId?: number | null;
  courtName?: string;
  courtCaseNumber?: string;
  courtroom?: string;
  judgeName?: string;
  secretaryName?: string;
  movementStatus?: string;
  resultSummary?: string;
  sourceType?: string;
  clientCaseDocumentId?: number | null;
  proceduralDeadlineId?: number | null;
}

export interface ClientCaseCourt {
  id: number;
  name: string;
  instance: string;
  instanceLabel: string;
  code?: string | null;
  url?: string | null;
  address?: string | null;
  contacts?: string | null;
  isActive: boolean;
  sortOrder: number;
  createdAt: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
}

export interface UpsertClientCaseCourtPayload {
  name?: string;
  instance?: string;
  code?: string;
  url?: string;
  address?: string;
  contacts?: string;
  isActive?: boolean;
  sortOrder?: number;
}

export interface ClientCaseCourtInstance {
  id: number;
  clientCaseId: number;
  instance: string;
  instanceLabel: string;
  courtId?: number | null;
  courtName?: string | null;
  courtCaseNumber?: string | null;
  courtUrl?: string | null;
  judgeName?: string | null;
  courtroom?: string | null;
  secretaryName?: string | null;
  notes?: string | null;
  createdAt: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
  complaints: ClientCaseCourtComplaint[];
}

export interface UpsertClientCaseCourtInstancePayload {
  courtId?: number | null;
  courtName?: string;
  courtCaseNumber?: string;
  courtUrl?: string;
  judgeName?: string;
  courtroom?: string;
  secretaryName?: string;
  notes?: string;
}

export interface ClientCaseCourtComplaint {
  id: number;
  clientCaseId: number;
  courtInstanceId: number;
  complaintType: string;
  complaintTypeLabel: string;
  title: string;
  targetInstance: string;
  targetInstanceLabel: string;
  filedAt?: string | null;
  status: string;
  statusLabel: string;
  clientCaseDocumentId?: number | null;
  notes?: string | null;
  isArchived: boolean;
  createdAt: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
}

export interface UpsertClientCaseCourtComplaintPayload {
  complaintType?: string;
  title?: string;
  targetInstance?: string;
  filedAt?: string | null;
  status?: string;
  clientCaseDocumentId?: number | null;
  notes?: string;
}

export interface ClientCaseCourtParty {
  id: number;
  clientCaseId: number;
  relatedPartyId?: number | null;
  displayName: string;
  side: string;
  sideLabel: string;
  processRole: string;
  representativeName?: string | null;
  notes?: string | null;
  isArchived: boolean;
  createdAt: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
}

export interface UpsertClientCaseCourtPartyPayload {
  relatedPartyId?: number | null;
  displayName?: string;
  side?: string;
  processRole?: string;
  representativeName?: string;
  notes?: string;
}

export interface ClientCaseActivity {
  lastViewedAt?: string | null;
  lastViewedByUserId?: number | null;
  lastViewedByUserName?: string | null;
  lastViewedByResponsibleAt?: string | null;
  lastViewedByResponsibleUserId?: number | null;
  lastViewedByResponsibleUserName?: string | null;
  activityConfirmedAt?: string | null;
  activityConfirmedByUserId?: number | null;
  activityConfirmedByUserName?: string | null;
  inactivityRemindersEnabled: boolean;
  eventHistory?: string | null;
}

export interface ClientCaseTimelineEntry {
  id: number;
  clientCaseId: number;
  occurredAt?: string | null;
  text: string;
}

export interface ClientCaseConversationEntry {
  id: number;
  clientCaseId: number;
  occurredAt?: string | null;
  text: string;
  authorName?: string | null;
  kind: 'message' | 'comment' | string;
}

export interface ClientCaseSummary {
  id: number;
  title: string;
  caseNumber: string;
  status: string;
  statusLabel: string;
  priority: string;
  priorityLabel: string;
  stage: string;
  ownerName: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  startDate?: string | null;
  dueDate?: string | null;
  nextHearingAt?: string | null;
  openTaskCount: number;
  overdueTaskCount: number;
  completedTaskCount: number;
  taskProgressPercent: number;
  feeAmount?: number | null;
  paidAmount: number;
  debtAmount: number;
  lastTaskAt?: string | null;
  lastViewedAt?: string | null;
  lastViewedByResponsibleAt?: string | null;
  lastViewedByResponsibleUserId?: number | null;
  lastViewedByResponsibleUserName?: string | null;
}

export interface ClientCaseAccessRequest {
  clientCaseId: number;
  caseNumber: string;
  title: string;
  ownerName: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  requestedAt: string;
  requestedByUserId?: number | null;
  requestedByUserName: string;
  reason: string;
  isHiddenFromExecutor: boolean;
}

export interface ClientCaseAccessRequestResolution {
  clientCaseId: number;
  granted: boolean;
  resolvedAt: string;
  resolvedByUserName: string;
  isHiddenFromExecutor: boolean;
}

export interface ClientCaseFinanceDetail {
  caseId: number;
  caseTitle: string;
  ownerName: string;
  totals: ClientCaseFinanceTotals;
  calendar?: ClientCaseFinanceCalendar | null;
  invoices: ClientCaseFinanceInvoice[];
  payments: ClientCaseFinancePayment[];
  contracts: ClientCaseFinanceContract[];
}

export interface ClientCaseFinanceTotals {
  feeAmount: number;
  incomeTotal: number;
  incomePaid: number;
  incomeOutstanding: number;
  incomeCount: number;
  expenseTotal: number;
  expensePaid: number;
  expenseOutstanding: number;
  expenseCount: number;
  plannedMargin: number;
  paidMargin: number;
  outstandingBalance: number;
}

export interface ClientCaseFinanceCalendar {
  paymentCount: number;
  planTotal: number;
  paidTotal: number;
  outstandingTotal: number;
  overdueTotal: number;
  paidCount: number;
  plannedCount: number;
  overdueCount: number;
  cancelledCount: number;
  nextPaymentDate?: string | null;
}

export interface ClientCaseFinanceInvoice {
  id: number;
  number: string;
  date?: string | null;
  amount: number;
  paidAmount: number;
  outstandingAmount: number;
  status: PaymentStatus | string;
  workflowStatus: InvoiceWorkflowStatus | string;
  contractId?: number | null;
  contractNumber?: string | null;
}

export interface ClientCaseFinancePayment {
  id: number;
  documentKind: DocumentKind | string;
  type: PaymentKind | string;
  amount: number;
  paidAmount: number;
  outstandingAmount: number;
  status: PaymentStatus | string;
  date: string;
  plannedDate: string;
  paidDate?: string | null;
  invoiceId?: number | null;
  invoiceNumber?: string | null;
  invoiceDate?: string | null;
  contractId?: number | null;
  contractNumber?: string | null;
  paymentSourceName?: string | null;
  paymentOrderNumber?: string | null;
  paymentDocumentDate?: string | null;
  description?: string | null;
}

export interface ClientCaseFinanceContract {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  amount?: number | null;
  status: ContractStatus | string;
}

export interface ClientCaseWorkOrder {
  id?: number | null;
  clientCaseId: number;
  title: string;
  status: string;
  statusLabel: string;
  currency: string;
  notes?: string | null;
  totalAmount: number;
  createdAt?: string | null;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserId?: number | null;
  updatedByUserName?: string | null;
  approvalRequestedAt?: string | null;
  approvalRequestedByUserId?: number | null;
  approvalRequestedByUserName?: string | null;
  approvalDecisionAt?: string | null;
  approvalDecisionByUserId?: number | null;
  approvalDecisionByUserName?: string | null;
  approvalComment?: string | null;
  items: ClientCaseWorkOrderItem[];
}

export interface ClientCaseWorkOrderInvoice {
  paymentId: number;
  invoiceEntityId?: number | null;
  number: string;
  date: string;
  dueDate: string;
  amount: number;
  documentId?: number | null;
  workOrder: ClientCaseWorkOrder;
}

export interface ClientCaseWorkOrderItem {
  id: number;
  clientCaseWorkOrderId: number;
  serviceName: string;
  description?: string | null;
  executorUserId?: number | null;
  executorName?: string | null;
  dueDate?: string | null;
  quantity: number;
  unitPrice: number;
  totalAmount: number;
  sortOrder: number;
}

export interface ClientCaseCourtSyncRun {
  clientCaseId: number;
  status: 'manual-check-recorded' | 'binding-required' | 'external-sync-completed' | 'external-sync-error' | string;
  hasCourtBinding: boolean;
  message: string;
  checkedAt: string;
  checkedByUserId?: number | null;
  checkedByUserName?: string | null;
  courtName?: string | null;
  courtCaseNumber?: string | null;
  courtUrl?: string | null;
  syncedFromExternalSource?: boolean;
  updatedFields?: string[];
  createdCourtEvents?: number;
  updatedCourtEvents?: number;
  createdCourtParties?: number;
  nextHearingAt?: string | null;
}

export interface ClientCaseCourtSyncRunRequest {
  manualOnly?: boolean;
  comment?: string;
}

export interface ClientCaseCourtSyncLog {
  id: number;
  clientCaseId: number;
  status: 'manual-check-recorded' | 'binding-required' | 'external-sync-completed' | 'external-sync-error' | 'history' | string;
  message: string;
  occurredAt?: string | null;
  actorName?: string | null;
}

export interface ClientCaseCourtSyncJournalEntry {
  id: string;
  logId: number;
  clientCaseId: number;
  clientCaseTitle: string;
  caseNumber?: string | null;
  ownerName?: string | null;
  courtName?: string | null;
  courtCaseNumber?: string | null;
  courtUrl?: string | null;
  status: ClientCaseCourtSyncLog['status'];
  message: string;
  occurredAt?: string | null;
  actorName?: string | null;
}

export interface ClientCaseCourtSyncSettings {
  isEnabled: boolean;
  featureFlagEnabled: boolean;
  intervalMinutes: number;
  batchSize: number;
  minIntervalMinutes: number;
  maxIntervalMinutes: number;
  minBatchSize: number;
  maxBatchSize: number;
}

export interface ClientCaseCourtSyncProvider {
  key: string;
  label: string;
  sourceType: string;
  status: 'active' | 'manual-only' | 'planned' | string;
  isAvailable: boolean;
  featureFlagEnabled: boolean;
  supportsManualRun: boolean;
  supportsBackgroundSync: boolean;
  supportedHosts: string[];
  message: string;
}

export type ClientCaseRelatedPartySourceType = 'client' | 'externalContact' | 'legalEntity';

export interface ClientCaseRelatedParty {
  id: number;
  clientCaseId: number;
  sourceType: ClientCaseRelatedPartySourceType;
  sourceId?: number | null;
  clientId?: number | null;
  externalContactId?: number | null;
  legalEntityId?: number | null;
  displayName: string;
  role?: string | null;
  organizationName?: string | null;
  phone?: string | null;
  email?: string | null;
  notes?: string | null;
  createdAt: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  isArchived?: boolean;
  archivedAt?: string | null;
  archivedByUserId?: number | null;
  archivedByUserName?: string | null;
  archiveReason?: string | null;
}

export interface ClientCaseDocument {
  id: number;
  clientCaseId: number;
  documentKey: string;
  templateKey?: string | null;
  templateLabel?: string | null;
  sourceTaskId?: number | null;
  sourceEventId?: number | null;
  sourceWorkOrderId?: number | null;
  sourceInvoiceId?: number | null;
  sourceStage?: string | null;
  version: number;
  documentType: string;
  status: string;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedByUserId?: number | null;
  uploadedByName: string;
  uploadedAt: string;
  isLatestVersion: boolean;
}

export interface ClientCaseDocumentTemplate {
  key: string;
  label: string;
  documentType: string;
  heading: string;
  caseTypes?: string[];
  description?: string | null;
  content?: string | null;
  version?: number;
  isActive?: boolean;
  updatedAt?: string | null;
}

export interface ClientCaseDocumentTemplateToken {
  key: string;
  category: string;
  description: string;
  placeholder: string;
}

export interface RenderedCaseDocumentTemplate {
  templateKey: string;
  templateLabel: string;
  documentType: string;
  fileName: string;
  html: string;
}

export interface InvoiceClientCaseLink {
  id: number;
  clientId?: number | null;
  legalEntityId?: number | null;
  legalEntityName?: string | null;
  title: string;
  status: string;
  createdAt: string;
}

export interface DealType {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
}

export type PaymentKind = 'Income' | 'Expense';

export type MoneyOperationDirection = 'Income' | 'Expense' | 'Transfer';
export type MoneyOperationSource = 'Bank' | 'Cash' | 'Card' | 'EMoney';
export type MoneyOperationAccountingType = 'Fact' | 'Plan';
export type MoneyOperationMatchStatus = 'None' | 'ClarificationRequired' | 'Matched' | 'Ignored';
export type MoneyOperationJournalStatus = 'Posted' | 'Processing' | 'Error';
export type CashOrderStatus = 'Draft' | 'Posted' | 'Cancelled';
export type CashOrderType = 'Incoming' | 'Outgoing';

export interface MoneyAccount {
  id: number;
  name: string;
  source: MoneyOperationSource;
  currency: string;
  bankName?: string | null;
  bankAccountNumber?: string | null;
  isDefault: boolean;
  isActive: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export interface MoneyOperation {
  id: number;
  source: MoneyOperationSource;
  direction: MoneyOperationDirection;
  accountingType: MoneyOperationAccountingType;
  journalStatus?: MoneyOperationJournalStatus;
  moneyAccountId?: number | null;
  moneyAccountName?: string | null;
  moneyAccount?: MoneyAccount | null;
  accountName?: string | null;
  counterpartyName?: string | null;
  responsibleUserId?: number | null;
  responsibleName?: string | null;
  incomeTypeId?: number | null;
  categoryName?: string | null;
  amount: number;
  vatRate?: number | null;
  vatAmount?: number | null;
  currency?: string | null;
  paymentOrderNumber?: string | null;
  date: string;
  recognitionDate?: string | null;
  isAdvancePayment: boolean;
  purpose?: string | null;
  paidInvoiceNumber?: string | null;
  paidAmount?: number | null;
  projectName?: string | null;
  directionName?: string | null;
  salesPointName?: string | null;
  paymentPriority?: number | null;
  paidAccountName?: string | null;
  linkedInvoiceId?: number | null;
  linkedInvoiceNumber?: string | null;
  linkedInvoiceDate?: string | null;
  linkedContractId?: number | null;
  linkedContractNumber?: string | null;
  linkedActId?: number | null;
  linkedActNumber?: string | null;
  linkedActDate?: string | null;
  matchStatus?: MoneyOperationMatchStatus;
  matchedAt?: string | null;
  matchComment?: string | null;
  attachmentCount?: number;
  attachmentSummary?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  isReadonly?: boolean;
  relatedPaymentId?: number | null;
  cashOrderId?: number | null;
  cashOrderNumber?: string | null;
  cashOrderDate?: string | null;
  cashOrderStatus?: CashOrderStatus | null;
  canCreateCashOrder?: boolean;
  transferTargetMoneyAccountId?: number | null;
}

export interface MoneyOperationTransferRequest {
  sourceMoneyAccountId: number;
  targetMoneyAccountId: number;
  amount: number;
  currency?: string | null;
  date: string;
  recognitionDate?: string | null;
  paymentOrderNumber?: string | null;
  purpose?: string | null;
  responsibleUserId?: number | null;
}

export interface MoneyOperationTransferResponse {
  sourceOperation: MoneyOperation;
  targetOperation: MoneyOperation;
}

export interface CashOrder {
  id: number;
  number: string;
  type: CashOrderType;
  status: CashOrderStatus;
  date: string;
  amount: number;
  currency?: string | null;
  clientId?: number | null;
  clientName?: string | null;
  paymentId?: number | null;
  moneyOperationId?: number | null;
  invoiceId?: number | null;
  account?: string | null;
  payerName?: string | null;
  receiverName?: string | null;
  basis?: string | null;
  appendix?: string | null;
  comment?: string | null;
  cancelledAt?: string | null;
  cancelledReason?: string | null;
  createdAt: string;
  updatedAt?: string | null;
}

export interface NextCashOrderNumber {
  number: string;
  type: CashOrderType;
  date: string;
}

export interface CashBookDay {
  date: string;
  openingBalance: number;
  income: number;
  expense: number;
  closingBalance: number;
  incomingCount: number;
  outgoingCount: number;
}

export interface CashBookSummary {
  from: string;
  to: string;
  accountName?: string | null;
  openingBalance: number;
  income: number;
  expense: number;
  closingBalance: number;
  incomingCount: number;
  outgoingCount: number;
  days: CashBookDay[];
}

export interface CashOrderRegister {
  items: CashOrder[];
  total: number;
  page: number;
  pageSize: number;
  incomingCount: number;
  outgoingCount: number;
  draftCount: number;
  postedCount: number;
  cancelledCount: number;
  incomingAmount: number;
  outgoingAmount: number;
}

export interface CashInventory {
  id: number;
  number: string;
  date: string;
  accountName: string;
  accountingBalance: number;
  actualBalance: number;
  difference: number;
  correctionCashOrderId?: number | null;
  correctionCashOrderNumber?: string | null;
  comment?: string | null;
  createdAt: string;
  updatedAt?: string | null;
}

export interface CashInventoryCreateRequest {
  date: string;
  accountName?: string | null;
  actualBalance: number;
  comment?: string | null;
  createCorrection: boolean;
}

export interface MoneyOperationSourceSummary {
  source: MoneyOperationSource;
  startBalance: number;
  income: number;
  expense: number;
  endBalance: number;
}

export interface MoneyOperationAccountSummary {
  accountName: string;
  source: MoneyOperationSource;
  startBalance: number;
  income: number;
  expense: number;
  endBalance: number;
  incomeCount: number;
  expenseCount: number;
}

export interface MoneyOperationDailyBalance {
  date: string;
  accountName: string;
  source: MoneyOperationSource;
  openingBalance: number;
  income: number;
  expense: number;
  closingBalance: number;
  incomeCount: number;
  expenseCount: number;
  isBalanced: boolean;
}

export interface MoneyBalanceReconciliationResult {
  id: number;
  date: string;
  accountName: string;
  source: MoneyOperationSource;
  openingBalance: number;
  income: number;
  expense: number;
  closingBalance: number;
  incomeCount: number;
  expenseCount: number;
  isBalanced: boolean;
  issueCount: number;
  checkedAt: string;
  checkedByUserId?: number | null;
}

export interface MoneyBalanceReconciliationRunResult {
  checkedAt: string;
  created: number;
  updated: number;
  issueCount: number;
  items: MoneyBalanceReconciliationResult[];
}

export interface MoneyOperationInvoiceCandidate {
  invoiceId: number;
  paymentId: number;
  invoiceNumber: string;
  invoiceDate?: string | null;
  amount: number;
  paidAmount: number;
  overpaymentHandling?: PaymentOverpaymentHandling | null;
  overpaymentComment?: string | null;
  outstandingAmount: number;
  status: PaymentStatus;
  clientId?: number | null;
  clientName?: string | null;
  counterpartyName?: string | null;
  matchReason?: string | null;
  score: number;
}

export interface MoneyOperationsSummary {
  startBalance: number;
  income: number;
  expense: number;
  endBalance: number;
  incomeCount: number;
  expenseCount: number;
  bySource: MoneyOperationSourceSummary[];
}

export interface CashFlowSourceBreakdown {
  source: MoneyOperationSource;
  openingBalance: number;
  income: number;
  expense: number;
  netCashFlow: number;
  closingBalance: number;
  incomeCount: number;
  expenseCount: number;
}

export interface CashFlowAccountBreakdown extends CashFlowSourceBreakdown {
  accountName: string;
}

export interface CashFlowDay {
  date: string;
  openingBalance: number;
  income: number;
  expense: number;
  netCashFlow: number;
  closingBalance: number;
  incomeCount: number;
  expenseCount: number;
}

export interface CashFlowReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  accountName?: string | null;
  openingBalance: number;
  income: number;
  expense: number;
  netCashFlow: number;
  closingBalance: number;
  incomeCount: number;
  expenseCount: number;
  bySource: CashFlowSourceBreakdown[];
  byAccount: CashFlowAccountBreakdown[];
  days: CashFlowDay[];
}

export interface MoneyCategoryReportItem {
  categoryName: string;
  amount: number;
  count: number;
  share: number;
}

export interface MoneyCategoryReport {
  from: string;
  to: string;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  income: MoneyCategoryReportItem[];
  expense: MoneyCategoryReportItem[];
}

export interface CounterpartyReportItem {
  counterpartyName: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface CounterpartyReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  counterparties: CounterpartyReportItem[];
}

export interface ProjectReportItem {
  projectName: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface ProjectReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  projects: ProjectReportItem[];
}

export interface LegalWorkCaseStatusItem {
  status: string;
  statusLabel: string;
  count: number;
  feeTotal: number;
  overdueDeadlines: number;
}

export interface LegalWorkCaseTypeItem {
  caseType: string;
  count: number;
  feeTotal: number;
  activeTasks: number;
}

export interface LegalWorkResponsibleItem {
  responsibleUserId?: number | null;
  responsibleName: string;
  activeCases: number;
  closedCases: number;
  lostCases: number;
  openTasks: number;
  overdueTasks: number;
  onApprovalTasks: number;
  overdueDeadlines: number;
  feeTotal: number;
  paidTotal: number;
  outstandingTotal: number;
}

export interface LegalWorkFinanceItem {
  status: string;
  statusLabel: string;
  caseCount: number;
  feeTotal: number;
  paidTotal: number;
  outstandingTotal: number;
}

export interface LegalWorkDeadlineSummaryItem {
  deadlineType: string;
  deadlineLabel: string;
  totalCount: number;
  overdueCount: number;
  upcomingCount: number;
}

export interface LegalWorkCaseItem {
  caseId: number;
  caseNumber: string;
  caseTitle: string;
  caseType: string;
  clientName: string;
  status: string;
  statusLabel: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  createdAt: string;
  startDate?: string | null;
  dueDate?: string | null;
  feeAmount: number;
  paidTotal: number;
  outstandingTotal: number;
  openTasks: number;
  overdueTasks: number;
  onApprovalTasks: number;
  overdueDeadlines: number;
  upcomingDeadlines: number;
}

export interface LegalWorkDeadlineItem {
  caseId: number;
  caseNumber: string;
  caseTitle: string;
  clientName: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  deadlineType: string;
  deadlineLabel: string;
  dueAt: string;
  daysOffset: number;
}

export interface LegalWorkReport {
  from: string;
  to: string;
  statusFilter: string;
  responsibleUserIdFilter?: number | null;
  caseTypeFilter: string;
  totalCases: number;
  activeCases: number;
  closedCases: number;
  lostCases: number;
  onHoldCases: number;
  overdueDeadlineCount: number;
  upcomingDeadlineCount: number;
  openTaskCount: number;
  overdueTaskCount: number;
  onApprovalTaskCount: number;
  feeTotal: number;
  paidTotal: number;
  outstandingTotal: number;
  casesByStatus: LegalWorkCaseStatusItem[];
  casesByType: LegalWorkCaseTypeItem[];
  responsibles: LegalWorkResponsibleItem[];
  financeByStatus: LegalWorkFinanceItem[];
  deadlinesByType: LegalWorkDeadlineSummaryItem[];
  cases: LegalWorkCaseItem[];
  overdueDeadlines: LegalWorkDeadlineItem[];
  upcomingDeadlines: LegalWorkDeadlineItem[];
}

export interface CaseFinanceStatusItem {
  status: string;
  statusLabel: string;
  caseCount: number;
  feeTotal: number;
  paidTotal: number;
  outstandingTotal: number;
  collectionRate: number;
}

export interface CaseFinanceResponsibleItem {
  responsibleUserId?: number | null;
  responsibleName: string;
  caseCount: number;
  feeTotal: number;
  paidTotal: number;
  outstandingTotal: number;
  collectionRate: number;
}

export interface CaseFinanceItem {
  caseId: number;
  caseNumber: string;
  caseTitle: string;
  clientName: string;
  caseType: string;
  status: string;
  statusLabel: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  createdAt: string;
  startDate?: string | null;
  dueDate?: string | null;
  feeAmount: number;
  paidTotal: number;
  outstandingTotal: number;
  paymentRate: number;
  financeState: string;
  financeStateLabel: string;
}

export interface CaseFinanceReport {
  generatedAt: string;
  from: string;
  to: string;
  statusFilter: string;
  responsibleUserIdFilter?: number | null;
  caseTypeFilter: string;
  limit: number;
  totalCases: number;
  activeCases: number;
  closedCases: number;
  feeTotal: number;
  paidTotal: number;
  outstandingTotal: number;
  collectionRate: number;
  fullyPaidCaseCount: number;
  partiallyPaidCaseCount: number;
  unpaidCaseCount: number;
  overpaidCaseCount: number;
  byStatus: CaseFinanceStatusItem[];
  byResponsible: CaseFinanceResponsibleItem[];
  items: CaseFinanceItem[];
}

export interface CaseSuccessStatusItem {
  status: string;
  statusLabel: string;
  caseCount: number;
  share: number;
}

export interface CaseSuccessResponsibleItem {
  responsibleUserId?: number | null;
  responsibleName: string;
  totalCases: number;
  completedCases: number;
  successfulCases: number;
  lostCases: number;
  inProgressCases: number;
  successRate: number;
}

export interface CaseSuccessTypeItem {
  caseType: string;
  totalCases: number;
  completedCases: number;
  successfulCases: number;
  lostCases: number;
  successRate: number;
}

export interface CaseSuccessItem {
  caseId: number;
  caseNumber: string;
  caseTitle: string;
  clientName: string;
  caseType: string;
  status: string;
  statusLabel: string;
  outcomeCategory: string;
  outcomeLabel: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  createdAt: string;
  startDate?: string | null;
  dueDate?: string | null;
  daysInWork: number;
  isFinished: boolean;
}

export interface CaseSuccessReport {
  generatedAt: string;
  from: string;
  to: string;
  statusFilter: string;
  responsibleUserIdFilter?: number | null;
  caseTypeFilter: string;
  limit: number;
  totalCases: number;
  completedCases: number;
  successfulCases: number;
  lostCases: number;
  inProgressCases: number;
  onHoldCases: number;
  successRate: number;
  lossRate: number;
  completionRate: number;
  formulaNote: string;
  byStatus: CaseSuccessStatusItem[];
  byResponsible: CaseSuccessResponsibleItem[];
  byType: CaseSuccessTypeItem[];
  items: CaseSuccessItem[];
}

export interface CaseActivityReportItem {
  caseId: number;
  caseNumber: string;
  caseTitle: string;
  caseType: string;
  status: string;
  statusLabel: string;
  ownerId?: number | null;
  ownerType: string;
  ownerName: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  createdAt: string;
  lastActivityAt: string;
  lastTaskAt?: string | null;
  lastViewedAt?: string | null;
  activityConfirmedAt?: string | null;
  daysWithoutActivity: number;
  activeTaskCount: number;
  completedTaskCount: number;
  inactivityRemindersEnabled: boolean;
  isInactive: boolean;
  isEscalated: boolean;
  reason: string;
}

export interface CaseActivityReport {
  generatedAt: string;
  inactivityDays: number;
  escalationDays: number;
  activity: string;
  totalVisibleCases: number;
  activeCaseCount: number;
  finishedCaseCount: number;
  inactiveCaseCount: number;
  escalatedCaseCount: number;
  disabledCaseCount: number;
  withActiveTasksCount: number;
  withoutResponsibleCount: number;
  items: CaseActivityReportItem[];
}

export interface CurrentCasesStatusItem {
  status: string;
  statusLabel: string;
  count: number;
  overdueCases: number;
  openTasks: number;
}

export interface CurrentCasesResponsibleItem {
  responsibleUserId?: number | null;
  responsibleName: string;
  caseCount: number;
  overdueCases: number;
  openTasks: number;
  overdueTasks: number;
  overdueDeadlines: number;
}

export interface CurrentCaseReportItem {
  caseId: number;
  caseNumber: string;
  caseTitle: string;
  caseType: string;
  clientName: string;
  status: string;
  statusLabel: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  createdAt: string;
  startDate?: string | null;
  dueDate?: string | null;
  nextHearingAt?: string | null;
  daysInWork: number;
  daysUntilDue?: number | null;
  isOverdue: boolean;
  openTaskCount: number;
  overdueTaskCount: number;
  onApprovalTaskCount: number;
  activeDeadlineCount: number;
  overdueDeadlineCount: number;
  nextDeadlineAt?: string | null;
  nextDeadlineAllDay?: boolean;
  nextDeadlineLabel: string;
  lastActivityAt: string;
}

export interface CurrentCasesReport {
  generatedAt: string;
  asOf: string;
  statusFilter: string;
  responsibleUserIdFilter?: number | null;
  caseTypeFilter: string;
  limit: number;
  totalCurrentCases: number;
  openCases: number;
  onHoldCases: number;
  overdueCases: number;
  withoutResponsibleCount: number;
  withOpenTasksCount: number;
  withOverdueTasksCount: number;
  withOverdueDeadlinesCount: number;
  byStatus: CurrentCasesStatusItem[];
  byResponsible: CurrentCasesResponsibleItem[];
  items: CurrentCaseReportItem[];
}

export interface LawyerWorkloadItem {
  lawyerUserId?: number | null;
  lawyerName: string;
  activeCaseCount: number;
  openTaskCount: number;
  overdueTaskCount: number;
  onApprovalTaskCount: number;
  dueTodayTaskCount: number;
  dueSoonTaskCount: number;
  overdueDeadlineCount: number;
  activeDeadlineCount: number;
  plannedMinutes: number;
  plannedHours: number;
  workloadScore: number;
  isOverloaded: boolean;
  riskLabel: string;
  nearestTaskDueAt?: string | null;
  nearestDeadlineAt?: string | null;
  nearestDeadlineAllDay?: boolean;
}

export interface LawyerWorkloadReport {
  generatedAt: string;
  asOf: string;
  horizonDays: number;
  lawyerUserIdFilter?: number | null;
  limit: number;
  lawyerCount: number;
  totalActiveCases: number;
  totalOpenTasks: number;
  totalOverdueTasks: number;
  totalOverdueDeadlines: number;
  overloadedLawyerCount: number;
  totalPlannedHours: number;
  items: LawyerWorkloadItem[];
}

export interface OverdueProceduralDeadlineTypeItem {
  deadlineType: string;
  deadlineTypeLabel: string;
  count: number;
  criticalCount: number;
}

export interface OverdueProceduralDeadlineResponsibleItem {
  responsibleUserId?: number | null;
  responsibleName: string;
  count: number;
  criticalCount: number;
}

export interface OverdueProceduralDeadlineItem {
  deadlineId: number;
  caseId: number;
  caseNumber: string;
  caseTitle: string;
  clientName: string;
  responsibleUserId?: number | null;
  responsibleName: string;
  deadlineType: string;
  deadlineTypeLabel: string;
  title: string;
  description: string;
  dueAt: string;
  allDay?: boolean;
  daysOverdue: number;
  status: string;
  statusLabel: string;
  taskJournalItemId?: number | null;
  taskTitle: string;
  clientCaseDocumentId?: number | null;
  documentName: string;
  sourceType: string;
  sourceId?: number | null;
}

export interface OverdueProceduralDeadlinesReport {
  generatedAt: string;
  asOf: string;
  deadlineTypeFilter: string;
  responsibleUserIdFilter?: number | null;
  limit: number;
  totalOverdueCount: number;
  criticalOverdueCount: number;
  withoutResponsibleCount: number;
  byType: OverdueProceduralDeadlineTypeItem[];
  byResponsible: OverdueProceduralDeadlineResponsibleItem[];
  items: OverdueProceduralDeadlineItem[];
}

export interface TaskReportStatusItem {
  status: string;
  statusLabel: string;
  count: number;
  overdueCount: number;
  share: number;
}

export interface TaskReportResponsibleItem {
  assigneeUserId?: number | null;
  assigneeName: string;
  totalTasks: number;
  openTasks: number;
  closedTasks: number;
  overdueTasks: number;
  nearestDueAt?: string | null;
}

export interface TaskReportItem {
  taskId: number;
  clientId?: number | null;
  clientName: string;
  legalEntityId?: number | null;
  legalEntityName: string;
  clientCaseId?: number | null;
  clientCaseTitle: string;
  objectName: string;
  title: string;
  assigneeUserId?: number | null;
  assigneeName: string;
  authorName: string;
  dueAt: string;
  plannedDurationMinutes: number;
  type: string;
  typeLabel: string;
  priority: string;
  priorityLabel: string;
  status: string;
  statusLabel: string;
  result: string;
  isClosed: boolean;
  isOverdue: boolean;
  daysOverdue?: number | null;
  createdAt: string;
  updatedAt?: string | null;
}

export interface TaskReport {
  generatedAt: string;
  asOf: string;
  scope: 'open' | 'closed' | 'all' | string;
  assigneeUserIdFilter?: number | null;
  limit: number;
  totalTaskCount: number;
  openTaskCount: number;
  closedTaskCount: number;
  overdueTaskCount: number;
  dueTodayTaskCount: number;
  withoutAssigneeCount: number;
  byStatus: TaskReportStatusItem[];
  byResponsible: TaskReportResponsibleItem[];
  items: TaskReportItem[];
}

export interface DirectionReportItem {
  directionName: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface DirectionReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  directions: DirectionReportItem[];
}

export interface SalesPointReportItem {
  salesPointName: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface SalesPointReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  salesPoints: SalesPointReportItem[];
}

export interface VatReportItem {
  vatRate?: number | null;
  vatRateLabel: string;
  incomeBase: number;
  incomeVat: number;
  incomeGross: number;
  expenseBase: number;
  expenseVat: number;
  expenseGross: number;
  netVat: number;
  incomeCount: number;
  expenseCount: number;
}

export interface VatReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeAmountTotal: number;
  incomeVatTotal: number;
  expenseAmountTotal: number;
  expenseVatTotal: number;
  vatBalance: number;
  rates: VatReportItem[];
}

export interface PlanFactDay {
  date: string;
  planIncome: number;
  factIncome: number;
  incomeVariance: number;
  planExpense: number;
  factExpense: number;
  expenseVariance: number;
  planNetTotal: number;
  factNetTotal: number;
  netVariance: number;
}

export interface PlanFactReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  planIncome: number;
  factIncome: number;
  incomeVariance: number;
  incomeCompletionPercent: number;
  planExpense: number;
  factExpense: number;
  expenseVariance: number;
  expenseExecutionPercent: number;
  planNetTotal: number;
  factNetTotal: number;
  netVariance: number;
  days: PlanFactDay[];
}

export interface AdvanceReportItem {
  counterpartyName: string;
  incomeTotal: number;
  expenseTotal: number;
  recognizedTotal: number;
  unrecognizedTotal: number;
  incomeCount: number;
  expenseCount: number;
  recognizedCount: number;
  unrecognizedCount: number;
}

export interface AdvanceReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  recognizedTotal: number;
  unrecognizedTotal: number;
  recognizedCount: number;
  unrecognizedCount: number;
  counterparties: AdvanceReportItem[];
}

export interface PaymentPriorityReportItem {
  priority?: number | null;
  priorityLabel: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface PaymentPriorityReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  priorities: PaymentPriorityReportItem[];
}

export interface PaidAccountReportItem {
  paidAccountName: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface PaidAccountReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  accounts: PaidAccountReportItem[];
}

export interface PaidInvoiceReportItem {
  paidInvoiceNumber: string;
  income: number;
  expense: number;
  netTotal: number;
  operationTotal: number;
  paidAmountTotal: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface PaidInvoiceReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  paidAmountTotal: number;
  invoices: PaidInvoiceReportItem[];
}

export interface InvoiceStatusReportItem {
  status: InvoiceWorkflowStatus | string;
  statusLabel: string;
  invoiceCount: number;
  incomeCount: number;
  expenseCount: number;
  paidCount: number;
  overdueCount: number;
  totalAmount: number;
  paidAmount: number;
  outstandingAmount: number;
  share: number;
}

export interface InvoiceStatusReport {
  from: string;
  to: string;
  invoiceCount: number;
  incomeCount: number;
  expenseCount: number;
  paidCount: number;
  overdueCount: number;
  totalAmount: number;
  paidAmount: number;
  outstandingAmount: number;
  statuses: InvoiceStatusReportItem[];
}

export interface ActStatusReportItem {
  workflowStatus: ActWorkflowStatus | string;
  workflowStatusLabel: string;
  actCount: number;
  createdCount: number;
  transferredCount: number;
  signedCount: number;
  terminatedCount: number;
  totalAmount: number;
  signedAmount: number;
  pendingAmount: number;
  share: number;
}

export interface ActStatusReport {
  from: string;
  to: string;
  actCount: number;
  createdCount: number;
  transferredCount: number;
  signedCount: number;
  terminatedCount: number;
  totalAmount: number;
  signedAmount: number;
  pendingAmount: number;
  statuses: ActStatusReportItem[];
}

export interface ContractStatusReportItem {
  status: ContractStatus | string;
  statusLabel: string;
  contractCount: number;
  openEndedCount: number;
  expiringCount: number;
  totalAmount: number;
  share: number;
}

export interface ContractStatusReport {
  from: string;
  to: string;
  contractCount: number;
  draftCount: number;
  notSignedCount: number;
  onSigningCount: number;
  concludedCount: number;
  terminatedCount: number;
  cancelledCount: number;
  openEndedCount: number;
  totalAmount: number;
  activeAmount: number;
  closedAmount: number;
  statuses: ContractStatusReportItem[];
}

export interface PaymentOrderReportItem {
  paymentOrderNumber: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface PaymentOrderReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  orders: PaymentOrderReportItem[];
}

export interface PaymentPurposeReportItem {
  purpose: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface PaymentPurposeReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  purposes: PaymentPurposeReportItem[];
}

export interface MatchStatusReportItem {
  status: MoneyOperationMatchStatus;
  statusLabel: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  operationCount: number;
  share: number;
}

export interface MatchStatusReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  operationCount: number;
  matchedCount: number;
  needsAttentionCount: number;
  statuses: MatchStatusReportItem[];
}

export interface JournalStatusReportItem {
  status: MoneyOperationJournalStatus;
  statusLabel: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  operationCount: number;
  share: number;
}

export interface JournalStatusReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  operationCount: number;
  postedCount: number;
  processingCount: number;
  errorCount: number;
  statuses: JournalStatusReportItem[];
}

export interface CurrencyReportItem {
  currency: string;
  income: number;
  expense: number;
  netTotal: number;
  totalAmount: number;
  incomeCount: number;
  expenseCount: number;
  share: number;
}

export interface CurrencyReport {
  from: string;
  to: string;
  source?: MoneyOperationSource | null;
  incomeTotal: number;
  expenseTotal: number;
  netTotal: number;
  currencies: CurrencyReportItem[];
}

export interface CashOrderReportItem {
  status: CashOrderStatus;
  statusLabel: string;
  incomingTotal: number;
  outgoingTotal: number;
  netTotal: number;
  totalAmount: number;
  incomingCount: number;
  outgoingCount: number;
  orderCount: number;
  share: number;
}

export interface CashOrderReport {
  from: string;
  to: string;
  postedIncomingTotal: number;
  postedOutgoingTotal: number;
  postedNetTotal: number;
  orderCount: number;
  draftCount: number;
  postedCount: number;
  cancelledCount: number;
  statuses: CashOrderReportItem[];
}

export interface BankStatementImportReportItem {
  status: string;
  statusLabel: string;
  batchCount: number;
  rowsRead: number;
  createdRows: number;
  skippedRows: number;
  errorRows: number;
  rollbackRows: number;
  share: number;
}

export interface BankStatementImportReport {
  from: string;
  to: string;
  batchCount: number;
  rowsReadTotal: number;
  createdRowsTotal: number;
  skippedRowsTotal: number;
  errorRowsTotal: number;
  rollbackRowsTotal: number;
  appliedCount: number;
  appliedWithErrorsCount: number;
  rolledBackCount: number;
  rollbackBlockedCount: number;
  statuses: BankStatementImportReportItem[];
}

export interface SpreadsheetImportReportItem {
  status: string;
  statusLabel: string;
  batchCount: number;
  fileCount: number;
  totalRows: number;
  createdRows: number;
  updatedRows: number;
  skippedRows: number;
  errorRows: number;
  rollbackRows: number;
  share: number;
}

export interface SpreadsheetImportReport {
  from: string;
  to: string;
  batchCount: number;
  fileCount: number;
  totalRows: number;
  createdRows: number;
  updatedRows: number;
  skippedRows: number;
  errorRows: number;
  rollbackRows: number;
  appliedCount: number;
  appliedWithErrorsCount: number;
  rolledBackCount: number;
  rolledBackWithConflictsCount: number;
  statuses: SpreadsheetImportReportItem[];
}

export interface BankStatementImportCreatedOperation {
  id: number;
  date: string;
  direction: MoneyOperationDirection;
  amount: number;
  counterpartyName?: string | null;
  paymentOrderNumber?: string | null;
  matchStatus?: MoneyOperationMatchStatus;
}

export interface BankStatementImportResult {
  batchId?: number | null;
  rowsRead: number;
  created: number;
  skipped: number;
  errors: string[];
  operations: BankStatementImportCreatedOperation[];
}

export interface BankStatementAutoImportFileResult {
  fileName: string;
  batchId?: number | null;
  rowsRead: number;
  created: number;
  skipped: number;
  errors: string[];
}

export interface BankStatementAutoImportRunResult {
  enabled: boolean;
  folderPath?: string | null;
  fileMask: string;
  checkedAt: string;
  filesFound: number;
  filesImported: number;
  rowsRead: number;
  created: number;
  skipped: number;
  errors: string[];
  files: BankStatementAutoImportFileResult[];
}

export interface BankStatementImportBatch {
  id: number;
  fileName: string;
  status: string;
  rowsRead: number;
  createdRows: number;
  skippedRows: number;
  errorRows: number;
  rollbackRows: number;
  createdAt: string;
  rolledBackAt?: string | null;
}

export interface BankStatementImportRow {
  id: number;
  rowNumber: number;
  status: string;
  message?: string | null;
  moneyOperationId?: number | null;
  date: string;
  direction: MoneyOperationDirection;
  amount: number;
  accountName?: string | null;
  counterpartyName?: string | null;
  purpose?: string | null;
  paymentOrderNumber?: string | null;
  createdAt: string;
  rolledBackAt?: string | null;
}

export interface BankStatementImportBatchDetail extends BankStatementImportBatch {
  fileHash: string;
  fileSize: number;
  errorSummary?: string | null;
  rows: BankStatementImportRow[];
}

export interface BankStatementImportRollbackResult {
  batchId: number;
  status: string;
  deletedOperations: number;
  skippedRows: number;
  messages: string[];
}

export interface PaymentsListSummary {
  income: number;
  expense: number;
  balance: number;
  incomeCount: number;
  expenseCount: number;
  total: number;
}

export type ClientBalanceTransactionType =
  | 'Overpayment'
  | 'CreditApplied'
  | 'ManualAdjustment'
  | 'Refund'
  | 'AdvanceFromCancelledInvoice';

export type ClientBalanceDirection = 'Credit' | 'Debit';

export interface ClientBalanceSummary {
  clientId: number;
  balance: number;
}

export interface ClientBalanceTransaction {
  id: number;
  clientId: number;
  invoiceId?: number | null;
  paymentId?: number | null;
  amount: number;
  type: ClientBalanceTransactionType;
  direction: ClientBalanceDirection;
  description: string;
  createdAt: string;
  createdByUserName?: string | null;
}

export interface ApplyClientBalanceResult {
  appliedAmount: number;
  balance: number;
}

export interface IncomeType {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
  paymentType: PaymentKind;
}

export interface PaymentSource {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
}

export interface ServiceDictionaryItem {
  id: number;
  name: string;
  description: string;
  unit?: string;
  defaultPrice?: number;
  categoryId?: number | null;
  category?: ServiceCategoryDictionaryItem | null;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
}

export interface ServiceCategoryDictionaryItem {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
}

export interface ClientStatus {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
  systemKey?: 'client' | 'supplier' | 'creditor' | string | null;
}

export interface PaymentStatusEntity {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
}

export interface DocumentStatusEntity {
  id: number;
  code: DocumentStatusCode;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  useForInvoices: boolean;
  useForActs: boolean;
  useForContracts: boolean;
  sortOrder: number;
  createdAt: string;
}

export type DocumentStatusCode =
  | InvoiceWorkflowStatus
  | ActWorkflowStatus
  | ContractStatus;

export type ActStatus = 'Created' | 'Transferred' | 'Signed' | 'Terminated';
export type ActWorkflowStatus =
  | 'Draft'
  | 'NotSigned'
  | 'RequiresClientSignature'
  | 'OnApproval'
  | 'Approved'
  | 'ApprovalRejected'
  | 'NeedsCorrection'
  | 'OnSigning'
  | 'Signed'
  | 'Cancelled';
export type ActCreationMode = 'Manual' | 'Full' | 'Partial';
export type DocumentKind = 'Invoice' | 'Accrual' | 'Payment';

export interface ActLineItem {
  id?: number | null;
  invoiceLineItemId?: number | null;
  sourceKey: string;
  sourceSortOrder?: number | null;
  name: string;
  quantity: number;
  unit: string;
  price: number;
}

export interface Act {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  amount: number;
  creationMode?: ActCreationMode;
  invoiceNumber?: string | null;
  invoiceId?: number | null;
  type?: PaymentKind | null;
  contractId?: number | null;
  counterpartyInn?: string | null;
  status: ActStatus;
  workflowStatus?: ActWorkflowStatus;
  sentForSigningAt?: string | null;
  signedAt?: string | null;
  cancelledAt?: string | null;
  cancelledByUserId?: number | null;
  cancelledReason?: string | null;
  clientId?: number | null;
  clientName?: string | null;
  legalEntityId?: number | null;
  legalEntityName?: string | null;
  legalEntityInn?: string | null;
  responsibleId?: number | null;
  responsibleName?: string | null;
  responsibles?: InvoiceResponsible[];
  comment?: string | null;
  lineItems?: ActLineItem[];
  createdAt: string;
  updatedAt?: string | null;
  auditCreatedByUserId?: number | null;
  auditCreatedByUserName?: string | null;
  auditUpdatedAt?: string | null;
  auditUpdatedByUserId?: number | null;
  auditUpdatedByUserName?: string | null;
  auditIsDeleted?: boolean;
  auditDeletedAt?: string | null;
  auditDeletedByUserId?: number | null;
  auditDeletedByUserName?: string | null;
}

export interface ActInput {
  number: string;
  title?: string | null;
  date: string;
  amount: number;
  creationMode?: ActCreationMode;
  invoiceNumber?: string | null;
  invoiceId?: number | null;
  contractId?: number | null;
  counterpartyInn?: string | null;
  status: ActStatus;
  workflowStatus?: ActWorkflowStatus;
  clientId?: number | null;
  legalEntityId?: number | null;
  responsibleId?: number | null;
  comment?: string | null;
  lineItems?: Array<Pick<ActLineItem, 'sourceKey' | 'quantity'> & Partial<Pick<ActLineItem, 'name' | 'unit' | 'price'>>>;
}

export interface ActSummaryBucket {
  amount: number;
  count: number;
}

export interface ActsSummary {
  created: ActSummaryBucket;
  transferred: ActSummaryBucket;
  signed: ActSummaryBucket;
  terminated: ActSummaryBucket;
  totalAmount: number;
  totalCount: number;
}

export interface ActResponsible {
  id: number;
  fullName: string;
}

export interface ContractClient {
  id: number;
  name: string;
  company?: string | null;
  clientStatusId?: number | null;
  clientStatus?: ClientStatus | null;
  comment?: string | null;
}

export interface ContractLegalEntity {
  id: number;
  shortName: string;
  fullName?: string | null;
  inn?: string | null;
  kpp?: string | null;
  comment?: string | null;
}

export type ContractBillingMode = 'Fixed' | 'Actual';
export type ContractStatus =
  | 'Draft'
  | 'NotSigned'
  | 'RequiresClientSignature'
  | 'OnApproval'
  | 'Approved'
  | 'ApprovalRejected'
  | 'NeedsCorrection'
  | 'OnSigning'
  | 'Concluded'
  | 'Terminated'
  | 'Cancelled';

export interface Contract {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  startDate?: string | null;
  description?: string | null;
  billingMode: ContractBillingMode;
  status?: ContractStatus;
  sentForSigningAt?: string | null;
  concludedAt?: string | null;
  isOpenEnded: boolean;
  amount?: number | null;
  validUntil?: string | null;
  terminationDate?: string | null;
  terminationReason?: string | null;
  terminationTerms?: string | null;
  terminationFinancialTerms?: string | null;
  cancelledAt?: string | null;
  cancelledReason?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  auditCreatedByUserId?: number | null;
  auditCreatedByUserName?: string | null;
  auditUpdatedAt?: string | null;
  auditUpdatedByUserId?: number | null;
  auditUpdatedByUserName?: string | null;
  auditIsDeleted?: boolean;
  auditDeletedAt?: string | null;
  auditDeletedByUserId?: number | null;
  auditDeletedByUserName?: string | null;
  invoiceAmountTotal: number;
  actAmountTotal: number;
  paymentAmountTotal: number;
  clients: ContractClient[];
  legalEntities?: ContractLegalEntity[];
  actIds?: number[];
  invoiceIds?: number[];
  paymentIds?: number[];
}

export interface ContractTerminationCheck {
  hasUnpaidInvoices: boolean;
  unpaidInvoiceCount: number;
  unpaidInvoiceAmount: number;
}


export interface ContractInput {
  number: string;
  title?: string | null;
  date: string;
  startDate?: string | null;
  description?: string | null;
  billingMode: ContractBillingMode;
  status?: ContractStatus;
  isOpenEnded: boolean;
  amount?: number | null;
  validUntil?: string | null;
  terminationDate?: string | null;
  terminationReason?: string | null;
  terminationTerms?: string | null;
  terminationFinancialTerms?: string | null;
  cancelledAt?: string | null;
  cancelledReason?: string | null;
  clients: Array<{ clientId: number; comment?: string | null }>;
  legalEntities?: Array<{ legalEntityId: number; comment?: string | null }>;
  actIds?: number[];
  invoiceIds?: number[];
  paymentIds?: number[];
}

export interface Payment {
  id: number;
  date: string;
  plannedDate: string;
  amount: number;
  type: 'Income' | 'Expense';
  status: PaymentStatus;
  description: string;
  isPaid: boolean;
  paidDate?: string;
  lastPaymentDate?: string | null;
  notes: string;
  systemNotes: string;
  rescheduleCount: number;
  paidAmount: number;
  outstandingAmount: number;
  hasPartialPayment: boolean;
  delayDays?: number | null;
  createdAt: string;
  auditCreatedByUserId?: number | null;
  auditCreatedByUserName?: string | null;
  auditUpdatedAt?: string | null;
  auditUpdatedByUserId?: number | null;
  auditUpdatedByUserName?: string | null;
  auditIsDeleted?: boolean;
  auditDeletedAt?: string | null;
  auditDeletedByUserId?: number | null;
  auditDeletedByUserName?: string | null;
  clientId?: number | null;
  legalEntityId?: number | null;
  legalEntityName?: string | null;
  legalEntityInn?: string | null;
  expenseClientId?: number | null;
  expenseLegalEntityId?: number | null;
  expenseLegalEntityName?: string | null;
  expenseLegalEntityInn?: string | null;
  contractId?: number | null;
  documentKind?: DocumentKind;
  paymentOrderNumber?: string | null;
  paymentDocumentDate?: string | null;
  clientCaseId?: number | null;
  clientCaseTitle?: string | null;
  clientCaseReference?: string | null;
  counterpartyName?: string | null;
  dealTypeId?: number | null;
  incomeTypeId?: number | null;
  serviceId?: number | null;
  paymentSourceId?: number | null;
  paymentStatusId?: number | null;
  client?: Client | null;
  expenseClient?: Client | null;
  clientCase?: ClientCase | null;
  dealType?: DealType | null;
  incomeType?: IncomeType | null;
  service?: ServiceDictionaryItem | null;
  paymentSource?: PaymentSource | null;
  paymentStatusEntity?: PaymentStatusEntity;
  invoiceId?: number | null;
  account?: string | null;
  accountDate?: string | null;
  overpaymentHandling?: PaymentOverpaymentHandling | null;
  overpaymentTargetPaymentId?: number | null;
  overpaymentComment?: string | null;
}

export type PaymentPayload = Omit<
  Payment,
  | 'id'
  | 'createdAt'
  | 'auditCreatedByUserId'
  | 'auditCreatedByUserName'
  | 'auditUpdatedAt'
  | 'auditUpdatedByUserId'
  | 'auditUpdatedByUserName'
  | 'auditIsDeleted'
  | 'auditDeletedAt'
  | 'auditDeletedByUserId'
  | 'auditDeletedByUserName'
  | 'client'
  | 'expenseClient'
  | 'clientCase'
  | 'dealType'
  | 'incomeType'
  | 'service'
  | 'paymentSource'
  | 'paymentStatusEntity'
  | 'outstandingAmount'
  | 'hasPartialPayment'
  | 'delayDays'
>;

export interface TransferPaymentRemainderResponse {
  payment: Payment;
  remainderPayment: Payment;
  createdRemainder: boolean;
}

export interface RollbackPaymentRemainderTransferResponse {
  payment: Payment;
  removedRemainderPaymentId: number;
}

export interface PartialPaymentRemainderCandidate {
  payment: Payment;
  originalAmount: number;
  paidAmount: number;
  remainderAmount: number;
  daysPastDue: number;
  suggestedRemainderDate: string;
}

export interface PartialPaymentRemainderCandidatesResponse {
  items: PartialPaymentRemainderCandidate[];
  total: number;
  page: number;
  pageSize: number;
  totalOriginalAmount: number;
  totalPaidAmount: number;
  totalRemainderAmount: number;
  includeFuture: boolean;
  checkedAt: string;
}

export interface InvoiceResponsible {
  id: number;
  fullName: string;
}

export interface InvoiceActLink {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  amount: number;
  creationMode?: ActCreationMode;
  status: ActStatus;
  workflowStatus?: ActWorkflowStatus;
  clientId?: number | null;
  clientName?: string | null;
  responsibleId?: number | null;
  responsibleName?: string | null;
  comment?: string | null;
  lineItems?: ActLineItem[];
}

export interface Invoice {
  id: number;
  invoiceEntityId?: number | null;
  number: string;
  date: string;
  dueDate?: string | null;
  amount: number;
  paidAmount?: number | null;
  outstandingAmount?: number | null;
  type: 'Income' | 'Expense';
  status: PaymentStatus;
  workflowStatus?: InvoiceWorkflowStatus;
  sentForSigningAt?: string | null;
  signedAt?: string | null;
  cancelledAt?: string | null;
  cancelledByUserId?: number | null;
  cancelledReason?: string | null;
  isPaid: boolean;
  paidDate?: string | null;
  clientId?: number | null;
  legalEntityId?: number | null;
  legalEntityName?: string | null;
  legalEntityInn?: string | null;
  expenseClientId?: number | null;
  expenseLegalEntityId?: number | null;
  expenseLegalEntityName?: string | null;
  expenseLegalEntityInn?: string | null;
  contractId?: number | null;
  documentKind?: DocumentKind;
  clientName?: string | null;
  clientCompany?: string | null;
  clientStatusId?: number | null;
  clientStatus?: ClientStatus | null;
  expenseClientName?: string | null;
  expenseClientCompany?: string | null;
  expenseClientStatusId?: number | null;
  expenseClientStatus?: ClientStatus | null;
  clientCaseId?: number | null;
  clientCaseTitle?: string | null;
  clientCaseIds?: number[];
  clientCases?: InvoiceClientCaseLink[];
  clientCaseReference?: string | null;
  description?: string | null;
  isNotForAccounting?: boolean | null;
  actReference?: string | null;
  actId?: number | null;
  actNumber?: string | null;
  actTitle?: string | null;
  actStatus?: ActStatus | null;
  actWorkflowStatus?: ActWorkflowStatus | null;
  actIds?: number[];
  acts?: InvoiceActLink[];
  responsibleId?: number | null;
  responsibleName?: string | null;
  responsibles?: InvoiceResponsible[];
  counterpartyInn?: string | null;
  counterpartyName?: string | null;
  serviceId?: number | null;
  serviceName?: string | null;
  paymentStatusName?: string | null;
  lineItems?: InvoiceLineItem[];
  responsibleIds?: number[];
  createdAt: string;
  auditCreatedByUserId?: number | null;
  auditCreatedByUserName?: string | null;
  auditUpdatedAt?: string | null;
  auditUpdatedByUserId?: number | null;
  auditUpdatedByUserName?: string | null;
  auditIsDeleted?: boolean;
  auditDeletedAt?: string | null;
  auditDeletedByUserId?: number | null;
  auditDeletedByUserName?: string | null;
}

export interface InvoiceLineItem {
  id?: number | null;
  sortOrder?: number | null;
  sourceKey?: string | null;
  serviceId?: number | null;
  name: string;
  quantity: number;
  unit: string;
  price: number;
}

export interface InvoiceInput {
  number: string;
  date: string;
  dueDate?: string | null;
  amount: number;
  type: 'Income' | 'Expense';
  status: PaymentStatus;
  workflowStatus?: InvoiceWorkflowStatus;
  clientId?: number | null;
  legalEntityId?: number | null;
  expenseClientId?: number | null;
  expenseLegalEntityId?: number | null;
  contractId?: number | null;
  clientCaseId?: number | null;
  clientCaseIds?: number[];
  clientCaseReference?: string | null;
  description?: string | null;
  isNotForAccounting?: boolean | null;
  actReference?: string | null;
  actId?: number | null;
  actIds?: number[];
  counterpartyName?: string | null;
  serviceId?: number | null;
  paymentSourceId?: number | null;
  incomeTypeId?: number | null;
  dealTypeId?: number | null;
  paymentStatusEntityId?: number | null;
  paidDate?: string | null;
  lineItems?: InvoiceLineItem[];
  responsibleIds?: number[];
  replaceInstallmentPlan?: boolean;
  installmentPlan?: InstallmentPlanInput | null;
  actionReason?: string | null;
}

export type ClientRole = 'counterparty' | 'expenseClient';
export type InvoiceClientRole = ClientRole;

export interface InvoiceSummaryBucket {
  amount: number;
  count: number;
}

export interface InvoiceSummary {
  total: InvoiceSummaryBucket;
  pending: InvoiceSummaryBucket;
  paid: InvoiceSummaryBucket;
  overdue: InvoiceSummaryBucket;
  overdueAging?: {
    day0To10: number;
    day11To30: number;
    day31To60: number;
    day61To90: number;
    day91Plus: number;
    total: number;
  };
}

export interface AccountsSummaryBucket {
  amount: number;
  count: number;
}

export interface AccountsSummary {
  total: AccountsSummaryBucket;
  pending: AccountsSummaryBucket;
  paid: AccountsSummaryBucket;
  overdue: AccountsSummaryBucket;
  cancelled: AccountsSummaryBucket;
}

export interface PayablesDueBucket {
  key: string;
  label: string;
  amount: number;
  count: number;
}

export interface PayablesSupplierGroup {
  supplierId?: number | null;
  supplierName: string;
  totalOutstanding: number;
  overdueOutstanding: number;
  nearestDueDate?: string | null;
  paymentCount: number;
  caseCount: number;
}

export interface PayablesDashboard {
  totalOutstanding: number;
  overdueOutstanding: number;
  upcomingOutstanding: number;
  supplierCount: number;
  paymentCount: number;
  dueBuckets: PayablesDueBucket[];
  suppliers: PayablesSupplierGroup[];
}

export interface AccountRegistryRow {
  groupKey: string;
  accountNumber: string;
  invoice?: Invoice | null;
  payments: Payment[];
  acts: Act[];
  totalAmount: number;
  dueDate?: string | null;
  paidDate?: string | null;
  status: 'created' | 'paid' | 'partial' | 'overdue' | 'cancelled';
  type: 'Income' | 'Expense';
  isOverdue: boolean;
}

export type AccountSuggestion = {
  account: string;
  accountDate: string | null;
};

export interface ClientStats {
  clientId: number;
  clientName: string;
  totalIncome: number;
  totalExpenses: number;
  netAmount: number;
  totalPayments: number;
  paidPayments: number;
  pendingPayments: number;
  overduePayments: number;
  lastPaymentDate?: string;
  outstandingIncome: number;
  outstandingExpenses: number;
  outstandingTotal: number;
  recentPayments: Payment[];
}

export interface ClientCaseFinanceSummary {
  caseId: number;
  clientId?: number | null;
  clientName?: string | null;
  caseTitle?: string | null;
  caseStatus?: string | null;
  caseCreatedAt?: string | null;
  incomeTotal: number;
  incomePaid: number;
  incomeOutstanding: number;
  incomeCount: number;
  expenseTotal: number;
  expensePaid: number;
  expenseOutstanding: number;
  expenseCount: number;
  plannedMargin: number;
  paidMargin: number;
  outstandingBalance: number;
}

export interface ClientCaseExpenseDetail {
  paymentId: number;
  invoiceId?: number | null;
  invoiceNumber?: string | null;
  invoiceDate?: string | null;
  paymentDate: string;
  plannedDate: string;
  supplierId?: number | null;
  supplierName?: string | null;
  expenseClientId?: number | null;
  expenseClientName?: string | null;
  caseId: number;
  caseTitle?: string | null;
  lineItemId?: number | null;
  lineItemName?: string | null;
  serviceId?: number | null;
  serviceName?: string | null;
  quantity?: number | null;
  unit?: string | null;
  price?: number | null;
  amount: number;
  paidAmount: number;
  outstandingAmount: number;
  status: PaymentStatus;
  paymentOrderNumber?: string | null;
  paymentDocumentDate?: string | null;
  description?: string | null;
}

export interface MonthlyStats {
  income: number;
  expense: number;
  profit: number;
  completionRate: number;
  counts: {
    completed: number;
    pending: number;
    overdue: number;
    total: number;
  };
  completedAmount?: number;
  pendingAmount?: number;
  overdueAmount?: number;
}

export interface InstallmentRequest {
  total: number;
  downPayment: number;
  annualRate: number;
  months: number;
  startDate: string;
  roundingMode?: 'none' | 'down' | 'up';
  roundingStep?: number | null;
}

export type InstallmentRoundingMode = 'none' | 'down' | 'up';

export interface InstallmentResponse {
  overpay: number;
  toPay: number;
  items: InstallmentItem[];
  loanAmount?: number;
  baseMonthlyPayment?: number;
  roundedMonthlyPayment?: number | null;
  roundingMode?: 'none' | 'down' | 'up';
  roundingStep?: number | null;
}

export interface InstallmentItem {
  date: string;
  principal: number;
  interest: number;
  payment: number;
  balance: number;
}

export interface InstallmentPlanItemInput {
  sequence: number;
  date: string;
  principal: number;
  interest: number;
  payment: number;
  balance: number;
}

export interface InstallmentPlanInput {
  total: number;
  downPayment: number;
  annualRate: number;
  months: number;
  startDate: string;
  roundingMode?: InstallmentRoundingMode;
  roundingStep?: number | null;
  toPay: number;
  overpay: number;
  loanAmount?: number | null;
  baseMonthlyPayment?: number | null;
  roundedMonthlyPayment?: number | null;
  items: InstallmentPlanItemInput[];
}

export type SummaryStatus = 'Pending' | 'Completed' | 'Overdue';

export type PeriodKey =
  | 'today'
  | 'yesterday'
  | 'last-7d'
  | 'last-30d'
  | 'this-week'
  | 'last-week'
  | 'this-month'
  | 'last-month'
  | 'previous-month'
  | 'this-quarter'
  | 'last-quarter'
  | 'qtd'
  | 'this-year'
  | 'ytd';

export type SummaryBucket = {
  totalAmount: number;
  totalCount: number;
  collectedAmount: number;
  completedAmount: number;
  completedCount: number;
  pendingAmount: number;
  pendingCount: number;
  overdueAmount: number;
  overdueCount: number;
  remainingAmount: number;
};

export type SummaryStats = {
  from: string;
  to: string;
  clientId?: number;
  caseId?: number;
  income: SummaryBucket;
  expense: SummaryBucket;
  netCompleted: number;
  netCollected: number;
  netTotal: number;
  netRemaining: number;
};
