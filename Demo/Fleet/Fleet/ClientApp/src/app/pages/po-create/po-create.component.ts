import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkPoCreateComponent } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-po-create',
  imports: [SparkPoCreateComponent],
  templateUrl: './po-create.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoCreateComponent {}
