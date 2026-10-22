import { InteractionRequiredAuthError, PublicClientApplication } from '@azure/msal-browser'

const tenant = import.meta.env.VITE_ENTRA_TENANT_ID as string | undefined
const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID as string | undefined
const scope = import.meta.env.VITE_ENTRA_API_SCOPE as string | undefined
export const identityConfigured = Boolean(tenant && clientId && scope)
// Session-only cache; never accept pasted bearer tokens or put credentials in Vite variables.
const client = identityConfigured ? new PublicClientApplication({
  auth: {
    clientId: clientId!,
    authority: `https://login.microsoftonline.com/${tenant}`,
    redirectUri: new URL(`${import.meta.env.BASE_URL}redirect.html`, window.location.origin).href,
    postLogoutRedirectUri: new URL(`${import.meta.env.BASE_URL}#evaluation`, window.location.origin).href,
  },
  cache: { cacheLocation: 'sessionStorage' },
}) : null
let initialization: Promise<void> | undefined
async function initialize() {
  if (!client) throw new Error('Configure the Entra tenant, SPA client ID and delegated API scope first.')
  initialization ??= client.initialize().then(async () => {
    const response = await client.handleRedirectPromise()
    if (response) client.setActiveAccount(response.account)
    else if (!client.getActiveAccount()) client.setActiveAccount(client.getAllAccounts()[0] ?? null)
  })
  await initialization
  return client
}
export const auth = {
  async account() { return (await initialize()).getActiveAccount()?.username ?? null },
  async signIn() {
    const msal = await initialize()
    await msal.loginRedirect({ scopes: [scope!] })
  },
  async signOut() { await (await initialize()).logoutRedirect() },
  async token() {
    const msal = await initialize()
    const account = msal.getActiveAccount()
    if (!account) throw new Error('Sign in to access Evaluation.')
    try {
      return (await msal.acquireTokenSilent({ account, scopes: [scope!] })).accessToken
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) {
        await msal.acquireTokenRedirect({ account, scopes: [scope!] })
      }
      throw error
    }
  },
}
