// Makes the pictures of the app icon from the three SVG masters in src/MatMail/wwwroot/icons (the same as the README pictures: Playwright and the installed Edge).
//
//   logo.svg      the detailed one (gradients, shadows, stamp): the large icons, the README, notifications
//   mark.svg      the medium one (flat colours): the logo in the menu and on the sign-in page
//   favicon.svg   the simple one (a bold M): the tab of the browser, favicon.ico
//
//   node scripts/build-app-icons.mjs          needs playwright-core (NODE_PATH=<folder with node_modules>), no other package
//
// Made: icon-512.png, icon-192.png (any), icon-maskable-512.png (full bleed, the picture inside the safe zone), apple-touch-icon.png (full bleed, 180 px,
// iOS rounds it itself), badge-96.png (white on transparent, the notification bar uses only the shape), favicon.ico (16, 32, 48 px as PNG in an ICO), ../favicon.ico.
// Every master marks its background with class="bg" and its picture with id="art": that is how the full bleed and the safe zone are made.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const { chromium } = createRequire(import.meta.url)('playwright-core');   // a CommonJS require, so that NODE_PATH works

const root = path.resolve(import.meta.dirname, '..');
const icons = path.join(root, 'src', 'MatMail', 'wwwroot', 'icons');
const read = (name) => fs.readFileSync(path.join(icons, name), 'utf8');
const logo = read('logo.svg');
const favicon = read('favicon.svg');

/** The same picture without rounded corners (the system rounds it). */
const fullBleed = (svg) => svg.replace(/(class="bg"[^>]*?) rx="14"/g, '$1 rx="0"');
/** The picture smaller around its centre: the safe zone of a maskable icon is the circle of 80 % of the side. */
const inSafeZone = (svg) => svg.replace('<g id="art">', '<g id="art" transform="translate(32 32) scale(.72) translate(-32 -32)">');

const badge = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><mask id="m"><rect width="64" height="64" fill="#fff"/>` +
  `<path d="M13.5 44.5V22.5l18.5 15 18.5-15v22" fill="none" stroke="#000" stroke-width="5.4" stroke-linecap="round" stroke-linejoin="round"/></mask>` +
  `<rect x="5" y="11" width="54" height="42" rx="8" fill="#fff" mask="url(#m)"/></svg>`;

const browser = await chromium.launch({ channel: 'msedge', headless: true });

async function png(svg, size, file) {
  const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
  const sized = svg.replace('<svg ', `<svg width="${size}" height="${size}" style="display:block" `);
  await page.setContent(`<!doctype html><html><body style="margin:0;background:transparent">${sized}</body></html>`);
  const buffer = await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
  await page.close();
  if (file) { fs.writeFileSync(path.join(icons, file), buffer); console.log(file, size + 'x' + size, buffer.length + ' bytes'); }
  return buffer;
}

/** An ICO file with PNG pictures inside (every browser reads that). */
function ico(pictures) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(pictures.length, 4);
  let offset = 6 + 16 * pictures.length;
  const entries = pictures.map(({ size, buffer }) => {
    const entry = Buffer.alloc(16);
    entry[0] = size >= 256 ? 0 : size;
    entry[1] = size >= 256 ? 0 : size;
    entry.writeUInt16LE(1, 4);
    entry.writeUInt16LE(32, 6);
    entry.writeUInt32LE(buffer.length, 8);
    entry.writeUInt32LE(offset, 12);
    offset += buffer.length;
    return entry;
  });
  return Buffer.concat([header, ...entries, ...pictures.map((p) => p.buffer)]);
}

await png(logo, 512, 'icon-512.png');
await png(logo, 192, 'icon-192.png');
await png(inSafeZone(fullBleed(logo)), 512, 'icon-maskable-512.png');
await png(fullBleed(logo), 180, 'apple-touch-icon.png');
await png(badge, 96, 'badge-96.png');

const pictures = [];
for (const size of [16, 32, 48]) { pictures.push({ size, buffer: await png(favicon, size) }); }
const file = ico(pictures);
fs.writeFileSync(path.join(root, 'src', 'MatMail', 'wwwroot', 'favicon.ico'), file);
console.log('favicon.ico', pictures.map((p) => p.size).join(', '), file.length + ' bytes');

await browser.close();
