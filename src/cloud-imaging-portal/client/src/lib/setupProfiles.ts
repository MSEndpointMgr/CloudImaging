/**
 * Setup profiles as the portal sees them: types mirroring the API, labels for the enums, the
 * pure helpers behind the list page and editor, and the API calls. Every rule here mirrors one in
 * Imaging Core, which checks them all again on save.
 */
import { apiFetch, apiFetchWithRetry, extractErrorDetail, getApiAccessToken } from './apiClient.ts';

export type SetupRegistryHive = 'Software' | 'System' | 'DefaultUser';
export type SetupRegistryValueType = 'String' | 'ExpandString' | 'MultiString' | 'DWord' | 'QWord';
export type SetupScriptPhase = 'WinPE' | 'WindowsSetup';
export type SetupFailureHandling = 'CompleteWithWarnings' | 'FailDeployment';
export type StepItemKind = 'RegistryValue' | 'WinPEScript' | 'WindowsSetupScript' | 'DriverInstaller' | 'AnswerFile';
export type StepItemOutcome = 'Succeeded' | 'Failed' | 'Queued' | 'NotRun';

export interface SetupProfileTargeting {
  allDevices: boolean;
  locationIds: string[];
}

export interface SetupRegistryValue {
  id: string | null;
  name: string;
  hive: SetupRegistryHive;
  key: string;
  valueName: string;
  type: SetupRegistryValueType;
  data: string;
}

/** Where a script runs: every location of the profile, or the chosen ones. */
export interface SetupScriptLocations {
  all: boolean;
  locationIds: string[];
}

/** A script refers to its uploaded text by hash; the text itself is loaded on demand. */
export interface SetupScript {
  id: string | null;
  name: string;
  runs: SetupScriptPhase;
  order: number;
  timeoutMinutes: number;
  locations: SetupScriptLocations;
  /** Lowercase hex SHA-256 of the uploaded text; empty until it is uploaded. */
  contentSha256: string;
  sizeBytes: number;
  lastModifiedAt?: string | null;
  lastModifiedBy?: string | null;
}

export interface SetupAnswerFile {
  contentSha256: string;
  sizeBytes: number;
  passes?: string[];
  lastModifiedAt?: string | null;
  lastModifiedBy?: string | null;
}

export interface SetupProfileContent {
  failureHandling: SetupFailureHandling;
  registryValues: SetupRegistryValue[];
  scripts: SetupScript[];
  answerFile: SetupAnswerFile | null;
}

export interface SetupProfileSummary {
  profileId: string;
  name: string;
  targeting: SetupProfileTargeting;
  registryValueCount: number;
  scriptCount: number;
  hasAnswerFile: boolean;
  failureHandling: SetupFailureHandling;
  currentVersion: number;
  lastModifiedAt: string;
  lastModifiedBy?: string | null;
}

export interface SetupProfileDetail extends SetupProfileSummary {
  createdAt: string;
  createdBy?: string | null;
  content: SetupProfileContent;
}

/** The profile a session pinned when imaging started. */
export interface SetupProfilePin {
  profileId: string;
  name: string;
  version: number;
}

export interface ImagingStepItemCount {
  kind: StepItemKind;
  outcome: StepItemOutcome;
  count: number;
}

export interface ImagingStepItem {
  index: number;
  kind: StepItemKind;
  name: string;
  target?: string | null;
  outcome: StepItemOutcome;
  reason?: string | null;
  exitCode?: number | null;
  durationMs?: number | null;
  lastOutputLine?: string | null;
}

/** What the editor edits. A new profile has no `profileId` and `currentVersion` 0. */
export interface SetupProfileDraft {
  name: string;
  targeting: SetupProfileTargeting;
  content: SetupProfileContent;
}

// ── Labels ────────────────────────────────────────────────────────────────────

export const HIVE_LABELS: Record<SetupRegistryHive, string> = {
  Software: 'HKLM SOFTWARE',
  System: 'HKLM SYSTEM',
  DefaultUser: 'Default user',
};

/** How each hive starts a displayed path. */
const HIVE_PATH_PREFIXES: Record<SetupRegistryHive, string> = {
  Software: 'HKLM\\SOFTWARE',
  System: 'HKLM\\SYSTEM',
  DefaultUser: 'Default user',
};

export const VALUE_TYPE_LABELS: Record<SetupRegistryValueType, string> = {
  String: 'String',
  ExpandString: 'Expandable string',
  MultiString: 'Multi-string',
  DWord: 'DWORD',
  QWord: 'QWORD',
};

export const SCRIPT_PHASE_LABELS: Record<SetupScriptPhase, string> = {
  WinPE: 'Offline',
  WindowsSetup: 'Specialize',
};

export const FAILURE_HANDLING_LABELS: Record<SetupFailureHandling, string> = {
  CompleteWithWarnings: 'Complete with warnings',
  FailDeployment: 'Fail the deployment',
};

export const ITEM_OUTCOME_LABELS: Record<StepItemOutcome, string> = {
  Succeeded: 'Succeeded',
  Failed: 'Failed',
  Queued: 'Queued',
  NotRun: 'Not run',
};

export const HIVE_OPTIONS = (Object.keys(HIVE_LABELS) as SetupRegistryHive[]).map((value) => ({ value, label: HIVE_LABELS[value] }));
export const VALUE_TYPE_OPTIONS = (Object.keys(VALUE_TYPE_LABELS) as SetupRegistryValueType[]).map((value) => ({ value, label: VALUE_TYPE_LABELS[value] }));

export const EMPTY_TARGETING_MESSAGE = 'Choose at least one location or All devices.';
export const MAX_REGISTRY_VALUES = 200;
export const MAX_NAME_LENGTH = 100;

// ── Targeting ────────────────────────────────────────────────────────────────

/** Why a location cannot be picked: another profile already targets it. */
export function locationDisabledReason(profiles: SetupProfileSummary[], ownProfileId: string | undefined, locationId: string): string | undefined {
  const owner = profiles.find((p) => p.profileId !== ownProfileId && !p.targeting.allDevices && p.targeting.locationIds.includes(locationId));
  return owner ? `Targeted by ${owner.name}` : undefined;
}

