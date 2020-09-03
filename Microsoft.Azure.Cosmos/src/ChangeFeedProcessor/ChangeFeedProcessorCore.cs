﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.ChangeFeed
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.ChangeFeed.Bootstrapping;
    using Microsoft.Azure.Cosmos.ChangeFeed.Configuration;
    using Microsoft.Azure.Cosmos.ChangeFeed.FeedManagement;
    using Microsoft.Azure.Cosmos.ChangeFeed.FeedProcessing;
    using Microsoft.Azure.Cosmos.ChangeFeed.LeaseManagement;
    using Microsoft.Azure.Cosmos.ChangeFeed.Monitoring;
    using Microsoft.Azure.Cosmos.ChangeFeed.Utils;
    using Microsoft.Azure.Cosmos.Core.Trace;

    internal sealed class ChangeFeedProcessorCore<T> : ChangeFeedProcessor
    {
        private readonly ChangeFeedObserverFactory<T> observerFactory;
        private ContainerInternal leaseContainer;
        private string monitoredContainerRid;
        private string instanceName;
        private ContainerInternal monitoredContainer;
        private PartitionManager partitionManager;
        private ChangeFeedLeaseOptions changeFeedLeaseOptions;
        private ChangeFeedProcessorOptions changeFeedProcessorOptions;
        private DocumentServiceLeaseStoreManager documentServiceLeaseStoreManager;
        private bool initialized = false;

        public ChangeFeedProcessorCore(ChangeFeedObserverFactory<T> observerFactory)
        {
            if (observerFactory == null)
            {
                throw new ArgumentNullException(nameof(observerFactory));
            }

            this.observerFactory = observerFactory;
        }

        public void ApplyBuildConfiguration(
            DocumentServiceLeaseStoreManager customDocumentServiceLeaseStoreManager,
            ContainerInternal leaseContainer,
            string monitoredContainerRid,
            string instanceName,
            ChangeFeedLeaseOptions changeFeedLeaseOptions,
            ChangeFeedProcessorOptions changeFeedProcessorOptions,
            ContainerInternal monitoredContainer)
        {
            if (monitoredContainer == null)
            {
                throw new ArgumentNullException(nameof(monitoredContainer));
            }

            if (customDocumentServiceLeaseStoreManager == null && leaseContainer == null)
            {
                throw new ArgumentNullException(nameof(leaseContainer));
            }

            if (instanceName == null)
            {
                throw new ArgumentNullException("InstanceName is required for the processor to initialize.");
            }

            documentServiceLeaseStoreManager = customDocumentServiceLeaseStoreManager;
            this.leaseContainer = leaseContainer;
            this.monitoredContainerRid = monitoredContainerRid;
            this.instanceName = instanceName;
            this.changeFeedProcessorOptions = changeFeedProcessorOptions;
            this.changeFeedLeaseOptions = changeFeedLeaseOptions;
            this.monitoredContainer = monitoredContainer;
        }

        public override async Task StartAsync()
        {
            if (!initialized)
            {
                await InitializeAsync().ConfigureAwait(false);
            }

            DefaultTrace.TraceInformation("Starting processor...");
            await partitionManager.StartAsync().ConfigureAwait(false);
            DefaultTrace.TraceInformation("Processor started.");
        }

        public override async Task StopAsync()
        {
            DefaultTrace.TraceInformation("Stopping processor...");
            await partitionManager.StopAsync().ConfigureAwait(false);
            DefaultTrace.TraceInformation("Processor stopped.");
        }

        private async Task InitializeAsync()
        {
            string monitoredContainerRid = await monitoredContainer.GetMonitoredContainerRidAsync(this.monitoredContainerRid);
            this.monitoredContainerRid = monitoredContainer.GetLeasePrefix(changeFeedLeaseOptions, monitoredContainerRid);
            documentServiceLeaseStoreManager = await ChangeFeedProcessorCore<T>.InitializeLeaseStoreManagerAsync(documentServiceLeaseStoreManager, leaseContainer, this.monitoredContainerRid, instanceName).ConfigureAwait(false);
            partitionManager = BuildPartitionManager();
            initialized = true;
        }

        internal static async Task<DocumentServiceLeaseStoreManager> InitializeLeaseStoreManagerAsync(
            DocumentServiceLeaseStoreManager documentServiceLeaseStoreManager,
            ContainerInternal leaseContainer,
            string leaseContainerPrefix,
            string instanceName)
        {
            if (documentServiceLeaseStoreManager == null)
            {
                ContainerResponse cosmosContainerResponse = await leaseContainer.ReadContainerAsync().ConfigureAwait(false);
                ContainerProperties containerProperties = cosmosContainerResponse.Resource;

                bool isPartitioned =
                    containerProperties.PartitionKey != null &&
                    containerProperties.PartitionKey.Paths != null &&
                    containerProperties.PartitionKey.Paths.Count > 0;
                bool isMigratedFixed = (containerProperties.PartitionKey?.IsSystemKey == true);
                if (isPartitioned
                    && !isMigratedFixed
                    && (containerProperties.PartitionKey.Paths.Count != 1 || containerProperties.PartitionKey.Paths[0] != "/id"))
                {
                    throw new ArgumentException("The lease collection, if partitioned, must have partition key equal to id.");
                }

                RequestOptionsFactory requestOptionsFactory = isPartitioned && !isMigratedFixed ?
                    new PartitionedByIdCollectionRequestOptionsFactory() :
                    (RequestOptionsFactory)new SinglePartitionRequestOptionsFactory();

                DocumentServiceLeaseStoreManagerBuilder leaseStoreManagerBuilder = new DocumentServiceLeaseStoreManagerBuilder()
                    .WithLeasePrefix(leaseContainerPrefix)
                    .WithLeaseContainer(leaseContainer)
                    .WithRequestOptionsFactory(requestOptionsFactory)
                    .WithHostName(instanceName);

                documentServiceLeaseStoreManager = await leaseStoreManagerBuilder.BuildAsync().ConfigureAwait(false);
            }

            return documentServiceLeaseStoreManager;
        }

        internal PartitionManager BuildPartitionManager()
        {
            CheckpointerObserverFactory<T> factory = new CheckpointerObserverFactory<T>(observerFactory, changeFeedProcessorOptions.CheckpointFrequency);
            PartitionSynchronizerCore synchronizer = new PartitionSynchronizerCore(
                monitoredContainer,
                documentServiceLeaseStoreManager.LeaseContainer,
                documentServiceLeaseStoreManager.LeaseManager,
                PartitionSynchronizerCore.DefaultDegreeOfParallelism,
                changeFeedProcessorOptions.QueryFeedMaxBatchSize);
            BootstrapperCore bootstrapper = new BootstrapperCore(synchronizer, documentServiceLeaseStoreManager.LeaseStore, BootstrapperCore.DefaultLockTime, BootstrapperCore.DefaultSleepTime);
            PartitionSupervisorFactoryCore<T> partitionSuperviserFactory = new PartitionSupervisorFactoryCore<T>(
                factory,
                documentServiceLeaseStoreManager.LeaseManager,
                new FeedProcessorFactoryCore<T>(monitoredContainer, changeFeedProcessorOptions, documentServiceLeaseStoreManager.LeaseCheckpointer, monitoredContainer.ClientContext.SerializerCore),
                changeFeedLeaseOptions);

            EqualPartitionsBalancingStrategy loadBalancingStrategy = new EqualPartitionsBalancingStrategy(
                    instanceName,
                    EqualPartitionsBalancingStrategy.DefaultMinLeaseCount,
                    EqualPartitionsBalancingStrategy.DefaultMaxLeaseCount,
                    changeFeedLeaseOptions.LeaseExpirationInterval);

            PartitionController partitionController = new PartitionControllerCore(documentServiceLeaseStoreManager.LeaseContainer, documentServiceLeaseStoreManager.LeaseManager, partitionSuperviserFactory, synchronizer);

            partitionController = new HealthMonitoringPartitionControllerDecorator(partitionController, new TraceHealthMonitor());
            PartitionLoadBalancerCore partitionLoadBalancer = new PartitionLoadBalancerCore(
                partitionController,
                documentServiceLeaseStoreManager.LeaseContainer,
                loadBalancingStrategy,
                changeFeedLeaseOptions.LeaseAcquireInterval);
            return new PartitionManagerCore(bootstrapper, partitionController, partitionLoadBalancer);
        }
    }
}