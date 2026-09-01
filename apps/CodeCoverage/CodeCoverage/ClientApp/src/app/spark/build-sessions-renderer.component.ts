import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';

interface SessionView {
  key: string;
  name: string;
  flags: string[];
  parseStatus: string;
  error?: string;
}

/**
 * Spark attribute renderer "build-sessions": the Build.Sessions AsDetail array
 * as master's compact per-session lines (job name, flag badges, parse-status
 * badge, error). The value arrives as nested PersistentObjects once Spark#241
 * ships the AsDetail renderer value; until then it renders nothing.
 */
@Component({
  selector: 'app-build-sessions-renderer',
  imports: [BsBadgeComponent],
  template: `
    @for (session of sessions(); track session.key) {
      <div class="small">
        {{ session.name }}
        @for (flag of session.flags; track flag) {
          <bs-badge class="text-bg-light ms-1">{{ flag }}</bs-badge>
        }
        <bs-badge class="ms-1"
                  [class]="session.parseStatus === 'Parsed' ? 'text-bg-success' : session.parseStatus === 'Pending' ? 'text-bg-warning' : 'text-bg-danger'">
          {{ session.parseStatus }}
        </bs-badge>
        @if (session.error) {
          <span class="text-danger ms-1">{{ session.error }}</span>
        }
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class BuildSessionsRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly sessions = computed<SessionView[]>(() => {
    const value = this.value();
    if (!Array.isArray(value)) return [];
    return value.map((entry, index) => {
      const dict = this.toDict(entry);
      const flags = dict['Flags'];
      return {
        key: String(dict['SessionId'] ?? index),
        name: String(dict['JobName'] ?? dict['SessionId'] ?? ''),
        flags: Array.isArray(flags) ? flags.map(String) : [],
        parseStatus: String(dict['ParseStatus'] ?? ''),
        error: dict['Error'] ? String(dict['Error']) : undefined,
      };
    });
  });

  private toDict(entry: unknown): Record<string, any> {
    // AsDetail nested PersistentObject → name/value dict; flat dict passes through.
    const po = entry as PersistentObject;
    if (Array.isArray(po?.attributes)) {
      return Object.fromEntries(po.attributes.map((a) => [a.name, a.value]));
    }
    return (entry ?? {}) as Record<string, any>;
  }
}
