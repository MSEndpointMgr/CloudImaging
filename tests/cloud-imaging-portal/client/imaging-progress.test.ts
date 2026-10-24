import { describe, it, expect } from 'vitest';
import { PIPELINE_STEPS, progressStepLabel } from '../../../src/cloud-imaging-portal/client/src/lib/imagingProgress.ts';

describe('Portal frontend: imaging progress steps', () => {
  it('lists Prepare setup as the only optional step, between Apply image and Configure boot', () => {
    expect(PIPELINE_STEPS.map((s) => s.name)).toEqual(['FormatDisk', 'DownloadImage', 'ApplyImage', 'PrepareSetup', 'ConfigureBoot', 'ApplyRecoveryImage']);
    expect(PIPELINE_STEPS.filter((s) => s.optional).map((s) => s.label)).toEqual(['Prepare setup']);
  });

  it('labels the current step, the wait before the first report and an unknown step', () => {
    expect(progressStepLabel('PrepareSetup')).toBe('Prepare setup');
    expect(progressStepLabel('ApplyImage')).toBe('Apply Windows image');
    expect(progressStepLabel(null)).toBe('Waiting for first progress report');
    expect(progressStepLabel('InstallDrivers')).toBe('InstallDrivers');
  });
});
