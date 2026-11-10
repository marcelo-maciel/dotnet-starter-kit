// Paged list envelope shared by the kit's paginated endpoints (PagedResponse<T> on the backend).
export type PagedResponse<T> = {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasNext: boolean;
  hasPrevious: boolean;
};