/** Why All devices cannot be picked: another profile already has it. */
export function allDevicesDisabledReason(profiles: SetupProfileSummary[], ownProfileId: string | undefined): string | undefined {
  const owner = profiles.find((p) => p.profileId !== ownProfileId && p.targeting.allDevices);
  return owner ? `Used by ${owner.name}. Only one profile can target all devices.` : undefined;
}

/** How many of the listed locations other profiles already target, for the picker footer. */
export function locationsTargetedElsewhere(profiles: SetupProfileSummary[], ownProfileId: string | undefined, locationIds: string[]): number {
  return locationIds.filter((id) => locationDisabledReason(profiles, ownProfileId, id) !== undefined).length;
}

export function targetingProblem(targeting: SetupProfileTargeting): string | null {
  return !targeting.allDevices && targeting.locationIds.length === 0 ? EMPTY_TARGETING_MESSAGE : null;
}

/** Location names a profile targets, sorted, or "All devices". */
export function targetedNames(targeting: SetupProfileTargeting, locations: { locationId: string; name: string }[]): string[] {
  if (targeting.allDevices) return ['All devices'];
  return targeting.locationIds
    .map((id) => locations.find((l) => l.locationId === id)?.name)
    .filter((n): n is string => Boolean(n))
    .sort((a, b) => a.localeCompare(b));
}

/** The profile whose targeting covers a location: its own location first, then All devices. */
export function profileForLocation(profiles: SetupProfileSummary[], locationId: string | null | undefined): SetupProfileSummary | undefined {
  return (locationId ? profiles.find((p) => !p.targeting.allDevices && p.targeting.locationIds.includes(locationId)) : undefined)
    ?? profiles.find((p) => p.targeting.allDevices);
}

// ── Draft ────────────────────────────────────────────────────────────────────

export function emptyContent(): SetupProfileContent {
  return { failureHandling: 'CompleteWithWarnings', registryValues: [], scripts: [], answerFile: null };
}

export function emptyDraft(): SetupProfileDraft {
  return { name: '', targeting: { allDevices: false, locationIds: [] }, content: emptyContent() };
}

/** The editor's draft; display fields such as passes and last modified stay, saving strips them. */
export function draftFrom(detail: Pick<SetupProfileDetail, 'name' | 'targeting' | 'content'>): SetupProfileDraft {
  return {
    name: detail.name,
    targeting: { allDevices: detail.targeting.allDevices, locationIds: [...detail.targeting.locationIds] },
    content: {
      failureHandling: detail.content.failureHandling,
      registryValues: detail.content.registryValues.map((v) => ({ ...v })),
      scripts: detail.content.scripts.map((s) => ({ ...s, locations: { all: s.locations?.all ?? true, locationIds: [...(s.locations?.locationIds ?? [])] } })),
      answerFile: detail.content.answerFile ? { ...detail.content.answerFile } : null,
    },
  };
}

/** Only the fields an operator edits; Core fills in sizes, passes and modified fields. */
export function contentForSave(content: SetupProfileContent): SetupProfileContent {
  return {
    failureHandling: content.failureHandling,
    registryValues: content.registryValues.map((v) => ({ id: v.id, name: v.name, hive: v.hive, key: v.key, valueName: v.valueName, type: v.type, data: v.data })),
    scripts: content.scripts.map((s) => ({
      id: s.id, name: s.name, runs: s.runs, order: s.order, timeoutMinutes: s.timeoutMinutes,
      locations: s.locations.all ? { all: true, locationIds: [] } : { all: false, locationIds: [...s.locations.locationIds].sort() },
      contentSha256: s.contentSha256,
    })) as SetupScript[],
    answerFile: content.answerFile ? { contentSha256: content.answerFile.contentSha256 } as SetupAnswerFile : null,
  };
}

function sameContent(a: SetupProfileContent, b: SetupProfileContent): boolean {
  return JSON.stringify(contentForSave(a)) === JSON.stringify(contentForSave(b));
}

function sameTargeting(a: SetupProfileTargeting, b: SetupProfileTargeting): boolean {
  if (a.allDevices !== b.allDevices) return false;
  if (a.allDevices) return true;
  const left = [...a.locationIds].sort();
  const right = [...b.locationIds].sort();
  return left.length === right.length && left.every((id, i) => id === right[i]);
}

export interface DraftChanges {
  /** Registry values, scripts, answer file or failure handling differ: saving creates a version. */
  contentChanged: boolean;
  /** Name or targeting differ. */
  metadataChanged: boolean;
  dirty: boolean;
}

export function draftChanges(saved: SetupProfileDraft, draft: SetupProfileDraft): DraftChanges {
  const contentChanged = !sameContent(saved.content, draft.content);
  const metadataChanged = saved.name.trim() !== draft.name.trim() || !sameTargeting(saved.targeting, draft.targeting);
  return { contentChanged, metadataChanged, dirty: contentChanged || metadataChanged };
}

/** "<name> (copy)", then "(copy 2)", "(copy 3)" until the name is free. */
export function duplicateName(name: string, existing: string[]): string {
  const taken = new Set(existing.map((n) => n.trim().toLowerCase()));
  const base = `${name.trim()} (copy`;
  if (!taken.has(`${base})`.toLowerCase())) return `${base})`;
  for (let i = 2; ; i++) {
    const candidate = `${base} ${String(i)})`;
    if (!taken.has(candidate.toLowerCase())) return candidate;
  }
}

export function deleteImpact(names: string[]): string {
  return `Devices started from media for these locations stop getting this profile: ${names.join(', ')}. Deployments already running keep the version they started with. History is kept.`;
}

// ── Registry values ──────────────────────────────────────────────────────────

