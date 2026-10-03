import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * "bad" in the ink, "Paka" in the green — the name as the design writes it. Not translated: it is
 * a name, and it reads the same in both languages. The halves are two spans inside one word, so a
 * screen reader still says it as one.
 *
 * On a dark ground (the venue side panel) set `--wordmark-paka` to the light green; the ink half
 * follows `color` like any other text.
 */
@Component({
  selector: 'app-wordmark',
  template: `bad<span class="paka">Paka</span>`,
  styles: `
    :host {
      font-family: var(--font-display);
      font-weight: 800;
      letter-spacing: -0.02em;
      white-space: nowrap;
    }
    .paka {
      color: var(--wordmark-paka, var(--mat-sys-primary));
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Wordmark {}
