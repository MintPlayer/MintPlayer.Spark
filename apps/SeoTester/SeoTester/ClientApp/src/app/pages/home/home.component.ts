import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { SparkBoardComponent } from '@mintplayer/ng-spark/boards';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

/**
 * The landing page (PRD §4.5 "Dashboard"): what SeoTester checks and how, with a way in; signed in, the "home" board
 * (#530) with the user's sites and audits (the audits list is live, so a running audit's progress shows here too).
 */
@Component({
  selector: 'app-home',
  imports: [RouterLink, SparkBoardComponent, TranslateKeyPipe],
  templateUrl: './home.component.html',
  styles: [`
    .seo-hero { max-width: 52rem; margin: 0 auto; }
    .seo-features { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(14rem, 100%), 1fr)); gap: 1rem; max-width: 64rem; margin: 0 auto; }
    .seo-steps { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(14rem, 100%), 1fr)); gap: 1rem; max-width: 64rem; margin: 0 auto; padding: 0; list-style-position: inside; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class HomeComponent {
  protected readonly auth = inject(SparkAuthService);

  /** The report's categories (PRD §3.1, §7). */
  protected readonly features = [
    { key: 'seo', icon: 'bi-search' },
    { key: 'technical', icon: 'bi-gear' },
    { key: 'performance', icon: 'bi-speedometer2' },
    { key: 'security', icon: 'bi-shield-lock' },
    { key: 'accessibility', icon: 'bi-universal-access' },
    { key: 'social', icon: 'bi-share' },
    { key: 'mobile', icon: 'bi-phone' },
    { key: 'domain', icon: 'bi-globe2' },
  ];

  protected readonly steps = ['add', 'verify', 'audit', 'fix'];
}
