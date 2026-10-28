import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry: vi.fn(),
  extractErrorDetail: vi.fn(),
}));

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
