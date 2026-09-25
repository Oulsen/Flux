// Builds flux.ico from the SVG marks, each drawn for its own size band. The mark is a
// stretch of music seen through five bars, the hit burning white where it lands. The
// 16, 24 and 32 px cuts serve the tray, the taskbar and the Start menu, and are drawn
// on whole pixels with the waveform's texture and glow left out, since at those sizes
// both read as blur; they keep what does survive, which is the bars, the heat colours
// and the low lead-in that jumps at the white hit. The master serves 48 and up, where
// its texture holds, so the desktop icon still shows the waveform inside the bars.
//
// Run with `npm install && npm run build-ico` in this folder after editing a mark.
// The .ico is committed, so a build never needs node for it.
import { readFile, writeFile } from 'node:fs/promises';
import sharp from 'sharp';
import pngToIco from 'png-to-ico';

const sizes = [
  { svg: 'flux-mark-16.svg', intrinsic: 16, size: 16 },
  { svg: 'flux-mark-24.svg', intrinsic: 24, size: 24 },
  { svg: 'flux-mark-32.svg', intrinsic: 32, size: 32 },
  { svg: 'flux-mark.svg', intrinsic: 512, size: 48 },
  { svg: 'flux-mark.svg', intrinsic: 512, size: 64 },
  { svg: 'flux-mark.svg', intrinsic: 512, size: 128 },
  { svg: 'flux-mark.svg', intrinsic: 512, size: 256 },
];

const pngs = [];
for(const { svg, intrinsic, size } of sizes) {
  const buffer = await readFile(new URL(svg, import.meta.url));
  const density = Math.max(72, Math.ceil(72 * (size / intrinsic)));
  pngs.push(await sharp(buffer, { density }).resize(size, size).png().toBuffer());
  console.log(`rendered ${svg} at ${size}x${size}`);
}

await writeFile(new URL('flux.ico', import.meta.url), await pngToIco(pngs));
console.log('flux.ico written');
