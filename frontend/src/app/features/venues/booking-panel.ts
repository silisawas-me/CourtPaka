import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { TranslationService } from '../../core/i18n/translation.service';
import { BookingKind } from '../../core/venues/venue-bookings.service';

/**
 * The frame of the booking pressed on the floor (badPaka 2a): what kind it is, a way out, and
 * whatever the page puts inside — which is the list's own row, so the doors in it are the doors
 * the server opened and nothing is decided here.
 *
 * A column beside the floor on a desk, a sheet over the foot of the screen on anything narrower,
 * which is where a thumb already is.
 */
@Component({
  selector: 'app-booking-panel',
  imports: [MatButton],
  template: `
    <header class="panel-head">
      <span class="panel-kind" [class]="'panel-kind kind-' + kind()">
        {{ i18n.t('board.kind.' + kind()) }}
      </span>
      <button matButton type="button" data-testid="panel-close" (click)="closed.emit()">
        {{ i18n.t('panel.close') }}
      </button>
    </header>
    <ng-content />
  `,
  styleUrl: './booking-panel.scss',
  host: {
    role: 'region',
    'data-testid': 'booking-panel',
    '[attr.aria-label]': "i18n.t('panel.label')",
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BookingPanel {
  protected readonly i18n = inject(TranslationService);

  readonly kind = input.required<BookingKind>();

  readonly closed = output<void>();
}
