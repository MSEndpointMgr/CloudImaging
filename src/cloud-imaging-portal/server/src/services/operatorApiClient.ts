import axios, { AxiosInstance } from 'axios';
import { getOperatorApiToken } from './operatorApiToken.js';

/**
 * Default timeout for most (fast) Operator API calls. Catches a genuinely unreachable
 * backend quickly instead of hanging the request pipeline.
 */
const DEFAULT_TIMEOUT_MS = 30_000;

/**
 * Timeout for the OS / boot / recovery image upload "publish" calls specifically.
 *
 * Publish used to verify the whole staged blob's SHA-256 and copy it to its published path
 * inline, which scales with image size and could not complete in time for multi-GB OS images.
 * A long timeout here never helped, because Azure Static Web Apps caps every API request at a
 * fixed 45 seconds and returns "Backend call failure" past that, and Azure Functions HTTP is
 * severed by the load balancer at 230 seconds regardless of functionTimeout. Both caps sit in
 * front of this client, so any value above ~40s was unreachable.
 *
 * Publish now only performs a cheap file-signature check and enqueues an UploadJob, returning
 * 202 Accepted, so it completes in a couple of seconds. This timeout is a modest allowance for
 * committing the block list on a large chunked upload, well inside the edge cap.
 */
const PUBLISH_TIMEOUT_MS = 40_000;

/**
 * Typed HTTP client for portal backend → Operator API calls over Private Link (T041, FR-013).
 * Every request is authenticated with the portal backend's OWN managed-identity token (which
 * carries the `CloudImaging.PortalAccess` service role) via a request interceptor. The user's
 * browser token is never forwarded downstream. User RBAC is enforced separately at the portal
 * edge by the roleGuard middleware.
 */
export class OperatorApiClient {
  private readonly http: AxiosInstance;

