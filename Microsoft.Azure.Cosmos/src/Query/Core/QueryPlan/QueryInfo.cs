//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Query.Core.QueryPlan
{
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Azure.Cosmos.Query.Core.ExecutionComponent.Aggregate;
    using Microsoft.Azure.Cosmos.Query.Core.ExecutionComponent.Distinct;
    using Microsoft.Azure.Cosmos.Query.Core.ExecutionContext.OrderBy;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class QueryInfo
    {
        [JsonProperty("distinctType")]
        [JsonConverter(typeof(StringEnumConverter))]
        public DistinctQueryType DistinctType
        {
            get;
            set;
        }

        [JsonProperty("top")]
        public int? Top
        {
            get;
            set;
        }

        [JsonProperty("offset")]
        public int? Offset
        {
            get;
            set;
        }

        [JsonProperty("limit")]
        public int? Limit
        {
            get;
            set;
        }

        [JsonProperty("orderBy", ItemConverterType = typeof(StringEnumConverter))]
        public IReadOnlyList<SortOrder> OrderBy
        {
            get;
            set;
        }

        [JsonProperty("orderByExpressions")]
        public IReadOnlyList<string> OrderByExpressions
        {
            get;
            set;
        }

        [JsonProperty("groupByExpressions")]
        public IReadOnlyList<string> GroupByExpressions
        {
            get;
            set;
        }

        [JsonProperty("groupByAliases")]
        public IReadOnlyList<string> GroupByAliases
        {
            get;
            set;
        }

        [JsonProperty("aggregates", ItemConverterType = typeof(StringEnumConverter))]
        public IReadOnlyList<AggregateOperator> Aggregates
        {
            get;
            set;
        }

        [JsonProperty("groupByAliasToAggregateType", ItemConverterType = typeof(StringEnumConverter))]
        public IReadOnlyDictionary<string, AggregateOperator?> GroupByAliasToAggregateType
        {
            get;
            set;
        }

        [JsonProperty("rewrittenQuery")]
        public string RewrittenQuery
        {
            get;
            set;
        }

        [JsonProperty("hasSelectValue")]
        public bool HasSelectValue
        {
            get;
            set;
        }

        public bool HasDistinct
        {
            get
            {
                return DistinctType != DistinctQueryType.None;
            }
        }
        public bool HasTop
        {
            get
            {
                return Top != null;
            }
        }

        public bool HasAggregates
        {
            get
            {
                bool aggregatesListNonEmpty = (Aggregates != null) && (Aggregates.Count > 0);
                if (aggregatesListNonEmpty)
                {
                    return true;
                }

                bool aggregateAliasMappingNonEmpty = (GroupByAliasToAggregateType != null)
                    && GroupByAliasToAggregateType
                        .Values
                        .Any(aggregateOperator => aggregateOperator.HasValue);
                return aggregateAliasMappingNonEmpty;
            }
        }

        public bool HasGroupBy
        {
            get
            {
                return GroupByExpressions != null && GroupByExpressions.Count > 0;
            }
        }

        public bool HasOrderBy
        {
            get
            {
                return OrderBy != null && OrderBy.Count > 0;
            }
        }

        public bool HasOffset
        {
            get
            {
                return Offset != null;
            }
        }

        public bool HasLimit
        {
            get
            {
                return Limit != null;
            }
        }
    }
}