import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PaymentMethod } from './venue-bookings.service';

/**
 * What a venue spends money on (PRD US-33). One list, because the radios that offer the choice and
 * the type that names it are the same five things.
 */
export const SHOP_SPEND_KINDS = ['Stock', 'Utilities', 'Wages', 'Repairs', 'Other'] as const;

export type ShopSpendKind = (typeof SHOP_SPEND_KINDS)[number];

/** One line of a venue's counter board (PRD US-32). */
export interface ShopItem {
  itemId: string;
  name: string;
  priceBaht: number;
  /** What one of them is, in the venue's own words: a tube, a bottle, an hour. */
  unit: string;
  counted: boolean;
  /** Below this, somebody should order more. Null for what is not counted. */
  tellMeAt: number | null;
  /** Null for what is not counted: a zero there would read as none left. */
  left: number | null;
  runningLow: boolean;
  withdrawnAt: string | null;
}

export interface ShopSaleLine {
  itemId: string;
  /** The name it was sold under, which is not always the name it has now. */
  name: string;
  quantity: number;
  eachBaht: number;
}

/** One trip to the counter (PRD US-32). */
export interface ShopSale {
  saleId: string;
  bookingId: string | null;
  totalBaht: number;
  soldAt: string;
  cancelledAt: string | null;
  cancelReason: string | null;
  lines: ShopSaleLine[];
}

/** Money the venue paid out (PRD US-33). */
export interface Spend {
  spendId: string;
  kind: ShopSpendKind;
  amountBaht: number;
  paidOn: string;
  paidBy: PaymentMethod;
  note: string | null;
  voidedAt: string | null;
  voidReason: string | null;
}

export interface AddShopItemRequest {
  name: string;
  priceBaht: number;
  unit: string;
  counted: boolean;
  tellMeAt: number | null;
}

export interface SellRequest {
  lines: { itemId: string; quantity: number }[];
  paidBy: string;
  bookingId: string | null;
}

export interface SpendRequest {
  kind: string;
  amountBaht: number;
  /** Null lets the server say what day it is, which is the only clock that should (PRD US-33). */
  paidOn: string | null;
  paidBy: string;
  note: string | null;
  /** Set together, and only for a delivery: one expense that also fills a shelf. */
  itemId: string | null;
  quantity: number | null;
}

/** What the counter sells besides court time, and what the venue paid out (PRD US-32, US-33). */
@Injectable({ providedIn: 'root' })
export class ShopService {
  private readonly http = inject(HttpClient);

  items(venueId: string): Observable<ShopItem[]> {
    return this.http.get<ShopItem[]>(`/api/venues/${venueId}/shop/items`);
  }

  addItem(venueId: string, asked: AddShopItemRequest): Observable<ShopItem> {
    return this.http.post<ShopItem>(`/api/venues/${venueId}/shop/items`, asked);
  }

  withdrawItem(venueId: string, itemId: string): Observable<ShopItem> {
    return this.http.post<ShopItem>(`/api/venues/${venueId}/shop/items/${itemId}/withdraw`, null);
  }

  /** What the shelf actually holds, and why it is not what the ledger said (PRD US-33). */
  count(venueId: string, itemId: string, counted: number, reason: string): Observable<ShopItem> {
    return this.http.post<ShopItem>(`/api/venues/${venueId}/shop/items/${itemId}/count`, {
      counted,
      reason,
    });
  }

  /**
   * What was sold on a day. Left to the server when no day is asked for, because "today" here is
   * the till's day rather than the calendar's — it runs to the count, not to midnight (PRD US-26).
   */
  sales(venueId: string, date?: string): Observable<ShopSale[]> {
    return this.http.get<ShopSale[]>(`/api/venues/${venueId}/shop/sales`, {
      params: date ? { date } : {},
    });
  }

  sell(venueId: string, asked: SellRequest): Observable<ShopSale> {
    return this.http.post<ShopSale>(`/api/venues/${venueId}/shop/sales`, asked);
  }

  cancelSale(venueId: string, saleId: string, reason: string): Observable<ShopSale> {
    return this.http.post<ShopSale>(`/api/venues/${venueId}/shop/sales/${saleId}/cancel`, {
      reason,
    });
  }

  spending(venueId: string, from?: string, to?: string): Observable<Spend[]> {
    return this.http.get<Spend[]>(`/api/venues/${venueId}/spending`, {
      params: from && to ? { from, to } : {},
    });
  }

  spend(venueId: string, asked: SpendRequest): Observable<Spend> {
    return this.http.post<Spend>(`/api/venues/${venueId}/spending`, asked);
  }

  voidSpend(venueId: string, spendId: string, reason: string): Observable<Spend> {
    return this.http.post<Spend>(`/api/venues/${venueId}/spending/${spendId}/void`, {
      reason,
    });
  }
}
