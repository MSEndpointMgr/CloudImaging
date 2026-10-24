import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry: vi.fn(),
  extractErrorDetail: vi.fn(),
}));

// A later optional step (for example driver installation) must not need a component change.
vi.mock('../../../src/cloud-imaging-portal/client/src/lib/imagingProgress.ts', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../src/cloud-imaging-portal/client/src/lib/imagingProgress.ts')>();
  const steps = [...actual.PIPELINE_STEPS];
  steps.splice(3, 0, { name: 'InstallDrivers', label: 'Install drivers', optional: true });
  return { ...actual, PIPELINE_STEPS: steps };
});

import { SessionProgressDetails } from '../../../src/cloud-imaging-portal/client/src/components/SessionProgressDetails.tsx';

describe('Portal frontend: deployment timeline columns', () => {
  afterEach(cleanup);

  it('gives every shown step its own column, seven included', () => {
    const { container } = render(
      <SessionProgressDetails
        overallPercent={40}
        currentStep="InstallDrivers"
        setupProfile={{ profileId: 'p1', name: 'Stockholm', version: 2 }}
        steps={[{ stepName: 'InstallDrivers', status: 'InProgress', stepProgressPercent: 10 }]}
      />,
    );

    expect(container.querySelectorAll('ol > li')).toHaveLength(7);
    expect(container.querySelector('ol')?.getAttribute('style')).toContain('repeat(7, minmax(0, 1fr))');
  });
});
