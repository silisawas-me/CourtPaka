import { DashboardMonth } from './venue-dashboard.service';

/** What each column holds, in the order PRD 7.3 asks for them. */
export interface RevenueColumns {
  month: string;
  bookings: string;
  online: string;
  counter: string;
  refundDue: string;
  refunded: string;
}

/**
 * A spreadsheet of what the venue kept, month by month (PRD US-16, columns from PRD 7.3).
 *
 * Built here rather than by the server for the reason every message is: the words belong to the
 * language the reader chose, and the server does not send words (PRD US-23). It is handed the
 * headings already translated and the same months the screen is showing, so a file and the page
 * it was taken from can never disagree.
 */
export function revenueCsv(months: readonly DashboardMonth[], columns: RevenueColumns): string {
  const rows = [
    [
      columns.month,
      columns.bookings,
      columns.online,
      columns.counter,
      columns.refundDue,
      columns.refunded,
    ],
    ...months.map((month) => [
      // ISO, not a printed month name: a spreadsheet sorts this and a person still reads it.
      `${month.year}-${String(month.month).padStart(2, '0')}`,
      String(month.bookings),
      money(month.onlineBaht),
      money(month.staffBaht),
      money(month.refundDueBaht),
      money(month.refundedBaht),
    ]),
  ];

  return rows.map((row) => row.map(quote).join(',')).join('\r\n') + '\r\n';
}

/**
 * The file as a spreadsheet will open it. The byte order mark is what makes Excel read it as
 * UTF-8; without it, every Thai heading arrives as mojibake and the file looks broken rather
 * than misread.
 */
export function revenueCsvBlob(csv: string): Blob {
  return new Blob(['﻿', csv], { type: 'text/csv;charset=utf-8' });
}

/** Two decimal places and no thousands separators: this is for a spreadsheet, not for reading. */
function money(amount: number): string {
  return amount.toFixed(2);
}

/**
 * A field a spreadsheet reads back as one value. Anything with a comma, a quote or a newline in
 * it is quoted, and quotes inside are doubled — a venue called "ก๊วน, จำกัด" must not become two
 * columns.
 */
function quote(value: string): string {
  return /[",\r\n]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value;
}
