export interface BaseDictItem {
  id: number;
  name: string;
  unit?: string;
  defaultPrice?: number;
  categoryId?: number | null;
  code?: string;
  useForInvoices?: boolean;
  useForActs?: boolean;
  useForContracts?: boolean;
  sortOrder?: number;
  isActive?: boolean;
  description?: string;
  colorHex?: string;
  createdAt?: string;
}
