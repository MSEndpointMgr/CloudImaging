/**
 * Answer file rules, run live in the answer file dialog. Imaging Core runs the same rules on every
 * save; both are pinned by tests/fixtures/answer-file-cases.json, so a rule or message changes in
 * both places at once.
 */
import { SaxesParser, type SaxesTagNS } from 'saxes';

export interface AnswerFileProblem {
  code: string;
  /** 1-based line of the offending start tag or parse error; 0 for the whole file. */
  line: number;
  message: string;
}

export interface AnswerFileValidation {
  problems: AnswerFileProblem[];
  /** Distinct configuration passes in document order, for a valid file. */
  passes: string[];
}

export const ANSWER_FILE_MESSAGES: Record<string, string> = {
  tooLarge: 'The answer file is larger than 256 KB. Remove settings you do not need.',
  notWellFormed: 'This is not well-formed XML. Check for a missing closing tag or an unescaped & or < on or before this line.',
  dtdNotAllowed: 'Document type declarations are not allowed. Remove the DOCTYPE line.',
  notAnswerFile: 'This is not an answer file. The root element must be unattend in the urn:schemas-microsoft-com:unattend namespace.',
  windowsPePass: 'The windowsPE pass never runs, because the image is already applied. Move its settings to specialize or oobeSystem, or remove it.',
  offlineServicingPass: 'The offlineServicing pass never runs, because the image is already applied. Use registry values or scripts instead, or remove it.',
  domainJoin: 'Domain join is not supported. Devices join Entra ID through Autopilot. Remove the UnattendedJoin component.',
  skipMachineOobe: 'SkipMachineOOBE skips OOBE, so Autopilot never starts. Remove this element.',
  skipUserOobe: 'SkipUserOOBE skips OOBE, so Autopilot never starts. Remove this element.',
  autoLogon: 'AutoLogon signs in a local account before Autopilot can run. Remove the AutoLogon element.',
  plainTextPassword: 'Plain-text passwords are not allowed, because everyone who can read the profile could read them. Remove this password.',
  runnerCommandPresent: 'This file already contains the Cloud Imaging setup runner command. Remove that RunSynchronousCommand; Cloud Imaging adds it to each device.',
  tooManySpecializeCommands: 'The specialize pass has more than 499 commands. Remove at least one, because Cloud Imaging adds its own.',
};

export const MAX_ANSWER_FILE_BYTES = 262_144;
export const MAX_ANSWER_FILE_PROBLEMS = 50;
const UNATTEND_NAMESPACE = 'urn:schemas-microsoft-com:unattend';
const RUNNER_SCRIPT_NAME = 'invoke-cloudimagingsetup.ps1';
const MAX_SPECIALIZE_COMMANDS = 499;

interface Node {
  local: string;
  uri: string;
  attributes: { local: string; value: string }[];
  line: number;
  parent: Node | null;
  children: Node[];
  text: string;
}

class Stop extends Error {}

const is = (node: Node | null, local: string) => node !== null && node.local.toLowerCase() === local.toLowerCase();
const attribute = (node: Node, local: string) => node.attributes.find((a) => a.local.toLowerCase() === local.toLowerCase())?.value;
const equalsIgnoreCase = (a: string | undefined, b: string) => a !== undefined && a.toLowerCase() === b.toLowerCase();

function single(code: string, line: number): AnswerFileValidation {
  return { problems: [{ code, line, message: ANSWER_FILE_MESSAGES[code] }], passes: [] };
}

/**
 * Line of the first `&` that does not start an entity or character reference. saxes reads such an
 * ampersand as the start of an entity name and only fails at the end of the file, while the
 * server's parser fails on the line itself. Comments and CDATA may contain a bare `&`.
 */
