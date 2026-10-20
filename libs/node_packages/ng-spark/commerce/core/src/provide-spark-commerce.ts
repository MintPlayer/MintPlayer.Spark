import { Routes } from '@angular/router';
import { SparkCommerceLegalDocumentsComponent } from './spark-commerce-legal-documents.component';
import { SparkCommerceRankingParametersComponent } from './spark-commerce-ranking-parameters.component';
import { SparkCommerceTraderPageComponent } from './spark-commerce-trader-page.component';

/**
 * The public commerce pages (commerce #521 M4), to mount under a path of the app's choosing — every page
 * can be replaced per route (D27). Needs `withComponentInputBinding()` for the `:organizationId` input.
 *
 * ```ts
 * provideRouter([{ path: 'commerce', children: sparkCommerceRoutes() }], withComponentInputBinding())
 * ```
 * - `traders/:organizationId` — the trader page (DSA Art. 30(7)); the trader label links here.
 * - `ranking` — the ranking parameters (GA5).
 * - `legal` / `legal/:organizationId` — the legal documents in force (GA2).
 */
export function sparkCommerceRoutes(): Routes {
  return [
    { path: 'traders/:organizationId', component: SparkCommerceTraderPageComponent },
    { path: 'ranking', component: SparkCommerceRankingParametersComponent },
    { path: 'legal', component: SparkCommerceLegalDocumentsComponent },
    { path: 'legal/:organizationId', component: SparkCommerceLegalDocumentsComponent },
  ];
}
