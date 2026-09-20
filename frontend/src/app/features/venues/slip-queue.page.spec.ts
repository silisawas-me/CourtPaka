import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { SlipQueuePage } from './slip-queue.page';

function item(overrides: Record<string, unknown> = {}) {
  return {
    bookingId: 'b1',
    bookerEmail: 'player@example.com',
    totalBaht: 400,
    slipUploadedAt: '2026-09-20T11:00:00Z',
    startsAt: '2026-09-21T11:00:00Z',
    playsSoon: false,
    sameSlipSeenBefore: false,
    ...overrides,
  };
}

describe('SlipQueuePage', () => {
  let fixture: ComponentFixture<SlipQueuePage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    // The page turns the slip into a blob URL; jsdom has no implementation of either.
    URL.createObjectURL = vi.fn(() => 'blob:slip');
    URL.revokeObjectURL = vi.fn();

    await TestBed.configureTestingModule({
      imports: [SlipQueuePage],
      providers: pageProviders([{ path: 'venues', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function render(queue: object[] = [item()]): void {
    fixture = TestBed.createComponent(SlipQueuePage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/slip-queue').flush(queue);
    fixture.detectChanges();

    // The first booking opens by itself, which fetches its picture.
    for (const booking of queue as { bookingId: string }[]) {
      const pending = httpMock.match(`/api/venues/v1/slip-queue/${booking.bookingId}/slip`);
      pending.forEach((request) => request.flush(new Blob(['slip'])));
      break;
    }
    fixture.detectChanges();
  }

  it('says so when nothing is waiting', () => {
    render([]);

    expect(textOf(fixture, 'queue-empty')).toBe(TRANSLATIONS.th['slipQueue.empty']);
    expect(elementOf(fixture, 'decision')).toBeNull();
  });

  it('opens the oldest booking with its picture and its amount', () => {
    render();

    expect(textOf(fixture, 'queue-count')).toContain('1');
    expect(textOf(fixture, 'decision-baht')).toContain('400');
    expect(textOf(fixture, 'decision-who')).toBe('player@example.com');
    expect(elementOf<HTMLImageElement>(fixture, 'slip')?.querySelector('img')?.src).toContain(
      'blob:slip',
    );
  });

  it('marks a booking that plays soon and one whose slip has been seen before', () => {
    render([item({ playsSoon: true, sameSlipSeenBefore: true })]);

    expect(textOf(fixture, 'queue-item-b1')).toContain(TRANSLATIONS.th['slipQueue.playsSoon']);
    expect(textOf(fixture, 'queue-item-b1')).toContain(TRANSLATIONS.th['slipQueue.sameSlip']);
    // And says it again where the decision is made, because that is where it matters.
    expect(textOf(fixture, 'same-slip-warning')).toBe(TRANSLATIONS.th['slipQueue.sameSlipWarning']);
  });

  it('confirms, and moves on to what is next', () => {
    render([item(), item({ bookingId: 'b2', bookerEmail: 'other@example.com' })]);

    clickOn(fixture, 'confirm');

    httpMock.expectOne('/api/venues/v1/slip-queue/b1/confirm').flush({});
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/slip-queue/b2/slip').flush(new Blob(['slip']));
    fixture.detectChanges();

    // The decided one is gone from the queue and the next one is open.
    expect(elementOf(fixture, 'queue-item-b1')).toBeNull();
    expect(textOf(fixture, 'decision-who')).toBe('other@example.com');
  });

  it('will not turn a booking away without a reason', () => {
    render();

    clickOn(fixture, 'reject');
    clickOn(fixture, 'reject-confirm');

    // Nothing was sent, and the form says why.
    httpMock.expectNone('/api/venues/v1/slip-queue/b1/reject');
    expect(elementOf(fixture, 'reason-error')).not.toBeNull();
  });

  it('sends the reason and the answer about the money', () => {
    render();

    clickOn(fixture, 'reject');
    setInput(fixture, '#reason', 'ยอดไม่ตรง');
    const received = (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>(
      'input[type=radio]',
    )[1];
    received.click();
    fixture.detectChanges();

    // Saying the money arrived says out loud that it has to go back.
    expect(textOf(fixture, 'refund-note')).toBe(TRANSLATIONS.th['slipQueue.refundNote']);

    clickOn(fixture, 'reject-confirm');

    const request = httpMock.expectOne('/api/venues/v1/slip-queue/b1/reject');
    expect(request.request.body).toEqual({ reason: 'ยอดไม่ตรง', paymentReceived: true });
    request.flush({});
    fixture.detectChanges();

    expect(elementOf(fixture, 'decision')).toBeNull();
  });

  it('explains a refusal and reads the queue again, because someone got there first', () => {
    render();

    clickOn(fixture, 'confirm');
    httpMock
      .expectOne('/api/venues/v1/slip-queue/b1/confirm')
      .flush({ code: 'slip.not_awaiting_verification' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'decide-error')).toBe(
      TRANSLATIONS.th['error.slip.not_awaiting_verification'],
    );

    httpMock.expectOne('/api/venues/v1/slip-queue').flush([]);
    fixture.detectChanges();

    expect(textOf(fixture, 'queue-empty')).toBe(TRANSLATIONS.th['slipQueue.empty']);
  });

  it('says so when the picture cannot be opened', () => {
    fixture = TestBed.createComponent(SlipQueuePage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/slip-queue').flush([item()]);
    fixture.detectChanges();
    httpMock
      .expectOne('/api/venues/v1/slip-queue/b1/slip')
      .flush(null, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(textOf(fixture, 'slip-missing')).toBe(TRANSLATIONS.th['slipQueue.slipMissing']);
    // The decision is still there: the venue may know from the bank what the picture would show.
    expect(elementOf(fixture, 'confirm')).not.toBeNull();
  });

  it('translates a venue the reader may not look at', () => {
    fixture = TestBed.createComponent(SlipQueuePage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/venues/v1/slip-queue')
      .flush({ code: 'venue.not_member' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(elementOf(fixture, 'page-error')).not.toBeNull();
  });
});
