import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-home',
  template: `
    <h1>SparkApp</h1>
    <p>Your Spark application is running. Pick a collection in the menu to start.</p>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class HomeComponent {}
