import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkPoEditComponent } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-po-edit',
  imports: [SparkPoEditComponent],
  templateUrl: './po-edit.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoEditComponent {}
