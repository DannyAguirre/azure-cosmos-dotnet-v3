﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Linq
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Query.Core;
    using Newtonsoft.Json;

    /// <summary> 
    /// This is the entry point for LINQ query creation/execution, it generate query provider, implements IOrderedQueryable.
    /// </summary> 
    /// <seealso cref="CosmosLinqQueryProvider"/>  
    internal sealed class CosmosLinqQuery<T> : IDocumentQuery<T>, IOrderedQueryable<T>
    {
        private readonly CosmosLinqQueryProvider queryProvider;
        private readonly Guid correlatedActivityId;

        private readonly ContainerInternal container;
        private readonly CosmosQueryClientCore queryClient;
        private readonly CosmosResponseFactoryInternal responseFactory;
        private readonly QueryRequestOptions cosmosQueryRequestOptions;
        private readonly bool allowSynchronousQueryExecution = false;
        private readonly string continuationToken;
        private readonly CosmosSerializationOptions serializationOptions;

        public CosmosLinqQuery(
           ContainerInternal container,
           CosmosResponseFactoryInternal responseFactory,
           CosmosQueryClientCore queryClient,
           string continuationToken,
           QueryRequestOptions cosmosQueryRequestOptions,
           Expression expression,
           bool allowSynchronousQueryExecution,
           CosmosSerializationOptions serializationOptions = null)
        {
            this.container = container ?? throw new ArgumentNullException(nameof(container));
            this.responseFactory = responseFactory ?? throw new ArgumentNullException(nameof(responseFactory));
            this.queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
            this.continuationToken = continuationToken;
            this.cosmosQueryRequestOptions = cosmosQueryRequestOptions;
            Expression = expression ?? Expression.Constant(this);
            this.allowSynchronousQueryExecution = allowSynchronousQueryExecution;
            correlatedActivityId = Guid.NewGuid();
            this.serializationOptions = serializationOptions;

            queryProvider = new CosmosLinqQueryProvider(
              container,
              responseFactory,
              queryClient,
              this.continuationToken,
              cosmosQueryRequestOptions,
              this.allowSynchronousQueryExecution,
              this.queryClient.OnExecuteScalarQueryCallback,
              this.serializationOptions);
        }

        public CosmosLinqQuery(
          ContainerInternal container,
          CosmosResponseFactoryInternal responseFactory,
          CosmosQueryClientCore queryClient,
          string continuationToken,
          QueryRequestOptions cosmosQueryRequestOptions,
          bool allowSynchronousQueryExecution,
          CosmosSerializationOptions serializationOptions = null)
            : this(
              container,
              responseFactory,
              queryClient,
              continuationToken,
              cosmosQueryRequestOptions,
              null,
              allowSynchronousQueryExecution,
              serializationOptions)
        {
        }

        public Type ElementType => typeof(T);

        public Expression Expression { get; }

        public IQueryProvider Provider => queryProvider;

        public bool HasMoreResults => throw new NotImplementedException();

        /// <summary>
        /// Retrieves an object that can iterate through the individual results of the query.
        /// </summary>
        /// <remarks>
        /// This triggers a synchronous multi-page load.
        /// </remarks>
        /// <returns>IEnumerator</returns>
        public IEnumerator<T> GetEnumerator()
        {
            if (!allowSynchronousQueryExecution)
            {
                throw new NotSupportedException("To execute LINQ query please set " + nameof(allowSynchronousQueryExecution) + " true or" +
                    " use GetItemQueryIterator to execute asynchronously");
            }

            FeedIterator<T> localFeedIterator = CreateFeedIterator(false);
            while (localFeedIterator.HasMoreResults)
            {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                FeedResponse<T> items = TaskHelper.InlineIfPossible(() => localFeedIterator.ReadNextAsync(CancellationToken.None), null).GetAwaiter().GetResult();
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits

                foreach (T item in items)
                {
                    yield return item;
                }
            }
        }

        /// <summary>
        /// Synchronous Multi-Page load
        /// </summary>
        /// <returns>IEnumerator</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public override string ToString()
        {
            SqlQuerySpec querySpec = DocumentQueryEvaluator.Evaluate(Expression, serializationOptions);
            if (querySpec != null)
            {
                return JsonConvert.SerializeObject(querySpec);
            }

            return container.LinkUri.ToString();
        }

        public QueryDefinition ToQueryDefinition(IDictionary<object, string> parameters = null)
        {
            SqlQuerySpec querySpec = DocumentQueryEvaluator.Evaluate(Expression, serializationOptions, parameters);
            return new QueryDefinition(querySpec);
        }

        public FeedIterator<T> ToFeedIterator()
        {
            return new FeedIteratorInlineCore<T>(CreateFeedIterator(true));
        }

        public FeedIterator ToStreamIterator()
        {
            return new FeedIteratorInlineCore(CreateStreamIterator(true));
        }

        public void Dispose()
        {
            //NOTHING TO DISPOSE HERE
        }

        Task<DocumentFeedResponse<TResult>> IDocumentQuery<T>.ExecuteNextAsync<TResult>(CancellationToken token)
        {
            throw new NotImplementedException();
        }

        Task<DocumentFeedResponse<dynamic>> IDocumentQuery<T>.ExecuteNextAsync(CancellationToken token)
        {
            throw new NotImplementedException();
        }

        internal async Task<Response<T>> AggregateResultAsync(CancellationToken cancellationToken = default)
        {
            List<T> result = new List<T>();
            CosmosDiagnosticsContext diagnosticsContext = null;
            Headers headers = new Headers();
            FeedIterator<T> localFeedIterator = CreateFeedIterator(false);
            while (localFeedIterator.HasMoreResults)
            {
                FeedResponse<T> response = await localFeedIterator.ReadNextAsync();
                headers.RequestCharge += response.RequestCharge;

                // If the first page has a diagnostic context use that. Else create a new one and add the diagnostic to it.
                if (response.Diagnostics is CosmosDiagnosticsCore diagnosticsCore)
                {
                    if (diagnosticsContext == null)
                    {
                        diagnosticsContext = diagnosticsCore.Context;
                    }
                    else
                    {
                        diagnosticsContext.AddDiagnosticsInternal(diagnosticsCore.Context);
                    }

                }
                else
                {
                    throw new ArgumentException($"Invalid diagnostic object {response.Diagnostics.GetType().FullName}");
                }

                result.AddRange(response);
            }

            return new ItemResponse<T>(
                System.Net.HttpStatusCode.OK,
                headers,
                result.FirstOrDefault(),
                diagnosticsContext.Diagnostics);
        }

        private FeedIteratorInternal CreateStreamIterator(bool isContinuationExcpected)
        {
            SqlQuerySpec querySpec = DocumentQueryEvaluator.Evaluate(Expression, serializationOptions);

            return container.GetItemQueryStreamIteratorInternal(
                sqlQuerySpec: querySpec,
                isContinuationExcpected: isContinuationExcpected,
                continuationToken: continuationToken,
                feedRange: null,
                requestOptions: cosmosQueryRequestOptions);
        }

        private FeedIterator<T> CreateFeedIterator(bool isContinuationExcpected)
        {
            SqlQuerySpec querySpec = DocumentQueryEvaluator.Evaluate(Expression, serializationOptions);

            FeedIteratorInternal streamIterator = CreateStreamIterator(isContinuationExcpected);
            return new FeedIteratorInlineCore<T>(new FeedIteratorCore<T>(
                streamIterator,
                responseFactory.CreateQueryFeedUserTypeResponse<T>));
        }
    }
}