  constructor(baseUrl: string) {
    this.http = axios.create({
      baseURL: baseUrl,
      timeout: DEFAULT_TIMEOUT_MS,
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
    const { data } = await this.http.get<unknown>(`/api/sessions/${encodeURIComponent(sessionId)}`);
    return data;
  }

  async coupleSession(passcode: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/sessions/couple', { passcode });
    return data;
  }

  async assignSession(sessionId: string, osImageId: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>(
      `/api/sessions/${encodeURIComponent(sessionId)}/assign`, { osImageId });
    return data;
  }

  async bulkAssign(sessionIds: string[], osImageId: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/sessions/bulk-assign', { sessionIds, osImageId });
    return data;
  }

  async getSessionHistory(from?: string, to?: string): Promise<unknown> {
    const params: Record<string, string> = {};
    if (from) params['from'] = from;
    if (to) params['to'] = to;
    const { data } = await this.http.get<unknown>('/api/session-history', { params });
    return data;
  }

  async cancelSession(sessionId: string): Promise<void> {
    await this.http.delete<unknown>(`/api/sessions/${encodeURIComponent(sessionId)}`);
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
    const { data } = await this.http.patch<unknown>(`/api/images/${encodeURIComponent(imageId)}`, payload);
    return data;
  }

  async deleteImage(imageId: string): Promise<void> {
    await this.http.delete<unknown>(`/api/images/${encodeURIComponent(imageId)}`);
  }

  async startOsImageUpload(payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/images/upload/start', payload);
    return data;
  }

  async publishOsImageUpload(uploadId: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>(
      `/api/images/upload/${encodeURIComponent(uploadId)}/publish`, payload, { timeout: PUBLISH_TIMEOUT_MS });
    return data;
  }

  async abandonOsImageUpload(uploadId: string, payload: unknown): Promise<void> {
    await this.http.post<unknown>(`/api/images/upload/${encodeURIComponent(uploadId)}/abandon`, payload);
  }

  // ── Upload job status ──────────────────────────────────────────────────────

  /**
   * Reads the status of a background publish job. Shared by the OS, boot and recovery image
   * upload flows, which all return 202 Accepted from publish and are completed by a worker.
   */
  async getUploadJob(uploadId: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/upload-jobs/${encodeURIComponent(uploadId)}`);
    return data;
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
    const { data } = await this.http.post<unknown>(
      `/api/boot-images/upload/${encodeURIComponent(token)}/publish`, payload, { timeout: PUBLISH_TIMEOUT_MS });
    return data;
  }

  async deleteBootImage(bootImageId: string): Promise<void> {
    await this.http.delete<unknown>(`/api/boot-images/${encodeURIComponent(bootImageId)}`);
  }

  // ── Configuration ─────────────────────────────────────────────────────────

  async getConfiguration(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/configuration');
    return data;
  }

  async putConfiguration(payload: unknown): Promise<void> {
    await this.http.put<unknown>('/api/configuration', payload);
  }

  // ── Partitioning scheme ───────────────────────────────────────────────────

  async getPartitioningScheme(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/partitioning-scheme');
    return data;
  }

  async putPartitioningScheme(payload: unknown): Promise<void> {
    await this.http.put<unknown>('/api/partitioning-scheme', payload);
  }

  // ── Recovery image operations ────────────────────────────────────────────

  async getRecoveryImages(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/recovery-images');
    return data;
  }

  async getRecoveryImageSas(recoveryImageId: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>(
      `/api/recovery-images/${encodeURIComponent(recoveryImageId)}/sas`);
    return data;
  }

  async deleteRecoveryImage(recoveryImageId: string): Promise<void> {
    await this.http.delete<unknown>(`/api/recovery-images/${encodeURIComponent(recoveryImageId)}`);
  }

  async startRecoveryImageUpload(payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/recovery-images/upload/start', payload);
    return data;
  }

  async publishRecoveryImageUpload(uploadId: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>(
      `/api/recovery-images/upload/${encodeURIComponent(uploadId)}/publish`, payload,
      { timeout: PUBLISH_TIMEOUT_MS });
    return data;
  }

  // ── Session logs ──────────────────────────────────────────────────────────

  async getSessionLogs(sessionId: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/sessions/${encodeURIComponent(sessionId)}/logs`);
    return data;
  }

  async getSessionLogDownloadUrl(sessionId: string, fileName: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(
      `/api/sessions/${encodeURIComponent(sessionId)}/logs/${encodeURIComponent(fileName)}/download-url`);
    return data;
  }

  /** Active boot media certificate metadata (thumbprint/validity). Never returns PFX bytes. */
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

  /** Clears the boot image logo, reverting boot media to the built-in default artwork. */
  async deleteBrandingLogo(): Promise<unknown> {
    const { data } = await this.http.delete<unknown>('/api/branding/logo');
    return data;
  }

  /** Clears the portal logo, reverting the portal UI to the built-in default artwork. */
  async deleteBrandingPortalLogo(): Promise<unknown> {
    const { data } = await this.http.delete<unknown>('/api/branding/portal-logo');
    return data;
  }

  // ── Location catalog ──────────────────────────────────────────────────────

  async getLocations(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/locations');
    return data;
  }

  async createLocation(payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/locations', payload);
    return data;
  }

  async deleteLocation(locationId: string): Promise<void> {
    await this.http.delete<unknown>(`/api/locations/${encodeURIComponent(locationId)}`);
  }

  // ── User location preference ──────────────────────────────────────────────

  async getUserLocationPreference(userId: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/user-preferences/${encodeURIComponent(userId)}`);
    return data;
  }

  async putUserLocationPreference(userId: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.put<unknown>(`/api/user-preferences/${encodeURIComponent(userId)}`, payload);
    return data;
  }
}

/** Singleton instance, created on first import, configured at request time. */
export const operatorApiClient = new OperatorApiClient(
  process.env['OPERATOR_API_BASE_URL'] ?? 'http://localhost:7072',
);
