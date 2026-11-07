import { ApplicationRef, DOCUMENT, EnvironmentInjector, Injectable, createComponent, inject } from '@angular/core';
import { PersistentObject } from '@mintplayer/ng-spark/models';
import { AddNewReferenceRequest, SparkPoNewModalComponent } from './spark-po-new-modal.component';

/**
 * Opens the **Add new** popup (reference add-new PRD D16). Every opener — the single picker, an array
 * Reference's tree-select, a form inside a retry prompt, another popup — makes the same call, and each
 * call renders its own modal, so they stack (ui-core's overlay stack handles z-order, Escape and focus).
 * Nothing has to be placed in the app's shell.
 */
@Injectable({ providedIn: 'root' })
export class SparkPoNewModalService {
  private readonly appRef = inject(ApplicationRef);
  private readonly environmentInjector = inject(EnvironmentInjector);
  private readonly document = inject(DOCUMENT);

  /** Resolves with the created object, or `null` when the user cancelled. */
  open(request: AddNewReferenceRequest): Promise<PersistentObject | null> {
    return new Promise(resolve => {
      const ref = createComponent(SparkPoNewModalComponent, { environmentInjector: this.environmentInjector });
      ref.setInput('request', request);
      const subscription = ref.instance.closed.subscribe(result => {
        subscription.unsubscribe();
        this.appRef.detachView(ref.hostView);
        ref.destroy();
        resolve(result);
      });
      this.appRef.attachView(ref.hostView);
      this.document.body.appendChild(ref.location.nativeElement);
    });
  }
}
