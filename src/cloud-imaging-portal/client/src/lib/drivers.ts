/**
 * Driver management types, labels and API calls. Types mirror the contracts in
 * src/CloudImaging.Contracts/Models/Drivers; vendor names always come from `vendorLabel` so no
 * page hard-codes a vendor.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { apiFetch, extractErrorDetail } from './apiClient.ts';

export type DriverVendor = 'Hp' | 'Dell' | 'Lenovo';

export type DriverAction = 'Offline' | 'OfflineAndSpecialize' | 'Specialize' | 'Skip';

export type DriverActionSource = 'Locked' | 'ReplacedByCustom' | 'Custom' | 'RolledBack' | 'Override' | 'Rule' | 'Default';

export type WindowsRelease = 'Windows10_22H2' | 'Windows11_23H2' | 'Windows11_24H2' | 'Windows11_25H2';

export type DriverModelState = 'Draft' | 'Active';

export type DriversTab = 'models' | 'rules' | 'sync' | 'downloads' | 'storage';

export const DRIVERS_TABS: readonly { key: DriversTab; label: string }[] = [
  { key: 'models', label: 'Models' },
  { key: 'rules', label: 'Rules' },
  { key: 'sync', label: 'Sync' },
  { key: 'downloads', label: 'Downloads' },
  { key: 'storage', label: 'Storage' },
];

const ACTION_LABELS: Record<DriverAction, string> = {
  Offline: 'Offline',
  OfflineAndSpecialize: 'Offline + Specialize',
  Specialize: 'Specialize',
  Skip: 'Skip',
};

const VENDOR_LABELS: Record<DriverVendor, string> = {
  Hp: 'HP',
  Dell: 'Dell',
  Lenovo: 'Lenovo',
};

const RELEASE_LABELS: Record<WindowsRelease, string> = {
  Windows10_22H2: 'Windows 10, version 22H2',
  Windows11_23H2: 'Windows 11, version 23H2',
  Windows11_24H2: 'Windows 11, version 24H2',
  Windows11_25H2: 'Windows 11, version 25H2',
};

/** Label of a driver action. */
export function actionLabel(action: DriverAction): string {
  return ACTION_LABELS[action];
}

/** Display name of a vendor. */
export function vendorLabel(vendor: DriverVendor): string {
  return VENDOR_LABELS[vendor];
}

/** Label of a Windows release, naming the Windows version. */
export function releaseLabel(release: WindowsRelease): string {
  return RELEASE_LABELS[release];
}

/** The release's version alone, "24H2", for compact badges next to the full name elsewhere. */
export function releaseVersion(release: WindowsRelease): string {
  return release.slice(release.indexOf('_') + 1);
}

/** The tab named in `?tab=`, or Models. */
export function parseDriversTab(value: string | null): DriversTab {
  return DRIVERS_TABS.some((tab) => tab.key === value) ? (value as DriversTab) : 'models';
}

/** Reads JSON from a driver route, turning an error response into an Error with the server's detail. */
export async function driversGet<T>(path: string, fallback: string): Promise<T> {
  const response = await apiFetch(`/api/drivers/${path}`);
  if (!response.ok) {
    throw new Error(await extractErrorDetail(response, fallback));
  }
  return (await response.json()) as T;
}

/** Sends a driver write and reads its JSON answer, or throws with the server's detail. */
export async function driversSend<T>(method: 'POST' | 'PUT' | 'DELETE', path: string, body: unknown, fallback: string): Promise<T> {
  const response = await apiFetch(`/api/drivers/${path}`, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body ?? {}),
  });
  if (!response.ok) {
    throw new Error(await extractErrorDetail(response, fallback));
  }
  // 202 and 204 carry no body.
  const text = response.status === 204 ? '' : await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export type MachineArchitecture = 'X64' | 'Arm64';

export type DriverComponentState = 'Active' | 'WaitingForApproval' | 'Withdrawn';

export interface DriverVendorRow {
  vendor: DriverVendor;
  supported: boolean;
  syncEnabled: boolean;
  onboardedModels: number;
  matchKeyType: string | null;
}

export interface DriverCatalogModel {
  catalogModelKey: string;
  vendor: DriverVendor;
  displayName: string;
  matchKeys: string[];
  architecture: MachineArchitecture;
  releases: WindowsRelease[];
  onboardedModelId: string | null;
}

export interface DriverCatalogModelList {
  fetchedAt: string | null;
  catalogUrl: string;
  stale: boolean;
  warning: string | null;
  models: DriverCatalogModel[];
}

export interface DriverModelPreview {
  catalogModelKey: string;
  displayName: string;
  offline: number;
  offlineAndSpecialize: number;
  specialize: number;
  skip: number;
  sizeBytes: number;
}

export interface DriverPreviewResult {
  models: DriverModelPreview[];
  errors: { catalogModelKey: string; detail: string }[];
}

export interface DriverModelRow {
  modelId: string;
  vendor: DriverVendor;
  displayName: string;
  matchKeys: string[];
  architecture: MachineArchitecture;
  releases: WindowsRelease[];
  state: DriverModelState;
  included: number;
  skipped: number;
  maxDownloadBytes: number;
  status: {
    waitingForApproval: number;
    failedItems: number;
    /** Why the first failed component failed, with its name. */
    failureReason?: string | null;
    syncOff: boolean;
    downloading: { done: number; total: number } | null;
    /** A driver set waits in testing for at least one Windows release. */
    inTesting?: boolean;
  };
}

export interface DriverDetailField {
  label: string;
  value: string;
}

