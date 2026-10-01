import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { forkJoin } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { HourPackage, PackagesService, PackageType } from '../../core/venues/packages.service';
import { SellPackage } from './sell-package';

/**
 * Members, as the owner app draws them (PR-6, PRD US-31): everybody holding hours, one package a
 * row, found by name or phone, and "+ เพิ่มสมาชิก" to sell one.
 *
 * The ones about to run out come first, because they are a phone call while the hours are still
 * worth something.
 */
@Component({
  selector: 'app-packages-page',
  imports: [MatCardModule, MatProgressBarModule, AppDatePipe, SellPackage],
  templateUrl: './packages.page.html',
  styleUrl: './packages.page.scss',
})
export class PackagesPage {
  private readonly packages = inject(PackagesService);

  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  private readonly board = signal<PackageType[]>([]);
  protected readonly sold = signal<HourPackage[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /** The sell-package dialog is open ("+ เพิ่มสมาชิก", artboard a). */
  protected readonly selling = signal(false);

  /** A package sold in the dialog is a member at the top of the table. */
  protected added(bought: HourPackage): void {
    this.sold.update((all) => [bought, ...all]);
    this.selling.set(false);
  }

  protected readonly onSale = computed(() =>
    this.board().filter((one) => one.withdrawnAt === null),
  );

  /** The ones somebody should ring, first. Everything else in the order it was sold. */
  protected readonly rows = computed(() =>
    [...this.sold()].sort((one, other) => Number(other.runningOut) - Number(one.runningOut)),
  );

  /** The members table's search and filter (owner app PR-6). */
  protected readonly query = signal('');
  protected readonly filters = ['all', 'live', 'runningOut'] as const;
  protected readonly filter = signal<'all' | 'live' | 'runningOut'>('all');

  /** The rows the table shows: found by name or phone, narrowed by how they stand. */
  protected readonly shown = computed(() => {
    const query = this.query().trim().toLowerCase();
    const filter = this.filter();
    return this.rows().filter(
      (one) =>
        (query === '' ||
          one.customerName.toLowerCase().includes(query) ||
          (one.customerPhone ?? '').includes(query)) &&
        (filter === 'all' ||
          (filter === 'live' && one.live) ||
          (filter === 'runningOut' && one.live && one.runningOut)),
    );
  });

  /** How a package stands, in the words the design uses — from the server's own flags. */
  protected standing(one: HourPackage): 'live' | 'runningOut' | 'done' {
    return !one.live ? 'done' : one.runningOut ? 'runningOut' : 'live';
  }

  protected readonly nothingSold = computed(() => !this.loading() && this.sold().length === 0);

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.pageError.set(null);

    forkJoin({
      board: this.packages.board(venueId),
      sold: this.packages.sold(venueId),
    }).subscribe({
      next: ({ board, sold }) => {
        this.loading.set(false);
        this.board.set(board);
        this.sold.set(sold);
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.pageError.set(errorKey(failure));
      },
    });
  }
}
