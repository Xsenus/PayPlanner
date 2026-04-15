export interface ClientRelativeItem {
  relation: string;
  fullName: string;
  birthYear: string;
  phone: string;
}

export const createEmptyClientRelative = (): ClientRelativeItem => ({
  relation: '',
  fullName: '',
  birthYear: '',
  phone: '',
});

export const normalizeClientRelative = (
  item?: Partial<ClientRelativeItem> | null,
): ClientRelativeItem => ({
  relation: item?.relation ?? '',
  fullName: item?.fullName ?? '',
  birthYear: item?.birthYear ?? '',
  phone: item?.phone ?? '',
});
