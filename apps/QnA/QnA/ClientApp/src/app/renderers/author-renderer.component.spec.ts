import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { SparkModerationService } from '@mintplayer/ng-spark/moderation';
import { AuthorRendererComponent } from './author-renderer.component';

@Component({
  imports: [AuthorRendererComponent],
  template: `<app-author-renderer [value]="value()" />`,
})
class HostComponent {
  value = signal<unknown>(undefined);
}

describe('AuthorRendererComponent', () => {
  const reputation = vi.fn();

  beforeEach(async () => {
    reputation.mockReset();
    reputation.mockImplementation(async (userId?: string) => ({ userId, total: 42, pending: 0, privileges: [], suspended: false }));
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      // The badge's translate pipe loads /spark/translations; the testing backend answers nothing.
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkModerationService, useValue: { reputation } }],
    }).compileComponents();
  });

  it('shows the author\'s reputation badge for an author id', async () => {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.value.set('SparkUsers/1-A');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.qna-author')?.getAttribute('data-author-id')).toBe('SparkUsers/1-A');
    expect(reputation).toHaveBeenCalledWith('SparkUsers/1-A');
    expect(host.querySelector('spark-reputation-badge')).toBeTruthy();
  });

  it('renders nothing without an author (a post from before Moderation stamped one)', async () => {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.value.set(null);
    fixture.detectChanges();
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('.qna-author')).toBeNull();
    expect(reputation).not.toHaveBeenCalled();
  });
});
