/**
 * Setup profiles as the portal sees them: types mirroring the API, labels for the enums, the
 * pure helpers behind the list page and editor, and the API calls. Every rule here mirrors one in
 * Imaging Core, which checks them all again on save.
 */
import { apiFetch, apiFetchWithRetry, extractErrorDetail } from './apiClient.ts';

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

export interface SetupScript {
  id: string | null;
  name: string;
  runs: SetupScriptPhase;
  order: number;
  timeoutMinutes: number;
  content: string;
  sha256?: string | null;
  lastModifiedAt?: string | null;
  lastModifiedBy?: string | null;
}

export interface SetupAnswerFile {
  content: string;
  passes?: string[];
  sizeBytes?: number;
  sha256?: string | null;
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
  WinPE: 'In WinPE',
  WindowsSetup: 'During Windows setup',
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
      scripts: detail.content.scripts.map((s) => ({ ...s })),
      answerFile: detail.content.answerFile ? { ...detail.content.answerFile } : null,
    },
  };
}

/** Only the fields an operator edits; Core fills in hashes, sizes, passes and modified fields. */
export function contentForSave(content: SetupProfileContent): SetupProfileContent {
  return {
    failureHandling: content.failureHandling,
    registryValues: content.registryValues.map((v) => ({ id: v.id, name: v.name, hive: v.hive, key: v.key, valueName: v.valueName, type: v.type, data: v.data })),
    scripts: content.scripts.map((s) => ({ id: s.id, name: s.name, runs: s.runs, order: s.order, timeoutMinutes: s.timeoutMinutes, content: s.content })),
    answerFile: content.answerFile ? { content: content.answerFile.content } : null,
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

/** "Save as version N" when content changed (N = current + 1), else "Save changes". */
export function saveLabel(currentVersion: number, contentChanged: boolean): string {
  return contentChanged ? `Save as version ${String(currentVersion + 1)}` : 'Save changes';
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

export const MAX_SCRIPTS = 20;
export const MAX_SCRIPT_BYTES = 65_536;
export const TIMEOUT_MINUTES = [1, 2, 5, 10, 15, 20, 30] as const;
export const DEFAULT_TIMEOUT_MINUTES = 5;

/** The Runs column: "WinPE" or "Windows setup". */
export const SCRIPT_PHASE_SHORT_LABELS: Record<SetupScriptPhase, string> = {
  WinPE: 'WinPE',
  WindowsSetup: 'Windows setup',
};

export const SCRIPT_RUNS_OPTIONS = [
  { value: 'WinPE', label: 'In WinPE', description: "Right after the image is applied. Shown on the timeline. A failure follows the profile's failure handling." },
  { value: 'WindowsSetup', label: 'During Windows setup', description: 'Before OOBE, after the restart and before Autopilot. Logged on the device only.' },
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

export function utf8Length(text: string): number {
  return new TextEncoder().encode(text).length;
}

/** "12.4 of 64 KB". */
export function scriptSizeLabel(content: string): string {
  return `${(utf8Length(content) / 1024).toFixed(1)} of 64 KB`;
}

/** WinPE scripts first, then Windows setup scripts, each by order. */
export function orderedScripts(scripts: SetupScript[]): SetupScript[] {
  const rank = (s: SetupScript) => (s.runs === 'WinPE' ? 0 : 1);
  return [...scripts].sort((a, b) => rank(a) - rank(b) || a.order - b.order);
}

/** Orders 1, 2, 3 within each phase, keeping the current sequence. */
export function renumberScripts(scripts: SetupScript[]): SetupScript[] {
  const next: Record<SetupScriptPhase, number> = { WinPE: 0, WindowsSetup: 0 };
  return orderedScripts(scripts).map((s) => ({ ...s, order: ++next[s.runs] }));
}

/** Swaps a script with its neighbour in the same phase; a move past either end does nothing. */
export function moveScript(scripts: SetupScript[], index: number, direction: -1 | 1): SetupScript[] {
  const list = orderedScripts(scripts);
  const target = index + direction;
  if (target < 0 || target >= list.length || list[target].runs !== list[index].runs) return list;
  [list[index], list[target]] = [list[target], list[index]];
  return renumberScripts(list.map((s, i) => ({ ...s, order: i })));
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
  const sentenceLabel = name ? `"${name}"` : 'A script without a name';
  if (!name || name.length > MAX_NAME_LENGTH) problems.push('Every script needs a name of 1 to 100 characters.');
  else if (others.some((o) => o.name.trim().toLowerCase() === name.toLowerCase())) problems.push(`Script names must be unique. Rename ${scriptLabel} and save again.`);
  if (!(TIMEOUT_MINUTES as readonly number[]).includes(script.timeoutMinutes)) problems.push(`The timeout of ${scriptLabel} must be 1 to 30 minutes.`);
  if (script.content.trim().length === 0) problems.push(`${sentenceLabel} is empty. Add the script text.`);
  else if (utf8Length(script.content) > MAX_SCRIPT_BYTES) problems.push(`${sentenceLabel} is larger than 64 KB. Shorten it.`);
  return problems;
}

// ── Answer file ──────────────────────────────────────────────────────────────

/** "2.4 KB" in the answer file row. */
export function answerFileSizeLabel(content: string): string {
  return `${(utf8Length(content) / 1024).toFixed(1)} KB`;
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

/** Entries of the Targeting tab: everything that is not a version. */
export function targetingHistory(entries: SetupProfileHistoryEntry[]): SetupProfileHistoryEntry[] {
  return entries.filter((e) => e.kind !== 'VersionSaved' && e.kind !== 'VersionRestored' && e.kind !== 'Deleted');
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
  WinPEScript: 'WinPE script',
  WindowsSetupScript: 'Windows setup script',
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
 * "1 WinPE script failed", "4 registry values written", "1 script and 5 driver installers queued
 * for Windows setup".
 */
export function itemCountsSummary(counts: ImagingStepItemCount[]): SummaryItem[] {
  const total = (kind: StepItemKind, outcome: StepItemOutcome) =>
    counts.filter((c) => c.kind === kind && c.outcome === outcome).reduce((sum, c) => sum + c.count, 0);
  const nouns: Record<StepItemKind, [string, string]> = {
    RegistryValue: ['registry value', 'registry values'],
    WinPEScript: ['WinPE script', 'WinPE scripts'],
    WindowsSetupScript: ['Windows setup script', 'Windows setup scripts'],
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
  if (queued.length > 0) items.push({ tone: 'queued', text: `${queued.join(' and ')} queued for Windows setup` });
  return items;
}

/** The status column of one item row, for example "Failed: exit code 1" or "Queued for Windows setup". */
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
      return 'Queued for Windows setup';
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
