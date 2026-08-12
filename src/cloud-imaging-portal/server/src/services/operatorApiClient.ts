import axios, { AxiosInstance } from 'axios';
import { getOperatorApiToken } from './operatorApiToken.js';

/**
 * Typed HTTP client for portal backend → Operator API calls over Private Link (T041, FR-013).
 * Every request is authenticated with the portal backend's OWN managed-identity token (which
 * carries the `CloudImaging.PortalAccess` service role) via a request interceptor — the user's
 * browser token is never forwarded downstream. User RBAC is enforced separately at the portal
 * edge by the roleGuard middleware.
 */
export class OperatorApiClient {
  private readonly http: AxiosInstance;

  constructor(baseUrl: string) {
    this.http = axios.create({
      baseURL: baseUrl,
      timeout: 30_000,
      headers: { 'Content-Type': 'application/json' },
    });

    this.http.interceptors.request.use(async (config) => {
      const token = await getOperatorApiToken();
      config.headers.set('Authorization', `Bearer ${token}`);
      return config;
    });
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

  // ── Boot image operations ────────────────────────────────────────────────────

  async getBootImages(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/boot-images');
    return data;
  }

  async startBootImageUpload(payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/boot-images/upload/start', payload);
    return data;
  }

  async publishBootImageUpload(token: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>(`/api/boot-images/upload/${token}/publish`, payload);
    return data;
  }

  async deleteBootImage(bootImageId: string): Promise<void> {
    await this.http.delete<unknown>(`/api/boot-images/${bootImageId}`);
  }

  // ── Configuration ─────────────────────────────────────────────────────────

  async getConfiguration(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/configuration');
    return data;
  }

  async putConfiguration(payload: unknown): Promise<void> {
    await this.http.put<unknown>('/api/configuration', payload);
  }

  /** Active boot media certificate metadata (thumbprint/validity) — never returns PFX bytes. */
  async getBootMediaCertMetadata(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/bootmedia/certificate/metadata');
    return data;
  }

  // ── Branding ────────────────────────────────────────────────────────────────

  async getBranding(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/branding');
    return data;
  }

  async putBranding(payload: unknown): Promise<void> {
    await this.http.put<unknown>('/api/branding', payload);
  }

  async getBrandingLogoSas(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/branding/logo/sas');
    return data;
  }

  async uploadBrandingLogo(payload: unknown): Promise<unknown> {
    const { data } = await this.http.put<unknown>('/api/branding/logo', payload);
    return data;
  }

  async uploadBrandingPortalLogo(payload: unknown): Promise<unknown> {
    const { data } = await this.http.put<unknown>('/api/branding/portal-logo', payload);
    return data;
  }

  /** Streams the boot image logo bytes (managed identity read, no SAS). */
  async getBrandingLogoContent(): Promise<{ data: Buffer; contentType: string }> {
    const res = await this.http.get('/api/branding/logo/content', { responseType: 'arraybuffer' });
    return {
      data: Buffer.from(res.data as ArrayBuffer),
      contentType: (res.headers['content-type'] as string | undefined) ?? 'image/png',
    };
  }

  /** Streams the portal logo bytes (managed identity read, no SAS). */
  async getBrandingPortalLogoContent(): Promise<{ data: Buffer; contentType: string }> {
    const res = await this.http.get('/api/branding/portal-logo/content', { responseType: 'arraybuffer' });
    return {
      data: Buffer.from(res.data as ArrayBuffer),
      contentType: (res.headers['content-type'] as string | undefined) ?? 'image/png',
    };
  }
}

/** Singleton instance — created on first import, configured at request time. */
export const operatorApiClient = new OperatorApiClient(
  process.env['OPERATOR_API_BASE_URL'] ?? 'http://localhost:7072',
);
