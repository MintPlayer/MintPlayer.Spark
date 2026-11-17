import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkShellComponent } from '@mintplayer/ng-spark/shell';

/** The application frame: Spark's shell builds the menu from App_Data/programUnits.json. */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, SparkShellComponent],
  template: `
    <spark-shell title="SparkApp">
      <router-outlet />
    </spark-shell>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {}
