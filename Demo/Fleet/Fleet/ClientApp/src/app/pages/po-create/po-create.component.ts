import { ChangeDetectionStrategy, Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BsColorPickerComponent } from '@mintplayer/ng-bootstrap/color-picker';
import { SparkPoCreateComponent, SparkFieldTemplateDirective } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-po-create',
  imports: [FormsModule, BsColorPickerComponent, SparkPoCreateComponent, SparkFieldTemplateDirective],
  templateUrl: './po-create.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoCreateComponent {}
