import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { createView, deleteView, listViews, updateView } from './views';

describe('saved views API', () => {
  it('lists, creates, updates and deletes through the project’s /filters route', async () => {
    const seen: string[] = [];
    const bodies: unknown[] = [];
    server.use(
      http.get('/api/projects/p1/filters', () => {
        seen.push('GET');
        return HttpResponse.json([]);
      }),
      http.post('/api/projects/p1/filters', async ({ request }) => {
        seen.push('POST');
        bodies.push(await request.json());
        return HttpResponse.json({ id: 'v1' }, { status: 201 });
      }),
      http.put('/api/projects/p1/filters/v1', async ({ request }) => {
        seen.push('PUT');
        bodies.push(await request.json());
        return HttpResponse.json({ id: 'v1' });
      }),
      http.delete('/api/projects/p1/filters/v1', () => {
        seen.push('DELETE');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await listViews('p1');
    await createView('p1', { name: 'A', sortBy: 'summary' });
    await updateView('p1', 'v1', { name: 'B', isDefault: true });
    await deleteView('p1', 'v1');

    expect(seen).toEqual(['GET', 'POST', 'PUT', 'DELETE']);
    expect(bodies).toEqual([{ name: 'A', sortBy: 'summary' }, { name: 'B', isDefault: true }]);
  });

  it('surfaces a refusal’s title as an ApiError', async () => {
    server.use(http.post('/api/projects/p1/filters', () =>
      HttpResponse.json({ title: "A view named 'A' already exists in this project." }, { status: 409 })));

    await expect(createView('p1', { name: 'A' })).rejects.toMatchObject({
      status: 409, title: "A view named 'A' already exists in this project.",
    });
  });
});
