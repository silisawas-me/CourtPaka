import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { Weekday } from './court.service';

export interface PriceBand {
  day: Weekday;
  fromHour: number;
  toHour: number;
  bahtPerHour: number;
}

export interface PriceList {
  id: string;
  createdAt: string;
  bands: PriceBand[];
}

export interface CancellationTier {
  hoursBefore: number;
  refundPercent: number;
}

export interface CancellationPolicy {
  id: string;
  createdAt: string;
  /** Most generous first, the way the server orders them. */
  tiers: CancellationTier[];
}

@Injectable({ providedIn: 'root' })
export class PricingService {
  private readonly http = inject(HttpClient);

  /** Null when the venue has not published any prices yet: the server answers 204, not an empty list. */
  prices(venueId: string): Observable<PriceList | null> {
    return this.http
      .get<PriceList>(`/api/venues/${venueId}/prices`, { observe: 'response' })
      .pipe(map((response) => response.body));
  }

  setPrices(venueId: string, bands: readonly PriceBand[]): Observable<PriceList> {
    return this.http.put<PriceList>(`/api/venues/${venueId}/prices`, { bands });
  }

  cancellationPolicy(venueId: string): Observable<CancellationPolicy> {
    return this.http.get<CancellationPolicy>(`/api/venues/${venueId}/cancellation-policy`);
  }

  setCancellationPolicy(
    venueId: string,
    tiers: readonly CancellationTier[],
  ): Observable<CancellationPolicy> {
    return this.http.put<CancellationPolicy>(`/api/venues/${venueId}/cancellation-policy`, {
      tiers,
    });
  }
}