export interface DriverComponentRow {
  lineageKey: string;
  componentId: string;
  name: string;
  category: string | null;
  version: string | null;
  lastUpdated: string | null;
  action: DriverAction;
  allowedActions: DriverAction[];
  source: DriverActionSource;
  ruleName: string | null;
  state: DriverComponentState;
  updatedByLastSync: boolean;
  rollBack?: DriverComponentRollBack | null;
  /** Set on a custom component's row. */
  custom?: { customId: string; replacesName: string | null; uploadedBy: string | null } | null;
  /** Set on a vendor component a custom component replaces on this model. */
  replacedBy?: { customId: string; name: string } | null;
  details: DriverDetailField[];
  descriptionUrl: string | null;
  sizeBytes: number;
}

export interface DriverModelPage {
  model: DriverModelRow;
  release: WindowsRelease;
  checkAvailableAt: string | null;
  checkBlockedReason: 'Draft' | 'SyncOff' | 'Running' | null;
  setVersion: number | null;
  builtAt: string | null;
  hasPreviousSet: boolean;
  previousSetVersion: number | null;
  pendingVersion: number | null;
  pending: { changes: number; buildAt: string } | null;
  /** `queued`: onboarding waits for the running sync, so nothing downloads yet. */
  preparing: { setVersion: number; done: number; total: number; queued?: boolean } | null;
  /** A newer set that waits in testing: only devices an Operator chooses it for get it. */
  testing?: { setVersion: number; since: string | null } | null;
  /** The vendor published a new version of a component a custom component replaces. */
  notices?: DriverCustomNotice[];
  tiles: { offline: number; offlineAndSpecialize: number; specialize: number; skip: number; downloadPerDeviceBytes: number };
  components: DriverComponentRow[];
}

export const WINDOWS_RELEASES: readonly WindowsRelease[] = ['Windows11_25H2', 'Windows11_24H2', 'Windows11_23H2', 'Windows10_22H2'];

export const DRIVER_ACTIONS: readonly DriverAction[] = ['Offline', 'OfflineAndSpecialize', 'Specialize', 'Skip'];

/** Locked to Skip with nothing to approve or remove, so the row offers no choice and leaves the table. */
export function isLockedAway(component: DriverComponentRow): boolean {
  return component.source === 'Locked' && component.state === 'Active';
}

/** "113 MB", "2.41 GB". */
export function formatSize(bytes: number): string {
  if (bytes >= 1_000_000_000) return `${(bytes / 1_000_000_000).toFixed(2)} GB`;
  if (bytes >= 1_000_000) return `${Math.round(bytes / 1_000_000)} MB`;
  if (bytes >= 1_000) return `${Math.round(bytes / 1_000)} KB`;
  return `${bytes} B`;
}

/** Match keys as the header badge shows them: "Baseboard 8C26" or "System SKU 0CB2, 0CB4". */
export function matchKeyLabel(vendor: DriverVendor, keys: string[]): string {
  return `${vendor === 'Dell' ? 'System SKU' : 'Baseboard'} ${keys.join(', ')}`;
}

export const fetchVendors = (): Promise<DriverVendorRow[]> => driversGet('vendors', 'Could not load vendors.');

export const fetchModels = (): Promise<DriverModelRow[]> => driversGet('models', 'Could not load driver models.');

export const fetchModelPage = (modelId: string, release?: WindowsRelease): Promise<DriverModelPage> =>
  driversGet(`models/${encodeURIComponent(modelId)}${release ? `?release=${release}` : ''}`, 'Could not load the model.');

export const fetchCatalogModels = (vendor: DriverVendor): Promise<DriverCatalogModelList> =>
  driversGet(`catalog/models?vendor=${vendor}`, 'Could not load the model list.');

export const previewModels = (vendor: DriverVendor, catalogModelKeys: string[], releases: WindowsRelease[]): Promise<DriverPreviewResult> =>
  driversSend('POST', 'models/preview', { vendor, catalogModelKeys, releases }, 'Could not preview the models.');

export const onboardModels = (vendor: DriverVendor, catalogModelKeys: string[], releases: WindowsRelease[]): Promise<{ modelIds: string[] }> =>
  driversSend('POST', 'models', { vendor, catalogModelKeys, releases }, 'Could not onboard the models.');

export const completeOnboarding = (modelId: string): Promise<DriverModelPage> =>
  driversSend('POST', `models/${encodeURIComponent(modelId)}/complete`, {}, 'Could not complete onboarding.');

export const checkModel = (modelId: string): Promise<{ runId: string }> =>
  driversSend('POST', `models/${encodeURIComponent(modelId)}/check`, {}, 'Could not check for updates.');

export const checkAllModels = (): Promise<{ runId: string }> =>
  driversSend('POST', 'models/check', {}, 'Could not check for updates.');

export const addRelease = (modelId: string, release: WindowsRelease): Promise<DriverModelPage> =>
  driversSend('POST', `models/${encodeURIComponent(modelId)}/releases`, { release }, 'Could not add the Windows release.');

export const removeRelease = (modelId: string, release: WindowsRelease): Promise<void> =>
  driversSend('DELETE', `models/${encodeURIComponent(modelId)}/releases/${release}`, undefined, 'Could not remove the Windows release.');

export const removeModel = (modelId: string): Promise<void> =>
  driversSend('DELETE', `models/${encodeURIComponent(modelId)}`, undefined, 'Could not remove the model.');

export const removeWithdrawnComponent = (modelId: string, release: WindowsRelease, lineageKey: string): Promise<void> =>
  driversSend('DELETE', `models/${encodeURIComponent(modelId)}/releases/${release}/components/${encodeURIComponent(lineageKey)}`, undefined, 'Could not remove the component.');

