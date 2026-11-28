import type { ImagingStepItemCount } from './setupProfiles.ts';

export interface ImagingStepDetails {
  stepName: string;
  status: string;
  stepProgressPercent?: number | null;
  startedAt?: string | null;
  completedAt?: string | null;
  errorDetail?: string | null;
  /** Items that failed in a step that still completed, for example under Complete with warnings. */
  warningCount?: number | null;
  itemCounts?: ImagingStepItemCount[] | null;
  /** The device reported more items than are kept. */
  itemsTruncated?: boolean | null;
  /** Note on a step: why it was skipped (for example why no drivers were installed), or what a running step waits for. */
  statusNote?: string | null;
}

/** The driver set pinned on a session when imaging started, or why none applies. */
export interface SessionDriverPin {
  driverModelName?: string | null;
  driverRelease?: string | null;
  /** The image's release when the model had no set for it and `driverRelease` is an earlier release's set. */
  driverFallbackFrom?: string | null;
  driverSetVersion?: number | null;
  driverSkipReason?: string | null;
  driverMatchKey?: string | null;
  driverChosenManually?: boolean;
  driverChosenBy?: string | null;
  /** BIOS version the device is updated to, decided when it registered; null when it gets none. */
  firmwareVersion?: string | null;
  /** BIOS version the device reported when it registered. */
  firmwareInstalledVersion?: string | null;
}

/**
 * Pipeline steps in order. An optional step only appears on the timeline when the device reported
 * it or the session expects it, so a session without a setup profile keeps its five columns.
 */
export const PIPELINE_STEPS: readonly { name: string; label: string; optional: boolean }[] = [
  { name: 'FormatDisk', label: 'Prepare disk', optional: false },
  { name: 'DownloadImage', label: 'Download image', optional: false },
  { name: 'ApplyImage', label: 'Apply Windows image', optional: false },
  { name: 'InstallDrivers', label: 'Install drivers', optional: true },
  { name: 'PrepareSetup', label: 'Prepare setup', optional: true },
  { name: 'ConfigureBoot', label: 'Configure boot', optional: false },
  { name: 'UpdateFirmware', label: 'Update firmware', optional: true },
  { name: 'ApplyRecoveryImage', label: 'Configure recovery', optional: false },
];

/**
 * Shorter labels for the horizontal stepper, where five labels share one row and the full
 * "Apply Windows image" wraps on anything narrower than a wide desktop.
 */
export const PIPELINE_STEP_SHORT_LABELS: Record<string, string> = {
  ApplyImage: 'Apply image',
};

export function progressStepLabel(stepName: string | null): string {
  if (!stepName) return 'Waiting for first progress report';
  return PIPELINE_STEPS.find(step => step.name === stepName)?.label ?? stepName;
}

/** What a running session is doing: the running step's note when it has one (waiting for the AC adapter), otherwise the step. */
export function currentStepText(currentStep: string | null, steps?: readonly ImagingStepDetails[] | null): string {
  const note = steps?.find(step => step.stepName === currentStep && step.status === 'InProgress')?.statusNote;
  return note || progressStepLabel(currentStep);
}

/**
 * When the device began imaging: the earliest step start it reported. The session's createdAt is
 * when it registered, which can be hours earlier while it waits to be coupled.
 */
export function imagingStartedAt(steps?: readonly ImagingStepDetails[] | null): string | null {
  let earliest: string | null = null;
  for (const { startedAt } of steps ?? []) {
    if (!startedAt || Number.isNaN(Date.parse(startedAt))) continue;
    if (earliest === null || Date.parse(startedAt) < Date.parse(earliest)) earliest = startedAt;
  }
  return earliest;
}

/** How long imaging took, from the first step start to the end of the session; null while either is unknown. */
export function imagingDurationMs(steps: readonly ImagingStepDetails[] | null | undefined, terminalAt: string | null | undefined): number | null {
  const startedAt = imagingStartedAt(steps);
  if (!startedAt || !terminalAt) return null;
  const elapsed = Date.parse(terminalAt) - Date.parse(startedAt);
  return Number.isNaN(elapsed) ? null : Math.max(0, elapsed);
}

/**
 * Items that failed in steps that still completed (the timeline's amber knobs), with a sentence
 * naming those steps in pipeline order. Null when every item succeeded.
 */
export function completionWarnings(steps?: readonly ImagingStepDetails[] | null): { count: number; text: string } | null {
  const order = (name: string): number => PIPELINE_STEPS.findIndex(step => step.name === name);
  const warned = (steps ?? [])
    .filter(step => step.status === 'Completed' && (step.warningCount ?? 0) > 0)
    .sort((a, b) => order(a.stepName) - order(b.stepName));
  if (warned.length === 0) return null;

  const count = warned.reduce((total, step) => total + (step.warningCount ?? 0), 0);
  const labels = warned.map(step => progressStepLabel(step.stepName));
  const where = labels.length === 1 ? labels[0] : `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1]}`;
  return { count, text: `${String(count)} ${count === 1 ? 'item' : 'items'} failed in ${where}.` };
}