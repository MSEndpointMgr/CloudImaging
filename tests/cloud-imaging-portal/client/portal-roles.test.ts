import { describe, it, expect } from 'vitest';
import {
  ROLE_ADMINISTRATOR,
  ROLE_OPERATOR,
  ROLE_TECHNICIAN,
  ROLE_READER,
  ROLE_AUTOPILOT_APPROVER,
  expandRoles,
  capabilitiesFromRoles,
  type PortalCapabilities,
} from '../../../src/cloud-imaging-portal/client/src/lib/portalRoles.ts';

const ALL_ROLES = [ROLE_ADMINISTRATOR, ROLE_OPERATOR, ROLE_TECHNICIAN, ROLE_READER, ROLE_AUTOPILOT_APPROVER];

function expanded(raw: string[]): string[] {
  return [...expandRoles(raw)].sort();
}

const NONE: PortalCapabilities = {
  hasPortalAccess: false,
  hasDashboardAccess: false,
  canUseImaging: false,
  canManageCatalog: false,
  canManageLocations: false,
  canViewReports: false,
  canViewAutopilot: false,
  canDecideAutopilot: false,
  canViewAutopilotReport: false,
  canViewSetupProfiles: false,
  canManageSetupProfiles: false,
  canViewDrivers: false,
  canManageDrivers: false,
  isAdministrator: false,
};

const OPERATOR: PortalCapabilities = {
  hasPortalAccess: true,
  hasDashboardAccess: true,
  canUseImaging: true,
  canManageCatalog: true,
  canManageLocations: true,
  canViewReports: true,
  canViewAutopilot: true,
  canDecideAutopilot: true,
  canViewAutopilotReport: true,
  canViewSetupProfiles: true,
  canManageSetupProfiles: true,
  canViewDrivers: true,
  canManageDrivers: true,
  isAdministrator: false,
};

describe('portalRoles: expandRoles', () => {
  it('Administrator expands to Operator and Technician', () => {
    expect(expanded([ROLE_ADMINISTRATOR])).toEqual([ROLE_ADMINISTRATOR, ROLE_OPERATOR, ROLE_TECHNICIAN].sort());
  });

  it('Operator expands to Technician', () => {
    expect(expanded([ROLE_OPERATOR])).toEqual([ROLE_OPERATOR, ROLE_TECHNICIAN].sort());
  });

  it.each([ROLE_TECHNICIAN, ROLE_READER, ROLE_AUTOPILOT_APPROVER])('%s expands to itself only', (role) => {
    expect(expanded([role])).toEqual([role]);
  });

  it('never yields PortalAccess, a server-only service role', () => {
    expect(expanded(ALL_ROLES)).not.toContain('CloudImaging.PortalAccess');
  });

  it.each(ALL_ROLES)('implication table is closed for %s', (role) => {
    const entry = expanded([role]);
    for (const implied of entry) {
      for (const transitive of expanded([implied])) expect(entry).toContain(transitive);
    }
  });

  it.each(ALL_ROLES.filter(r => r !== ROLE_ADMINISTRATOR))('%s does not imply Administrator', (role) => {
    expect(expanded([role])).not.toContain(ROLE_ADMINISTRATOR);
  });

  it.each([[[]], [[ROLE_OPERATOR]], [[ROLE_TECHNICIAN, ROLE_READER]], [ALL_ROLES]])('expansion of %j is a superset and idempotent', (raw) => {
    const once = expanded(raw);
    for (const role of raw) expect(once).toContain(role);
    expect(expanded(once)).toEqual(once);
  });

  it('compares role values case-sensitively', () => {
    expect(expanded(['cloudimaging.administrator'])).toEqual(['cloudimaging.administrator']);
  });
});

describe('portalRoles: capabilitiesFromRoles', () => {
  it('Administrator has every capability', () => {
    expect(capabilitiesFromRoles([ROLE_ADMINISTRATOR])).toEqual({ ...OPERATOR, isAdministrator: true });
  });

  it('Operator has every capability except the Administrator-only surfaces', () => {
    expect(capabilitiesFromRoles([ROLE_OPERATOR])).toEqual(OPERATOR);
  });

  it('Technician can use imaging and view Autopilot only', () => {
    expect(capabilitiesFromRoles([ROLE_TECHNICIAN])).toEqual({
      ...NONE,
      hasPortalAccess: true,
      hasDashboardAccess: true,
      canUseImaging: true,
      canViewAutopilot: true,
      canViewSetupProfiles: true,
      canViewDrivers: true,
    });
  });

  it('Reader sees the Dashboard and Reports only', () => {
    expect(capabilitiesFromRoles([ROLE_READER])).toEqual({
      ...NONE,
      hasPortalAccess: true,
      hasDashboardAccess: true,
      canViewReports: true,
      canViewAutopilotReport: true,
    });
  });

  it('AutopilotApprover sees and decides Autopilot registrations without the Dashboard', () => {
    expect(capabilitiesFromRoles([ROLE_AUTOPILOT_APPROVER])).toEqual({
      ...NONE,
      hasPortalAccess: true,
      canViewAutopilot: true,
      canDecideAutopilot: true,
      canViewAutopilotReport: true,
    });
  });

  it.each([ROLE_TECHNICIAN, ROLE_READER, ROLE_AUTOPILOT_APPROVER])('Operator plus %s equals Operator alone', (role) => {
    expect(capabilitiesFromRoles([ROLE_OPERATOR, role])).toEqual(OPERATOR);
  });

  it('an empty role list has no capability', () => {
    expect(capabilitiesFromRoles([])).toEqual(NONE);
  });

  it.each([
    [ROLE_ADMINISTRATOR, true, true],
    [ROLE_OPERATOR, true, true],
    [ROLE_TECHNICIAN, true, false],
    [ROLE_READER, false, false],
    [ROLE_AUTOPILOT_APPROVER, false, false],
  ])('%s: view drivers %s, manage drivers %s', (role, canView, canManage) => {
    const caps = capabilitiesFromRoles([role]);
    expect(caps.canViewDrivers).toBe(canView);
    expect(caps.canManageDrivers).toBe(canManage);
  });

  it.each([
    [ROLE_ADMINISTRATOR, true, true],
    [ROLE_OPERATOR, true, true],
    [ROLE_TECHNICIAN, true, false],
    [ROLE_READER, false, false],
    [ROLE_AUTOPILOT_APPROVER, false, false],
  ])('%s: view setup profiles %s, manage setup profiles %s', (role, canView, canManage) => {
    const caps = capabilitiesFromRoles([role]);
    expect(caps.canViewSetupProfiles).toBe(canView);
    expect(caps.canManageSetupProfiles).toBe(canManage);
  });

  it('Operator plus Reader keeps both setup profile flags', () => {
    const caps = capabilitiesFromRoles([ROLE_OPERATOR, ROLE_READER]);
    expect(caps.canViewSetupProfiles).toBe(true);
    expect(caps.canManageSetupProfiles).toBe(true);
  });

  it('unknown and differently cased role values are ignored', () => {
    expect(capabilitiesFromRoles(['CloudImaging.Unknown', 'cloudimaging.operator', 'CLOUDIMAGING.ADMINISTRATOR'])).toEqual(NONE);
    expect(capabilitiesFromRoles(['CloudImaging.Unknown', ROLE_READER])).toEqual(capabilitiesFromRoles([ROLE_READER]));
  });
});
