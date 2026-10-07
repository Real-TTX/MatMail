#!/usr/bin/env node
// MatMail translation helper. The UI text is English in the source (L["..."]); German lives in
// src/MatMail/Resources/SharedResource.de.resx with the English text as key.
//
//   node tools/i18n.mjs check                 list keys used in code that have no German text (exit 1 if any)
//   node tools/i18n.mjs check --unused        also list resx entries nothing uses any more
//   node tools/i18n.mjs add patch.json        merge {"English": "Deutsch", ...} into the resx
//   node tools/i18n.mjs seed other.resx ...   take German texts for missing keys from other resx files (e.g. MatPaper's)
//   node tools/i18n.mjs prune                 remove resx entries nothing uses any more
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const srcDir = path.join(root, 'src', 'MatMail');
const resxPath = path.join(srcDir, 'Resources', 'SharedResource.de.resx');

const HEADER = `<?xml version="1.0" encoding="utf-8"?>
<root>
  <!--
    German translations. The key is the English source text; anything without an entry
    here falls back to that English text, so pages can be translated one at a time.
    Maintained with tools/i18n.mjs.
  -->
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
`;

const xmlEscape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const xmlUnescape = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');

function readResx(file) {
  const map = new Map();
  if (!fs.existsSync(file)) return map;
  const text = fs.readFileSync(file, 'utf8');
  const re = /<data name="([\s\S]*?)"[^>]*>\s*<value>([\s\S]*?)<\/value>\s*<\/data>/g;
  let m;
  while ((m = re.exec(text))) map.set(xmlUnescape(m[1]), xmlUnescape(m[2]));
  return map;
}

function writeResx(map) {
  const keys = [...map.keys()].sort((a, b) => a.localeCompare(b, 'en'));
  const body = keys.map((k) => `  <data name="${xmlEscape(k).replace(/"/g, '&quot;')}" xml:space="preserve"><value>${xmlEscape(map.get(k))}</value></data>`).join('\n');
  fs.mkdirSync(path.dirname(resxPath), { recursive: true });
  fs.writeFileSync(resxPath, HEADER + '\n' + body + '\n</root>\n', 'utf8');
}

function* walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['bin', 'obj', 'Migrations', 'wwwroot', 'Resources'].includes(entry.name)) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(full);
    else if (/\.(cs|cshtml)$/.test(entry.name)) yield full;
  }
}

function unescapeCSharp(s) {
  return s.replace(/\\(["'\\])/g, '$1').replace(/\\n/g, '\n').replace(/\\t/g, '\t');
}

// The string literals in the first argument of every localizer call: L["text"], l[flag ? "A" : "B"], L["format {0}", value].
function* localizerKeys(text) {
  const start = /\b(?:L|l|_l|localizer)\s*\[/g;
  let m;
  while ((m = start.exec(text))) {
    let depth = 1;
    let inFirstArgument = true;
    for (let i = m.index + m[0].length; i < text.length && depth > 0; i++) {
      const c = text[i];
      if (c === '"') {
        let j = i + 1;
        let literal = '';
        while (j < text.length && text[j] !== '"') {
          if (text[j] === '\\') { literal += text[j] + (text[j + 1] ?? ''); j += 2; } else { literal += text[j]; j++; }
        }
        if (inFirstArgument) yield literal;
        i = j;
      } else if (c === '[' || c === '(' || c === '{') depth++;
      else if (c === ']' || c === ')' || c === '}') depth--;
      else if (c === ',' && depth === 1) inFirstArgument = false;
    }
  }
}

// Keys used in code: L["..."], l["..."], _l["..."] (see localizerKeys), plus sentence-like literals in Services
// (error messages that pages translate with l[error]) and Permissions descriptions.
function usedKeys() {
  const keys = new Map(); // key -> first file
  const add = (k, f) => { if (k && !keys.has(k)) keys.set(k, f); };
  for (const file of walk(srcDir)) {
    const text = fs.readFileSync(file, 'utf8');
    let m;
    for (const literal of localizerKeys(text)) add(unescapeCSharp(literal), file);

    // API errors: new Failure("text") is translated when the response is written.
    for (const failure of text.matchAll(/new Failure\("((?:[^"\\]|\\.)*)"\)/g)) add(unescapeCSharp(failure[1]), file);

    // Breadcrumbs are "Section / Page" literals in ViewData["Breadcrumb"]; the layout translates every segment.
    const crumb = /ViewData\["Breadcrumb"\]\s*=\s*([^;]+);/g;
    while ((m = crumb.exec(text))) {
      for (const literal of m[1].matchAll(/"((?:[^"\\]|\\.)*)"/g)) {
        for (const segment of literal[1].split(' / ')) add(segment.trim(), file);
      }
    }

    const inServices = /[\\/](Services|Messaging|Api)[\\/]/.test(file) || /Permissions\.cs$/.test(file);
    if (inServices) {
      const sentence = /"((?:[^"\\\n]|\\.)+)"/g;
      while ((m = sentence.exec(text))) {
        const s = unescapeCSharp(m[1]);
        if (/^[A-Z][^"{}\\]*[.?!]$/.test(s) && s.includes(' ') && s.length > 12) add(s, file);
        else if (/Permissions\.cs$/.test(file) && /^[A-Z][A-Za-z ,".’-]+$/.test(s) && s.includes(' ')) add(s, file);
      }
    }
  }
  return keys;
}

const [command = 'check', ...args] = process.argv.slice(2);
const existing = readResx(resxPath);

if (command === 'add') {
  const patch = JSON.parse(fs.readFileSync(path.resolve(args[0]), 'utf8'));
  let changed = 0;
  for (const [k, v] of Object.entries(patch)) if (existing.get(k) !== v) { existing.set(k, v); changed++; }
  writeResx(existing);
  console.log(`Merged ${changed} entries; ${existing.size} in total.`);
} else if (command === 'seed') {
  const used = usedKeys();
  let taken = 0;
  for (const file of args) {
    for (const [k, v] of readResx(path.resolve(file))) {
      if (used.has(k) && !existing.has(k)) { existing.set(k, v); taken++; }
    }
  }
  writeResx(existing);
  console.log(`Took ${taken} translations from ${args.length} file(s); ${existing.size} in total.`);
} else if (command === 'prune') {
  const used = usedKeys();
  let removed = 0;
  for (const k of [...existing.keys()]) if (!used.has(k)) { existing.delete(k); removed++; }
  writeResx(existing);
  console.log(`Removed ${removed} unused entries; ${existing.size} left.`);
} else {
  const used = usedKeys();
  const missing = [...used.keys()].filter((k) => !existing.has(k));
  console.log(`${used.size} keys in code, ${existing.size} translated, ${missing.length} missing.`);
  for (const k of missing) console.log(`  MISSING  ${JSON.stringify(k)}   (${path.relative(root, used.get(k))})`);
  if (args.includes('--unused')) {
    for (const k of existing.keys()) if (!used.has(k)) console.log(`  UNUSED   ${JSON.stringify(k)}`);
  }
  process.exit(missing.length ? 1 : 0);
}
