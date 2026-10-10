import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

@Component({
  selector: 'app-home',
  imports: [RouterLink, BsCardComponent, BsCardHeaderComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, TranslateKeyPipe],
  templateUrl: './home.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class HomeComponent {
  protected readonly auth = inject(SparkAuthService);
}