export const rollBackSet = (modelId: string, release: WindowsRelease): Promise<DriverModelPage> =>
  driversSend('POST', `models/${encodeURIComponent(modelId)}/releases/${release}/rollback`, {}, 'Could not roll back the driver set.');

/** Makes the set in testing the current set for every device. */
export const releaseTestingSet = (modelId: string, release: WindowsRelease): Promise<DriverModelPage> =>
  driversSend('POST', `models/${encodeURIComponent(modelId)}/releases/${release}/testing/release`, {}, 'Could not release the driver set.');

/** What removing a model, or one of its releases, deletes and frees. */
export interface DriverRemovalImpact {
  freedBytes: number;
  sets: number;
  overrides: number;
  runningSessions: number;
}

export const fetchRemovalImpact = (modelId: string, release?: WindowsRelease): Promise<DriverRemovalImpact> =>
  driversGet(`models/${encodeURIComponent(modelId)}/removal-impact${release ? `?release=${release}` : ''}`, 'Could not work out what the removal affects.');

export interface DriverStorageModelRow {
  modelId: string;
  displayName: string;
  vendor: DriverVendor;
  releases: WindowsRelease[];
  sizeBytes: number;
  sets: { current: number; rollBack: number; testing?: number };
}

export interface DriverStorage {
  totalBytes: number;
  vendors: { vendor: DriverVendor; bytes: number }[];
  models: DriverStorageModelRow[];
  previousVersionsToKeep: number;
  previousVersionsBytes: number;
}

export type DriverStoredStatus = 'Stored' | 'Downloading' | 'Failed';

export interface DriverStorageComponent {
  lineageKey: string;
  componentId: string;
  name: string;
  version: string | null;
  sizeBytes: number;
  /** Set versions, `next` for a set being prepared, `previous` for a kept previous version. */
  sets: string[];
  sharedWithModels: number;
  status: DriverStoredStatus;
  reason: string | null;
  downloadedAt: string | null;
}

export interface DriverStorageRelease {
  release: WindowsRelease;
  current: { version: number; builtAt: string | null } | null;
  rollBack: { version: number; builtAt: string | null } | null;
  testing?: { version: number; builtAt: string | null } | null;
  componentCount: number;
  bytes: number;
  components: DriverStorageComponent[];
}

export interface DriverStorageModel {
  modelId: string;
  releases: DriverStorageRelease[];
}

export const PREVIOUS_VERSION_OPTIONS: readonly { value: string; label: string }[] = [
  { value: '0', label: 'Keep none' },
  { value: '1', label: 'Keep 1 previous version' },
  { value: '2', label: 'Keep 2 previous versions' },
  { value: '3', label: 'Keep 3 previous versions' },
];

/** "2 current, 2 roll-back", with ", 1 in testing" when any set waits in testing. */
export function setCountsLabel(sets: { current: number; rollBack: number; testing?: number }): string {
  return `${sets.current} current, ${sets.rollBack} roll-back${sets.testing ? `, ${sets.testing} in testing` : ''}`;
}

/** "This model only" or "3 models" (this model and the others sharing the file). */
export function sharedWithLabel(otherModels: number): string {
  return otherModels === 0 ? 'This model only' : `${otherModels + 1} models`;
}

/** A Driver sets pill: the set number, "Previous version" or "Next set". */
export function setPillLabel(set: string): string {
  if (set === 'previous') return 'Previous version';
  if (set === 'next') return 'Next set';
  return set;
}

export const fetchStorage = (): Promise<DriverStorage> => driversGet('storage', 'Could not load storage.');

export const setPreviousVersionsToKeep = (previousVersionsToKeep: number): Promise<DriverStorage> =>
  driversSend('PUT', 'storage/settings', { previousVersionsToKeep }, 'Could not save the setting.');

export const fetchStorageModel = (modelId: string): Promise<DriverStorageModel> =>
  driversGet(`storage/models/${encodeURIComponent(modelId)}`, 'Could not load the stored components.');

export interface DriverDownloadInProgress {
  modelId: string;
  modelName: string;
  release: WindowsRelease;
  setVersion: number;
  components: number;
  componentsDone: number;
  bytes: number;
  bytesDone: number;
  status: 'Downloading' | 'Waiting' | 'Failed';
  failed: number;
  reasons: string[];
  startedAt: string;
}

export interface DriverDownloadFinished {
  modelId: string;
  modelName: string;
  release: WindowsRelease;
  setVersion: number;
  newComponents: number;
  reusedComponents: number;
  sizeBytes: number;
  readyAt: string;
}

export interface DriverDownloads {
  inProgress: DriverDownloadInProgress[];
  finished: DriverDownloadFinished[];
}

export const fetchDownloads = (): Promise<DriverDownloads> => driversGet('downloads', 'Could not load downloads.');

export type DriverRollBackUntil = 'NewerVersion' | 'Undone';

/** A component held at an earlier version. */
export interface DriverComponentRollBack {
  version: string;
  until: DriverRollBackUntil;
  skippedVersion: string | null;
  changedBy: string | null;
  changedAt: string;
}

export type DriverVersionStatus = 'Stored' | 'DownloadsOnRollBack' | 'Unavailable';

/** One version in a component's history, newest first. */
export interface DriverComponentVersion {
  componentId: string;
  version: string;
  firstSet: number;
  lastSet: number | null;
  from: string;
  to: string | null;
  change: DriverVersionChange;
  /** Who made the change; null for a sync. */
  changedBy: string | null;
  origin: 'Vendor' | 'Custom' | 'RolledBack';
  status: DriverVersionStatus;
  sizeBytes: number;
}

export type DriverVersionChange = 'Onboarding' | 'Sync' | 'RollBack' | 'Custom';

