import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { TranslationService } from '../../core/i18n/translation.service';

/**
 * The platform's three screens, side by side (PRD US-20, US-22). The bar has one "admin" link,
 * because a booker's bar should not grow with every screen an admin has; the rest are reached
 * from here.
 */
@Component({
  selector: 'app-admin-tabs',
  imports: [RouterLink, RouterLinkActive],
  template: `
    <nav class="admin-tabs" [attr.aria-label]="i18n.t('admin.tabs.label')">
      @for (tab of tabs; track tab.path) {
        <a
          class="admin-tab"
          [routerLink]="tab.path"
          routerLinkActive="on"
          ariaCurrentWhenActive="page"
          [attr.data-testid]="'admin-tab-' + tab.id"
        >
          {{ i18n.t('admin.tabs.' + tab.id) }}
        </a>
      }
    </nav>
  `,
  styles: `
    .admin-tabs {
      display: flex;
      flex-wrap: wrap;
      gap: 0.25rem;
    }

    .admin-tab {
      padding: 0.5rem 0.75rem;
      min-height: 2.75rem;
      display: inline-flex;
      align-items: center;
      border-radius: var(--mat-sys-corner-full);
      font: var(--mat-sys-label-large);
      color: var(--mat-sys-primary);
      text-decoration: none;
    }

    /* The page you are on carries the weight; the others are ways across. */
    .admin-tab.on {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
  `,
})
export class AdminTabs {
  protected readonly i18n = inject(TranslationService);
  protected readonly tabs = [
    { id: 'venues', path: '/admin/venues' },
    { id: 'dashboard', path: '/admin/dashboard' },
    { id: 'users', path: '/admin/users' },
  ];
}