/** Full path including the value name, for example HKLM\SOFTWARE\Policies\Microsoft\FVE\EncryptionMethodWithXtsOs. */
export function registryValuePath(value: Pick<SetupRegistryValue, 'hive' | 'key' | 'valueName'>): string {
  return `${HIVE_PATH_PREFIXES[value.hive]}\\${value.key}\\${value.valueName || '(Default)'}`;
}

export function registryValueTooltip(value: Pick<SetupRegistryValue, 'hive' | 'key' | 'valueName' | 'type'>): string {
  return `${registryValuePath(value)} (${VALUE_TYPE_LABELS[value.type]})`;
}

const MAX_KEY_LENGTH = 512;
const MAX_KEY_PART_LENGTH = 255;
const MAX_VALUE_NAME_LENGTH = 255;
const MAX_STRING_DATA_LENGTH = 4096;

function label(name: string): string {
  return name.trim() ? `"${name.trim()}"` : 'a registry value without a name';
}

function isNumber(data: string, maxHexDigits: number, max: bigint): boolean {
  const text = data.trim();
  if (/^0x/i.test(text)) {
    const hex = text.slice(2);
    return hex.length > 0 && hex.length <= maxHexDigits && /^[0-9a-f]+$/i.test(hex) && BigInt(`0x${hex}`) <= max;
  }
  return /^\d+$/.test(text) && BigInt(text) <= max;
}

/** The same rules as Imaging Core, for one value against the profile's other values. */
export function validateRegistryValue(value: SetupRegistryValue, others: SetupRegistryValue[]): string[] {
  const problems: string[] = [];
  const name = label(value.name);
  if (!value.name.trim() || value.name.trim().length > MAX_NAME_LENGTH) problems.push('Every registry value needs a name of 1 to 100 characters.');

  const key = value.key;
  const parts = key.split('\\');
  if (!key.trim() || key.length > MAX_KEY_LENGTH) {
    problems.push(`The key of ${name} must be 1 to 512 characters.`);
  } else if (/^(HKLM\\|HKEY_)/i.test(key) || key.startsWith('\\') || key.endsWith('\\') || parts.some((p) => p.length === 0)) {
    problems.push(`The key of ${name} must be relative to the hive, for example Policies\\Microsoft\\FVE.`);
  } else if (parts.some((p) => p.length > MAX_KEY_PART_LENGTH)) {
    problems.push(`Each part of the key of ${name} can be at most 255 characters.`);
  } else if (value.hive !== 'System' && parts[0].toLowerCase() === 'currentcontrolset') {
    problems.push(`The key of ${name} starts with CurrentControlSet, which only exists under HKLM SYSTEM.`);
  }

  if (value.valueName.length > MAX_VALUE_NAME_LENGTH) problems.push(`The value name of ${name} can be at most 255 characters.`);

  const data = value.data;
  if ((value.type === 'String' || value.type === 'ExpandString' || value.type === 'MultiString') && data.length > MAX_STRING_DATA_LENGTH) {
    problems.push(`The data of ${name} can be at most 4,096 characters.`);
  } else if (value.type === 'MultiString' && (data.length === 0 || data.split('\n').some((line) => line.length === 0))) {
    problems.push(`The data of ${name} cannot contain empty lines.`);
  } else if (value.type === 'DWord' && !isNumber(data, 8, 0xffffffffn)) {
    problems.push(`The data of ${name} must be a DWORD: 0 to 4294967295, or 0x followed by up to 8 hex digits.`);
  } else if (value.type === 'QWord' && !isNumber(data, 16, 0xffffffffffffffffn)) {
    problems.push(`The data of ${name} must be a QWORD: 0 to 18446744073709551615, or 0x followed by up to 16 hex digits.`);
  }

  const identity = (v: SetupRegistryValue) => `${v.hive}|${v.key.trim().toLowerCase()}|${v.valueName.toLowerCase()}`;
  const duplicate = others.find((o) => identity(o) === identity(value));
  if (duplicate) problems.push(`${name} sets the same value as ${label(duplicate.name)}. Keep one of them.`);
  return problems;
}

// ── Scripts ──────────────────────────────────────────────────────────────────

export const MAX_SCRIPTS = 1000;
/** 24 MB of UTF-8 per script, a leading BOM not counted. */
export const MAX_SCRIPT_BYTES = 25_165_824;
export const TIMEOUT_MINUTES = [1, 2, 5, 10, 15, 20, 30] as const;
export const DEFAULT_TIMEOUT_MINUTES = 5;
export const SCRIPT_NOT_UPLOADED_MESSAGE = 'The script was not uploaded.';
export const ANSWER_FILE_NOT_UPLOADED_MESSAGE = 'The answer file was not uploaded.';

/** The Runs column. */
export const SCRIPT_PHASE_SHORT_LABELS: Record<SetupScriptPhase, string> = SCRIPT_PHASE_LABELS;

export const SCRIPT_RUNS_OPTIONS = [
  { value: 'WinPE', label: 'Offline', description: "Right after the image is applied, in the boot image. Shown on the timeline. A failure follows the profile's failure handling." },
  { value: 'WindowsSetup', label: 'Specialize', description: 'During Windows setup, after the restart and before OOBE and Autopilot. Logged on the device only.' },
];

export const SCRIPT_LOCATIONS_OPTIONS = [
  { value: 'all', label: 'All locations of this profile' },
  { value: 'selected', label: 'Selected locations' },
];

/** "5 min" in the table. */
export function timeoutLabel(minutes: number): string {
  return `${String(minutes)} min`;
}

/** "5 minutes" (or "1 minute") in the dialog. */
export function timeoutOptionLabel(minutes: number): string {
  return `${String(minutes)} ${minutes === 1 ? 'minute' : 'minutes'}`;
}

export const TIMEOUT_OPTIONS = TIMEOUT_MINUTES.map((m) => ({ value: String(m), label: timeoutOptionLabel(m) }));

