import { Component, ChangeDetectionStrategy } from '@angular/core';

@Component({
  selector: 'app-home',
  template: `
    <h1>Spark Editor</h1>
    <p>Select an item from the sidebar to start editing your Spark configuration.</p>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class HomeComponent {}
