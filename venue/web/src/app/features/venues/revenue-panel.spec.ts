import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Dashboard } from '../../core/venues/venue-dashboard.service';
import { TRANSLATIONS } from '../../testing/translations';
import { elementOf, pageProviders, textOf } from '../../testing/dom';
import { RevenuePanel } from './revenue-panel';

/** Two days: 1,000 of court money and 200 of shop, against 1,000 in the period before. */
const FIGURES = {
  onlineBaht: 600,
  staffBaht: 400,
  days: [
    {
      date: '2026-09-01',
      onlineBaht: 600,
      staffBaht: 0,
      sellableHours: 8,
      bookedHours: 2,
      shopBaht: 200,
    },
    {
      date: '2026-09-02',
      onlineBaht: 0,
      staffBaht: 400,
      sellableHours: 8,
      bookedHours: 1,
      shopBaht: 0,
    },
  ],
  trade: { shopBaht: 200, spentBaht: 0, leftOverBaht: 1200 },
  byMethod: [
    { method: 'PromptPay', baht: 900 },
    { method: 'Cash', baht: 300 },
  ],
  topItems: [{ itemId: 'i1', name: 'ลูกขนไก่', quantity: 2, baht: 200 }],
  priorBaht: 1000,
} as unknown as Dashboard;

describe('RevenuePanel', () => {
  let fixture: ComponentFixture<RevenuePanel>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [RevenuePanel], providers: pageProviders() });
    fixture = TestBed.createComponent(RevenuePanel);
    fixture.componentRef.setInput('figures', FIGURES);
    fixture.detectChanges();
  });

  it('adds the shop to the court money, and says how it compares with before', () => {
    expect(textOf(fixture, 'revenue-kpi-total')).toBe('฿1,200');
    expect(textOf(fixture, 'revenue-change')).toContain('+20%');
    expect(textOf(fixture, 'revenue-kpi-shop')).toBe('฿200');
  });

  it('draws the tallest day to the top, and every day to the same scale', () => {
    const bars = elementOf(fixture, 'revenue-chart')!.querySelectorAll('.part.court');
    const shops = elementOf(fixture, 'revenue-chart')!.querySelectorAll('.part.shop');
    // Day one is 800 tall and the tallest: 600 of court and 200 of shop.
    expect((bars[0] as HTMLElement).style.height).toBe('75%');
    expect((shops[0] as HTMLElement).style.height).toBe('25%');
    expect((bars[1] as HTMLElement).style.height).toBe('50%');
  });

  it('shares the money out by how it was paid, and names the best seller', () => {
    expect(textOf(fixture, 'revenue-method-PromptPay')).toContain('75%');
    expect(textOf(fixture, 'revenue-method-Cash')).toContain('25%');
    expect(textOf(fixture, 'revenue-item-i1')).toContain('2×');
    expect(fixture.nativeElement.textContent).toContain(TRANSLATIONS.th['revenue.bestSeller']);
  });
});
