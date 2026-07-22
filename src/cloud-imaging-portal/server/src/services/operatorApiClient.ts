import axios, { AxiosInstance } from 'axios';

/**
 * Typed HTTP client for portal backend → Operator API calls over Private Link (T041, FR-013).
 * The access token is passed through as a Bearer credential for downstream validation.
 */
export class OperatorApiClient {
  private readonly http: AxiosInstance;

  constructor(baseUrl: string) {
    this.http = axios.create({
      baseURL: baseUrl,
      timeout: 30_000,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  /** Sets the Entra Bearer token for downstream Operator API auth. */
  setToken(token: string): void {
    this.http.defaults.headers.common['Authorization'] = `Bearer ${token}`;
  }

  // ── Session operations ────────────────────────────────────────────────────

  async getSessions(filter?: string): Promise<unknown> {
    const params = filter ? { filter } : undefined;
    const { data } = await this.http.get<unknown>('/api/sessions', { params });
    return data;
  }

  async getSession(sessionId: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/sessions/${sessionId}`);
    return data;
  }

  async coupleSession(passcode: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/sessions/couple', { passcode });
    return data;
  }

  async assignSession(sessionId: string, osImageId: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>(`/api/sessions/${sessionId}/assign`, { osImageId });
    return data;
  }

  async bulkAssign(sessionIds: string[], osImageId: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/sessions/bulk-assign', { sessionIds, osImageId });
    return data;
  }

  // ── OS image operations ────────────────────────────────────────────────────

  async getImages(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/images');
    return data;
  }

  async createImage(payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/images', payload);
    return data;
  }

  async updateImage(imageId: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.patch<unknown>(`/api/images/${imageId}`, payload);
    return data;
  }

  async deleteImage(imageId: string): Promise<void> {
    await this.http.delete<unknown>(`/api/images/${imageId}`);
  }

  // ── Configuration ─────────────────────────────────────────────────────────

  async getConfiguration(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/configuration');
    return data;
  }

  async putConfiguration(payload: unknown): Promise<void> {
    await this.http.put<unknown>('/api/configuration', payload);
  }
}

/** Singleton instance — created on first import, configured at request time. */
export const operatorApiClient = new OperatorApiClient(
  process.env['OPERATOR_API_BASE_URL'] ?? 'http://localhost:7072',
);
