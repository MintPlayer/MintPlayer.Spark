import { ChangeDetectionStrategy, Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { SparkService, PersistentObject } from '@mintplayer/ng-spark';
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
  imports: [CommonModule, RouterModule, BsCardComponent, BsCardHeaderComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsAlertComponent],
  templateUrl: './github-projects.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class GitHubProjectsComponent implements OnInit {
  private readonly ghService = inject(GitHubProjectsService);
  private readonly sparkService = inject(SparkService);
  readonly colors = Color;

  projects = signal<ProjectRow[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.loadProjects();
  }

  private async loadProjects(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);

    try {
      const [ghProjects, sparkEntities] = await Promise.all([
        this.ghService.listProjects(),
        this.sparkService.list('GitHubProject'),
      ]);

      const enabledMap = new Map<string, PersistentObject>();
      for (const entity of sparkEntities) {
        const nodeIdAttr = entity.attributes.find(a => a.name === 'NodeId');
        if (nodeIdAttr?.value) {
          enabledMap.set(nodeIdAttr.value, entity);
        }
      }

      this.projects.set(ghProjects.map(p => {
        const sparkEntity = enabledMap.get(p.id);
        return {
          ...p,
          enabled: !!sparkEntity,
          sparkDocumentId: sparkEntity?.id,
          loading: false,
        };
      }));
    } catch (err: any) {
      this.error.set(err.message || 'Failed to load projects');
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
        const created = await this.sparkService.create('GitHubProject', {
          name: '',
          objectTypeId: 'GitHubProject',
          attributes: [
            { id: '', name: 'Name', value: project.title, dataType: 'String', isRequired: false, isVisible: true, isReadOnly: false, order: 0, rules: [] },
            { id: '', name: 'NodeId', value: project.id, dataType: 'String', isRequired: false, isVisible: true, isReadOnly: false, order: 1, rules: [] },
            { id: '', name: 'OwnerLogin', value: project.ownerLogin, dataType: 'String', isRequired: false, isVisible: true, isReadOnly: false, order: 2, rules: [] },
            { id: '', name: 'Number', value: project.number, dataType: 'Int32', isRequired: false, isVisible: true, isReadOnly: false, order: 3, rules: [] },
          ],
        });
        this.updateProject(idx, { enabled: true, sparkDocumentId: created.id, loading: false });
      }
    } catch (err: any) {
      this.updateProject(idx, { loading: false });
      this.error.set(err.message || 'Operation failed');
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
