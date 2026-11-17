//#if (UseNotifications || UseOrganizations)
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
//#else
import { ChangeDetectionStrategy, Component } from '@angular/core';
//#endif
import { RouterOutlet } from '@angular/router';
//#if (UseNotifications || UseOrganizations)
import { SparkShellComponent, SparkShellTopbarEndDirective } from '@mintplayer/ng-spark/shell';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
//#else
import { SparkShellComponent } from '@mintplayer/ng-spark/shell';
//#endif
//#if (UseNotifications)
import { SparkNotificationBellComponent } from '@mintplayer/ng-spark/notifications';
//#endif
//#if (UseOrganizations)
import { SparkOrganizationSwitcherComponent } from '@mintplayer/ng-spark/organizations';
//#endif

/** The application frame: Spark's shell builds the menu from App_Data/programUnits.json. */
@Component({
  selector: 'app-shell',
//#if (UseNotifications && UseOrganizations)
  imports: [RouterOutlet, SparkShellComponent, SparkShellTopbarEndDirective, SparkNotificationBellComponent, SparkOrganizationSwitcherComponent],
//#elseif (UseNotifications)
  imports: [RouterOutlet, SparkShellComponent, SparkShellTopbarEndDirective, SparkNotificationBellComponent],
//#elseif (UseOrganizations)
  imports: [RouterOutlet, SparkShellComponent, SparkShellTopbarEndDirective, SparkOrganizationSwitcherComponent],
//#else
  imports: [RouterOutlet, SparkShellComponent],
//#endif
  template: `
    <spark-shell title="SparkApp">
//#if (UseNotifications || UseOrganizations)
      <ng-container *sparkShellTopbarEnd>
        @if (auth.isAuthenticated()) {
//#if (UseOrganizations)
          <!-- Lists of organization-owned rows follow the organization picked here. -->
          <spark-organization-switcher />
//#endif
//#if (UseNotifications)
          <!-- The live unread count, the recent notifications and the push opt-in. -->
          <spark-notification-bell />
//#endif
        }
      </ng-container>
//#endif
      <router-outlet />
    </spark-shell>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
//#if (UseNotifications || UseOrganizations)
export class ShellComponent {
  protected readonly auth = inject(SparkAuthService);
}
//#else
export class ShellComponent {}
//#endif
