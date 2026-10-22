#!/usr/bin/env pwsh
# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
#Requires -Version 7.4

<#
.SYNOPSIS
    Creates or updates the Entra app registrations for the Evaluation workspace.
.DESCRIPTION
    Idempotently registers a single-tenant API app exposing the delegated scope
    Evaluation.Access (v2 access tokens) and a SPA app whose redirect URIs point to
    the MSAL redirect bridge. The SPA is pre-authorized for the scope so users are not
    prompted for consent. Existing redirect URIs are preserved and new ones merged.
    The script refuses to run unless the active Azure CLI tenant equals -TenantId.
.PARAMETER TenantId
    Entra tenant that owns the registrations.
.PARAMETER RedirectUri
    SPA redirect URIs (the redirect.html bridge) to add.
.PARAMETER ApiDisplayName
    Display name of the API registration.
.PARAMETER SpaDisplayName
    Display name of the SPA registration.
.PARAMETER ManagedIdentityPrincipalId
    Object ID of the API's user-assigned managed identity. When set, the API registration trusts it through a
    federated identity credential (no client secret) and gets admin-consented delegated User.Invite.All, so the API
    can send B2B invitations on behalf of the signed-in administrator.
.EXAMPLE
    ./Register-EntraApps.ps1 -TenantId <tenant> -RedirectUri http://localhost:5173/Apex-Dynamics-Response-Systems/redirect.html
.NOTES
    Outputs JSON with tenantId, apiAppId, spaClientId, and scope. No secrets are created.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [Parameter(Mandatory = $false)]
    [string[]]$RedirectUri = @(),

    [Parameter(Mandatory = $false)]
    [string]$ApiDisplayName = 'Apex Evaluation API',

    [Parameter(Mandatory = $false)]
    [string]$SpaDisplayName = 'Apex Evaluation Web',

    [Parameter(Mandatory = $false)]
    [string]$ManagedIdentityPrincipalId
)

$ErrorActionPreference = 'Stop'

#region Functions

function Invoke-Graph {
    [CmdletBinding()]
    [OutputType([object])]
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $false)][object]$Body
    )
    # Direct REST with a CLI-issued token: 'az rest' to Graph can fail CAE challenges that need interactive sign-in.
    $token = az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or -not $token) { throw 'Could not acquire a Microsoft Graph token.' }
    $request = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0$Path"; Headers = @{ Authorization = "Bearer $token" } }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 10); $request.ContentType = 'application/json' }
    try { return Invoke-RestMethod @request }
    catch { throw "Graph $Method $Path failed ($($_.Exception.Response.StatusCode))." }
}

function Get-OrCreateApplication {
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory = $true)][string]$DisplayName)
    $filter = [uri]::EscapeDataString("displayName eq '$DisplayName'")
    $found = @((Invoke-Graph -Method GET -Path "/applications?`$filter=$filter").value)
    if ($found.Count -gt 1) { throw "Multiple applications named '$DisplayName'; resolve manually." }
    if ($found.Count -eq 1) { return $found[0] }
    Write-Host "Creating application '$DisplayName'" -ForegroundColor Cyan
    return Invoke-Graph -Method POST -Path '/applications' -Body @{ displayName = $DisplayName; signInAudience = 'AzureADMyOrg' }
}

function Confirm-ServicePrincipal {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][string]$AppId)
    $filter = [uri]::EscapeDataString("appId eq '$AppId'")
    $existing = @((Invoke-Graph -Method GET -Path "/servicePrincipals?`$filter=$filter").value)
    if ($existing.Count -eq 0) { return (Invoke-Graph -Method POST -Path '/servicePrincipals' -Body @{ appId = $AppId }).id }
    return $existing[0].id
}

function Confirm-TenantConsent {
    # Tenant-wide (AllPrincipals) delegated consent so invited guests are never stopped by a consent prompt.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ClientSpId,
        [Parameter(Mandatory = $true)][string]$ResourceSpId,
        [Parameter(Mandatory = $true)][string[]]$Scopes
    )
    $filter = [uri]::EscapeDataString("clientId eq '$ClientSpId' and resourceId eq '$ResourceSpId' and consentType eq 'AllPrincipals'")
    $grant = @((Invoke-Graph -Method GET -Path "/oauth2PermissionGrants?`$filter=$filter").value)[0]
    if ($null -eq $grant) {
        Invoke-Graph -Method POST -Path '/oauth2PermissionGrants' -Body @{
            clientId = $ClientSpId; consentType = 'AllPrincipals'; resourceId = $ResourceSpId; scope = ($Scopes -join ' ')
        } | Out-Null
        return
    }
    $merged = @(@($grant.scope -split ' ') + $Scopes | Where-Object { $_ } | Sort-Object -Unique)
    if (($merged -join ' ') -ne (@($grant.scope -split ' ' | Where-Object { $_ } | Sort-Object -Unique) -join ' ')) {
        Invoke-Graph -Method PATCH -Path "/oauth2PermissionGrants/$($grant.id)" -Body @{ scope = ($merged -join ' ') } | Out-Null
    }
}

#endregion Functions

