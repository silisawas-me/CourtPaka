import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/home/home.page').then((m) => m.HomePage),
  },
  {
    path: 'register',
    loadComponent: () => import('./features/auth/register.page').then((m) => m.RegisterPage),
  },
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login.page').then((m) => m.LoginPage),
  },
  {
    path: 'verify-email',
    loadComponent: () => import('./features/auth/verify-email.page').then((m) => m.VerifyEmailPage),
  },
  {
    path: 'resend-verification',
    loadComponent: () =>
      import('./features/auth/resend-verification.page').then((m) => m.ResendVerificationPage),
  },
  {
    // The counter's "now" (badPaka 2b): the floor this minute, who is due, and a quick sale.
    path: 'venues/:venueId/now',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/now.page').then((m) => m.NowPage),
    canActivate: [authGuard],
  },
  {
    // The schedule's slip view (thai-fit T5): the slips waiting, the picture, the answer.
    path: 'venues/:venueId/slips',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/slips.page').then((m) => m.SlipsPage),
    canActivate: [authGuard],
  },
  {
    // The court schedule of one branch, as the owner app draws it: tracks and the booking panel.
    path: 'venues/:venueId/timeline',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/timeline.page').then((m) => m.TimelinePage),
    canActivate: [authGuard],
  },
  {
    // Pricing & peak, painted (owner app PR-4).
    path: 'venues/:venueId/pricing',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/pricing.page').then((m) => m.PricingPage),
    canActivate: [authGuard],
  },
  {
    // The schedule's list view: any day's bookings, a search across days, the same panel.
    path: 'venues/:venueId/bookings',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/bookings.page').then((m) => m.BookingsPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues/:venueId/dashboard',
    data: { venueShell: true },
    loadComponent: () =>
      import('./features/venues/venue-dashboard.page').then((m) => m.VenueDashboardPage),
    canActivate: [authGuard],
  },
  {
    path: 'venues/:venueId/packages',
    data: { venueShell: true },
    loadComponent: () => import('./features/venues/packages.page').then((m) => m.PackagesPage),
    canActivate: [authGuard],
  },
  {
    // The first sign-in of staff an owner added: the privacy policy, accepted by them (thai-fit T1).
    path: 'welcome',
    loadComponent: () => import('./features/auth/welcome.page').then((m) => m.WelcomePage),
    canActivate: [authGuard],
  },
  {
    path: 'account',
    loadComponent: () => import('./features/auth/account.page').then((m) => m.AccountPage),
    canActivate: [authGuard],
  },
  {
    path: 'admin/dashboard',
    loadComponent: () =>
      import('./features/admin/dashboard.page').then((m) => m.AdminDashboardPage),
    canActivate: [authGuard],
  },
  {
    path: 'admin/commission',
    loadComponent: () =>
      import('./features/admin/commission.page').then((m) => m.AdminCommissionPage),
    canActivate: [authGuard],
  },
  {
    path: 'admin/complaints',
    loadComponent: () =>
      import('./features/admin/complaints.page').then((m) => m.AdminComplaintsPage),
    canActivate: [authGuard],
  },
  {
    path: 'admin/users',
    loadComponent: () => import('./features/admin/users.page').then((m) => m.AdminUsersPage),
    canActivate: [authGuard],
  },
  {
    path: 'admin/venues',
    loadComponent: () => import('./features/admin/venues.page').then((m) => m.AdminVenuesPage),
    canActivate: [authGuard],
  },
  {
    // Every venue at once: the owner app's first page (docs/plan/owner-app.md).
    path: 'venues',
    data: { venueShell: 'all' },
    canActivate: [authGuard],
    loadComponent: () => import('./features/venues/venues.page').then((m) => m.VenuesPage),
  },
  {
    path: 'venues/apply',
    loadComponent: () => import('./features/venues/apply.page').then((m) => m.VenueApplyPage),
    canActivate: [authGuard],
  },
  {
    // The pages that were under "อื่น ๆ" are gone (the owner's call, 2026-10-02): the four sections
    // are the app. Old links — the venue's own emails among them — land on the court schedule.
    path: 'venues/:venueId',
    pathMatch: 'full',
    redirectTo: 'venues/:venueId/timeline',
  },
  {
    path: 'venues/:venueId/report',
    redirectTo: 'venues/:venueId/dashboard',
  },
  {
    path: 'venues/:venueId/package-board',
    redirectTo: 'venues/:venueId/packages',
  },
  {
    path: 'venues/:venueId/:gone',
    redirectTo: 'venues/:venueId/timeline',
  },
  {
    path: 'venue-invitation',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/venues/accept-invitation.page').then((m) => m.AcceptInvitationPage),
  },
  { path: '**', redirectTo: '' },
];
