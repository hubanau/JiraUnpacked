import { http, HttpResponse } from 'msw';

// Default handlers are intentionally empty: each test declares the API
// behaviour it depends on via server.use(...), so no test silently relies
// on a shared fixture it cannot see.
export const handlers = [
  http.get('/api/health-placeholder', () => HttpResponse.json({ ok: true })),
];