/** UTF-8 length without encoding a copy, so a 24 MB script is measured on every keystroke cheaply. */
export function utf8Length(text: string): number {
  let bytes = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code < 0x80) bytes += 1;
    else if (code < 0x800) bytes += 2;
    else if (code >= 0xd800 && code <= 0xdbff && i + 1 < text.length) { bytes += 4; i++; }
    else bytes += 3;
  }
  return bytes;
}

const MB = 1_048_576;

function megabytes(bytes: number): string {
  return (bytes / MB).toFixed(1);
}

/** "1.8 of 24 MB". */
export function scriptSizeLabel(bytes: number): string {
  return `${megabytes(bytes)} of 24 MB`;
}

/** "This script is 25.3 MB. A script can be at most 24 MB.", the size rounded up so it never reads 24.0. */
export function scriptTooLargeMessage(bytes: number): string {
  return `This script is ${(Math.ceil((bytes / MB) * 10) / 10).toFixed(1)} MB. A script can be at most 24 MB.`;
}

/** "Uploading 9.8 of 18.2 MB". */
export function uploadProgressLabel(loaded: number, total: number): string {
  return `Uploading ${megabytes(loaded)} of ${megabytes(total)} MB`;
}

/** "Loading the script (12.4 MB)". */
export function loadingScriptLabel(bytes: number): string {
  return `Loading the script (${megabytes(bytes)} MB)`;
}

/** Offline scripts first, then Specialize scripts, each by order. */
export function orderedScripts(scripts: SetupScript[]): SetupScript[] {
  const rank = (s: SetupScript) => (s.runs === 'WinPE' ? 0 : 1);
  return [...scripts].sort((a, b) => rank(a) - rank(b) || a.order - b.order);
}

/** Orders 1, 2, 3 within each phase, keeping the current sequence. */
export function renumberScripts(scripts: SetupScript[]): SetupScript[] {
  const next: Record<SetupScriptPhase, number> = { WinPE: 0, WindowsSetup: 0 };
  return orderedScripts(scripts).map((s) => ({ ...s, order: ++next[s.runs] }));
}

/** Rebuilds a list from the phases in their new sequence, numbering each phase from 1. */
function fromPhases(offline: SetupScript[], specialize: SetupScript[]): SetupScript[] {
  return [...offline.map((s, i) => ({ ...s, order: i + 1 })), ...specialize.map((s, i) => ({ ...s, order: i + 1 }))];
}

/**
 * Moves a script so it runs directly before `before` (a script of the same phase), or last in its
 * phase when `before` is null. Indexes are into `orderedScripts(scripts)`.
 */
export function moveScriptBefore(scripts: SetupScript[], index: number, before: number | null): SetupScript[] {
  const list = orderedScripts(scripts);
  const moving = list[index];
  if (!moving || (before !== null && (list[before]?.runs !== moving.runs || before === index))) return list;
  const anchor = before === null ? null : list[before];
  const phase = list.filter((s) => s.runs === moving.runs && s !== moving);
  const at = anchor === null ? phase.length : phase.indexOf(anchor);
  phase.splice(at, 0, moving);
  const offline = moving.runs === 'WinPE' ? phase : list.filter((s) => s.runs === 'WinPE');
  const specialize = moving.runs === 'WindowsSetup' ? phase : list.filter((s) => s.runs === 'WindowsSetup');
  return fromPhases(offline, specialize);
}

/**
 * Moves a script to `order` within its phase: it is inserted there and the scripts from that place
 * on move down one (or up one when it moves down). With a filter on, `visible` holds the rows on
 * screen and the script lands directly before the first visible row at or after that place, or
 * after the last visible row, so it stays among the scripts the operator sees.
 */
export function moveScriptTo(scripts: SetupScript[], index: number, order: number, visible?: SetupScript[]): SetupScript[] {
  const list = orderedScripts(scripts);
  const moving = list[index];
  if (!moving || !Number.isFinite(order)) return list;
  const phase = list.filter((s) => s.runs === moving.runs && s !== moving);
  const at = Math.min(Math.max(Math.trunc(order), 1), phase.length + 1) - 1;
  const shown = visible ? new Set(visible.filter((s) => s !== moving).map((s) => keyOf(s))) : null;
  let anchor: SetupScript | null = phase[at] ?? null;
  if (shown) {
    const visibleInPhase = phase.filter((s) => shown.has(keyOf(s)));
    if (visibleInPhase.length > 0) {
      const next = phase.slice(at).find((s) => shown.has(keyOf(s)));
      anchor = next ?? phase[phase.indexOf(visibleInPhase[visibleInPhase.length - 1]) + 1] ?? null;
    }
  }
  return moveScriptBefore(list, index, anchor === null ? null : list.indexOf(anchor));
}

/**
 * Moves a script one visible row up or down within its phase, for the arrow keys on its handle.
 * Without a filter every row is visible.
 */
export function moveScriptBy(scripts: SetupScript[], index: number, direction: -1 | 1, visible?: SetupScript[]): SetupScript[] {
  const list = orderedScripts(scripts);
  const moving = list[index];
  if (!moving) return list;
  const shown = new Set((visible ?? list).map((s) => keyOf(s)));
  const rows = list.filter((s) => s.runs === moving.runs && (s === moving || shown.has(keyOf(s))));
  const target = rows[rows.indexOf(moving) + direction];
  if (!target) return list;
  return moveScriptTo(list, index, target.order, visible);
}

/** A stable key for a script row: its id, or for an unsaved one its phase, order and name. */
export function keyOf(script: Pick<SetupScript, 'id' | 'runs' | 'order' | 'name'>): string {
  return script.id ?? `new-${script.runs}-${String(script.order)}-${script.name}`;
}

/** Adds or replaces a script; a new script, or one moved to the other phase, goes last in its phase. */
export function upsertScript(scripts: SetupScript[], script: SetupScript, replaceIndex?: number): SetupScript[] {
  const list = orderedScripts(scripts);
  const previous = replaceIndex === undefined ? undefined : list[replaceIndex];
  const rest = replaceIndex === undefined ? list : list.filter((_, i) => i !== replaceIndex);
  const last = Math.max(0, ...rest.filter((s) => s.runs === script.runs).map((s) => s.order));
  const order = previous && previous.runs === script.runs ? previous.order : last + 1;
  const merged = replaceIndex === undefined ? [...rest, { ...script, order }] : [...rest.slice(0, replaceIndex), { ...script, order }, ...rest.slice(replaceIndex)];
  return renumberScripts(merged);
}

