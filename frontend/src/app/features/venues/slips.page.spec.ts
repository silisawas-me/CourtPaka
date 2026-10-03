import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QueuedSlip } from '../../core/venues/slips.service';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { SlipsPage } from './slips.page';

function slip(id: string, more: Partial<QueuedSlip> = {}): QueuedSlip {
  return {
    bookingId: id,
    bookerEmail: `${id}@example.com`,
    bookerPhone: null,
    bookerName: null,
    totalBaht: 580,
    depositBaht: 580,
    slipUploadedAt: '2026-10-03T10:42:00Z',
    startsAt: '2026-10-03T12:00:00Z',
    endsAt: '2026-10-03T14:00:00Z',
    courts: ['คอร์ต 1'],
    playsSoon: false,
    sameSlipSeenBefore: false,
    ...more,
  };
}

describe('SlipsPage', () => {
  let fixture: ComponentFixture<SlipsPage>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    URL.createObjectURL = vi.fn(() => 'blob:slip');
    URL.revokeObjectURL = vi.fn();
    TestBed.configureTestingModule({ imports: [SlipsPage], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SlipsPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    httpMock
      .expectOne('/api/venues/v1/slip-queue')
      .flush([
        slip('a', { bookerName: 'คุณแพร' }),
        slip('b', { sameSlipSeenBefore: true, depositBaht: 160, totalBaht: 320 }),
      ]);
    fixture.detectChanges();
    httpMock
      .expectOne('/api/venues/v1/slip-queue/a/slip')
      .flush(new Blob(['x'], { type: 'image/jpeg' }));
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  it('opens the top of the queue, its picture, and flags a slip seen before', () => {
    expect(textOf(fixture, 'slip-a')).toContain('คุณแพร');
    expect(elementOf(fixture, 'slip-a')!.getAttribute('aria-pressed')).toBe('true');
    expect(elementOf<HTMLImageElement>(fixture, 'slip-image')!.src).toBe('blob:slip');
    expect(elementOf(fixture, 'slip-same-b')).not.toBeNull();
    expect(elementOf(fixture, 'slip-same-a')).toBeNull();
    expect(elementOf<HTMLInputElement>(fixture, 'slip-amount')!.value).toBe('580');
    expect(elementOf(fixture, 'slip-auto-check')).not.toBeNull();
  });

  it('confirms what was asked as null, and moves on to the next slip', () => {
    clickOn(fixture, 'slip-confirm');
    const request = httpMock.expectOne('/api/venues/v1/slip-queue/a/confirm');
    expect(request.request.body).toEqual({ amountBaht: null });
    request.flush({});
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/slip-queue/b/slip').flush(new Blob(['y']));
    fixture.detectChanges();

    expect(elementOf(fixture, 'slip-a')).toBeNull();
    expect(textOf(fixture, 'slips-done')).toContain('คุณแพร');
    expect(elementOf<HTMLInputElement>(fixture, 'slip-amount')!.value).toBe('160');
    expect(elementOf(fixture, 'slip-same-warning')).not.toBeNull();
  });

  it('sends the amount the slip shows when it is not what was asked, and refuses nonsense', () => {
    setInput(fixture, '[data-testid=slip-amount]', 'abc');
    clickOn(fixture, 'slip-confirm');
    expect(textOf(fixture, 'slip-error')).toBeTruthy();

    setInput(fixture, '[data-testid=slip-amount]', '290');
    clickOn(fixture, 'slip-confirm');
    const request = httpMock.expectOne('/api/venues/v1/slip-queue/a/confirm');
    expect(request.request.body).toEqual({ amountBaht: 290 });
    request.flush({});
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/slip-queue/b/slip').flush(new Blob(['y']));
  });

  it('rejects only with a reason and an answer about the money', () => {
    clickOn(fixture, 'slip-reject-open');
    clickOn(fixture, 'slip-reject');
    expect(textOf(fixture, 'slip-error')).toBeTruthy();

    setInput(fixture, '[data-testid=slip-reason]', 'ยอดไม่ตรง');
    clickOn(fixture, 'slip-reject');
    httpMock.expectNone('/api/venues/v1/slip-queue/a/reject');

    clickOn(fixture, 'slip-received-no');
    clickOn(fixture, 'slip-reject');
    const request = httpMock.expectOne('/api/venues/v1/slip-queue/a/reject');
    expect(request.request.body).toEqual({
      reason: 'ยอดไม่ตรง',
      paymentReceived: false,
      amountBaht: null,
    });
    request.flush({});
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/slip-queue/b/slip').flush(new Blob(['y']));
  });
});
