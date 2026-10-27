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
 * Typed HTTP client for portal backend -> Operator API calls over Private Link (T041, FR-013).
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

  async assignSession(sessionId: string, osImageId: string, extra?: Record<string, unknown>): Promise<unknown> {
    const { data } = await this.http.post<unknown>(
      `/api/sessions/${encodeURIComponent(sessionId)}/assign`, { osImageId, ...extra });
    return data;
  }

  /** `extra` carries the driver choices and the acting user, only when a choice was made. */
  async bulkAssign(sessionIds: string[], osImageId: string, extra?: Record<string, unknown>): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/sessions/bulk-assign', { sessionIds, osImageId, ...extra });
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

  // ── Pre-flight overrides ─────────────────────────────────────────────────────

  async listPreFlightOverrides(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/preflight-overrides');
    return data;
  }

  async approvePreFlightOverride(sessionId: string, payload: { approvedBy: string; approvedByObjectId?: string }): Promise<unknown> {
    const { data } = await this.http.post<unknown>(`/api/sessions/${encodeURIComponent(sessionId)}/preflight-override`, payload);
    return data;
  }

  async revokePreFlightOverride(serialNumber: string, revokedBy: string): Promise<void> {
    await this.http.delete<unknown>('/api/preflight-overrides', { params: { serialNumber, revokedBy } });
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

  async promoteBootImage(bootImageId: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>(`/api/boot-images/${encodeURIComponent(bootImageId)}/promote`);
    return data;
  }

  async demoteBootImage(bootImageId: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>(`/api/boot-images/${encodeURIComponent(bootImageId)}/demote`);
    return data;
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

  /** Stops accepting the boot media certificate the last rotation replaced. False when none was still accepted. */
  async stopAcceptingPreviousBootMediaCert(): Promise<boolean> {
    const res = await this.http.post('/api/cert/previous/stop', undefined, { validateStatus: (s) => s === 204 || s === 404 });
    return res.status === 204;
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

  async updateLocation(locationId: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.put<unknown>(`/api/locations/${encodeURIComponent(locationId)}`, payload);
    return data;
  }

  async deleteLocation(locationId: string, changedBy?: string): Promise<void> {
    await this.http.delete<unknown>(`/api/locations/${encodeURIComponent(locationId)}`, changedBy ? { params: { changedBy } } : undefined);
  }

  async getRegions(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/regions');
    return data;
  }

  async putRegions(payload: unknown): Promise<void> {
    await this.http.put<unknown>('/api/regions', payload);
  }

  // ── Wi-Fi networks ────────────────────────────────────────────────────────

  async getWifiNetworks(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/wifi-networks');
    return data;
  }

  async createWifiNetwork(payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/wifi-networks', payload);
    return data;
  }

  async updateWifiNetwork(networkId: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.put<unknown>(`/api/wifi-networks/${encodeURIComponent(networkId)}`, payload);
    return data;
  }

  async deleteWifiNetwork(networkId: string): Promise<void> {
    await this.http.delete<unknown>(`/api/wifi-networks/${encodeURIComponent(networkId)}`);
  }

  async setWifiNetworkOrder(payload: unknown): Promise<void> {
    await this.http.put<unknown>('/api/wifi-networks/order', payload);
  }

  async setLocationWifiNetworks(locationId: string, payload: unknown): Promise<void> {
    await this.http.put<unknown>(`/api/locations/${encodeURIComponent(locationId)}/wifi-networks`, payload);
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

  // ── Autopilot registration ───────────────────────────────────────────────

  async listAutopilotRegistrations(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/autopilot/registrations');
    return data;
  }

  async listHandledAutopilotRegistrations(from?: string, to?: string): Promise<unknown> {
    const params: Record<string, string> = { view: 'history' };
    if (from) params['from'] = from;
    if (to) params['to'] = to;
    const { data } = await this.http.get<unknown>('/api/autopilot/registrations', { params });
    return data;
  }

  async getAutopilotRegistration(requestId: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/autopilot/registrations/${encodeURIComponent(requestId)}`);
    return data;
  }

  async decideAutopilotRegistration(requestId: string, action: 'approve' | 'reject' | 'retry', payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>(`/api/autopilot/registrations/${encodeURIComponent(requestId)}/${action}`, payload);
    return data;
  }

  async listAutopilotGroupTags(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/autopilot/group-tags');
    return data;
  }

  async createAutopilotGroupTag(payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/autopilot/group-tags', payload);
    return data;
  }

  async updateAutopilotGroupTag(id: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.put<unknown>(`/api/autopilot/group-tags/${encodeURIComponent(id)}`, payload);
    return data;
  }

  async deleteAutopilotGroupTag(id: string, changedBy?: string): Promise<void> {
    await this.http.delete<unknown>(`/api/autopilot/group-tags/${encodeURIComponent(id)}`, changedBy ? { params: { changedBy } } : undefined);
  }

  // ── Setup profiles ──────────────────────────────────────────────────────────

  async getSetupProfiles(): Promise<unknown> {
    const { data } = await this.http.get<unknown>('/api/setup-profiles');
    return data;
  }

  /**
   * Driver routes: forwards to `/api/drivers/<path>` with the same method, query and body and
   * returns the upstream status with the body. The path is built from validated segments only.
   */
  async drivers(
    method: 'GET' | 'POST' | 'PUT' | 'DELETE',
    path: string,
    options: { body?: unknown; query?: Record<string, string | undefined> } = {},
  ): Promise<{ status: number; data: unknown }> {
    const response = await this.http.request<unknown>({
      method,
      url: `/api/drivers/${path}`,
      params: options.query,
      data: options.body,
    });
    return { status: response.status, data: response.data };
  }

  async getSetupProfile(profileId: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/setup-profiles/${encodeURIComponent(profileId)}`);
    return data;
  }

  async createSetupProfile(payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/setup-profiles', payload);
    return data;
  }

  async updateSetupProfile(profileId: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.put<unknown>(`/api/setup-profiles/${encodeURIComponent(profileId)}`, payload);
    return data;
  }

  async deleteSetupProfile(profileId: string, changedBy?: string): Promise<void> {
    await this.http.delete<unknown>(`/api/setup-profiles/${encodeURIComponent(profileId)}`, changedBy ? { params: { changedBy } } : undefined);
  }

  async restoreSetupProfileVersion(profileId: string, payload: unknown): Promise<unknown> {
    const { data } = await this.http.post<unknown>(`/api/setup-profiles/${encodeURIComponent(profileId)}/restore`, payload);
    return data;
  }

  async getSetupProfileVersions(profileId: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/setup-profiles/${encodeURIComponent(profileId)}/versions`);
    return data;
  }

  async getSetupProfileVersion(profileId: string, version: number): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/setup-profiles/${encodeURIComponent(profileId)}/versions/${String(version)}`);
    return data;
  }

  async getSetupProfileHistory(profileId: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/setup-profiles/${encodeURIComponent(profileId)}/history`);
    return data;
  }

  /** Sends one script or answer file text as raw bytes; Imaging Core applies every limit. */
  async uploadSetupProfileContent(kind: string | undefined, body: Buffer, uploadedBy?: string): Promise<unknown> {
    const { data } = await this.http.post<unknown>('/api/setup-profiles/content', body, {
      params: uploadedBy ? { kind: kind ?? '', uploadedBy } : { kind: kind ?? '' },
      headers: { 'Content-Type': 'application/octet-stream' },
      maxBodyLength: Infinity,
      timeout: PUBLISH_TIMEOUT_MS,
    });
    return data;
  }

  /** Reads a script or answer file text a retained version of the profile uses. */
  async getSetupProfileContent(profileId: string, sha256: string): Promise<{ data: Buffer; contentType: string }> {
    const res = await this.http.get(`/api/setup-profiles/${encodeURIComponent(profileId)}/content/${encodeURIComponent(sha256)}`, {
      responseType: 'arraybuffer',
      maxContentLength: Infinity,
      timeout: PUBLISH_TIMEOUT_MS,
    });
    return {
      data: Buffer.from(res.data as ArrayBuffer),
      contentType: (res.headers['content-type'] as string | undefined) ?? 'text/plain; charset=utf-8',
    };
  }

  async getSessionStepItems(sessionId: string, stepName: string): Promise<unknown> {
    const { data } = await this.http.get<unknown>(`/api/sessions/${encodeURIComponent(sessionId)}/steps/${encodeURIComponent(stepName)}/items`);
    return data;
  }
}

/** Singleton instance, created on first import, configured at request time. */
export const operatorApiClient = new OperatorApiClient(
  process.env['OPERATOR_API_BASE_URL'] ?? 'http://localhost:7072',
);
