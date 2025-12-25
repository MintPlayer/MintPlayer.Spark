import { Component, OnInit, ChangeDetectionStrategy, ChangeDetectorRef, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsShellModule } from '@mintplayer/ng-bootstrap/shell';
import { BsAccordionModule } from '@mintplayer/ng-bootstrap/accordion';
import { SparkService } from '../core/services/spark.service';
import { ProgramUnitGroup } from '../core/models';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [CommonModule, RouterModule, BsShellModule, BsAccordionModule],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent implements OnInit {
  private sparkService = inject(SparkService);
  private cdr = inject(ChangeDetectorRef);

  programUnitGroups: ProgramUnitGroup[] = [];
  shellState: 'auto' | 'show' | 'hide' = 'auto';

  ngOnInit(): void {
    this.sparkService.getProgramUnits().subscribe(config => {
      this.programUnitGroups = config.programUnitGroups.sort((a, b) => a.order - b.order);
      this.cdr.markForCheck();
    });
  }

  getRouterLink(unit: any): string[] {
    if (unit.type === 'query') {
      return ['/query', unit.queryId];
    } else if (unit.type === 'persistentObject') {
      return ['/po', unit.persistentObjectId];
    }
    return ['/'];
  }
}
