import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, map, of, switchMap } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { venueClock } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { QueuedSlip, SlipsService } from '../../core/venues/slips.service';
import { slipAmount, slipDay, slipHours, slipName } from './slips';

/** What the middle column holds for the slip on screen. */
type Picture =
  | { state: 'loading' }
  | { state: 'shown'; url: string; pdf: boolean }
  | { state: 'failed'; key: string };

/**
 * The schedule's slip view (thai-fit T5, the "ตรวจสลิป" artboard): the slips waiting, the soonest
 * game first; the picture the booker sent; and the two answers. A person decides — the box for an
 * automatic check is there, empty, until the owner picks a service (docs/plan/thai-fit.md T5).
 */
@Component({
  selector: 'app-slips-page',
  imports: [AppDatePipe, BahtPipe],
  templateUrl: './slips.page.html',
  styleUrls: ['./venue-settings-tab.scss', './slips.page.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SlipsPage {
  private readonly slips = inject(SlipsService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  protected readonly queue = signal<QueuedSlip[] | null>(null);
  protected readonly loadError = signal<string | null>(null);
  private readonly chosenId = signal<string | null>(null);

  /** The slip on screen: the one pressed, else the top of the queue. */
  protected readonly chosen = computed(() => {
    const queue = this.queue() ?? [];
    return queue.find((one) => one.bookingId === this.chosenId()) ?? queue[0] ?? null;
  });

  protected readonly picture = signal<Picture>({ state: 'loading' });
  protected readonly zoomed = signal(false);

  /** What the venue reads off the slip; starts at what was asked. */
  protected readonly amount = signal('');
  protected readonly amountBaht = computed(() => slipAmount(this.amount()));

  protected readonly rejecting = signal(false);
  protected readonly reason = signal('');
  protected readonly received = signal<boolean | null>(null);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  /** The last answer given, said back once: "ยืนยันสลิปของคุณแพรแล้ว". */
  protected readonly done = signal<{ key: string; name: string } | null>(null);

  protected readonly nameOf = slipName;
  protected readonly hoursOf = slipHours;
  protected readonly dayOf = slipDay;
  protected readonly clock = venueClock;

  constructor() {
    effect(() => this.load(this.venueId()));

    // A new slip on screen: its own amount, a clean answer, and its picture fetched.
    effect(() => {
      const slip = this.chosen();
      untracked(() => {
        this.amount.set(slip ? String(slip.depositBaht) : '');
        this.rejecting.set(false);
        this.reason.set('');
        this.received.set(null);
        this.error.set(null);
        this.zoomed.set(false);
      });
    });

    let shown: string | null = null;
    const release = () => {
      if (shown) {
        URL.revokeObjectURL(shown);
        shown = null;
      }
    };
    inject(DestroyRef).onDestroy(release);

    toObservable(computed(() => this.chosen()?.bookingId ?? null))
      .pipe(
        switchMap((bookingId) => {
          release();
          if (!bookingId) {
            return of(null);
          }
          this.picture.set({ state: 'loading' });
          return this.slips.slip(this.venueId(), bookingId).pipe(
            map((file): Picture => {
              shown = URL.createObjectURL(file);
              return { state: 'shown', url: shown, pdf: file.type === 'application/pdf' };
            }),
            catchError((failure: unknown) =>
              of<Picture>({ state: 'failed', key: errorKey(failure) }),
            ),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((picture) => {
        if (picture) {
          this.picture.set(picture);
        }
      });
  }

  protected choose(slip: QueuedSlip): void {
    this.chosenId.set(slip.bookingId);
  }

  protected confirm(slip: QueuedSlip): void {
    const amount = this.amountBaht();
    if (amount === null || amount > slip.totalBaht) {
      this.error.set('slips.amountInvalid');
      return;
    }
    this.answer(
      slip,
      'slips.confirmed',
      this.slips.confirm(this.venueId(), slip.bookingId, this.asked(slip, amount)),
    );
  }

  protected reject(slip: QueuedSlip): void {
    const reason = this.reason().trim();
    const received = this.received();
    if (!reason) {
      this.error.set('error.slip.reason_required');
      return;
    }
    if (received === null) {
      this.error.set('slipQueue.moneyRequired');
      return;
    }
    const amount = this.amountBaht();
    if (received && amount === null) {
      this.error.set('slips.amountInvalid');
      return;
    }
    this.answer(
      slip,
      'slips.rejected',
      this.slips.reject(
        this.venueId(),
        slip.bookingId,
        reason,
        received,
        received && amount !== null ? this.asked(slip, amount) : null,
      ),
    );
  }

  /** What was asked goes as null: the server reads that as the booker's code, the usual case. */
  private asked(slip: QueuedSlip, amount: number): number | null {
    return amount === slip.depositBaht ? null : amount;
  }

  private answer(
    slip: QueuedSlip,
    key: string,
    request: ReturnType<SlipsService['confirm']>,
  ): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    request.subscribe({
      next: () => {
        this.busy.set(false);
        this.done.set({ key, name: slipName(slip) });
        this.queue.update((queue) => (queue ?? []).filter((one) => one !== slip));
        this.chosenId.set(null);
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        const code = errorKey(failure);
        this.error.set(code);
        // Somebody else answered it meanwhile: the queue on screen is out of date.
        if (code === 'error.slip.not_awaiting_verification') {
          this.load(this.venueId());
        }
      },
    });
  }

  private load(venueId: string): void {
    this.slips.queue(venueId).subscribe({
      next: (queue) => {
        this.loadError.set(null);
        this.queue.set(queue);
      },
      error: (failure: unknown) => this.loadError.set(errorKey(failure)),
    });
  }
}
