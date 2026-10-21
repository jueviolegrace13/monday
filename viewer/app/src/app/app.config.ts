import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { environment } from '../environments/environment';
import { MondayApi } from './api/monday-api';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    {
      provide: MondayApi,
      useFactory: () =>
        new MondayApi({ fetch: (input, init) => fetch(input, init), devUser: environment.devUser }),
    },
  ],
};
