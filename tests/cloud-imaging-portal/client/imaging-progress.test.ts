import { describe, it, expect } from 'vitest';
import { PIPELINE_STEPS, progressStepLabel } from '../../../src/cloud-imaging-portal/client/src/lib/imagingProgress.ts';

describe('Portal frontend: imaging progress steps', () => {
  it('lists Install drivers and Prepare setup as the optional steps, between Apply image and Configure boot', () => {
    expect(PIPELINE_STEPS.map((s) => s.name)).toEqual(['FormatDisk', 'DownloadImage', 'ApplyImage', 'InstallDrivers', 'PrepareSetup', 'ConfigureBoot', 'ApplyRecoveryImage']);
    expect(PIPELINE_STEPS.filter((s) => s.optional).map((s) => s.label)).toEqual(['Install drivers', 'Prepare setup']);
  });

  it('labels the current step, the wait before the first report and an unknown step', () => {
    expect(progressStepLabel('PrepareSetup')).toBe('Prepare setup');
    expect(progressStepLabel('InstallDrivers')).toBe('Install drivers');
    expect(progressStepLabel('ApplyImage')).toBe('Apply Windows image');
    expect(progressStepLabel(null)).toBe('Waiting for first progress report');
    expect(progressStepLabel('FutureStep')).toBe('FutureStep');
  });
});
