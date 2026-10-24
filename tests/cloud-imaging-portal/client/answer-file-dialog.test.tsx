import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import AnswerFileDialog from '../../../src/cloud-imaging-portal/client/src/components/setup-profiles/AnswerFileDialog.tsx';
import { problemHeading } from '../../../src/cloud-imaging-portal/client/src/lib/answerFileValidation.ts';
import { answerFileProblems, answerFileSizeLabel } from '../../../src/cloud-imaging-portal/client/src/lib/setupProfiles.ts';

/**
 * The answer file dialog lists every problem at once with its line, keeps Done disabled while
 * anything is found, and shows the passes of a valid file.
 */
const VALID = '<?xml version="1.0"?>\n<unattend xmlns="urn:schemas-microsoft-com:unattend">\n  <settings pass="specialize"/>\n  <settings pass="oobeSystem"/>\n</unattend>';
const REFUSED = '<?xml version="1.0"?>\n<unattend xmlns="urn:schemas-microsoft-com:unattend">\n  <settings pass="windowsPE"/>\n  <settings pass="specialize"><component name="Microsoft-Windows-UnattendedJoin"/></settings>\n</unattend>';

describe('Portal frontend: answer file dialog', () => {
  afterEach(cleanup);

  it('shows the passes of a valid file and enables Done', () => {
    let applied: [string, string[]] | null = null;
    render(<AnswerFileDialog content={VALID} onClose={() => undefined} onApply={(c, p) => { applied = [c, p]; }} />);

    expect(screen.getByText('Edit answer file')).toBeTruthy();
    expect(screen.getByText(/Valid\. Passes: specialize, oobeSystem\./)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(applied).toEqual([VALID, ['specialize', 'oobeSystem']]);
  });

  it('lists every problem with its line and keeps Done disabled', async () => {
    render(<AnswerFileDialog content="" onClose={() => undefined} onApply={() => undefined} />);
    expect(screen.getByText('Add answer file')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('unattend.xml', { selector: 'textarea' }), { target: { value: REFUSED } });

    expect(await screen.findByText('2 settings would break the deployment')).toBeTruthy();
    expect(screen.getByText(/^Line 3: The windowsPE pass never runs/)).toBeTruthy();
    expect(screen.getByText(/^Line 4: Domain join is not supported/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Done' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows the problems Imaging Core returned until the text changes', async () => {
    render(<AnswerFileDialog content={VALID} serverProblems={[{ code: 'autoLogon', line: 9, message: 'AutoLogon signs in a local account before Autopilot can run. Remove the AutoLogon element.' }]} onClose={() => undefined} onApply={() => undefined} />);

    expect(screen.getByText(/^Line 9: AutoLogon/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText('unattend.xml', { selector: 'textarea' }), { target: { value: `${VALID}\n` } });
    await waitFor(() => expect(screen.queryByText(/^Line 9: AutoLogon/)).toBeNull());
  });

  it('names a file that cannot be used at all', () => {
    expect(problemHeading([{ code: 'notWellFormed' }])).toBe('This file cannot be used');
    expect(problemHeading([{ code: 'autoLogon' }])).toBe('1 setting would break the deployment');
  });

  it('formats the row size and reads server problems without a line as the whole file', () => {
    expect(answerFileSizeLabel('x'.repeat(2458))).toBe('2.4 KB');
    expect(answerFileProblems([{ code: 'tooLarge', line: null, message: 'm' }])).toEqual([{ code: 'tooLarge', line: 0, message: 'm' }]);
  });

  const uploadFile = (file: File) =>
    fireEvent.change(document.querySelector('input[type="file"]')!, { target: { files: [file] } });

  it('refuses an uploaded file over 256 KB, even when its line endings would shrink it', async () => {
    render(<AnswerFileDialog content="" onClose={() => undefined} onApply={() => undefined} />);
    const crlf = `<!--${'x\r\n'.repeat(90_000)}-->\r\n${VALID}`;

    uploadFile(new File([crlf], 'unattend.xml'));

    expect(await screen.findByText('The file is larger than 256 KB. Choose a smaller answer file.')).toBeTruthy();
  });

  it('accepts a 256 KB upload with a byte order mark, which does not count', async () => {
    render(<AnswerFileDialog content="" onClose={() => undefined} onApply={() => undefined} />);
    const padded = VALID + ' '.repeat(262_144 - new TextEncoder().encode(VALID).length);

    uploadFile(new File([new Uint8Array([0xef, 0xbb, 0xbf]), padded], 'unattend.xml'));

    expect(await screen.findByText(/Valid\. Passes: specialize, oobeSystem\./)).toBeTruthy();
  });
});
