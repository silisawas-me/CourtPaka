import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { TranslationService } from '../../core/i18n/translation.service';
import { BookingKind } from '../../core/venues/venue-bookings.service';

/**
 * What the floor's fills and marks mean, once, above it (badPaka 2a / owner app). A key rather
 * than words in every block: a block an hour wide has room for a name and a time and not much
 * else. Its own component so the board's stylesheet stays the board's.
 */
@Component({
  selector: 'app-board-key',
  template: `
    <ul class="board-key" data-testid="board-key">
      @for (kind of kinds(); track kind) {
        <li>
          <span class="swatch" [class]="'swatch kind-' + kind"></span
          >{{ i18n.t('board.kind.' + kind) }}
        </li>
      }
      <li><span class="swatch closed"></span>{{ i18n.t('board.closed') }}</li>
      <li><span class="mark due"></span>{{ i18n.t('board.due') }}</li>
      @if (live()) {
        <li><span class="mark now"></span>{{ i18n.t('board.now') }}</li>
      }
    </ul>
  `,
  styles: `
    .board-key {
      display: flex;
      flex-wrap: wrap;
      gap: 0.35rem 1rem;
      margin: 0 0 1.25rem;
      padding: 0;
      list-style: none;
      font: var(--mat-sys-label-medium);
      color: var(--mat-sys-on-surface-variant);

      li {
        display: inline-flex;
        align-items: center;
        gap: 0.4rem;
      }

      .swatch {
        width: 0.9rem;
        height: 0.9rem;
        border-radius: var(--mat-sys-corner-extra-small);
        background: var(--soft);
        box-shadow: inset 0 0 0 1px var(--line);
      }

      .swatch.closed {
        background: repeating-linear-gradient(
          135deg,
          var(--status-risk-soft) 0 3px,
          var(--mat-sys-surface) 3px 6px
        );
        box-shadow: inset 0 0 0 1px var(--mat-sys-outline-variant);
      }

      .now {
        width: 2px;
        height: 0.9rem;
        background: var(--status-waiting);
      }
    }

    .due {
      width: 0.5rem;
      height: 0.5rem;
      border-radius: 50%;
      background: var(--status-waiting);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BoardKey {
  protected readonly i18n = inject(TranslationService);

  readonly kinds = input.required<readonly BookingKind[]>();

  /** Whether the day shown is today, and so has a line for now to explain. */
  readonly live = input(false);
}
