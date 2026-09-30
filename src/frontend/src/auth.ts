import { PublicClientApplication, InteractionRequiredAuthError, type AccountInfo } from '@azure/msal-browser';
import type { RuntimeConfig } from './contracts';
export class EmployeeAuth {
  readonly client: PublicClientApplication;
  constructor(readonly config: RuntimeConfig) {
    this.client = new PublicClientApplication({
      auth: { clientId: config.entraClientId, authority: `https://login.microsoftonline.com/${config.entraTenantId}`, redirectUri: window.location.origin, postLogoutRedirectUri: window.location.origin },
      cache: { cacheLocation: 'sessionStorage' },
    });
  }
  async initialize(): Promise<AccountInfo | null> {
    await this.client.initialize();
    const redirect = await this.client.handleRedirectPromise();
    const account = redirect?.account || this.client.getActiveAccount() || this.client.getAllAccounts()[0] || null;
    if (account && account.tenantId.toLowerCase() !== this.config.entraTenantId.toLowerCase()) throw new Error('The signed-in account belongs to a different Entra tenant.');
    this.client.setActiveAccount(account);
    return account;
  }
  async signIn() { await this.client.loginRedirect({ scopes: [this.config.entraApiScope], prompt: 'select_account' }); }
  async signOut() { await this.client.logoutRedirect(); }
  async token(): Promise<string> {
    const account = this.client.getActiveAccount();
    if (!account) throw new Error('Sign in with your employee account before using the helpdesk.');
    try {
      return (await this.client.acquireTokenSilent({ scopes: [this.config.entraApiScope], account })).accessToken;
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) {
        await this.client.acquireTokenRedirect({ scopes: [this.config.entraApiScope], account });
        throw new Error('Additional employee sign-in is required. Redirecting to Microsoft Entra.');
      }
      throw error;
    }
  }
}
