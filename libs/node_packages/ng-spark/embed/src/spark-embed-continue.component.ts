import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkEmbedService } from './spark-embed.service';

/**
 * The last resort of a hand-over (commerce X23): when neither a popup nor a top navigation was possible (a sandboxed
 * iframe, a strict popup blocker), the hand-over URL is shown as a link that opens a new tab. A link click is a fresh
 * user gesture, which browsers always allow. Place it once in the embed layout.
 */
@Component({
  selector: 'spark-embed-continue',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-embed-continue.component.html',
  styleUrl: './spark-embed-continue.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkEmbedContinueComponent {
  protected readonly embed = inject(SparkEmbedService);
}
