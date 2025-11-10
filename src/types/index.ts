export type PaymentStatus = 'Pending' | 'Completed' | 'Overdue' | 'Cancelled' | 'Processing';

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
  cases?: ClientCase[];
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

export interface PaymentStatusEntity {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
}

export type ActStatus = 'Created' | 'Transferred' | 'Signed' | 'Terminated';

export interface Act {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  amount: number;
  invoiceNumber?: string | null;
  counterpartyInn?: string | null;
  status: ActStatus;
  clientId?: number | null;
  clientName?: string | null;
  responsibleId?: number | null;
  responsibleName?: string | null;
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

export type PaymentTimelineEventType =
  | 'created'
  | 'partialPayment'
  | 'amountAdjusted'
  | 'rescheduled'
  | 'statusChanged'
  | 'finalized';

export interface PaymentTimelineEntry {
  timestamp: string;
  eventType: PaymentTimelineEventType;
  amountDelta?: number | null;
  effectiveDate?: string | null;
  previousDate?: string | null;
  newDate?: string | null;
  previousAmount?: number | null;
  newAmount?: number | null;
  totalPaid?: number | null;
  outstanding?: number | null;
  previousStatus?: PaymentStatus | null;
  newStatus?: PaymentStatus | null;
  comment?: string | null;
}

export interface Payment {
  id: number;
  date: string;
  amount: number;
  paidAmount: number;
  type: 'Income' | 'Expense';
  status: PaymentStatus;
  description: string;
  isPaid: boolean;
  paidDate?: string;
  lastPaymentDate?: string | null;
  originalDate?: string | null;
  notes: string;
  createdAt: string;
  clientId?: number | null;
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
  outstandingAmount: number;
  hasPartialPayment: boolean;
  timeline: PaymentTimelineEntry[];
}

export interface Invoice {
  id: number;
  number: string;
  date: string;
  dueDate?: string | null;
  amount: number;
  status: PaymentStatus;
  isPaid: boolean;
  paidDate?: string | null;
  clientId?: number | null;
  clientName?: string | null;
  clientCompany?: string | null;
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
  counterpartyInn?: string | null;
  paymentStatusName?: string | null;
  createdAt: string;
}

export interface InvoiceInput {
  number: string;
  date: string;
  dueDate?: string | null;
  amount: number;
  status: PaymentStatus;
  clientId: number;
  clientCaseId?: number | null;
  description?: string | null;
  actReference?: string | null;
  paymentSourceId?: number | null;
  incomeTypeId?: number | null;
  dealTypeId?: number | null;
  paymentStatusEntityId?: number | null;
  paidDate?: string | null;
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
  lastPaymentDate?: string;
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
}

export interface InstallmentResponse {
  overpay: number;
  toPay: number;
  items: InstallmentItem[];
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
  netTotal: number;
  netRemaining: number;
};
