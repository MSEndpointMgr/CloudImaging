import { DefaultAzureCredential, type AccessToken, type TokenCredential } from '@azure/identity';

/**
 * Acquires the portal backend's OWN Entra token for the Operator API (backend-for-frontend
 * pattern, FR-013/FR-040a). The portal is a confidential service: it authenticates to the
 * Operator API with its user-assigned managed identity, which carries the service-only
 * `CloudImaging.PortalAccess` app role. User RBAC (Administrator/Technician) is enforced at
 * the portal edge by {@link ../middleware/roleGuard roleGuard}; the user's browser token is
 * NEVER forwarded downstream (it lacks the service role and would be rejected with 403).
 *
 * In Azure, {@link DefaultAzureCredential} selects the user-assigned identity via the
 * `AZURE_CLIENT_ID` app setting. Locally it falls back to the developer's Azure CLI sign-in
 * (which will only succeed if that principal has been granted the PortalAccess role).
 */

const scope = process.env['OPERATOR_API_SCOPE'] ?? '';
const managedIdentityClientId = process.env['AZURE_CLIENT_ID'];

// Refresh a little before the token actually expires to avoid using a token mid-flight
// that lapses before the downstream call completes.
const EXPIRY_SKEW_MS = 5 * 60_000;

let credential: TokenCredential | undefined;
let cachedToken: AccessToken | undefined;
let inFlight: Promise<AccessToken> | undefined;

function getCredential(): TokenCredential {
  credential ??= new DefaultAzureCredential(
    managedIdentityClientId ? { managedIdentityClientId } : {},
  );
  return credential;
}

function isValid(token: AccessToken | undefined): token is AccessToken {
  return token !== undefined && token.expiresOnTimestamp - Date.now() > EXPIRY_SKEW_MS;
}

/**
 * Returns a valid bearer token for the Operator API, acquiring (and caching) a new one from
 * the managed identity when necessary. Concurrent callers share a single in-flight request.
 */
export async function getOperatorApiToken(): Promise<string> {
  if (!scope) {
    throw new Error(
      'OPERATOR_API_SCOPE is not configured. Set it to the Operator API app registration scope ' +
        '(e.g. api://<operator-api-client-id>/.default) so the portal backend can authenticate.',
    );
  }

  if (isValid(cachedToken)) {
    return cachedToken.token;
  }

  inFlight ??= getCredential()
    .getToken(scope)
    .then((token) => {
      if (!token) {
        throw new Error('Managed identity returned no token for the Operator API.');
      }
      cachedToken = token;
      return token;
    })
    .finally(() => {
      inFlight = undefined;
    });

  return (await inFlight).token;
}

/** Test-only hook to reset the module-level token cache. */
export function __resetOperatorApiTokenCache(): void {
  cachedToken = undefined;
  inFlight = undefined;
  credential = undefined;
}
