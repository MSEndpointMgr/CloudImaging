/**
 * Shared types and pure helpers for the Autopilot registration feature. Kept free of React so the
 * rules the portal shows (template preview, state labels) are unit-testable and match the
 * server-side resolver in CloudImaging.Contracts (AutopilotGroupTagTemplate).
 */

export type AutopilotState =
  | 'PendingApproval'
  | 'Importing'
  | 'Imported'
  | 'ImportFailed'
  | 'Rejected'
  | 'Expired'
  | 'AlreadyRegistered';

export type GroupTagKind = 'Static' | 'Template';

export interface AutopilotRequest {
  requestId: string;
  referenceCode: string;
  serialNumber: string;
  manufacturer: string;
  model: string;
  architecture: 'x64' | 'arm64';
  locationId?: string | null;
  locationName?: string | null;
  state: AutopilotState;
  submittedAt: string;
  updatedAt: string;
  expiresAt: string;
  clientVersion?: string | null;
  tpmVersion?: string | null;
  /** True when the hash has TPM 2.0 data; false when it lacks it; null when it could not be read. */
  preProvisioningReady?: boolean | null;
  groupTag?: string | null;
  decidedByUpn?: string | null;
  decidedAt?: string | null;
  rejectionReason?: string | null;
  importStartedAt?: string | null;
  importCompletedAt?: string | null;
  importAttempts: number;
  importErrorCode?: string | null;
  importErrorName?: string | null;
}

export interface GroupTagOption {
  definitionId: string;
  name: string;
  kind: GroupTagKind;
  value: string;
  resolvedValue?: string | null;
  unavailableReason?: string | null;
}

export interface AutopilotDetail {
  request: AutopilotRequest;
  groupTagOptions: GroupTagOption[];
  groupTagRequired: boolean;
  locationRegion?: string | null;
  locationCountryCode?: string | null;
}

export interface GroupTagDefinition {
  id: string;
  name: string;
  kind: GroupTagKind;
  value: string;
  description?: string | null;
  createdAt: string;
}

export type BadgeVariant = 'success' | 'warning' | 'info' | 'negative' | 'muted';

const STATE_PRESENTATION: Record<AutopilotState, { label: string; variant: BadgeVariant }> = {
  PendingApproval: { label: 'Pending approval', variant: 'warning' },
  Importing: { label: 'Importing', variant: 'info' },
  Imported: { label: 'Imported', variant: 'success' },
  ImportFailed: { label: 'Import failed', variant: 'negative' },
  Rejected: { label: 'Rejected', variant: 'muted' },
  Expired: { label: 'Expired', variant: 'muted' },
  AlreadyRegistered: { label: 'Already registered', variant: 'success' },
};

export function stateLabel(state: AutopilotState): string {
  return STATE_PRESENTATION[state]?.label ?? state;
}

export function stateVariant(state: AutopilotState): BadgeVariant {
  return STATE_PRESENTATION[state]?.variant ?? 'muted';
}

/** How the hash's TPM data affects Autopilot deployment modes, for display. */
export function preProvisioningLabel(ready: boolean | null | undefined): string {
  if (ready === true) return 'Supported';
  if (ready === false) return 'Not supported (no TPM 2.0 data)';
  return 'Unknown';
}

export const TEMPLATE_TOKENS = [
  { token: '{LocationName}', label: 'Location name' },
  { token: '{Region}', label: 'Region' },
  { token: '{CountryCode}', label: 'Country' },
] as const;

const TOKEN_PATTERN = /\{[^{}]*\}/g;
const ALLOWED_CHARACTERS = /^[A-Za-z0-9 _.-]+$/;
export const MAX_GROUP_TAG_LENGTH = 128;

/** Returns a validation message for a group tag definition, or null when it can be saved. */
export function validateGroupTagDefinition(kind: GroupTagKind, value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) return 'A value is required.';
  if (kind === 'Static') return validateResolvedTag(trimmed);

  const tokens = trimmed.match(TOKEN_PATTERN) ?? [];
  if (tokens.length === 0) return 'A template must contain at least one token. Use a static tag for fixed text.';
  const known = TEMPLATE_TOKENS.map((t) => t.token as string);
  const unknown = tokens.find((t) => !known.includes(t));
  if (unknown) return `Unknown token ${unknown}.`;

  const literal = trimmed.replace(TOKEN_PATTERN, '');
  return literal && !ALLOWED_CHARACTERS.test(literal)
    ? 'Literal text may only contain letters, digits, spaces, hyphens, underscores and periods.'
    : null;
}

/** Returns a validation message for a final tag value, or null when it can be imported. */
export function validateResolvedTag(value: string): string | null {
  if (value.length > MAX_GROUP_TAG_LENGTH) return `Group tags are limited to ${String(MAX_GROUP_TAG_LENGTH)} characters.`;
  return ALLOWED_CHARACTERS.test(value)
    ? null
    : 'Group tags may only contain letters, digits, spaces, hyphens, underscores and periods.';
}

export interface TemplateLocation {
  name?: string | null;
  region?: string | null;
  countryCode?: string | null;
}

/** Resolves a template against a location the same way Imaging Core does. */
export function resolveTemplate(template: string, location: TemplateLocation): { value: string | null; reason: string | null } {
  const missing = new Set<string>();
  const resolved = template.replace(TOKEN_PATTERN, (token) => {
    const raw = token === '{LocationName}' ? location.name
      : token === '{Region}' ? location.region
      : token === '{CountryCode}' ? location.countryCode
      : null;
    const tokenValue = raw?.trim();
    if (!tokenValue) {
      missing.add(token);
      return '';
    }
    return tokenValue;
  });

  if (missing.size > 0) {
    const reason = location.name?.trim()
      ? `The location has no value for ${[...missing].join(', ')}.`
      : 'The device has no location.';
    return { value: null, reason };
  }

  const error = validateResolvedTag(resolved);
  return error ? { value: null, reason: error } : { value: resolved, reason: null };
}