/** The same rules as Imaging Core, for one script against the profile's other scripts. */
export function validateScript(script: SetupScript, others: SetupScript[]): string[] {
  const problems: string[] = [];
  const name = script.name.trim();
  const scriptLabel = name ? `"${name}"` : 'a script without a name';
  if (!name || name.length > MAX_NAME_LENGTH) problems.push('Every script needs a name of 1 to 100 characters.');
  else if (others.some((o) => o.name.trim().toLowerCase() === name.toLowerCase())) problems.push(`Script names must be unique. Rename ${scriptLabel} and save again.`);
  if (!(TIMEOUT_MINUTES as readonly number[]).includes(script.timeoutMinutes)) problems.push(`The timeout of ${scriptLabel} must be 1 to 30 minutes.`);
  if (!script.locations.all && script.locations.locationIds.length === 0) problems.push('Choose at least one location, or run the script at all locations of this profile.');
  return problems;
}

/** "Empty" problem of the script text, checked in the dialog before it is uploaded. */
export function scriptTextProblem(text: string): string | null {
  if (text.trim().length === 0) return 'The script is empty. Add the script text.';
  const bytes = utf8Length(text.charCodeAt(0) === 0xfeff ? text.slice(1) : text);
  return bytes > MAX_SCRIPT_BYTES ? scriptTooLargeMessage(bytes) : null;
}

// ── Scripts per location ─────────────────────────────────────────────────────

/** The locations a script may run at: the profile's own, or every location for an All devices profile. */
export function profileLocationIds(targeting: SetupProfileTargeting, allLocationIds: string[]): string[] {
  return targeting.allDevices ? allLocationIds : targeting.locationIds;
}

/** A script's chosen locations the profile still targets; null when it runs at all of them. */
export function effectiveScriptLocations(script: SetupScript, targeting: SetupProfileTargeting, allLocationIds: string[]): string[] | null {
  if (script.locations.all) return null;
  const allowed = new Set(profileLocationIds(targeting, allLocationIds));
  return script.locations.locationIds.filter((id) => allowed.has(id));
}

/** "\"<name>\" has no locations left. Choose a location or delete the script." */
export function noLocationsLeftMessage(name: string): string {
  return `"${name.trim()}" has no locations left. Choose a location or delete the script.`;
}

/** The save bar's message for the first script left without a location, or null. */
export function scriptsWithoutLocationsProblem(scripts: SetupScript[], targeting: SetupProfileTargeting, allLocationIds: string[]): string | null {
  const stranded = orderedScripts(scripts).find((s) => effectiveScriptLocations(s, targeting, allLocationIds)?.length === 0);
  return stranded ? noLocationsLeftMessage(stranded.name) : null;
}

/** The scripts a device from `locationId` runs, in run order. */
export function scriptsForLocation(scripts: SetupScript[], locationId: string, targeting: SetupProfileTargeting, allLocationIds: string[]): SetupScript[] {
  return orderedScripts(scripts).filter((s) => effectiveScriptLocations(s, targeting, allLocationIds)?.includes(locationId) ?? true);
}

/** "4 of 186 scripts run on devices from Oslo Office". */
export function locationFilterFooter(shown: number, total: number, locationName: string): string {
  return `${String(shown)} of ${plural(total, 'script', 'scripts')} run on devices from ${locationName}`;
}

/** The draft as it is saved: every script keeps only the locations the profile still targets. */
export function withEffectiveLocations(content: SetupProfileContent, targeting: SetupProfileTargeting, allLocationIds: string[]): SetupProfileContent {
  return {
    ...content,
    scripts: content.scripts.map((s) => (s.locations.all ? s : { ...s, locations: { all: false, locationIds: effectiveScriptLocations(s, targeting, allLocationIds) ?? [] } })),
  };
}

// ── Answer file ──────────────────────────────────────────────────────────────

/** "2.4 KB" in the answer file row. */
export function answerFileSizeLabel(bytes: number): string {
  return `${(bytes / 1024).toFixed(1)} KB`;
}

/** "0.1 of 2 MB" under the answer file editor. */
export function answerFileEditorSizeLabel(bytes: number): string {
  return `${megabytes(bytes)} of 2 MB`;
}

/** Answer file problems from a refused save, with a missing line read as the whole file. */
export function answerFileProblems(errors: SetupProfileApiError['errors']): { code: string; line: number; message: string }[] {
  return errors.map((e) => ({ code: e.code, line: e.line ?? 0, message: e.message }));
}

// ── Versions and history ─────────────────────────────────────────────────────

export interface SetupProfileSectionChange {
  added: number;
  removed: number;
  changed: number;
  reordered?: boolean;
}

export interface SetupProfileChangeSummary {
  registryValues: SetupProfileSectionChange;
  scripts: SetupProfileSectionChange;
  answerFile: 'NoChange' | 'Added' | 'Removed' | 'Changed';
  failureHandling: { from: SetupFailureHandling; to: SetupFailureHandling } | null;
  names: { registryValues: string[]; scripts: string[] };
  total: number;
}

export interface SetupProfileVersionSummary {
  version: number;
  createdAt: string;
  createdBy?: string | null;
  changeSummary: SetupProfileChangeSummary;
  restoredFrom?: number | null;
  duplicatedFrom?: { profileId: string; profileName: string; version: number } | null;
  isCurrent: boolean;
  inUseCount: number;
}

export interface SetupProfileVersionDetail extends SetupProfileVersionSummary {
  content: SetupProfileContent;
}

export type SetupProfileHistoryKind = 'Created' | 'Renamed' | 'TargetingChanged' | 'LocationDeleted' | 'VersionSaved' | 'VersionRestored' | 'Duplicated' | 'Deleted';

