import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { venueClock } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { ReceiptLine, ReceiptPreview, ReceiptService } from '../../core/venues/receipt.service';

/** The paper on screen: the venue's kind, or the full invoice a VAT venue gives when asked. */
export type Paper = 'Rec' | 'Abb' | 'Tax';

/** What the customer asking for a full invoice has to give (ประกาศอธิบดีฯ ฉบับที่ 199). */
export interface Buyer {
  name: string;
  taxId: string;
  branch: string;
  address: string;
}

/** A buyer the full invoice can carry: a name, an address, and a 13-digit tax id. */
export function buyerIsComplete(buyer: Buyer): boolean {
  return (
    buyer.name.trim().length > 0 &&
    buyer.address.trim().length > 0 &&
    /^\d{13}$/.test(buyer.taxId.trim()) &&
    /^\d{5}$/.test(buyer.branch.trim())
  );
}

/**
 * A booking's receipt, as the receipt artboard draws it (thai-fit T6): one sheet of paper, the
 * kind the venue's VAT registration says, and — at a VAT venue — the full invoice when the
 * customer asks and gives their details. Every copy carries "ตัวอย่าง · ยังไม่ใช่เอกสารทางภาษี"
 * until the accountant answers Q1; nothing is issued or kept.
 */
@Component({
  selector: 'app-receipt-sheet',
  imports: [AppDatePipe],
  templateUrl: './receipt-sheet.html',
  styleUrl: './receipt-sheet.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReceiptSheet {
  private readonly receipts = inject(ReceiptService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly bookingId = input.required<string>();
  readonly closed = output<void>();

  /** A modal <dialog>: the top layer is above every panel's stacking context, and Esc closes it. */
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  protected readonly receipt = signal<ReceiptPreview | null>(null);
  protected readonly error = signal<string | null>(null);

  /** The customer asked for the full invoice (only a VAT venue gives one). */
  protected readonly full = signal(false);
  protected readonly buyer = signal<Buyer>({ name: '', taxId: '', branch: '00000', address: '' });
  protected readonly buyerComplete = computed(() => buyerIsComplete(this.buyer()));

  protected readonly paper = computed<Paper | null>(() => {
    const receipt = this.receipt();
    if (!receipt) {
      return null;
    }
    return receipt.kind === 'Abb' && this.full() && this.buyerComplete() ? 'Tax' : receipt.kind;
  });

  /** "ARI01-TAX-2026-······": the shape of the number, the sequence left out on purpose. */
  protected readonly number = computed(() => {
    const receipt = this.receipt();
    const paper = this.paper();
    if (!receipt || !paper) {
      return '';
    }
    const prefix =
      paper === 'Tax' ? receipt.numberPrefix.replace('-ABB-', '-TAX-') : receipt.numberPrefix;
    return `${prefix}-······`;
  });

  constructor() {
    afterNextRender(() => this.dialog().nativeElement.showModal?.());

    effect(() => {
      const [venueId, bookingId] = [this.venueId(), this.bookingId()];
      this.receipt.set(null);
      this.receipts.preview(venueId, bookingId).subscribe({
        next: (receipt) => this.receipt.set(receipt),
        error: (failure: unknown) => this.error.set(errorKey(failure)),
      });
    });

    // Printing shows the paper and nothing else: a class on <body> for the length of the print.
    const done = () => document.body.classList.remove('printing-receipt');
    window.addEventListener('afterprint', done);
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('afterprint', done);
      done();
    });
  }

  protected setBuyer(field: keyof Buyer, value: string): void {
    this.buyer.update((buyer) => ({ ...buyer, [field]: value }));
  }

  /** "คอร์ต 1 · 19:00–21:00", or the item's name. */
  protected lineName(line: ReceiptLine): string {
    return line.kind === 'Court' && line.startsAt && line.endsAt
      ? `${this.i18n.t('receipt.court')} · ${line.name} · ${venueClock(line.startsAt)}–${venueClock(line.endsAt)}`
      : line.name;
  }

  /** Money on paper is written to the satang, always: "580.00". */
  protected money(value: number): string {
    return new Intl.NumberFormat(this.i18n.locale(), {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(value);
  }

  /** "เงินสด · พร้อมเพย์", in the reader's words. */
  protected paidByText(receipt: ReceiptPreview): string {
    return receipt.paidBy.map((method) => this.i18n.t(`money.method.${method}`)).join(' · ');
  }

  /** Closing the dialog fires its `close` event, which tells the panel. */
  protected close(): void {
    const dialog = this.dialog().nativeElement;
    if (dialog.open && dialog.close) {
      dialog.close();
    } else {
      this.closed.emit();
    }
  }

  /** The print dialog is also how a phone saves a PDF. */
  protected print(): void {
    document.body.classList.add('printing-receipt');
    window.print();
  }
}
