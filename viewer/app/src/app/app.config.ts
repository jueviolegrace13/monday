import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { environment } from '../environments/environment';
import { MondayApi } from './api/monday-api';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    {
      provide: MondayApi,
      useFactory: () =>
        new MondayApi({ fetch: (input, init) => fetch(input, init), devUser: environment.devUser }),
    },
  ],
};
