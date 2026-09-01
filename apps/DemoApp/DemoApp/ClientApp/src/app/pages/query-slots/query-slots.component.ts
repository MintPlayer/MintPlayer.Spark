import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import {
  SparkQueryCardComponent,
  SparkQueryIconDirective,
  SparkQueryCaptionDirective,
  SparkQueryActionsDirective,
} from '@mintplayer/ng-spark/grid';

/**
 * Exercises the three `spark-query-card` slots, including the two claims that are easy to
 * assert in a unit test and easy to break in real markup: that an ABSENT slot still renders
 * the built-in default, and that a slot carrying a query alias targets only that card.
 *
 * The query ids are DemoApp's own (`GetCars`, `GetStocks`); `GetCars` has no alias, so it is
 * addressed by id exactly as an auto-rendered sub-query would be.
 */
@Component({
  selector: 'app-query-slots',
  imports: [
    BsAlertComponent,
    BsGridComponent, BsGridRowDirective, BsGridColumnDirective,
    SparkIconComponent,
    SparkQueryCardComponent,
    SparkQueryIconDirective, SparkQueryCaptionDirective, SparkQueryActionsDirective,
  ],
  templateUrl: './query-slots.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class QuerySlotsComponent {
  protected readonly colors = Color;

  protected readonly stocksQuery = 'all-stocks';
  protected readonly carsQuery = 'bc696815-2abb-4e7c-98a1-ac86b4352105';

  protected readonly lastAction = signal<string | null>(null);

  protected note(what: string): void {
    this.lastAction.set(what);
  }
}
