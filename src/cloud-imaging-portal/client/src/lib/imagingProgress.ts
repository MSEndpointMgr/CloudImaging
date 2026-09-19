export interface ImagingStepDetails {
  stepName: string;
  status: string;
  stepProgressPercent?: number | null;
  startedAt?: string | null;
  completedAt?: string | null;
  errorDetail?: string | null;
}

export const PIPELINE_STEPS = [
  { name: 'FormatDisk', label: 'Prepare disk' },
  { name: 'DownloadImage', label: 'Download image' },
  { name: 'ApplyImage', label: 'Apply Windows image' },
  { name: 'ConfigureBoot', label: 'Configure boot' },
  { name: 'ApplyRecoveryImage', label: 'Configure recovery' },
] as const;

export function progressStepLabel(stepName: string | null): string {
  if (!stepName) return 'Waiting for first progress report';
  return PIPELINE_STEPS.find(step => step.name === stepName)?.label ?? stepName;
}