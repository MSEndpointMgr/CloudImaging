import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import AnswerFileDialog from '../../../src/cloud-imaging-portal/client/src/components/setup-profiles/AnswerFileDialog.tsx';
import { problemHeading } from '../../../src/cloud-imaging-portal/client/src/lib/answerFileValidation.ts';
import { SetupProfileApiError, answerFileProblems, answerFileSizeLabel, type SetupAnswerFile } from '../../../src/cloud-imaging-portal/client/src/lib/setupProfiles.ts';

/**
 * The answer file dialog loads the stored text, lists every problem at once with its line, keeps
 * Done disabled while anything is found, uploads the text on Done and shows the problems Imaging
 * Core still finds.
 */
const VALID = '<?xml version="1.0"?>\n<unattend xmlns="urn:schemas-microsoft-com:unattend">\n  <settings pass="specialize"/>\n  <settings pass="oobeSystem"/>\n</unattend>';
const REFUSED = '<?xml version="1.0"?>\n<unattend xmlns="urn:schemas-microsoft-com:unattend">\n  <settings pass="windowsPE"/>\n  <settings pass="specialize"><component name="Microsoft-Windows-UnattendedJoin"/></settings>\n</unattend>';
const STORED: SetupAnswerFile = { contentSha256: 'a'.repeat(64), sizeBytes: 152, passes: ['specialize', 'oobeSystem'] };
const textarea = () => screen.getByLabelText('unattend.xml', { selector: 'textarea' });
const done = () => screen.getByRole('button', { name: 'Done' }) as HTMLButtonElement;
const unused = () => Promise.reject(new Error('unused'));