export const VERSION_CHANGE_LABELS: Record<DriverVersionChange, string> = {
  Onboarding: 'Onboard',
  Sync: 'Sync',
  RollBack: 'Roll back',
  Custom: 'Custom component',
};

/** "52 to 53", or "52" when one set used it. */
export function setRangeLabel(version: Pick<DriverComponentVersion, 'firstSet' | 'lastSet'>, currentSet: number | null): string {
  const last = version.lastSet ?? currentSet ?? version.firstSet;
  return last === version.firstSet ? String(version.firstSet) : `${version.firstSet} to ${last}`;
}

/** The Source column tooltip of a rolled back component. */
export function rollBackText(rollBack: DriverComponentRollBack, vendor: DriverVendor): string {
  const who = rollBack.changedBy ? ` by ${rollBack.changedBy}` : '';
  const skipping = rollBack.skippedVersion ? `, skipping ${rollBack.skippedVersion}` : '';
  const when = rollBack.changedAt.slice(0, 10);
  const next = rollBack.until === 'NewerVersion' && rollBack.skippedVersion
    ? `Moves forward when ${vendorLabel(vendor)} publishes a version newer than ${rollBack.skippedVersion}.`
    : 'Stays until the roll back is undone.';
  return `Rolled back to ${rollBack.version}${who} on ${when}${skipping}. ${next}`;
}

const componentPath = (modelId: string, release: WindowsRelease, lineageKey: string): string =>
  `models/${encodeURIComponent(modelId)}/releases/${release}/components/${encodeURIComponent(lineageKey)}`;

export const fetchComponentVersions = (modelId: string, release: WindowsRelease, lineageKey: string, check = false): Promise<DriverComponentVersion[]> =>
  driversGet(`${componentPath(modelId, release, lineageKey)}/versions${check ? '?check=true' : ''}`, 'Could not load the version history.');

export const rollBackComponent = (modelId: string, release: WindowsRelease, lineageKey: string, version: string, until: DriverRollBackUntil): Promise<DriverComponentRow> =>
  driversSend('PUT', `${componentPath(modelId, release, lineageKey)}/rollback`, { version, until }, 'Could not roll back the component.');

export const undoRollBack = (modelId: string, release: WindowsRelease, lineageKey: string): Promise<DriverComponentRow> =>
  driversSend('DELETE', `${componentPath(modelId, release, lineageKey)}/rollback`, undefined, 'Could not undo the roll back.');

export interface DriverCustomNotice {
  customId: string;
  name: string;
  vendorComponentName: string;
  componentId: string;
  version: string | null;
  releasedAt: string | null;
}

export interface DriverCustomComponent {
  customId: string;
  name: string;
  version: string;
  category: string;
  action: DriverAction;
  fileName: string;
  sizeBytes: number;
  infCount: number;
  installCommand: string | null;
  uploadedBy: string | null;
  uploadedAt: string;
  targets: { modelId: string; modelName: string | null; replacesLineageKey: string | null; replacesName: string | null }[];
}

export interface DriverCustomComponentInput {
  uploadId?: string;
  name: string;
  version: string;
  action: DriverAction;
  category: string;
  installCommand: string | null;
  targets: { modelId: string; replacesLineageKey: string | null }[];
}

/** A write Core refused, with the reason per form field. */
export class DriverFieldError extends Error {
  readonly fields: Record<string, string>;

  constructor(message: string, fields: Record<string, string>) {
    super(message);
    this.fields = fields;
  }
}

export const CUSTOM_ACTIONS: readonly DriverAction[] = ['Offline', 'OfflineAndSpecialize', 'Specialize'];

export const startCustomUpload = (fileName: string, sizeBytes: number): Promise<{ uploadId: string; uploadUrl: string; blockSize: number }> =>
  driversSend('POST', 'custom-components/uploads/start', { fileName, sizeBytes }, 'Could not start the upload.');

export const completeCustomUpload = (uploadId: string, blockIds: string[]): Promise<{ sha256: string; sizeBytes: number; infCount: number }> =>
  driversSend('POST', `custom-components/uploads/${encodeURIComponent(uploadId)}/complete`, { blockIds }, 'Could not check the package.');

export const abandonCustomUpload = (uploadId: string): Promise<void> =>
  driversSend('POST', `custom-components/uploads/${encodeURIComponent(uploadId)}/abandon`, {}, 'Could not delete the upload.');

export const fetchCustomComponents = (): Promise<DriverCustomComponent[]> => driversGet('custom-components', 'Could not load custom components.');

