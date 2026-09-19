import { Pipe, PipeTransform } from '@angular/core';
import { VenueAddress } from '../core/venues/venue.service';

/** Where a venue is, in one line. Three pages show it, so they show it the same way. */
@Pipe({ name: 'venueAddress' })
export class VenueAddressPipe implements PipeTransform {
  transform(venue: VenueAddress): string {
    return [venue.addressLine, venue.district, venue.province].filter(Boolean).join(' · ');
  }
}
