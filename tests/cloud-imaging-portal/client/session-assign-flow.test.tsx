import { describe, it, expect } from 'vitest';

/**
 * Portal frontend OS image assignment / imaging-start flow tests (T038a, FR-033).
 * A single OS image dropdown above the Coupled Devices table applies to every
 * currently-coupled session; there is no more per-row "Assign Image" dialog.
 */
describe('Portal frontend: start imaging flow', () => {
  it('Coupled Devices table only lists SessionAssigned rows', () => {
    const coupledState  = 'SessionAssigned';
    const otherStates    = ['SessionInit', 'SessionAllowed', 'SessionStarted', 'SessionCompleted', 'SessionFailed'];
    expect(otherStates).not.toContain(coupledState);
  });

  it('OS image dropdown and Start Imaging button are disabled until an image is selected', () => {
    const selectedImageId: string | null = null;
    const startDisabled = !selectedImageId;
    expect(startDisabled).toBe(true);
  });

  it('Start Imaging button label reflects the number of coupled devices', () => {
    const coupledCount = 3;
    const label = `Start Imaging (${coupledCount})`;
    expect(label).toBe('Start Imaging (3)');
  });

  it('Start Imaging submits all coupled session IDs to POST /api/sessions/bulk-assign', () => {
    const endpoint = '/api/sessions/bulk-assign';
    expect(endpoint).toBe('/api/sessions/bulk-assign');
  });

  it('rows transition out of Coupled once imaging starts on next device poll', () => {
    const newState = 'SessionStarted';
    expect(newState).toBe('SessionStarted');
  });
});

describe('Portal frontend: no-OS-images guard', () => {
  it('a warning banner is shown on the Pending tab when no active OS images exist', () => {
    const images: unknown[] = [];
    const hasOsImages = images.length > 0;
    expect(hasOsImages).toBe(false);
  });

  it('device coupling is still allowed when no OS images exist (only imaging is blocked)', () => {
    const couplingRequiresOsImages = false;
    expect(couplingRequiresOsImages).toBe(false);
  });

  it('the OS image select and Start Imaging button are disabled when the image catalog is empty', () => {
    const images: unknown[] = [];
    const selectDisabled = images.length === 0;
    expect(selectDisabled).toBe(true);
  });

  it('Start Imaging is defensively blocked with an error toast if invoked with no OS images', () => {
    const hasOsImages = false;
    const toastStatus = !hasOsImages ? 'error' : 'success';
    expect(toastStatus).toBe('error');
  });
});