/** Adds a custom component; field errors from Core come back as a {@link DriverFieldError}. */
export async function createCustomComponent(input: DriverCustomComponentInput): Promise<DriverCustomComponent> {
  const response = await apiFetch('/api/drivers/custom-components', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  if (!response.ok) {
    const body = await response.clone().json().catch(() => null) as { errors?: { field?: string; detail?: string }[] } | null;
    const fields = Object.fromEntries((body?.errors ?? []).filter(e => e.field && e.detail).map(e => [e.field as string, e.detail as string]));
    throw new DriverFieldError(await extractErrorDetail(response, 'Could not add the custom component.'), fields);
  }
  return (await response.json()) as DriverCustomComponent;
}

export const deleteCustomComponent = (customId: string): Promise<void> =>
  driversSend('DELETE', `custom-components/${encodeURIComponent(customId)}`, undefined, 'Could not remove the custom component.');

export const dismissCustomNotice = (customId: string, modelId: string): Promise<void> =>
  driversSend('POST', `custom-components/${encodeURIComponent(customId)}/notices/${encodeURIComponent(modelId)}/dismiss`, {}, 'Could not dismiss the notice.');

/** Sizes in the Driver set column: "2.41 GB", "113 MB". */
export const formatBytes = formatSize;

export type DriverSkipReason = 'NoMatchingModel' | 'NoWindowsRelease' | 'ReleaseNotOnboarded' | 'NoDriverSet' | 'ArchitectureMismatch' | 'NoneChosen';

export interface DriverAssignmentOption {
  modelId: string;
  vendor: DriverVendor;
  displayName: string;
  maxDownloadBytes: number;
  /** The model's set in testing rather than its current set; offered to Operators only. */
  testing?: boolean;
}

export interface DriverAssignmentPreview {
  release: WindowsRelease | null;
  architecture: MachineArchitecture;
  options: DriverAssignmentOption[];
  sessions: { sessionId: string; matchedModelId: string | null; testingModelId?: string | null; skipReason: DriverSkipReason | null; matchKey: string | null }[];
}

/** A manual choice in the Driver set column: a model's id, `testing:` and a model's id for its set in testing, or no drivers. */
export const NO_DRIVERS = 'none';

const TESTING_PREFIX = 'testing:';

/** The column value of an option. */
export function optionValue(option: DriverAssignmentOption): string {
  return option.testing ? `${TESTING_PREFIX}${option.modelId}` : option.modelId;
}

/** "HP EliteBook 840 G11", or "HP EliteBook 840 G11 (testing)" for its set in testing. */
export function optionLabel(option: DriverAssignmentOption): string {
  return option.testing ? `${option.displayName} (testing)` : option.displayName;
}

/** A choice as Start imaging sends it. */
export interface DriverChoice {
  sessionId: string;
  kind: 'Model' | 'Testing' | 'None';
  modelId?: string;
}

/** Turns a column value into the choice Start imaging sends. */
export function choiceFor(sessionId: string, value: string): DriverChoice {
  if (value === NO_DRIVERS) return { sessionId, kind: 'None' };
  return value.startsWith(TESTING_PREFIX) ? { sessionId, kind: 'Testing', modelId: value.slice(TESTING_PREFIX.length) } : { sessionId, kind: 'Model', modelId: value };
}

/**
 * Why a coupled device has no matched set, with its match key: "System ID 8D41 is not onboarded
 * for Windows 11, version 24H2." Dell devices are named by system SKU. `testingName` is the
 * matched model when its only set is in testing and the user may choose it.
 */
export function noMatchText(
  session: { skipReason: DriverSkipReason | null; matchKey: string | null },
  manufacturer: string,
  release: WindowsRelease,
  imageArchitecture: MachineArchitecture,
  testingName?: string,
): string {
  const keyName = /dell/i.test(manufacturer) ? 'System SKU' : 'System ID';
  switch (session.skipReason) {
    case 'ArchitectureMismatch': {
      const image = imageArchitecture === 'Arm64' ? 'ARM64' : 'x64';
      const model = imageArchitecture === 'Arm64' ? 'x64' : 'ARM64';
      return `This model is onboarded for ${model}, the OS image is ${image}.`;
    }
    case 'ReleaseNotOnboarded':
    case 'NoDriverSet':
      return testingName
        ? `The driver set of ${testingName} for ${releaseLabel(release)} is in testing. Choose ${testingName} (testing) to use it.`
        : `This model has no driver set for ${releaseLabel(release)} yet.`;
    default:
      return session.matchKey
        ? `${keyName} ${session.matchKey} is not onboarded for ${releaseLabel(release)}.`
        : `No ${keyName.toLowerCase()} was read from this device.`;
  }
}

export async function fetchAssignmentPreview(osImageId: string, sessionIds: string[], signal?: AbortSignal): Promise<DriverAssignmentPreview> {
  const response = await apiFetch('/api/drivers/assignment-preview', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ osImageId, sessionIds }),
    signal,
  });
  if (!response.ok) {
    throw new Error(await extractErrorDetail(response, 'Driver sets could not be loaded.'));
  }
  return (await response.json()) as DriverAssignmentPreview;
}

/** Wait after the last change of image or devices before the preview is read. */
export const PREVIEW_DEBOUNCE_MS = 300;

/**
 * The Driver set column's preview for the selected OS image and coupled devices: read
 * {@link PREVIEW_DEBOUNCE_MS} after the last change, the previous request aborted.
 */
export function useDriverAssignmentPreview(osImageId: string | null, sessionIds: string[]): {
  preview: DriverAssignmentPreview | null;
  loading: boolean;
  error: string | null;
  refetch: () => void;
} {
  const [preview, setPreview] = useState<DriverAssignmentPreview | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const key = sessionIds.join(',');
  const controller = useRef<AbortController | null>(null);

  useEffect(() => {
    controller.current?.abort();
    if (!osImageId || key === '') {
      setPreview(null);
      setLoading(false);
      setError(null);
      return undefined;
    }
    setLoading(true);
    setError(null);
    const abort = new AbortController();
    controller.current = abort;
    const timer = window.setTimeout(() => {
      fetchAssignmentPreview(osImageId, key.split(','), abort.signal)
        .then(loaded => { if (!abort.signal.aborted) { setPreview(loaded); setLoading(false); } })
        .catch((err: unknown) => {
          if (abort.signal.aborted) return;
          setPreview(null);
          setError(err instanceof Error ? err.message : 'Driver sets could not be loaded.');
          setLoading(false);
        });
    }, PREVIEW_DEBOUNCE_MS);
    return () => { window.clearTimeout(timer); abort.abort(); };
  }, [osImageId, key, attempt]);

  const refetch = useCallback(() => setAttempt(n => n + 1), []);
  return { preview, loading, error, refetch };
}

