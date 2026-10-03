import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReceiptPreview } from '../../core/venues/receipt.service';
import { check, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { buyerIsComplete, ReceiptSheet } from './receipt-sheet';

function preview(kind: 'Rec' | 'Abb'): ReceiptPreview {
  return {
    kind,
    numberPrefix: kind === 'Abb' ? 'ARI01-ABB-2026' : 'ARI01-REC-2026',
    date: '2026-10-03',
    seller: {
      name: 'อารีย์',
      legalName: 'บจก. อารีย์ สปอร์ต',
      taxId: '0105566012345',
      taxBranch: '00000',
      address: '12/3 ซ.อารีย์ 4',
      vatRegistered: kind === 'Abb',
    },
    customerName: 'คุณแพร',
    lines: [
      {
        kind: 'Court',
        name: 'คอร์ต 1',
        startsAt: '2026-10-03T12:00:00Z',
        endsAt: '2026-10-03T14:00:00Z',
        quantity: 2,
        amountBaht: 580,
      },
      { kind: 'Item', name: 'น้ำดื่ม', startsAt: null, endsAt: null, quantity: 4, amountBaht: 60 },
    ],
    totalBaht: 640,
    beforeVatBaht: kind === 'Abb' ? 598.13 : 640,
    vatBaht: kind === 'Abb' ? 41.87 : 0,
    paidBy: ['Cash'],
  };
}

describe('ReceiptSheet', () => {
  let fixture: ComponentFixture<ReceiptSheet>;
  let httpMock: HttpTestingController;

  function open(kind: 'Rec' | 'Abb'): void {
    TestBed.configureTestingModule({ imports: [ReceiptSheet], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ReceiptSheet);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('bookingId', 'b1');
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/bookings/b1/receipt').flush(preview(kind));
    fixture.detectChanges();
  }

  afterEach(() => httpMock.verify());

  it('gives a venue not registered for VAT a receipt with no tax-invoice words and no VAT line', () => {
    open('Rec');
    expect(textOf(fixture, 'receipt-kind')).toBe('ใบเสร็จรับเงิน');
    expect(textOf(fixture, 'receipt-paper')).not.toContain('ใบกำกับภาษี');
    expect(elementOf(fixture, 'receipt-vat')).toBeNull();
    expect(elementOf(fixture, 'receipt-full')).toBeNull();
    expect(textOf(fixture, 'receipt-total')).toContain('640.00');
    expect(textOf(fixture, 'receipt-number')).toBe('ARI01-REC-2026-······');
    expect(textOf(fixture, 'receipt-paper')).toContain('ตัวอย่าง');
  });

  it('turns the abbreviated invoice into the full one only once the buyer is complete', () => {
    open('Abb');
    expect(textOf(fixture, 'receipt-kind')).toBe('ใบกำกับภาษีอย่างย่อ');
    expect(elementOf(fixture, 'receipt-vat')).toBeNull();

    check(fixture, '[data-testid=receipt-full]');
    fixture.detectChanges();
    setInput(fixture, '[data-testid=buyer-name]', 'บจก. ทีมออฟฟิศ');
    expect(textOf(fixture, 'receipt-kind')).toBe('ใบกำกับภาษีอย่างย่อ');
    expect(elementOf(fixture, 'buyer-missing')).not.toBeNull();

    setInput(fixture, '[data-testid=buyer-tax-id]', '0105560098765');
    setInput(fixture, '[data-testid=buyer-address]', '99 ถ.พหลโยธิน');
    expect(textOf(fixture, 'receipt-kind')).toContain('เต็มรูป');
    expect(textOf(fixture, 'receipt-vat')).toContain('41.87');
    expect(textOf(fixture, 'receipt-before-vat')).toContain('598.13');
    expect(textOf(fixture, 'receipt-number')).toBe('ARI01-TAX-2026-······');
    expect(textOf(fixture, 'receipt-buyer-name')).toBe('บจก. ทีมออฟฟิศ');
  });

  it('asks for a 13-digit tax id and a 5-digit branch', () => {
    const buyer = { name: 'ก', taxId: '0105560098765', branch: '00000', address: 'ข' };
    expect(buyerIsComplete(buyer)).toBe(true);
    expect(buyerIsComplete({ ...buyer, taxId: '123' })).toBe(false);
    expect(buyerIsComplete({ ...buyer, branch: '1' })).toBe(false);
    expect(buyerIsComplete({ ...buyer, name: ' ' })).toBe(false);
    open('Rec');
  });
});
