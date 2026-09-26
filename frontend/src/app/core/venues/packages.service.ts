import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { VenueBooking } from './venue-bookings.service';

/** One kind of package a venue offers (PRD US-31). */
export interface PackageType {
  typeId: string;
  name: string;
  hours: number;
  priceBaht: number;
  /** Worked out by the server so no screen divides it itself. */
  bahtPerHour: number;
  validForDays: number;
  /** When it came off the board, or null while it is still on it. */
  withdrawnAt: string | null;
}

/** One movement of hours, in or out. The balance is their sum, never a stored number. */
export interface PackageMove {
  hours: number;
  move: 'Sold' | 'Used' | 'GivenBack' | 'Expired';
  bookingId: string | null;
  at: string;
}

/** One package somebody bought, and everything that has happened to it (PRD US-31). */
export interface HourPackage {
  packageId: string;
  typeId: string;
  typeName: string;
  customerName: string;
  customerPhone: string | null;
  hoursSold: number;
  priceBaht: number;
  bahtPerHour: number;
  hoursLeft: number;
  expiresOn: string;
  /**
   * Whether its hours can still be spent. The server's answer, not one derived here — a screen
   * that works it out itself offers packages the server then refuses (PRD US-31).
   */
  live: boolean;
  /** Whether somebody should be rung about it before the hours run out. */
  runningOut: boolean;
  expiredAt: string | null;
  soldAt: string;
  moves: PackageMove[];
}

export interface OfferPackageRequest {
  name: string;
  hours: number;
  priceBaht: number;
  validForDays: number;
}

export interface SellPackageRequest {
  packageTypeId: string;
  customerName: string;
  customerPhone: string | null;
  paidBy: string;
}

/** A venue's board of packages, and the ones it has sold (PRD US-31). */
@Injectable({ providedIn: 'root' })
export class PackagesService {
  private readonly http = inject(HttpClient);

  board(venueId: string): Observable<PackageType[]> {
    return this.http.get<PackageType[]>(`/api/venues/${venueId}/packages/types`);
  }

  offer(venueId: string, asked: OfferPackageRequest): Observable<PackageType> {
    return this.http.post<PackageType>(`/api/venues/${venueId}/packages/types`, asked);
  }

  withdraw(venueId: string, typeId: string): Observable<PackageType> {
    return this.http.post<PackageType>(
      `/api/venues/${venueId}/packages/types/${typeId}/withdraw`,
      null,
    );
  }

  sold(venueId: string): Observable<HourPackage[]> {
    return this.http.get<HourPackage[]>(`/api/venues/${venueId}/packages`);
  }

  sell(venueId: string, asked: SellPackageRequest): Observable<HourPackage> {
    return this.http.post<HourPackage>(`/api/venues/${venueId}/packages`, asked);
  }

  /**
   * Pays for a booking the venue is already holding with a package's hours (PRD US-31). Answers
   * with the booking's row as the day's list draws it, so the screen replaces that row rather
   * than reading the whole day again.
   */
  spend(venueId: string, bookingId: string, packageId: string): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(
      `/api/venues/${venueId}/bookings/${bookingId}/pay-with-package`,
      { packageId },
    );
  }
}
