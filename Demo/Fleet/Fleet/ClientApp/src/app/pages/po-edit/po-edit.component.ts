import { ChangeDetectionStrategy, Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BsColorPickerComponent } from '@mintplayer/ng-bootstrap/color-picker';
import { SparkPoEditComponent, SparkFieldTemplateDirective } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-po-edit',
  imports: [FormsModule, BsColorPickerComponent, SparkPoEditComponent, SparkFieldTemplateDirective],
  templateUrl: './po-edit.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoEditComponent {}
