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
}

/**
 * Pipeline steps in order. An optional step only appears on the timeline when the device reported
 * it or the session expects it, so a session without a setup profile keeps its five columns.
 */
export const PIPELINE_STEPS: readonly { name: string; label: string; optional: boolean }[] = [
  { name: 'FormatDisk', label: 'Prepare disk', optional: false },
  { name: 'DownloadImage', label: 'Download image', optional: false },
  { name: 'ApplyImage', label: 'Apply Windows image', optional: false },
  { name: 'PrepareSetup', label: 'Prepare setup', optional: true },
  { name: 'ConfigureBoot', label: 'Configure boot', optional: false },
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