import { Component, computed, inject, input, output } from '@angular/core';
import { BahtPipe, formatBaht } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability } from '../../core/venues/public-venue.service';
import { BOOKING_KINDS, BookingKind, VenueBooking } from '../../core/venues/venue-bookings.service';

/** One booking as it sits on the board: which court, which hours, and how it reads. */
interface Block {
  bookingId: string;
  courtId: string;
  /** The hour it starts on, and how many hours it covers. */
  hour: number;
  hours: number;
  who: string;
  /** What the block says under the name: the money, or that nobody has said they are coming. */
  note: string;
  /** playing | confirmed | waiting — what is happening to it, drawn on its edge. */
  reads: 'playing' | 'confirmed' | 'waiting';
  /** What kind of booking it is, which is what its fill says. */
  kind: BookingKind;
  /** The server has opened check-in and nobody has come in yet: somebody is due at the desk. */
  due: boolean;
}

/** An hour nobody has taken, and what it costs — which is what the phone is asking. */
interface Free {
  kind: 'free';
  hour: number;
  /** Null when the venue has no price for that hour, and then the cell says nothing. */
  baht: number | null;
}

/** A court's row: the blocks on it, and which of its hours are shut (PRD US-11). */
interface Row {
  courtId: string;
  name: string;
  cells: (Free | { kind: 'closed'; hour: number } | Block)[];
}

/**
 * The venue's day, drawn the way the counter reads it (PRD US-25): every court down the side,
 * every open hour across the top, and each booking as one block over the hours it holds.
 *
 * It is built from two answers the page already has — the grid of hours the venue sells that day
 * and the bookings on it — rather than a third of its own. The blocks are the bookings, so what
 * the board shows and what the list below it says can never disagree.
 */
@Component({
  selector: 'app-day-board',
  imports: [BahtPipe],
  templateUrl: './day-board.html',
  styleUrl: './day-board.scss',
})
export class DayBoard {
  protected readonly i18n = inject(TranslationService);

  /** The hours this venue sells that day, and the courts it sells them on. */
  readonly day = input.required<Availability | null>();

  readonly bookings = input.required<readonly VenueBooking[]>();

  /** Now, when the board is showing today; null on any other day, and then there is no line. */
  readonly now = input<Date | null>(null);

  /** The booking open in the panel beside the floor, drawn as the one being looked at. */
  readonly chosen = input<string | null>(null);

  /** A block was pressed: the counter wants that booking. */
  readonly opened = output<string>();

  protected readonly kinds = BOOKING_KINDS;

  /** An empty hour was pressed: the counter wants to sell it. */
  readonly picked = output<{ courtId: string; hour: number }>();

  protected readonly hours = computed(() => {
    const day = this.day();
    const opens = day?.opensHour;
    const closes = day?.closesHour;
    return opens == null || closes == null
      ? []
      : Array.from({ length: closes - opens }, (_, index) => opens + index);
  });

  protected readonly rows = computed<Row[]>(() => {
    const day = this.day();
    const hours = this.hours();
    if (!day || hours.length === 0) {
      return [];
    }

    const taken = this.blocksByCourt();

    return day.courts.map((court) => {
      const blocks = taken.get(court.courtId) ?? [];
      const cells: Row['cells'] = [];

      for (let index = 0; index < hours.length; index++) {
        const hour = hours[index];
        const block = blocks.find((one) => one.hour === hour);
        if (block) {
          cells.push(block);
          // The block covers its own hours; the ones under it are not drawn again.
          index += block.hours - 1;
          continue;
        }

        // The price of an empty hour is on the board because the question asked across the
        // counter — and down the phone — is "how much is seven o'clock" (PRD US-25). The day
        // already carries it, so the answer costs nothing to show.
        const cell = court.hours.find((one) => one.hour === hour);
        cells.push(
          cell?.status === 'Closed'
            ? { kind: 'closed', hour }
            : { kind: 'free', hour, baht: cell?.bahtPerHour ?? null },
        );
      }

      return { courtId: court.courtId, name: court.name, cells };
    });
  });

