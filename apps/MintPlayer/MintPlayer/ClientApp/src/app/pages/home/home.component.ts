import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkBoardComponent } from '@mintplayer/ng-spark/boards';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

/** The landing page: the "home" board (#530) with the newest songs and artists, open to anonymous visitors like the whole catalog. */
@Component({
  selector: 'app-home',
  imports: [SparkBoardComponent, TranslateKeyPipe],
  templateUrl: './home.component.html',
  styleUrl: './home.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class HomeComponent {}
