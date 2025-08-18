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

export interface IncomeType {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt: string;
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
  clientId?: number;
  clientCaseId?: number;
  dealTypeId?: number;
  incomeTypeId?: number;
  paymentSourceId?: number;
  paymentStatusId?: number;
  client?: Client;
  clientCase?: ClientCase;
  dealType?: DealType;
  incomeType?: IncomeType;
  paymentSource?: PaymentSource;
  paymentStatusEntity?: PaymentStatusEntity;
}

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
