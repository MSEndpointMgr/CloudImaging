import { CircuitBoard, Cpu, Fingerprint, ShieldCheck, type LucideIcon } from 'lucide-react';

/**
 * Pre-flight check display text. Mirrors `PreFlightCheckText` in CloudImaging.Contracts, which the
 * device shows and logs; change both together so the portal and the device read the same.
 */

export type PreFlightCheckName = 'AutopilotPresence' | 'FirmwareMode' | 'SecureBoot' | 'TpmVersion';
export type PreFlightCheckOutcome = 'Passed' | 'Failed' | 'NotRequired' | 'Approved';

export interface PreFlightCheckResult {
  check: PreFlightCheckName;
  outcome: PreFlightCheckOutcome;
  observed: string;
  approvedBy?: string | null;
}

export interface PreFlightOverride {
  serialNumber: string;
  coveredChecks: PreFlightCheckName[];
  sourceSessionId: string;
  deviceManufacturer: string;
  deviceModel: string;
  locationName?: string | null;
  sourceChecks: PreFlightCheckResult[];
  approvedBy: string;
  approvedByObjectId?: string | null;
  approvedAt: string;
  expiresAt: string;
}

export interface PreFlightOverrideList {
  preFlightEnabled: boolean;
  overrides: PreFlightOverride[];
}

/** The pre-flight part of the portal configuration. */
export interface PreFlightSettings {
  devicePreFlightAuthorizationEnabled: boolean;
  preFlightRequireAutopilotPresence: boolean;
  preFlightRequireUefiFirmware: boolean;
  preFlightRequireSecureBoot: boolean;
  preFlightRequireTpm20: boolean;
}

/** Mirrors `PortalConfiguration.IsPreFlightConfigurationValid`: switched on with nothing selected blocks nothing. */
export function isPreFlightSettingsValid(s: PreFlightSettings): boolean {
  return !s.devicePreFlightAuthorizationEnabled
    || s.preFlightRequireAutopilotPresence
    || s.preFlightRequireUefiFirmware
    || s.preFlightRequireSecureBoot
    || s.preFlightRequireTpm20;
}

/** Display order everywhere: chip strips, tiles, the device screen. */
export const PRE_FLIGHT_CHECKS: readonly PreFlightCheckName[] = ['AutopilotPresence', 'FirmwareMode', 'SecureBoot', 'TpmVersion'];

/**
 * Sessions for the Blocked tab. A blocked session leaves it once an administrator approves it:
 * from then on its override stands for it, until the device uses or loses the pass.
 */
export function blockedSessions<T extends { sessionId: string; state: string }>(sessions: readonly T[], overrides: readonly PreFlightOverride[]): T[] {
  const approved = new Set(overrides.map(o => o.sourceSessionId));
  return sessions.filter(s => s.state === 'SessionNotAuthorized' && !approved.has(s.sessionId));
}

const NAMES: Record<PreFlightCheckName, string> = {
  AutopilotPresence: 'Autopilot presence',
  FirmwareMode: 'Firmware mode',
  SecureBoot: 'Secure Boot',
  TpmVersion: 'TPM version',
};

export const PRE_FLIGHT_ICONS: Record<PreFlightCheckName, LucideIcon> = {
  AutopilotPresence: Fingerprint,
  FirmwareMode: CircuitBoard,
  SecureBoot: ShieldCheck,
  TpmVersion: Cpu,
};

const VALUES: Record<string, string> = {
  NotReported: 'Not reported',
  NotChecked: 'Not checked',
  Autopilot: 'Present in Autopilot',
  CorporateIdentifier: 'Imported as Corporate Identifier',
  NotFound: 'Not present',
  Uefi: 'UEFI',
  LegacyBios: 'Legacy BIOS (CSM)',
  Enabled: 'Enabled',
  Disabled: 'Not enabled',
  Unsupported: 'Not enabled',
  Tpm20: 'Version 2.0 or above',
  Tpm12: 'Version 1.2',
  NotDetected: 'Not detected',
  Unknown: 'Could not be detected',
};

const FIXES: Record<PreFlightCheckName, string> = {
  AutopilotPresence: 'Register the device in Windows Autopilot, or import it as a Corporate Identifier.',
  FirmwareMode: 'Switch to UEFI in firmware settings.',
  SecureBoot: 'Enable Secure Boot in firmware settings.',
  TpmVersion: 'Enable the TPM in firmware settings.',
};

export function preFlightCheckName(check: PreFlightCheckName): string {
  return NAMES[check] ?? check;
}

export function preFlightValue(observed: string): string {
  return VALUES[observed] ?? observed;
}

/** One-line action for a failed check. Never refers to another check. */
export function preFlightFixHint(check: PreFlightCheckName, observed: string): string {
  if (observed === 'NotReported') return 'Update the boot media.';
  if (observed === 'Unknown') return 'See the device log for why it could not be read.';
  return FIXES[check] ?? '';
}

/** The session's results in display order, with any check the backend did not report left out. */
export function orderedChecks(checks: readonly PreFlightCheckResult[] | null | undefined): PreFlightCheckResult[] {
  if (!checks) return [];
  return PRE_FLIGHT_CHECKS
    .map(name => checks.find(c => c.check === name))
    .filter((c): c is PreFlightCheckResult => c !== undefined);
}

/** "Name: value" for a chip's tooltip and accessible name. */
export function preFlightChipLabel(result: PreFlightCheckResult): string {
  const name = preFlightCheckName(result.check);
  if (result.outcome === 'NotRequired') return `${name}: Not required`;
  const value = preFlightValue(result.observed);
  return result.outcome === 'Approved' ? `${name}: ${value} (approved)` : `${name}: ${value}`;
}

/** The source session's checks as the override applies them: the covered ones read as approved. */
export function overrideChecks(override: PreFlightOverride): PreFlightCheckResult[] {
  return override.sourceChecks.map(c => (override.coveredChecks.includes(c.check)
    ? { ...c, outcome: 'Approved' as const, approvedBy: override.approvedBy }
    : c));
}

/** Time left until an override expires ("in 6 days"). Overrides are listed only while valid. */
export function formatExpiresIn(iso: string, now: number = Date.now()): string {
  const remaining = new Date(iso).getTime() - now;
  if (Number.isNaN(remaining)) return '-';
  const minute = 60_000;
  const hour = 60 * minute;
  const day = 24 * hour;
  if (remaining < minute) return 'in under a minute';
  if (remaining < hour) return `in ${Math.floor(remaining / minute)} min`;
  if (remaining < day) return `in ${Math.floor(remaining / hour)} hr`;
  const days = Math.floor(remaining / day);
  return days === 1 ? 'in 1 day' : `in ${days} days`;
}
