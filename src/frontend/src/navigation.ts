export const pages = [
  { id: 'chat', path: '/', label: 'Helpdesk chat' },
  { id: 'examples', path: '/examples', label: 'Example questions' },
  { id: 'architecture', path: '/architecture', label: 'Architecture & agent' },
] as const;

export type Page = typeof pages[number]['id'];

export function pageFromPath(pathname: string): Page {
  if (pathname.startsWith('/architecture')) return 'architecture';
  if (pathname.startsWith('/examples')) return 'examples';
  return 'chat';
}

export function pagePath(page: Page): string {
  const route = pages.find(route => route.id === page);
  if (!route) throw new Error('Unknown helpdesk page.');
  return route.path;
}