  /** Where the live line sits, as a share of the day, or null when it is not on this day. */
  protected readonly liveAt = computed(() => {
    const now = this.now();
    const hours = this.hours();
    if (!now || hours.length === 0) {
      return null;
    }

    const minutes = now.getHours() * 60 + now.getMinutes();
    const opens = hours[0] * 60;
    const closes = (hours[hours.length - 1] + 1) * 60;
    if (minutes < opens || minutes > closes) {
      return null;
    }

    return ((minutes - opens) / (closes - opens)) * 100;
  });

  protected isBlock(cell: Row['cells'][number]): cell is Block {
    return 'bookingId' in cell;
  }

  /** What pressing an empty hour would do, said in full for whoever is not looking at it. */
  protected sellLabel(courtName: string, cell: Free): string {
    const hour = `${this.i18n.t('board.sell')} ${courtName} ${cell.hour}:00`;
    return cell.baht === null
      ? hour
      : `${hour} ${formatBaht(cell.baht, this.i18n.locale())} ${this.i18n.t('money.baht')}`;
  }

  /**
   * Every booking on the day as a block, keyed by court. A booking that holds three hours on one
   * court is one block three wide; the same booking on two courts is one block on each, which is
   * what a counter sees when it looks at the floor.
   */
  private blocksByCourt(): Map<string, Block[]> {
    const byCourt = new Map<string, Block[]>();

    for (const booking of this.bookings()) {
      // Hours that are no longer this booking's do not belong on the floor: a cancelled booking
      // has given them back, and somebody else may already have them.
      if (booking.status === 'Cancelled' || booking.status === 'Expired') {
        continue;
      }

      const byCourtHours = new Map<string, number[]>();
      for (const slot of booking.slots) {
        byCourtHours.set(slot.courtId, [...(byCourtHours.get(slot.courtId) ?? []), slot.hour]);
      }

      for (const [courtId, hours] of byCourtHours) {
        for (const run of runs(hours)) {
          const blocks = byCourt.get(courtId) ?? [];
          blocks.push({
            bookingId: booking.bookingId,
            courtId,
            hour: run[0],
            hours: run.length,
            who: this.whoFor(booking),
            note: this.noteFor(booking),
            reads: this.readsAs(booking),
            kind: booking.kind,
            due: booking.can.checkIn,
          });
          byCourt.set(courtId, blocks);
        }
      }
    }

    return byCourt;
  }

  private whoFor(booking: VenueBooking): string {
    if (booking.customerName) {
      return booking.customerName;
    }

    // The part before the @, because the rest is the same for everybody at gmail and a block is
    // narrow. The row below the board still carries the whole address for anyone who needs it.
    if (booking.bookerEmail) {
      return booking.bookerEmail.split('@')[0];
    }

    return booking.bookerPhone ?? this.i18n.t('booker.deleted');
  }

  private noteFor(booking: VenueBooking): string {
    if (booking.outstandingBaht > 0) {
      return this.i18n.t('board.owes');
    }

    return booking.arrival === 'Arrived'
      ? this.i18n.t('board.arrived')
      : this.i18n.t(`board.arrival.${booking.arrival}`);
  }

  /**
   * Three readings, not eight statuses: a court is being played on, or somebody is expected on
   * it, or it is still waiting on something (PRD US-25).
   */
  private readsAs(booking: VenueBooking): Block['reads'] {
    if (booking.arrival === 'Arrived') {
      return 'playing';
    }

    return booking.status === 'Confirmed' || booking.status === 'Completed'
      ? 'confirmed'
      : 'waiting';
  }
}

/** Consecutive hours, grouped: [18,19,21] is two runs, not three blocks. */
function runs(hours: number[]): number[][] {
  const sorted = [...hours].sort((left, right) => left - right);
  const grouped: number[][] = [];

  for (const hour of sorted) {
    const last = grouped[grouped.length - 1];
    if (last && hour === last[last.length - 1] + 1) {
      last.push(hour);
    } else {
      grouped.push([hour]);
    }
  }

  return grouped;
}