/** "1.1 of 2.5 GB": both sizes in the unit of the larger one. */
export function sizeOfLabel(done: number, total: number): string {
  const [unit, divisor] = total >= 1_000_000_000 ? ['GB', 1_000_000_000] : total >= 1_000_000 ? ['MB', 1_000_000] : ['KB', 1_000];
  const digits = unit === 'GB' ? 1 : 0;
  return `${(done / divisor).toFixed(digits)} of ${(total / divisor).toFixed(digits)} ${unit}`;
}

export type DriverRuleScope = 'AllVendors' | 'Hp' | 'Dell';

export type DriverRuleMatch = 'Category' | 'NamePattern' | 'ComponentId';

export interface DriverRule {
  ruleId: string;
  name: string;
  scope: DriverRuleScope;
  match: DriverRuleMatch;
  value: string;
  action: DriverAction;
  changedAt: string;
  changedBy: string | null;
  hits: number;
}

export type DriverRuleInput = Pick<DriverRule, 'name' | 'scope' | 'match' | 'value' | 'action'>;

export interface DriverCategory {
  vendor: DriverVendor;
  category: string;
  critical: boolean;
}

export const RULE_MATCHES: readonly { value: DriverRuleMatch; label: string }[] = [
  { value: 'Category', label: 'Category' },
  { value: 'NamePattern', label: 'Name pattern' },
  { value: 'ComponentId', label: 'Component ID' },
];

/** "All vendors", or the vendor's name. */
export function scopeLabel(scope: DriverRuleScope): string {
  return scope === 'AllVendors' ? 'All vendors' : vendorLabel(scope);
}

/** "Name: HP Services Scan*", "Category: Driver - Enabling", "ID: sp150123". */
export function ruleMatchLabel(rule: Pick<DriverRule, 'match' | 'value'>): string {
  const kind = rule.match === 'NamePattern' ? 'Name' : rule.match === 'ComponentId' ? 'ID' : 'Category';
  return `${kind}: ${rule.value}`;
}

/** The same field rules Imaging Core applies, so most problems show before anything is sent. */
export function validateRule(rule: DriverRuleInput, others: DriverRule[]): Partial<Record<'name' | 'value', string>> {
  const problems: Partial<Record<'name' | 'value', string>> = {};
  const name = rule.name.trim();
  const value = rule.value.trim();
  if (name.length === 0 || name.length > 100) problems.name = 'Enter a name of 1 to 100 characters.';
  else if (others.some(o => o.name.toLowerCase() === name.toLowerCase())) problems.name = `A rule named "${name}" already exists.`;
  if (rule.match === 'Category' && (value.length === 0 || value.length > 200)) problems.value = 'Choose a category.';
  if (rule.match === 'NamePattern') {
    if (value.length === 0 || value.length > 200) problems.value = 'Enter a pattern of 1 to 200 characters. Use * as a wildcard.';
    else if (value.includes('?')) problems.value = 'Use * as the only wildcard.';
  }
  if (rule.match === 'ComponentId' && (value.length === 0 || value.length > 80 || value.includes('*'))) {
    problems.value = 'Enter a component ID of 1 to 80 characters, without wildcards.';
  }
  return problems;
}

const pairPath = (modelId: string, release: WindowsRelease): string => `models/${encodeURIComponent(modelId)}/releases/${release}`;

export const setOverride = (modelId: string, release: WindowsRelease, lineageKey: string, action: DriverAction): Promise<DriverComponentRow> =>
  driversSend('PUT', `${pairPath(modelId, release)}/overrides/${encodeURIComponent(lineageKey)}`, { action }, 'Could not change the action.');

export const clearOverride = (modelId: string, release: WindowsRelease, lineageKey: string): Promise<DriverComponentRow> =>
  driversSend('DELETE', `${pairPath(modelId, release)}/overrides/${encodeURIComponent(lineageKey)}`, undefined, 'Could not undo the override.');

export const buildNow = (modelId: string, release: WindowsRelease): Promise<void> =>
  driversSend('POST', `${pairPath(modelId, release)}/build`, {}, 'Could not build the driver set.');

export const approveComponents = (modelId: string, release: WindowsRelease, lineageKeys: string[]): Promise<DriverModelPage> =>
  driversSend('POST', `${pairPath(modelId, release)}/approvals`, { lineageKeys }, 'Could not approve the component.');

export const fetchRules = (): Promise<DriverRule[]> => driversGet('rules', 'Could not load rules.');

export const fetchRuleSuggestions = (): Promise<DriverRule[]> => driversGet('rules/suggestions', 'Could not load suggested rules.');

export const createRule = (rule: DriverRuleInput): Promise<DriverRule> => driversSend('POST', 'rules', rule, 'Could not add the rule.');

export const updateRule = (ruleId: string, rule: DriverRuleInput): Promise<DriverRule> =>
  driversSend('PUT', `rules/${encodeURIComponent(ruleId)}`, rule, 'Could not save the rule.');

export const deleteRule = (ruleId: string): Promise<void> => driversSend('DELETE', `rules/${encodeURIComponent(ruleId)}`, undefined, 'Could not delete the rule.');

export const fetchCategories = (vendor?: DriverVendor): Promise<DriverCategory[]> =>
  driversGet(vendor ? `categories?vendor=${vendor}` : 'categories', 'Could not load categories.');

export const setCriticalCategories = (vendor: DriverVendor, categories: string[]): Promise<DriverCategory[]> =>
  driversSend('PUT', `critical-categories/${vendor}`, { categories }, 'Could not save the critical categories.');

export type DriverSyncSchedule = 'Daily' | 'SelectedDays' | 'Manual';

