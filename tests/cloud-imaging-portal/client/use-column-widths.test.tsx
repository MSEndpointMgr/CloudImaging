import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useColumnWidths } from '../../../src/cloud-imaging-portal/client/src/lib/useColumnWidths.tsx';

function Harness() {
  const cols = useColumnWidths({ serial: 20, device: 30, step: 50 });
  return (
    <table>
      <thead>
        <tr>
          <th data-testid="serial" style={cols.style('serial')}>{cols.handle('serial', 'Serial')}</th>
          <th data-testid="device" style={cols.style('device')}>{cols.handle('device', 'Device')}</th>
          <th data-testid="step" style={cols.style('step')}>{cols.handle('step', 'Step')}</th>
        </tr>
      </thead>
    </table>
  );
}

const width = (id: string) => screen.getByTestId(id).style.width;

describe('Portal frontend: useColumnWidths', () => {
  afterEach(cleanup);

  it('gives every column except the last a resize handle', () => {
    render(<Harness />);

    expect(screen.getAllByRole('separator')).toHaveLength(2);
    expect(screen.queryByRole('separator', { name: 'Resize Step column' })).toBeNull();
  });

  it('trades width with the right-hand neighbour so the row total is unchanged', () => {
    render(<Harness />);

    fireEvent.keyDown(screen.getByRole('separator', { name: 'Resize Serial column' }), { key: 'ArrowRight' });

    expect(width('serial')).toBe('22%');
    expect(width('device')).toBe('28%');
    expect(width('step')).toBe('50%');
  });

  it('restores every default width on reset', () => {
    render(<Harness />);
    const deviceHandle = screen.getByRole('separator', { name: 'Resize Device column' });

    fireEvent.keyDown(deviceHandle, { key: 'ArrowLeft' });
    fireEvent.doubleClick(deviceHandle);

    expect(width('device')).toBe('30%');
    expect(width('step')).toBe('50%');
  });
});
