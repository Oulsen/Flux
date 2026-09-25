// Builds flux.ico from the SVG marks, each drawn for its own size band: the 16 px
// cut has three bars because four do not survive at that size, the 32 px cut
// serves 24 to 48, and the master serves everything above. The glow is the
// master's alone, since below 64 px a blur reads as a smudge rather than light.
//
// Run with `npm install && npm run build-ico` in this folder after editing a mark.
// The .ico is committed, so a build never needs node for it.
import { readFile, writeFile } from 'node:fs/promises';
import sharp from 'sharp';
import pngToIco from 'png-to-ico';

const sizes = [
  { svg: 'flux-mark-16.svg', intrinsic: 16, size: 16 },
  { svg: 'flux-mark-32.svg', intrinsic: 32, size: 24 },
  { svg: 'flux-mark-32.svg', intrinsic: 32, size: 32 },
  { svg: 'flux-mark-32.svg', intrinsic: 32, size: 48 },
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
