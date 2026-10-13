#!/usr/bin/env node
// Makes src/MatMail/wwwroot/matui/matmail-icons.svg: the icons MatMail uses that the sprite of the toolkit (matui-icons.svg) does not have,
// taken from the optional icon library of MatDesign (ids mat-il-NAME, rule ICON-05), and ICONS-LICENSE.txt next to it (the notice has to stay
// with the icons). The names are in scripts/icons-extra.json. An icon that gets a meaning shared by the apps belongs in the sprite of the toolkit:
// ask MatDesign for it and remove the name here.
//
//   node scripts/build-icons.mjs [library]      library: the address of matui-iconlib.svg of a running MatDesign (default
//                                               http://localhost:8060/ui/iconlib/matui-iconlib.svg) or a file; LICENSE.txt is read from the same place
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const target = path.join(root, 'src', 'MatMail', 'wwwroot', 'matui');
const source = process.argv[2] || 'http://localhost:8060/ui/iconlib/matui-iconlib.svg';
const names = JSON.parse(fs.readFileSync(path.join(import.meta.dirname, 'icons-extra.json'), 'utf8')).names;

async function read(location) {
  if (/^https?:/.test(location)) {
    const response = await fetch(location);
    if (!response.ok) throw new Error(`${location}: ${response.status}`);
    return await response.text();
  }
  return fs.readFileSync(location, 'utf8');
}

const sibling = (location, file) => (/^https?:/.test(location) ? location.replace(/[^/]*$/, file) : path.join(path.dirname(location), file));

const library = await read(source);
const toolkit = fs.readFileSync(path.join(target, 'matui-icons.svg'), 'utf8');
const symbols = new Map([...library.matchAll(/<symbol id="mat-il-([a-z0-9-]+)"[^>]*>[\s\S]*?<\/symbol>/g)].map((m) => [m[1], m[0]]));

const missing = names.filter((n) => !symbols.has(n));
if (missing.length) throw new Error('not in the library: ' + missing.join(', '));
const inToolkit = names.filter((n) => toolkit.includes(`id="mat-i-${n}"`));
if (inToolkit.length) console.warn('also in the toolkit sprite (use mat-i-NAME and remove from icons-extra.json): ' + inToolkit.join(', '));

const body = [...new Set(names)].sort().map((n) => '  ' + symbols.get(n)).join('\n');
const svg = `<svg xmlns="http://www.w3.org/2000/svg" class="mat-sprite" width="0" height="0" aria-hidden="true" focusable="false">\n${body}\n</svg>\n`;
fs.writeFileSync(path.join(target, 'matmail-icons.svg'), svg);
fs.writeFileSync(path.join(target, 'ICONS-LICENSE.txt'), await read(sibling(source, 'LICENSE.txt')));
console.log(`${names.length} icons from the library written to matmail-icons.svg`);
