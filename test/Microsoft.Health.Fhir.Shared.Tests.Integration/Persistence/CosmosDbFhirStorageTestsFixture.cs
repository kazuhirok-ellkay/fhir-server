﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search.Registry;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.CosmosDb.Configs;
using Microsoft.Health.Fhir.CosmosDb.Features.Queries;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Operations;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Registry;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Versioning;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Tests.Integration.Persistence
{
    public class CosmosDbFhirStorageTestsFixture : IServiceProvider, IAsyncLifetime
    {
        private static readonly SemaphoreSlim CollectionInitializationSemaphore = new SemaphoreSlim(1, 1);

        private readonly CosmosDataStoreConfiguration _cosmosDataStoreConfiguration;
        private readonly CosmosCollectionConfiguration _cosmosCollectionConfiguration;

        private Container _container;
        private IFhirDataStore _fhirDataStore;
        private IFhirOperationDataStore _fhirOperationDataStore;
        private IFhirStorageTestHelper _fhirStorageTestHelper;
        private FilebasedSearchParameterStatusDataStore _filebasedSearchParameterStatusDataStore;
        private CosmosClient _cosmosClient;

        public CosmosDbFhirStorageTestsFixture()
        {
            _cosmosDataStoreConfiguration = new CosmosDataStoreConfiguration
            {
                Host = Environment.GetEnvironmentVariable("CosmosDb:Host") ?? CosmosDbLocalEmulator.Host,
                Key = Environment.GetEnvironmentVariable("CosmosDb:Key") ?? CosmosDbLocalEmulator.Key,
                DatabaseId = Environment.GetEnvironmentVariable("CosmosDb:DatabaseId") ?? "FhirTests",
                AllowDatabaseCreation = true,
                PreferredLocations = Environment.GetEnvironmentVariable("CosmosDb:PreferredLocations")?.Split(';', StringSplitOptions.RemoveEmptyEntries),
            };

            _cosmosCollectionConfiguration = new CosmosCollectionConfiguration
            {
                CollectionId = Guid.NewGuid().ToString(),
            };
        }

        public async Task InitializeAsync()
        {
            var fhirStoredProcs = typeof(IStoredProcedure).Assembly
                .GetTypes()
                .Where(x => !x.IsAbstract && typeof(IStoredProcedure).IsAssignableFrom(x))
                .ToArray()
                .Select(type => (IStoredProcedure)Activator.CreateInstance(type));

            var optionsMonitor = Substitute.For<IOptionsMonitor<CosmosCollectionConfiguration>>();

            optionsMonitor.Get(CosmosDb.Constants.CollectionConfigurationName).Returns(_cosmosCollectionConfiguration);

            var searchParameterDefinitionManager = new SearchParameterDefinitionManager(ModelInfoProvider.Instance);
            searchParameterDefinitionManager.Start();

            _filebasedSearchParameterStatusDataStore = new FilebasedSearchParameterStatusDataStore(searchParameterDefinitionManager, ModelInfoProvider.Instance);

            var updaters = new ICollectionUpdater[]
            {
                new FhirCollectionSettingsUpdater(_cosmosDataStoreConfiguration, optionsMonitor, NullLogger<FhirCollectionSettingsUpdater>.Instance),
                new StoredProcedureInstaller(fhirStoredProcs),
                new CosmosDbSearchParameterStatusInitializer(
                    () => _filebasedSearchParameterStatusDataStore,
                    new CosmosQueryFactory(
                        new CosmosResponseProcessor(Substitute.For<IFhirRequestContextAccessor>(), Substitute.For<IMediator>(), NullLogger<CosmosResponseProcessor>.Instance),
                        NullFhirCosmosQueryLogger.Instance)),
            };

            var dbLock = new CosmosDbDistributedLockFactory(Substitute.For<Func<IScoped<Container>>>(), NullLogger<CosmosDbDistributedLock>.Instance);

            var upgradeManager = new CollectionUpgradeManager(updaters, _cosmosDataStoreConfiguration, optionsMonitor, dbLock, NullLogger<CollectionUpgradeManager>.Instance);
            ICosmosClientTestProvider testProvider = new CosmosClientReadWriteTestProvider();

            var fhirRequestContextAccessor = new FhirRequestContextAccessor();
            var cosmosResponseProcessor = Substitute.For<ICosmosResponseProcessor>();

            var responseProcessor = new CosmosResponseProcessor(fhirRequestContextAccessor, Substitute.For<IMediator>(), NullLogger<CosmosResponseProcessor>.Instance);
            var handler = new FhirCosmosResponseHandler(() => new NonDisposingScope(_container), _cosmosDataStoreConfiguration, fhirRequestContextAccessor, responseProcessor);
            var documentClientInitializer = new FhirCosmosClientInitializer(testProvider, () => new[] { handler }, NullLogger<FhirCosmosClientInitializer>.Instance);
            _cosmosClient = documentClientInitializer.CreateCosmosClient(_cosmosDataStoreConfiguration);
            var fhirCollectionInitializer = new CollectionInitializer(_cosmosCollectionConfiguration.CollectionId, _cosmosDataStoreConfiguration, _cosmosCollectionConfiguration.InitialCollectionThroughput, upgradeManager, NullLogger<CollectionInitializer>.Instance);

            // Cosmos DB emulators throws errors when multiple collections are initialized concurrently.
            // Use the semaphore to only allow one initialization at a time.
            await CollectionInitializationSemaphore.WaitAsync();

            try
            {
                await documentClientInitializer.InitializeDataStore(_cosmosClient, _cosmosDataStoreConfiguration, new List<ICollectionInitializer> { fhirCollectionInitializer });
                _container = documentClientInitializer.CreateFhirContainer(_cosmosClient, _cosmosDataStoreConfiguration.DatabaseId, _cosmosCollectionConfiguration.CollectionId);
            }
            finally
            {
                CollectionInitializationSemaphore.Release();
            }

            var cosmosDocumentQueryFactory = new CosmosQueryFactory(cosmosResponseProcessor, NullFhirCosmosQueryLogger.Instance);

            var documentClient = new NonDisposingScope(_container);

            _fhirDataStore = new CosmosFhirDataStore(
                documentClient,
                _cosmosDataStoreConfiguration,
                optionsMonitor,
                cosmosDocumentQueryFactory,
                new RetryExceptionPolicyFactory(_cosmosDataStoreConfiguration),
                NullLogger<CosmosFhirDataStore>.Instance,
                new VersionSpecificModelInfoProvider(),
                Options.Create(new CoreFeatureConfiguration()));

            _fhirOperationDataStore = new CosmosFhirOperationDataStore(
                documentClient,
                _cosmosDataStoreConfiguration,
                optionsMonitor,
                new RetryExceptionPolicyFactory(_cosmosDataStoreConfiguration),
                new CosmosQueryFactory(responseProcessor, new NullFhirCosmosQueryLogger()),
                NullLogger<CosmosFhirOperationDataStore>.Instance);

            _fhirStorageTestHelper = new CosmosDbFhirStorageTestHelper(
                _container,
                _cosmosDataStoreConfiguration.DatabaseId,
                _cosmosCollectionConfiguration.CollectionId);
        }

        public async Task DisposeAsync()
        {
            if (_container != null)
            {
                await _container.DeleteContainerAsync();
            }

            _cosmosClient.Dispose();
        }

        object IServiceProvider.GetService(Type serviceType)
        {
            if (serviceType == typeof(IFhirDataStore))
            {
                return _fhirDataStore;
            }

            if (serviceType == typeof(IFhirOperationDataStore))
            {
                return _fhirOperationDataStore;
            }

            if (serviceType == typeof(IFhirStorageTestHelper))
            {
                return _fhirStorageTestHelper;
            }

            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }

            return null;
        }
    }
}
