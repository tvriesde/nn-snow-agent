import type { EmployeeAuth } from './auth';
import { parseChatResult, parseExamples } from './contracts';
type AuthTransport = Pick<EmployeeAuth, 'config' | 'token'>;
export async function apiRequest(auth: AuthTransport, route: string, message?: string, conversationId?: string): Promise<unknown> {
  const token = await auth.token();
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 90000);
  try {
    const response = await fetch(`${auth.config.apiBaseUrl}${route}`, {
      method: message === undefined ? 'GET' : 'POST',
      headers: { Authorization: `Bearer ${token}`, ...(message === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: message === undefined ? undefined : JSON.stringify({ message, ...(conversationId ? { conversationId } : {}) }),
      signal: controller.signal,
      credentials: 'omit',
    });
    if (!response.ok) {
      if (response.status === 401 || response.status === 403) throw new Error('The API rejected your employee authorization. Ask IT to verify your assigned access and Helpdesk.Access scope.');
      if (response.status === 429) throw new Error('The helpdesk request limit was reached. Wait a moment before retrying.');
      throw new Error(`The backend could not complete the request (HTTP ${response.status}). No agent answer was generated. Please retry or contact IT.`);
    }
    return await response.json();
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw new Error('The backend timed out. No agent answer was received. Free-tier cold starts can delay requests; please retry.');
    if (error instanceof TypeError) throw new Error('The backend is unreachable. Check your connection or ask IT to verify the API URL and CORS configuration.');
    throw error;
  } finally { clearTimeout(timeout); }
}
export const sendChat = async (auth: AuthTransport, message: string, conversationId?: string) => parseChatResult(await apiRequest(auth, '/api/chat', message, conversationId));
export const loadExamples = async (auth: AuthTransport) => parseExamples(await apiRequest(auth, '/api/examples'));
