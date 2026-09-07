import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { BrowseService, GateSettings } from '../../services/browse.service';
import { RepoGatePanelComponent } from './repo-gate-panel.component';

/**
 * The gate panel edits the policy the check runs judge against, so the two properties worth
 * pinning are about *not* showing or sending the wrong thing: it must render nothing for a caller
 * who cannot manage the repository, and an emptied number input must reach the API as null rather
 * than as NaN.
 */
function aGate(overrides: Partial<GateSettings> = {}): GateSettings {
  return {
    projectMode: 'auto',
    projectBasis: 'scoped',
    projectTarget: null,
    projectThreshold: 0,
    patchTarget: null,
    patchThreshold: 0,
    blocking: false,
    ...overrides,
  } as GateSettings;
}

describe('RepoGatePanelComponent', () => {
  let fixture: ComponentFixture<RepoGatePanelComponent>;
  let browse: {
    getRepo: ReturnType<typeof vi.fn>;
    getGate: ReturnType<typeof vi.fn>;
    putGate: ReturnType<typeof vi.fn>;
  };

  async function setup(canManage: boolean, gate: GateSettings | null = aGate()) {
    browse = {
      getRepo: vi.fn().mockResolvedValue({ canManage }),
      getGate: vi.fn().mockResolvedValue(gate),
      putGate: vi.fn().mockImplementation((_o, _n, g) => Promise.resolve(g)),
    };

    TestBed.configureTestingModule({
      imports: [RepoGatePanelComponent],
      providers: [provideZonelessChangeDetection(), { provide: BrowseService, useValue: browse }],
    });

    fixture = TestBed.createComponent(RepoGatePanelComponent);
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('name', 'widget');
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  /**
   * The API refuses regardless, so this is not the security boundary — but rendering an editable
   * policy form that cannot be saved is its own bug, and it is the one a reader would notice.
   */
  it('renders nothing, and does not fetch the gate, for a caller who cannot manage the repo', async () => {
    const component = await setup(false);

    expect(component.canManage()).toBe(false);
    expect(browse.getGate).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).not.toContain('Coverage gate');
  });

  it('loads the gate for a manager', async () => {
    const component = await setup(true);

    expect(component.canManage()).toBe(true);
    expect(browse.getGate).toHaveBeenCalledWith('acme', 'widget');
    expect(component.gate()).not.toBeNull();
  });

  /**
   * A failed lookup must close the panel rather than leave it half-rendered. Treating an error as
   * "manager" would show an editable form whose every save then fails.
   */
  it('treats a failed repo lookup as not-a-manager', async () => {
    browse = {
      getRepo: vi.fn().mockRejectedValue(new Error('boom')),
      getGate: vi.fn(),
      putGate: vi.fn(),
    };

    TestBed.configureTestingModule({
      imports: [RepoGatePanelComponent],
      providers: [provideZonelessChangeDetection(), { provide: BrowseService, useValue: browse }],
    });

    fixture = TestBed.createComponent(RepoGatePanelComponent);
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('name', 'widget');
    await fixture.whenStable();

    expect(fixture.componentInstance.canManage()).toBe(false);
  });

  /**
   * The comment in the component says it: an emptied number input round-trips as NaN or '', and
   * the API wants null. Sending NaN serialises to `null` in JSON anyway, but sending '' does not —
   * it reaches the server as a string and fails validation, so the user sees "saving failed" with
   * no way to clear a target.
   */
  it('sends an emptied target as null rather than NaN or empty string', async () => {
    const component = await setup(true, aGate({ projectTarget: NaN as never, patchTarget: '' as never }));

    await component.save();

    const sent = browse.putGate.mock.calls[0][2];
    expect(sent.projectTarget).toBeNull();
    expect(sent.patchTarget).toBeNull();
  });

  it('keeps a real target untouched', async () => {
    const component = await setup(true, aGate({ projectMode: 'fixed', projectTarget: 80 }));

    await component.save();

    expect(browse.putGate.mock.calls[0][2].projectTarget).toBe(80);
  });

  it('reports the server error text, and clears the saving flag either way', async () => {
    const component = await setup(true);
    browse.putGate.mockRejectedValue({ error: { error: 'projectMode must be auto or fixed.' } });

    await component.save();

    expect(component.error()).toBe('projectMode must be auto or fixed.');
    expect(component.savedAt()).toBe(false);
    // finally, not after the try: a failed save must not leave the button disabled forever.
    expect(component.saving()).toBe(false);
  });

  it('falls back to a generic message when the server sends no error text', async () => {
    const component = await setup(true);
    browse.putGate.mockRejectedValue(new Error('network'));

    await component.save();

    expect(component.error()).toBe('Saving failed.');
    expect(component.saving()).toBe(false);
  });

  it('does nothing when there is no gate to save', async () => {
    const component = await setup(true, null);

    await component.save();

    expect(browse.putGate).not.toHaveBeenCalled();
  });
});
