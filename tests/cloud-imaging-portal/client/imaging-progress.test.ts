import { describe, it, expect } from 'vitest';
import { PIPELINE_STEPS, completionWarnings, currentStepText, imagingDurationMs, imagingStartedAt, progressStepLabel } from '../../../src/cloud-imaging-portal/client/src/lib/imagingProgress.ts';

describe('Portal frontend: imaging progress steps', () => {
  it('lists Install drivers, Prepare setup and Update firmware as the optional steps', () => {
    expect(PIPELINE_STEPS.map((s) => s.name)).toEqual(['FormatDisk', 'DownloadImage', 'ApplyImage', 'InstallDrivers', 'PrepareSetup', 'ConfigureBoot', 'UpdateFirmware', 'ApplyRecoveryImage']);
    expect(PIPELINE_STEPS.filter((s) => s.optional).map((s) => s.label)).toEqual(['Install drivers', 'Prepare setup', 'Update firmware']);
  });

  it('labels the current step, the wait before the first report and an unknown step', () => {
    expect(progressStepLabel('PrepareSetup')).toBe('Prepare setup');
    expect(progressStepLabel('InstallDrivers')).toBe('Install drivers');
    expect(progressStepLabel('ApplyImage')).toBe('Apply Windows image');
    expect(progressStepLabel(null)).toBe('Waiting for first progress report');
    expect(progressStepLabel('FutureStep')).toBe('FutureStep');
  });

  it('shows what the running step waits for, and the step otherwise', () => {
    const waiting = [{ stepName: 'FormatDisk', status: 'InProgress', statusNote: 'Waiting for the AC adapter' }];
    expect(currentStepText('FormatDisk', waiting)).toBe('Waiting for the AC adapter');

    // An empty note is how the device clears the wait.
    expect(currentStepText('FormatDisk', [{ stepName: 'FormatDisk', status: 'InProgress', statusNote: '' }])).toBe('Prepare disk');
    expect(currentStepText('InstallDrivers', [{ stepName: 'InstallDrivers', status: 'Completed', statusNote: 'No onboarded model matches this device.' }])).toBe('Install drivers');
    expect(currentStepText(null, undefined)).toBe('Waiting for first progress report');
  });

  it('starts imaging at the earliest step start, whatever order the steps arrive in', () => {
    const steps = [
      { stepName: 'ApplyImage', status: 'Completed', startedAt: '2026-11-23T00:08:10Z' },
      { stepName: 'FormatDisk', status: 'Completed', startedAt: '2026-11-23T00:02:04Z' },
      // A step reported only on completion has no start.
      { stepName: 'ConfigureBoot', status: 'Completed', startedAt: null },
    ];
    expect(imagingStartedAt(steps)).toBe('2026-11-23T00:02:04Z');
    expect(imagingDurationMs(steps, '2026-11-23T00:26:55Z')).toBe(((24 * 60) + 51) * 1000);
  });

  it('has no start or duration before the first step start or before the session ends', () => {
    expect(imagingStartedAt(undefined)).toBeNull();
    expect(imagingStartedAt([{ stepName: 'FormatDisk', status: 'Completed', startedAt: 'not-a-timestamp' }])).toBeNull();
    expect(imagingDurationMs([], '2026-11-23T00:26:55Z')).toBeNull();
    expect(imagingDurationMs([{ stepName: 'FormatDisk', status: 'InProgress', startedAt: '2026-11-23T00:02:04Z' }], null)).toBeNull();
  });

  it('counts the items that failed in completed steps and names those steps in pipeline order', () => {
    expect(completionWarnings([{ stepName: 'FormatDisk', status: 'Completed', warningCount: 0 }])).toBeNull();
    expect(completionWarnings(null)).toBeNull();
    expect(completionWarnings([{ stepName: 'InstallDrivers', status: 'Completed', warningCount: 1 }])).toEqual({ count: 1, text: '1 item failed in Install drivers.' });
    expect(completionWarnings([
      { stepName: 'PrepareSetup', status: 'Completed', warningCount: 1 },
      // A failed step is the Failed tab's business, not a warning.
      { stepName: 'ApplyImage', status: 'Failed', warningCount: 4 },
      { stepName: 'InstallDrivers', status: 'Completed', warningCount: 2 },
    ])).toEqual({ count: 3, text: '3 items failed in Install drivers and Prepare setup.' });
  });
});
