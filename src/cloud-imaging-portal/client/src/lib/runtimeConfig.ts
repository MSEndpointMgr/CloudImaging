/**
 * Runtime portal configuration.
 *
 * The portal is shipped as a single prebuilt SPA bundle that must work for any
 * customer tenant without a per-deployment rebuild (FR-041/FR-042). Rather than
 * baking Entra ID identifiers in at build time, the SPA fetches them at startup
 * from the portal backend's public `/api/config` endpoint, which is populated by
 * Bicep from the deployment's `portalClientId` / `tenantId` parameters.
 *
 * A build-time `VITE_ENTRA_*` fallback is retained for local development, where
 * the Vite dev server proxies `/api` to a locally-run backend that may not expose
 * `/api/config`.
 */
export interface PortalRuntimeConfig {
  clientId: string;
  tenantId: string;
  authority: string;
  apiScope: string;
}

let cachedConfig: PortalRuntimeConfig | null = null;

function fromBuildTimeEnv(): PortalRuntimeConfig | null {
  const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID as string | undefined;
  const authority = import.meta.env.VITE_ENTRA_AUTHORITY as string | undefined;
  if (!clientId || !authority) return null;
  return {
    clientId,
    tenantId: (import.meta.env.VITE_ENTRA_TENANT_ID as string | undefined) ?? '',
    authority,
    apiScope: `api://${clientId}/user_impersonation`,
  };
}

/**
 * Loads portal configuration once and caches it. Prefers the backend's runtime
 * `/api/config` endpoint (production); falls back to build-time Vite env (local
 * dev). Throws when neither source yields a usable client ID + authority.
 */
export async function loadRuntimeConfig(): Promise<PortalRuntimeConfig> {
  if (cachedConfig) return cachedConfig;

  try {
    const res = await fetch('/api/config', { credentials: 'omit' });
    if (res.ok) {
      const data = (await res.json()) as Partial<PortalRuntimeConfig>;
      if (data.clientId && data.authority) {
        cachedConfig = {
          clientId: data.clientId,
          tenantId: data.tenantId ?? '',
          authority: data.authority,
          apiScope: data.apiScope && data.apiScope.length > 0
            ? data.apiScope
            : `api://${data.clientId}/user_impersonation`,
        };
        return cachedConfig;
      }
    }
  } catch {
    // Network/endpoint unavailable. Fall through to the build-time fallback.
  }

  const fallback = fromBuildTimeEnv();
  if (!fallback) {
    throw new Error(
      'Portal configuration unavailable: /api/config returned no client ID and no ' +
      'VITE_ENTRA_* build-time fallback is set. Verify the portal backend is reachable ' +
      'and that its ENTRA_CLIENT_ID / ENTRA_AUTHORITY app settings are populated.',
    );
  }
  cachedConfig = fallback;
  return cachedConfig;
}

/** Returns the loaded config. Throws if {@link loadRuntimeConfig} has not completed. */
export function getRuntimeConfig(): PortalRuntimeConfig {
  if (!cachedConfig) {
    throw new Error('Runtime config not loaded. Call loadRuntimeConfig() during bootstrap.');
  }
  return cachedConfig;
}

/** Delegated scope requested for the portal backend API (`api://<clientId>/user_impersonation`). */
export function getApiScope(): string {
  return getRuntimeConfig().apiScope;
}
