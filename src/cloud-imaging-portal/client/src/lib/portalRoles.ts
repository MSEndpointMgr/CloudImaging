/** Portal user roles carried in the ID token `roles` claim. */
export const ROLE_ADMINISTRATOR = 'CloudImaging.Administrator';
export const ROLE_OPERATOR = 'CloudImaging.Operator';
export const ROLE_TECHNICIAN = 'CloudImaging.Technician';
export const ROLE_READER = 'CloudImaging.Reader';
export const ROLE_AUTOPILOT_APPROVER = 'CloudImaging.AutopilotApprover';

/**
 * Administrator implies Operator, which implies Technician. Tokens carry only directly assigned
 * roles, so the portal expands them itself. Each entry lists its full closure because expansion is
 * a single pass. Reader and AutopilotApprover are standalone. Mirrors the server's table without
 * PortalAccess, which is a service role that never reaches the browser.
 */
const ROLE_IMPLICATIONS: Readonly<Record<string, readonly string[]>> = {
  [ROLE_ADMINISTRATOR]: [ROLE_OPERATOR, ROLE_TECHNICIAN],
  [ROLE_OPERATOR]: [ROLE_TECHNICIAN],
};

/** Returns the raw roles plus every role they imply. Comparison is case-sensitive. */
export function expandRoles(raw: readonly string[]): Set<string> {
  const expanded = new Set(raw);
  for (const role of raw) {
    for (const implied of ROLE_IMPLICATIONS[role] ?? []) expanded.add(implied);
  }
  return expanded;
}

/** What the signed-in user may see and do in the portal. The server enforces the same rules. */
export interface PortalCapabilities {
  /** Any portal role; false shows the Access denied screen. */
  hasPortalAccess: boolean;
  /** Home route and Dashboard. An Approver-only user lands on the Autopilot page instead. */
  hasDashboardAccess: boolean;
  /** Devices and the OS, Boot and Recovery Images pages. */
  canUseImaging: boolean;
  /** Upload, edit, delete, promote and demote catalog images. */
  canManageCatalog: boolean;
  /** Locations and Wi-Fi Networks pages. */
  canManageLocations: boolean;
  /** Reports index and the session reports. */
  canViewReports: boolean;
  /** Autopilot registrations page. */
  canViewAutopilot: boolean;
  /** Approve, reject, retry and change the group tag of Autopilot registrations. */
  canDecideAutopilot: boolean;
  /** Autopilot Registration History report. */
  canViewAutopilotReport: boolean;
  /** Setup Profiles page, read-only. */
  canViewSetupProfiles: boolean;
  /** Create, edit, duplicate, delete and restore setup profiles. */
  canManageSetupProfiles: boolean;
  /** Drivers page and model pages, read-only. */
  canViewDrivers: boolean;
  /** Onboard and remove models, change actions, rules, sync, storage and custom components. */
  canManageDrivers: boolean;
  /** Branding, Configuration, pre-flight overrides and the update notice. */
  isAdministrator: boolean;
}

export function capabilitiesFromRoles(raw: readonly string[]): PortalCapabilities {
  const roles = expandRoles(raw);
  const has = (...wanted: string[]): boolean => wanted.some(role => roles.has(role));
  return {
    hasPortalAccess: has(ROLE_TECHNICIAN, ROLE_READER, ROLE_AUTOPILOT_APPROVER),
    hasDashboardAccess: has(ROLE_TECHNICIAN, ROLE_READER),
    canUseImaging: has(ROLE_TECHNICIAN),
    canManageCatalog: has(ROLE_OPERATOR),
    canManageLocations: has(ROLE_OPERATOR),
    canViewReports: has(ROLE_OPERATOR, ROLE_READER),
    canViewAutopilot: has(ROLE_TECHNICIAN, ROLE_AUTOPILOT_APPROVER),
    canDecideAutopilot: has(ROLE_OPERATOR, ROLE_AUTOPILOT_APPROVER),
    canViewAutopilotReport: has(ROLE_OPERATOR, ROLE_READER, ROLE_AUTOPILOT_APPROVER),
    canViewSetupProfiles: has(ROLE_TECHNICIAN),
    canManageSetupProfiles: has(ROLE_OPERATOR),
    canViewDrivers: has(ROLE_TECHNICIAN),
    canManageDrivers: has(ROLE_OPERATOR),
    isAdministrator: has(ROLE_ADMINISTRATOR),
  };
}