export interface SetupProfileHistoryEntry {
  kind: SetupProfileHistoryKind;
  at: string;
  by?: string | null;
  change: string;
}

const TARGETING_HISTORY_KINDS: ReadonlySet<SetupProfileHistoryKind> = new Set(['Created', 'Renamed', 'TargetingChanged', 'LocationDeleted', 'Deleted']);

/** Entries of the Targeting tab. Version kinds, duplication included, belong to the Versions tab. */
export function targetingHistory(entries: SetupProfileHistoryEntry[]): SetupProfileHistoryEntry[] {
  return entries.filter((e) => TARGETING_HISTORY_KINDS.has(e.kind));
}

/** "1 change", "3 changes". */
export function changesLabel(total: number): string {
  return total === 0 ? 'No changes' : plural(total, 'change', 'changes');
}

/**
 * One section of an expanded version: "No change", ""Time zone" changed", or with mixed kinds
 * "1 added, 2 changed: "A", "B", "C"". Past ten names the rest is counted.
 */
export function sectionChangeText(change: SetupProfileSectionChange, names: string[]): string {
  const kinds = ([['added', change.added], ['changed', change.changed], ['removed', change.removed]] as const).filter(([, n]) => n > 0);
  const count = change.added + change.changed + change.removed;
  if (count === 0) return change.reordered ? 'Reordered' : 'No change';
  const quoted = names.map((n) => `"${n}"`).join(', ') + (count > names.length ? `, and ${String(count - names.length)} more` : '');
  const text = kinds.length === 1 && names.length > 0
    ? `${quoted} ${kinds[0][0]}`
    : `${kinds.map(([kind, n]) => `${String(n)} ${kind}`).join(', ')}${names.length > 0 ? `: ${quoted}` : ''}`;
  return change.reordered ? `${text}, reordered` : text;
}

export function answerFileChangeText(change: SetupProfileChangeSummary['answerFile']): string {
  return change === 'NoChange' ? 'No change' : change;
}

export function failureHandlingChangeText(change: SetupProfileChangeSummary['failureHandling']): string {
  return change ? `${FAILURE_HANDLING_LABELS[change.from]} to ${FAILURE_HANDLING_LABELS[change.to]}` : 'No change';
}

/** The labelled fields of an expanded version row. */
export function versionFields(version: SetupProfileVersionSummary): { label: string; value: string }[] {
  const summary = version.changeSummary;
  const fields = [
    { label: 'Registry values', value: sectionChangeText(summary.registryValues, summary.names.registryValues) },
    { label: 'Scripts', value: sectionChangeText(summary.scripts, summary.names.scripts) },
    { label: 'Answer file', value: answerFileChangeText(summary.answerFile) },
    { label: 'Failure handling', value: failureHandlingChangeText(summary.failureHandling) },
  ];
  if (version.restoredFrom) fields.push({ label: 'Restored from', value: `Version ${String(version.restoredFrom)}` });
  if (version.duplicatedFrom) fields.push({ label: 'Duplicated from', value: `${version.duplicatedFrom.profileName}, version ${String(version.duplicatedFrom.version)}` });
  return fields;
}

/** Tooltip of the In use badge. */
export function inUseTooltip(count: number): string {
  return count === 1 ? '1 running deployment uses this version' : `${String(count)} running deployments use this version`;
}

/** Under the Versions table; only Administrators can read the retention setting, so it is optional. */
export function versionsFootnote(count: number, retention: number | null): string {
  const kept = retention === null ? 'The most recent' : `The ${String(retention)} most recent`;
  return `${plural(count, 'version', 'versions')}. ${kept} are kept, plus older ones running deployments still use.`;
}

export function restoreImpact(version: number, currentVersion: number): string {
  return `Saves version ${String(version)}'s registry values, scripts, answer file and failure handling as version ${String(currentVersion + 1)}. Devices imaged from now on get it; running deployments keep the version they started with. Targeting is not changed.`;
}

export function readOnlyVersionBanner(version: number, currentVersion: number): string {
  if (version === currentVersion) return `Version ${String(version)}, read-only. This is the current version.`;
  return `Version ${String(version)}, read-only. The current version is ${String(currentVersion)}.`;
}

// ── Start imaging ────────────────────────────────────────────────────────────

/**
 * One line per group of selected devices for the Start imaging overlay, for example "3 devices:
 * Stockholm and Gothenburg", "1 device started from media without a location: Nordics standard"
 * and "2 devices: No setup profile". No version numbers: the version that ran is on the timeline.
 * Empty when no profiles exist.
 */
export function groupDevicesBySetupProfile(sessions: { locationId?: string | null }[], profiles: SetupProfileSummary[]): string[] {
  if (profiles.length === 0 || sessions.length === 0) return [];
  const devices = (n: number) => plural(n, 'device', 'devices');
  const byProfile = new Map<string, number>();
  let withoutLocation = 0;
  let unresolved = 0;
  const allDevices = profiles.find((p) => p.targeting.allDevices);
  for (const session of sessions) {
    const own = session.locationId ? profiles.find((p) => !p.targeting.allDevices && p.targeting.locationIds.includes(session.locationId!)) : undefined;
    if (own) byProfile.set(own.name, (byProfile.get(own.name) ?? 0) + 1);
    else if (allDevices && session.locationId) byProfile.set(allDevices.name, (byProfile.get(allDevices.name) ?? 0) + 1);
    else if (allDevices) withoutLocation++;
    else unresolved++;
  }
  const lines = [...byProfile.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([name, n]) => `${devices(n)}: ${name}`);
  if (withoutLocation > 0) lines.push(`${devices(withoutLocation)} started from media without a location: ${allDevices!.name}`);
  if (unresolved > 0) lines.push(`${devices(unresolved)}: No setup profile`);
  return lines;
}

// ── Deployment timeline ──────────────────────────────────────────────────────

function plural(count: number, one: string, many: string): string {
  return `${String(count)} ${count === 1 ? one : many}`;
}

