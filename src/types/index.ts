export type PaymentStatus = 'Pending' | 'Completed' | 'Overdue' | 'Cancelled' | 'Processing';

export interface CompanyLink {
  id: number;
  name: string;
  email: string;
  phone: string;
  role?: string;
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
  cases?: ClientCase[];
  companies?: CompanyLink[];
}

export interface ClientPayload {
  name: string;
  email: string;
  phone: string;
  company: string;
  address: string;
  notes: string;
  isActive: boolean;
  companyIds: number[];
}

export interface ClientCase {
  id: number;
  clientId: number;
  title: string;
  description?: string;
  status: string;
  createdAt: string;
}

export interface CompanyMember {
  id: number;
  name: string;
  email: string;
  phone: string;
  role?: string;
}

export interface Company {
  id: number;
  name: string;
  email: string;
  phone: string;
  address: string;
  notes: string;
  isActive: boolean;
  createdAt: string;
  members?: CompanyMember[];
}

export interface CompanyPayload {
  name: string;
  email: string;
  phone: string;
  address: string;
  notes: string;
  isActive: boolean;
  clientIds: number[];
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

export interface Payment {
  id: number;
  date: string;
  amount: number;
  type: 'Income' | 'Expense';
  status: PaymentStatus;
  description: string;
  isPaid: boolean;
  paidDate?: string;
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
