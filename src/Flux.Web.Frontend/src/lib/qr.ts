import qrcode from 'qrcode-generator';

/**
 * The dark modules of a QR code for one short text, row by row.
 *
 * A grid of booleans rather than the library's own SVG string, so the page draws the
 * code as an element it owns instead of inserting markup, and so a test can read it.
 *
 * Error correction M: a URL is short enough that the stronger levels cost almost
 * nothing in size, and a code on a laptop screen in a dark room is read through glare
 * and at an angle, where L's seven percent is the first thing to run out. The version
 * is left to the library, which picks the smallest that fits.
 */
export function qrModules(text: string): boolean[][] {
  const code = qrcode(0, 'M');
  code.addData(text, 'Byte');
  code.make();

  const size = code.getModuleCount();
  return Array.from({ length: size }, (_, row) => Array.from({ length: size }, (_, col) => code.isDark(row, col)));
}

/**
 * One SVG path for all the dark modules, a unit square each, so the code is a single
 * element that scales to any size without a gap between neighbours.
 */
export function qrPath(modules: boolean[][]): string {
  const parts: string[] = [];
  modules.forEach((row, y) => {
    row.forEach((dark, x) => {
      if (dark) {
        parts.push(`M${x} ${y}h1v1h-1z`);
      }
    });
  });

  return parts.join('');
}