describe('Portal frontend: answer file dialog', () => {
  afterEach(cleanup);

  it('loads the stored file, shows its passes, and applies it unchanged without uploading', async () => {
    const upload = vi.fn();
    const onApply = vi.fn();
    render(<AnswerFileDialog answerFile={STORED} loadText={() => Promise.resolve(VALID)} upload={upload} onClose={() => undefined} onApply={onApply} />);

    expect(screen.getByText('Loading the answer file')).toBeTruthy();
    expect(await screen.findByText(/Valid\. Passes: specialize, oobeSystem\./)).toBeTruthy();
    expect(screen.getByText('Edit answer file')).toBeTruthy();
    fireEvent.click(done());

    expect(upload).not.toHaveBeenCalled();
    expect(onApply).toHaveBeenCalledWith(STORED, VALID);
  });

  it('uploads a changed file on Done and applies the stored reference', async () => {
    const upload = vi.fn().mockResolvedValue({ sha256: 'b'.repeat(64), sizeBytes: 153, passes: ['specialize', 'oobeSystem'], problems: [] });
    const onApply = vi.fn();
    render(<AnswerFileDialog answerFile={null} loadText={unused} upload={upload} onClose={() => undefined} onApply={onApply} />);
    expect(screen.getByText('Add answer file')).toBeTruthy();

    fireEvent.change(textarea(), { target: { value: VALID } });
    await waitFor(() => expect(done().disabled).toBe(false));
    fireEvent.click(done());

    await waitFor(() => expect(onApply).toHaveBeenCalledWith({ contentSha256: 'b'.repeat(64), sizeBytes: 153, passes: ['specialize', 'oobeSystem'] }, VALID));
    expect(upload.mock.calls[0][0]).toBe('answerFile');
    expect(upload.mock.calls[0][1]).toBe(VALID);
  });

  it('shows the problems Imaging Core found in the upload and stays open', async () => {
    const upload = vi.fn().mockResolvedValue({ sha256: 'b'.repeat(64), sizeBytes: 153, passes: [], problems: [{ code: 'autoLogon', line: 4, message: 'AutoLogon signs in a local account before Autopilot can run. Remove the AutoLogon element.' }] });
    const onApply = vi.fn();
    render(<AnswerFileDialog answerFile={null} loadText={unused} upload={upload} onClose={() => undefined} onApply={onApply} />);

    fireEvent.change(textarea(), { target: { value: VALID } });
    await waitFor(() => expect(done().disabled).toBe(false));
    fireEvent.click(done());

    expect(await screen.findByText(/^Line 4: AutoLogon/)).toBeTruthy();
    expect(onApply).not.toHaveBeenCalled();
    expect(done().disabled).toBe(true);
  });

  it('offers Retry when the upload fails', async () => {
    const upload = vi.fn().mockRejectedValueOnce(new SetupProfileApiError('The answer file was not uploaded.', 0)).mockResolvedValue({ sha256: 'c'.repeat(64), sizeBytes: 152, passes: ['specialize'], problems: [] });
    const onApply = vi.fn();
    render(<AnswerFileDialog answerFile={null} loadText={unused} upload={upload} onClose={() => undefined} onApply={onApply} />);

    fireEvent.change(textarea(), { target: { value: VALID } });
    await waitFor(() => expect(done().disabled).toBe(false));
    fireEvent.click(done());

    expect(await screen.findByText('The answer file was not uploaded.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
  });

  it('lists every problem with its line and keeps Done disabled', async () => {
    render(<AnswerFileDialog answerFile={null} loadText={unused} onClose={() => undefined} onApply={() => undefined} />);

    fireEvent.change(textarea(), { target: { value: REFUSED } });

    expect(await screen.findByText('2 settings would break the deployment')).toBeTruthy();
    expect(screen.getByText(/^Line 3: The windowsPE pass never runs/)).toBeTruthy();
    expect(screen.getByText(/^Line 4: Domain join is not supported/)).toBeTruthy();
    expect(done().disabled).toBe(true);
  });

  it('shows the problems a refused save returned until the text changes', async () => {
    render(<AnswerFileDialog answerFile={STORED} loadText={() => Promise.resolve(VALID)} serverProblems={[{ code: 'autoLogon', line: 9, message: 'AutoLogon signs in a local account before Autopilot can run. Remove the AutoLogon element.' }]} onClose={() => undefined} onApply={() => undefined} />);

    expect(await screen.findByText(/^Line 9: AutoLogon/)).toBeTruthy();
    fireEvent.change(textarea(), { target: { value: `${VALID}\n` } });
    await waitFor(() => expect(screen.queryByText(/^Line 9: AutoLogon/)).toBeNull());
  });

  it('names a file that cannot be used at all', () => {
    expect(problemHeading([{ code: 'notWellFormed' }])).toBe('This file cannot be used');
    expect(problemHeading([{ code: 'autoLogon' }])).toBe('1 setting would break the deployment');
  });

  it('formats the sizes and reads server problems without a line as the whole file', () => {
    expect(answerFileSizeLabel(2458)).toBe('2.4 KB');
    expect(answerFileProblems([{ code: 'tooLarge', line: null, message: 'm' }])).toEqual([{ code: 'tooLarge', line: 0, message: 'm' }]);
    render(<AnswerFileDialog answerFile={{ ...STORED, sizeBytes: 104_858 }} loadText={() => new Promise(() => undefined)} onClose={() => undefined} onApply={() => undefined} />);
    expect(screen.getByText('0.1 of 2 MB')).toBeTruthy();
  });

  const uploadFile = (file: File) =>
    fireEvent.change(document.querySelector('input[type="file"]')!, { target: { files: [file] } });

  it('refuses an uploaded file over 2 MB, even when its line endings would shrink it', async () => {
    render(<AnswerFileDialog answerFile={null} loadText={unused} onClose={() => undefined} onApply={() => undefined} />);
    const crlf = `<!--${'x\r\n'.repeat(700_000)}-->\r\n${VALID}`;

    uploadFile(new File([crlf], 'unattend.xml'));

    expect(await screen.findByText('The file is larger than 2 MB. Choose a smaller answer file.')).toBeTruthy();
  });

  it('accepts a 2 MB upload with a byte order mark, which does not count', async () => {
    render(<AnswerFileDialog answerFile={null} loadText={unused} onClose={() => undefined} onApply={() => undefined} />);
    const padded = VALID + ' '.repeat(2_097_152 - new TextEncoder().encode(VALID).length);

    uploadFile(new File([new Uint8Array([0xef, 0xbb, 0xbf]), padded], 'unattend.xml'));

    expect(await screen.findByText(/Valid\. Passes: specialize, oobeSystem\./, undefined, { timeout: 5000 })).toBeTruthy();
  });
});
