import { ChangeDetectionStrategy, Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { PersistentObject, EntityType } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { GitHubProjectsService } from '../../services/github-projects.service';
import { GitHubProjectInfo } from '../../models/github-project';
import { Color } from '@mintplayer/ng-bootstrap';

interface ProjectRow extends GitHubProjectInfo {
  enabled: boolean;
  sparkDocumentId?: string;
  loading: boolean;
}

@Component({
  selector: 'app-github-projects',
  imports: [CommonModule, RouterModule, BsCardComponent, BsCardHeaderComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsAlertComponent, BsTableComponent, TranslateKeyPipe],
  templateUrl: './github-projects.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class GitHubProjectsComponent implements OnInit {
  private readonly ghService = inject(GitHubProjectsService);
  private readonly sparkService = inject(SparkService);
  private readonly lang = inject(SparkLanguageService);
  readonly colors = Color;

  private entityType: EntityType | undefined;
  entityTypeId = '';

  projects = signal<ProjectRow[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  installAppUrl = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await Promise.all([this.loadProjects(), this.loadAppInfo()]);
  }

  private async loadAppInfo(): Promise<void> {
    try {
      const info = await this.ghService.getAppInfo();
      const slug = info.productionAppSlug;
      this.installAppUrl.set(slug ? `https://github.com/apps/${slug}/installations/new` : null);
    } catch {
      // Soft-fail: link just stays hidden if the backend can't tell us the slug.
      this.installAppUrl.set(null);
    }
  }

  private async loadProjects(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);

    try {
      // Reads the declared query rather than the old "list every row of this type" endpoint, which
      // has been removed: it was a second list pipeline with no paging, no search, no sort and — the
      // reason it is gone — no cap at all, while /execute clamps take for exactly that reason.
      const projectsQuery = await this.sparkService.getQueryByName('GetGitHubProjects');
      const [ghProjects, sparkRows, entityType] = await Promise.all([
        this.ghService.listProjects(),
        projectsQuery
          // Explicit take: the server default is 50, and this list is matched against every GitHub
          // project, so a silent truncation would render enabled projects as disabled.
          ? this.sparkService.executeQuery(projectsQuery.id, { take: 500 })
          : Promise.resolve({ items: [] as QueryResultItem[] }),
        this.sparkService.getEntityTypeByClrType('WebhooksDemo.Entities.GitHubProject'),
      ]);
      this.entityType = entityType;
      this.entityTypeId = entityType?.id ?? 'GitHubProject';

      // Rows carry their values as a keyed list rather than as attributes, and the row id IS the
      // document id — which is all this page needed from the entity it used to load.
      const enabledMap = new Map<string, string>();
      for (const row of sparkRows.items) {
        const nodeId = row.values.find(v => v.key === 'NodeId')?.value;
        if (nodeId) {
          enabledMap.set(String(nodeId), row.id);
        }
      }

      this.projects.set(ghProjects.map(p => {
        const sparkDocumentId = enabledMap.get(p.id);
        return {
          ...p,
          enabled: !!sparkDocumentId,
          sparkDocumentId,
          loading: false,
        };
      }));
    } catch (err: any) {
      this.error.set(err.message || this.lang.t('app.failedToLoadProjects'));
    } finally {
      this.loading.set(false);
    }
  }

  async toggleProject(project: ProjectRow): Promise<void> {
    const idx = this.projects().indexOf(project);
    if (idx < 0) return;

    this.updateProject(idx, { loading: true });

    try {
      if (project.enabled && project.sparkDocumentId) {
        await this.sparkService.delete('GitHubProject', project.sparkDocumentId);
        this.updateProject(idx, { enabled: false, sparkDocumentId: undefined, loading: false });
      } else {
        if (!this.entityType) throw new Error(this.lang.t('entityTypeNotLoaded'));

        const attrDef = (name: string) => this.entityType!.attributes.find(a => a.name === name);
        const created = await this.sparkService.create('GitHubProject', {
          name: '',
          objectTypeId: this.entityType.id,
          attributes: [
            { ...attrDef('Name')!, value: project.title },
            { ...attrDef('InstallationId')!, value: project.installationId },
            { ...attrDef('NodeId')!, value: project.id },
            { ...attrDef('OwnerLogin')!, value: project.ownerLogin },
            { ...attrDef('Number')!, value: project.number },
          ],
        });
        this.updateProject(idx, { enabled: true, sparkDocumentId: created.id, loading: false });
      }
    } catch (err: any) {
      this.updateProject(idx, { loading: false });
      this.error.set(err.message || this.lang.t('operationFailed'));
    }
  }

  private updateProject(idx: number, patch: Partial<ProjectRow>): void {
    const current = this.projects();
    this.projects.set([
      ...current.slice(0, idx),
      { ...current[idx], ...patch },
      ...current.slice(idx + 1),
    ]);
  }
}
