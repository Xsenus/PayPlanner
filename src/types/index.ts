export type PaymentStatus = 'Pending' | 'Completed' | 'Overdue' | 'Cancelled' | 'Processing';

export type AddressType = 'Registration' | 'Actual' | 'Legal' | 'Other';

export interface AddressRecord {
  id: number;
  type: number;
  value: string;
  unrestrictedValue?: string | null;
  postalCode?: string | null;
  country?: string | null;
  regionWithType?: string | null;
  cityWithType?: string | null;
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
  createdAt: string;
  updatedAt?: string | null;
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
  cityWithType?: string | null;
  streetWithType?: string | null;
  house?: string | null;
  flat?: string | null;
  postalCode?: string | null;
  okato?: string | null;
  oktmo?: string | null;
  okpo?: string | null;
  okogu?: string | null;
  okfs?: string | null;
  taxSystem?: string | null;
  phones?: string[];
  emails?: string[];
}

export interface LegalEntityAddressInput {
  type: string | number;
  value: string;
  isPrimary?: boolean;
}

export interface LegalEntityInput {
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
  isActive: boolean;
  legalEntityId?: number | null;
  legalEntity?: LegalEntitySummary | null;
  responsibleUserId?: number | null;
  responsibleUserName?: string | null;
  clientStatusId?: number | null;
  clientStatus?: ClientStatus | null;
  cases?: ClientCase[];
  contracts?: Contract[];
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
  clientId: number;
  title: string;
  description?: string;
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
export type MoneyOperationSource = 'Bank' | 'Cash' | 'Card';
export type MoneyOperationAccountingType = 'Fact' | 'Plan';

export interface MoneyOperation {
  id: number;
  source: MoneyOperationSource;
  direction: MoneyOperationDirection;
  accountingType: MoneyOperationAccountingType;
  accountName?: string | null;
  counterpartyName?: string | null;
  categoryName?: string | null;
  amount: number;
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
  createdAt: string;
  updatedAt?: string | null;
  isReadonly?: boolean;
  relatedPaymentId?: number | null;
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
}

export interface PaymentStatusEntity {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
}

export type ActStatus = 'Created' | 'Transferred' | 'Signed' | 'Terminated';
export type DocumentKind = 'Invoice' | 'Accrual' | 'Payment';

export interface Act {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  amount: number;
  invoiceNumber?: string | null;
  invoiceId?: number | null;
  contractId?: number | null;
  counterpartyInn?: string | null;
  status: ActStatus;
  clientId?: number | null;
  clientName?: string | null;
  responsibleId?: number | null;
  responsibleName?: string | null;
  responsibles?: InvoiceResponsible[];
  comment?: string | null;
  createdAt: string;
  updatedAt?: string | null;
}

export interface ActInput {
  number: string;
  title?: string | null;
  date: string;
  amount: number;
  invoiceNumber?: string | null;
  invoiceId?: number | null;
  contractId?: number | null;
  counterpartyInn?: string | null;
  status: ActStatus;
  clientId?: number | null;
  responsibleId?: number | null;
  comment?: string | null;
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

export interface Contract {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  startDate?: string | null;
  description?: string | null;
  amount?: number | null;
  validUntil?: string | null;
  terminationDate?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  clients: ContractClient[];
  actIds?: number[];
  invoiceIds?: number[];
  paymentIds?: number[];
}


export interface ContractInput {
  number: string;
  title?: string | null;
  date: string;
  startDate?: string | null;
  description?: string | null;
  amount?: number | null;
  validUntil?: string | null;
  terminationDate?: string | null;
  clients: Array<{ clientId: number; comment?: string | null }>;
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
  clientId?: number | null;
  contractId?: number | null;
  documentKind?: DocumentKind;
  clientCaseId?: number | null;
  dealTypeId?: number | null;
  incomeTypeId?: number | null;
  paymentSourceId?: number | null;
  paymentStatusId?: number | null;
  client?: Client | null;
  clientCase?: ClientCase | null;
  dealType?: DealType | null;
  incomeType?: IncomeType | null;
  paymentSource?: PaymentSource | null;
  paymentStatusEntity?: PaymentStatusEntity;
  account?: string | null;
  accountDate?: string | null;
}

export type PaymentPayload = Omit<
  Payment,
  | 'id'
  | 'createdAt'
  | 'client'
  | 'clientCase'
  | 'dealType'
  | 'incomeType'
  | 'paymentSource'
  | 'paymentStatusEntity'
  | 'outstandingAmount'
  | 'hasPartialPayment'
  | 'delayDays'
>;

export interface InvoiceResponsible {
  id: number;
  fullName: string;
}

export interface Invoice {
  id: number;
  number: string;
  date: string;
  dueDate?: string | null;
  amount: number;
  type: 'Income' | 'Expense';
  status: PaymentStatus;
  isPaid: boolean;
  paidDate?: string | null;
  clientId?: number | null;
  contractId?: number | null;
  documentKind?: DocumentKind;
  clientName?: string | null;
  clientCompany?: string | null;
  clientStatusId?: number | null;
  clientStatus?: ClientStatus | null;
  clientCaseId?: number | null;
  clientCaseTitle?: string | null;
  description?: string | null;
  actReference?: string | null;
  actId?: number | null;
  actNumber?: string | null;
  actTitle?: string | null;
  actStatus?: ActStatus | null;
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
}

export interface InvoiceLineItem {
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
  clientId: number;
  contractId?: number | null;
  clientCaseId?: number | null;
  description?: string | null;
  actReference?: string | null;
  actId?: number | null;
  counterpartyName?: string | null;
  serviceId?: number | null;
  paymentSourceId?: number | null;
  incomeTypeId?: number | null;
  dealTypeId?: number | null;
  paymentStatusEntityId?: number | null;
  paidDate?: string | null;
  lineItems?: InvoiceLineItem[];
  responsibleIds?: number[];
}

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