function bareAmpersandLine(text: string): number | null {
  const blanked = text.replace(/<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>/g, (m) => m.replace(/[^\n]/g, ' '));
  const match = /&(?!(?:[A-Za-z_:][\w.:-]*|#[0-9]+|#x[0-9A-Fa-f]+);)/.exec(blanked);
  return match ? blanked.slice(0, match.index).split('\n').length : null;
}

/** Parses into a small tree with the line of each start tag; throws a whole-file problem instead. */
function parse(text: string): Node | AnswerFileValidation {
  const parser = new SaxesParser({ xmlns: true, position: true });
  let root: Node | null = null;
  let current: Node | null = null;
  let tagLine = 0;
  let failure: AnswerFileValidation | null = null;

  parser.on('error', () => {
    failure ??= single('notWellFormed', Math.max(1, parser.line));
    throw new Stop();
  });
  parser.on('doctype', () => {
    failure = single('dtdNotAllowed', Math.max(1, parser.line));
    throw new Stop();
  });
  parser.on('opentagstart', () => { tagLine = parser.line; });
  parser.on('opentag', (tag: SaxesTagNS) => {
    const node: Node = {
      local: tag.local,
      uri: tag.uri,
      attributes: Object.values(tag.attributes).map((a) => ({ local: a.local, value: a.value })),
      line: tagLine,
      parent: current,
      children: [],
      text: '',
    };
    if (current) current.children.push(node);
    else root = node;
    current = node;
  });
  parser.on('closetag', () => { current = current?.parent ?? null; });
  // Only text counts for the rules; CDATA, comments and processing instructions are ignored.
  parser.on('text', (value: string) => { if (current) current.text += value; });

  try {
    parser.write(text).close();
  } catch (err) {
    if (!(err instanceof Stop)) failure ??= single('notWellFormed', Math.max(1, parser.line));
  }
  const ampersand = bareAmpersandLine(text);
  if (ampersand !== null && (failure === null || failure.problems[0].line > ampersand)) {
    return single('notWellFormed', ampersand);
  }
  if (failure) return failure;
  return root ?? single('notWellFormed', 1);
}

function isSpecializeDeployment(component: Node | null): boolean {
  return component !== null
    && is(component, 'component')
    && equalsIgnoreCase(attribute(component, 'name'), 'Microsoft-Windows-Deployment')
    && is(component.parent, 'settings')
    && equalsIgnoreCase(attribute(component.parent!, 'pass'), 'specialize');
}

function check(node: Node, add: (code: string, line: number) => void): void {
  if (is(node, 'settings')) {
    const pass = attribute(node, 'pass');
    if (equalsIgnoreCase(pass, 'windowsPE')) add('windowsPePass', node.line);
    else if (equalsIgnoreCase(pass, 'offlineServicing')) add('offlineServicingPass', node.line);
  } else if (is(node, 'component') && equalsIgnoreCase(attribute(node, 'name'), 'Microsoft-Windows-UnattendedJoin')) {
    add('domainJoin', node.line);
  } else if (is(node, 'SkipMachineOOBE')) {
    add('skipMachineOobe', node.line);
  } else if (is(node, 'SkipUserOOBE')) {
    add('skipUserOobe', node.line);
  } else if (is(node, 'AutoLogon')) {
    add('autoLogon', node.line);
  } else if (is(node, 'PlainText') && node.text.trim().toLowerCase() === 'true') {
    add('plainTextPassword', node.line);
  } else if (is(node, 'Path') && node.text.toLowerCase().includes(RUNNER_SCRIPT_NAME)) {
    add('runnerCommandPresent', node.line);
  } else if (is(node, 'RunSynchronous') && isSpecializeDeployment(node.parent)
    && node.children.filter((c) => is(c, 'RunSynchronousCommand')).length > MAX_SPECIALIZE_COMMANDS) {
    add('tooManySpecializeCommands', node.line);
  }

  // A password with a Value and no PlainText child is treated as plain text by Windows. A
  // PlainText child decides on its own line instead.
  if (node.local.toLowerCase().endsWith('password')
    && node.children.some((c) => is(c, 'Value'))
    && !node.children.some((c) => is(c, 'PlainText'))) {
    add('plainTextPassword', node.line);
  }
  node.children.forEach((child) => check(child, add));
}

function passesOf(node: Node, passes: string[]): string[] {
  if (is(node, 'settings')) {
    const pass = attribute(node, 'pass');
    if (pass !== undefined && !passes.includes(pass)) passes.push(pass);
  }
  node.children.forEach((child) => passesOf(child, passes));
  return passes;
}

/** Applies every rule to `content`, as Imaging Core does. */
export function validateAnswerFile(content: string): AnswerFileValidation {
  const text = content.startsWith('\uFEFF') ? content.slice(1) : content;
  if (new TextEncoder().encode(text).length > MAX_ANSWER_FILE_BYTES) return single('tooLarge', 0);

  const parsed = parse(text);
  if ('problems' in parsed) return parsed;

  const problems: AnswerFileProblem[] = [];
  const add = (code: string, line: number) => problems.push({ code, line, message: ANSWER_FILE_MESSAGES[code] });
  if (parsed.local.toLowerCase() !== 'unattend' || parsed.uri !== UNATTEND_NAMESPACE) add('notAnswerFile', parsed.line);
  check(parsed, add);

  problems.sort((a, b) => a.line - b.line || (a.code < b.code ? -1 : a.code > b.code ? 1 : 0));
  return { problems: problems.slice(0, MAX_ANSWER_FILE_PROBLEMS), passes: problems.length === 0 ? passesOf(parsed, []) : [] };
}

/** "Line 4: <message>", or the message alone for a whole-file problem. */
export function problemText(problem: Pick<AnswerFileProblem, 'line' | 'message'>): string {
  return problem.line > 0 ? `Line ${String(problem.line)}: ${problem.message}` : problem.message;
}

const WHOLE_FILE = new Set(['tooLarge', 'notWellFormed', 'dtdNotAllowed', 'notAnswerFile']);

/** Heading of the problem box: settings that break the deployment, or a file that cannot be used at all. */
export function problemHeading(problems: Pick<AnswerFileProblem, 'code'>[]): string {
  if (problems.some((p) => WHOLE_FILE.has(p.code))) return 'This file cannot be used';
  return `${String(problems.length)} ${problems.length === 1 ? 'setting would' : 'settings would'} break the deployment`;
}
