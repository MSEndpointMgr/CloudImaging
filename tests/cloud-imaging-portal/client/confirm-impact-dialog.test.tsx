import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { ConfirmImpactDialog } from '../../../src/cloud-imaging-portal/client/src/components/ConfirmImpactDialog.tsx';

const copy = { confirmTitle: 'Delete location "Stockholm HQ"?', impact: 'Media prepared for it keeps working.', confirmLabel: 'Delete', destructive: true };

describe('Portal frontend: impact confirmation overlay', () => {
  afterEach(cleanup);

  it('holds only the confirm button while the impact text is still loading', () => {
    render(<ConfirmImpactDialog copy={copy} busy={false} confirmDisabled onCancel={() => undefined} onConfirm={() => undefined} />);

    expect((screen.getByRole('button', { name: 'Delete' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Cancel' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('uses the secondary Cancel and the destructive confirm variants', () => {
    render(<ConfirmImpactDialog copy={copy} busy={false} onCancel={() => undefined} onConfirm={() => undefined} />);

    expect(screen.getByRole('button', { name: 'Cancel' }).className).toContain('bg-secondary');
    expect(screen.getByRole('button', { name: 'Delete' }).className).toContain('bg-destructive');
  });
});
