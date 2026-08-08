import { describe, expect, it } from 'vitest';

describe('toolchain', () => {
  it('runs TypeScript under Vitest with a DOM', () => {
    const el = document.createElement('div');
    el.textContent = 'ready';
    expect(el.textContent).toBe('ready');
  });
});