#region Main Execution

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $activeTenant = az account show --query tenantId -o tsv
        if ($LASTEXITCODE -ne 0 -or $activeTenant -ne $TenantId) {
            throw "Active Azure CLI tenant '$activeTenant' does not match '$TenantId'. Select the correct account first."
        }

        $api = Get-OrCreateApplication -DisplayName $ApiDisplayName
        $scope = @($api.api.oauth2PermissionScopes | Where-Object value -eq 'Evaluation.Access')[0]
        $scopeId = $scope.id ?? [guid]::NewGuid().ToString()
        $scopes = @(@($api.api.oauth2PermissionScopes | Where-Object value -ne 'Evaluation.Access') + @{
                id                      = $scopeId
                value                   = 'Evaluation.Access'
                type                    = 'User'
                isEnabled               = $true
                adminConsentDisplayName = 'Access the Evaluation workspace'
                adminConsentDescription = 'Allows the app to call the Evaluation API as the signed-in user.'
                userConsentDisplayName  = 'Access the Evaluation workspace'
                userConsentDescription  = 'Allows the app to call the Evaluation API on your behalf.'
            } | Where-Object { $_ })
        Invoke-Graph -Method PATCH -Path "/applications/$($api.id)" -Body @{
            identifierUris = @("api://$($api.appId)")
            api            = @{ requestedAccessTokenVersion = 2; oauth2PermissionScopes = $scopes }
            # email lets the API show invited guests by address; guests' preferred_username is a #EXT# UPN.
            optionalClaims = @{ accessToken = @(@{ name = 'email'; essential = $false }); idToken = @(); saml2Token = @() }
        } | Out-Null

        $spa = Get-OrCreateApplication -DisplayName $SpaDisplayName
        $redirects = @(@($spa.spa.redirectUris) + $RedirectUri | Where-Object { $_ } | Sort-Object -Unique)
        $graphAppId = '00000003-0000-0000-c000-000000000000'
        $graphSp = @((Invoke-Graph -Method GET -Path "/servicePrincipals?`$filter=$([uri]::EscapeDataString("appId eq '$graphAppId'"))").value)[0]
        $signInScopes = @('openid', 'profile', 'offline_access', 'email')
        $graphAccess = @($graphSp.oauth2PermissionScopes | Where-Object { $_.value -in $signInScopes } | ForEach-Object { @{ id = $_.id; type = 'Scope' } })
        Invoke-Graph -Method PATCH -Path "/applications/$($spa.id)" -Body @{
            spa                    = @{ redirectUris = $redirects }
            requiredResourceAccess = @(
                @{ resourceAppId = $api.appId; resourceAccess = @(@{ id = $scopeId; type = 'Scope' }) },
                @{ resourceAppId = $graphAppId; resourceAccess = $graphAccess }
            )
        } | Out-Null

        Invoke-Graph -Method PATCH -Path "/applications/$($api.id)" -Body @{
            api = @{
                requestedAccessTokenVersion = 2
                oauth2PermissionScopes      = $scopes
                preAuthorizedApplications   = @(@{ appId = $spa.appId; delegatedPermissionIds = @($scopeId) })
            }
        } | Out-Null

        $apiSpId = Confirm-ServicePrincipal -AppId $api.appId
        $spaSpId = Confirm-ServicePrincipal -AppId $spa.appId
        Confirm-TenantConsent -ClientSpId $spaSpId -ResourceSpId $apiSpId -Scopes @('Evaluation.Access')
        Confirm-TenantConsent -ClientSpId $spaSpId -ResourceSpId $graphSp.id -Scopes $signInScopes

        if ($ManagedIdentityPrincipalId) {
            $credentialName = 'apex-evaluation-managed-identity'
            $existingFic = @((Invoke-Graph -Method GET -Path "/applications/$($api.id)/federatedIdentityCredentials").value |
                    Where-Object name -eq $credentialName)
            $fic = @{
                name = $credentialName; issuer = "https://login.microsoftonline.com/$TenantId/v2.0"
                subject = $ManagedIdentityPrincipalId; audiences = @('api://AzureADTokenExchange')
                description = 'Lets the Evaluation API managed identity act as this app for on-behalf-of Graph calls (no secret).'
            }
            if ($existingFic.Count -eq 0) { Invoke-Graph -Method POST -Path "/applications/$($api.id)/federatedIdentityCredentials" -Body $fic | Out-Null }
            elseif ($existingFic[0].subject -ne $ManagedIdentityPrincipalId) {
                Invoke-Graph -Method PATCH -Path "/applications/$($api.id)/federatedIdentityCredentials/$($existingFic[0].id)" -Body $fic | Out-Null
            }
            $inviteScope = @($graphSp.oauth2PermissionScopes | Where-Object value -eq 'User.Invite.All')[0]
            $readBasicScope = @($graphSp.oauth2PermissionScopes | Where-Object value -eq 'User.ReadBasic.All')[0]
            Invoke-Graph -Method PATCH -Path "/applications/$($api.id)" -Body @{
                requiredResourceAccess = @(@{ resourceAppId = $graphAppId; resourceAccess = @(
                            @{ id = $inviteScope.id; type = 'Scope' }, @{ id = $readBasicScope.id; type = 'Scope' }) })
            } | Out-Null
            # User.ReadBasic.All (delegated): people search runs with the signed-in user's own directory permissions,
            # so tenant restrictions on guests' directory access still apply.
            Confirm-TenantConsent -ClientSpId $apiSpId -ResourceSpId $graphSp.id -Scopes @('User.Invite.All', 'User.ReadBasic.All')
        }

        Write-Host "✅ Entra registrations ready ($($redirects.Count) redirect URI(s))" -ForegroundColor Green
        [ordered]@{
            tenantId     = $TenantId
            apiAppId     = $api.appId
            spaClientId  = $spa.appId
            scope        = "api://$($api.appId)/Evaluation.Access"
            redirectUris = $redirects
        } | ConvertTo-Json
        exit 0
    }
    catch {
        Write-Error -ErrorAction Continue "Register-EntraApps failed: $($_.Exception.Message)"
        exit 1
    }
}

#endregion Main Execution
