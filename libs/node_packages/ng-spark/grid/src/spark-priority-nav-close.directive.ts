import { Directive, inject } from '@angular/core';
import { BsPriorityNavComponent } from '@mintplayer/ng-bootstrap/priority-nav';

/**
 * Closes a `<bs-priority-nav>`'s "More" overflow once one of its actions is chosen.
 *
 * `bs-priority-nav` closes its overflow on a click OUTSIDE the nav and on Escape, but a click on an
 * item inside the overflow is inside the nav, so the list stayed open after "Add a passkey" ran —
 * at phone width, where every action lives in the overflow, after every action. A menu of actions
 * should close when one is picked; until the nav does that itself, this does.
 *
 * Only an activating control closes it: a `button`, a link or a `menuitem`. A control that opens
 * something of its own (`aria-haspopup` / `aria-expanded`, e.g. an add-on's nested dropdown) leaves
 * the overflow open, or its own menu would vanish with it. Disabled buttons fire no click.
 */
@Directive({
  selector: 'bs-priority-nav[sparkCloseOnAction]',
  host: { '(click)': 'onClick($event)' },
})
export class SparkPriorityNavCloseOnActionDirective {
  private readonly nav = inject(BsPriorityNavComponent);

  onClick(event: Event): void {
    const target = event.target;
    if (!(target instanceof Element) || !this.nav.isMoreOpen()) return;
    const overflow = target.closest('.priority-nav-overflow');
    if (!overflow) return;
    const control = target.closest('button, a[href], [role="menuitem"]');
    if (!control || !overflow.contains(control)) return;
    if (control.hasAttribute('aria-haspopup') || control.hasAttribute('aria-expanded')) return;
    this.nav.isMoreOpen.set(false);
  }
}
