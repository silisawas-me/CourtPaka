import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

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
}
