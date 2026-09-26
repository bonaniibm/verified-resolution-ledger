// Verified Resolution Ledger – Azure infrastructure.
// Identity-based everywhere: one user-assigned managed identity for Storage, Service Bus, Key Vault, Azure OpenAI
// and (same-tenant) Dataverse. The only SAS key is a Send-only rule on the Dataverse events queue, because the
// Dataverse service endpoint cannot use managed identity.

targetScope = 'resourceGroup'

@description('Short name used to derive resource names (lowercase, 3-11 chars).')
@minLength(3)
@maxLength(11)
param baseName string = 'vrl'

@description('Region for everything except the Function App plan.')
param location string = resourceGroup().location

@description('Region for the Flex Consumption plan. Check support: az functionapp list-flexconsumption-locations -o table')
param functionLocation string = location

@description('Dataverse environment URL, e.g. https://contoso.crm.dynamics.com')
param dataverseUrl string

@description('Existing Azure OpenAI / AI Foundry account in this resource group used for embeddings. Leave empty to use local embeddings.')
param openAiAccountName string = ''

@description('Create the embedding deployment on the Foundry account.')
param deployEmbeddingModel bool = true

param embeddingDeploymentName string = 'text-embedding-3-small'
param embeddingModelName string = 'text-embedding-3-small'
param embeddingModelVersion string = '1'
@allowed([ 'Standard', 'GlobalStandard' ])
param embeddingSku string = 'GlobalStandard'
@description('Thousands of tokens per minute.')
param embeddingCapacity int = 50

@description('Cross-tenant only: tenant id of the Dataverse environment when it differs from this Azure tenant.')
param dataverseTenantId string = ''
@description('Cross-tenant only: client id of the app registration in the Dataverse tenant. Store its secret in Key Vault as "dataverse-client-secret".')
param dataverseClientId string = ''

@description('Object id of the person running the deployment. Grants Key Vault Secrets Officer (to store the Dataverse secret) and OpenAI User (to seed with the same embedding model as the Function).')
param deployerPrincipalId string = ''

param maximumInstanceCount int = 40
param tags object = {
  solution: 'verified-resolution-ledger'
  owner: 'contact-center-accelerators'
}

var suffix = uniqueString(resourceGroup().id, baseName)
var names = {
  identity: 'id-${baseName}'
  logs: 'log-${baseName}-${suffix}'
  appInsights: 'appi-${baseName}'
  storage: toLower(take('st${baseName}${suffix}', 24))
  serviceBus: 'sb-${baseName}-${suffix}'
  keyVault: take('kv-${baseName}-${suffix}', 24)
  plan: 'plan-${baseName}'
  functionApp: 'func-${baseName}-${suffix}'
}
var deploymentContainer = 'app-package'
var crossTenant = !empty(dataverseClientId)

