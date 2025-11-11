export type UserActivityStatus = 'Info' | 'Success' | 'Warning' | 'Failure';

export interface UserActivityLogItem {
  id: number;
  userId: number | null;
  userEmail: string | null;
  userFullName: string | null;
  category: string;
  action: string;
  section: string | null;
  objectType: string | null;
  objectId: string | null;
  description: string | null;
  status: UserActivityStatus;
  ipAddress: string | null;
  userAgent: string | null;
  httpMethod: string | null;
  path: string | null;
  queryString: string | null;
  httpStatusCode: number | null;
  durationMs: number | null;
  metadata: unknown;
  createdAt: string;
}

export interface UserActivityFiltersResponse {
  statuses: UserActivityStatus[];
  categories: string[];
  actions: string[];
  sections: string[];
  httpMethods: string[];
  actors: UserActivityActor[];
}

export interface UserActivityActor {
  id: number;
  fullName: string | null;
  email: string | null;
}

export interface CreateUserActivityInput {
  category: string;
  action: string;
  section?: string | null;
  objectType?: string | null;
  objectId?: string | null;
  description?: string | null;
  status?: UserActivityStatus;
  metadata?: Record<string, unknown>;
}

export interface UserActivityLogResponse {
  items: UserActivityLogItem[];
  total: number;
  page: number;
  pageSize: number;
}
