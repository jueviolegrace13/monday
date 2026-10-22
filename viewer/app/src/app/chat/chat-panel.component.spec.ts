import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ChatPanelComponent } from './chat-panel.component';
import { ChatStore } from './chat-store';

@Component({ template: '' })
class Blank {}

/** Only what the panel reads: a project with no model set up yet. */
const store = {
  messages: signal([]),
  streaming: signal(false),
  notConfigured: signal(true),
  send: async () => undefined,
  stop: () => undefined,
} as unknown as ChatStore;

@Component({
  imports: [ChatPanelComponent],
  template: `<app-chat-panel
    [store]="store"
    projectId="p 1"
    [open]="true"
    [canManageModel]="true"
  />`,
})
class Host {
  readonly store = store;
}

describe('ChatPanelComponent', () => {
  it('links to model settings through the router, without reloading the page', async () => {
    await TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideRouter([{ path: 'settings/model', component: Blank }])],
    }).compileComponents();
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')].find(
      (a) => a.textContent?.trim() === 'Set up a model',
    )!;
    expect(link.getAttribute('href')).toBe('/settings/model?project=p%201');

    // RouterLink takes the click (default prevented), so the app is not reloaded.
    const click = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 });
    link.dispatchEvent(click);
    await fixture.whenStable();
    expect(click.defaultPrevented).toBe(true);
    expect(TestBed.inject(Router).url).toBe('/settings/model?project=p%201');
  });
});
