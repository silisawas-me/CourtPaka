import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The CourtPaka alpaca — the "paka" in the name — about to serve a shuttlecock.
 *
 * Drawn rather than loaded: it is a handful of colours from the theme, so it follows the palette
 * and the reader's light or dark setting on its own, costs no request, and stays sharp at any size.
 */
@Component({
  selector: 'app-alpaca',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './alpaca.scss',
  template: `
    <svg
      class="alpaca"
      viewBox="0 0 220 210"
      role="img"
      [attr.aria-label]="label()"
      [class.waving]="waving()"
    >
      <ellipse class="shadow" cx="112" cy="192" rx="52" ry="8" />

      <!-- Legs, behind the fleece so they end in it rather than against it. -->
      <g class="limbs">
        <rect x="76" y="140" width="14" height="50" rx="7" />
        <rect x="128" y="140" width="14" height="50" rx="7" />
        <rect x="98" y="146" width="13" height="44" rx="6.5" />
        <rect x="118" y="146" width="13" height="44" rx="6.5" />
      </g>

      <!-- Neck, drawn before the fleece so the fleece covers where the two meet. -->
      <g class="neck">
        <path d="M128 132c-4-30 0-52 10-62 6-6 16-6 21 1 6 8 6 22 3 36-3 16-9 26-14 31z" />
      </g>

      <!-- The fleece: one soft mass with a woolly top edge. -->
      <g class="fleece">
        <path
          d="M58 128c-8-26 2-48 24-57 20-8 46-7 62 4 16 12 20 36 14 55-5 16-24 25-51 25s-44-11-49-27z"
        />
        <circle cx="66" cy="92" r="16" />
        <circle cx="92" cy="76" r="18" />
        <circle cx="120" cy="74" r="17" />
        <circle cx="145" cy="88" r="15" />
        <circle cx="156" cy="112" r="13" />
      </g>

      <!-- Head, with the short upright ears an alpaca actually has. -->
      <g class="head">
        <path class="ear" d="M142 36c-3-14 0-24 4-24s7 10 6 22z" />
        <path class="ear" d="M166 34c4-13 9-21 13-19s0 13-5 23z" />
        <ellipse class="skull" cx="156" cy="52" rx="21" ry="19" />
        <path
          class="fringe"
          d="M137 46c2-12 9-20 19-22 12-2 20 4 22 12-8-4-16-4-24 0-7 3-12 7-17 10z"
        />
        <ellipse class="muzzle" cx="163" cy="64" rx="14" ry="10" />
      </g>

      <g class="face">
        <circle class="eye" cx="148" cy="48" r="3.4" />
        <circle class="eye" cx="166" cy="46" r="3.4" />
        <path class="smile" d="M157 66q6 5 12 0" />
        <ellipse class="nose" cx="163" cy="59" rx="3.6" ry="2.4" />
        <circle class="blush" cx="141" cy="58" r="5" />
      </g>

      <!-- The shuttlecock, tossed up and waiting to be hit — clear of the fleece, not against it. -->
      <g class="shuttle" transform="translate(-16 -12)">
        <g class="feathers">
          <path d="M48 46l-20-24 8-7 20 24z" />
          <path d="M55 41l-9-29 10-3 9 29z" />
          <path d="M63 41l6-29 10 3-8 29z" />
        </g>
        <path class="skirt" d="M46 44h34l-5 16a13 13 0 0 1-24 0z" />
        <circle class="cork" cx="63" cy="64" r="12" />
      </g>
    </svg>
  `,
})
export class Alpaca {
  /** What a screen reader is told. The alpaca is decoration on some pages and the subject on others. */
  readonly label = input('');

  /** A small idle nod, for the one place where it greets someone. */
  readonly waving = input(false);
}
