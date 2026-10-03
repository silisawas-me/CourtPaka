import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { TRANSLATIONS } from '../../testing/translations';
import { PackagesPage } from './packages.page';

function offer(overrides: Record<string, unknown> = {}) {
  return {
    typeId: 't1',
    name: 'ชุด 10 ชั่วโมง',
    hours: 10,
    priceBaht: 1800,
    bahtPerHour: 180,
    validForDays: 90,
    withdrawnAt: null,
    ...overrides,
  };
}

function sold(overrides: Record<string, unknown> = {}) {
  return {
    packageId: 'p1',
    typeId: 't1',
    typeName: 'ชุด 10 ชั่วโมง',
    customerName: 'ก๊วนเหมา',
    customerPhone: '0812345678',
    hoursSold: 10,
    priceBaht: 1800,
    bahtPerHour: 180,
    hoursLeft: 8,
    expiresOn: '2026-12-25',
    live: true,
    runningOut: false,
    expiredAt: null,
    soldAt: '2026-09-26T04:00:00Z',
    moves: [
      { hours: 10, move: 'Sold', bookingId: null, at: '2026-09-26T04:00:00Z' },
      { hours: -2, move: 'Used', bookingId: 'b1', at: '2026-09-27T04:00:00Z' },
    ],
    ...overrides,
  };
}

describe('PackagesPage', () => {
  let fixture: ComponentFixture<PackagesPage>;
  let httpMock: HttpTestingController;

  function render(board: unknown[], packages: unknown[]): void {
    // The language is remembered in storage, and a spec that ran earlier in this worker may have
    // left English there; these assertions read the Thai words.
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [PackagesPage],
      providers: pageProviders(),
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(PackagesPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/packages/types').flush(board);
    httpMock.expectOne('/api/venues/v1/packages').flush(packages);
    fixture.detectChanges();
  }

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  it('says when nothing has been sold', () => {
    render([offer()], []);

    expect(textOf(fixture, 'nothing-sold')).toBe(TRANSLATIONS.th['packages.nothingSold']);
  });

  // Selling a package is off this page for now (the owner's call): members and the board only.
  // "+ เพิ่มสมาชิก" opens the design's dialog (artboard a): adding a member is selling a package.
  it('adds a member by selling them a package in the dialog', () => {
    render([offer(), offer({ typeId: 't2', name: 'ชุด 20 ชั่วโมง', priceBaht: 3400 })], []);
    expect(elementOf(fixture, 'sell-package-dialog')).toBeNull();

    clickOn(fixture, 'members-add');
    fixture.detectChanges();
    // The first offer is chosen until another is pressed, and the button says its price.
    expect(textOf(fixture, 'sell-package')).toContain('1,800');
    clickOn(fixture, 'offer-pick-t2');
    fixture.detectChanges();
    expect(textOf(fixture, 'sell-package')).toContain('3,400');

    setInput(fixture, '[data-testid="customer-name"]', ' ก๊วนเหมา ');
    setInput(fixture, '[data-testid="customer-phone"]', '0812345678');
    clickOn(fixture, 'paid-Cash');
    fixture.detectChanges();
    clickOn(fixture, 'sell-package');

    const request = httpMock.expectOne('/api/venues/v1/packages');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      packageTypeId: 't2',
      customerName: 'ก๊วนเหมา',
      customerPhone: '0812345678',
      paidBy: 'Cash',
    });
    request.flush(sold({ hoursLeft: 20, moves: [] }));
    fixture.detectChanges();

    // The new member is on the table, and the dialog has gone.
    expect(elementOf(fixture, 'package-p1')).not.toBeNull();
    expect(elementOf(fixture, 'sell-package-dialog')).toBeNull();
  });

  it('will not sell to nobody', () => {
    render([offer()], []);
    clickOn(fixture, 'members-add');
    fixture.detectChanges();

    clickOn(fixture, 'sell-package');
    fixture.detectChanges();

    httpMock.expectNone('/api/venues/v1/packages');
    expect(textOf(fixture, 'customer-name-error')).toBe(TRANSLATIONS.th['sellPackage.nameNeeded']);
  });

  it('is the design members page, nothing else', () => {
    render([offer()], [sold()]);

    expect(elementOf(fixture, 'package-p1')).not.toBeNull();
    expect(elementOf(fixture, 'board')).toBeNull();
  });

  it('says there is nothing to sell when the board is empty', () => {
    render([], []);
    clickOn(fixture, 'members-add');
    fixture.detectChanges();

    expect(elementOf(fixture, 'sell-package-nothing')).not.toBeNull();
  });

  /**
   * What is left is the number somebody opened this page for (PRD US-31).
   */
  it('shows how much of each package is used', () => {
    render([offer()], [sold()]);

    expect(textOf(fixture, 'left-p1')).toContain('8');
    expect(textOf(fixture, 'left-p1')).toContain('10');
  });

  it('puts the ones about to run out first, and marks them', () => {
    render([offer()], [sold(), sold({ packageId: 'p2', runningOut: true })]);

    const rows = fixture.nativeElement.querySelectorAll('[data-testid^="package-"]');
    expect(rows[0].getAttribute('data-testid')).toBe('package-p2');
    expect(rows[0].classList).toContain('running-out');
  });

  /*
   * Members, as the owner app lists them (PR-6): found by name or phone, narrowed to the ones
   * running out, each saying how it stands.
   */
  it('finds a member by name or phone, and narrows to the ones running out', () => {
    render(
      [offer()],
      [
        sold(),
        sold({
          packageId: 'p2',
          customerName: 'คุณโอ๊ต',
          customerPhone: '0621189034',
          runningOut: true,
        }),
      ],
    );

    const search = elementOf<HTMLInputElement>(fixture, 'members-search')!;
    search.value = '0621';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    let rows = fixture.nativeElement.querySelectorAll('[data-testid^="package-"]');
    expect(rows.length).toBe(1);
    expect(rows[0].getAttribute('data-testid')).toBe('package-p2');

    search.value = '';
    search.dispatchEvent(new Event('input'));
    elementOf<HTMLElement>(fixture, 'members-filter-runningOut')!.click();
    fixture.detectChanges();
    rows = fixture.nativeElement.querySelectorAll('[data-testid^="package-"]');
    expect(rows.length).toBe(1);
    expect(textOf(fixture, 'standing-p2')).toBe(TRANSLATIONS.th['members.standing.runningOut']);
  });

  /** An offer is replaced, never edited: a package sold from one keeps the terms it was sold on. */
});