export const STEP_ITEM_KIND_LABELS: Record<StepItemKind, string> = {
  RegistryValue: 'Registry value',
  WinPEScript: 'Offline script',
  WindowsSetupScript: 'Specialize script',
  DriverInstaller: 'Driver installer',
  AnswerFile: 'Answer file',
};

export type SummaryTone = 'failed' | 'notRun' | 'succeeded' | 'queued';

export interface SummaryItem {
  tone: SummaryTone;
  text: string;
}

/**
 * The Prepare setup card's summary, one entry per outcome with failures first, for example
 * "1 Offline script failed", "4 registry values written", "1 script and 5 driver installers queued
 * for Specialize".
 */
export function itemCountsSummary(counts: ImagingStepItemCount[]): SummaryItem[] {
  const total = (kind: StepItemKind, outcome: StepItemOutcome) =>
    counts.filter((c) => c.kind === kind && c.outcome === outcome).reduce((sum, c) => sum + c.count, 0);
  const nouns: Record<StepItemKind, [string, string]> = {
    RegistryValue: ['registry value', 'registry values'],
    WinPEScript: ['Offline script', 'Offline scripts'],
    WindowsSetupScript: ['Specialize script', 'Specialize scripts'],
    DriverInstaller: ['driver installer', 'driver installers'],
    AnswerFile: ['answer file', 'answer files'],
  };
  const kinds: StepItemKind[] = ['RegistryValue', 'WinPEScript', 'WindowsSetupScript', 'DriverInstaller', 'AnswerFile'];
  const items: SummaryItem[] = [];

  for (const [outcome, tone, verb] of [['Failed', 'failed', 'failed'], ['NotRun', 'notRun', 'not run']] as const) {
    for (const kind of kinds) {
      const n = total(kind, outcome);
      if (n > 0) items.push({ tone, text: kind === 'AnswerFile' ? `Answer file ${verb}` : `${plural(n, ...nouns[kind])} ${verb}` });
    }
  }

  const written = total('RegistryValue', 'Succeeded');
  if (written > 0) items.push({ tone: 'succeeded', text: `${plural(written, ...nouns.RegistryValue)} written` });
  const ran = total('WinPEScript', 'Succeeded');
  if (ran > 0) items.push({ tone: 'succeeded', text: `${plural(ran, ...nouns.WinPEScript)} succeeded` });

  const queuedScripts = total('WindowsSetupScript', 'Queued');
  const queuedInstallers = total('DriverInstaller', 'Queued');
  const queued = [
    queuedScripts > 0 ? plural(queuedScripts, 'script', 'scripts') : null,
    queuedInstallers > 0 ? plural(queuedInstallers, ...nouns.DriverInstaller) : null,
  ].filter((part): part is string => part !== null);
  if (queued.length > 0) items.push({ tone: 'queued', text: `${queued.join(' and ')} queued for Specialize` });
  return items;
}

/** The status column of one item row, for example "Failed: exit code 1" or "Queued for Specialize". */
export function itemStatusText(item: Pick<ImagingStepItem, 'kind' | 'outcome' | 'reason' | 'exitCode'>): string {
  switch (item.outcome) {
    case 'Failed': {
      if (item.exitCode != null && item.reason?.startsWith('Exited with code')) return `Failed: exit code ${String(item.exitCode)}`;
      const first = item.reason?.split(/(?<=\.)\s/)[0]?.replace(/\.$/, '');
      return first ? `Failed: ${first}` : 'Failed';
    }
    case 'NotRun':
      return 'Not run';
    case 'Queued':
      return 'Queued for Specialize';
    case 'Succeeded':
      return item.kind === 'RegistryValue' || item.kind === 'AnswerFile' ? 'Written' : 'Succeeded';
  }
}

/** The full reason behind an item row, with the script's last output line when there is one. */
export function itemTooltip(item: Pick<ImagingStepItem, 'reason' | 'lastOutputLine'>): string | undefined {
  const text = [item.reason, item.lastOutputLine ? `Last output: ${item.lastOutputLine}` : null].filter(Boolean).join(' ');
  return text || undefined;
}

/** Failed items first, then not run, then the rest, each group in device order. */
export function sortItemsFailuresFirst<T extends Pick<ImagingStepItem, 'index' | 'outcome'>>(items: T[]): T[] {
  const rank: Record<StepItemOutcome, number> = { Failed: 0, NotRun: 1, Succeeded: 2, Queued: 2 };
  return [...items].sort((a, b) => rank[a.outcome] - rank[b.outcome] || a.index - b.index);
}

// ── API ──────────────────────────────────────────────────────────────────────

/** A refusal from the server: its message, and the per-line problems when there are any. */
export class SetupProfileApiError extends Error {
  constructor(message: string, readonly status: number, readonly errors: { code: string; line: number | null; message: string }[] = []) {
    super(message);
  }
}

async function failure(res: Response, fallback: string): Promise<SetupProfileApiError> {
  const text = await res.clone().text().catch(() => '');
  let errors: SetupProfileApiError['errors'] = [];
  try {
    const parsed = JSON.parse(text) as { errors?: unknown };
    if (Array.isArray(parsed.errors)) errors = parsed.errors as SetupProfileApiError['errors'];
  } catch {
    // Plain-text body; the message is enough.
  }
  return new SetupProfileApiError(await extractErrorDetail(res, fallback), res.status, errors);
}

async function readJson<T>(res: Response, fallback: string): Promise<T> {
  if (!res.ok) throw await failure(res, fallback);
  return await res.json() as T;
}

const JSON_HEADERS = { 'Content-Type': 'application/json' };

export async function fetchSetupProfiles(): Promise<SetupProfileSummary[]> {
  return readJson(await apiFetchWithRetry('/api/setup-profiles', { credentials: 'include' }), 'Could not load setup profiles.');
}

