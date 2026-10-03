import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** One line of the paper: a court run or a line off the shelf. */
export interface ReceiptLine {
  kind: 'Court' | 'Item';
  name: string;
  startsAt: string | null;
  endsAt: string | null;
  quantity: number;
  amountBaht: number;
}

/**
 * A booking's receipt as it would be printed (thai-fit T6). `kind` is `Rec` (ใบเสร็จรับเงิน, a
 * venue not registered for VAT) or `Abb` (ใบกำกับภาษีอย่างย่อ); the full invoice is the same
 * numbers with the buyer added at the desk. A preview: no number is taken, nothing is kept.
 */
export interface ReceiptPreview {
  kind: 'Rec' | 'Abb';
  /** "ARI01-ABB-2026": the number without the sequence a preview may not take. */
  numberPrefix: string;
  date: string;
  seller: {
    name: string;
    legalName: string;
    taxId: string;
    taxBranch: string;
    address: string;
    vatRegistered: boolean;
  };
  customerName: string | null;
  lines: ReceiptLine[];
  totalBaht: number;
  beforeVatBaht: number;
  vatBaht: number;
  paidBy: string[];
}

@Injectable({ providedIn: 'root' })
export class ReceiptService {
  private readonly http = inject(HttpClient);

  preview(venueId: string, bookingId: string): Observable<ReceiptPreview> {
    return this.http.get<ReceiptPreview>(`/api/venues/${venueId}/bookings/${bookingId}/receipt`);
  }
}