export type DriverSyncTrigger = 'Scheduled' | 'Manual' | 'Check' | 'ModelCheck' | 'Onboarding';

export type DriverSyncResult = 'Running' | 'Completed' | 'PartlyFailed' | 'Failed';

export type DriverSyncItemOutcome = 'Updated' | 'New' | 'NewWaitingForApproval' | 'Withdrawn' | 'Removed' | 'Failed';

export interface DriverTimeZone {
  ianaId: string;
  displayName: string;
  covers: string[];
}

export interface DriverSyncSettingsInput {
  schedule: DriverSyncSchedule;
  weekdays: number;
  repeatWeeks: number;
  localTime: string;
  timeZone: string;
  waitForApprovalOfNewComponents: boolean;
  /** Newer driver sets wait in testing until an Operator releases them. */
  waitForTesting: boolean;
}

export interface DriverSyncSettings extends DriverSyncSettingsInput {
  /** Model and release pairs with a set in testing; switching to Use automatically releases them. */
  setsInTesting?: number;
  nextRunAt: string | null;
  syncNowAvailableAt: string | null;
  lastRunStartedAt: string | null;
  changedAt: string | null;
  timeZones: DriverTimeZone[];
}

export interface DriverSyncRun {
  runId: string;
  trigger: DriverSyncTrigger;
  modelId: string | null;
  modelName: string | null;
  startedAt: string;
  finishedAt: string | null;
  result: DriverSyncResult;
  startedBy: string | null;
  counts: Partial<Record<DriverSyncItemOutcome, number>>;
  vendors: { vendor: DriverVendor; processed: boolean; failureReason: string | null; newModels: number }[];
  /** Vendors whose catalogs the run set out to read; one missing from `vendors` was never read. */
  plannedVendors?: DriverVendor[];
  modelsTotal?: number;
  modelsChecked?: number;
  downloadedComponents?: number;
  downloadedBytes?: number;
  newSets?: number;
}

export interface DriverSyncRunItem {
  modelId: string | null;
  modelName: string | null;
  release: WindowsRelease | null;
  lineageKey: string | null;
  componentName: string | null;
  oldVersion: string | null;
  newVersion: string | null;
  outcome: DriverSyncItemOutcome;
  reason: string | null;
  at?: string | null;
  vendor?: DriverVendor | null;
  category?: string | null;
}

/** Done and total of one stage of a running sync. The total grows while the sync finds work. */
export interface DriverSyncStage {
  done: number;
  total: number;
}

export interface DriverSyncRunProgress {
  run: DriverSyncRun;
  catalogs: DriverSyncStage;
  models: DriverSyncStage;
  /** Downloads finished, verified or failed; `run.downloadedComponents` counts the verified ones. */
  components: DriverSyncStage;
  driverSets: DriverSyncStage;
  found: Partial<Record<DriverSyncItemOutcome, number>>;
  /** Newest first. */
  latest: DriverSyncRunItem[];
  /** Onboardings that start when this run finishes, in order. */
  queued?: DriverQueuedRun[];
}

export interface DriverQueuedRun {
  modelId: string;
  modelName: string;
  queuedBy: string;
  queuedAt: string;
}

export interface DriverSyncReportRun {
  run: DriverSyncRun;
  items: DriverSyncRunItem[];
}

export const TRIGGER_LABELS: Record<DriverSyncTrigger, string> = {
  Scheduled: 'Schedule',
  Manual: 'Sync now',
  Check: 'Check for updates',
  ModelCheck: 'Check for updates',
  Onboarding: 'Complete onboarding',
};

/** Monday first, as the day toggles show them; bit 0 is Monday. */
export const WEEKDAYS: readonly { bit: number; short: string; long: string }[] = [
  { bit: 1, short: 'Mon', long: 'Monday' },
  { bit: 2, short: 'Tue', long: 'Tuesday' },
  { bit: 4, short: 'Wed', long: 'Wednesday' },
  { bit: 8, short: 'Thu', long: 'Thursday' },
  { bit: 16, short: 'Fri', long: 'Friday' },
  { bit: 32, short: 'Sat', long: 'Saturday' },
  { bit: 64, short: 'Sun', long: 'Sunday' },
];

export const MONDAY_TO_FRIDAY = 31;

export const RESULT_LABELS: Record<DriverSyncResult, string> = {
  Running: 'Running',
  Completed: 'Completed',
  PartlyFailed: 'Partly failed',
  Failed: 'Failed',
};

const OUTCOME_LABELS: Record<DriverSyncItemOutcome, string> = {
  Updated: 'updated',
  New: 'new',
  NewWaitingForApproval: 'waiting for approval',
  Withdrawn: 'withdrawn',
  Removed: 'removed',
  Failed: 'failed',
};

/** "Updated", "Waiting for approval": an item's outcome as a sentence-case label. */
export function outcomeLabel(outcome: DriverSyncItemOutcome): string {
  const label = OUTCOME_LABELS[outcome];
  return label.charAt(0).toUpperCase() + label.slice(1);
}

/** "Monday to Friday" for three or more consecutive days, otherwise "Monday, Wednesday and Friday". */
export function daysPhrase(mask: number): string {
  const days = WEEKDAYS.filter(d => (mask & d.bit) !== 0);
  if (days.length === 0) return '';
  const indexes = days.map(d => WEEKDAYS.indexOf(d));
  const consecutive = indexes.every((index, i) => i === 0 || index === indexes[i - 1] + 1);
  if (days.length >= 3 && consecutive) return `${days[0].long} to ${days[days.length - 1].long}`;
  if (days.length === 1) return days[0].long;
  return `${days.slice(0, -1).map(d => d.long).join(', ')} and ${days[days.length - 1].long}`;
}

