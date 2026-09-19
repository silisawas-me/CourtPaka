/**
 * The API speaks plain dates (YYYY-MM-DD) and the datepicker speaks Date, so the two meet here.
 *
 * The parts are read in the viewer's own zone on purpose: the date someone picked in the calendar
 * is the date they meant. Going through toISOString() would turn a Bangkok evening into the day
 * before, which is how a week gets published a day early.
 */
export function plainDate(date: Date): string {
  return [
    date.getFullYear(),
    `${date.getMonth() + 1}`.padStart(2, '0'),
    `${date.getDate()}`.padStart(2, '0'),
  ].join('-');
}
