import { describe, it, expect } from 'vitest';
import { ANSWER_FILE_MESSAGES, problemText, validateAnswerFile } from '../../../src/cloud-imaging-portal/client/src/lib/answerFileValidation.ts';
import shared from '../../fixtures/answer-file-cases.json';

/**
 * The answer file dialog checks the same rules as Imaging Core. Both suites read one case file, so
 * a device is never refused on save for a file the dialog accepted, or the other way round.
 */

interface Case {
  name: string;
  xml?: string;
  generate?: { kind: 'specializeCommands' | 'repeat'; count: number; element?: string };
  padToBytes?: number;
  expected: { code: string; line: number }[] | 'first50';
  firstLine?: number;
  passes?: string[];
}

function generate(spec: NonNullable<Case['generate']>): string {
  const header = ['<?xml version="1.0"?>', '<unattend xmlns="urn:schemas-microsoft-com:unattend">'];
  if (spec.kind === 'repeat') {
    return [...header, ...Array.from({ length: spec.count }, () => `  <${spec.element!}>true</${spec.element!}>`), '</unattend>'].join('\n');
  }
  return [
    ...header,
    '  <settings pass="specialize">',
    '    <component name="Microsoft-Windows-Deployment">',
    '      <RunSynchronous>',
    ...Array.from({ length: spec.count }, (_, i) => `        <RunSynchronousCommand><Order>${String(i + 1)}</Order><Path>a.cmd</Path></RunSynchronousCommand>`),
    '      </RunSynchronous>',
    '    </component>',
    '  </settings>',
    '</unattend>',
  ].join('\n');
}

function pad(xml: string, bytes: number): string {
  const missing = bytes - new TextEncoder().encode(xml).length - 7;
  const at = xml.lastIndexOf('</unattend>');
  return `${xml.slice(0, at)}<!--${'x'.repeat(missing)}-->${xml.slice(at)}`;
}

export function caseXml(c: Case): string {
  const xml = c.xml ?? generate(c.generate!);
  return c.padToBytes ? pad(xml, c.padToBytes) : xml;
}

describe('Portal frontend: answer file rules', () => {
  const cases = shared.cases as Case[];

  it('uses the shared message for every rule', () => {
    expect(ANSWER_FILE_MESSAGES).toEqual(shared.messages);
  });

  it.each(cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    const result = validateAnswerFile(caseXml(c));

    if (c.expected === 'first50') {
      expect(result.problems).toHaveLength(50);
      expect(result.problems.map((p) => p.line)).toEqual(Array.from({ length: 50 }, (_, i) => c.firstLine! + i));
    } else {
      expect(result.problems.map((p) => ({ code: p.code, line: p.line }))).toEqual(c.expected);
    }
    for (const p of result.problems) expect(p.message).toBe(ANSWER_FILE_MESSAGES[p.code]);
    if (c.passes) expect(result.passes).toEqual(c.passes);
  });

  it('writes a problem with its line, or alone for the whole file', () => {
    expect(problemText({ line: 4, message: 'Domain join is not supported.' })).toBe('Line 4: Domain join is not supported.');
    expect(problemText({ line: 0, message: 'Too large.' })).toBe('Too large.');
  });
});