export async function fetchSetupProfile(profileId: string): Promise<SetupProfileDetail> {
  return readJson(await apiFetchWithRetry(`/api/setup-profiles/${encodeURIComponent(profileId)}`, { credentials: 'include' }), 'Could not load the setup profile.');
}

export async function createSetupProfile(draft: SetupProfileDraft, duplicatedFrom: { profileId: string; version: number } | null): Promise<SetupProfileDetail> {
  const res = await apiFetch('/api/setup-profiles', {
    method: 'POST',
    headers: JSON_HEADERS,
    credentials: 'include',
    body: JSON.stringify({ name: draft.name.trim(), targeting: draft.targeting, duplicatedFrom, content: contentForSave(draft.content) }),
  });
  return readJson(res, 'The setup profile was not saved.');
}

export async function saveSetupProfile(profileId: string, baseVersion: number, draft: SetupProfileDraft, includeContent: boolean): Promise<SetupProfileDetail> {
  const res = await apiFetch(`/api/setup-profiles/${encodeURIComponent(profileId)}`, {
    method: 'PUT',
    headers: JSON_HEADERS,
    credentials: 'include',
    body: JSON.stringify({ baseVersion, name: draft.name.trim(), targeting: draft.targeting, ...(includeContent ? { content: contentForSave(draft.content) } : {}) }),
  });
  return readJson(res, 'The setup profile was not saved.');
}

export async function deleteSetupProfile(profileId: string): Promise<void> {
  const res = await apiFetch(`/api/setup-profiles/${encodeURIComponent(profileId)}`, { method: 'DELETE', credentials: 'include' });
  if (!res.ok) throw await failure(res, 'The setup profile was not deleted.');
}

export async function fetchSetupProfileVersions(profileId: string): Promise<SetupProfileVersionSummary[]> {
  return readJson(await apiFetchWithRetry(`/api/setup-profiles/${encodeURIComponent(profileId)}/versions`, { credentials: 'include' }), 'Could not load the versions.');
}

export async function fetchSetupProfileVersion(profileId: string, version: number): Promise<SetupProfileVersionDetail> {
  return readJson(await apiFetchWithRetry(`/api/setup-profiles/${encodeURIComponent(profileId)}/versions/${String(version)}`, { credentials: 'include' }), 'Could not load the version.');
}

export async function fetchSetupProfileHistory(profileId: string): Promise<SetupProfileHistoryEntry[]> {
  return readJson(await apiFetchWithRetry(`/api/setup-profiles/${encodeURIComponent(profileId)}/history`, { credentials: 'include' }), 'Could not load the history.');
}

export async function restoreSetupProfileVersion(profileId: string, version: number, baseVersion: number): Promise<SetupProfileDetail> {
  const res = await apiFetch(`/api/setup-profiles/${encodeURIComponent(profileId)}/restore`, {
    method: 'POST',
    headers: JSON_HEADERS,
    credentials: 'include',
    body: JSON.stringify({ version, baseVersion }),
  });
  return readJson(res, 'The version was not restored.');
}

export async function fetchStepItems(sessionId: string, stepName: string): Promise<ImagingStepItem[]> {
  return readJson(await apiFetchWithRetry(`/api/sessions/${encodeURIComponent(sessionId)}/steps/${encodeURIComponent(stepName)}/items`, { credentials: 'include' }), 'Could not load the items.');
}

/** What Imaging Core stored for one uploaded text; `problems` and `passes` come with an answer file. */
export interface ContentUploadResult {
  sha256: string;
  sizeBytes: number;
  passes?: string[];
  problems?: { code: string; line: number; message: string }[];
}

/**
 * Uploads one script or answer file text as raw UTF-8, reporting bytes sent as it goes.
 * XMLHttpRequest because fetch cannot report upload progress. Aborting `signal` stops it.
 */
export async function uploadSetupProfileContent(
  kind: 'script' | 'answerFile',
  text: string,
  onProgress?: (loaded: number, total: number) => void,
  signal?: AbortSignal,
): Promise<ContentUploadResult> {
  const token = await getApiAccessToken();
  const body = new Blob([text], { type: 'application/octet-stream' });
  const fallback = kind === 'script' ? SCRIPT_NOT_UPLOADED_MESSAGE : ANSWER_FILE_NOT_UPLOADED_MESSAGE;
  return new Promise<ContentUploadResult>((resolve, reject) => {
    if (signal?.aborted) { reject(new DOMException('Upload cancelled', 'AbortError')); return; }
    const xhr = new XMLHttpRequest();
    xhr.open('POST', `/api/setup-profiles/content?kind=${kind}`);
    xhr.withCredentials = true;
    if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`);
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
    xhr.upload.addEventListener('progress', (e) => { if (e.lengthComputable) onProgress?.(e.loaded, e.total); });
    xhr.addEventListener('load', () => {
      let parsed: unknown = null;
      try { parsed = JSON.parse(xhr.responseText); } catch { /* not JSON */ }
      if (xhr.status >= 200 && xhr.status < 300 && parsed && typeof parsed === 'object') {
        resolve(parsed as ContentUploadResult);
        return;
      }
      const detail = parsed && typeof parsed === 'object' ? (parsed as { detail?: unknown }).detail : undefined;
      reject(new SetupProfileApiError(typeof detail === 'string' && detail ? detail : fallback, xhr.status));
    });
    xhr.addEventListener('error', () => reject(new SetupProfileApiError(fallback, 0)));
    xhr.addEventListener('abort', () => reject(new DOMException('Upload cancelled', 'AbortError')));
    signal?.addEventListener('abort', () => xhr.abort());
    xhr.send(body);
  });
}

/** Loads a script or answer file text a retained version of the profile uses. */
export async function fetchSetupProfileContent(profileId: string, sha256: string, signal?: AbortSignal): Promise<string> {
  const res = await apiFetchWithRetry(`/api/setup-profiles/${encodeURIComponent(profileId)}/content/${encodeURIComponent(sha256)}`, { credentials: 'include', signal });
  if (!res.ok) throw await failure(res, 'Could not load the script.');
  return await res.text();
}