// Built-in role definition ids
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  serviceBusDataOwner: '090c5cfd-751d-490a-894a-3ce6f1109419'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  cognitiveServicesOpenAiUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
  tags: tags
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: names.appInsights
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: true
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: names.storage
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
  resource blob 'blobServices' = {
    name: 'default'
    resource container 'containers' = {
      name: deploymentContainer
    }
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: names.serviceBus
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' } // sessions + duplicate detection require Standard or above
  properties: {
    minimumTlsVersion: '1.2'
    disableLocalAuth: false // required only for the Send-only rule used by the Dataverse service endpoint
  }

  // Raw Dataverse events (RemoteExecutionContext JSON). Non-session: Dataverse cannot set SessionId.
  resource dataverseEvents 'queues' = {
    name: 'vrl-dataverse-events'
    properties: {
      maxDeliveryCount: 10
      lockDuration: 'PT2M'
      deadLetteringOnMessageExpiration: true
      defaultMessageTimeToLive: 'P7D'
    }
    resource sendRule 'authorizationRules' = {
      name: 'dataverse-send'
      properties: { rights: [ 'Send' ] }
    }
  }

  // Canonical ledger commands, one session per customer key.
  resource interactions 'queues' = {
    name: 'vrl-interactions'
    properties: {
      requiresSession: true
      requiresDuplicateDetection: true
      duplicateDetectionHistoryTimeWindow: 'PT30M'
      maxDeliveryCount: 10
      lockDuration: 'PT2M'
      deadLetteringOnMessageExpiration: true
      defaultMessageTimeToLive: 'P7D'
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: names.keyVault
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = if (!empty(openAiAccountName)) {
  name: empty(openAiAccountName) ? 'none' : openAiAccountName
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = if (!empty(openAiAccountName) && deployEmbeddingModel) {
  parent: openAi
  name: embeddingDeploymentName
  sku: { name: embeddingSku, capacity: embeddingCapacity }
  properties: {
    model: { format: 'OpenAI', name: embeddingModelName, version: embeddingModelVersion }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: names.plan
  location: functionLocation
  tags: tags
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: names.functionApp
  location: functionLocation
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: concat([
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: identity.properties.clientId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'ClientId=${identity.properties.clientId};Authorization=AAD' }
        { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
        { name: 'ServiceBusConnection__fullyQualifiedNamespace', value: '${serviceBus.name}.servicebus.windows.net' }
        { name: 'ServiceBusConnection__credential', value: 'managedidentity' }
        { name: 'ServiceBusConnection__clientId', value: identity.properties.clientId }
        { name: 'Vrl__DataverseUrl', value: dataverseUrl }
        { name: 'Vrl__InteractionsQueue', value: 'vrl-interactions' }
        { name: 'Vrl__MaturationBatch', value: '500' }
        { name: 'Vrl__AoaiEndpoint', value: empty(openAiAccountName) ? '' : openAi!.properties.endpoint }
        { name: 'Vrl__AoaiDeployment', value: empty(openAiAccountName) ? '' : embeddingDeploymentName }
        { name: 'Vrl__AoaiModel', value: embeddingModelName }
        { name: 'Vrl__AoaiDimensions', value: '512' }
      ], crossTenant ? [
        { name: 'Vrl__DataverseTenantId', value: dataverseTenantId }
        { name: 'Vrl__DataverseClientId', value: dataverseClientId }
        { name: 'Vrl__DataverseClientSecret', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=dataverse-client-secret)' }
      ] : [])
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainer}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: 2048
      }
      runtime: { name: 'dotnet-isolated', version: '8.0' }
    }
  }
  dependsOn: [ storageRoles ]
}

// ---- Role assignments (least privilege, scoped to the individual resource) ----

var storageRoleIds = [ roles.storageBlobDataOwner, roles.storageQueueDataContributor, roles.storageTableDataContributor ]

resource storageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in storageRoleIds: {
  name: guid(storage.id, identity.id, r)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource serviceBusRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, identity.id, roles.serviceBusDataOwner)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataOwner)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource appInsightsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, identity.id, roles.monitoringMetricsPublisher)
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.monitoringMetricsPublisher)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource openAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(openAiAccountName)) {
  name: guid(resourceGroup().id, openAiAccountName, identity.id, roles.cognitiveServicesOpenAiUser)
  scope: openAi
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAiUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(keyVault.id, deployerPrincipalId, roles.keyVaultSecretsOfficer)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
    principalId: deployerPrincipalId
    principalType: 'User'
  }
}

resource deployerOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && !empty(openAiAccountName)) {
  name: guid(resourceGroup().id, openAiAccountName, deployerPrincipalId, roles.cognitiveServicesOpenAiUser)
  scope: openAi
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAiUser)
    principalId: deployerPrincipalId
    principalType: 'User'
  }
}

output functionAppName string = functionApp.name
output functionAppHostName string = functionApp.properties.defaultHostName
output managedIdentityClientId string = identity.properties.clientId
output managedIdentityPrincipalId string = identity.properties.principalId
output tenantId string = subscription().tenantId
output serviceBusNamespace string = serviceBus.name
output keyVaultName string = keyVault.name
output openAiEndpoint string = empty(openAiAccountName) ? '' : openAi!.properties.endpoint
