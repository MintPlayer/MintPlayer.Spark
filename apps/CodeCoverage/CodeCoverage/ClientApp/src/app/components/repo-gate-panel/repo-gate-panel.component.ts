import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSelectComponent } from '@mintplayer/ng-bootstrap/select';
import { BsGridComponent, BsGridColumnDirective, BsGridRowDirective, BsColFormLabelDirective } from "@mintplayer/ng-bootstrap/grid";
import { BsForDirective } from "@mintplayer/ng-bootstrap/for";
import { BsFormComponent, BsFormControlDirective } from "@mintplayer/ng-bootstrap/form";
import { BsCheckboxComponent } from "@mintplayer/ng-bootstrap/checkbox";
import { BsButtonTypeDirective } from "@mintplayer/ng-bootstrap/button-type";
import { BrowseService, GateSettings } from '../../services/browse.service';

/**
 * "Coverage gate" card for the Repository detail page — the policy the
 * check-runs judge against. Manager-only: the panel self-fetches RepoInfo and
 * renders nothing without canManage (the API refuses regardless). Blocking is
 * deliberately presented as the opt-in it is: everything starts informational.
 */
@Component({
  selector: 'app-repo-gate-panel',
  imports: [FormsModule, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsSelectComponent, BsGridComponent, BsGridColumnDirective, BsGridRowDirective, BsForDirective, BsFormComponent, BsFormControlDirective, BsColFormLabelDirective, BsCheckboxComponent, BsButtonTypeDirective],
  template: `
    @if (canManage() && gate(); as g) {
      <bs-card class="mt-3 d-block">
        <bs-card-header><i class="bi bi-shield-check"></i> Coverage gate</bs-card-header>
        <bs-card-body>
          <bs-grid>
            <bs-form>
              <div bsRow class="g-3">
                <div [sm]="4">
                  <label [bsFor]="projectMode" bsColFormLabel class="mb-1">Project comparison</label>
                  <bs-select #projectMode [(ngModel)]="g.projectMode">
                    <option [ngValue]="'auto'">Ratchet against the base commit</option>
                    <option [ngValue]="'fixed'">Fixed target</option>
                  </bs-select>
                </div>
                @if (g.projectMode === 'fixed') {
                  <div [sm]="4">
                    <label [bsFor]="projectTarget" bsColFormLabel class="mb-1">Project target (%)</label>
                    <input type="number" #projectTarget min="0" max="100" step="0.1" [(ngModel)]="g.projectTarget">
                  </div>
                }
                <div [sm]="4">
                  <label [bsFor]="projectThreshold" bsColFormLabel class="mb-1">Allowed drop (points)</label>
                  <input type="number" #projectThreshold min="0" max="100" step="0.1" [(ngModel)]="g.projectThreshold">
                </div>
                <div [sm]="4">
                  <label [bsFor]="projectBasis" bsColFormLabel class="mb-1">Partial builds judge</label>
                  <bs-select [(ngModel)]="g.projectBasis" #projectBasis>
                    <option [ngValue]="'scoped'">Scoped baseline (like-for-like)</option>
                    <option [ngValue]="'projection'">Patched projection (whole workspace)</option>
                  </bs-select>
                </div>
                <div [sm]="4">
                  <label [bsFor]="patchTarget" bsColFormLabel class="mb-1">Patch target (%)</label>
                  <input type="number" #patchTarget min="0" max="100" step="0.1"
                        placeholder="off" [(ngModel)]="g.patchTarget">
                </div>
                <div [sm]="4">
                  <label [bsFor]="patchThreshold" bsColFormLabel class="mb-1">Patch tolerance (points)</label>
                  <input type="number" #patchThreshold min="0" max="100" step="0.1" [(ngModel)]="g.patchThreshold">
                </div>
              </div>

              <bs-checkbox name="gateBlocking" [(ngModel)]="g.blocking">
                Blocking — failed checks turn red. Off, the checks post the same numbers but never fail.
              </bs-checkbox>

              <div class="d-flex align-items-center gap-2 mt-3">
                <button [color]="colors.primary" (click)="save()" [disabled]="saving()">
                  <i class="bi bi-save"></i> Save gate
                </button>
                @if (savedAt()) { <span class="small text-success">Saved.</span> }
                @if (error()) { <span class="small text-danger">{{ error() }}</span> }
              </div>
              <div class="small text-muted mt-2">
                A <code>coverage.yml</code> in the repository overrides these per field, read from the base branch.
              </div>
            </bs-form>
          </bs-grid>
        </bs-card-body>
      </bs-card>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoGatePanelComponent {
  private readonly browse = inject(BrowseService);

  owner = input.required<string>();
  name = input.required<string>();

  readonly colors = Color;
  readonly canManage = signal(false);
  readonly gate = signal<GateSettings | null>(null);
  readonly saving = signal(false);
  readonly savedAt = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(async () => {
      const owner = this.owner();
      const name = this.name();
      try {
        const repo = await this.browse.getRepo(owner, name);
        this.canManage.set(repo.canManage);
        if (repo.canManage) {
          this.gate.set(await this.browse.getGate(owner, name));
        }
      } catch {
        this.canManage.set(false);
      }
    });
  }

  async save(): Promise<void> {
    const gate = this.gate();
    if (!gate) return;
    this.saving.set(true);
    this.savedAt.set(false);
    this.error.set(null);
    try {
      this.gate.set(await this.browse.putGate(this.owner(), this.name(), {
        ...gate,
        // An emptied number input round-trips as NaN/'' — the API wants null.
        projectTarget: numberOrNull(gate.projectTarget),
        patchTarget: numberOrNull(gate.patchTarget),
      }));
      this.savedAt.set(true);
    } catch (err) {
      this.error.set((err as { error?: { error?: string } })?.error?.error ?? 'Saving failed.');
    } finally {
      this.saving.set(false);
    }
  }
}

function numberOrNull(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}
