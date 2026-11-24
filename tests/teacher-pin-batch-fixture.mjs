import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';

// The fixture names contain commas and quotes, but no line breaks.
export async function readTeacherPinBatch(path) {
  const text = await readFile(path, 'utf8');
  assert.ok(text.startsWith('\uFEFF学校,学校代码,姓名,短账号,初始PIN,本次教学空间\r\n'), 'expected the UTF-8 BOM and credential CSV header');
  const lines = text.trimEnd().split('\r\n').slice(1);
  return lines.map(line => {
    assert.ok(/^(?:"(?:[^"]|"")*",){5}"(?:[^"]|"")*"$/.test(line), 'each issued row has six escaped CSV fields');
    return [...line.matchAll(/"((?:[^"]|"")*)"/g)].map(cell => cell[1].replaceAll('""', '"'));
  });
}
