import { Component } from '@angular/core';
import { AllVenuesToday } from './all-venues-today';

/**
 * "ตารางคอร์ท · ทุกสาขา", the owner app's first page: today at every branch this person reads the
 * reports of. The list of venues and the way to apply for another left with the owner app's
 * design — the branches are the bar above every venue page, one press from here.
 */
@Component({
  selector: 'app-venues-page',
  imports: [AllVenuesToday],
  templateUrl: './venues.page.html',
})
export class VenuesPage {}
