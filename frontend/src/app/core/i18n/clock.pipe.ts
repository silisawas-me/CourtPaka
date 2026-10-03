import { Pipe, PipeTransform } from '@angular/core';

/**
 * An hour of a venue's day as a wall clock reads it. A venue open past midnight sells hours 24
 * and up on the day it opened (thai-fit T4); on the wall, hour 25 is 01:00.
 */
export function clockHour(hour: number, padded = false): string {
  const wall = ((hour % 24) + 24) % 24;
  return `${padded ? String(wall).padStart(2, '0') : wall}:00`;
}

/** `{{ hour | clock }}` — "9:00", "1:00" for hour 25; `{{ hour | clock: true }}` pads it, "09:00". */
@Pipe({ name: 'clock' })
export class ClockPipe implements PipeTransform {
  transform(hour: number, padded = false): string {
    return clockHour(hour, padded);
  }
}
