import { describe, expect, it } from 'vitest';
import manifest from '../public/manifest.json';

describe('PWA Manifest & Configuration', () => {
  it('should have valid manifest metadata', () => {
    expect(manifest.name).toBe('InternalChat');
    expect(manifest.short_name).toBe('Chat');
    expect(manifest.start_url).toBe('/');
    expect(manifest.display).toBe('standalone');
    expect(manifest.theme_color).toBeDefined();
    expect(manifest.background_color).toBeDefined();
  });

  it('should include required icons (192, 512, maskable)', () => {
    expect(manifest.icons).toHaveLength(3);

    const icon192 = manifest.icons.find((i) => i.sizes === '192x192');
    expect(icon192).toBeDefined();
    expect(icon192?.src).toBe('/icons/icon-192.png');
    expect(icon192?.type).toBe('image/png');

    const icon512 = manifest.icons.find((i) => i.sizes === '512x512' && i.purpose !== 'maskable');
    expect(icon512).toBeDefined();
    expect(icon512?.src).toBe('/icons/icon-512.png');

    const maskableIcon = manifest.icons.find((i) => i.purpose === 'maskable');
    expect(maskableIcon).toBeDefined();
    expect(maskableIcon?.sizes).toBe('512x512');
  });
});
