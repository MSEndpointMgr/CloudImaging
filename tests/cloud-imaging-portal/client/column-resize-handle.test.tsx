import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ColumnResizeHandle } from '../../../src/cloud-imaging-portal/client/src/components/ui/column-resize-handle.tsx';

function renderHandle(widthPct: number, onChange = vi.fn(), onReset = vi.fn()) {
  render(
    <table>
      <thead>
        <tr>
          <th>
            <ColumnResizeHandle label="Serial / Session" widthPct={widthPct} minPct={14} maxPct={40} onChange={onChange} onReset={onReset} />
          </th>
        </tr>
      </thead>
    </table>,
  );
  return { handle: screen.getByRole('separator', { name: 'Resize Serial / Session column' }), onChange, onReset };
}

describe('Portal frontend: column resize handle', () => {
  afterEach(cleanup);

  it('exposes an accessible, focusable separator with its current width', () => {
    const { handle } = renderHandle(20);

    expect(handle.getAttribute('aria-orientation')).toBe('vertical');
    expect(handle.getAttribute('aria-valuenow')).toBe('20');
    expect(handle.getAttribute('aria-valuemin')).toBe('14');
    expect(handle.getAttribute('aria-valuemax')).toBe('40');
    expect(handle.getAttribute('tabindex')).toBe('0');
  });

  it('widens and narrows with the arrow keys, clamped to the bounds', () => {
    const { handle, onChange } = renderHandle(39);

    fireEvent.keyDown(handle, { key: 'ArrowRight' });
    fireEvent.keyDown(handle, { key: 'ArrowLeft' });

    expect(onChange).toHaveBeenNthCalledWith(1, 40);
    expect(onChange).toHaveBeenNthCalledWith(2, 37);
  });

  it('resets to the default width on double-click and Home', () => {
    const { handle, onReset } = renderHandle(30);

    fireEvent.doubleClick(handle);
    fireEvent.keyDown(handle, { key: 'Home' });

    expect(onReset).toHaveBeenCalledTimes(2);
  });
});
