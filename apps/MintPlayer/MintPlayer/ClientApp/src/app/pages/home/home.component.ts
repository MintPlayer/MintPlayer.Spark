import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkQueryCardComponent } from '@mintplayer/ng-spark/grid';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

/** The landing page: the newest songs and artists, open to anonymous visitors like the whole catalog. */
@Component({
  selector: 'app-home',
  imports: [SparkQueryCardComponent, TranslateKeyPipe],
  templateUrl: './home.component.html',
  styleUrl: './home.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class HomeComponent {}