/** The next-sync line: "Next sync: Monday 2026-10-26 02:00, then every day." Null for Manually only. */
export function nextSyncLine(settings: Pick<DriverSyncSettings, 'schedule' | 'weekdays' | 'repeatWeeks' | 'timeZone' | 'nextRunAt'>): string | null {
  if (settings.schedule === 'Manual' || !settings.nextRunAt) return null;
  const at = new Date(settings.nextRunAt);
  const parts = Object.fromEntries(new Intl.DateTimeFormat('en-GB', {
    timeZone: settings.timeZone, weekday: 'long', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).formatToParts(at).map(p => [p.type, p.value]));
  const when = `${parts.weekday} ${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`;
  const repeat = settings.schedule === 'Daily'
    ? 'every day'
    : `every ${settings.repeatWeeks === 1 ? 'week' : `${settings.repeatWeeks} weeks`} on ${daysPhrase(settings.weekdays)}`;
  return `Next sync: ${when}, then ${repeat}.`;
}

/** "4 updated, 1 new, 1 failed": counts per outcome, new components counted as new whether or not they wait for approval. Empty when nothing changed. */
export function countsSummary(counts: Partial<Record<DriverSyncItemOutcome, number>>): string {
  const fresh = (counts.New ?? 0) + (counts.NewWaitingForApproval ?? 0);
  const parts: string[] = [];
  if ((counts.Updated ?? 0) > 0) parts.push(`${counts.Updated} updated`);
  if (fresh > 0) parts.push(`${fresh} new`);
  for (const o of ['Withdrawn', 'Removed', 'Failed'] as const) {
    if ((counts[o] ?? 0) > 0) parts.push(`${counts[o]} ${OUTCOME_LABELS[o]}`);
  }
  return parts.join(', ');
}

/** What an onboarding run did when no component changed: it downloaded the first driver set, or found every file already stored. */
export function onboardingSummary(run: DriverSyncRun): string {
  const downloaded = run.downloadedComponents ?? 0;
  if (downloaded === 0) return 'every component was already stored';
  return `${downloaded} ${downloaded === 1 ? 'component' : 'components'} downloaded${(run.downloadedBytes ?? 0) > 0 ? `, ${formatSize(run.downloadedBytes ?? 0)}` : ''}`;
}

/** The History card's one-line summary of a run. */
export function runSummary(run: DriverSyncRun): string {
  let summary = countsSummary(run.counts)
    || (run.result === 'Running' ? 'Running' : run.trigger === 'Onboarding' ? `Onboarded, ${onboardingSummary(run)}` : 'No changes');
  if ((run.trigger === 'ModelCheck' || run.trigger === 'Onboarding') && run.modelName) summary = `${run.modelName}: ${summary}`;
  const newModels = run.vendors.filter(v => v.newModels > 0).map(v => `${vendorLabel(v.vendor)}: ${v.newModels} new ${v.newModels === 1 ? 'model' : 'models'} available`);
  return newModels.length > 0 ? `${summary}. ${newModels.join('. ')}` : summary;
}

/** "40 s", "6 min", "1 h 5 min": seconds under a minute, so a quick run does not read as "0 min". */
export function durationLabel(ms: number): string {
  if (ms < 60_000) return `${Math.max(0, Math.floor(ms / 1_000))} s`;
  const minutes = Math.floor(ms / 60_000);
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** "Available again at 03:00:15" while Sync now and Check for updates wait; null when they can run. */
export function availableAgainText(availableAt: string | null): string | null {
  if (!availableAt || new Date(availableAt).getTime() <= Date.now()) return null;
  const at = new Date(availableAt);
  return `Available again at ${[at.getHours(), at.getMinutes(), at.getSeconds()].map(n => String(n).padStart(2, '0')).join(':')}`;
}

/** The time zone entry whose `covers` includes the browser's zone, otherwise UTC. */
export function browserTimeZone(zones: DriverTimeZone[], browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone): DriverTimeZone | undefined {
  return zones.find(z => z.covers.includes(browserZone)) ?? zones.find(z => z.ianaId === 'Etc/UTC');
}

export const fetchSyncSettings = (): Promise<DriverSyncSettings> => driversGet('sync/settings', 'Could not load the sync settings.');

export const saveSyncSettings = (settings: DriverSyncSettingsInput): Promise<DriverSyncSettings> =>
  driversSend('PUT', 'sync/settings', settings, 'Could not save the sync settings.');

export const syncNow = (): Promise<{ runId: string }> => driversSend('POST', 'sync/runs', {}, 'Could not start the sync.');

export const fetchSyncRuns = (): Promise<DriverSyncRun[]> => driversGet('sync/runs', 'Could not load the sync history.');

export const fetchSyncRunItems = (runId: string): Promise<DriverSyncRunItem[]> =>
  driversGet(`sync/runs/${encodeURIComponent(runId)}/items`, 'Could not load the run.');

export const fetchSyncRunProgress = (runId: string): Promise<DriverSyncRunProgress> =>
  driversGet(`sync/runs/${encodeURIComponent(runId)}/progress`, 'Could not load the progress of the sync.');

/** Synchronizations that started between the two ISO timestamps, newest first, with their changes. */
export const fetchSyncReport = (from: string, to: string): Promise<DriverSyncReportRun[]> =>
  driversGet(`reports/sync?${new URLSearchParams({ from, to }).toString()}`, 'Could not load the synchronizations.');

export const setVendorSync = (vendor: DriverVendor, syncEnabled: boolean): Promise<DriverVendorRow> =>
  driversSend('PUT', `vendors/${vendor}`, { syncEnabled }, 'Could not change the vendor.');
